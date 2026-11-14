using DogAI.Core;
using DogAI.Core.Images;
using DogAI.Engine;
using DogAI.Engine.Images;

namespace DogAI.Tests;

/// <summary>
/// The chat model as the card coordinator sees it, behaving like the engine manager: a model that was set aside comes
/// back with the next request, after taking a turn on the card; a model the owner unloaded does not.
/// </summary>
internal sealed class FakeChatModel : IChatModelSlot
{
    public List<string> Events { get; init; } = [];
    public bool IsLoaded { get; private set; }
    public bool IsResting { get; private set; }
    public bool IsBusy { get; set; }
    public CardTurn? CardTurn { get; set; }
    public int Rests { get; private set; }
    public int Loads { get; private set; }

    /// <summary>A model that was loaded before the test starts.</summary>
    public FakeChatModel Loaded()
    {
        IsLoaded = true;
        return this;
    }

    public Task<bool> RestAsync()
    {
        if (!IsLoaded || IsBusy) return Task.FromResult(false);
        IsLoaded = false;
        IsResting = true;
        Rests++;
        lock (Events) Events.Add("rest");
        return Task.FromResult(true);
    }

    /// <summary>A chat request. False when no model is loaded ("No model is loaded").</summary>
    public async Task<bool> AskAsync(CancellationToken ct = default)
    {
        if (IsResting)
        {
            using var turn = await CardTurn!(true, ct);
            if (IsResting) Load();
        }
        if (!IsLoaded) return false;
        lock (Events) Events.Add("reply");
        return true;
    }

    /// <summary>The owner loads a model in the model picker.</summary>
    public async Task LoadByHandAsync(CancellationToken ct = default)
    {
        using var turn = await CardTurn!(false, ct);
        Load();
    }

    /// <summary>The owner unloads the model; one that was set aside is forgotten too.</summary>
    public void UnloadByOwner()
    {
        IsLoaded = false;
        IsResting = false;
        lock (Events) Events.Add("unload-chat");
    }

    private void Load()
    {
        IsResting = false;
        IsLoaded = true;
        Loads++;
        lock (Events) Events.Add("load");
    }
}

/// <summary>A 24 GB card whose free memory follows what the two fakes hold.</summary>
internal sealed class FakeCard(FakeChatModel chat, FakeImageEngine images) : ICardMeter
{
    public const long Gb = 1L << 30;
    public long Total { get; set; } = 24 * Gb;

    /// <summary>Windows and other programs.</summary>
    public long Other { get; set; } = 1 * Gb;
    public long ChatBytes { get; set; } = 18 * Gb;
    public long PictureBytes { get; set; } = 8 * Gb;
    public bool Unmeasurable { get; set; }

    public Task<CardMemory?> ReadAsync(CancellationToken ct = default)
    {
        if (Unmeasurable) return Task.FromResult<CardMemory?>(null);
        var c = chat.IsLoaded ? ChatBytes : 0;
        var p = images.IsRunning ? PictureBytes : 0;
        return Task.FromResult<CardMemory?>(new CardMemory(Total, Total - Other - c - p, p, c));
    }
}

public class PictureMemoryTests
{
    private const long Gb = FakeCard.Gb;
    private const long Checkpoint = (long)(6.46 * Gb); // the trial's checkpoint

    [Fact]
    public void The_estimate_matches_the_trial_and_errs_high()
    {
        // Measured: about 11.5 GB at the peak of a Standard picture, about 17 GB for Fine (hires fix ×1.5).
        var standard = PictureMemory.Need(Checkpoint, 0, new ImageRequest("x", Width: 832, Height: 1216, Steps: 28));
        var fine = PictureMemory.Need(Checkpoint, 0, new ImageRequest("x", Width: 832, Height: 1216, Steps: 28, Hires: new HiresFix(1.5, 14, 0.5)));
        Assert.InRange(standard, (long)(11.5 * Gb), 13 * Gb);
        Assert.InRange(fine, 17 * Gb, (long)(18.5 * Gb));
        // LoRAs add their size; an unknown checkpoint counts as a usual one.
        Assert.Equal(standard + Gb, PictureMemory.Need(Checkpoint, Gb, new ImageRequest("x", Width: 832, Height: 1216)));
        Assert.True(PictureMemory.Need(0, 0, new ImageRequest("x")) > PictureMemory.TypicalCheckpointBytes);
    }

    [Fact]
    public void Fitting_needs_the_margin_counts_what_the_picture_model_already_holds_and_never_trusts_an_unmeasured_card()
    {
        var need = 12 * Gb;
        Assert.True(PictureMemory.FitsBeside(new CardMemory(24 * Gb, 13 * Gb), need));
        Assert.False(PictureMemory.FitsBeside(new CardMemory(24 * Gb, 13 * Gb - 1), need));
        // The picture model already holds 8 GB of the 12, so 5 GB free is enough.
        Assert.True(PictureMemory.FitsBeside(new CardMemory(24 * Gb, 5 * Gb, PictureBytes: 8 * Gb), need));
        Assert.False(PictureMemory.FitsBeside(new CardMemory(24 * Gb, 4 * Gb, PictureBytes: 8 * Gb), need));
        Assert.False(PictureMemory.FitsBeside(null, need));
    }
}

public class CardCoordinatorTests
{
    private const long Gb = FakeCard.Gb;
    private static readonly ImageModel Model = new(@"D:\m\c.safetensors", @"D:\m\loras");
    private static readonly ImageRequest Standard = new("1girl", Width: 832, Height: 1216, Steps: 3);
    private static readonly ImageRequest Fine = Standard with { Hires = new HiresFix(1.5, 2, 0.5) };

    private sealed class Rig
    {
        public List<string> Events { get; } = [];
        public FakeChatModel Chat { get; }
        public FakeImageEngine Images { get; }
        public FakeCard Card { get; }
        public CardCoordinator Coordinator { get; }
        public List<string> Notes { get; } = [];

        public Rig(bool chatLoaded = true, long chatBytes = 18 * Gb)
        {
            Chat = new FakeChatModel { Events = Events };
            if (chatLoaded) Chat.Loaded();
            Images = new FakeImageEngine { Events = Events };
            Card = new FakeCard(Chat, Images) { ChatBytes = chatBytes };
            Coordinator = new CardCoordinator(Images, Chat, Card, path => path.EndsWith("c.safetensors", StringComparison.Ordinal) ? (long)(6.5 * Gb) : Gb / 5)
            {
                PollInterval = TimeSpan.FromMilliseconds(5),
            };
            Coordinator.Noted += Notes.Add;
        }

        public async Task<Rig> ChosenAsync()
        {
            await Coordinator.LoadAsync(Model);
            return this;
        }

        public string Order() { lock (Events) return string.Join(" ", Events); }
    }

    [Fact]
    public async Task When_both_fit_the_chat_model_stays_loaded()
    {
        var rig = await new Rig(chatBytes: 8 * Gb).ChosenAsync();

        await rig.Coordinator.DrawAsync(Standard);
        // The second picture counts what the picture model already holds, so it still fits.
        await rig.Coordinator.DrawAsync(Standard);

        Assert.True(rig.Chat.IsLoaded);
        Assert.Equal(0, rig.Chat.Rests);
        Assert.Equal(CardChoice.KeptBoth, rig.Coordinator.LastDecision!.Choice);
        Assert.Equal("draw drawn draw drawn", rig.Order());
        Assert.Contains(rig.Notes, n => n.Contains("stays loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task When_they_do_not_fit_the_chat_model_is_set_aside_and_not_loaded_again_straight_away()
    {
        var rig = await new Rig().ChosenAsync();

        var result = await rig.Coordinator.DrawAsync(Standard);

        Assert.Single(result.Images);
        Assert.Equal("rest draw drawn", rig.Order());
        Assert.False(rig.Chat.IsLoaded);
        Assert.True(rig.Chat.IsResting);
        Assert.Equal(0, rig.Chat.Loads);
        var decision = rig.Coordinator.LastDecision!;
        Assert.Equal(CardChoice.SetChatAside, decision.Choice);
        Assert.Equal(5 * Gb, decision.FreeBytes);
        Assert.Contains(rig.Notes, n => n.Contains("set aside", StringComparison.Ordinal) && n.Contains("18.0 GB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_of_pictures_costs_one_swap_and_the_next_message_brings_the_chat_model_back()
    {
        var rig = await new Rig().ChosenAsync();

        await rig.Coordinator.DrawAsync(Standard);
        await rig.Coordinator.DrawAsync(Standard with { Seed = 2 }); // "Try again"
        await rig.Coordinator.DrawAsync(Fine);
        Assert.Equal(1, rig.Chat.Rests);
        Assert.Equal(0, rig.Chat.Loads);
        Assert.Equal(CardChoice.NothingLoaded, rig.Coordinator.LastDecision!.Choice);

        Assert.True(await rig.Chat.AskAsync());

        Assert.Equal(1, rig.Chat.Loads);
        Assert.True(rig.Chat.IsLoaded);
        // The picture model's 8 GB had to go first: the chat model needs 18 of the card's 24.
        Assert.False(rig.Images.IsRunning);
        Assert.Equal(Model, rig.Coordinator.Model);
        Assert.Equal("rest draw drawn draw drawn draw drawn release load reply", rig.Order());

        // And the next picture swaps again, drawing with the checkpoint that stayed chosen.
        await rig.Coordinator.DrawAsync(Standard);
        Assert.Equal(2, rig.Chat.Rests);
    }

    [Fact]
    public async Task The_picture_model_stays_when_the_returning_chat_model_fits_beside_it()
    {
        // Another program held 9 GB when the picture was asked for, so the 8 GB chat model had to make room...
        var rig = await new Rig(chatBytes: 8 * Gb).ChosenAsync();
        rig.Card.Other = 9 * Gb;
        await rig.Coordinator.DrawAsync(Standard);
        Assert.True(rig.Chat.IsResting);

        // ...and has closed by the time the next message is sent.
        rig.Card.Other = 1 * Gb;
        Assert.True(await rig.Chat.AskAsync());

        Assert.True(rig.Images.IsRunning);
        Assert.Equal("rest draw drawn load reply", rig.Order());
    }

    [Fact]
    public async Task A_fine_picture_can_need_the_swap_when_a_standard_one_does_not()
    {
        var rig = await new Rig(chatBytes: 8 * Gb).ChosenAsync();

        await rig.Coordinator.DrawAsync(Standard);
        Assert.True(rig.Chat.IsLoaded);

        await rig.Coordinator.DrawAsync(Fine);
        Assert.True(rig.Chat.IsResting);
    }

    [Fact]
    public async Task A_card_that_cannot_be_measured_gets_the_swap()
    {
        var rig = await new Rig(chatBytes: 2 * Gb).ChosenAsync();
        rig.Card.Unmeasurable = true;

        await rig.Coordinator.DrawAsync(Standard);

        Assert.True(rig.Chat.IsResting);
        Assert.Equal(-1, rig.Coordinator.LastDecision!.FreeBytes);
        // Coming back, its size is unknown, so the picture model is let go.
        Assert.True(await rig.Chat.AskAsync());
        Assert.False(rig.Images.IsRunning);
    }

    [Fact]
    public async Task A_chat_model_the_owner_unloaded_stays_unloaded()
    {
        // Unloaded before any picture: the coordinator has nothing to set aside and loads nothing afterwards.
        var rig = await new Rig(chatLoaded: false).ChosenAsync();
        await rig.Coordinator.DrawAsync(Standard);
        Assert.Equal(CardChoice.NothingLoaded, rig.Coordinator.LastDecision!.Choice);
        Assert.False(await rig.Chat.AskAsync());
        Assert.Equal(0, rig.Chat.Rests + rig.Chat.Loads);
        Assert.Equal("draw drawn", rig.Order());

        // Unloaded by the owner while it was set aside: it does not come back with the next message, or after pictures.
        rig = await new Rig().ChosenAsync();
        await rig.Coordinator.DrawAsync(Standard);
        rig.Chat.UnloadByOwner();
        await rig.Coordinator.DrawAsync(Standard);
        Assert.False(await rig.Chat.AskAsync());
        Assert.Equal(0, rig.Chat.Loads);
        Assert.False(rig.Chat.IsLoaded);
        Assert.True(rig.Images.IsRunning);
    }

    [Fact]
    public async Task A_chat_request_during_a_picture_waits_for_it_and_is_then_answered()
    {
        var rig = await new Rig().ChosenAsync();
        rig.Images.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var picture = rig.Coordinator.DrawAsync(Standard);
        await rig.Images.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var asked = rig.Chat.AskAsync();
        await Task.Delay(60);

        // The picture is still being drawn; the chat model has not been loaded under it.
        Assert.False(asked.IsCompleted);
        Assert.False(rig.Chat.IsLoaded);
        Assert.Equal("rest draw", rig.Order());

        rig.Images.Hold.SetResult();
        await picture.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await asked.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("rest draw drawn release load reply", rig.Order());
    }

    [Fact]
    public async Task A_waiting_chat_request_can_be_stopped()
    {
        var rig = await new Rig().ChosenAsync();
        rig.Images.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var picture = rig.Coordinator.DrawAsync(Standard);
        await rig.Images.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var stop = new CancellationTokenSource();
        var asked = rig.Chat.AskAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asked);

        rig.Images.Hold.SetResult();
        await picture.WaitAsync(TimeSpan.FromSeconds(5));
        // Nothing was loaded for the stopped request; the model is still set aside for the next one.
        Assert.True(rig.Chat.IsResting);
        Assert.True(await rig.Chat.AskAsync());
    }

    [Fact]
    public async Task A_model_loaded_by_hand_during_a_picture_waits_and_gets_the_card_free_of_the_picture_model()
    {
        var rig = await new Rig(chatLoaded: false).ChosenAsync();
        rig.Images.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var picture = rig.Coordinator.DrawAsync(Standard);
        await rig.Images.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var load = rig.Chat.LoadByHandAsync();
        await Task.Delay(60);
        Assert.False(load.IsCompleted);

        rig.Images.Hold.SetResult();
        await picture.WaitAsync(TimeSpan.FromSeconds(5));
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("draw drawn release load", rig.Order());
        Assert.False(rig.Images.IsRunning);
    }

    [Fact]
    public async Task A_picture_waits_for_a_reply_being_written_and_counts_as_busy_all_along()
    {
        var rig = await new Rig().ChosenAsync();
        rig.Chat.IsBusy = true;
        var progress = new Collect<ImageProgress>();
        Assert.False(rig.Coordinator.IsBusy);

        var picture = rig.Coordinator.DrawAsync(Standard, progress);
        await Task.Delay(60);

        // The reply is not cut off: the model is still loaded and nothing is being drawn. The idle lock sees "busy".
        Assert.True(rig.Chat.IsLoaded);
        Assert.Empty(rig.Images.Requests);
        Assert.True(rig.Coordinator.IsBusy);
        Assert.False(picture.IsCompleted);

        rig.Chat.IsBusy = false;
        await picture.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("rest draw drawn", rig.Order());
        Assert.False(rig.Coordinator.IsBusy);
        var stages = progress.Items.Select(p => p.Stage).Distinct().ToList();
        Assert.Equal([CardCoordinator.MakingRoom, CardCoordinator.Waiting, "sampling"], stages);
    }

    [Fact]
    public async Task Cancelling_a_waiting_picture_leaves_the_chat_model_alone()
    {
        var rig = await new Rig().ChosenAsync();
        rig.Chat.IsBusy = true;
        using var cancel = new CancellationTokenSource();

        var picture = rig.Coordinator.DrawAsync(Standard, null, cancel.Token);
        await Task.Delay(30);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => picture);
        Assert.True(rig.Chat.IsLoaded);
        Assert.Equal(0, rig.Chat.Rests);
        Assert.False(rig.Coordinator.IsBusy);
        // The card is not left taken: the next picture goes ahead.
        rig.Chat.IsBusy = false;
        await rig.Coordinator.DrawAsync(Standard).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task One_picture_at_a_time()
    {
        var rig = await new Rig(chatLoaded: false).ChosenAsync();
        rig.Images.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = rig.Coordinator.DrawAsync(Standard);
        await rig.Images.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var progress = new Collect<ImageProgress>();

        var second = rig.Coordinator.DrawAsync(Standard with { Seed = 2 }, progress);
        await Task.Delay(40);
        Assert.Equal("draw", rig.Order());
        Assert.Equal(CardCoordinator.Waiting, progress.Items[0].Stage);

        var hold = rig.Images.Hold;
        rig.Images.Hold = null;
        hold.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("draw drawn draw drawn", rig.Order());
        Assert.False(rig.Coordinator.IsBusy);
    }

    [Fact]
    public async Task A_picture_that_cannot_be_drawn_moves_nothing()
    {
        var rig = new Rig();
        // No checkpoint chosen.
        var e = await Assert.ThrowsAsync<ImageEngineException>(() => rig.Coordinator.DrawAsync(Standard));
        Assert.Contains("No picture model", e.Message);

        await rig.Coordinator.LoadAsync(Model);
        e = await Assert.ThrowsAsync<ImageEngineException>(() => rig.Coordinator.DrawAsync(Standard with { Prompt = "" }));
        Assert.Contains("prompt is empty", e.Message);

        Assert.True(rig.Chat.IsLoaded);
        Assert.Equal(0, rig.Chat.Rests);
        Assert.False(rig.Coordinator.IsBusy);
        Assert.Equal("", rig.Order());
    }

    [Fact]
    public async Task Loras_count_towards_what_a_picture_needs()
    {
        // 8 GB chat model and 2 GB of other programs: a plain Standard picture just fits (12.2 + 1 against 14 GB free)...
        var rig = await new Rig(chatBytes: 8 * Gb).ChosenAsync();
        rig.Card.Other = 2 * Gb;
        await rig.Coordinator.DrawAsync(Standard);
        Assert.True(rig.Chat.IsLoaded);
        await rig.Coordinator.ReleaseAsync();

        // ...but not with five LoRAs of 0.2 GB each.
        var loras = Enumerable.Range(1, 5).Select(i => new LoraUse($"l{i}.safetensors")).ToList();
        await rig.Coordinator.DrawAsync(Standard with { Loras = loras });
        Assert.True(rig.Chat.IsResting);
    }

    [Fact]
    public async Task Unloading_the_picture_model_does_not_wait_for_the_picture()
    {
        var rig = await new Rig(chatLoaded: false).ChosenAsync();
        rig.Images.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var picture = rig.Coordinator.DrawAsync(Standard);
        await rig.Images.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await rig.Coordinator.UnloadAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(rig.Coordinator.Model);

        rig.Images.Hold.SetResult();
        await picture.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

/// <summary>The engine manager's side of sharing the card, as far as it goes without a real model.</summary>
public class EngineRestTests
{
    [Fact]
    public async Task With_no_model_loaded_there_is_nothing_to_set_aside_and_nothing_comes_back()
    {
        using var http = new LoopbackHttp();
        using var engine = new EngineManager(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())));

        Assert.False(await engine.RestAsync());
        Assert.Equal(EngineState.Idle, engine.State);
        Assert.False(engine.IsReady);
        Assert.False(engine.IsResting);
        await Assert.ThrowsAsync<EngineException>(() => engine.CountTokensAsync("x"));
        await Assert.ThrowsAsync<EngineException>(async () =>
        {
            await foreach (var _ in engine.StreamChatAsync([new("user", "hi")], DogAI.Core.Chat.SamplerPresets.Get("precise"))) { }
        });
        Assert.False(engine.IsBusy);
    }

    [Fact]
    public async Task Loads_take_a_turn_on_the_card_through_the_coordinator()
    {
        using var http = new LoopbackHttp();
        using var engine = new EngineManager(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())));
        // Without a coordinator there is nothing to wait for.
        (await engine.TakeCardTurnAsync(waking: false)).Dispose();

        var images = new FakeImageEngine();
        var coordinator = new CardCoordinator(images, engine, new FakeCard(new FakeChatModel(), images), _ => 0);
        Assert.NotNull(engine.CardTurn);
        await coordinator.LoadAsync(new ImageModel("c.safetensors"));
        images.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var picture = coordinator.DrawAsync(new ImageRequest("x", Steps: 2));
        await images.Held.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // A load asked for now (the model picker, a benchmark) waits until the picture is finished.
        var turn = engine.TakeCardTurnAsync(waking: false);
        await Task.Delay(40);
        Assert.False(turn.IsCompleted);
        images.Hold.SetResult();
        await picture.WaitAsync(TimeSpan.FromSeconds(5));
        // The picture model was let go for the load, and while the load has the card a picture waits in its turn.
        images.Hold = null;
        Task<ImageResult> next;
        using (await turn.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.False(images.IsRunning);
            next = coordinator.DrawAsync(new ImageRequest("x", Steps: 2));
            await Task.Delay(40);
            Assert.False(next.IsCompleted);
            Assert.Single(images.Requests);
        }
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, images.Requests.Count);
    }

    [Fact]
    public void The_picture_model_is_let_go_after_two_idle_minutes()
    {
        var root = Path.Combine(Path.GetTempPath(), "dogai-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(root);
            using var http = new LoopbackHttp();
            using var server = new ImageServer(http, new SdServerLocator(paths), paths);
            Assert.Equal(TimeSpan.FromMinutes(2), server.IdleStop);
            Assert.False(server.IsRunning);
            Assert.Null(server.ProcessId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
