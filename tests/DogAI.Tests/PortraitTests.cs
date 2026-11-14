using System.Runtime.CompilerServices;
using DogAI.Core;
using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>M9 step 7: the rating, Settings → Pictures, and the picture maker's tries (draw, try again, edit and redraw, keep, take back).</summary>
public sealed class PortraitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dogai-portraits-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly CheckpointSettings Illustrious = CheckpointSettings.ForFamily(ImageFamily.Illustrious);

    // The family's own rating words, so no test spells one out.
    private static readonly RatingWords Words = RatingWords.ForFamily(ImageFamily.Illustrious);
    private static string First(string words) => PictureRatings.Pieces(words)[0];
    private static string Above(PictureRating rating) => string.Join(", ", Words.Above(rating));

    // ---- Rating ----

    [Fact]
    public void Each_family_has_its_own_rating_words()
    {
        Assert.Equal("safe", RatingWords.ForFamily(ImageFamily.NoobAI).General);
        Assert.Contains(RatingWords.ForFamily(ImageFamily.NoobAI).Questionable, RatingWords.ForFamily(ImageFamily.NoobAI).Above(PictureRating.General));
        Assert.Equal("rating_safe", RatingWords.ForFamily(ImageFamily.Pony).General);
        Assert.Equal("", RatingWords.ForFamily(ImageFamily.Pony).Sensitive);
        Assert.Equal("general", RatingWords.ForFamily(ImageFamily.Illustrious).General);
        Assert.Equal(RatingWords.None, RatingWords.ForFamily(ImageFamily.Sdxl));
        // Every family's starting card uses its family's words.
        foreach (var family in Enum.GetValues<ImageFamily>())
            Assert.Equal(RatingWords.ForFamily(family), CheckpointSettings.ForFamily(family).RatingsOrDefault());
    }

    [Fact]
    public void The_rating_word_follows_the_quality_words_and_higher_ratings_go_into_the_negative()
    {
        var request = ImagePrompt.Compose(Illustrious, "1girl, forest", rating: PictureRating.General);
        Assert.StartsWith(Illustrious.QualityBefore + ", general, 1girl, forest", request.Prompt);
        Assert.StartsWith(Illustrious.Negative, request.Negative());
        // Above General: the words of the three higher levels, a word two of them share only once.
        Assert.EndsWith(", " + Above(PictureRating.General), request.NegativePrompt);
        Assert.Equal(4, Words.Above(PictureRating.General).Count);

        var sensitive = ImagePrompt.Compose(Illustrious, "1girl", rating: PictureRating.Sensitive);
        Assert.Contains(", sensitive, 1girl", sensitive.Prompt);
        Assert.EndsWith(", " + Above(PictureRating.Sensitive), sensitive.NegativePrompt);

        // At the top nothing is above, so the negative prompt is the card's own (the owner's decision, 2026-11-14: no
        // words are added to it for the higher ratings).
        var top = ImagePrompt.Compose(Illustrious, "1girl", rating: PictureRating.Explicit);
        Assert.Contains(Words.Explicit + ", 1girl", top.Prompt);
        Assert.Equal(Illustrious.Negative, top.NegativePrompt);
        // The third level shares a word with the highest: only the highest level's other word is above it.
        // The shared word is asked for, so it must not also be asked against.
        var questionable = ImagePrompt.Compose(Illustrious, "1girl", rating: PictureRating.Questionable);
        Assert.Contains(Words.Questionable + ", 1girl", questionable.Prompt);
        Assert.Equal(Illustrious.Negative + ", " + First(Words.Explicit), questionable.NegativePrompt);
        var noob = CheckpointSettings.ForFamily(ImageFamily.NoobAI);
        Assert.Equal(noob.Negative + ", " + First(noob.RatingsOrDefault().Explicit), ImagePrompt.Compose(noob, "1girl", rating: PictureRating.Questionable).NegativePrompt);

        // Pony's middle level has no word: nothing is added to the prompt.
        var pony = CheckpointSettings.ForFamily(ImageFamily.Pony);
        var ponyPrompt = ImagePrompt.Compose(pony, "1girl", rating: PictureRating.Sensitive);
        Assert.Equal(pony.QualityBefore + ", 1girl", ponyPrompt.Prompt);
        Assert.Equal(string.Join(", ", pony.RatingsOrDefault().Above(PictureRating.Sensitive)), ponyPrompt.NegativePrompt);

        // Without a rating nothing changes (the test picture of step 4 and earlier tests).
        var none = ImagePrompt.Compose(Illustrious, "1girl");
        Assert.Equal(Illustrious.Negative, none.NegativePrompt);
        Assert.DoesNotContain("general", none.Prompt);
    }

    [Fact]
    public void A_word_already_in_the_negative_is_not_repeated()
    {
        // A rating word the card already has in its negative prompt, in capitals.
        var shared = PictureRatings.Pieces(Words.Questionable)[1];
        var card = Illustrious with { Negative = shared.ToUpperInvariant() + ", lowres" };
        var request = ImagePrompt.Compose(card, "1girl", rating: PictureRating.General);
        Assert.Equal(shared.ToUpperInvariant() + ", lowres, " + string.Join(", ", Words.Above(PictureRating.General).Where(w => w != shared)), request.NegativePrompt);
    }

    [Fact]
    public void Assemble_adds_the_rating_and_the_owners_own_words_on_the_card()
    {
        var card = Illustrious with { Ratings = new RatingWords("very safe", "", "", "top word") };
        var scene = new PictureScene("Mara smiles.", [new ScenePerson("Mara", "girl", [], [], [], [])], [], [], ["portrait"]);
        var planned = PicturePrompt.Assemble(scene, new PictureOrder(card, [new PersonLooks("Mara", new Looks("1girl, silver hair"))], Rating: PictureRating.General));
        Assert.StartsWith(card.QualityBefore + ", very safe, 1girl, solo, silver hair", planned.Request.Prompt);
        Assert.EndsWith(", top word", planned.Request.NegativePrompt);
        // The rating words are never put through the tag check (they are not in the tag list on purpose).
        Assert.Empty(planned.Notes.Dropped);
    }

    [Fact]
    public void The_planner_is_told_the_rating()
    {
        var general = PictureWriter.SceneInstruction(PromptStyle.Tags, [new CastEntry("Mara", true)], 1, portrait: true);
        Assert.Contains(PictureRatings.ForPlanner(PictureRating.General), general);
        Assert.Contains("fully clothed", general);
        var highest = PictureWriter.SceneInstruction(PromptStyle.Tags, [new CastEntry("Mara", true)], 2, portrait: false, PictureRating.Explicit);
        Assert.Contains("Everyone in it is an adult.", highest);
        Assert.DoesNotContain("fully clothed", highest);
    }

    [Fact]
    public async Task The_portrait_job_passes_the_rating_on()
    {
        var engine = new OneAnswer("""{"moment":"Mara on the beach.","people":[{"name":"Mara","kind":"girl","looks":[],"clothing":["swimsuit"],"expression":["smile"],"pose":["standing"]}],"place":["beach"],"light":["sunlight"],"framing":"cowboy shot","view":"straight on"}""");
        var scene = await new PictureWriter(engine).PortraitAsync("Mara", "A scout.", hasLooks: true, PromptStyle.Tags, PictureRating.Sensitive);
        Assert.Equal("Mara on the beach.", scene.Moment);
        Assert.Contains(PictureRatings.ForPlanner(PictureRating.Sensitive), engine.System);
    }

    [Fact]
    public void Rating_words_are_cleaned_when_a_card_is_saved()
    {
        var words = new RatingWords(" general \n", "a,  b ,", new string('x', 300), "");
        var clean = words.Clean();
        Assert.Equal("general", clean.General);
        Assert.Equal("a, b", clean.Sensitive);
        Assert.Equal(200, clean.Questionable.Length);
    }

    [Fact]
    public async Task Rating_words_and_preferences_are_kept_and_older_documents_still_read()
    {
        var paths = new AppPaths(Path.Combine(_dir, "data"));
        paths.EnsureCreated();
        using var services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
        await services.MigrateDogDatabaseAsync();
        var store = services.GetRequiredService<IDocumentStore>();

        // A document as step 6 saved it: a changed card with no rating words, no preferences.
        await store.SaveAsync(ImageModelCatalog.Key, new Step6Document
        {
            Checkpoints = { ["il.safetensors"] = Step6Card.From(Illustrious with { Steps = 30 }) },
            Chosen = "il.safetensors",
        });
        var catalog = new ImageModelCatalog(store);
        await catalog.LoadAsync();
        var verdict = new ImageFileVerdict(ImageFileKind.Checkpoint, "", Family: ImageFamily.Illustrious);
        var old = catalog.CheckpointFor("il.safetensors", verdict);
        Assert.Equal(30, old.Steps);
        Assert.Null(old.Ratings);
        Assert.Equal(RatingWords.ForFamily(ImageFamily.Illustrious), old.RatingsOrDefault());
        Assert.Equal(PicturePreferences.Default, catalog.Preferences);

        await catalog.SaveCheckpointAsync("il.safetensors", old with { Ratings = new RatingWords("safe ", "", "", "") });
        await catalog.SetPreferencesAsync(new PicturePreferences(PictureQuality.Draft, ShowPromptFirst: true, PictureRating.Sensitive));

        var again = new ImageModelCatalog(store);
        await again.LoadAsync();
        Assert.Equal(new RatingWords("safe"), again.CheckpointFor("il.safetensors", verdict).Ratings);
        Assert.Equal(new PicturePreferences(PictureQuality.Draft, true, PictureRating.Sensitive), again.Preferences);
        Assert.Equal("il.safetensors", again.Chosen);
        await Assert.ThrowsAsync<InvalidOperationException>(() => again.SetPreferencesAsync(new PicturePreferences((PictureQuality)9)));
    }

    /// <summary>The settings document exactly as M9 step 6 wrote it, to check that older data still reads.</summary>
    private sealed class Step6Document
    {
        public Dictionary<string, Step6Card> Checkpoints { get; set; } = [];
        public Dictionary<string, LoraSettings> Loras { get; set; } = [];
        public Dictionary<string, List<string>> StyleLoras { get; set; } = [];
        public string? Chosen { get; set; }
    }

    private sealed record Step6Card(ImageFamily Family, PromptStyle Style, string QualityBefore, string QualityAfter, string Negative,
        string Sampler, string? Scheduler, int Steps, double Guidance, int? ClipSkip, ImagePrediction Prediction, string? Vae)
    {
        public static Step6Card From(CheckpointSettings s) => new(s.Family, s.Style, s.QualityBefore, s.QualityAfter, s.Negative, s.Sampler,
            s.Scheduler, s.Steps, s.Guidance, s.ClipSkip, s.Prediction, s.Vae);
    }

    // ---- The picture maker's tries ----

    private const string Root = @"C:\models\image";

    private static ImageRequest Base(long seed = -1) => ImagePrompt.Compose(Illustrious, "1girl, silver hair", seed: seed, rating: PictureRating.General);

    [Fact]
    public async Task A_try_is_drawn_as_kept_pictures_are_and_remembers_its_seed()
    {
        var engine = new WebpEngine();
        var tries = new PictureTries(engine);
        var progress = new Collect<ImageProgress>();
        var first = await tries.DrawAsync(Root, "il.safetensors", Illustrious, Base(), PictureQuality.Draft, PictureShape.Tall, "Mara.", TagNotes.None, progress);

        var sent = engine.Requests.Single();
        Assert.Equal(ImageFormat.Webp, sent.Format);
        Assert.Equal(PictureFormat.Quality, sent.Quality);
        Assert.True(sent.Preview);
        Assert.Equal(PictureLevels.DraftSteps(Illustrious.Steps), sent.Steps);
        Assert.Equal((832, 1216), (sent.Width, sent.Height));
        Assert.Equal(Path.Combine(Root, "checkpoints", "il.safetensors"), engine.Model!.CheckpointPath);
        Assert.NotEmpty(progress.Items);

        // The base stays at the checkpoint's own steps, with the seed the engine used.
        Assert.Equal(Illustrious.Steps, first.Base.Steps);
        Assert.Equal(engine.LastSeed, first.Base.Seed);
        Assert.Equal((832, 1216), (first.ImageWidth, first.ImageHeight));
        Assert.Equal(1, first.Number);
        Assert.Equal("Mara.", first.Moment);
    }

    [Fact]
    public async Task Try_again_takes_a_new_seed_and_redraw_keeps_it()
    {
        var engine = new WebpEngine();
        var tries = new PictureTries(engine);
        var first = await tries.DrawAsync(Root, "il.safetensors", Illustrious, Base(), PictureQuality.Draft, PictureShape.Tall, "Mara.", TagNotes.None);
        var second = await tries.TryAgainAsync(Root, first);
        Assert.NotEqual(first.Base.Seed, second.Base.Seed);
        Assert.Equal(-1, engine.Requests[1].Seed); // a new seed was asked for
        Assert.Equal(first.Base.Prompt, second.Base.Prompt);
        Assert.Equal("Mara.", second.Moment);

        // Draft → Fine with the same seed: the checkpoint's own steps plus the hires fix, not Draft's steps refined.
        var fine = await tries.RedrawAsync(Root, first, first.Base.Prompt, PictureQuality.Fine, PictureShape.Tall);
        var sent = engine.Requests[2];
        Assert.Equal(first.Base.Seed, sent.Seed);
        Assert.Equal(Illustrious.Steps, sent.Steps);
        Assert.NotNull(sent.Hires);
        Assert.Equal("Mara.", fine.Moment);

        // Fine → Draft from the Fine try: Draft's own steps, no hires fix.
        await tries.RedrawAsync(Root, fine, fine.Base.Prompt, PictureQuality.Draft, PictureShape.Square);
        Assert.Equal(PictureLevels.DraftSteps(Illustrious.Steps), engine.Requests[3].Steps);
        Assert.Null(engine.Requests[3].Hires);
        Assert.Equal((1024, 1024), (engine.Requests[3].Width, engine.Requests[3].Height));

        // A changed prompt keeps the seed and everything else; the plan's moment no longer describes it.
        var edited = await tries.RedrawAsync(Root, first, first.Base.Prompt + ",\n red dress", PictureQuality.Draft, PictureShape.Tall);
        Assert.Equal(first.Base.Seed, edited.Base.Seed);
        Assert.EndsWith("red dress", engine.Requests[4].Prompt);
        Assert.DoesNotContain("\n", engine.Requests[4].Prompt);
        Assert.Equal(first.Base.NegativePrompt, engine.Requests[4].NegativePrompt);
        Assert.Null(edited.Moment);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tries.RedrawAsync(Root, first, "  ", PictureQuality.Draft, PictureShape.Tall));
    }

    [Fact]
    public async Task Redraw_can_leave_a_lora_out_and_keep_the_seed()
    {
        var engine = new WebpEngine();
        var tries = new PictureTries(engine);
        var withLora = ImagePrompt.Compose(Illustrious, "1girl", [("Maid_marian.safetensors", new LoraSettings("maid_marian", 0.8))]);
        var first = await tries.DrawAsync(Root, "il.safetensors", Illustrious, withLora, PictureQuality.Draft, PictureShape.Tall, null, TagNotes.None);
        Assert.Single(engine.Requests[0].Loras!);

        var without = await tries.RedrawAsync(Root, first, first.Base.Prompt.Replace("maid_marian, ", ""), PictureQuality.Draft, PictureShape.Tall, []);
        Assert.Empty(engine.Requests[1].Loras!);
        Assert.DoesNotContain("maid_marian", engine.Requests[1].Prompt);
        Assert.Equal(first.Base.Seed, without.Base.Seed);

        // Null keeps the LoRAs as they were; a strength can change.
        await tries.RedrawAsync(Root, first, first.Base.Prompt, PictureQuality.Draft, PictureShape.Tall);
        Assert.Equal(0.8, engine.Requests[2].Loras!.Single().Strength);
        await tries.RedrawAsync(Root, first, first.Base.Prompt, PictureQuality.Draft, PictureShape.Tall, [new LoraUse("Maid_marian.safetensors", 0.4)]);
        Assert.Equal(0.4, engine.Requests[3].Loras!.Single().Strength);
    }

    [Fact]
    public async Task Only_so_many_tries_are_held_and_a_kept_one_is_never_let_go()
    {
        var tries = new PictureTries(new WebpEngine());
        var kept = new Picture { Id = 7, Checkpoint = "il.safetensors", Prompt = "1girl", Width = 832, Height = 1216, Seed = 5, Steps = 28, Sampler = "euler", Image = KeptPictureTests.Webp(832, 1216) };
        tries.Adopt(kept, Illustrious);
        for (var i = 0; i < PictureTries.Max + 3; i++)
            await tries.DrawAsync(Root, "il.safetensors", Illustrious, Base(), PictureQuality.Draft, PictureShape.Tall, null, TagNotes.None);
        Assert.Equal(PictureTries.Max, tries.All.Count);
        Assert.True(tries.Dropped);
        Assert.Equal(7, tries.All[0].KeptId);
        Assert.Equal(PictureTries.Max + 4, tries.All[^1].Number);
    }

    [Theory]
    [InlineData(20, false, PictureQuality.Draft, 28)]
    [InlineData(28, false, PictureQuality.Standard, 28)]
    [InlineData(28, true, PictureQuality.Fine, 28)]
    [InlineData(25, false, PictureQuality.Standard, 25)] // a count the card does not have now: drawn again as it was
    public void A_kept_picture_tells_its_quality(int steps, bool hires, PictureQuality quality, int baseSteps)
    {
        var kept = new Picture { Checkpoint = "il.safetensors", Prompt = "1girl", Width = 832, Height = 1216, Seed = 5, Steps = steps, Sampler = "euler",
            Hires = hires ? PictureLevels.FineHires(28) : null };
        var (b, q) = PictureTries.BaseOf(kept, Illustrious.Steps);
        Assert.Equal(quality, q);
        Assert.Equal(baseSteps, b.Steps);
        Assert.Null(b.Hires);
        // Levelled again, it is exactly the request the picture was drawn with.
        Assert.Equal(kept.ToRequest() with { Loras = null }, PictureTries.Levelled(b, q, PictureShape.Tall) with { Loras = null });
    }

    [Fact]
    public void Shapes_are_told_from_sizes()
    {
        Assert.Equal(PictureShape.Tall, PictureTries.ShapeOf(832, 1216));
        Assert.Equal(PictureShape.Square, PictureTries.ShapeOf(1024, 1024));
        Assert.Equal(PictureShape.Wide, PictureTries.ShapeOf(1216, 832));
        Assert.Equal(PictureShape.Wide, PictureTries.ShapeOf(1344, 768));
    }

    [Fact]
    public async Task A_kept_try_comes_back_from_the_store_and_redraws_the_same_way()
    {
        var paths = new AppPaths(Path.Combine(_dir, "data"));
        paths.EnsureCreated();
        using var services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
        await services.MigrateDogDatabaseAsync();
        var store = services.GetRequiredService<IPictureStore>();

        var engine = new WebpEngine();
        var tries = new PictureTries(engine);
        var card = Illustrious with { Vae = "fix.safetensors" };
        var loras = new List<(string, LoraSettings)> { ("Maid_marian.safetensors", new LoraSettings("maid_marian", 0.8)) };
        var @base = ImagePrompt.Compose(card, "1girl", loras, rating: PictureRating.General);
        var drawn = await tries.DrawAsync(Root, "il.safetensors", card, @base, PictureQuality.Draft, PictureShape.Square, "Mara.", TagNotes.None);

        var kept = await store.AddAsync(drawn.ToKeep(PictureOwners.Character, 42));
        Assert.Equal("fix.safetensors", kept.Vae);
        Assert.Equal(PictureLevels.DraftSteps(card.Steps), kept.Steps);

        // Taken back in a new picture maker with the card as it is now (without the VAE): the picture's own VAE is kept.
        var later = new PictureTries(engine);
        var back = later.Adopt((await store.GetAsync(kept.Id))!, Illustrious);
        Assert.Equal(kept.Id, back.KeptId);
        Assert.Equal(PictureQuality.Draft, back.Quality);
        Assert.Equal(PictureShape.Square, back.Shape);
        Assert.Equal("fix.safetensors", back.Settings.Vae);
        Assert.Equal(drawn.Request with { Loras = null }, back.Request with { Loras = null });
        Assert.Equal(drawn.Request.Loras, back.Request.Loras);
        Assert.Equal(drawn.Image, back.Image);

        // Redrawn at Standard: same seed, the checkpoint's own steps, the picture's VAE.
        await later.RedrawAsync(Root, back, back.Base.Prompt, PictureQuality.Standard, PictureShape.Square);
        Assert.Equal(kept.Seed, engine.Requests[^1].Seed);
        Assert.Equal(card.Steps, engine.Requests[^1].Steps);
        Assert.Equal(Path.Combine(Root, "vae", "fix.safetensors"), engine.Model!.VaePath);
    }

    [Fact]
    public async Task An_owner_keeps_one_picture_and_a_failed_keep_leaves_the_old_one()
    {
        var paths = new AppPaths(Path.Combine(_dir, "data"));
        paths.EnsureCreated();
        using var services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
        await services.MigrateDogDatabaseAsync();
        var store = services.GetRequiredService<IPictureStore>();
        var tries = new PictureTries(new WebpEngine());
        PictureTry Draw() => tries.DrawAsync(Root, "il.safetensors", Illustrious, Base(), PictureQuality.Draft, PictureShape.Tall, null, TagNotes.None).Result;
        async Task<List<long>> Kept(long owner) => (await store.ListAsync(PictureOwners.Character, owner)).Select(p => p.Id).ToList();

        // First portrait; another owner's picture is never touched.
        var other = await store.AddAsync(Draw().ToKeep(PictureOwners.Character, 9));
        var first = await Portraits.KeepAsync(store, PictureOwners.Character, 1, Draw());
        Assert.Equal([first!.Value], await Kept(1));

        // Replaced: only the new one is left.
        var second = await Portraits.KeepAsync(store, PictureOwners.Character, 1, Draw());
        Assert.NotEqual(first, second);
        Assert.Equal([second!.Value], await Kept(1));

        // The kept picture chosen again: nothing written, nothing removed.
        var again = tries.Adopt((await store.GetAsync(second.Value))!, Illustrious);
        Assert.Equal(second, await Portraits.KeepAsync(store, PictureOwners.Character, 1, again));
        Assert.Equal([second.Value], await Kept(1));

        // A picture that cannot be kept (not WebP): the old one stays.
        var broken = Draw() with { Image = [1, 2, 3] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Portraits.KeepAsync(store, PictureOwners.Character, 1, broken));
        Assert.Equal([second.Value], await Kept(1));

        // Removed (or replaced by a picture from a file): none kept.
        Assert.Null(await Portraits.KeepAsync(store, PictureOwners.Character, 1, null));
        Assert.Empty(await Kept(1));
        Assert.Equal([other.Id], await Kept(9));
    }

    /// <summary>A picture engine that answers with a WebP of the size asked for (the store and <see cref="WebpInfo"/> read its header).</summary>
    internal sealed class WebpEngine : IImageEngine
    {
        public ImageModel? Model { get; private set; }
        public bool IsBusy => false;
        public bool IsRunning => Model is not null;
        public string? Error => null;
        public event Action? Changed { add { } remove { } }
        public List<ImageRequest> Requests { get; } = [];
        public long LastSeed { get; private set; }
        private long _next = 100;

        public Task LoadAsync(ImageModel model, CancellationToken ct = default) { Model = model; return Task.CompletedTask; }
        public Task UnloadAsync(CancellationToken ct = default) { Model = null; return Task.CompletedTask; }
        public Task ReleaseAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<ImageResult> DrawAsync(ImageRequest request, IProgress<ImageProgress>? progress = null, CancellationToken ct = default)
        {
            if (Model is null) throw new ImageEngineException("No picture model is loaded.");
            Assert.Empty(request.Problems());
            Requests.Add(request);
            progress?.Report(new ImageProgress("sampling", 1, request.Steps));
            LastSeed = request.Seed >= 0 ? request.Seed : _next++;
            var scale = request.Hires?.Scale ?? 1;
            var image = KeptPictureTests.Webp((int)(request.Width * scale), (int)(request.Height * scale), 200, (int)LastSeed);
            return Task.FromResult(new ImageResult([image], request.Format, LastSeed));
        }
    }

    private sealed class OneAnswer(string answer) : IChatEngine
    {
        public bool IsReady => true;
        public string? ModelName => "fake";
        public int ContextLength => 8192;
        public string System { get; private set; } = "";

        public Task<int> CountTokensAsync(string text, CancellationToken ct = default) => Task.FromResult(text.Length / 4);
        public Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct = default) => Task.FromResult("");

        public IAsyncEnumerable<ChatDelta> StreamChatAsync(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, CancellationToken ct = default)
        {
            System = messages[0].Content;
            return Stream(ct);
        }

        public IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, SamplerPreset sampler, CancellationToken ct = default) => Stream(ct);

        private async IAsyncEnumerable<ChatDelta> Stream([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return new ChatDelta(answer, null, null);
        }
    }
}

internal static class RequestTestExtensions
{
    public static string Negative(this ImageRequest r) => r.NegativePrompt;
}
