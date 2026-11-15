using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DogAI.Core;
using DogAI.Core.Images;
using DogAI.Data;
using DogAI.Engine.Images;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>
/// Upscale in the Images section (M16 step 5): the two ways (a redraw from the seed with a hires fix; an upscaler file
/// that enlarges the picture as it is), the larger sizes' memory and tiled decoding, the engine's enlarge call, and the
/// rule that an upscaled picture is a new picture beside the original, in memory until it is kept.
/// </summary>
public sealed class UpscaleTests : IDisposable
{
    private const long Gb = 1L << 30;
    private const string Checkpoint = "illustrious/nova.safetensors";
    private static readonly CheckpointSettings Card = CheckpointSettings.ForFamily(ImageFamily.Illustrious);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-upscale-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static ImageModel ModelOf(ImagesControls controls) => ImagesDraw.Model(@"C:\models\image", Checkpoint, Card, controls);

    private static async Task<ImagesTry> DrawOne(ImagesSession session, IImageEngine engine, ImagesControls controls, string prompt = "  a lighthouse,\n at dusk ")
    {
        await session.DrawAsync(engine, ModelOf(controls), Checkpoint, controls.Sampling.Vae, Card.Prediction, ImagesDraw.Request(prompt, "blurry, ", controls));
        return session.Tries[^1];
    }

    // ── The rules ──

    [Fact]
    public void A_redraw_is_the_pictures_own_request_with_a_hires_fix_and_nothing_else_changed()
    {
        var plain = new ImageRequest("  a lighthouse,\n at dusk ", "blurry, ", 832, 1216, Seed: 777, Steps: 28, Sampler: "euler", Scheduler: "karras", Guidance: 5.5,
            ClipSkip: 2, Loras: [new LoraUse("style/ink.safetensors", 0.65)]).ForKeeping();

        var twice = PictureUpscale.Redraw(plain, 2);
        // The numbers Fine uses, at the scale asked for.
        Assert.Equal(PictureLevels.FineHires(28) with { Scale = 2 }, twice.Hires);
        Assert.Equal(plain, twice with { Hires = null });
        Assert.Empty(twice.Problems());

        // A picture that already has a hires fix keeps its steps, strength and enlarger; only the scale changes.
        var own = plain with { Hires = new HiresFix(1.5, 9, 0.45, "Latent"), Count = 4, Preview = true };
        var larger = PictureUpscale.Redraw(own, 2.5);
        Assert.Equal(new HiresFix(2.5, 9, 0.45, "Latent"), larger.Hires);
        Assert.Equal(1, larger.Count);
        Assert.False(larger.Preview);
        Assert.Equal((ImageFormat.Webp, PictureFormat.Quality), (larger.Format, larger.Quality!.Value));
    }

    [Fact]
    public void Redraw_offers_only_larger_sizes_the_request_allows()
    {
        var tall = new ImageRequest("x", Width: 832, Height: 1216, Seed: 1);
        Assert.Equal(["1280 × 1856 (×1.5)", "1664 × 2432 (×2)", "2112 × 3072 (×2.5)", "2496 × 3648 (×3)"],
            PictureUpscale.RedrawSizes(tall).Select(s => s.Label));

        // Already enlarged ×1.5 when drawn: only what is larger than that.
        Assert.Equal([2, 2.5, 3], PictureUpscale.RedrawSizes(tall with { Hires = new HiresFix(1.5, 14, 0.3, "Lanczos") }).Select(s => s.Scale));

        // 1536 × 3 would pass the largest size a picture is drawn at.
        Assert.Equal([1.5, 2, 2.5], PictureUpscale.RedrawSizes(new ImageRequest("x", Width: 1536, Height: 1536, Seed: 1)).Select(s => s.Scale));

        var most = tall with { Hires = new HiresFix(3, 14, 0.3, "Lanczos") };
        Assert.Empty(PictureUpscale.RedrawSizes(most));
        Assert.Contains("as large as a redraw goes", PictureUpscale.NoRedrawSize(most), StringComparison.Ordinal);
        var wide = new ImageRequest("x", Width: 4096, Height: 2048, Seed: 1);
        Assert.Empty(PictureUpscale.RedrawSizes(wide));
        Assert.Contains("4096 pixels", PictureUpscale.NoRedrawSize(wide), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_pictures_larger_than_any_drawn_before_are_decoded_in_tiles()
    {
        // Standard and Fine in every shape are drawn as they always were, so a kept one still redraws byte for byte.
        foreach (var shape in Enum.GetValues<PictureShape>())
        {
            var (width, height) = PictureLevels.Size(shape);
            var standard = new ImageRequest("x", Width: width, Height: height, Steps: 28);
            Assert.False(standard.DecodesInTiles);
            Assert.False((standard with { Hires = PictureLevels.FineHires(28) }).DecodesInTiles);
            Assert.True(PictureUpscale.Redraw(standard, 2).DecodesInTiles);
        }
        Assert.Equal((1280, 1856), new ImageRequest("x", Width: 832, Height: 1216, Hires: new HiresFix(1.5, 14, 0.3)).LargestPass);
        Assert.Equal((832, 1216), new ImageRequest("x", Width: 832, Height: 1216).LargestPass);
        Assert.True(new ImageRequest("x", Width: 2048, Height: 2048).DecodesInTiles);

        // The engine is told so, and only then.
        Assert.False(SdApi.Body(new ImageRequest("x", Width: 832, Height: 1216, Hires: PictureLevels.FineHires(28)), 1).ContainsKey("vae_tiling_params"));
        var body = SdApi.Body(PictureUpscale.Redraw(new ImageRequest("x", Width: 832, Height: 1216, Seed: 5), 2), 5);
        Assert.True((bool?)body["vae_tiling_params"]!["enabled"]);
        Assert.Equal(2.0, (double?)body["hires"]!["scale"]);
    }

    [Fact]
    public void The_memory_estimate_knows_the_larger_sizes_from_the_trial_and_errs_high()
    {
        // Measured above an idle 6.9 GB model (the trial's checkpoint), decoded in tiles: ×2 8.7 GB, ×2.5 10.0 GB, ×3 11.4 GB.
        const long checkpoint = 6_938_040_682;
        var tall = new ImageRequest("x", Width: 832, Height: 1216, Steps: 28, Seed: 1);
        long Need(double scale) => PictureMemory.Need(checkpoint, 0, PictureUpscale.Redraw(tall, scale));

        Assert.InRange(Need(2), (long)(8.7 * Gb), (long)(11.2 * Gb));
        Assert.InRange(Need(2.5), (long)(10.0 * Gb), (long)(12.5 * Gb));
        Assert.InRange(Need(3), (long)(11.4 * Gb), (long)(13.9 * Gb));
        Assert.True(Need(2) < Need(2.5) && Need(2.5) < Need(3));
        // In tiles a ×2 picture needs less than Fine (×1.5, decoded whole: 16.1 GB measured) and less than a Standard one.
        Assert.True(Need(2) < PictureMemory.Need(checkpoint, 0, tall with { Hires = PictureLevels.FineHires(28) }));
        Assert.True(Need(2) < PictureMemory.Need(checkpoint, 0, tall));
        // Enlarging with an upscaler file measured half a gigabyte.
        Assert.InRange(PictureMemory.EnlargeNeed, Gb / 2, 2 * Gb);
    }

    [Fact]
    public void A_picture_enlarged_by_an_upscaler_file_is_told_from_its_size()
    {
        var tall = new ImageRequest("x", Width: 832, Height: 1216);
        Assert.Null(PictureUpscale.EnlargedBy(tall, 832, 1216));
        Assert.Equal(4, PictureUpscale.EnlargedBy(tall, 3328, 4864));
        Assert.Equal(2, PictureUpscale.EnlargedBy(tall, 1664, 2432));
        // A hires fix is not an enlargement, whichever way its size was rounded.
        var fine = tall with { Hires = new HiresFix(1.5, 14, 0.3) };
        Assert.Null(PictureUpscale.EnlargedBy(fine, 1280, 1856));
        Assert.Null(PictureUpscale.EnlargedBy(fine, 1248, 1824));
        Assert.Equal(4, PictureUpscale.EnlargedBy(fine, 5120, 7424));
        Assert.Null(PictureUpscale.EnlargedBy(tall, 0, 0));

        Assert.Equal((3328, 4864), PictureUpscale.Enlarged(832, 1216, 4));
        Assert.Null(PictureUpscale.EnlargeProblem(832, 1216, 4));
        Assert.Null(PictureUpscale.EnlargeProblem(2048, 2048, 4));
        Assert.Contains("6656 × 9728", PictureUpscale.EnlargeProblem(1664, 2432, 4), StringComparison.Ordinal);
        Assert.Equal("RealESRGAN_x4plus (×4)", PictureUpscale.UpscalerLabel("RealESRGAN_x4plus", 4));
        Assert.Equal("old", PictureUpscale.UpscalerLabel("old", null));
    }

    [Theory]
    [InlineData("RealESRGAN_x4plus", true)]
    [InlineData("4x anime 6B", true)]
    [InlineData("sub/inner", false)]
    [InlineData("sub\\inner", false)]
    [InlineData("../models/x", false)]
    [InlineData("C:\\x", false)]
    [InlineData("", false)]
    [InlineData("a*b", false)]
    public void An_upscaler_is_named_by_a_file_at_the_top_of_its_folder(string name, bool ok)
    {
        Assert.Equal(ok, ImageEnlarge.IsUpscalerName(name));
        Assert.Equal(ok, new ImageEnlarge(KeptPictureTests.Webp(832, 1216, 100), name).Problems().Count == 0);
    }

    [Fact]
    public void Enlarging_takes_a_webp_picture_only()
    {
        Assert.Contains(new ImageEnlarge([1, 2, 3], "x").Problems(), p => p.Contains("not a WebP", StringComparison.Ordinal));
    }

    // ── The engine's answer ──

    private static byte[] Riff(params (string Name, byte[] Body)[] chunks)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(0u);
        writer.Write("WEBP"u8);
        foreach (var (name, body) in chunks)
        {
            writer.Write(Encoding.ASCII.GetBytes(name));
            writer.Write((uint)body.Length);
            writer.Write(body);
            if (body.Length % 2 == 1) writer.Write((byte)0);
        }
        writer.Flush();
        var bytes = stream.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    /// <summary>A WebP in the extended format as the engine writes an enlarged picture: the picture, then an XMP part.</summary>
    private static byte[] WithXmp(int width, int height, string xmp, params (string Name, byte[] Body)[] more)
    {
        var plain = KeptPictureTests.Webp(width, height, 301);
        var picture = plain.AsSpan(20, (int)BitConverter.ToUInt32(plain, 16)).ToArray();
        var header = new byte[10];
        header[0] = 0x04; // XMP
        (header[4], header[5], header[6]) = ((byte)(width - 1), (byte)((width - 1) >> 8), (byte)((width - 1) >> 16));
        (header[7], header[8], header[9]) = ((byte)(height - 1), (byte)((height - 1) >> 8), (byte)((height - 1) >> 16));
        return Riff([("VP8X", header), ("VP8 ", picture), ("XMP ", Encoding.UTF8.GetBytes(xmp)), .. more]);
    }

    [Fact]
    public void An_enlarged_picture_comes_back_with_nothing_but_the_picture_in_it()
    {
        const string marker = "upscale_factor-marker";
        var written = WithXmp(3328, 4864, $"<x:xmpmeta>{marker}</x:xmpmeta>");
        Assert.True(WebpInfo.Read(written)!.Value.HasMetadata);

        var plain = WebpInfo.PictureOnly(written)!;
        var info = WebpInfo.Read(plain)!.Value;
        Assert.Equal((3328, 4864, false), (info.Width, info.Height, info.HasMetadata));
        Assert.DoesNotContain(marker, Encoding.ASCII.GetString(plain), StringComparison.Ordinal);
        Assert.Equal("VP8 ", Encoding.ASCII.GetString(plain, 12, 4));
        // The picture's own bytes are untouched.
        Assert.Equal(KeptPictureTests.Webp(3328, 4864, 301), plain);
        // The store takes it as a picture.
        Assert.Empty(new Picture
        {
            OwnerKind = PictureOwners.Images, Image = plain, ImageWidth = 3328, ImageHeight = 4864, Checkpoint = Checkpoint,
            Prompt = "x", Width = 832, Height = 1216, Seed = 1, Steps = 28, Sampler = "euler", Guidance = 5.5,
        }.Problems());

        // A plain file is left as it is; transparency or anything unknown cannot be dropped, so there is no answer.
        var simple = KeptPictureTests.Webp(832, 1216, 100);
        Assert.Same(simple, WebpInfo.PictureOnly(simple));
        Assert.Null(WebpInfo.PictureOnly(WithXmp(832, 1216, "x", ("ALPH", [1, 2, 3, 4]))));
        Assert.Null(WebpInfo.PictureOnly(WithXmp(832, 1216, "x", ("ANIM", [1, 2, 3, 4, 5, 6]))));
        Assert.Null(WebpInfo.PictureOnly([1, 2, 3]));
    }

    [Fact]
    public async Task The_engine_is_asked_by_name_for_webp_and_its_answer_is_read_and_cleaned()
    {
        var picture = KeptPictureTests.Webp(832, 1216, 100);
        var server = new FakeSdServer
        {
            EnlargeAnswer = (HttpStatusCode.OK, $$"""
                {"images":[{"index":0,"b64_json":"{{Convert.ToBase64String(WithXmp(3328, 4864, "<x:xmpmeta>4</x:xmpmeta>"))}}"}],
                 "upscaler":"RealESRGAN_x4plus","scale":4,"repeats":1,"width":3328,"height":4864,"output_format":"webp"}
                """),
        };
        var api = new SdApi(new HttpClient(server), 5555);

        var larger = await api.EnlargeAsync(new ImageEnlarge(picture, "RealESRGAN_x4plus"), CancellationToken.None);

        Assert.Equal((3328, 4864, 4), (larger.Width, larger.Height, larger.Scale));
        Assert.False(WebpInfo.Read(larger.Image)!.Value.HasMetadata);
        var body = Assert.Single(server.Enlarged);
        Assert.Equal("RealESRGAN_x4plus", (string?)body["upscaler"]);
        Assert.Equal(Convert.ToBase64String(picture), (string?)body["image"]);
        Assert.Equal("webp", (string?)body["output_format"]);
        Assert.Equal(PictureFormat.Quality, (int?)body["output_compression"]);
        Assert.Equal(1, (int?)body["repeats"]);
        // Nothing of a prompt is in it: only the five fields.
        Assert.Equal(5, body.Count);

        // Only the files that can enlarge a picture are offered by the engine.
        Assert.Equal(["RealESRGAN_x4plus"], await api.UpscalersAsync());
    }

    [Theory]
    [InlineData("""{"error":"no compatible image upscaler called odd"}""", "cannot use “odd” as an upscaler")]
    [InlineData("""{"error":"no RGB ESRGAN upscaler models are available; start the server with --hires-upscalers-dir"}""", "cannot use “odd” as an upscaler")]
    [InlineData("""{"error":"the upscaler model could not be loaded"}""", "cannot use “odd” as an upscaler")]
    [InlineData("""{"error":"upscaled dimensions must not exceed 8192 x 8192"}""", "larger than 8192 pixels")]
    [InlineData("""{"error":"upscale failed"}""", "refused the request (HTTP 400): upscale failed")]
    public async Task A_refused_enlargement_is_said_in_words(string answer, string expected)
    {
        var server = new FakeSdServer { EnlargeAnswer = (HttpStatusCode.BadRequest, answer) };
        var e = await Assert.ThrowsAsync<ImageEngineException>(() =>
            new SdApi(new HttpClient(server), 5555).EnlargeAsync(new ImageEnlarge(KeptPictureTests.Webp(832, 1216, 100), "odd"), CancellationToken.None));
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    // ── The upscalers folder ──

    private static SafetensorsHeader Header(params (string Name, long[] Shape)[] tensors) =>
        new(tensors.ToDictionary(t => t.Name, t => t.Shape), new Dictionary<string, string>());

    [Fact]
    public void An_esrgan_file_says_how_many_times_it_enlarges()
    {
        var x4 = ImageFileRecognizer.Classify(Header(("conv_first.weight", [64, 3, 3, 3]), ("body.0.rdb1.conv1.weight", [32, 64, 3, 3])), "RealESRGAN_x4plus.safetensors");
        Assert.Equal((ImageFileKind.Upscaler, 4), (x4.Kind, x4.Enlarges));
        Assert.Contains("enlarges ×4", x4.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("does not use", x4.Note, StringComparison.Ordinal);
        Assert.Equal(2, ImageFileRecognizer.Classify(Header(("conv_first.weight", [64, 12, 3, 3])), "x2.safetensors").Enlarges);
        // The older layout does not say: the engine's answer is what counts.
        var old = ImageFileRecognizer.Classify(Header(("model.0.weight", [64, 3, 3, 3]), ("model.1.sub.0.RDB1.conv1.0.weight", [32, 64, 3, 3])), "old.safetensors");
        Assert.Equal((ImageFileKind.Upscaler, (int?)null), (old.Kind, old.Enlarges));
    }

    [Fact]
    public void The_engine_is_given_the_upscalers_folder_only_while_no_file_in_it_is_a_refused_format()
    {
        var folder = Path.Combine(_root, "image", "upscalers");
        Assert.Equal("There is no upscalers folder yet.", ImageLibrary.UpscalerFolderProblem(folder));
        Assert.NotNull(ImageLibrary.UpscalerFolderProblem(null));

        Directory.CreateDirectory(folder);
        Assert.Null(ImageLibrary.UpscalerFolderProblem(folder));
        File.WriteAllBytes(Path.Combine(folder, "fine.safetensors"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "notes.txt"), [1]);
        Assert.Null(ImageLibrary.UpscalerFolderProblem(folder));

        // The engine would open it (it opens every file at the top of the folder), so the folder is not handed over.
        File.WriteAllBytes(Path.Combine(folder, "RealESRGAN_x4plus_anime_6B.pth"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "another.ckpt"), [1]);
        var problem = ImageLibrary.UpscalerFolderProblem(folder)!;
        Assert.Contains("Move another.ckpt and 1 more like it out of the upscalers folder", problem, StringComparison.Ordinal);
        File.Delete(Path.Combine(folder, "another.ckpt"));
        Assert.StartsWith("Move RealESRGAN_x4plus_anime_6B.pth out of the upscalers folder", ImageLibrary.UpscalerFolderProblem(folder), StringComparison.Ordinal);

        // A subfolder is the owner's business: the engine does not look inside it.
        File.Delete(Path.Combine(folder, "RealESRGAN_x4plus_anime_6B.pth"));
        Directory.CreateDirectory(Path.Combine(folder, "old"));
        File.WriteAllBytes(Path.Combine(folder, "old", "x.pth"), [1]);
        Assert.Null(ImageLibrary.UpscalerFolderProblem(folder));

        // The start arguments name the folder only when the model carries one.
        var model = ImageFolders.ModelFor(Path.Combine(_root, "image"), "c.safetensors", null, ImagePrediction.Auto);
        Assert.Equal(folder, model.UpscalerFolder);
        var args = ImageServer.Arguments(model, 1, "b.html");
        Assert.Equal(folder, args[args.ToList().IndexOf("--hires-upscalers-dir") + 1]);
        Assert.DoesNotContain("--hires-upscalers-dir", ImageServer.Arguments(model with { UpscalerFolder = null }, 1, "b.html"));
    }

    [Fact]
    public void Only_esrgan_safetensors_files_at_the_top_of_the_upscalers_folder_are_offered()
    {
        var upscaler = new ImageFileVerdict(ImageFileKind.Upscaler, "Upscaler", Enlarges: 4);
        ImageFile File(string folder, string path, ImageFileVerdict verdict, string? misplaced = null) => new(folder, path, @"C:\x\" + path, 10, verdict, misplaced);
        var files = new[]
        {
            File(ImageFolders.Upscalers, "RealESRGAN_x4plus.safetensors", upscaler),
            File(ImageFolders.Upscalers, "old/inner.safetensors", upscaler),
            File(ImageFolders.Loras, "stray.safetensors", upscaler, ImageFolders.Upscalers),
            File(ImageFolders.Upscalers, "x.pth", new ImageFileVerdict(ImageFileKind.Refused, ImageFileRecognizer.RefusedNote)),
            File(ImageFolders.Upscalers, "what.safetensors", new ImageFileVerdict(ImageFileKind.Unusable, "?")),
        };
        Assert.Equal(["RealESRGAN_x4plus"], ImageLibrary.Upscalers(files).Select(f => f.Name));
    }

    [Fact]
    public void The_upscaler_is_among_the_recommended_downloads_as_a_safetensors_file()
    {
        var upscaler = Assert.Single(ImageDownloads.All, d => d.Folder == ImageFolders.Upscalers);
        Assert.EndsWith(".safetensors", upscaler.File, StringComparison.Ordinal);
        Assert.EndsWith(".safetensors", upscaler.SaveAs, StringComparison.Ordinal);
        Assert.True(ImageEnlarge.IsUpscalerName(Path.GetFileNameWithoutExtension(upscaler.SaveAs)));
    }

    // ── A new picture beside the original, in memory until kept ──

    [Fact]
    public async Task Upscale_on_the_make_tab_redraws_the_try_larger_as_a_new_try_beside_it()
    {
        var engine = new PortraitTests.WebpEngine();
        var session = new ImagesSession();
        var controls = ImagesControls.StartFor(Card) with { Seed = 777, Loras = [new LoraUse("style/ink.safetensors", 0.65)], Count = 2 };
        await DrawOne(session, engine, controls);
        var original = session.Tries[0];
        var stages = new List<string>();
        session.Changed += () => { if (session.IsUpscaling && session.Progress is { } p) stages.Add(p.Stage); };

        var made = await session.UpscaleAsync(engine, ModelOf(controls), UpscaleSource.Of(original), UpscaleWay.Redraw, 2, null, add: true);

        // The strip holds the two drawn, then the new one; the one it came from is the same object as before.
        Assert.Equal(3, session.Tries.Count);
        Assert.Same(original, session.Tries[0]);
        Assert.Same(made, session.Tries[2]);
        Assert.Same(made, session.Selected);
        Assert.Equal(3, made.Number);
        Assert.Equal((1664, 2432), (made.ImageWidth, made.ImageHeight));
        // Sent: the try's own request, seed included, with the hires fix; exactly as typed.
        var sent = engine.Requests[^1];
        Assert.Equal(PictureUpscale.Redraw(original.Request, 2) with { Preview = true, PreviewEvery = 4 }, sent);
        Assert.Equal("  a lighthouse,\n at dusk ", sent.Prompt);
        Assert.Equal(777, sent.Seed);
        Assert.Equal(original.Request.Loras, sent.Loras);
        Assert.Contains("sampling", stages);
        Assert.False(session.IsDrawing);
        Assert.False(session.IsUpscaling);

        // Kept, it is an ordinary picture with a hires fix: its row draws it again, and Make one like this gives the same request.
        var row = made.ToKeep();
        Assert.Empty(row.Problems());
        Assert.Equal(new HiresFix(2, 14, 0.3, "Lanczos"), row.Hires);
        Assert.Null(PictureUpscale.EnlargedBy(row));
        var like = ImagesDraw.Like(row, ImagesControls.StartFor(Card));
        Assert.True(like.Controls.HiresOn);
        Assert.Equal(made.Request with { Loras = [] }, ImagesDraw.Singles(ImagesDraw.Request(like.Prompt, like.NegativePrompt, like.Controls))[0] with { Loras = [] });
        Assert.StartsWith("Upscaled to 1664 × 2432 in ", PictureUpscale.Done(made), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enlarge_only_hands_the_picture_itself_to_the_upscaler_and_keeps_the_request_it_was_drawn_with()
    {
        var engine = new PortraitTests.WebpEngine();
        var session = new ImagesSession();
        var controls = ImagesControls.StartFor(Card) with { Seed = 42 };
        var original = await DrawOne(session, engine, controls);
        var draws = engine.Requests.Count;

        var made = await session.UpscaleAsync(engine, ModelOf(controls), UpscaleSource.Of(original), UpscaleWay.Enlarge, 0, "RealESRGAN_x4plus", add: true);

        // Nothing was drawn, and no prompt went anywhere: the upscaler got the picture's own bytes and its name.
        Assert.Equal(draws, engine.Requests.Count);
        var asked = Assert.Single(engine.Enlarges);
        Assert.Same(original.Image, asked.Image);
        Assert.Equal("RealESRGAN_x4plus", asked.Upscaler);
        Assert.Equal((3328, 4864), (made.ImageWidth, made.ImageHeight));
        Assert.Equal(original.Request, made.Request);
        Assert.Equal(4, PictureUpscale.EnlargedBy(made));
        Assert.Contains("3328 × 4864 · enlarged ×4 · seed 42", ImagesPanels.Meta(made), StringComparison.Ordinal);
        Assert.StartsWith("3328 × 4864 · enlarged ×4 · ", PictureUpscale.ResultMeta(made), StringComparison.Ordinal);
        Assert.DoesNotContain("enlarged", ImagesPanels.Meta(original), StringComparison.Ordinal);

        // Kept: the large picture with the settings of the one it came from, so Make one like this draws that one.
        var row = made.ToKeep();
        Assert.Empty(row.Problems());
        Assert.Equal((3328, 4864, 832, 1216), (row.ImageWidth, row.ImageHeight, row.Width, row.Height));
        Assert.Null(row.Hires);
        Assert.Contains(" · enlarged ×4 · ", ImagesPanels.KeptMeta(row, new DateTime(2026, 11, 15, 14, 3, 0)), StringComparison.Ordinal);
        var like = ImagesDraw.Like(row, ImagesControls.StartFor(Card));
        Assert.Equal((832, 1216, false, 42L), (like.Controls.Width, like.Controls.Height, like.Controls.HiresOn, like.Controls.Seed!.Value));
        // The enlarged picture cannot be enlarged four times again (the engine stops at 8192), and says so first.
        Assert.NotNull(PictureUpscale.EnlargeProblem(made.ImageWidth, made.ImageHeight, 4));
    }

    [Fact]
    public async Task Enlarging_keeps_the_checkpoint_in_use_and_a_redraw_loads_the_pictures_own()
    {
        var engine = new PortraitTests.WebpEngine();
        var session = new ImagesSession();
        var controls = ImagesControls.StartFor(Card);
        var original = await DrawOne(session, engine, controls);
        var other = ImageFolders.ModelFor(@"C:\models\image", "other.safetensors", null, ImagePrediction.Auto);
        await engine.LoadAsync(other);

        await session.UpscaleAsync(engine, ModelOf(controls), UpscaleSource.Of(original), UpscaleWay.Enlarge, 0, "RealESRGAN_x4plus", add: false);
        Assert.Equal(other, engine.Model);

        await session.UpscaleAsync(engine, ModelOf(controls), UpscaleSource.Of(original), UpscaleWay.Redraw, 1.5, null, add: false);
        Assert.Equal(ModelOf(controls), engine.Model);

        // With no checkpoint chosen at all, enlarging starts the engine with the one it is given.
        await engine.UnloadAsync();
        await session.UpscaleAsync(engine, ModelOf(controls), UpscaleSource.Of(original), UpscaleWay.Enlarge, 0, "RealESRGAN_x4plus", add: false);
        Assert.Equal(ModelOf(controls), engine.Model);
    }

    [Fact]
    public async Task The_upscaled_copy_of_a_kept_picture_is_a_new_row_and_the_original_is_not_touched()
    {
        var engine = new PortraitTests.WebpEngine();
        var session = new ImagesSession();
        var controls = ImagesControls.StartFor(Card) with { Seed = 9 };
        var first = await DrawOne(session, engine, controls);

        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        await using var services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
        await services.MigrateDogDatabaseAsync();
        var store = services.GetRequiredService<IPictureStore>();
        var original = await store.AddAsync(first.ToKeep());
        session.Clear();

        // Kept pictures › Upscale…: from the row as the large view reads it.
        var opened = (await store.GetAsync(original.Id))!;
        var source = UpscaleSource.Of(opened)!;
        Assert.Equal(first.Request with { Loras = [] }, source.Request with { Loras = [] });
        var redrawn = await session.UpscaleAsync(engine, opened.ModelIn(@"C:\models\image"), source, UpscaleWay.Redraw, 2, null, add: false);
        var enlarged = await session.UpscaleAsync(engine, opened.ModelIn(@"C:\models\image"), source, UpscaleWay.Enlarge, 0, "RealESRGAN_x4plus", add: false);

        // In memory only: not in the strip, and nothing new in the database until Keep it.
        Assert.Empty(session.Tries);
        Assert.Equal(0, redrawn.Number);
        Assert.Single((await store.PageAsync(PictureQuery.Images())).Pictures);
        Assert.Equal(PictureUpscale.Redraw(first.Request, 2) with { Preview = true, PreviewEvery = 4, Loras = [] }, engine.Requests[^1] with { Loras = [] });

        var keptRedraw = await store.AddAsync(redrawn.ToKeep());
        var keptEnlarged = await store.AddAsync(enlarged.ToKeep());
        var page = await store.PageAsync(PictureQuery.Images());
        Assert.Equal([keptEnlarged.Id, keptRedraw.Id, original.Id], page.Pictures.Select(p => p.Id));
        Assert.Equal([(3328, 4864), (1664, 2432), (832, 1216)], page.Pictures.Select(p => (p.ImageWidth, p.ImageHeight)));
        // All three are the Tall shape for the filter, and the original is byte for byte what it was.
        Assert.All(page.Pictures, p => Assert.Equal(PictureShape.Tall, PictureQuery.Shapes(p.ImageWidth, p.ImageHeight)));
        var after = (await store.GetAsync(original.Id))!;
        Assert.Equal(first.Image, after.Image);
        Assert.Equal(opened.ToRequest() with { Loras = [] }, after.ToRequest() with { Loras = [] });
        // Read back, the enlarged one still says so, and the redrawn one draws itself again.
        Assert.Equal(4, PictureUpscale.EnlargedBy((await store.GetAsync(keptEnlarged.Id))!));
        Assert.Equal(redrawn.Request with { Loras = [] }, (await store.GetAsync(keptRedraw.Id))!.ToRequest() with { Loras = [] });

        // A picture from a list has no image: there is nothing to upscale from.
        Assert.Null(UpscaleSource.Of(page.Pictures[0]));
    }

    [Fact]
    public async Task An_upscale_is_a_draw_one_at_a_time_and_cancel_stops_it_leaving_the_strip_as_it_was()
    {
        var engine = new FakeImageEngine { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var session = new ImagesSession();
        var controls = ImagesControls.StartFor(Card) with { Seed = 3 };
        var source = new UpscaleSource(Checkpoint, null, ImagePrediction.Auto, ImagesDraw.Singles(ImagesDraw.Request("a fox", "", controls))[0],
            KeptPictureTests.Webp(832, 1216, 100), 832, 1216);

        var running = session.UpscaleAsync(engine, ModelOf(controls), source, UpscaleWay.Redraw, 2, null, add: true);
        await engine.Held.Task;
        Assert.True(session.IsDrawing);
        Assert.True(session.IsUpscaling);
        Assert.Equal((1, 1), (session.Drawing, session.Of));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.UpscaleAsync(engine, ModelOf(controls), source, UpscaleWay.Enlarge, 0, "RealESRGAN_x4plus", add: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.DrawAsync(engine, ModelOf(controls), Checkpoint, null, ImagePrediction.Auto, ImagesDraw.Request("x", "", controls)));

        session.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Empty(session.Tries);
        Assert.False(session.IsDrawing);
        Assert.False(session.IsUpscaling);

        // What cannot be done is refused before anything is asked of the engine.
        var draws = engine.Requests.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UpscaleAsync(engine, ModelOf(controls), source, UpscaleWay.Redraw, 9, null, add: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UpscaleAsync(engine, ModelOf(controls), source, UpscaleWay.Enlarge, 0, "sub/x", add: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.UpscaleAsync(engine, ModelOf(controls), source, UpscaleWay.Enlarge, 0, null, add: true));
        Assert.Equal(draws, engine.Requests.Count);
        Assert.False(session.IsDrawing);
    }

    // ── The graphics card ──

    private static (CardCoordinator Coordinator, FakeChatModel Chat, FakeImageEngine Images, List<string> Notes) Rig(long chatBytes)
    {
        var events = new List<string>();
        var chat = new FakeChatModel { Events = events };
        chat.Loaded();
        var images = new FakeImageEngine { Events = events };
        var card = new FakeCard(chat, images) { ChatBytes = chatBytes };
        var coordinator = new CardCoordinator(images, chat, card, _ => (long)(6.5 * Gb)) { PollInterval = TimeSpan.FromMilliseconds(5) };
        var notes = new List<string>();
        coordinator.Noted += notes.Add;
        return (coordinator, chat, images, notes);
    }

    [Fact]
    public async Task Enlarging_leaves_a_large_chat_model_loaded_where_a_picture_sets_it_aside()
    {
        // An 18 GB chat model on a 24 GB card leaves 5 GB: far too little for a picture, plenty for an upscaler file.
        var (coordinator, chat, images, notes) = Rig(18 * Gb);
        await coordinator.LoadAsync(new ImageModel(@"D:\m\c.safetensors", @"D:\m\loras"));
        var stages = new Collect<ImageProgress>();

        var larger = await coordinator.EnlargeAsync(new ImageEnlarge(KeptPictureTests.Webp(832, 1216, 100), "RealESRGAN_x4plus"), stages);

        Assert.Equal((3328, 4864, 4), (larger.Width, larger.Height, larger.Scale));
        Assert.True(chat.IsLoaded);
        Assert.Equal(0, chat.Rests);
        Assert.Equal(CardChoice.KeptBoth, coordinator.LastDecision!.Choice);
        Assert.Equal(PictureMemory.EnlargeNeed, coordinator.LastDecision.NeedBytes);
        Assert.Contains(stages.Items, p => p.Stage == CardCoordinator.Enlarging);
        Assert.Contains(notes, n => n.StartsWith("enlarging the picture needs about 1.0 GB", StringComparison.Ordinal) && n.Contains("stays loaded", StringComparison.Ordinal));
        Assert.False(coordinator.IsBusy);

        // The same card, a picture: the chat model makes room.
        await coordinator.DrawAsync(new ImageRequest("1girl", Width: 832, Height: 1216, Steps: 3));
        Assert.True(chat.IsResting);
        lock (images.Events) Assert.Equal("enlarge rest draw drawn", string.Join(" ", images.Events));
    }

    [Fact]
    public async Task A_redraw_at_twice_the_size_fits_beside_a_chat_model_that_a_standard_picture_does_not()
    {
        // 10.5 GB of chat model: 12.5 GB free. A Standard picture is judged at 12.2 GB (it is decoded whole), a ×2 redraw
        // at 10.2 GB (decoded in tiles), so the redraw is the one that leaves the chat model where it is.
        var tall = new ImageRequest("1girl", Width: 832, Height: 1216, Steps: 3, Seed: 1);
        var (coordinator, chat, _, _) = Rig((long)(10.5 * Gb));
        await coordinator.LoadAsync(new ImageModel(@"D:\m\c.safetensors", @"D:\m\loras"));

        await coordinator.DrawAsync(PictureUpscale.Redraw(tall, 2));
        Assert.True(chat.IsLoaded);
        Assert.Equal(CardChoice.KeptBoth, coordinator.LastDecision!.Choice);

        var (second, chat2, _, _) = Rig((long)(10.5 * Gb));
        await second.LoadAsync(new ImageModel(@"D:\m\c.safetensors", @"D:\m\loras"));
        await second.DrawAsync(tall);
        Assert.True(chat2.IsResting);
    }

    [Fact]
    public async Task Enlarging_needs_a_checkpoint_chosen_and_a_sound_request_before_anything_is_moved()
    {
        var (coordinator, chat, images, _) = Rig(18 * Gb);
        var picture = KeptPictureTests.Webp(832, 1216, 100);
        var none = await Assert.ThrowsAsync<ImageEngineException>(() => coordinator.EnlargeAsync(new ImageEnlarge(picture, "x")));
        Assert.Contains("Choose a checkpoint first", none.Message, StringComparison.Ordinal);

        await coordinator.LoadAsync(new ImageModel(@"D:\m\c.safetensors"));
        await Assert.ThrowsAsync<ImageEngineException>(() => coordinator.EnlargeAsync(new ImageEnlarge(picture, "../x")));
        await Assert.ThrowsAsync<ImageEngineException>(() => coordinator.EnlargeAsync(new ImageEnlarge([1, 2], "x")));
        Assert.True(chat.IsLoaded);
        lock (images.Events) Assert.Empty(images.Events);
    }

    // ── The screens ──

    [Fact]
    public void Both_tabs_offer_upscale_through_the_one_dialog_and_carry_the_ids_the_checks_use()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DogAI.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([root!.FullName, "src", "DogAI.UI", .. parts]));

        var make = Read("Pages", "ImagesMake.razor");
        Assert.Contains("id=\"images-upscale\"", make, StringComparison.Ordinal);
        Assert.Contains("<UpscaleBox", make, StringComparison.Ordinal);
        // Under the picture, between Use these settings and Save as a file… (the plan's order).
        Assert.True(make.IndexOf("id=\"images-use\"", StringComparison.Ordinal) < make.IndexOf("id=\"images-upscale\"", StringComparison.Ordinal));
        Assert.True(make.IndexOf("id=\"images-upscale\"", StringComparison.Ordinal) < make.IndexOf("id=\"images-save\"", StringComparison.Ordinal));

        var kept = Read("Components", "Images", "KeptPictures.razor");
        Assert.Contains("id=\"kept-upscale\"", kept, StringComparison.Ordinal);
        // The grid never draws or keeps by itself: the page's dialog does both.
        Assert.DoesNotContain("UpscaleAsync(Card", kept, StringComparison.Ordinal);

        var box = Read("Components", "Images", "UpscaleBox.razor");
        foreach (var id in new[] { "upscale", "upscale-now", "upscale-way-redraw", "upscale-way-enlarge", "upscale-about", "upscale-size", "upscale-file",
                     "upscale-becomes", "upscale-none", "upscale-folder", "upscale-why", "upscale-go", "upscale-cancel", "upscale-status", "upscale-error",
                     "upscale-result", "upscale-meta", "upscale-keep", "upscale-discard" })
            Assert.Contains($"id=\"{id}\"", box, StringComparison.Ordinal);
        // It draws only through the session (so through the card coordinator), and keeps nothing by itself.
        Assert.Contains("Session.UpscaleAsync(Card", box, StringComparison.Ordinal);
        Assert.DoesNotContain("AddAsync", box, StringComparison.Ordinal);
        Assert.DoesNotContain("ImageServer", box, StringComparison.Ordinal);
    }
}
