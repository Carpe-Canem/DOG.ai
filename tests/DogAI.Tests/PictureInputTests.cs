using System.Text;
using System.Text.Json.Nodes;
using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Engine;

namespace DogAI.Tests;

/// <summary>
/// Picture input on the engine's side (M17 step 2): what a request with pictures may hold (stored JPEGs as bytes and
/// nothing else), how a model's projector is found, how the engine is started with one, and what the projector costs.
/// The real engine is in <see cref="PictureInputSmokeTests"/>.
/// </summary>
public sealed class PictureInputTests : IDisposable
{
    private static readonly byte[] A = TestJpegs.Make(256, 160, seed: 1);
    private static readonly byte[] B = TestJpegs.Make(1024, 640, seed: 2);
    private static readonly SamplerPreset Preset = SamplerPresets.Get(null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-projector-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Url(JsonNode part) => part["image_url"]!["url"]!.GetValue<string>();

    // ---- What a request holds ----

    [Fact]
    public void A_chat_request_carries_a_picture_only_as_the_stored_jpegs_bytes()
    {
        PromptMessage[] messages =
        [
            new("system", "Be brief."),
            new("user", "Before after", [new PromptPicture(1, A, 256, 160, After: 7), new PromptPicture(2, B, 1024, 640)]),
            new("assistant", "A dog."),
            new("user", "", [new PromptPicture(3, A, 256, 160)]),
        ];

        var body = LlamaApi.ChatRequestBody(messages, Preset);

        var sent = body["messages"]!.AsArray();
        // A turn without pictures is plain text, exactly as before M17.
        Assert.Equal("Be brief.", sent[0]!["content"]!.GetValue<string>());
        Assert.Equal("A dog.", sent[2]!["content"]!.GetValue<string>());
        // A turn with pictures is a list: text and pictures in their order.
        var parts = sent[1]!["content"]!.AsArray();
        Assert.Equal(["image_url", "text", "image_url", "text"], parts.Select(p => p!["type"]!.GetValue<string>()));
        Assert.Equal("Before ", parts[1]!["text"]!.GetValue<string>());
        Assert.Equal("after", parts[3]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String(B), Url(parts[0]!));
        Assert.Equal("data:image/jpeg;base64," + Convert.ToBase64String(A), Url(parts[2]!));
        // A picture with no words: the picture alone, no empty text piece.
        Assert.Equal(["image_url"], sent[3]!["content"]!.AsArray().Select(p => p!["type"]!.GetValue<string>()));

        // There is no other kind of part anywhere in the request, and no address: every picture is bytes DOG.ai stored.
        foreach (var part in sent.Where(m => m!["content"] is JsonArray).SelectMany(m => m!["content"]!.AsArray()))
        {
            var kind = part!["type"]!.GetValue<string>();
            Assert.Contains(kind, new[] { "text", "image_url" });
            if (kind != "image_url") continue;
            Assert.StartsWith("data:image/jpeg;base64,", Url(part));
            Assert.Null(AttachedPictures.Problem(Convert.FromBase64String(Url(part)["data:image/jpeg;base64,".Length..])));
            Assert.Equal(["url"], part["image_url"]!.AsObject().Select(p => p.Key));
        }
        var text = body.ToJsonString();
        Assert.DoesNotContain("http", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file:", text, StringComparison.OrdinalIgnoreCase);

        // Without pictures the whole request is byte for byte what it was.
        PromptMessage[] plain = [new("system", "Be brief."), new("user", "Hi")];
        Assert.Equal("""[{"role":"system","content":"Be brief."},{"role":"user","content":"Hi"}]""", LlamaApi.ChatRequestBody(plain, Preset)["messages"]!.ToJsonString());
    }

    [Fact]
    public void Nothing_but_a_checked_jpeg_is_ever_sent()
    {
        // On bytes it cannot read the engine tries to start another program; given an address it fetches it.
        byte[][] refused =
        [
            KeptPictureTests.Webp(64, 64),
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13],
            Encoding.ASCII.GetBytes("http://127.0.0.1:1/picture.jpg"),
            Encoding.ASCII.GetBytes("file:///C:/Users/owner/picture.jpg"),
            Encoding.ASCII.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>"),
            TestJpegs.Make(2048, 1024),
            TestJpegs.Make(64, 64, extra: TestJpegs.Exif()),
            TestJpegs.Make(64, 64, extra: TestJpegs.Profile()),
            TestJpegs.Make(64, 64, after: [1, 2, 3]),
            [],
        ];
        foreach (var bytes in refused)
        {
            var picture = new PromptPicture(1, bytes, 64, 64);
            Assert.Throws<EngineException>(() => LlamaApi.ChatRequestBody([new("user", "Look", [picture])], Preset));
            Assert.Throws<EngineException>(() => LlamaApi.CompletionPrompt("Look", [picture], "<m>"));
            Assert.Throws<EngineException>(() => LlamaApi.CountedPicture(bytes));
        }
        var counted = LlamaApi.CountedPicture(A);
        Assert.Equal(("image", "base64", "image/jpeg", Convert.ToBase64String(A)),
            (counted["type"]!.GetValue<string>(), counted["source"]!["type"]!.GetValue<string>(), counted["source"]!["media_type"]!.GetValue<string>(), counted["source"]!["data"]!.GetValue<string>()));
    }

    [Fact]
    public void A_raw_prompt_names_each_pictures_place_with_the_engines_own_marker()
    {
        const string marker = "<__media_x7__>";

        var prompt = LlamaApi.CompletionPrompt("<user> Look at this. <assistant> It is", [new PromptPicture(2, B, 1024, 640, After: 21), new PromptPicture(1, A, 256, 160, After: 7)], marker);

        Assert.Equal($"<user> {marker}Look at this. {marker}<assistant> It is", prompt["prompt_string"]!.GetValue<string>());
        Assert.Equal([Convert.ToBase64String(A), Convert.ToBase64String(B)], prompt["multimodal_data"]!.AsArray().Select(d => d!.GetValue<string>()));
        // Without pictures the prompt is the text, exactly as before.
        Assert.Equal("Just text", LlamaApi.CompletionPrompt("Just text", [], marker).GetValue<string>());
        // A text that holds the engine's marker would count as one picture too many: refused here, in words.
        Assert.Throws<EngineException>(() => LlamaApi.CompletionPrompt($"He typed {marker} once", [new PromptPicture(1, A, 256, 160)], marker));
    }

    [Fact]
    public void Too_much_for_the_context_is_said_in_words_with_both_numbers()
    {
        const string error = """{"code":400,"message":"request (3291 tokens) exceeds the available context size (2048 tokens), try increasing it","type":"exceed_context_size_error","n_prompt_tokens":3291,"n_ctx":2048}""";

        var asAnswer = LlamaApi.ErrorText("{\"error\":" + error + "}", 400);
        var inStream = LlamaApi.ErrorText(error, 0);

        Assert.Equal(ContextBudgeter.TooLong(3291, 2048), asAnswer);
        Assert.Equal(asAnswer, inStream);
        Assert.Contains("3,291", asAnswer);
        Assert.Contains("2,048", asAnswer);
        Assert.Contains(ModelsPlaces.LibraryName, asAnswer);
        // Any other problem is passed on as the engine worded it.
        Assert.Equal("The model engine reported: image input is not supported - hint: if this is unexpected, you may need to provide the mmproj",
            LlamaApi.ErrorText("""{"error":{"code":500,"message":"image input is not supported - hint: if this is unexpected, you may need to provide the mmproj","type":"server_error"}}""", 500));
    }

    [Fact]
    public void The_engine_says_whether_the_loaded_model_sees()
    {
        var with = LlamaApi.ReadProps(JsonNode.Parse("""{"default_generation_settings":{"n_ctx":8192},"modalities":{"vision":true,"audio":false},"media_marker":"<__media_a__>"}""")!);
        var without = LlamaApi.ReadProps(JsonNode.Parse("""{"default_generation_settings":{"n_ctx":4096},"modalities":{"vision":false,"audio":false}}""")!);
        var old = LlamaApi.ReadProps(JsonNode.Parse("""{"default_generation_settings":{"n_ctx":4096}}""")!);

        Assert.Equal((8192, true), (with.ContextLength, with.Vision));
        Assert.Equal((4096, false), (without.ContextLength, without.Vision));
        Assert.False(old.Vision);
        Assert.Null(LlamaApi.ReadProps(JsonNode.Parse("{}")!).ContextLength);
    }

    // ---- Starting the engine ----

    [Fact]
    public void A_model_without_a_projector_is_started_exactly_as_before_and_one_with_it_gets_two_more_switches()
    {
        string[] before =
        [
            "--model", @"D:\m\model.gguf", "--host", "127.0.0.1", "--port", "5123", "--ctx-size", "8192", "--gpu-layers", "999",
            "--parallel", "1", "--no-webui", "--jinja",
        ];

        Assert.Equal(before, EngineManager.Arguments(@"D:\m\model.gguf", 5123, 8192, -1, null));
        var with = EngineManager.Arguments(@"D:\m\model.gguf", 5123, 8192, -1, @"D:\m\mmproj-F16.gguf");
        Assert.Equal([.. before, "--mmproj", @"D:\m\mmproj-F16.gguf", "--image-max-tokens", "1024"], with);
        Assert.Equal("28", EngineManager.Arguments("m.gguf", 1, 4096, 28, "p.gguf")[9]);
        // Pictures arrive only as bytes in a request: no folder the engine may read from, and nothing it may fetch.
        Assert.DoesNotContain(with, a => a is "--media-path" or "--mmproj-url" or "--mmproj-auto" or "-mm" or "-mmu");
    }

    [Fact]
    public void The_engine_inherits_none_of_the_picture_librarys_settings()
    {
        var start = new System.Diagnostics.ProcessStartInfo("llama-server");
        start.Environment["MTMD_BACKEND_DEVICE"] = "cpu";
        start.Environment["mtmd_debug_image"] = "1";
        start.Environment["LLAMA_ARG_MMPROJ_URL"] = "http://example.invalid/mmproj.gguf";
        start.Environment["KEEP_ME"] = "x";

        var removed = EngineEnvironment.Clean(start);

        Assert.Equal(3, removed.Count);
        Assert.DoesNotContain(start.Environment.Keys, k => k.StartsWith("MTMD_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("LLAMA_ARG_", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("x", start.Environment["KEEP_ME"]);
    }

    [Fact]
    public void An_engine_that_stopped_with_the_projector_is_named_as_the_reason()
    {
        var note = EngineManager.PictureInputStopped("Huihui Qwen3.5 9B", "mmproj-F16.gguf");
        Assert.Contains("Huihui Qwen3.5 9B was loaded without picture input", note);
        Assert.Contains("mmproj-F16.gguf", note);
        Assert.Contains(ModelsPlaces.EnginesName, note);
    }

    // ---- Which projector belongs to a model ----

    /// <summary>A GGUF file's header with these values and nothing else.</summary>
    private string Gguf(string name, params (string Key, object Value)[] values)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream, Encoding.UTF8);
        void Str(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            w.Write((ulong)bytes.Length);
            w.Write(bytes);
        }
        w.Write(Encoding.ASCII.GetBytes("GGUF"));
        w.Write(3u);
        w.Write(0UL);
        w.Write((ulong)values.Length);
        foreach (var (key, value) in values)
        {
            Str(key);
            switch (value)
            {
                case string text: w.Write(8u); Str(text); break;
                case bool flag: w.Write(7u); w.Write((byte)(flag ? 1 : 0)); break;
                case int number: w.Write(4u); w.Write((uint)number); break;
                default: throw new ArgumentException(key);
            }
        }
        w.Write(new byte[64]); // so files differ in more than their name
        return path;
    }

    private string Model(string name, int embedding, string architecture = "qwen35") =>
        Gguf(name, ("general.architecture", architecture), ("general.name", "A model"), ($"{architecture}.embedding_length", embedding), ($"{architecture}.block_count", 32));

    private string Projector(string name, int dim, bool vision = true, string type = "mmproj") =>
        Gguf(name, ("general.architecture", "clip"), ("general.type", type), ("clip.has_vision_encoder", vision), ("clip.vision.projection_dim", dim),
            ("clip.projector_type", "qwen3vl_merger"));

    [Fact]
    public void A_projectors_header_is_read()
    {
        var info = GgufReader.TryRead(Projector("mmproj-F16.gguf", 4096))!;
        Assert.Equal(("clip", "mmproj", 4096, true, true), (info.Architecture, info.Type, info.ProjectionDim, info.HasVisionEncoder, info.IsVisionProjector));
        Assert.False(GgufReader.TryRead(Projector("mmproj-sound.gguf", 4096, vision: false))!.IsVisionProjector);
        Assert.False(GgufReader.TryRead(Model("chat.Q8_0.gguf", 4096))!.IsVisionProjector);
    }

    [Fact]
    public void A_models_projector_is_the_one_in_its_folder_whose_width_matches_and_f16_comes_first()
    {
        var model = Model("Huihui.Q8_0.gguf", 4096);
        Assert.Null(ModelProjector.Find(model)); // none beside it: loaded as before

        Projector("mmproj-large-BF16.gguf", 5120);                    // another model's: it would stop the engine after the load
        Projector("Huihui.mmproj-sound-f16.gguf", 4096, vision: false);   // reads sound, not pictures
        Model("mmproj-in-name-only-F16.gguf", 4096);                  // not a projector by its header
        File.WriteAllText(Path.Combine(_root, "mmproj-notes-F16.gguf"), "not a GGUF file at all");
        Projector(Path.Combine("deeper", "mmproj-F16.gguf"), 4096);   // not in the model's own folder
        Assert.Null(ModelProjector.Find(model));

        var f32 = Projector("Huihui.mmproj-f32.gguf", 4096);
        Assert.Null(ModelProjector.Find(model)); // never the F32 file
        var other = Projector("Huihui.mmproj-Q4_K_M.gguf", 4096);
        Assert.Equal(other, ModelProjector.Find(model)!.Path);
        var q8 = Projector("Huihui.mmproj-Q8_0.gguf", 4096);
        Assert.Equal(q8, ModelProjector.Find(model)!.Path);
        var bf16 = Projector("mmproj-BF16.gguf", 4096);
        Assert.Equal(bf16, ModelProjector.Find(model)!.Path);
        var f16 = Projector("Huihui.mmproj-f16.gguf", 4096);
        var found = ModelProjector.Find(model)!;
        Assert.Equal((f16, new FileInfo(f16).Length, "Huihui.mmproj-f16.gguf"), (found.Path, found.SizeBytes, found.Name));
        Assert.NotNull(f32);

        // Another fine-tune of the same base in the folder takes the same projector; a larger model takes its own.
        Assert.Equal(f16, ModelProjector.Find(Model("Defiant.Q8_0.gguf", 4096))!.Path);
        Assert.Equal("mmproj-large-BF16.gguf", ModelProjector.Find(Model("Large.Q4_K_M.gguf", 5120))!.Name);
        Assert.Null(ModelProjector.Find(Model("Gemma.Q6_K.gguf", 3584, "gemma2")));
        // A document search model is given no pictures, whatever lies beside it.
        Assert.Null(ModelProjector.Find(Model("Qwen3-Embedding-Q8_0.gguf", 4096)));
        Assert.Null(ModelProjector.Find(Path.Combine(_root, "gone.gguf")));

        // A file changed since it was read is read again.
        File.Delete(f16);
        Projector("Huihui.mmproj-f16.gguf", 5120);
        Assert.Equal(bf16, ModelProjector.Find(model)!.Path);
    }

    [Fact]
    public void Of_several_projectors_f16_then_bf16_then_q8_is_taken_and_never_f32()
    {
        Assert.Equal([0, 1, 2, 3, null], new[] { "mmproj-F16.gguf", "m.mmproj-bf16.gguf", "mmproj-model-Q8_0.gguf", "mmproj-Q6_K.gguf", "mmproj-F32.gguf" }.Select(Projectors.Rank));
        Assert.Equal("mmproj-F16.gguf", Projectors.Pick(["mmproj-F32.gguf", "mmproj-Q8_0.gguf", "mmproj-F16.gguf", "mmproj-BF16.gguf"], n => n));
        Assert.Equal("mmproj-BF16.gguf", Projectors.Pick(["mmproj-F32.gguf", "mmproj-Q8_0.gguf", "mmproj-BF16.gguf"], n => n));
        Assert.Null(Projectors.Pick(["mmproj-F32.gguf", "model-F16.gguf", "mmproj-F16.bin"], n => n));
        Assert.True(Projectors.IsProjectorName("Huihui-Qwen3.5-9B-abliterated.mmproj-f16.gguf"));
        Assert.False(Projectors.IsProjectorName("Huihui-Qwen3.5-9B-abliterated.Q8_0.gguf"));
        // Still hidden from the model lists, as before.
        Assert.True(Quantization.IsAddOn("mmproj-F16.gguf"));
    }

    // ---- What a projector costs ----

    [Fact]
    public void A_projector_counts_in_the_fit_with_its_file_size_and_a_quarter_gigabyte()
    {
        const long projector = 918_166_016;
        Assert.Equal(projector + Sizes.GiB / 4, Projectors.Need(projector));
        Assert.Equal(0, Projectors.Need(0));

        var info = new GgufInfo { Architecture = "qwen35", BlockCount = 32, EmbeddingLength = 4096, HeadCount = 16, HeadCountKv = 4, ContextLength = 262144 };
        var plain = info.ToShape(9_527_502_208);
        var seeing = info.ToShape(9_527_502_208, projector);
        var roomy = new FitBudget(22 * Sizes.GiB, 20 * Sizes.GiB, 1008, 60);

        var without = FitEstimator.Estimate(plain, 8192, roomy);
        var with = FitEstimator.Estimate(seeing, 8192, roomy);
        Assert.Equal(FitKind.Full, with.Kind);
        Assert.Equal(without.NeededBytes + Projectors.Need(projector), with.NeededBytes);

        // A card the model just fits on alone: with the projector part of the model goes to system memory, never the projector.
        var tight = roomy with { VramBytes = without.NeededBytes + 100 * 1024 * 1024 };
        Assert.Equal(FitKind.Full, FitEstimator.Estimate(plain, 8192, tight).Kind);
        var partly = FitEstimator.Estimate(seeing, 8192, tight);
        Assert.Equal(FitKind.Partial, partly.Kind);
        Assert.InRange(partly.GpuLayers, 20, 31);

        // The "Fits beside pictures" mark is judged with it: 1.1 GB less room for the picture.
        long card = without.NeededBytes + 9 * Sizes.GiB, picture = 8 * Sizes.GiB;
        Assert.True(BesidePictures.Fits(without, card, picture));
        Assert.False(BesidePictures.Fits(with, card, picture));
    }

    [Fact]
    public void Memory_measured_with_the_projector_is_never_used_for_a_load_without_it_nor_the_other_way_round()
    {
        var with = new ModelRecord { Path = "m.gguf", MeasuredVramBytes = 9_900_000_000, MeasuredVramContext = 8192, MeasuredVramProjector = true };
        Assert.Equal(9_900_000_000, with.MeasuredAt(8192, withProjector: true));
        Assert.Null(with.MeasuredAt(8192));
        Assert.Null(with.MeasuredAt(4096, withProjector: true));

        var without = new ModelRecord { Path = "m.gguf", MeasuredVramBytes = 8_800_000_000, MeasuredVramContext = 8192 };
        Assert.Equal(8_800_000_000, without.MeasuredAt(8192));
        Assert.Null(without.MeasuredAt(8192, withProjector: true));
    }

    // ---- LoRA file words (the owner's decision of 2026-11-15) ----

    [Fact]
    public async Task A_lora_without_a_card_is_drawn_with_its_files_words_cleaned_and_a_saved_card_as_it_is()
    {
        var catalog = new ImageModelCatalog(new MemoryDocumentStore());
        await catalog.LoadAsync();
        var facts = new LoraFacts(
            ["inkstyle", @"C:\Users\maker\train\10_inkstyle", "see https://example.invalid/lora", "ink wash", new string('x', 61), "<lora:other:1>", "inkstyle"],
            [], null, false);

        // What is shown is what is sent: no path, address, overlong word or markup from the file's maker.
        Assert.Equal(new LoraSettings("inkstyle, ink wash"), catalog.LoraToDraw("style/ink.safetensors", facts));
        Assert.Equal(string.Join(", ", LoraChips.FromFile(facts.Triggers).Words), catalog.LoraToDraw("style/ink.safetensors", facts).TriggerWords);
        Assert.Equal(new LoraSettings(""), catalog.LoraToDraw("style/ink.safetensors", null));
        // The card itself still reads the file as it is, for the place that shows how many words were left out.
        Assert.Contains("maker", catalog.LoraFor("style/ink.safetensors", facts).TriggerWords);

        // A card the owner saved is theirs, to the letter.
        await catalog.SaveLoraAsync("style/ink.safetensors", new LoraSettings("my words, a/b", 0.7));
        Assert.Equal(new LoraSettings("my words, a/b", 0.7), catalog.LoraToDraw("STYLE\\ink.safetensors", facts));

        // And it reaches the prompt that way.
        var request = ImagePrompt.Compose(CheckpointSettings.ForFamily(ImageFamily.Illustrious), "1girl", [("style/other.safetensors", catalog.LoraToDraw("style/other.safetensors", facts))]);
        Assert.Contains("inkstyle, ink wash, 1girl", request.Prompt);
        Assert.DoesNotContain("maker", request.Prompt);
        Assert.DoesNotContain("example.invalid", request.Prompt);
    }
}
