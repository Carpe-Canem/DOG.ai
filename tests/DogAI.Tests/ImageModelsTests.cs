using System.Text;
using System.Text.Json;
using DogAI.Core;
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Core.Settings;
using DogAI.Data;
using DogAI.Engine.Images;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>M9 step 4: recognising image model files, family defaults, the settings cards and prompt assembly.</summary>
public sealed class ImageModelsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dogai-images-" + Guid.NewGuid().ToString("N"));

    public ImageModelsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- Writing small .safetensors files: a header naming the tensors, then a few bytes of "weights". ----

    private static byte[] Safetensors(IEnumerable<(string Name, long[] Shape)> tensors, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var header = new Dictionary<string, object>();
        if (metadata is not null) header["__metadata__"] = metadata;
        foreach (var (name, shape) in tensors)
            header[name] = new { dtype = "F16", shape, data_offsets = new[] { 0, 0 } };
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header));
        return [.. BitConverter.GetBytes((long)json.Length), .. json, 0, 0, 0, 0];
    }

    private static SafetensorsHeader Header(IEnumerable<(string, long[])> tensors, IReadOnlyDictionary<string, string>? metadata = null) =>
        SafetensorsHeader.Read(new MemoryStream(Safetensors(tensors, metadata)));

    private static (string, long[])[] Keys(params string[] names) => names.Select(n => (n, new long[] { 4, 4 })).ToArray();

    private static readonly (string, long[])[] SdxlCheckpoint =
    [
        ("model.diffusion_model.input_blocks.0.0.weight", [320, 4, 3, 3]),
        ("conditioner.embedders.0.transformer.text_model.embeddings.position_embedding.weight", [77, 768]),
        ("conditioner.embedders.1.model.ln_final.weight", [1280]),
        ("first_stage_model.decoder.conv_in.weight", [512, 4, 3, 3]),
    ];

    private static readonly (string, long[])[] SdxlLora = Keys(
        "lora_te1_text_model_encoder_layers_0_mlp_fc1.lora_down.weight",
        "lora_te2_text_model_encoder_layers_0_mlp_fc1.lora_down.weight",
        "lora_unet_input_blocks_4_1_transformer_blocks_1_attn1_to_k.lora_down.weight");

    // The three sample LoRAs' training tags, as their files hold them (counts trimmed to the top few).
    private static Dictionary<string, string> TrainingTags(string folder, int pictures, params (string Tag, int Count)[] tags) => new()
    {
        ["ss_tag_frequency"] = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, int>> { [folder] = tags.ToDictionary(t => t.Tag, t => t.Count) }),
        ["ss_dataset_dirs"] = JsonSerializer.Serialize(new Dictionary<string, object> { [folder] = new { n_repeats = 4, img_count = pictures } }),
        ["ss_sd_model_name"] = "889818.safetensors",
        ["modelspec.architecture"] = "stable-diffusion-xl-v1-base/lora",
    };

    [Fact]
    public void Sdxl_checkpoint_is_recognised_with_its_family_from_the_name()
    {
        var verdict = ImageFileRecognizer.Classify(Header(SdxlCheckpoint), "illustrious_novaFurryXL_ilV180A.safetensors");
        Assert.Equal(ImageFileKind.Checkpoint, verdict.Kind);
        Assert.Equal(ImageFamily.Illustrious, verdict.Family);
        Assert.False(verdict.VPrediction);
        Assert.Contains("Illustrious", verdict.Note);
    }

    [Theory]
    [InlineData("NoobAI-XL-v1.1.safetensors", ImageFamily.NoobAI)]
    [InlineData("noobaiXLNAIXL_epsilonPred11Version.safetensors", ImageFamily.NoobAI)]
    [InlineData("PonyDiffusion-V6-XL.safetensors", ImageFamily.Pony)]
    [InlineData("autismmixSDXL_autismmixPony.safetensors", ImageFamily.Pony)]
    [InlineData("Illustrious-XL-v2.0.safetensors", ImageFamily.Illustrious)]
    [InlineData("waiNSFWIllustrious_v140.safetensors", ImageFamily.Illustrious)]
    [InlineData("sd_xl_base_1.0.safetensors", ImageFamily.Sdxl)]
    [InlineData("juggernautXL_v9.safetensors", ImageFamily.Sdxl)]
    [InlineData("Pencil_still_life.safetensors", ImageFamily.Sdxl)] // "il" inside a word is not Illustrious
    public void Family_is_guessed_from_whole_words(string file, ImageFamily expected)
    {
        var verdict = ImageFileRecognizer.Classify(Header(SdxlCheckpoint), file);
        Assert.Equal(expected, verdict.Family);
    }

    [Fact]
    public void Words_split_at_case_changes_digits_and_separators()
    {
        Assert.Equal(["illustrious", "nova", "furry", "xl", "il", "v", "180", "a"], ImageFileRecognizer.Words("illustrious_novaFurryXL_ilV180A"));
        Assert.Equal(["noob", "ai", "xl", "vpred", "v", "1", "0"], ImageFileRecognizer.Words("NoobAI-XL-Vpred-v1.0"));
    }

    [Fact]
    public void Family_can_come_from_the_metadata_name_when_the_file_was_renamed()
    {
        var meta = new Dictionary<string, string> { ["modelspec.title"] = "NoobAI XL 1.1" };
        Assert.Equal(ImageFamily.NoobAI, ImageFileRecognizer.Classify(Header(SdxlCheckpoint, meta), "model.safetensors").Family);
    }

    [Theory]
    [InlineData("NoobAI-XL-Vpred-v1.0.safetensors", null, false)]
    [InlineData("model.safetensors", "v", false)]
    [InlineData("model.safetensors", null, true)]
    public void V_prediction_is_read_from_name_metadata_or_marker(string file, string? declared, bool marker)
    {
        var tensors = SdxlCheckpoint.ToList();
        if (marker) tensors.Add(("v_pred", []));
        var meta = declared is null ? null : new Dictionary<string, string> { ["modelspec.prediction_type"] = declared };
        Assert.True(ImageFileRecognizer.Classify(Header(tensors, meta), file).VPrediction);
        Assert.False(ImageFileRecognizer.Classify(Header(SdxlCheckpoint), "model.safetensors").VPrediction);
    }

    [Fact]
    public void Other_kinds_are_told_apart()
    {
        Assert.Equal(ImageFileKind.Lora, ImageFileRecognizer.Classify(Header(SdxlLora), "x.safetensors").Kind);
        Assert.Equal(ImageFileKind.Vae, ImageFileRecognizer.Classify(Header([("decoder.conv_in.weight", [512, 4, 3, 3]), ("encoder.conv_in.weight", [128, 3, 3, 3])]), "vae.safetensors").Kind);
        Assert.Equal(ImageFileKind.Upscaler, ImageFileRecognizer.Classify(Header(Keys("conv_first.weight", "body.0.rdb1.conv1.weight")), "4x.safetensors").Kind);

        var sd15 = ImageFileRecognizer.Classify(Header(Keys("model.diffusion_model.input_blocks.0.0.weight", "cond_stage_model.transformer.text_model.final_layer_norm.weight")), "v1-5.safetensors");
        Assert.Equal(ImageFileKind.Unusable, sd15.Kind);
        Assert.Contains("1.5", sd15.Note);

        var sd15Lora = ImageFileRecognizer.Classify(Header(Keys("lora_te_text_model_encoder_layers_0_mlp_fc1.lora_down.weight", "lora_unet_down_blocks_0_attentions_0_transformer_blocks_0_attn1_to_k.lora_down.weight")), "old.safetensors");
        Assert.Equal(ImageFileKind.Unusable, sd15Lora.Kind);

        var flux = ImageFileRecognizer.Classify(Header(Keys("model.diffusion_model.double_blocks.0.img_attn.qkv.weight")), "flux1-dev.safetensors");
        Assert.Equal(ImageFileKind.Unusable, flux.Kind);
        Assert.Contains("Flux", flux.Note);

        var fluxLora = ImageFileRecognizer.Classify(Header(Keys("lora_unet_double_blocks_0_img_attn_proj.lora_down.weight")), "f.safetensors");
        Assert.Contains("Flux", fluxLora.Note);

        Assert.Equal(ImageFileKind.Unusable, ImageFileRecognizer.Classify(Header(Keys("some.other.weight")), "x.safetensors").Kind);

        var inpaint = SdxlCheckpoint.Select(t => t.Item1 == "model.diffusion_model.input_blocks.0.0.weight" ? (t.Item1, new long[] { 320, 9, 3, 3 }) : t);
        Assert.Equal(ImageFileKind.Unusable, ImageFileRecognizer.Classify(Header(inpaint), "inpaint.safetensors").Kind);
    }

    [Fact]
    public void Lora_trigger_words_come_from_tags_in_every_training_picture()
    {
        var micro = TrainingTags("4_addmicrodetails details", 50, ("addmicrodetails", 50), ("solo", 23), ("1girl", 23), ("detailed eyes", 10), ("(yellow highlights:1.2)", 1));
        var incase = TrainingTags("img", 77, ("icjournals", 77), ("blocky", 77), ("painterly", 77), ("multiple girls", 49), ("long hair", 30), ("1girl", 25));
        var marian = TrainingTags("img", 22, ("maid_marian", 22), ("1girl", 22), ("furry female", 22), ("furry", 22), ("solo", 20), ("animal ears", 19), ("dress", 15));

        Assert.Equal(["addmicrodetails"], ImageFileRecognizer.Triggers(micro));
        Assert.Equal(["blocky", "icjournals", "painterly"], ImageFileRecognizer.Triggers(incase).Order());
        Assert.Equal(["maid_marian"], ImageFileRecognizer.Triggers(marian));
        Assert.Empty(ImageFileRecognizer.Triggers(new Dictionary<string, string>()));

        var common = ImageFileRecognizer.CommonTags(marian);
        Assert.DoesNotContain("maid_marian", common);
        Assert.Equal("1girl", common[0]);
        Assert.Contains("yellow highlights", ImageFileRecognizer.CommonTags(micro)); // weights and brackets are stripped

        var verdict = ImageFileRecognizer.Classify(Header(SdxlLora, marian), "Maid_marian.safetensors");
        Assert.Equal(["maid_marian"], verdict.Lora!.Triggers);
    }

    [Fact]
    public void Declared_trigger_phrase_comes_first_and_damaged_metadata_is_ignored()
    {
        var meta = new Dictionary<string, string> { ["modelspec.trigger_phrase"] = "inkstyle, bold lines", ["ss_tag_frequency"] = "{not json" };
        Assert.Equal(["inkstyle", "bold lines"], ImageFileRecognizer.Triggers(meta));
        Assert.Empty(ImageFileRecognizer.CommonTags(meta));
    }

    [Fact]
    public void Lora_made_for_a_family_says_so()
    {
        var meta = new Dictionary<string, string> { ["ss_sd_model_name"] = "illustriousXL20_v20.safetensors" };
        var verdict = ImageFileRecognizer.Classify(Header(SdxlLora, meta), "AddMicroDetails.safetensors");
        Assert.Equal(ImageFamily.Illustrious, verdict.Lora!.MadeFor);
        Assert.Contains("made for Illustrious", verdict.Note);
    }

    [Fact]
    public void Not_a_safetensors_header_is_refused_cleanly()
    {
        Assert.Throws<InvalidDataException>(() => SafetensorsHeader.Read(new MemoryStream([.. BitConverter.GetBytes(SafetensorsHeader.MaxHeaderBytes + 1), 0])));
        Assert.Throws<InvalidDataException>(() => SafetensorsHeader.Read(new MemoryStream([.. BitConverter.GetBytes(4L), .. "[1]"u8, 0])));
        Assert.ThrowsAny<EndOfStreamException>(() => SafetensorsHeader.Read(new MemoryStream([.. BitConverter.GetBytes(100L), .. "{}"u8])));
    }

    [Fact]
    public void Ckpt_files_are_refused_without_being_opened()
    {
        var path = Path.Combine(_dir, "old.ckpt");
        File.WriteAllBytes(path, Safetensors(SdxlCheckpoint));
        // Held open with no sharing: opening it would fail, so a "refused" answer proves it was never opened.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var verdict = ImageLibrary.Recognise(new FileInfo(path));
            Assert.Equal(ImageFileKind.Refused, verdict.Kind);
            Assert.Contains(".safetensors", verdict.Note);
        }
        foreach (var ext in new[] { ".pt", ".pth", ".bin" })
            Assert.True(ImageFileRecognizer.RefusedExtensions.Contains(ext));
        Assert.False(ImageFileRecognizer.IsModelFile("preview.png"));
        Assert.True(ImageFileRecognizer.IsModelFile("x.CKPT"));
    }

    [Fact]
    public void Scan_lists_every_file_and_says_where_misplaced_ones_belong()
    {
        var root = ImageLibrary.Ensure(Path.Combine(_dir, "image"));
        foreach (var folder in ImageFolders.All) Assert.True(Directory.Exists(Path.Combine(root, folder)));
        File.WriteAllBytes(Path.Combine(root, "checkpoints", "Illustrious-XL-v2.0.safetensors"), Safetensors(SdxlCheckpoint));
        File.WriteAllBytes(Path.Combine(root, "checkpoints", "a lora.safetensors"), Safetensors(SdxlLora));
        Directory.CreateDirectory(Path.Combine(root, "loras", "style"));
        File.WriteAllBytes(Path.Combine(root, "loras", "style", "ink.safetensors"), Safetensors(SdxlLora));
        File.WriteAllBytes(Path.Combine(root, "loras", "old.ckpt"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "loras", "broken.safetensors"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "loras", "ink.preview.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(root, "loose.safetensors"), Safetensors(SdxlCheckpoint));

        var files = ImageLibrary.Scan(root);
        Assert.Equal(6, files.Count); // the picture is not a model file

        var checkpoint = Assert.Single(files, f => f.Usable && f.Kind == ImageFileKind.Checkpoint);
        Assert.Equal("Illustrious-XL-v2.0.safetensors", checkpoint.RelativePath);
        var lora = Assert.Single(files, f => f.Usable && f.Kind == ImageFileKind.Lora);
        Assert.Equal("style/ink.safetensors", lora.RelativePath); // the path the engine takes for a LoRA

        Assert.Equal("loras", files.Single(f => f.RelativePath == "a lora.safetensors").Misplaced);
        Assert.Equal("checkpoints", files.Single(f => f.RelativePath == "loose.safetensors").Misplaced);
        Assert.Equal(ImageFileKind.Refused, files.Single(f => f.RelativePath == "old.ckpt").Kind);
        Assert.Equal(ImageFileKind.Unusable, files.Single(f => f.RelativePath == "broken.safetensors").Kind);

        Assert.Empty(ImageLibrary.Scan(Path.Combine(_dir, "missing")));
        Assert.Empty(ImageLibrary.Scan(null));
    }

    [Fact]
    public void Family_defaults_follow_the_plan_and_the_model_cards()
    {
        var il = CheckpointSettings.ForFamily(ImageFamily.Illustrious);
        Assert.Equal((PromptStyle.Tags, "euler_a", 5.5, 2, 28), (il.Style, il.Sampler, il.Guidance, il.ClipSkip, il.Steps));
        Assert.Equal("masterpiece, best quality, amazing quality, very aesthetic, absurdres", il.QualityBefore);
        Assert.Equal("worst quality, low quality, bad quality, lowres, bad anatomy, bad hands, jpeg artifacts, signature, watermark, text", il.Negative);
        Assert.Equal(ImagePrediction.Auto, il.Prediction);

        var vpred = CheckpointSettings.ForFamily(ImageFamily.NoobAI, vPrediction: true);
        Assert.Equal((ImagePrediction.V, "euler"), (vpred.Prediction, vpred.Sampler));
        Assert.StartsWith("score_9", CheckpointSettings.ForFamily(ImageFamily.Pony).QualityBefore);
        Assert.Equal(PromptStyle.Sentences, CheckpointSettings.ForFamily(ImageFamily.Sdxl).Style);
        Assert.Equal(ImagePrediction.V, CheckpointSettings.ForFamily(ImageFamily.Sdxl, vPrediction: true).Prediction);

        foreach (var family in Enum.GetValues<ImageFamily>())
        foreach (var v in new[] { false, true })
        {
            var s = CheckpointSettings.ForFamily(family, v);
            Assert.Empty(s.Problems());
            Assert.Contains(SamplerNames.Samplers, n => n.Id == s.Sampler);
            Assert.True(s.Scheduler is null || SamplerNames.Schedulers.Any(n => n.Id == s.Scheduler));
            Assert.Empty(ImagePrompt.Compose(s, "1girl").Problems());
        }
    }

    [Fact]
    public void Bad_settings_are_refused_in_plain_words()
    {
        var s = CheckpointSettings.ForFamily(ImageFamily.Illustrious);
        Assert.NotEmpty((s with { Steps = 0 }).Problems());
        Assert.NotEmpty((s with { Guidance = 99 }).Problems());
        Assert.NotEmpty((s with { ClipSkip = 0 }).Problems());
        Assert.NotEmpty((s with { Vae = @"..\..\x.safetensors" }).Problems());
        Assert.NotEmpty(new LoraSettings("", 9).Problems());
    }

    [Fact]
    public async Task Settings_cards_round_trip_and_untouched_ones_follow_the_defaults()
    {
        var store = new MemoryDocumentStore();
        var catalog = new ImageModelCatalog(store);
        await catalog.LoadAsync();
        var verdict = new ImageFileVerdict(ImageFileKind.Checkpoint, "", ImageFamily.Illustrious);

        Assert.Equal(CheckpointSettings.ForFamily(ImageFamily.Illustrious), catalog.CheckpointFor("il.safetensors", verdict));
        Assert.False(catalog.IsChanged("il.safetensors"));

        var mine = CheckpointSettings.ForFamily(ImageFamily.Illustrious) with { Steps = 32, Guidance = 6, Vae = "sdxl-vae-fp16-fix.safetensors", QualityAfter = "  sharp  " };
        await catalog.SaveCheckpointAsync("sub\\il.safetensors", mine);
        await catalog.SaveLoraAsync("style/ink.safetensors", new LoraSettings(" inkstyle ", 0.6));
        await catalog.ChooseAsync("sub\\il.safetensors");
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SaveCheckpointAsync("x.safetensors", mine with { Steps = 0 }));

        var again = new ImageModelCatalog(store);
        await again.LoadAsync();
        Assert.Equal(mine with { QualityAfter = "sharp" }, again.CheckpointFor("sub/il.safetensors", verdict));
        Assert.True(again.IsChanged("SUB/IL.safetensors"));
        Assert.Equal(new LoraSettings("inkstyle", 0.6), again.LoraFor("style\\ink.safetensors", null));
        Assert.Equal("sub/il.safetensors", again.ChosenAmong(["a.safetensors", "sub/il.safetensors"]));
        Assert.Equal("a.safetensors", again.ChosenAmong(["a.safetensors"])); // the chosen one is gone: the first

        await again.ResetCheckpointAsync("sub/il.safetensors");
        Assert.Equal(CheckpointSettings.ForFamily(ImageFamily.Illustrious), again.CheckpointFor("sub/il.safetensors", verdict));
        var facts = new LoraFacts(["maid_marian"], [], null, false);
        await again.ResetLoraAsync("style/ink.safetensors");
        Assert.Equal(new LoraSettings("maid_marian", 1.0), again.LoraFor("style/ink.safetensors", facts));
    }

    [Fact]
    public void Prompt_is_assembled_from_the_card_and_loras()
    {
        var s = CheckpointSettings.ForFamily(ImageFamily.Illustrious) with { QualityAfter = "sharp focus," };
        var request = ImagePrompt.Compose(s, "1girl, forest", [("style/ink.safetensors", new LoraSettings("inkstyle", 0.7)), ("plain.safetensors", new LoraSettings(""))]);
        Assert.Equal("masterpiece, best quality, amazing quality, very aesthetic, absurdres, inkstyle, 1girl, forest, sharp focus", request.Prompt);
        Assert.Equal(s.Negative, request.NegativePrompt);
        Assert.Equal((28, "euler_a", 5.5, (int?)2, 832, 1216), (request.Steps, request.Sampler, request.Guidance, request.ClipSkip, request.Width, request.Height));
        Assert.Equal([new LoraUse("style/ink.safetensors", 0.7), new LoraUse("plain.safetensors", 1.0)], request.Loras!);
        Assert.Empty(request.Problems());

        var model = ImageFolders.ModelFor(@"C:\m\image", "sub/il.safetensors", s with { Vae = "fix.safetensors", Prediction = ImagePrediction.V });
        Assert.Equal(new ImageModel(@"C:\m\image\checkpoints\sub\il.safetensors", @"C:\m\image\loras", @"C:\m\image\vae\fix.safetensors", ImagePrediction.V), model);
        Assert.StartsWith("1girl", ImagePrompt.TestSubject(PromptStyle.Tags));
        Assert.StartsWith("A woman", ImagePrompt.TestSubject(PromptStyle.Sentences));
    }

    [Fact]
    public async Task Settings_are_kept_in_the_database_without_full_paths_and_survive_a_portable_move()
    {
        var oldHome = Path.Combine(_dir, "old place");
        var newHome = Path.Combine(_dir, "new place");
        var paths = new AppPaths(Path.Combine(newHome, "data"));
        paths.EnsureCreated();
        var mine = CheckpointSettings.ForFamily(ImageFamily.NoobAI) with { Vae = "fix.safetensors" };
        using (var services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider())
        {
            await services.MigrateDogDatabaseAsync();
            await services.GetRequiredService<ISettingsStore>().SaveAsync(new AppSettings { ModelsDirectory = Path.Combine(oldHome, "models") });
            var catalog = new ImageModelCatalog(services.GetRequiredService<IDocumentStore>());
            await catalog.LoadAsync();
            await catalog.SaveCheckpointAsync("noob.safetensors", mine);
            await catalog.ChooseAsync("noob.safetensors");
        }
        SqliteConnection.ClearAllPools();

        using (var db = new SqliteConnection(DatabaseLock.ConnectionString(paths.DatabaseFile, null)))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Json FROM Settings WHERE Key = $k";
            cmd.Parameters.AddWithValue("$k", ImageModelCatalog.Key);
            var json = (string)cmd.ExecuteScalar()!;
            Assert.DoesNotContain(":\\\\", json); // no drive-rooted path
            Assert.DoesNotContain(_dir.Replace("\\", "\\\\"), json);
        }

        File.WriteAllText(paths.HomeRecordFile, oldHome);
        File.WriteAllText(Path.Combine(newHome, AppPaths.PortableMarker), "portable");
        var moved = AppPaths.Resolve(newHome, overrideRoot: null);
        Assert.NotNull(PortableMove.Apply(moved));
        using (var services = new ServiceCollection().AddSingleton(moved).AddDogData().BuildServiceProvider())
        {
            var settings = await services.GetRequiredService<ISettingsStore>().LoadAsync();
            Assert.Equal(Path.Combine(newHome, "models", "image"), settings.ImageFolder());
            var catalog = new ImageModelCatalog(services.GetRequiredService<IDocumentStore>());
            await catalog.LoadAsync();
            Assert.Equal(mine, catalog.CheckpointFor("noob.safetensors", new ImageFileVerdict(ImageFileKind.Checkpoint, "")));
            Assert.Equal("noob.safetensors", catalog.Chosen);
        }
    }

    [Fact]
    public void Recommended_downloads_are_recognised_by_their_saved_names()
    {
        foreach (var d in ImageDownloads.All)
        {
            Assert.Contains(d.Folder, ImageFolders.All);
            Assert.EndsWith(".safetensors", d.SaveAs);
            Assert.True(ImageRequest.IsSafeRelativePath(d.SaveAs));
            if (d.Folder != ImageFolders.Checkpoints) continue;
            var verdict = ImageFileRecognizer.Classify(Header(SdxlCheckpoint), d.SaveAs);
            var expected = d.Title switch
            {
                var t when t.StartsWith("Illustrious") => ImageFamily.Illustrious,
                var t when t.StartsWith("NoobAI") => ImageFamily.NoobAI,
                var t when t.StartsWith("Pony") => ImageFamily.Pony,
                _ => ImageFamily.Sdxl,
            };
            Assert.Equal(expected, verdict.Family);
            Assert.Equal(d.Title.Contains("V-Pred"), verdict.VPrediction);
        }
    }

    /// <summary>The owner's sample files, read where they are, when the image test variables point at them.</summary>
    [Fact]
    public void Sample_files_are_recognised_when_present()
    {
        if (Environment.GetEnvironmentVariable("DOGAI_TEST_IMAGE_MODEL") is { Length: > 0 } checkpoint && File.Exists(checkpoint))
        {
            var verdict = ImageLibrary.Recognise(new FileInfo(checkpoint));
            Assert.Equal(ImageFileKind.Checkpoint, verdict.Kind);
            Assert.Equal(ImageFamily.Illustrious, verdict.Family);
            Assert.False(verdict.VPrediction);
        }
        if (Environment.GetEnvironmentVariable("DOGAI_TEST_IMAGE_LORAS") is { Length: > 0 } folder && Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.safetensors"))
            {
                var verdict = ImageLibrary.Recognise(new FileInfo(file));
                Assert.Equal(ImageFileKind.Lora, verdict.Kind);
                var expected = Path.GetFileName(file) switch
                {
                    "AddMicroDetails_Illustrious_v7.safetensors" => new[] { "addmicrodetails" },
                    "Incase_New_IllustV3.safetensors" => ["icjournals", "blocky", "painterly"],
                    "Maid_marian.safetensors" => ["maid_marian"],
                    _ => null,
                };
                if (expected is not null) Assert.Equal(expected.Order(), verdict.Lora!.Triggers.Order());
            }
        }
    }
}
