using System.Security.Cryptography;
using System.Text;
using DogAI.Core;
using DogAI.Core.Models;
using DogAI.Core.Net;
using DogAI.Data;
using DogAI.Engine;
using DogAI.Hub;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>
/// Picture input in the Models section (M17 step 3): which projector a repository offers with a model, the "Sees
/// pictures" mark from a file list, a download that brings the projector into the model's folder, which projector files
/// Delete and Remove picture input take, what a benchmark's layer setting is good for, and the fit of a model offered
/// with its projector.
/// </summary>
public sealed class PictureInputModelsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-input-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static RemoteFile F(string path, long size = 1000) => new(path, size, null);

    private static ModelVariant V(string path) => Quantization.Group([F(path)]).Single();

    // ---- In a repository ----

    /// <summary>The two repositories the plan names, as Hugging Face listed them on 2026-11-16.</summary>
    private static readonly RemoteFile[] Mradermacher =
    [
        F(".gitattributes"), F("README.md"),
        F("Huihui-Qwen3.5-9B-abliterated.Q4_K_M.gguf", 5_627_045_248),
        F("Huihui-Qwen3.5-9B-abliterated.Q8_0.gguf", 9_527_502_208),
        F("Huihui-Qwen3.5-9B-abliterated.f16.gguf", 17_920_697_728),
        F("Huihui-Qwen3.5-9B-abliterated.mmproj-Q8_0.gguf", 624_229_760),
        F("Huihui-Qwen3.5-9B-abliterated.mmproj-f16.gguf", 918_166_016),
    ];

    private static readonly RemoteFile[] Unsloth =
    [
        F("Qwen3.5-9B-Q4_K_M.gguf"), F("Qwen3.5-9B-Q8_0.gguf"),
        F("mmproj-BF16.gguf", 921_705_024), F("mmproj-F16.gguf", 918_166_080), F("mmproj-F32.gguf", 1_824_062_016),
    ];

    [Fact]
    public void A_repositorys_projector_is_offered_with_each_version_f16_first_and_never_f32()
    {
        // The projectors stay out of the versions offered (Quantization.IsAddOn) but come with each of them.
        Assert.Equal(3, Quantization.Group(Mradermacher).Count);
        foreach (var variant in Quantization.Group(Mradermacher))
            Assert.Equal("Huihui-Qwen3.5-9B-abliterated.mmproj-f16.gguf", Projectors.ForVariant(Mradermacher, variant)!.Path);
        Assert.Equal(918_166_016, Projectors.ForVariant(Mradermacher, V("Huihui-Qwen3.5-9B-abliterated.Q8_0.gguf"))!.Size);
        foreach (var variant in Quantization.Group(Unsloth))
            Assert.Equal("mmproj-F16.gguf", Projectors.ForVariant(Unsloth, variant)!.Path);

        // Without F16: BF16, then Q8_0; F32 alone is never offered.
        Assert.Equal("mmproj-BF16.gguf", Projectors.ForVariant([F("m-Q8_0.gguf"), F("mmproj-BF16.gguf"), F("mmproj-Q8_0.gguf"), F("mmproj-F32.gguf")], V("m-Q8_0.gguf"))!.Path);
        Assert.Equal("m.mmproj-Q8_0.gguf", Projectors.ForVariant([F("m.Q8_0.gguf"), F("m.mmproj-Q8_0.gguf"), F("m.mmproj-f32.gguf")], V("m.Q8_0.gguf"))!.Path);
        Assert.Null(Projectors.ForVariant([F("m.Q8_0.gguf"), F("m.mmproj-f32.gguf")], V("m.Q8_0.gguf")));
        Assert.Null(Projectors.ForVariant([F("m.Q8_0.gguf"), F("README.md")], V("m.Q8_0.gguf")));
    }

    [Fact]
    public void A_projector_named_for_the_model_comes_before_one_named_for_none_and_one_for_another_model_last()
    {
        RemoteFile[] files =
        [
            F("Small-2B.Q8_0.gguf"), F("Large-9B.Q8_0.gguf"),
            F("Small-2B.mmproj-f16.gguf"), F("Large-9B.mmproj-f16.gguf"), F("mmproj-F16.gguf"),
        ];
        Assert.Equal("Small-2B.mmproj-f16.gguf", Projectors.ForVariant(files, V("Small-2B.Q8_0.gguf"))!.Path);
        Assert.Equal("Large-9B.mmproj-f16.gguf", Projectors.ForVariant(files, V("Large-9B.Q8_0.gguf"))!.Path);
        Assert.Equal("mmproj-F16.gguf", Projectors.ForVariant(files, V("Other-4B.Q8_0.gguf"))!.Path);
        // A name that only starts like the model's is another model's ("Large-9B-v2" is not "Large-9B").
        Assert.Equal("Large-9B.mmproj-f16.gguf", Projectors.ForVariant([F("Large-9B.mmproj-f16.gguf")], V("Large-9B-v2.Q8_0.gguf"))!.Path);
        Assert.Equal("Large-9B-v2.mmproj-f16.gguf", Projectors.ForVariant([F("Large-9B.mmproj-f16.gguf"), F("Large-9B-v2.mmproj-f16.gguf")], V("Large-9B-v2.Q8_0.gguf"))!.Path);
    }

    [Fact]
    public void A_version_in_a_folder_of_the_repository_takes_the_projector_beside_it_else_the_one_at_the_top()
    {
        RemoteFile[] files =
        [
            F("Q8_0/model-Q8_0-00001-of-00002.gguf"), F("Q8_0/model-Q8_0-00002-of-00002.gguf"),
            F("BF16/model-BF16-00001-of-00002.gguf"), F("BF16/model-BF16-00002-of-00002.gguf"), F("BF16/mmproj-BF16.gguf"),
            F("mmproj-F16.gguf"),
        ];
        var variants = Quantization.Group(files);
        Assert.Equal("mmproj-F16.gguf", Projectors.ForVariant(files, variants.Single(v => v.Quant == "Q8_0"))!.Path);
        Assert.Equal("BF16/mmproj-BF16.gguf", Projectors.ForVariant(files, variants.Single(v => v.Quant == "BF16"))!.Path);
    }

    [Fact]
    public void The_sees_pictures_mark_on_a_search_result_comes_from_its_file_list()
    {
        Assert.True(Projectors.AnyIn(Mradermacher.Select(f => f.Path)));
        Assert.True(Projectors.AnyIn(["sub/mmproj-F16.gguf"]));
        Assert.False(Projectors.AnyIn(["m.Q8_0.gguf", "mmproj-F32.gguf", "mmproj.bin", "README.md"]));
        Assert.True(new HfModelSummary("a/b", 0, 0, null, false, "text-generation", Files: ["b.Q8_0.gguf", "b.mmproj-f16.gguf"]).SeesPictures);
        Assert.False(new HfModelSummary("a/b", 0, 0, null, false, "image-text-to-text", Files: ["b.Q8_0.gguf"]).SeesPictures);
        Assert.False(new HfModelSummary("a/b", 0, 0, null, false, null).SeesPictures);
    }

    [Fact]
    public async Task Search_results_carry_each_repositorys_file_names()
    {
        var server = new FileServer();
        server.Files["https://huggingface.co/api/models?filter=gguf&sort=likes&direction=-1&limit=40&expand[]=downloads&expand[]=likes&expand[]=lastModified&expand[]=gated&expand[]=pipeline_tag&expand[]=gguf&expand[]=tags&expand[]=siblings&search=qwen"] =
            Encoding.UTF8.GetBytes("""
                [{"id":"mradermacher/Huihui-Qwen3.5-9B-abliterated-GGUF","likes":3,"downloads":4,"siblings":[{"rfilename":".gitattributes"},{"rfilename":"Huihui-Qwen3.5-9B-abliterated.Q8_0.gguf"},{"rfilename":"Huihui-Qwen3.5-9B-abliterated.mmproj-f16.gguf"}]},
                 {"id":"acme/text-only-GGUF","likes":1,"downloads":2,"siblings":[{"rfilename":"t.Q8_0.gguf"}]},
                 {"id":"acme/unlisted-GGUF","likes":1,"downloads":2}]
                """);
        var results = await new HuggingFaceClient(new HttpClient(server), new HfToken()).SearchAsync("qwen", HfSort.Likes);
        Assert.Equal([true, false, false], results.Select(r => r.SeesPictures));
        Assert.Equal(3, results[0].Files!.Count);
        Assert.Null(results[2].Files);
    }

    // ---- Downloading with the model ----

    [Fact]
    public async Task A_model_downloaded_with_its_projector_lands_beside_it_in_one_job_whose_first_part_is_the_model()
    {
        var model = RandomNumberGenerator.GetBytes(300_000);
        var projector = RandomNumberGenerator.GetBytes(90_000);
        var server = new FileServer();
        var modelUrl = HuggingFaceClient.ResolveUrl("acme/m-GGUF", "m.Q8_0.gguf");
        var projectorUrl = HuggingFaceClient.ResolveUrl("acme/m-GGUF", "m.mmproj-f16.gguf");
        server.Files[modelUrl] = model;
        server.Files[projectorUrl] = projector;
        var gate = new OnlineGate();
        var session = new OnlineSession(gate);
        session.EnterModels();
        session.SetWanted(true);
        var store = new MemoryDocumentStore();
        var manager = new DownloadManager(new HttpClient(server), store, session, new HfToken(), _ => null);
        DownloadJob? completed = null;
        manager.Completed += j => completed = j;
        var folder = Path.Combine(_root, "m-GGUF");

        var job = await manager.EnqueueAsync(new DownloadJob
        {
            Repo = "acme/m-GGUF",
            Title = "m.Q8_0",
            Quant = "Q8_0",
            Folder = folder,
            WithProjector = true,
            Parts =
            [
                new DownloadPart { Url = modelUrl, FileName = "m.Q8_0.gguf", Size = model.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(model)) },
                new DownloadPart { Url = projectorUrl, FileName = "m.mmproj-f16.gguf", Size = projector.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(projector)) },
            ],
        });
        await manager.IdleAsync();

        Assert.Equal(DownloadStatus.Done, job.Status);
        Assert.Same(job, completed);
        Assert.Equal(Path.Combine(folder, "m.Q8_0.gguf"), job.MainPath); // the library record goes by it
        Assert.Equal(model, await File.ReadAllBytesAsync(Path.Combine(folder, "m.Q8_0.gguf")));
        Assert.Equal(projector, await File.ReadAllBytesAsync(Path.Combine(folder, "m.mmproj-f16.gguf")));
        Assert.Equal(1, job.ModelParts);
        Assert.Equal("with picture input", Projectors.WithInputParts(job.ModelParts));
        Assert.Equal("in 3 parts, with picture input", Projectors.WithInputParts(3));

        // The saved queue keeps it (a restart reads it back the same).
        var again = new DownloadManager(new HttpClient(server), store, session, new HfToken(), _ => null);
        await again.LoadAsync();
        Assert.True(Assert.Single(again.Jobs).WithProjector);
        manager.Dispose();
        again.Dispose();
    }

    [Fact]
    public void The_offer_says_how_much_the_picture_input_adds()
    {
        Assert.Equal("with picture input, + 0.9 GB", Projectors.WithInput(918_166_016));
        Assert.Equal("Picture input for Huihui", PictureInput.JobTitle("Huihui"));
    }

    // ---- On this PC: which projector files go ----

    private string Gguf(string name, params (string Key, object Value)[] values)
    {
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
        w.Write(new byte[64]);
        return path;
    }

    private string Model(string name, int embedding) =>
        Gguf(name, ("general.architecture", "qwen35"), ("general.name", Path.GetFileNameWithoutExtension(name)), ("qwen35.embedding_length", embedding), ("qwen35.block_count", 32));

    private string Projector(string name, int dim) =>
        Gguf(name, ("general.architecture", "clip"), ("general.type", "mmproj"), ("clip.has_vision_encoder", true), ("clip.vision.projection_dim", dim));

    [Fact]
    public void Delete_takes_the_projector_only_when_no_other_model_in_the_folder_uses_it()
    {
        var huihui = Model(Path.Combine("a", "Huihui.Q8_0.gguf"), 4096);
        var f16 = Projector(Path.Combine("a", "Huihui.mmproj-f16.gguf"), 4096);
        var large = Model(Path.Combine("a", "Large.Q4_K_M.gguf"), 5120);   // another family: not a user of it

        var alone = ModelProjector.WithModel(huihui);
        Assert.Equal([f16], alone.Delete.Select(p => p.Path));
        Assert.Empty(alone.Keep);
        Assert.Empty(ModelProjector.WithModel(large).Delete);

        // Defiant Fable is another fine-tune of the same base model: the projector belongs to it too, so it stays.
        var defiant = Model(Path.Combine("a", "Defiant.Q8_0.gguf"), 4096);
        var shared = ModelProjector.WithModel(huihui);
        Assert.Empty(shared.Delete);
        Assert.Equal([f16], shared.Keep.Select(p => p.Path));
        Assert.Equal([defiant], ModelProjector.AlsoUsedBy(huihui, [f16]).Select(m => m.Path));

        // A model in another folder, even one below, does not count: the projector is looked for in a model's own folder.
        var below = Model(Path.Combine("a", "deeper", "Other.Q8_0.gguf"), 4096);
        Assert.DoesNotContain(below, ModelProjector.AlsoUsedBy(defiant, [f16]).Select(m => m.Path));
        Assert.Equal([huihui], ModelProjector.AlsoUsedBy(defiant, [f16]).Select(m => m.Path));
    }

    [Fact]
    public void Remove_picture_input_takes_every_projector_that_belongs_or_the_next_one_would_be_picked()
    {
        var model = Model(Path.Combine("b", "Huihui.Q8_0.gguf"), 4096);
        var f16 = Projector(Path.Combine("b", "Huihui.mmproj-f16.gguf"), 4096);
        var q8 = Projector(Path.Combine("b", "Huihui.mmproj-Q8_0.gguf"), 4096);
        var f32 = Projector(Path.Combine("b", "Huihui.mmproj-f32.gguf"), 4096);
        var other = Projector(Path.Combine("b", "mmproj-large-F16.gguf"), 5120);

        Assert.Equal(f16, ModelProjector.Find(model)!.Path);
        var belonging = ModelProjector.Belonging(model).Select(p => p.Path).Order().ToList();
        Assert.Equal(new[] { f16, f32, q8 }.Order().ToList(), belonging);
        Assert.DoesNotContain(other, belonging);
        File.Delete(f16);
        Assert.Equal(q8, ModelProjector.Find(model)!.Path); // removing only the picked one would not switch it off
        foreach (var path in belonging.Where(File.Exists)) File.Delete(path);
        Assert.Null(ModelProjector.Find(model));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void A_projectors_header_on_hugging_face_is_checked_against_the_model_before_get_picture_input()
    {
        var info = GgufReader.TryRead(Projector("p.mmproj-f16.gguf", 4096));
        Assert.True(ModelProjector.Fits(info, 4096));
        Assert.False(ModelProjector.Fits(info, 5120));
        Assert.False(ModelProjector.Fits(info, null));
        Assert.False(ModelProjector.Fits(GgufReader.TryRead(Model("m.Q8_0.gguf", 4096)), 4096));
        Assert.False(ModelProjector.Fits(null, 4096));
    }

    // ---- Fit and the benchmark ----

    [Fact]
    public void A_benchmarks_layer_setting_is_used_only_for_a_load_with_the_projector_as_it_was_measured()
    {
        var budget = new FitBudget(23 * Sizes.GiB, 20 * Sizes.GiB, 1008, 60);
        var plan = new FitResult(FitKind.Full, 33, 33, 10 * Sizes.GiB, 80);
        var without = new ModelRecord { Path = "m.gguf", TunedGpuLayers = 30, TunedContext = 8192, TunedWithProjector = false };
        Assert.Equal(30, LoadDecision.Decide("m", 8192, plan, budget, without).GpuLayers);
        Assert.Equal(-1, LoadDecision.Decide("m", 8192, plan, budget, without, withProjector: true).GpuLayers);
        var with = new ModelRecord { Path = "m.gguf", TunedGpuLayers = 28, TunedContext = 8192, TunedWithProjector = true };
        Assert.Equal(28, LoadDecision.Decide("m", 8192, plan, budget, with, withProjector: true).GpuLayers);
        Assert.Equal(-1, LoadDecision.Decide("m", 8192, plan, budget, with).GpuLayers);
        Assert.Contains("without picture input", PictureInput.BenchmarkStale(seesNow: true));
        Assert.Contains("with picture input", PictureInput.BenchmarkStale(seesNow: false));

        // The row says so when the setting is not used now, except after a projector went from a model the benchmark put whole on the card.
        Assert.True(PictureInput.BenchmarkUnused(without, seesNow: true));
        Assert.False(PictureInput.BenchmarkUnused(without, seesNow: false));
        Assert.True(PictureInput.BenchmarkUnused(with, seesNow: false));
        var whole = new ModelRecord { Path = "m.gguf", TunedGpuLayers = -1, TunedContext = 8192, TunedWithProjector = true };
        Assert.False(PictureInput.BenchmarkUnused(whole, seesNow: false));
        whole.TunedWithProjector = false;
        Assert.True(PictureInput.BenchmarkUnused(whole, seesNow: true));
        Assert.False(PictureInput.BenchmarkUnused(new ModelRecord { Path = "m.gguf" }, seesNow: true));
        Assert.False(PictureInput.BenchmarkUnused(null, seesNow: true));
    }

    [Fact]
    public void A_model_offered_with_its_projector_is_judged_with_it_on_the_download_lists()
    {
        var pick = new CuratedPick("general", "mradermacher/Huihui-Qwen3.5-9B-abliterated-GGUF", "Qwen3.5 9B abliterated", 9.0, "");
        var budget = new FitBudget(23 * Sizes.GiB, 20 * Sizes.GiB, 1008, 60);
        var without = FitEstimator.Estimate(pick.EstimatedShape(9_527_502_208), 8192, budget);
        var with = FitEstimator.Estimate(pick.EstimatedShape(9_527_502_208, 918_166_016), 8192, budget);
        Assert.Equal(without.NeededBytes + Projectors.Need(918_166_016), with.NeededBytes);

        // "Fits beside pictures" is judged with it too: a model just inside the line without its projector is outside with it.
        var picture = 9 * Sizes.GiB;
        var card = without.NeededBytes + picture + DogAI.Core.Images.PictureMemory.Margin;
        Assert.True(BesidePictures.Fits(without, card, picture));
        Assert.False(BesidePictures.Fits(with, card, picture));
    }

    [Fact]
    public async Task The_benchmarks_projector_flag_is_kept_in_the_models_record()
    {
        var root = Path.Combine(_root, "data");
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
        try
        {
            await services.MigrateDogDatabaseAsync();
            var store = services.GetRequiredService<IModelRecordStore>();
            await store.SaveAsync(new ModelRecord { Path = @"Z:\models\q.gguf", Repo = "acme/q-GGUF", TunedGpuLayers = 30, TunedContext = 8192 });
            Assert.False((await store.FindAsync(@"Z:\models\q.gguf"))!.TunedWithProjector); // rows from before were measured without one
            var record = (await store.FindAsync(@"Z:\models\q.gguf"))!;
            record.TunedWithProjector = true;
            await store.SaveAsync(record);
            Assert.True((await store.FindAsync(@"Z:\models\q.gguf"))!.TunedWithProjector);
            Assert.Equal("acme/q-GGUF", (await store.FindAsync(@"Z:\models\q.gguf"))!.Repo);
        }
        finally
        {
            await services.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void The_wording_names_every_file_and_every_model_that_loses_it()
    {
        var remove = PictureInput.RemoveText("Huihui", [("Huihui.mmproj-f16.gguf", 918_166_016), ("Huihui.mmproj-Q8_0.gguf", 624_229_760)], @"D:\m");
        Assert.Contains("Huihui.mmproj-f16.gguf (0.9 GB) and Huihui.mmproj-Q8_0.gguf (0.6 GB)", remove);
        Assert.Contains("permanently", remove);
        Assert.Equal("Defiant in the same folder uses the same file and cannot see pictures afterwards either.", PictureInput.AlsoLoses(["Defiant"]));
        Assert.StartsWith("A and B in the same folder use", PictureInput.AlsoLoses(["A", "B"]));
        Assert.Contains("Defiant in the same folder uses it too", PictureInput.KeptOnDelete(["Huihui.mmproj-f16.gguf"], ["Defiant"]));
        Assert.Contains(Projectors.Label, PictureInput.GeneralCannotSee("Gemma"));
    }
}
