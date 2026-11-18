using System.Text;
using System.Text.Json;
using DogAI.Core;
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Core.Net;
using DogAI.Core.Settings;
using DogAI.Core.Video;
using DogAI.Engine;
using DogAI.Engine.Video;
using DogAI.Hub;

namespace DogAI.Tests;

/// <summary>
/// M20 step 3: the video model in the Models section. Its four files told apart by their headers; the chat-model list and
/// the document search pick never see the video folder; the download set's checksums are pinned and every part carries
/// one; whether a card is offered the model.
/// </summary>
public sealed class VideoModelsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dogai-video-models-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- Headers, as the real files have them (read from the four files of the M20 trial) ----

    private static SafetensorsHeader Header(params (string Name, long[] Shape)[] tensors) =>
        new(tensors.ToDictionary(t => t.Name, t => t.Shape), new Dictionary<string, string>());

    internal static (string, long[])[] Wan22ModelTensors(string prefix = "") =>
    [
        (prefix + "patch_embedding.weight", [3072, 48, 1, 2, 2]),
        (prefix + "head.head.weight", [192, 3072]),
        (prefix + "blocks.0.cross_attn.k.weight", [3072, 3072]),
        (prefix + "blocks.0.self_attn.q.weight", [3072, 3072]),
    ];

    internal static (string, long[])[] Wan22VaeTensors(int channels = 48) =>
    [
        ("conv1.weight", [channels * 2, channels * 2, 1, 1, 1]),
        ("conv2.weight", [channels, channels, 1, 1, 1]),
        ("decoder.conv1.weight", [1024, channels, 3, 3, 3]),
        ("decoder.head.0.gamma", [256, 1, 1, 1]),
        ("encoder.conv1.weight", [160, 12, 3, 3, 3]),
        ("encoder.downsamples.0.residual.0.gamma", [160, 1, 1, 1]),
    ];

    internal static (string, long[])[] FastDecoderTensors(int channels = 48) =>
    [
        ("decoder.1.weight", [256, channels, 3, 3]),
        ("decoder.10.conv.0.weight", [128, 256, 3, 3]),
        ("encoder.10.conv.0.weight", [64, 64, 3, 3]),
        ("encoder.14.conv.0.weight", [64, 64, 3, 3]),
    ];

    [Fact]
    public void The_three_safetensors_files_are_told_apart_by_their_headers()
    {
        Assert.Equal(VideoFileKind.Wan22Model, VideoFileRecognizer.Classify(Header(Wan22ModelTensors())).Kind);
        Assert.Equal(VideoFileKind.Wan22Model, VideoFileRecognizer.Classify(Header(Wan22ModelTensors("model.diffusion_model."))).Kind);
        Assert.Equal(VideoFileKind.Wan22Vae, VideoFileRecognizer.Classify(Header(Wan22VaeTensors())).Kind);
        Assert.Equal(VideoFileKind.Wan22FastDecoder, VideoFileRecognizer.Classify(Header(FastDecoderTensors())).Kind);
    }

    [Fact]
    public void Other_wan_files_are_named_and_not_used()
    {
        // Wan 2.1 1.3B, the 14B models, Wan 2.1's decoder and fast decoder.
        var wan21 = Header(("patch_embedding.weight", [1536, 16, 1, 2, 2]), ("head.head.weight", [64, 1536]));
        var wan14b = Header(("patch_embedding.weight", [5120, 16, 1, 2, 2]), ("blocks.0.cross_attn.k.weight", [5120, 5120]));
        foreach (var header in new[] { wan21, wan14b, Header(Wan22VaeTensors(16)), Header(FastDecoderTensors(16)) })
        {
            var verdict = VideoFileRecognizer.Classify(header);
            Assert.Equal(VideoFileKind.OtherWan, verdict.Kind);
            Assert.False(string.IsNullOrWhiteSpace(verdict.Note));
        }
    }

    [Fact]
    public void Picture_files_are_not_video_files_and_video_files_are_not_picture_files()
    {
        var sdxl = Header(("model.diffusion_model.input_blocks.0.0.weight", [320, 4, 3, 3]), ("conditioner.embedders.1.model.ln_final.weight", [1280]));
        var sdxlVae = Header(("decoder.conv_in.weight", [512, 4, 3, 3]), ("encoder.conv_in.weight", [128, 3, 3, 3]));
        Assert.Equal(VideoFileKind.Unknown, VideoFileRecognizer.Classify(sdxl).Kind);
        Assert.Equal(VideoFileKind.Unknown, VideoFileRecognizer.Classify(sdxlVae).Kind);

        // A video file dropped into the image folders is never offered as a checkpoint, LoRA, VAE or upscaler.
        foreach (var tensors in new[] { Wan22ModelTensors(), Wan22ModelTensors("model.diffusion_model."), Wan22VaeTensors(), FastDecoderTensors() })
        {
            var kind = ImageFileRecognizer.Classify(Header(tensors), "wan2.2_file.safetensors").Kind;
            Assert.DoesNotContain(kind, new[] { ImageFileKind.Checkpoint, ImageFileKind.Lora, ImageFileKind.Vae, ImageFileKind.Upscaler });
        }
    }

    [Fact]
    public void The_text_encoder_is_umt5_xxl_by_its_gguf_header()
    {
        Assert.Equal(VideoFileKind.Umt5Encoder, VideoFileRecognizer.ClassifyGguf("t5encoder", 4096, "Umt5 Xxl", "umt5", "umt5-xxl-encoder-Q8_0.gguf").Kind);
        Assert.Equal(VideoFileKind.Umt5Encoder, VideoFileRecognizer.ClassifyGguf("t5encoder", 4096, null, null, "umt5-xxl-encoder-Q4_K_M.gguf").Kind);
        // Flux's T5-XXL: the same width, another vocabulary.
        Assert.Equal(VideoFileKind.OtherWan, VideoFileRecognizer.ClassifyGguf("t5encoder", 4096, "T5 Xxl", "t5", "t5-v1_1-xxl-encoder-Q8_0.gguf").Kind);
        Assert.Equal(VideoFileKind.OtherWan, VideoFileRecognizer.ClassifyGguf("t5encoder", 2048, "Umt5 Xl", "umt5", "umt5-xl.gguf").Kind);
        Assert.Equal(VideoFileKind.Unknown, VideoFileRecognizer.ClassifyGguf("qwen35", 4096, "Huihui", null, "umt5-renamed.gguf").Kind);
        Assert.Equal(VideoFileKind.Unknown, VideoFileRecognizer.ClassifyGguf(null, null, null, null, "x.gguf").Kind);
    }

    // ---- Files on disk ----

    internal static void WriteSafetensors(string path, IEnumerable<(string Name, long[] Shape)> tensors, int padding = 64)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var header = tensors.ToDictionary(t => t.Name, t => (object)new { dtype = "F16", shape = t.Shape, data_offsets = new[] { 0, 0 } });
        var json = JsonSerializer.SerializeToUtf8Bytes(header);
        using var file = File.Create(path);
        file.Write(BitConverter.GetBytes((long)json.Length));
        file.Write(json);
        file.Write(new byte[padding]);
    }

    /// <summary>A GGUF header with string and number keys only, as the text encoder's starts.</summary>
    internal static void WriteGguf(string path, params (string Key, object Value)[] pairs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using var w = new BinaryWriter(file, Encoding.UTF8);
        w.Write(0x46554747u);
        w.Write(3u);
        w.Write(0UL);
        w.Write((ulong)pairs.Length);
        foreach (var (key, value) in pairs)
        {
            WriteString(w, key);
            switch (value)
            {
                case string s: w.Write(8u); WriteString(w, s); break;
                case uint n: w.Write(4u); w.Write(n); break;
                default: throw new ArgumentException("Only strings and uint here.");
            }
        }
        w.Write(new byte[64]);

        static void WriteString(BinaryWriter w, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            w.Write((ulong)bytes.Length);
            w.Write(bytes);
        }
    }

    internal static void WriteUmt5(string path) => WriteGguf(path,
        ("general.architecture", "t5encoder"), ("general.name", "Umt5 Xxl"), ("general.basename", "umt5"), ("t5encoder.embedding_length", 4096u));

    private string Root => Path.Combine(_dir, "models", VideoFolders.Name);

    private string PathOf(VideoFileRole role) => Path.Combine(Root, VideoDownloads.For(role).PartName);

    private void WriteAllFour()
    {
        WriteSafetensors(PathOf(VideoFileRole.Model), Wan22ModelTensors());
        WriteSafetensors(PathOf(VideoFileRole.Vae), Wan22VaeTensors());
        WriteUmt5(PathOf(VideoFileRole.TextEncoder));
        WriteSafetensors(PathOf(VideoFileRole.FastDecoder), FastDecoderTensors());
    }

    [Fact]
    public void An_empty_or_missing_folder_has_nothing_and_says_so()
    {
        var look = VideoLibrary.Look(Root);
        Assert.Equal("none", look.State);
        Assert.False(look.Ready);
        Assert.Equal(4, look.ToDownload.Count);
        Assert.Empty(look.Others);
        Assert.False(Directory.Exists(Root)); // looking creates nothing
        Assert.Equal("none", VideoLibrary.Look(null).State);
    }

    [Fact]
    public void Four_right_files_are_ready_and_say_they_are_not_the_recommended_size()
    {
        WriteAllFour();
        var look = VideoLibrary.Look(Root);
        Assert.True(look.Ready);
        Assert.Equal("ready", look.State);
        Assert.Empty(look.ToDownload);
        // These are a few bytes, not the recommended files: usable, with a note.
        Assert.All(look.Files, f => Assert.True(f.Unusual));
        Assert.All(look.Files, f => Assert.Contains("has not tried it", f.Note));
    }

    [Fact]
    public void A_missing_file_a_wrong_one_and_one_arriving_are_each_said()
    {
        WriteAllFour();
        File.Delete(PathOf(VideoFileRole.FastDecoder));
        // The model's name holding the decoder.
        WriteSafetensors(PathOf(VideoFileRole.Model), Wan22VaeTensors());
        File.Delete(PathOf(VideoFileRole.TextEncoder));

        var look = VideoLibrary.Look(Root, [VideoFileRole.TextEncoder]);
        Assert.False(look.Ready);
        Assert.Equal("arriving", look.State);
        Assert.Equal(VideoFileStatus.Wrong, look.Files.Single(f => f.Role == VideoFileRole.Model).Status);
        Assert.Contains("not the model", look.Files.Single(f => f.Role == VideoFileRole.Model).Note);
        Assert.Equal(VideoFileStatus.Missing, look.Files.Single(f => f.Role == VideoFileRole.FastDecoder).Status);
        Assert.Equal(VideoFileStatus.Arriving, look.Files.Single(f => f.Role == VideoFileRole.TextEncoder).Status);
        Assert.Equal(VideoFileStatus.Ready, look.Files.Single(f => f.Role == VideoFileRole.Vae).Status);
        // Only what is not there is downloaded: not the wrong file (deleted first), not the one arriving.
        Assert.Equal([VideoFileRole.FastDecoder], look.ToDownload);
        Assert.Single(look.Wrong);

        var later = VideoLibrary.Look(Root);
        Assert.Equal("incomplete", later.State);
    }

    [Fact]
    public void A_refused_format_is_listed_without_being_opened_and_other_files_are_named()
    {
        WriteAllFour();
        var pickle = Path.Combine(Root, VideoFolders.Vae, "wan_2.1_vae.pth");
        File.WriteAllBytes(pickle, [1, 2, 3]);
        WriteSafetensors(Path.Combine(Root, VideoFolders.Vae, "wan_2.1_vae.safetensors"), Wan22VaeTensors(16));
        WriteSafetensors(Path.Combine(Root, VideoFolders.DiffusionModels, "sub", "notes.safetensors"), [("x", [1])]);
        File.WriteAllText(Path.Combine(Root, VideoFolders.Vae, "readme.txt"), "not a model");

        // Held open by nothing else may be read; a refused file is never opened, so a lock on it changes nothing.
        using (new FileStream(pickle, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var look = VideoLibrary.Look(Root);
            Assert.True(look.Ready);
            var others = look.Others.ToDictionary(o => o.RelativePath, o => o.Verdict.Kind);
            Assert.Equal(VideoFileKind.Refused, others["wan_2.1_vae.pth"]);
            Assert.Equal(VideoFileKind.OtherWan, others["wan_2.1_vae.safetensors"]);
            Assert.Equal(VideoFileKind.Unknown, others["sub/notes.safetensors"]);
            Assert.DoesNotContain("readme.txt", others.Keys);
        }
    }

    [Fact]
    public void Delete_takes_the_four_files_and_their_unfinished_downloads_and_nothing_else()
    {
        WriteAllFour();
        var other = Path.Combine(Root, VideoFolders.Vae, "keep-me.safetensors");
        WriteSafetensors(other, [("x", [1])]);
        File.WriteAllBytes(PathOf(VideoFileRole.Model) + ".part", [1]);
        File.WriteAllBytes(PathOf(VideoFileRole.Model) + ".part.segments", [1]);

        Assert.Null(VideoLibrary.DeleteModel(Root));
        foreach (var role in Enum.GetValues<VideoFileRole>()) Assert.False(File.Exists(PathOf(role)));
        Assert.False(File.Exists(PathOf(VideoFileRole.Model) + ".part"));
        Assert.False(File.Exists(PathOf(VideoFileRole.Model) + ".part.segments"));
        Assert.True(File.Exists(other));
        Assert.Equal("none", VideoLibrary.Look(Root).State);

        var outside = Path.Combine(_dir, "outside.safetensors");
        File.WriteAllBytes(outside, [1]);
        Assert.NotNull(VideoLibrary.DeleteOther(Root, outside));
        Assert.NotNull(VideoLibrary.DeleteOther(Root, Path.Combine(Root, "..", "outside.safetensors")));
        Assert.True(File.Exists(outside));
        Assert.Null(VideoLibrary.DeleteOther(Root, other));
        Assert.False(File.Exists(other));
    }

    // ---- The chat-model list and the document search pick ----

    [Fact]
    public void The_chat_model_list_never_shows_the_video_folder()
    {
        var models = Path.Combine(_dir, "models");
        WriteUmt5(Path.Combine(models, VideoFolders.Name, VideoFolders.TextEncoders, "umt5-xxl-encoder-Q8_0.gguf"));
        WriteGguf(Path.Combine(models, "chat", "writer-Q8_0.gguf"), ("general.architecture", "llama"));
        // A folder named "video" deeper down is someone's own and is not skipped.
        WriteGguf(Path.Combine(models, "chat", "video", "clipper-Q4_K_M.gguf"), ("general.architecture", "llama"));

        Assert.Equal(["clipper-Q4_K_M", "writer-Q8_0"], ModelLibrary.Scan(models).Select(m => m.Name));
        // An extra folder above the models folder, and the video folder added as an extra folder, change nothing.
        Assert.Equal(["clipper-Q4_K_M", "writer-Q8_0"], ModelLibrary.ScanAll([models, _dir, Path.Combine(models, VideoFolders.Name)]).Select(m => m.Name));
        Assert.Equal(["clipper-Q4_K_M", "writer-Q8_0"], ModelLibrary.ScanAll([models + Path.DirectorySeparatorChar, _dir]).Select(m => m.Name));
    }

    [Fact]
    public async Task The_text_encoder_is_never_picked_for_document_search()
    {
        var models = Path.Combine(_dir, "models");
        var encoder = Path.Combine(models, VideoFolders.Name, VideoFolders.TextEncoders, "umt5-xxl-encoder-Q8_0.gguf");
        WriteUmt5(encoder);
        // Its header counts as a search model's: without the rule it would be the one picked.
        Assert.True(GgufReader.TryRead(encoder)!.IsEmbedding(encoder));

        using var http = new LoopbackHttp();
        var settings = new VoiceFlowTests.MemorySettings(new AppSettings { ModelsDirectory = models });
        using (var search = new EmbeddingServer(http, new LlamaServerLocator(new AppPaths(_dir)), settings, new NoRecords()))
            Assert.Null(await search.ModelPathAsync());

        var nomic = Path.Combine(models, "search", "nomic-embed-text-v1.5.Q8_0.gguf");
        WriteGguf(nomic, ("general.architecture", "nomic-bert"));
        using (var search = new EmbeddingServer(http, new LlamaServerLocator(new AppPaths(_dir)), settings, new NoRecords()))
            Assert.Equal(nomic, await search.ModelPathAsync());
    }

    private sealed class NoRecords : IModelRecordStore
    {
        public Task<IReadOnlyList<ModelRecord>> AllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelRecord>>([]);
        public Task<ModelRecord?> FindAsync(string path, CancellationToken ct = default) => Task.FromResult<ModelRecord?>(null);
        public Task SaveAsync(ModelRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---- The download ----

    [Fact]
    public void The_set_is_the_four_files_the_trial_used_with_their_published_checksums()
    {
        var set = VideoDownloads.Wan22;
        Assert.Equal(VideoFileRole.Model, set[0].Role);
        Assert.Equal(Enum.GetValues<VideoFileRole>().Order(), set.Select(d => d.Role).Order());
        var model = VideoFolders.ModelFor(@"D:\m\video", VideoFiles.Wan22);
        Assert.Equal(model.DiffusionPath, Path.Combine(@"D:\m\video", VideoDownloads.For(VideoFileRole.Model).PartName));
        Assert.Equal(model.VaePath, Path.Combine(@"D:\m\video", VideoDownloads.For(VideoFileRole.Vae).PartName));
        Assert.Equal(model.TextEncoderPath, Path.Combine(@"D:\m\video", VideoDownloads.For(VideoFileRole.TextEncoder).PartName));
        Assert.Equal(model.FastDecoderPath, Path.Combine(@"D:\m\video", VideoDownloads.For(VideoFileRole.FastDecoder).PartName));
        // The plan's table: 17.5 GB together.
        Assert.Equal(9_999_658_848 + 1_409_400_960 + 6_043_068_256 + 22_848_048, VideoDownloads.TotalBytes);
        Assert.All(set, d => Assert.Matches("^[0-9a-f]{64}$", d.Sha256));
        Assert.Equal(set.Count, set.Select(d => d.Sha256).Distinct().Count());
        Assert.All(set, d => Assert.True(VideoModel.IsOpenable(d.FileName)));

        var github = Assert.Single(set, d => d.FromGitHub);
        Assert.Equal(VideoFileRole.FastDecoder, github.Role);
        Assert.Contains(VideoDownloads.TaehvCommit, github.Url);
        Assert.Matches("^[0-9a-f]{40}$", VideoDownloads.TaehvCommit);
        Assert.True(OriginPolicy.IsAllowedHubUri(new Uri(github.Url!)));
        Assert.All(set.Where(d => !d.FromGitHub), d => Assert.True(OriginPolicy.IsAllowedHubUri(new Uri(HuggingFaceClient.ResolveUrl(d.Repo, d.SourcePath)))));
    }

    private static Dictionary<string, IReadOnlyList<RemoteFile>> Listings(Func<VideoDownload, RemoteFile?>? change = null)
    {
        var result = new Dictionary<string, IReadOnlyList<RemoteFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in VideoDownloads.Wan22.Where(d => !d.FromGitHub).GroupBy(d => d.Repo))
        {
            result[group.Key] = group.Select(d => change?.Invoke(d) ?? new RemoteFile(d.SourcePath, d.Bytes, d.Sha256))
                .Append(new RemoteFile("README.md", 100, null)).ToList();
        }
        return result;
    }

    [Fact]
    public void The_job_holds_every_missing_file_each_with_its_pinned_checksum()
    {
        var root = @"D:\m\video";
        var all = Enum.GetValues<VideoFileRole>();
        var job = VideoDownloadJobs.Build(root, all, Listings());
        Assert.Equal(VideoDownloads.UseCase, job.UseCase);
        Assert.Equal(root, job.Folder);
        Assert.Equal(VideoDownloads.Wan22.Select(d => d.PartName), job.Parts.Select(p => p.FileName));
        Assert.Equal(VideoDownloads.Wan22.Select(d => d.Sha256), job.Parts.Select(p => p.Sha256));
        Assert.All(job.Parts, p => Assert.False(string.IsNullOrEmpty(p.Sha256)));
        Assert.Equal(VideoDownloads.TotalBytes, job.Parts.Sum(p => p.Size));
        Assert.Equal(Path.Combine(root, "diffusion_models", "wan2.2_ti2v_5B_fp16.safetensors"), job.MainPath);
        Assert.All(job.Parts, p => Assert.True(OriginPolicy.IsAllowedHubUri(new Uri(p.Url))));
        Assert.StartsWith("https://raw.githubusercontent.com/madebyollin/taehv/", job.Parts[^1].Url);

        // Only the files asked for, in the set's order; the fast decoder needs no listing.
        var some = VideoDownloadJobs.Build(root, [VideoFileRole.FastDecoder, VideoFileRole.Vae], new Dictionary<string, IReadOnlyList<RemoteFile>>(Listings()));
        Assert.Equal([VideoDownloads.For(VideoFileRole.Vae).PartName, VideoDownloads.For(VideoFileRole.FastDecoder).PartName], some.Parts.Select(p => p.FileName));
        Assert.Single(VideoDownloadJobs.Build(root, [VideoFileRole.FastDecoder], new Dictionary<string, IReadOnlyList<RemoteFile>>()).Parts);
        Assert.Equal(["Comfy-Org/Wan_2.2_ComfyUI_Repackaged"], VideoDownloadJobs.ReposFor([VideoFileRole.Vae, VideoFileRole.FastDecoder]));
        Assert.Empty(VideoDownloadJobs.ReposFor([VideoFileRole.FastDecoder]));
    }

    [Fact]
    public void A_file_that_changed_on_hugging_face_or_is_gone_is_refused_before_anything_is_fetched()
    {
        var all = Enum.GetValues<VideoFileRole>();
        var model = VideoDownloads.For(VideoFileRole.Model);
        Assert.Throws<DownloadRefusedException>(() => VideoDownloadJobs.Build("D:\\v", all,
            Listings(d => d == model ? new RemoteFile(d.SourcePath, d.Bytes, new string('0', 64)) : null)));
        Assert.Throws<DownloadRefusedException>(() => VideoDownloadJobs.Build("D:\\v", all,
            Listings(d => d == model ? new RemoteFile(d.SourcePath, d.Bytes + 1, d.Sha256) : null)));
        Assert.Throws<DownloadRefusedException>(() => VideoDownloadJobs.Build("D:\\v", all,
            Listings(d => d == model ? new RemoteFile(d.SourcePath, d.Bytes, null) : null)));
        Assert.Throws<DownloadRefusedException>(() => VideoDownloadJobs.Build("D:\\v", all,
            Listings(d => d == model ? new RemoteFile("elsewhere/" + d.FileName, d.Bytes, d.Sha256) : null)));
        Assert.Throws<DownloadRefusedException>(() => VideoDownloadJobs.Build("D:\\v", all, new Dictionary<string, IReadOnlyList<RemoteFile>>()));
        Assert.Throws<DownloadRefusedException>(() => VideoDownloadJobs.Build("D:\\v", [], Listings()));
        // Hugging Face writes the checksum with or without its "sha256:" mark.
        Assert.NotNull(VideoDownloadJobs.Build("D:\\v", all, Listings(d => new RemoteFile(d.SourcePath, d.Bytes, "sha256:" + d.Sha256.ToUpperInvariant()))));
    }

    [Fact]
    public void A_download_in_the_list_says_which_files_are_arriving()
    {
        var root = Path.Combine(_dir, "video");
        var job = VideoDownloadJobs.Build(root, Enum.GetValues<VideoFileRole>(), Listings());
        job.Parts[0].Done = true;
        job.Status = DownloadStatus.Running;
        Assert.Equal([VideoFileRole.TextEncoder, VideoFileRole.Vae, VideoFileRole.FastDecoder], VideoDownloadJobs.Arriving([job], root));
        Assert.Same(job, VideoDownloadJobs.Unfinished([job], root + Path.DirectorySeparatorChar));
        Assert.Empty(VideoDownloadJobs.Arriving([job], Path.Combine(_dir, "other")));
        // A failed download brings nothing until it is tried again, but it is still in the list (Delete waits for it).
        job.Status = DownloadStatus.Failed;
        Assert.Empty(VideoDownloadJobs.Arriving([job], root));
        Assert.Same(job, VideoDownloadJobs.Unfinished([job], root));
        job.Status = DownloadStatus.Done;
        Assert.Empty(VideoDownloadJobs.Arriving([job], root));
        Assert.Null(VideoDownloadJobs.Unfinished([job], root));
        Assert.Null(VideoDownloadJobs.Unfinished([job], null));
    }

    [Fact]
    public void A_finished_video_download_makes_no_chat_model_record()
    {
        // The app's handler adds a chat-model record for any download it does not know; the video model is not one.
        var app = File.ReadAllText(Path.Combine(SolutionRoot(), "src", "DogAI.App", "App.xaml.cs"));
        var video = app.IndexOf("job.UseCase == DogAI.Core.Video.VideoDownloads.UseCase) { }", StringComparison.Ordinal);
        var record = app.IndexOf("else _ = records.SaveAsync(new ModelRecord", StringComparison.Ordinal);
        Assert.True(video > 0 && record > video);
    }

    // ---- Fit ----

    [Fact]
    public void A_card_of_8_gb_or_more_is_offered_the_model_and_a_smaller_one_is_not()
    {
        // M20 offered it from 16 GB; M21 step 5 from 8 GB, Quick only up to 16 GB (CardGuestsAndSizesTests has every size).
        const long MiB = 1L << 20;
        Assert.True(VideoDownloads.Offered(24_564 * MiB)); // this PC's 24 GB card, as it reports itself
        Assert.True(VideoDownloads.Offered(16_376 * MiB)); // a 16 GB card
        Assert.True(VideoDownloads.Offered(16_384 * MiB));
        Assert.True(VideoDownloads.Offered(12_282 * MiB)); // a 12 GB card
        Assert.True(VideoDownloads.Offered(15 * 1024 * MiB));
        Assert.True(VideoDownloads.Offered(8_188 * MiB)); // an 8 GB card
        Assert.False(VideoDownloads.Offered(6_144 * MiB)); // a 6 GB card
        Assert.False(VideoDownloads.Offered(null));

        Assert.StartsWith("Fits this PC", VideoDownloads.FitText(24_564 * MiB));
        Assert.Contains("24.0 GB", VideoDownloads.FitText(24_564 * MiB));
        Assert.Contains("15.0 GB", VideoDownloads.FitText(24_564 * MiB));
        Assert.Contains("16.0 GB", VideoDownloads.FitText(24_564 * MiB));
        Assert.StartsWith("Fits this PC for Quick clips", VideoDownloads.FitText(12_282 * MiB));
        Assert.Contains("not been tried", VideoDownloads.FitText(12_282 * MiB));
        Assert.Contains("8 GB or more", VideoDownloads.FitText(6_144 * MiB));
        Assert.Contains("No graphics card", VideoDownloads.FitText(null));
    }

    [Fact]
    public void The_missing_line_names_get_models_video()
    {
        var all = VideoDownloads.Missing([.. Enum.GetValues<VideoFileRole>()]);
        Assert.Equal(ModelsPlaces.GetVideoName, all.PlaceName);
        Assert.Equal("models/get-video", all.Address);
        Assert.Contains("four files", all.Text);
        var two = VideoDownloads.Missing([VideoFileRole.Vae, VideoFileRole.FastDecoder]);
        Assert.Contains("its decoder and the fast decoder", two.Text);
    }

    private static string SolutionRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DogAI.sln"))) root = root.Parent;
        return root!.FullName;
    }
}
