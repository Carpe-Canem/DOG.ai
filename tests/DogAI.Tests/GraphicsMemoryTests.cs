using DogAI.Core;
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Core.Settings;
using DogAI.Data;
using DogAI.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>Something else on the card: the document search model or a speech model, as the coordinator sees it.</summary>
internal sealed class FakeGuest(string name, long bytes) : ICardGuest
{
    public string Name => name;
    public long Bytes => bytes;
    public bool HoldsMemory { get; set; } = true;
    public bool IsBusy { get; set; }
    public int LetGo { get; private set; }
    public List<string>? Events { get; init; }

    public Task<bool> LetGoAsync()
    {
        if (IsBusy) return Task.FromResult(false);
        HoldsMemory = false;
        LetGo++;
        if (Events is { } events) lock (events) events.Add("let-go");
        return Task.FromResult(true);
    }
}

/// <summary>
/// M15: Unload everything, the document search model making way, downloading a model that is too large, and the
/// "Fits beside pictures" mark.
/// </summary>
public sealed class GraphicsMemoryTests
{
    private const long Gb = FakeCard.Gb;
    private static readonly ImageModel Model = new(@"D:\m\c.safetensors", @"D:\m\loras");
    private static readonly ImageRequest Standard = new("1girl", Width: 832, Height: 1216, Steps: 3);

    /// <summary>A 24 GB card with 1 GB used by Windows, a chat model, a picture model (8 GB once it has drawn) and guests.</summary>
    private sealed class Rig : ICardMeter
    {
        public List<string> Events { get; } = [];
        public FakeChatModel Chat { get; }
        public FakeImageEngine Images { get; }
        public List<FakeGuest> Guests { get; } = [];
        public CardCoordinator Coordinator { get; }
        public List<string> Notes { get; } = [];
        public long ChatBytes { get; }
        public bool Unmeasurable { get; set; }

        public Rig(long chatBytes, bool chatLoaded = true, params (string Name, double Gb)[] guests)
        {
            ChatBytes = chatBytes;
            Chat = new FakeChatModel { Events = Events };
            if (chatLoaded) Chat.Loaded();
            Images = new FakeImageEngine { Events = Events };
            foreach (var (name, gb) in guests) Guests.Add(new FakeGuest(name, (long)(gb * Gb)) { Events = Events });
            // A 6.5 GB checkpoint: a Standard picture needs about 12.2 GB.
            Coordinator = new CardCoordinator(Images, Chat, this, _ => (long)(6.5 * Gb), Guests) { PollInterval = TimeSpan.FromMilliseconds(5) };
            Coordinator.Noted += Notes.Add;
        }

        public async Task<Rig> ChosenAsync()
        {
            await Coordinator.LoadAsync(Model);
            return this;
        }

        public Task<CardMemory?> ReadAsync(CancellationToken ct = default)
        {
            if (Unmeasurable) return Task.FromResult<CardMemory?>(null);
            var chat = Chat.IsLoaded ? ChatBytes : 0;
            var picture = Images.IsRunning ? 8 * Gb : 0;
            var guests = Guests.Where(g => g.HoldsMemory).Sum(g => g.Bytes);
            return Task.FromResult<CardMemory?>(new CardMemory(24 * Gb, 24 * Gb - Gb - chat - picture - guests, picture, chat));
        }

        public string Order() { lock (Events) return string.Join(" ", Events); }

        /// <summary>The owner's unload, as the model launcher passes it.</summary>
        public Task<bool> UnloadChat()
        {
            var had = Chat.IsLoaded || Chat.IsResting;
            Chat.UnloadByOwner();
            return Task.FromResult(had);
        }
    }

    // ---- Unload everything ----

    [Fact]
    public async Task Unload_everything_empties_the_card_and_says_what_went()
    {
        var rig = await new Rig(6 * Gb, true, ("the document search model", 1), ("the speech recognition model", 1.5)).ChosenAsync();
        await rig.Coordinator.DrawAsync(Standard);
        Assert.Equal(["the chat model", "the picture model", "the document search model", "the speech recognition model"], rig.Coordinator.InMemory);
        Assert.Null(rig.Coordinator.BusyReason);

        var cleared = await rig.Coordinator.ClearAsync(rig.UnloadChat);

        Assert.Equal("Unloaded the chat model, the picture model, the document search model and the speech recognition model.", cleared.Text);
        Assert.Empty(rig.Coordinator.InMemory);
        Assert.False(rig.Chat.IsLoaded);
        Assert.False(rig.Images.IsRunning);
        Assert.All(rig.Guests, g => Assert.False(g.HoldsMemory));
        // The checkpoint stays chosen, so the next picture needs no choosing; the chat model is the owner's unload, so it stays out.
        Assert.Equal(Model, rig.Images.Model);
        Assert.False(await rig.Chat.AskAsync());
        Assert.EndsWith("unload-chat release let-go let-go", rig.Order());
        Assert.Contains(rig.Notes, n => n.Contains("unload everything", StringComparison.Ordinal));

        // The card reads empty apart from Windows' own gigabyte, and the next picture loads its model again.
        Assert.Equal(23 * Gb, (await rig.ReadAsync())!.FreeBytes);
        await rig.Coordinator.DrawAsync(Standard);
        Assert.True(rig.Images.IsRunning);
        Assert.False(rig.Chat.IsLoaded);
    }

    [Fact]
    public async Task Unload_everything_with_nothing_loaded_says_so()
    {
        var rig = await new Rig(8 * Gb, chatLoaded: false).ChosenAsync();
        var cleared = await rig.Coordinator.ClearAsync(rig.UnloadChat);
        Assert.Equal("Nothing was loaded.", cleared.Text);
        Assert.Empty(cleared.Gone);
    }

    [Fact]
    public async Task Unload_everything_forgets_a_chat_model_that_was_set_aside()
    {
        var rig = await new Rig(18 * Gb).ChosenAsync();
        await rig.Coordinator.DrawAsync(Standard);
        Assert.True(rig.Chat.IsResting);

        var cleared = await rig.Coordinator.ClearAsync(rig.UnloadChat);

        Assert.Equal("Unloaded the chat model and the picture model.", cleared.Text);
        Assert.False(await rig.Chat.AskAsync());
        Assert.Equal(0, rig.Chat.Loads);
    }

    [Fact]
    public async Task Unload_everything_is_refused_while_a_reply_is_written_or_a_picture_is_drawn()
    {
        var rig = await new Rig(8 * Gb, true, ("the document search model", 1)).ChosenAsync();

        rig.Chat.IsBusy = true;
        Assert.StartsWith("A reply is being written.", rig.Coordinator.BusyReason);
        var cleared = await rig.Coordinator.ClearAsync(rig.UnloadChat);
        Assert.Equal(rig.Coordinator.BusyReason, cleared.Text);
        Assert.True(rig.Chat.IsLoaded);
        Assert.True(rig.Guests[0].HoldsMemory);
        rig.Chat.IsBusy = false;

        rig.Images.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var picture = rig.Coordinator.DrawAsync(Standard);
        await rig.Images.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith("A picture is being drawn.", rig.Coordinator.BusyReason);
        cleared = await rig.Coordinator.ClearAsync(rig.UnloadChat);
        Assert.NotNull(cleared.Refused);
        Assert.True(rig.Chat.IsLoaded);
        rig.Images.Hold.SetResult();
        await picture.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(rig.Coordinator.BusyReason);
        Assert.Null((await rig.Coordinator.ClearAsync(rig.UnloadChat)).Refused);
        Assert.Empty(rig.Coordinator.InMemory);
    }

    [Fact]
    public async Task Unload_everything_leaves_a_search_model_that_is_indexing_and_says_so()
    {
        var rig = await new Rig(8 * Gb, true, ("the document search model", 1), ("the speech recognition model", 1.5)).ChosenAsync();
        rig.Guests[0].IsBusy = true;

        var cleared = await rig.Coordinator.ClearAsync(rig.UnloadChat);

        Assert.Equal("Unloaded the chat model and the speech recognition model. The document search model is being used, so it stays.", cleared.Text);
        Assert.Equal(["the document search model"], rig.Coordinator.InMemory);
    }

    [Fact]
    public void What_unload_everything_did_reads_as_a_sentence()
    {
        Assert.Equal("Unloaded the chat model.", new CardCleared(["the chat model"], []).Text);
        Assert.Equal("Unloaded a and b.", new CardCleared(["a", "b"], []).Text);
        Assert.Equal("Nothing else was loaded. The document search model is being used, so it stays.", new CardCleared([], ["the document search model"]).Text);
        Assert.Equal("Unloaded a. B and c are being used, so they stay.", new CardCleared(["a"], ["b", "c"]).Text);
        Assert.Equal("Later.", new CardCleared([], [], "Later.").Text);
    }

    // ---- The document search model makes way ----

    [Fact]
    public async Task An_idle_search_model_is_let_go_for_a_picture_before_the_chat_model_is_set_aside()
    {
        // 24 − 1 (Windows) − 9 (chat) − 1 (search) = 13 GB free; the picture wants 12.2 + 1 spare. Without the search model it fits.
        var rig = await new Rig(9 * Gb, true, ("the document search model", 1)).ChosenAsync();

        await rig.Coordinator.DrawAsync(Standard);

        Assert.Equal(1, rig.Guests[0].LetGo);
        Assert.True(rig.Chat.IsLoaded);
        Assert.Equal(0, rig.Chat.Rests);
        Assert.Equal(CardChoice.KeptBoth, rig.Coordinator.LastDecision!.Choice);
        Assert.Equal(14 * Gb, rig.Coordinator.LastDecision.FreeBytes);
        Assert.Equal("let-go draw drawn", rig.Order());
        Assert.Contains(rig.Notes, n => n.StartsWith("the document search model was let go because a picture needs the room", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_search_model_stays_when_the_picture_fits_beside_it()
    {
        var rig = await new Rig(8 * Gb, true, ("the document search model", 1)).ChosenAsync();
        await rig.Coordinator.DrawAsync(Standard);
        Assert.Equal(0, rig.Guests[0].LetGo);
        Assert.True(rig.Chat.IsLoaded);
        Assert.Equal(CardChoice.KeptBoth, rig.Coordinator.LastDecision!.Choice);

        // With no chat model either: nothing is moved, as before.
        rig = await new Rig(8 * Gb, false, ("the document search model", 1)).ChosenAsync();
        await rig.Coordinator.DrawAsync(Standard);
        Assert.Equal(0, rig.Guests[0].LetGo);
        Assert.Equal(CardChoice.NothingLoaded, rig.Coordinator.LastDecision!.Choice);
    }

    [Fact]
    public async Task A_search_model_that_is_indexing_is_never_let_go_and_the_chat_model_is_set_aside_instead()
    {
        var rig = await new Rig(9 * Gb, true, ("the document search model", 1)).ChosenAsync();
        rig.Guests[0].IsBusy = true;

        await rig.Coordinator.DrawAsync(Standard);

        Assert.Equal(0, rig.Guests[0].LetGo);
        Assert.True(rig.Guests[0].HoldsMemory);
        Assert.True(rig.Chat.IsResting);
        Assert.Equal(CardChoice.SetChatAside, rig.Coordinator.LastDecision!.Choice);
    }

    [Fact]
    public async Task When_letting_the_search_model_go_is_not_enough_the_chat_model_is_set_aside_too()
    {
        var rig = await new Rig(18 * Gb, true, ("the document search model", 1)).ChosenAsync();
        await rig.Coordinator.DrawAsync(Standard);
        Assert.Equal(1, rig.Guests[0].LetGo);
        Assert.True(rig.Chat.IsResting);
        Assert.Equal("let-go rest draw drawn", rig.Order());
    }

    [Fact]
    public async Task A_card_that_cannot_be_measured_gets_all_the_room()
    {
        var rig = await new Rig(8 * Gb, true, ("the document search model", 1)).ChosenAsync();
        rig.Unmeasurable = true;
        await rig.Coordinator.DrawAsync(Standard);
        Assert.Equal(1, rig.Guests[0].LetGo);
        Assert.True(rig.Chat.IsResting);
    }

    [Fact]
    public async Task A_chat_model_coming_back_gets_the_room_it_had()
    {
        // 18 GB set aside for a picture; the search model starts meanwhile. Letting the picture model go is enough
        // (24 − 1 − 1 = 22 GB free then), so the search model stays.
        var rig = await SetAsideThenSearchStartsAsync(18);
        Assert.True(await rig.Chat.AskAsync());
        Assert.Equal(0, rig.Guests[0].LetGo);
        Assert.False(rig.Images.IsRunning);

        // 21.5 GB set aside: even without the picture model only 22 GB would be free, less than it needs with the spare
        // gigabyte, so the idle search model goes as well.
        rig = await SetAsideThenSearchStartsAsync(21.5);
        Assert.True(await rig.Chat.AskAsync());
        Assert.Equal(1, rig.Guests[0].LetGo);
        Assert.False(rig.Images.IsRunning);
        Assert.Contains(rig.Notes, n => n.Contains("the chat model is coming back and needs the room", StringComparison.Ordinal));

        // ...but not while it is searching.
        rig = await SetAsideThenSearchStartsAsync(21.5);
        rig.Guests[0].IsBusy = true;
        Assert.True(await rig.Chat.AskAsync());
        Assert.Equal(0, rig.Guests[0].LetGo);

        static async Task<Rig> SetAsideThenSearchStartsAsync(double chatGb)
        {
            var rig = await new Rig((long)(chatGb * Gb), true, ("the document search model", 1)).ChosenAsync();
            rig.Guests[0].HoldsMemory = false;
            await rig.Coordinator.DrawAsync(Standard);
            Assert.True(rig.Chat.IsResting);
            rig.Guests[0].HoldsMemory = true;
            return rig;
        }
    }

    [Fact]
    public async Task Making_way_for_a_chat_model_lets_only_idle_guests_go()
    {
        var rig = await new Rig(8 * Gb, true, ("the document search model", 1), ("the speech recognition model", 1.5)).ChosenAsync();
        rig.Guests[1].IsBusy = true;

        Assert.True(await rig.Coordinator.MakeWayAsync("a chat model is being loaded and does not fit on the card beside it"));

        Assert.False(rig.Guests[0].HoldsMemory);
        Assert.True(rig.Guests[1].HoldsMemory);
        Assert.Single(rig.Notes, n => n.Contains("was let go because a chat model is being loaded", StringComparison.Ordinal));
        // Nothing left to let go.
        Assert.False(await rig.Coordinator.MakeWayAsync("again"));
    }

    [Fact]
    public void A_model_that_does_not_fit_whole_asks_for_room_before_it_is_split_or_refused()
    {
        var budget = new FitBudget(22 * Sizes.GiB, 20 * Sizes.GiB, 1008, 60);
        Assert.False(LoadDecision.Decide("m", 8192, new FitResult(FitKind.Full, 40, 40, 1, 1), budget, null).WantsRoom);
        Assert.True(LoadDecision.Decide("m", 8192, new FitResult(FitKind.Partial, 37, 80, 1, 1), budget, null).WantsRoom);
        Assert.True(LoadDecision.Decide("m", 8192, new FitResult(FitKind.TooLarge, 0, 96, 75 * Sizes.GiB, 0), budget, null).WantsRoom);
        Assert.False(LoadDecision.Decide("m", 8192, null, null, null).WantsRoom);
        // Layers a benchmark proved are used as they are, but the room is still asked for when the model no longer fits whole.
        var tuned = new ModelRecord { Path = "m", TunedGpuLayers = 50, TunedContext = 8192 };
        var decision = LoadDecision.Decide("m", 8192, new FitResult(FitKind.Partial, 37, 80, 1, 1), budget, tuned);
        Assert.Equal(50, decision.GpuLayers);
        Assert.True(decision.WantsRoom);
    }

    [Fact]
    public async Task The_search_model_is_let_go_after_two_idle_minutes_and_never_while_it_is_used()
    {
        using var http = new LoopbackHttp();
        using var search = new EmbeddingServer(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())), new NoSettings(), new NoRecords());
        ICardGuest guest = search;

        Assert.Equal(TimeSpan.FromMinutes(2), search.IdleStop);
        Assert.Equal("the document search model", guest.Name);
        Assert.False(guest.HoldsMemory);
        Assert.False(search.IsRunning);
        Assert.False(search.IsBusy);
        Assert.True(await search.LetGoAsync());

        // Documents are being indexed: it is busy between two batches too, and is not let go.
        var indexing = true;
        search.KeepWhile = () => indexing;
        Assert.True(search.IsBusy);
        Assert.False(await search.LetGoAsync());
        indexing = false;
        Assert.False(search.IsBusy);
        Assert.True(await search.LetGoAsync());
    }

    private sealed class NoSettings : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings());
        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoRecords : IModelRecordStore
    {
        public Task<IReadOnlyList<ModelRecord>> AllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelRecord>>([]);
        public Task<ModelRecord?> FindAsync(string path, CancellationToken ct = default) => Task.FromResult<ModelRecord?>(null);
        public Task SaveAsync(ModelRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---- Fits beside pictures ----

    [Fact]
    public void What_a_standard_picture_needs_is_the_coordinators_own_estimate_for_the_largest_shape()
    {
        var checkpoint = (long)(6.46 * Gb);
        var need = BesidePictures.PictureNeed(checkpoint);
        Assert.InRange(need, (long)(11.5 * Gb), 13 * Gb);
        foreach (var shape in Enum.GetValues<PictureShape>())
        {
            var (width, height) = PictureLevels.Size(shape);
            // No shape at Standard needs more, so a marked model is kept whichever shape is drawn.
            Assert.True(need >= PictureMemory.Need(checkpoint, 0, new ImageRequest("x").At(PictureQuality.Standard, shape) with { Width = width, Height = height }));
        }
    }

    [Fact]
    public void A_model_is_marked_when_it_and_a_picture_fit_the_card_together()
    {
        var picture = BesidePictures.PictureNeed((long)(6.5 * Gb)); // about 12.3 GB
        var card = 22 * Gb;
        FitResult Full(double gb) => new(FitKind.Full, 40, 40, (long)(gb * Gb), 100);

        Assert.True(BesidePictures.Fits(Full(6), card, picture));
        Assert.True(BesidePictures.Fits(Full(8.5), card, picture));
        // 22 − 9 = 13 GB left: the picture fits, but not with the gigabyte kept spare.
        Assert.False(BesidePictures.Fits(Full(9), card, picture));
        Assert.False(BesidePictures.Fits(Full(17), card, picture));
        // The line is exactly the coordinator's: what is left must hold the picture and the margin.
        var most = card - picture - PictureMemory.Margin;
        Assert.True(BesidePictures.Fits(new FitResult(FitKind.Full, 40, 40, most, 100), card, picture));
        Assert.False(BesidePictures.Fits(new FitResult(FitKind.Full, 40, 40, most + 1, 100), card, picture));
        Assert.Equal(PictureMemory.FitsBeside(new CardMemory(24 * Gb, card - most), picture), BesidePictures.Fits(new FitResult(FitKind.Full, 40, 40, most, 100), card, picture));
    }

    [Fact]
    public void Nothing_is_marked_without_a_picture_model_or_for_a_model_that_is_not_whole_on_the_card()
    {
        var picture = BesidePictures.PictureNeed((long)(6.5 * Gb));
        var small = new FitResult(FitKind.Full, 40, 40, 4 * Gb, 100);
        Assert.False(BesidePictures.Fits(small, 22 * Gb, null));
        Assert.False(BesidePictures.Fits(small, 22 * Gb, 0));
        Assert.False(BesidePictures.Fits(null, 22 * Gb, picture));
        Assert.False(BesidePictures.Fits(small with { Kind = FitKind.Partial }, 22 * Gb, picture));
        Assert.False(BesidePictures.Fits(small with { Kind = FitKind.TooLarge }, 22 * Gb, picture));
        // A small card: the picture alone does not fit, so nothing is marked.
        Assert.False(BesidePictures.Fits(small, 8 * Gb, picture));
    }

    [Fact]
    public void What_a_model_really_took_replaces_the_estimate_once_it_has_been_loaded()
    {
        var picture = BesidePictures.PictureNeed((long)(6.46 * Gb)); // 12.3 GB
        var card = (long)(21.7 * Gb);
        // Seen on the owner's PC: a 9B model at Q8_0 judged at 10.6 GB really held 8.3 GB and stayed beside a picture.
        var judged = new FitResult(FitKind.Full, 32, 32, (long)(10.6 * Gb), 63);
        Assert.False(BesidePictures.Fits(judged, card, picture));
        Assert.True(BesidePictures.Fits(judged, card, picture, (long)(8.3 * Gb)));
        // Another judged at 10.4 GB really held 9.3 GB and was set aside.
        Assert.False(BesidePictures.Fits(judged with { NeededBytes = (long)(10.4 * Gb) }, card, picture, (long)(9.3 * Gb)));
        // A measurement never marks a model that is not whole on the card, and none means the estimate.
        Assert.False(BesidePictures.Fits(judged with { Kind = FitKind.Partial }, card, picture, Gb));
        Assert.False(BesidePictures.Fits(judged, card, picture, 0));

        // The measurement counts only for the context size it was taken at.
        var record = new ModelRecord { Path = "m.gguf", MeasuredVramBytes = (long)(8.3 * Gb), MeasuredVramContext = 8192 };
        Assert.Equal((long)(8.3 * Gb), record.MeasuredAt(8192));
        Assert.Null(record.MeasuredAt(16384));
        Assert.Null(new ModelRecord { Path = "m.gguf" }.MeasuredAt(8192));
    }

    [Fact]
    public async Task The_measured_memory_is_kept_in_the_models_record()
    {
        var root = Path.Combine(Path.GetTempPath(), "dogai-tests-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
        try
        {
            await services.MigrateDogDatabaseAsync();
            var store = services.GetRequiredService<IModelRecordStore>();
            await store.SaveAsync(new ModelRecord { Path = @"Z:\models\q.gguf", Quant = "Q8_0" });
            var record = (await store.FindAsync(@"Z:\models\q.gguf"))!;
            Assert.Null(record.MeasuredVramBytes);
            record.MeasuredVramBytes = 8_900_000_000;
            record.MeasuredVramContext = 8192;
            await store.SaveAsync(record);
            var again = (await store.FindAsync(@"Z:\models\q.gguf"))!;
            Assert.Equal(8_900_000_000, again.MeasuredAt(8192));
            Assert.Equal("Q8_0", again.Quant);
        }
        finally
        {
            await services.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void The_rule_in_how_fit_is_judged_names_the_checkpoint_and_the_memory()
    {
        var rule = BesidePictures.Rule("waiIllustrious", (long)(12.3 * Gb));
        Assert.StartsWith("Fits beside pictures:", rule);
        Assert.Contains("12.3 GB", rule);
        Assert.Contains("waiIllustrious", rule);
        Assert.Contains("set aside", rule);
    }

    // ---- Downloading a model that is too large ----

    [Fact]
    public void The_confirmation_names_the_size_and_why_the_model_will_not_run_here()
    {
        var budget = new FitBudget(22 * Sizes.GiB, 20 * Sizes.GiB, 1008, 60);
        var fit = new FitResult(FitKind.TooLarge, 0, 96, 75 * Sizes.GiB, 0);
        var text = TooLargeDownload.Warning("Colossus (Q8_0)", 70 * Sizes.GiB, 8192, fit, budget);
        Assert.Contains("Colossus (Q8_0) is a 70.0 GB download", text);
        Assert.Contains("8,192 tokens", text);
        Assert.Contains("needs about 75.0 GB", text);
        Assert.Contains("about 42.0 GB of graphics and system memory free", text);
        Assert.Contains("will not load it here", text);
    }

    [Fact]
    public void The_screens_keep_the_download_open_and_carry_the_ids_the_checks_use()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DogAI.sln"))) root = root.Parent;
        Assert.NotNull(root);
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([root!.FullName, "src", "DogAI.UI", .. parts]));

        var button = Read("Components", "Models", "DownloadButton.razor");
        // Too large no longer disables the button; it asks once, and the fit badge beside it is not this component's to hide.
        Assert.DoesNotContain("|| TooLarge", button);
        Assert.Contains("id=\"too-large-download\"", button);
        Assert.Contains("id=\"too-large-cancel\"", button);
        Assert.Contains("TooLargeDownload.Warning", button);

        var models = Read("Pages", "Models.razor");
        Assert.Contains("id=\"unload-all\"", models);
        Assert.Contains("id=\"unload-all-why\"", models);
        Assert.Contains("Launcher.UnloadEverythingAsync", models);
        Assert.Contains("id=\"fit-pictures-note\"", models);

        foreach (var tab in new[] { "RecommendedTab.razor", "RepoView.razor", "LibraryTab.razor" })
            Assert.Contains("<BesidePicturesChip", Read("Components", "Models", tab));

        var panel = Read("Components", "Documents", "SearchModelPanel.razor");
        Assert.Contains("id=\"search-model-state\"", panel);
        Assert.Contains("two minutes", panel);
        Assert.DoesNotContain("ten idle minutes", panel);
    }
}
