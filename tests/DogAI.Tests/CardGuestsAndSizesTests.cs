using DogAI.Core;
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Core.Settings;
using DogAI.Core.Video;
using DogAI.Core.Voice;
using DogAI.Engine;
using DogAI.Engine.Voice;
using Xunit.Abstractions;

namespace DogAI.Tests;

/// <summary>
/// A guest as the real ones are since M21 step 5: it asks at the door before it comes onto the card, and with the
/// door closed (a video is being made) it runs on the processor.
/// </summary>
internal sealed class DoorGuest(string name, CardDoor door) : ICardGuest
{
    public string Name => name;
    public bool HoldsMemory { get; private set; }
    public bool OnProcessor { get; private set; }
    public bool IsBusy { get; set; }
    public int OnCardLoads { get; private set; }
    public int ProcessorLoads { get; private set; }
    public List<string>? Events { get; init; }

    /// <summary>One use: it loads (or loads again) where the door allows, as EmbeddingServer and WhisperRecognizer do.</summary>
    public void Use()
    {
        using var entry = door.Enter();
        Settle(processor: entry is null);
    }

    /// <summary>A load onto the card that takes a while: the entry is kept until <paramref name="loaded"/> completes.</summary>
    public async Task UseSlowlyAsync(Task loaded)
    {
        using var entry = door.Enter();
        await loaded;
        Settle(processor: entry is null);
    }

    private void Settle(bool processor)
    {
        if (processor ? OnProcessor : HoldsMemory) return;
        HoldsMemory = !processor;
        OnProcessor = processor;
        if (processor) ProcessorLoads++;
        else OnCardLoads++;
        if (Events is { } events) lock (events) events.Add(processor ? "guest-processor" : "guest-card");
    }

    public Task<bool> LetGoAsync()
    {
        if (IsBusy) return Task.FromResult(false);
        HoldsMemory = false;
        OnProcessor = false;
        if (Events is { } events) lock (events) events.Add("let-go");
        return Task.FromResult(true);
    }

    /// <summary>What it is doing right now, when it is being used: completed by the test when that piece of work is done.</summary>
    public TaskCompletionSource? Working { get; set; }

    /// <summary>As the real guests: what is being done right now is finished, then it leaves, also while it is in use.</summary>
    public async Task<bool> StepOffAsync(CancellationToken ct = default)
    {
        if (Working is { } working) await working.Task.WaitAsync(ct);
        HoldsMemory = false;
        OnProcessor = false;
        if (Events is { } events) lock (events) events.Add(IsBusy ? "step-off" : "let-go");
        return true;
    }
}

/// <summary>M21 step 5: the door that keeps DOG.ai's guests off the card while a video is made.</summary>
public sealed class CardDoorTests
{
    [Fact]
    public void The_door_is_open_until_a_video_closes_it_and_every_close_needs_its_open()
    {
        var door = new CardDoor();
        var changes = 0;
        door.Changed += () => changes++;

        Assert.False(door.IsClosed);
        using (var entry = door.Enter()) Assert.NotNull(entry);

        door.Close();
        Assert.True(door.IsClosed);
        Assert.Null(door.Enter());
        // Two videos cannot be made at once, but the door would hold if two were asked for: it opens with the last.
        door.Close();
        door.Open();
        Assert.True(door.IsClosed);
        Assert.Null(door.Enter());
        door.Open();
        Assert.False(door.IsClosed);
        Assert.NotNull(door.Enter());
        Assert.Equal(4, changes);
        // An open too many does no harm.
        door.Open();
        Assert.False(door.IsClosed);
    }

    [Fact]
    public async Task A_video_waits_for_a_guest_that_is_on_its_way_onto_the_card()
    {
        var door = new CardDoor();
        // Nobody on the way in: nothing to wait for.
        await door.EmptyAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var first = door.Enter()!;
        var second = door.Enter()!;
        door.Close();
        var empty = door.EmptyAsync();
        await Task.Delay(30);
        Assert.False(empty.IsCompleted);
        first.Dispose();
        first.Dispose(); // leaving twice counts once
        await Task.Delay(30);
        Assert.False(empty.IsCompleted);
        second.Dispose();
        await empty.WaitAsync(TimeSpan.FromSeconds(5));

        // The wait can be given up (the owner cancels the clip).
        var third = door.Enter();
        Assert.Null(third); // still closed
        door.Open();
        using var late = door.Enter();
        using var cts = new CancellationTokenSource();
        var waiting = door.EmptyAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }
}

/// <summary>
/// M21 step 5: guests and videos through the card coordinator, and the rule for smaller cards (Quick clips by a plan
/// from 8 GB, measured with another program holding the rest of a 24 GB card).
/// </summary>
public sealed class CardGuestsAndSizesTests : IDisposable
{
    private const long Gb = FakeCard.Gb;
    private const long MiB = 1L << 20;
    private static readonly ImageModel Checkpoint = new(@"D:\m\c.safetensors", @"D:\m\loras");
    private static readonly ImageRequest Picture = new("1girl", Width: 832, Height: 1216, Steps: 3);
    private static readonly VideoRequest Quick = VideoRequest.For("A fox runs through the snow.", VideoQuality.Quick, PictureShape.Wide, seed: 9);
    private static readonly VideoRequest Better = VideoRequest.For("A fox runs through the snow.", VideoQuality.Better, PictureShape.Wide, seed: 9);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-cardsize-" + Guid.NewGuid().ToString("N"));
    private readonly VideoModel _model;

    public CardGuestsAndSizesTests()
    {
        _model = VideoFolders.ModelFor(_root, VideoFiles.Wan22);
        foreach (var (path, _) in _model.Files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Rig
    {
        public List<string> Events { get; } = [];
        public CardDoor Door { get; } = new();
        public FakeChatModel Chat { get; }
        public FakeImageEngine Images { get; }
        public FakeVideoEngine Video { get; }
        public DoorGuest Search { get; }
        public DoorGuest Speech { get; }
        public FakeCard Card { get; }
        public CardCoordinator Coordinator { get; }
        public IVideoEngine Engine => Coordinator;
        public List<string> Notes { get; } = [];

        public Rig(bool chatLoaded = false, long? cardTotal = null)
        {
            Chat = new FakeChatModel { Events = Events };
            if (chatLoaded) Chat.Loaded();
            Images = new FakeImageEngine { Events = Events };
            Video = new FakeVideoEngine { Events = Events };
            Search = new DoorGuest("the document search model", Door) { Events = Events };
            Speech = new DoorGuest("the speech recognition model", Door) { Events = Events };
            Card = new FakeCard(Chat, Images) { ChatBytes = 9 * Gb };
            if (cardTotal is { } total) Card.Total = total;
            Coordinator = new CardCoordinator(Images, Chat, Card, _ => (long)(6.5 * Gb), [Search, Speech], Video, Door) { PollInterval = TimeSpan.FromMilliseconds(5) };
            Coordinator.Noted += Notes.Add;
        }

        public string Order() { lock (Events) return string.Join(" ", Events); }
    }

    // ---- Guests ----

    [Fact]
    public async Task No_guest_comes_onto_the_card_while_a_clip_is_made_and_each_moves_back_at_its_next_use()
    {
        var rig = new Rig(chatLoaded: true);
        // Before the clip both guests are on the card, as before M21.
        rig.Search.Use();
        rig.Speech.Use();
        Assert.True(rig.Search.HoldsMemory && rig.Speech.HoldsMemory);
        rig.Events.Clear();
        rig.Video.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var making = rig.Engine.MakeAsync(_model, Quick);
        await rig.Video.Held.Task;

        // The clip cleared the card, the idle guests with it.
        Assert.Equal("rest let-go let-go make", rig.Order());
        Assert.True(rig.Door.IsClosed);
        Assert.Equal(["the video model"], rig.Coordinator.InMemory);

        // A document is indexed and something is said while the clip is made: both run, at once, off the card.
        rig.Search.Use();
        rig.Speech.Use();
        rig.Speech.Use();
        Assert.True(rig.Search.OnProcessor && rig.Speech.OnProcessor);
        Assert.False(rig.Search.HoldsMemory || rig.Speech.HoldsMemory);
        Assert.Equal((1, 1), (rig.Search.ProcessorLoads, rig.Speech.ProcessorLoads));
        Assert.Equal(["the video model"], rig.Coordinator.InMemory);
        Assert.Equal("rest let-go let-go make guest-processor guest-processor", rig.Order());

        rig.Video.Hold.SetResult();
        await making;

        // The clip is over: the door is open, and each guest goes back onto the card the next time it is used.
        Assert.False(rig.Door.IsClosed);
        Assert.False(rig.Search.HoldsMemory);
        rig.Search.Use();
        Assert.True(rig.Search.HoldsMemory);
        Assert.False(rig.Search.OnProcessor);
        Assert.True(rig.Speech.OnProcessor); // not used yet
        rig.Speech.Use();
        Assert.True(rig.Speech.HoldsMemory);
        Assert.Equal((2, 2), (rig.Search.OnCardLoads, rig.Speech.OnCardLoads));
        Assert.Equal(["the document search model", "the speech recognition model"], rig.Coordinator.InMemory);
    }

    [Fact]
    public async Task A_guest_on_its_way_in_when_the_clip_is_asked_for_is_let_in_first_and_then_makes_way()
    {
        var rig = new Rig();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = rig.Search.UseSlowlyAsync(loaded.Task);
        var progress = new Collect<VideoProgress>();

        var making = rig.Engine.MakeAsync(_model, Quick, progress);
        await Task.Delay(60);

        // The clip neither measures the card nor starts while the guest is half way in.
        Assert.False(making.IsCompleted);
        Assert.Equal("", rig.Order());
        Assert.Empty(rig.Video.Made);
        Assert.True(rig.Door.IsClosed);

        loaded.SetResult();
        await loading;
        await making;

        // It arrived, was idle, and was let go with everything else.
        Assert.Equal("guest-card let-go make made", rig.Order());
        Assert.False(rig.Search.HoldsMemory);
        Assert.Equal([23 * Gb], rig.Video.Told);
    }

    [Fact]
    public async Task A_guest_in_use_finishes_what_it_is_doing_leaves_the_card_and_carries_on_on_the_processor()
    {
        // Documents are being indexed on the card when the clip is asked for. In M20 such a guest stayed and the clip
        // was planned around it; measured in M21 step 5, the clip then took half as long again (3 min 40 s for 2 min 26 s).
        var rig = new Rig(chatLoaded: true);
        rig.Search.Use();
        rig.Search.IsBusy = true;
        rig.Search.Working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Events.Clear();
        var progress = new Collect<VideoProgress>();

        var making = rig.Engine.MakeAsync(_model, Quick with { Preview = true }, progress);
        await Task.Delay(60);

        // The batch being answered is never cut off: the clip waits for it, with the chat model already aside.
        Assert.False(making.IsCompleted);
        Assert.Equal("rest", rig.Order());
        Assert.True(rig.Search.HoldsMemory);
        Assert.Contains(progress.Items, p => p.Stage == VideoStages.MakingRoom);

        rig.Search.Working.SetResult();
        rig.Video.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.Video.Held.Task;

        // It left, and the clip has the whole card: no plan, previews as asked.
        Assert.Equal("rest step-off make", rig.Order());
        Assert.False(rig.Search.HoldsMemory);
        Assert.False(rig.Coordinator.IsVideoCrowded);
        Assert.Equal([23 * Gb], rig.Video.Told);
        Assert.True(rig.Video.Made[0].Request.Preview);
        Assert.Equal(["the video model"], rig.Coordinator.InMemory);
        // The next batch runs on the processor.
        rig.Search.Use();
        Assert.True(rig.Search.OnProcessor);
        Assert.False(rig.Search.HoldsMemory);

        rig.Video.Hold.SetResult();
        await making;
        Assert.Equal("a video needs the whole card (about 15.0 GB); the chat model (9.0 GB) was set aside and the document search model left the card and carries on on the processor. 23.0 GB is free.", rig.Notes[^1]);
        // And the batch after the clip is back on the card.
        rig.Search.Use();
        Assert.True(rig.Search.HoldsMemory);

        // The wait for a working guest can be given up: the clip is cancelled, the guest goes on where it is.
        var waiting = new Rig();
        waiting.Speech.Use();
        waiting.Speech.IsBusy = true;
        waiting.Speech.Working = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var held = waiting.Engine.MakeAsync(_model, Quick, ct: cts.Token);
        await Task.Delay(30);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
        Assert.True(waiting.Speech.HoldsMemory);
        Assert.False(waiting.Door.IsClosed);
        Assert.Empty(waiting.Video.Made);

        // Pictures, chat models and Unload everything still never take a guest that is being used (M15).
        var picture = new Rig(chatLoaded: true);
        picture.Card.ChatBytes = 18 * Gb;
        await picture.Coordinator.LoadAsync(Checkpoint);
        picture.Search.Use();
        picture.Search.IsBusy = true;
        await picture.Coordinator.DrawAsync(Picture);
        Assert.True(picture.Search.HoldsMemory);
        var cleared = await picture.Coordinator.ClearAsync(() => Task.FromResult(false));
        Assert.Contains("the document search model", cleared.Stayed);
    }

    [Fact]
    public async Task The_real_guests_step_off_when_asked_and_a_guest_that_knows_no_better_is_let_go_only_when_idle()
    {
        using var http = new LoopbackHttp();
        using var search = new EmbeddingServer(http, new LlamaServerLocator(new AppPaths(_root)), new Settings(), new NoRecords(), new CardDoor());
        // Indexing: busy between two batches, so it is not let go for a picture, but it steps off for a video.
        search.KeepWhile = () => true;
        Assert.False(await search.LetGoAsync());
        Assert.True(await search.StepOffAsync());
        Assert.False(((ICardGuest)search).HoldsMemory);
        using var speech = new WhisperRecognizer(new Settings(), new CardDoor());
        Assert.True(await speech.StepOffAsync());
        Assert.False(speech.HoldsMemory);

        // A guest without a step of its own (the fakes of M15 and M20): idle it goes, in use it stays.
        ICardGuest plain = new FakeGuest("a guest", Gb);
        Assert.True(await plain.StepOffAsync());
        var busy = new FakeGuest("a guest", Gb) { IsBusy = true };
        Assert.False(await ((ICardGuest)busy).StepOffAsync());
        Assert.True(busy.HoldsMemory);
    }

    [Fact]
    public async Task The_door_opens_again_after_a_clip_that_failed_was_cancelled_or_was_refused()
    {
        var failing = new Rig();
        failing.Video.FailWith = "The video engine stopped.";
        await Assert.ThrowsAsync<VideoEngineException>(() => failing.Engine.MakeAsync(_model, Quick));
        Assert.False(failing.Door.IsClosed);

        var cancelled = new Rig();
        cancelled.Video.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var making = cancelled.Engine.MakeAsync(_model, Quick, ct: cts.Token);
        await cancelled.Video.Held.Task;
        Assert.True(cancelled.Door.IsClosed);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => making);
        Assert.False(cancelled.Door.IsClosed);

        // Refused before anything moved (a request that cannot be made), and refused after the card was cleared.
        var refused = new Rig();
        await Assert.ThrowsAsync<VideoEngineException>(() => refused.Engine.MakeAsync(_model, Quick with { Prompt = " " }));
        Assert.False(refused.Door.IsClosed);
        refused.Card.Other = 20 * Gb;
        await Assert.ThrowsAsync<VideoEngineException>(() => refused.Engine.MakeAsync(_model, Quick));
        Assert.False(refused.Door.IsClosed);
        refused.Search.Use();
        Assert.True(refused.Search.HoldsMemory);

        // Cancelled while it waited for a guest on its way in.
        var waiting = new Rig();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = waiting.Search.UseSlowlyAsync(never.Task);
        using var cts2 = new CancellationTokenSource();
        var held = waiting.Engine.MakeAsync(_model, Quick, ct: cts2.Token);
        await Task.Delay(30);
        cts2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);
        Assert.False(waiting.Door.IsClosed);
        Assert.False(waiting.Coordinator.IsBusy);
        never.SetResult();
        await loading;
        Assert.True(waiting.Search.HoldsMemory);
    }

    [Fact]
    public async Task Pictures_and_chat_models_beside_a_guest_are_as_before_the_door_is_never_closed_for_them()
    {
        var rig = new Rig(chatLoaded: true);
        var closings = 0;
        rig.Door.Changed += () => closings++;
        await rig.Coordinator.LoadAsync(Checkpoint);
        rig.Images.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // While a picture is drawn a guest comes onto the card as ever.
        var drawing = rig.Coordinator.DrawAsync(Picture);
        await rig.Images.Held.Task;
        Assert.False(rig.Door.IsClosed);
        rig.Search.Use();
        Assert.True(rig.Search.HoldsMemory);
        Assert.False(rig.Search.OnProcessor);
        rig.Images.Hold.SetResult();
        rig.Images.Hold = null;
        await drawing;

        // A reply, an enlargement, the picture model changed, and Unload everything: none of them touches the door.
        Assert.True(await rig.Chat.AskAsync());
        await rig.Coordinator.EnlargeAsync(new ImageEnlarge(KeptPictureTests.Webp(64, 64), "x4.safetensors"));
        await rig.Coordinator.ReleaseAsync();
        rig.Speech.Use();
        var cleared = await rig.Coordinator.ClearAsync(() => Task.FromResult(true));
        Assert.Contains("the document search model", cleared.Gone);
        Assert.Contains("the speech recognition model", cleared.Gone);
        Assert.Equal(0, closings);
        Assert.Equal((0, 0), (rig.Search.ProcessorLoads, rig.Speech.ProcessorLoads));
    }

    [Fact]
    public async Task The_real_guests_say_where_they_run_and_the_owner_is_told_in_words()
    {
        var door = new CardDoor();
        using var http = new LoopbackHttp();
        using var search = new EmbeddingServer(http, new LlamaServerLocator(new AppPaths(_root)), new Settings(), new NoRecords(), door);
        ICardGuest guest = search;
        var changes = 0;
        search.Changed += () => changes++;

        // Not running: on neither.
        Assert.False(search.OnProcessor);
        Assert.False(guest.HoldsMemory);
        // On the card it is started exactly as before M21; for the processor with no layers and no card at all
        // (with "--gpu-layers 0" alone the program still took 454 MiB of the card).
        Assert.Equal(
            ["--model", @"D:\m\e.gguf", "--host", "127.0.0.1", "--port", "4321", "--embedding", "--ctx-size", "8192", "--parallel", "4",
                "--batch-size", "2048", "--ubatch-size", "2048", "--gpu-layers", "999", "--no-webui"],
            EmbeddingServer.Arguments(@"D:\m\e.gguf", 4321, onProcessor: false));
        Assert.Equal(
            ["--model", @"D:\m\e.gguf", "--host", "127.0.0.1", "--port", "4321", "--embedding", "--ctx-size", "8192", "--parallel", "4",
                "--batch-size", "2048", "--ubatch-size", "2048", "--gpu-layers", "0", "--no-webui", "--device", "none"],
            EmbeddingServer.Arguments(@"D:\m\e.gguf", 4321, onProcessor: true));
        // The Documents panel hears of the door through the model it shows.
        door.Close();
        door.Open();
        Assert.Equal(2, changes);

        using var speech = new WhisperRecognizer(new Settings(), door);
        Assert.Null(speech.Slower);
        Assert.False(speech.HoldsMemory);
        door.Close();
        Assert.Equal(CardGuestWords.SpeechOnProcessor, speech.Slower);
        // Dictation passes the words on to the chat box, which shows them with its own line.
        using var dictation = new Dictation(new VoiceFlowTests.FakeMic(), speech, new Settings());
        Assert.Equal(CardGuestWords.SpeechOnProcessor, dictation.Slower);
        door.Open();
        Assert.Null(dictation.Slower);
        // Without a door (nothing shares the card) nothing is ever slower, and a recognizer that says nothing is taken at its word.
        using var alone = new WhisperRecognizer(new Settings());
        Assert.Null(alone.Slower);
        using var plain = new Dictation(new VoiceFlowTests.FakeMic(), new VoiceFlowTests.FakeEars("hello"), new Settings());
        Assert.Null(plain.Slower);

        Assert.Contains("video", CardGuestWords.SpeechOnProcessor);
        Assert.Contains("processor", CardGuestWords.SpeechOnProcessor);
        Assert.Contains("video", CardGuestWords.SearchOnProcessor);
        Assert.Contains("processor", CardGuestWords.SearchAbout);
        Assert.Contains("slower", CardGuestWords.SearchAbout);
        await Task.CompletedTask;
    }

    [Fact]
    public void The_screens_show_the_words_where_the_guests_are_used()
    {
        var ui = Path.Combine(RepoRoot(), "src", "DogAI.UI");
        var composer = File.ReadAllText(Path.Combine(ui, "Components", "Chat", "ChatComposer.razor"));
        Assert.Contains("id=\"composer-voice-slower\"", composer);
        Assert.Contains("Dict.Slower", composer);
        var panel = File.ReadAllText(Path.Combine(ui, "Components", "Documents", "SearchModelPanel.razor"));
        Assert.Contains("id=\"search-model-processor\"", panel);
        Assert.Contains("CardGuestWords.SearchOnProcessor", panel);
        Assert.Contains("CardGuestWords.SearchAbout", panel);
        var page = File.ReadAllText(Path.Combine(ui, "Pages", "VideoMake.razor"));
        // The Video page is where the owner is while a clip is made (leaving it stops the clip): the words are there.
        Assert.Contains("id=\"video-guests\"", page);
        Assert.Contains("CardGuestWords.Meanwhile(Search.OnProcessor, Library.IsIndexing, Dict.IsOn && Dict.Slower is not null)", page);
        Assert.Null(CardGuestWords.Meanwhile(false, false, false));
        Assert.Null(CardGuestWords.Meanwhile(false, true, false)); // indexing on the card (it was there before the clip): nothing to say
        Assert.Equal(CardGuestWords.IndexingMeanwhile, CardGuestWords.Meanwhile(true, true, false));
        Assert.Equal(CardGuestWords.SearchMeanwhile, CardGuestWords.Meanwhile(true, false, false));
        Assert.Equal(CardGuestWords.SpeechOnProcessor, CardGuestWords.Meanwhile(false, false, true));
        Assert.Equal(CardGuestWords.IndexingMeanwhile + " " + CardGuestWords.SpeechOnProcessor, CardGuestWords.Meanwhile(true, true, true));
        Assert.Contains("slower", CardGuestWords.IndexingMeanwhile);
        Assert.Contains("by itself", CardGuestWords.IndexingMeanwhile);
        Assert.Contains("id=\"video-small-card\"", page);
        Assert.Contains("VideoMemory.SmallCardNote", page);
        // The app gives the guests and the coordinator one door.
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DogAI.App", "App.xaml.cs"));
        Assert.Contains("AddSingleton<DogAI.Core.Images.CardDoor>()", app);
        Assert.Contains("door: sp.GetRequiredService<DogAI.Core.Images.CardDoor>()", app);
    }

    // ---- Smaller cards ----

    /// <summary>A card of this many GB as it reports itself: a little under the size on the box.</summary>
    private static long CardOf(int gb) => gb * 1024 * MiB - 8 * MiB;

    public static TheoryData<int, string, bool, string?> Sizes() => new()
    {
        // GB, the plan a Quick clip gets on the cleared card, whether Better is made, and Better's plan.
        { 8, "3.9", false, null },
        { 9, "4.9", false, null },
        { 10, "5.9", false, null },
        { 11, "6.9", false, null },
        { 12, "7.9", false, null },
        { 13, "8.9", false, null },
        { 14, "9.9", false, null },
        { 15, "10.0", false, null },
        { 16, "10.0", false, null },
        { 17, "10.0", true, "12.9" },
        { 18, "10.0", true, "13.0" },
        { 19, "10.0", true, "13.0" },
        { 20, "10.0", true, "13.0" },
        { 21, "10.0", true, "13.0" },
        { 22, "10.0", true, "13.0" },
        { 23, "10.0", true, "13.0" },
        { 24, "-3", true, "-3" },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public async Task Every_card_from_8_to_24_GB_makes_Quick_clips_and_is_planned_by_its_size(int gb, string quickPlan, bool better, string? betterPlan)
    {
        var total = CardOf(gb);
        var free = total - Gb; // Windows keeps a gigabyte
        var whole = gb == 24;

        // The rule, asked directly.
        Assert.True(VideoMemory.CardCanMake(total, VideoQuality.Quick));
        Assert.Equal(better, VideoMemory.CardCanMake(total, VideoQuality.Better));
        Assert.Null(VideoMemory.CannotMake(total, VideoQuality.Quick));
        Assert.Equal(!better, VideoMemory.IsSmallCard(total));
        Assert.Equal(!whole, VideoMemory.AlwaysPlanned(total));
        Assert.Equal(!whole, VideoMemory.Crowded(free));
        Assert.True(VideoMemory.RoomFor(free, Quick));
        Assert.Equal(better, VideoMemory.RoomFor(free, Better));
        Assert.Equal(quickPlan, VideoMemory.EnginePlan(free));
        if (betterPlan is not null) Assert.Equal(betterPlan, VideoMemory.EnginePlan(free, VideoDecoder.Own));
        // A plan is never under what the engine was seen to work with at normal speed (3.8), nor over the cap.
        if (!whole) Assert.InRange(double.Parse(quickPlan, System.Globalization.CultureInfo.InvariantCulture), 3.8, 10.0);

        // Get models › Video offers it, and says what kind of offer it is.
        Assert.True(VideoDownloads.Offered(total));
        var fit = VideoDownloads.FitText(total);
        if (better)
        {
            Assert.StartsWith("Fits this PC: a clip needs about 15.0 GB of graphics memory (16.0 GB for Better)", fit);
            Assert.DoesNotContain("not been tried", fit);
        }
        else
        {
            Assert.StartsWith($"Fits this PC for Quick clips: this card has {gb}.0 GB, and a Quick clip can be made with 7.0 GB of it free. Better needs 16.0 GB free and is not offered here.", fit);
            Assert.EndsWith(VideoMemory.SmallCardNote, fit);
            Assert.Contains("Quick works on it.", VideoMemory.CannotMake(total, VideoQuality.Better));
        }

        // Through the coordinator: a Quick clip is made, the engine told what is free, without previews when planned.
        var rig = new Rig(chatLoaded: true, cardTotal: total);
        rig.Card.FreeRam = 16 * Gb;
        rig.Video.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var making = rig.Engine.MakeAsync(_model, Quick with { Preview = true });
        await rig.Video.Held.Task;
        Assert.Equal(!whole, rig.Coordinator.IsVideoCrowded);
        rig.Video.Hold.SetResult();
        await making;
        Assert.Equal("rest make made", rig.Order());
        Assert.Equal([free], rig.Video.Told);
        Assert.Equal(whole, rig.Video.Made[0].Request.Preview);
        Assert.Equal(quickPlan, VideoMemory.EnginePlan(rig.Video.Told[0], rig.Video.Made[0].Request.Decoder));
        if (whole) Assert.EndsWith("23.0 GB is free.", rig.Notes[^1]);
        else Assert.EndsWith($"(the card is smaller than that), so the video engine was told to plan with {quickPlan} GB and to send no previews.", rig.Notes[^1]);

        // Better: made where the card can, refused before anything is moved where it cannot.
        var other = new Rig(chatLoaded: true, cardTotal: total);
        other.Card.FreeRam = 16 * Gb;
        if (better)
        {
            await other.Engine.MakeAsync(_model, Better);
            Assert.Equal(betterPlan, VideoMemory.EnginePlan(other.Video.Told[0], VideoDecoder.Own));
        }
        else
        {
            var e = await Assert.ThrowsAsync<VideoEngineException>(() => other.Engine.MakeAsync(_model, Better));
            Assert.Equal($"A video needs about 16.0 GB of graphics memory, and this PC’s graphics card has {gb}.0 GB.", e.Message);
            Assert.Equal("", other.Order());
            Assert.True(other.Chat.IsLoaded);
        }
    }

    [Fact]
    public async Task A_24_GB_card_with_nothing_else_on_it_is_judged_exactly_as_in_M20()
    {
        const long real = 24_564 * MiB; // as this PC's card reports itself
        Assert.Equal(15 * Gb, VideoMemory.Need(Quick));
        Assert.Equal(16 * Gb, VideoMemory.Need(Better));
        Assert.Equal(16 * Gb, VideoMemory.LeastNeed(Better));
        Assert.False(VideoMemory.IsSmallCard(real));
        Assert.False(VideoMemory.AlwaysPlanned(real));
        Assert.Equal(VideoMemory.WholeCardPlan, VideoMemory.EnginePlan(real - Gb));
        Assert.Equal(VideoMemory.WholeCardPlan, VideoMemory.EnginePlan(real - Gb, VideoDecoder.Own));
        // No plan, so the model is on the card and RAM is not asked about, however little is free.
        Assert.True(VideoMemory.RamEnough(Gb, real - Gb));
        Assert.True(VideoMemory.RamEnough(null, real - Gb));

        foreach (var request in new[] { Quick, Better })
        {
            var rig = new Rig(chatLoaded: true, cardTotal: real);
            rig.Card.FreeRam = 2 * Gb;
            rig.Card.Other = real - 23 * Gb;
            await rig.Engine.MakeAsync(_model, request with { Preview = true });
            Assert.Equal("rest make made", rig.Order());
            Assert.Equal([23 * Gb], rig.Video.Told);
            Assert.True(rig.Video.Made[0].Request.Preview);
            Assert.Equal(request with { Preview = true }, rig.Video.Made[0].Request);
            Assert.False(rig.Coordinator.IsVideoCrowded);
            var need = request == Quick ? "15.0" : "16.0";
            Assert.Equal(new CardDecision(CardChoice.SetChatAside, request == Quick ? 15 * Gb : 16 * Gb, 23 * Gb), rig.Coordinator.LastDecision);
            Assert.Equal($"a video needs the whole card (about {need} GB); the chat model (9.0 GB) was set aside. 23.0 GB is free.", rig.Notes[^1]);
        }
        Assert.StartsWith("Fits this PC: a clip needs about 15.0 GB of graphics memory (16.0 GB for Better), and this card has 24.0 GB.", VideoDownloads.FitText(real));
        Assert.Equal("Drawing, step 3 of 20", VideoPanels.Doing(new VideoProgress(VideoStages.Drawing, 3, 20), crowded: false, alwaysPlanned: false));
    }

    [Fact]
    public async Task A_card_under_8_GB_is_not_offered_and_says_so()
    {
        foreach (var gb in new[] { 4, 6, 7 })
        {
            var total = CardOf(gb);
            Assert.False(VideoMemory.CardCanMake(total, VideoQuality.Quick));
            Assert.False(VideoMemory.CardCanMake(total, VideoQuality.Better));
            Assert.False(VideoMemory.IsSmallCard(total));
            Assert.False(VideoDownloads.Offered(total));
            Assert.Equal($"Needs a graphics card with 8 GB or more; this one has {gb}.0 GB.", VideoDownloads.FitText(total));
            Assert.Equal($"Quick needs about 7.0 GB of free graphics memory, more than this card ({gb}.0 GB) has once Windows has its part.", VideoMemory.CannotMake(total, VideoQuality.Quick));

            var rig = new Rig(chatLoaded: true, cardTotal: total);
            var e = await Assert.ThrowsAsync<VideoEngineException>(() => rig.Engine.MakeAsync(_model, Quick));
            Assert.Equal($"A video needs about 7.0 GB of graphics memory, and this PC’s graphics card has {gb}.0 GB.", e.Message);
            Assert.Equal("", rig.Order());
        }
        Assert.False(VideoDownloads.Offered(null));
        Assert.Equal("Needs an NVIDIA graphics card with 8 GB or more. No graphics card was found on this PC, so it is not offered.", VideoDownloads.FitText(null));
        // A card that cannot be measured is not held back by the page (the coordinator clears it and goes ahead).
        Assert.True(VideoMemory.CardCanMake(null, VideoQuality.Quick));
        Assert.False(VideoMemory.IsSmallCard(null));
        Assert.False(VideoMemory.AlwaysPlanned(null));
        // An 8 GB card as some report it (8,188 MiB) and one at 8,192.
        Assert.True(VideoDownloads.Offered(8_188 * MiB));
        Assert.True(VideoDownloads.Offered(8_192 * MiB));
    }

    [Fact]
    public async Task With_less_than_a_planned_clip_needs_free_it_is_refused_in_words_and_nothing_is_made()
    {
        // The least a Quick clip was made with at normal speed was 6.8 GB free; with 6.2 it was a fifth slower, and
        // with Faster at 6.1 the engine made nothing.
        Assert.Equal(7 * Gb, VideoMemory.QuickPlannedNeed);
        Assert.Equal(7 * Gb, VideoMemory.LeastNeed(Quick));
        Assert.Equal(7 * Gb, VideoMemory.LeastNeed(VideoQuality.Quick));
        Assert.True(VideoMemory.RoomFor(7 * Gb, Quick));
        Assert.True(VideoMemory.RoomFor(7 * Gb - 256 * MiB, Quick));
        Assert.False(VideoMemory.RoomFor(7 * Gb - 257 * MiB, Quick));
        Assert.False(VideoMemory.RoomFor((long)(6.5 * Gb), Quick));
        // Better is as in M20: its whole need, within half a gigabyte.
        Assert.True(VideoMemory.RoomFor(16 * Gb - 512 * MiB, Better));
        Assert.False(VideoMemory.RoomFor(16 * Gb - 513 * MiB, Better));
        Assert.False(VideoMemory.RoomFor(15 * Gb, Better));

        // An 8 GB card on which Windows and other programs hold 1.5 GB: 6.5 GB is free once DOG.ai made room.
        var rig = new Rig(chatLoaded: true, cardTotal: 8 * Gb);
        rig.Card.Other = (long)(1.5 * Gb);
        rig.Card.FreeRam = 20 * Gb;
        var e = await Assert.ThrowsAsync<VideoEngineException>(() => rig.Engine.MakeAsync(_model, Quick));
        Assert.Equal("A video needs about 7.0 GB of graphics memory and only 6.5 GB is free after DOG.ai made room. Close other programs that use the graphics card, then try again.", e.Message);
        Assert.Equal("rest", rig.Order());
        Assert.Empty(rig.Video.Made);
        Assert.False(rig.Door.IsClosed);
        // The chat model comes back with the next message, as after a picture.
        Assert.True(await rig.Chat.AskAsync());

        // With 6.8 GB free the same card makes the clip, with a plan of 3.8.
        var made = new Rig(chatLoaded: true, cardTotal: 8 * Gb);
        made.Card.Other = (long)(1.2 * Gb);
        await made.Engine.MakeAsync(_model, Quick);
        Assert.Equal("3.8", VideoMemory.EnginePlan(made.Video.Told[0]));

        // A 24 GB card with another program holding 14 GB: 10 GB free. M20 refused it (a clip needed 15); now it is planned.
        var crowded = new Rig(chatLoaded: true);
        crowded.Card.Other = 14 * Gb;
        await crowded.Engine.MakeAsync(_model, Quick);
        Assert.Equal([10 * Gb], crowded.Video.Told);
        Assert.Equal("7.0", VideoMemory.EnginePlan(crowded.Video.Told[0]));
        Assert.EndsWith("Only 10.0 GB is free (something else holds part of the card), so the video engine was told to plan with 7.0 GB and to send no previews.", crowded.Notes[^1]);
        // Better there is still refused: its decoder needs the model kept on the card.
        var better = new Rig(chatLoaded: true);
        better.Card.Other = 14 * Gb;
        e = await Assert.ThrowsAsync<VideoEngineException>(() => better.Engine.MakeAsync(_model, Better));
        Assert.StartsWith("A video needs about 16.0 GB of graphics memory and only 10.0 GB is free after DOG.ai made room.", e.Message);
    }

    [Fact]
    public async Task A_clip_made_by_a_plan_needs_free_RAM_and_is_refused_in_words_without_it()
    {
        // The engine keeps the model itself in RAM then: 11.0 GB of its own in every planned clip measured.
        Assert.Equal(12 * Gb, VideoMemory.PlannedRamNeed);
        Assert.True(VideoMemory.RamEnough(12 * Gb, 10 * Gb));
        Assert.False(VideoMemory.RamEnough(12 * Gb - 1, 10 * Gb));
        Assert.False(VideoMemory.RamEnough(0, 21 * Gb));
        // Not measured: not held against the clip. The whole card: not asked.
        Assert.True(VideoMemory.RamEnough(null, 10 * Gb));
        Assert.True(VideoMemory.RamEnough(Gb, 22 * Gb));
        Assert.True(VideoMemory.RamEnough(Gb, null));

        // A 12 GB card on a PC with 9.3 GB of RAM free.
        var rig = new Rig(chatLoaded: true, cardTotal: CardOf(12));
        rig.Card.FreeRam = (long)(9.3 * Gb);
        var e = await Assert.ThrowsAsync<VideoEngineException>(() => rig.Engine.MakeAsync(_model, Quick));
        Assert.Equal("On a graphics card of this size the video model is kept in the PC’s own memory while a clip is made. That takes about 12.0 GB of free memory (RAM), and only 9.3 GB is free. Close other programs, then try again.", e.Message);
        Assert.Equal("rest", rig.Order());
        Assert.Empty(rig.Video.Made);
        Assert.False(rig.Coordinator.IsBusy);
        Assert.False(rig.Door.IsClosed);
        Assert.Contains("of RAM is free, so the video was not made", rig.Notes[^1]);
        Assert.True(await rig.Chat.AskAsync());

        // A 24 GB card with another program on it says it the other way.
        var crowded = new Rig(chatLoaded: true);
        crowded.Card.Other = 7 * Gb;
        crowded.Card.FreeRam = 8 * Gb;
        e = await Assert.ThrowsAsync<VideoEngineException>(() => crowded.Engine.MakeAsync(_model, Quick));
        Assert.StartsWith("With part of the graphics card in use the video model is kept in the PC’s own memory while a clip is made. That takes about 12.0 GB of free memory (RAM), and only 8.0 GB is free.", e.Message);

        // With the RAM, or with RAM that could not be measured, the clip is made.
        var enough = new Rig(chatLoaded: true, cardTotal: CardOf(12));
        enough.Card.FreeRam = 12 * Gb;
        await enough.Engine.MakeAsync(_model, Quick);
        var unknown = new Rig(chatLoaded: true, cardTotal: CardOf(12));
        await unknown.Engine.MakeAsync(_model, Quick);
        Assert.Equal("rest make made", unknown.Order());
    }

    [Fact]
    public void The_page_says_a_smaller_card_was_not_tried_for_real()
    {
        Assert.Contains("has not been tried on a graphics card of this size", VideoMemory.SmallCardNote);
        Assert.Contains("24 GB card with another program holding the rest", VideoMemory.SmallCardNote);
        Assert.Contains("12 GB of free memory (RAM)", VideoMemory.SmallCardNote);
        Assert.Contains("nothing of the clip shows until it is done", VideoMemory.SmallCardNote);
        // While a clip is made on such a card the line does not blame another program.
        var drawing = new VideoProgress(VideoStages.Drawing, 3, 20);
        Assert.Equal("Drawing, step 3 of 20 On a graphics card of this size nothing of the clip shows until it is done.", VideoPanels.Doing(drawing, crowded: true, alwaysPlanned: true));
        Assert.Equal("Drawing, step 3 of 20 " + VideoPanels.Crowded, VideoPanels.Doing(drawing, crowded: true));
        Assert.Equal("Drawing, step 3 of 20", VideoPanels.Doing(drawing, crowded: false, alwaysPlanned: true));
    }

    [Fact]
    public void For_checks_the_card_can_be_taken_for_a_smaller_one_and_nothing_else_changes_it()
    {
        var real = new GpuInfo("RTX 4090", 24_564 * MiB, 900 * MiB, 23_664 * MiB);
        IReadOnlyList<GpuInfo> cards = [real];
        // Not set, or nonsense, or not smaller: as measured.
        Assert.Same(cards, HardwareProbe.AsTested(cards, null));
        Assert.Same(cards, HardwareProbe.AsTested(cards, ""));
        Assert.Same(cards, HardwareProbe.AsTested(cards, "eight"));
        Assert.Same(cards, HardwareProbe.AsTested(cards, "-8188"));
        Assert.Same(cards, HardwareProbe.AsTested(cards, "24564"));
        Assert.Same(cards, HardwareProbe.AsTested(cards, "32000"));
        Assert.Empty(HardwareProbe.AsTested([], "8188"));

        // An 8 GB card with nothing else on the real one: what such a card has once Windows has its gigabyte.
        var small = Assert.Single(HardwareProbe.AsTested(cards, "8188"));
        Assert.Equal((8_188 * MiB, 7_164 * MiB, 1_024 * MiB), (small.TotalBytes, small.FreeBytes, small.UsedBytes));
        Assert.True(VideoMemory.IsSmallCard(small.TotalBytes));
        // With another program holding the rest of the real card, what is really free.
        var held = Assert.Single(HardwareProbe.AsTested([real with { UsedBytes = 17_600 * MiB, FreeBytes = 6_964 * MiB }], "8188"));
        Assert.Equal(6_964 * MiB, held.FreeBytes);
        Assert.Equal("DOGAI_TEST_CARD_MIB", HardwareProbe.TestCardVariable);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DogAI.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DogAI.sln was not found above the test folder.");
    }

    private sealed class Settings : ISettingsStore
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
}

/// <summary>
/// M21 step 5, the real guests: with the door closed (a video is being made) each runs on the processor and holds
/// nothing of the graphics card; with it open again each goes back at its next use. The speech model runs when
/// DOGAI_TEST_VOICE_DIR is a models folder with a Whisper model in its "voice" folder; the search model when
/// DOGAI_TEST_SEARCH_MODEL is an embedding model file and DOGAI_LLAMA_SERVER the engine. Run each by itself.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class CardGuestSmokeTests(ITestOutputHelper output)
{
    private const string Sentence = "The quick brown fox jumps over the lazy dog near the river bank.";

    [Fact]
    public async Task The_real_speech_model_runs_on_the_processor_while_the_door_is_closed_and_moves_back()
    {
        if (Environment.GetEnvironmentVariable("DOGAI_TEST_VOICE_DIR") is not { Length: > 0 } dir || !Directory.Exists(Path.Combine(dir, "voice"))) return;
        var settings = new VoiceFlowTests.MemorySettings(new AppSettings { ModelsDirectory = dir, SpeechLanguage = "en" });
        using var voices = new VoiceBox(settings);
        var windows = (await voices.VoicesAsync()).First(o => o.Id.Kind == VoiceKind.Windows);
        var clip = await voices.SynthesizeAsync(Sentence, windows.Id, 1f);
        var samples = Wav.Resample(clip.Samples, clip.SampleRate, VoiceActivity.SampleRate);
        var door = new CardDoor();
        using var ears = new WhisperRecognizer(settings, door);
        var probe = new HardwareProbe();
        var me = Environment.ProcessId;
        var before = await probe.VramOfProcessAsync(me);

        // A video is being made: the model loads for the processor and takes nothing of the card.
        door.Close();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var heard = await ears.RecognizeAsync(samples, "en");
        var onProcessor = clock.Elapsed;
        Assert.Contains("lazy dog", Transcript.Clean([heard.Text]).ToLowerInvariant());
        Assert.Equal("processor", ears.RunsOn);
        Assert.False(ears.HoldsMemory);
        Assert.Equal(CardGuestWords.SpeechOnProcessor, ears.Slower);
        var during = await probe.VramOfProcessAsync(me);
        output.WriteLine($"on the processor: {onProcessor.TotalSeconds:0.0} s for {clip.Duration.TotalSeconds:0.0} s of speech; this process holds {(during - before) / 1048576.0:0} MiB more of the card");
        Assert.InRange(during - before, -64L << 20, 64L << 20);

        // The video is done: the next thing said loads the model on the card again (where this PC has one for it).
        door.Open();
        Assert.Null(ears.Slower);
        heard = await ears.RecognizeAsync(samples, "en"); // the first run on a card also prepares its shaders
        clock.Restart();
        heard = await ears.RecognizeAsync(samples, "en");
        Assert.Contains("lazy dog", Transcript.Clean([heard.Text]).ToLowerInvariant());
        output.WriteLine($"with the door open: runs on the {ears.RunsOn}, {clock.Elapsed.TotalSeconds:0.0} s");
        if (ears.RunsOn == "graphics card")
        {
            Assert.True(ears.HoldsMemory);
            // Another video: it was idle, so the coordinator lets it go, and the next use is on the processor again.
            door.Close();
            Assert.True(await ears.LetGoAsync());
            Assert.False(ears.HoldsMemory);
            heard = await ears.RecognizeAsync(samples, "en");
            Assert.Equal("processor", ears.RunsOn);
            Assert.False(ears.HoldsMemory);
            Assert.Contains("lazy dog", Transcript.Clean([heard.Text]).ToLowerInvariant());
        }
    }

    [Fact]
    public async Task The_real_search_model_starts_on_the_processor_while_the_door_is_closed_and_moves_back()
    {
        if (Environment.GetEnvironmentVariable("DOGAI_TEST_SEARCH_MODEL") is not { Length: > 0 } model || !File.Exists(model)) return;
        if (Environment.GetEnvironmentVariable(LlamaServerLocator.OverrideVariable) is not { Length: > 0 }) return;
        var dir = Path.Combine(Path.GetTempPath(), "dogai-guest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new VoiceFlowTests.MemorySettings(new AppSettings { ModelsDirectory = dir, DefaultModels = new Dictionary<string, string> { [EmbeddingServer.UseCase] = model } });
            var door = new CardDoor();
            using var http = new LoopbackHttp();
            using var search = new EmbeddingServer(http, new LlamaServerLocator(new AppPaths(dir)), settings, new Records(), door);
            ICardGuest guest = search;
            var probe = new HardwareProbe();
            var passages = Enumerable.Range(0, 32).Select(i => $"Passage {i}. The lighthouse keeper climbed the worn stone stairs each evening, carrying oil for the lamp and a ledger for the ships that passed the reef.").ToList();

            // A video is being made: it starts on the processor, at once, and holds nothing of the card.
            door.Close();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var slow = await search.EmbedAsync(passages);
            output.WriteLine($"on the processor: {passages.Count} passages in {clock.Elapsed.TotalSeconds:0.0} s (with the start)");
            Assert.True(search.OnProcessor);
            Assert.False(guest.HoldsMemory);
            Assert.InRange(await probe.VramOfProcessAsync(search.ProcessId!.Value), 0, Trace);
            var first = search.ProcessId;

            // The video is done: the next batch starts it again on the card.
            door.Open();
            clock.Restart();
            var fast = await search.EmbedAsync(passages);
            output.WriteLine($"on the card: {passages.Count} passages in {clock.Elapsed.TotalSeconds:0.0} s (with the start)");
            Assert.False(search.OnProcessor);
            Assert.True(guest.HoldsMemory);
            Assert.NotEqual(first, search.ProcessId);
            if (await probe.MainGpuAsync() is not null) Assert.True(await probe.VramOfProcessAsync(search.ProcessId!.Value) > 0);
            // The same passages give the same vectors wherever they were made.
            for (var i = 0; i < passages.Count; i++) Assert.InRange(Cosine(slow[i], fast[i]), 0.999, 1.001);

            // Another video while it is indexing on the card (busy between batches, a batch on its way): it is not let
            // go as for a picture, but it steps off: the batch is answered, the program ends, the next batch is on the processor.
            search.KeepWhile = () => true;
            var batch = search.EmbedAsync(passages);
            door.Close();
            Assert.False(await search.LetGoAsync());
            Assert.True(await search.StepOffAsync());
            Assert.Equal(passages.Count, (await batch).Count);
            await search.EmbedAsync(passages.Take(2).ToList());
            Assert.True(search.OnProcessor);
            Assert.False(guest.HoldsMemory);
            Assert.InRange(await probe.VramOfProcessAsync(search.ProcessId!.Value), 0, Trace);
            search.KeepWhile = null;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Windows counts a few kilobytes of the card for any program that has a window station; a model is hundreds of megabytes.</summary>
    private const long Trace = 16L << 20;

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            aa += a[i] * (double)a[i];
            bb += b[i] * (double)b[i];
        }
        return dot / Math.Sqrt(aa * bb);
    }

    private sealed class Records : IModelRecordStore
    {
        public Task<IReadOnlyList<ModelRecord>> AllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelRecord>>([]);
        public Task<ModelRecord?> FindAsync(string path, CancellationToken ct = default) => Task.FromResult<ModelRecord?>(null);
        public Task SaveAsync(ModelRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }
}
