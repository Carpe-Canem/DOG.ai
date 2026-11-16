using System.Text;
using DogAI.Core;
using DogAI.Core.Chat;
using DogAI.Core.Models;
using DogAI.Core.Settings;
using DogAI.Core.Stories;
using DogAI.Data;
using DogAI.Engine;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>
/// Pictures with a real model on the real engine (M17 step 2). Skipped unless DOGAI_TEST_MODEL points at a chat model
/// with its projector beside it (a .gguf with "mmproj" in its name that belongs to it). DOGAI_LLAMA_SERVER picks the
/// engine; run it once with each build. DOGAI_TEST_OTHER_MODEL, when set, is a small model the projector does not
/// belong to, for the "loaded again without it" path. Check that graphics memory is free first.
/// </summary>
public class PictureInputSmokeTests
{
    private static (string Model, ProjectorFile Projector)? Setup()
    {
        var model = Environment.GetEnvironmentVariable("DOGAI_TEST_MODEL");
        if (string.IsNullOrEmpty(model) || ModelProjector.Find(model) is not { } projector) return null;
        return (model, projector);
    }

    private static EngineManager Engine(LoopbackHttp http, List<string>? output = null)
    {
        var engine = new EngineManager(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())));
        if (output is not null) engine.Output += line => { lock (output) output.Add(line); };
        return engine;
    }

    private static PromptPicture Picture(string name, long id = 1)
    {
        var jpeg = TestJpegs.Real(name);
        var (width, height) = AttachedPictures.SizeOf(jpeg);
        return new PromptPicture(id, jpeg, width, height);
    }

    private static async Task<(string Text, string Thinking, GenerationStats? Stats, string? ReplyId)> AskAsync(EngineManager engine,
        IReadOnlyList<PromptMessage> prompt, SamplerPreset preset)
    {
        StringBuilder text = new(), thinking = new();
        GenerationStats? stats = null;
        string? id = null;
        await foreach (var delta in engine.StreamChatAsync(prompt, preset))
        {
            text.Append(delta.Content);
            thinking.Append(delta.Reasoning);
            stats = delta.Stats ?? stats;
            id = delta.ReplyId ?? id;
        }
        return (text.ToString(), thinking.ToString(), stats, id);
    }

    private static readonly SamplerPreset Quiet = SamplerPresets.Get("precise") with { Thinking = false, MaxTokens = 40 };

    [Fact]
    public async Task A_real_model_with_its_projector_sees_counts_and_streams()
    {
        if (Setup() is not var (model, projector)) return;
        var output = new List<string>();
        using var http = new LoopbackHttp();
        using var engine = Engine(http, output);

        await engine.LoadAsync(model, new EngineOptions(8192, -1, projector.Path));

        Assert.True(engine.IsLoaded, engine.Error);
        Assert.True(engine.SeesPictures);
        Assert.Equal(projector.Path, engine.Projector);
        Assert.Null(engine.PictureInputProblem);
        Assert.Equal(8192, engine.ContextLength);

        // The engine's own count is the rule's, for a small, a wide and the largest picture.
        foreach (var name in new[] { TestJpegs.Red, TestJpegs.Sign, TestJpegs.Yellow })
        {
            var picture = Picture(name);
            Assert.Equal(AttachedPictures.TokensByRule(picture.Width, picture.Height), await engine.CountPictureTokensAsync(picture));
        }

        // A streamed answer about a picture; the prompt's size is the text's plus the picture's, as DOG.ai counts it.
        PromptMessage[] colour = [new("user", "Which colour fills this picture? Answer with one word.", [Picture(TestJpegs.Red)])];
        var red = await AskAsync(engine, colour, Quiet);
        Assert.Contains("red", red.Text, StringComparison.OrdinalIgnoreCase);
        var textOnly = await engine.CountTokensAsync(await engine.RenderPromptAsync([new("user", colour[0].Content)]));
        Assert.InRange(red.Stats!.PromptTokens, textOnly + 42 - 6, textOnly + 42 + 6);

        // Words in a picture, with text on both sides of it.
        PromptMessage[] sign = [new("user", "Look at this sign. What word is written on it? Answer with the word only.", [Picture(TestJpegs.Sign) with { After = 19 }])];
        Assert.Contains("WOOF", (await AskAsync(engine, sign, Quiet)).Text, StringComparison.OrdinalIgnoreCase);

        // Thinking with a budget and a picture in the prompt: the reply is named, thinks, and answers.
        var thinking = ThinkingLevels.Apply(SamplerPresets.Get("precise") with { MaxTokens = 2000 }, ThinkingLevel.Short);
        var thought = await AskAsync(engine, colour, thinking);
        Assert.NotNull(thought.ReplyId);
        Assert.NotEmpty(thought.Thinking);
        Assert.Contains("red", thought.Text, StringComparison.OrdinalIgnoreCase);

        // Answer now while it thinks about a picture.
        var unlimited = ThinkingLevels.Apply(SamplerPresets.Get("precise") with { MaxTokens = 3000 }, ThinkingLevel.NoLimit);
        PromptMessage[] hard = [new("user", "Study this sign closely. Reason step by step about its colours, shapes and letters, then describe it in two sentences.", [Picture(TestJpegs.Sign)])];
        string? replyId = null;
        var pieces = 0;
        ThinkingEnd? ended = null;
        var answer = new StringBuilder();
        await foreach (var delta in engine.StreamChatAsync(hard, unlimited))
        {
            replyId = delta.ReplyId ?? replyId;
            if (delta.Reasoning is not null && ++pieces == 40 && replyId is not null) ended = await engine.EndThinkingAsync(replyId);
            answer.Append(delta.Content);
        }
        Assert.Equal(ThinkingEnd.Ended, ended);
        Assert.True(answer.Length > 20, answer.ToString());

        // More than the context holds: the engine cuts nothing, and DOG.ai says so in words with both numbers.
        PromptMessage[] tooMuch = [new("user", "Compare these.", [.. Enumerable.Range(1, 9).Select(i => Picture(TestJpegs.Yellow, i))])];
        var refused = await Assert.ThrowsAsync<EngineException>(() => AskAsync(engine, tooMuch, Quiet));
        Assert.StartsWith("What was sent needs 9,", refused.Message);
        Assert.Contains("room for 8,192", refused.Message);
        // It goes on answering afterwards.
        Assert.Contains("red", (await AskAsync(engine, colour, Quiet)).Text, StringComparison.OrdinalIgnoreCase);

        await engine.UnloadAsync();
        Assert.False(engine.SeesPictures);
        // What the engine printed holds counts and timings, never a picture's bytes or the questions' words.
        string printed;
        lock (output) printed = string.Join("\n", output);
        Assert.DoesNotContain(Convert.ToBase64String(TestJpegs.Real(TestJpegs.Red))[..60], printed);
        Assert.DoesNotContain("Which colour fills", printed);
    }

    [Fact]
    public async Task The_chat_service_sends_pictures_to_a_real_model_and_carries_a_reply_on()
    {
        if (Setup() is not var (model, projector)) return;
        var root = Path.Combine(Path.GetTempPath(), "dogai-picture-smoke-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        try
        {
            await using var services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
            await services.MigrateDogDatabaseAsync();
            var store = services.GetRequiredService<IChatStore>();
            var options = new ChatOptions(services.GetRequiredService<ISettingsStore>());
            await options.SetGeneralAsync(ThinkingLevel.Off);
            using var http = new LoopbackHttp();
            using var engine = Engine(http);
            var records = services.GetRequiredService<IModelRecordStore>();
            var launcher = new ModelLauncher(engine, new HardwareProbe(), records) { MeasureDelay = TimeSpan.FromSeconds(2) };
            var chat = new ChatService(store, engine, chatOptions: options, attached: services.GetRequiredService<IAttachedPictureStore>());

            // Loaded the way the app loads it: the launcher finds the projector by itself and counts it.
            var decision = await launcher.DecideAsync(model, 8192);
            Assert.Equal(projector.Path, decision.Projector);
            await launcher.LoadAsync(model, 8192);
            Assert.True(engine.SeesPictures, engine.Error);

            var id = (await store.CreateAsync(new Conversation())).Id;
            await chat.SendAsync(id, "What word is written on this sign? Answer with the word only.", [TestJpegs.Real(TestJpegs.Sign)]);
            Assert.Null(chat.LastError);
            var shown = new MessageTree(await store.GetMessagesAsync(id)).PathTo((await store.GetAsync(id))!.ActiveLeafId);
            Assert.Contains("WOOF", shown[1].Content, StringComparison.OrdinalIgnoreCase);
            // The inspector's text names the picture with the engine's own count, and the size DOG.ai worked out is the engine's.
            Assert.Contains("[picture 1,024 × 640, 642 tokens]", shown[1].PromptText);
            Assert.Equal(642, chat.PictureTokens(shown[0].Pictures[0]));
            var counted = await engine.CountTokensAsync(shown[1].PromptText!.Replace("[picture 1,024 × 640, 642 tokens]", "")) + 642;
            Assert.InRange(shown[1].PromptTokens!.Value, counted - 6, counted + 6);

            // A second turn in words alone still knows the picture; a second picture joins it.
            await chat.SendAsync(id, "And which colour is the circle beside the word? One word.");
            shown = new MessageTree(await store.GetMessagesAsync(id)).PathTo((await store.GetAsync(id))!.ActiveLeafId);
            Assert.Contains("green", shown[3].Content, StringComparison.OrdinalIgnoreCase);
            await chat.SendAsync(id, "Which colour fills this second picture? One word.", [TestJpegs.Real(TestJpegs.Red)]);
            shown = new MessageTree(await store.GetMessagesAsync(id)).PathTo((await store.GetAsync(id))!.ActiveLeafId);
            Assert.Contains("red", shown[5].Content, StringComparison.OrdinalIgnoreCase);

            // Another answer to the same question, and a reply carried on (raw text with the pictures in place).
            await chat.RegenerateAsync(id, shown[5].Id);
            Assert.Null(chat.LastError);
            var long_ = (await store.CreateAsync(new Conversation())).Id;
            await chat.SendAsync(long_, "Describe this sign in three sentences.", [TestJpegs.Real(TestJpegs.Sign)]);
            var first = new MessageTree(await store.GetMessagesAsync(long_)).PathTo((await store.GetAsync(long_))!.ActiveLeafId)[1];
            await chat.ContinueAsync(long_, first.Id);
            Assert.Null(chat.LastError);
            var carried = (await store.GetMessagesAsync(long_)).Single(m => m.Id == first.Id);
            Assert.StartsWith(first.Content.TrimEnd(), carried.Content);

            // Answer now through the chat service, with a picture in the prompt.
            await options.SetGeneralAsync(ThinkingLevel.NoLimit);
            var thinking = (await store.CreateAsync(new Conversation())).Id;
            var sending = chat.SendAsync(thinking, "Study this sign closely. Reason step by step about its colours, shapes and letters, then describe it in two sentences.", [TestJpegs.Real(TestJpegs.Sign)]);
            for (var i = 0; i < 600 && !sending.IsCompleted && chat.Active?.CanAnswerNow != true; i++) await Task.Delay(50);
            Assert.True(chat.Active?.CanAnswerNow, "the reply never started thinking");
            await Task.Delay(500);
            await chat.AnswerNowAsync();
            await sending;
            Assert.Null(chat.LastError);
            var answered = new MessageTree(await store.GetMessagesAsync(thinking)).PathTo((await store.GetAsync(thinking))!.ActiveLeafId)[1];
            Assert.True(StoryTurn.Of(answered).Thinking!.AnswerNow);
            Assert.True(answered.Content.Length > 20, answered.Content);

            // What the model really holds with its projector is remembered as such.
            await Task.Delay(3000);
            if ((await records.FindAsync(model)) is { MeasuredVramBytes: > 0 } record)
            {
                Assert.True(record.MeasuredVramProjector);
                Assert.Null(record.MeasuredAt(8192));
                Assert.NotNull(record.MeasuredAt(8192, withProjector: true));
            }
            await engine.UnloadAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Without_its_projector_the_model_is_loaded_as_before_and_is_told_a_picture_was_there()
    {
        if (Setup() is not var (model, _)) return;
        var root = Path.Combine(Path.GetTempPath(), "dogai-picture-smoke-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        try
        {
            await using var services = new ServiceCollection().AddSingleton(paths).AddDogData().BuildServiceProvider();
            await services.MigrateDogDatabaseAsync();
            var store = services.GetRequiredService<IChatStore>();
            var options = new ChatOptions(services.GetRequiredService<ISettingsStore>());
            await options.SetGeneralAsync(ThinkingLevel.Off);
            using var http = new LoopbackHttp();
            using var engine = Engine(http);
            var chat = new ChatService(store, engine, chatOptions: options, attached: services.GetRequiredService<IAttachedPictureStore>());

            await engine.LoadAsync(model, new EngineOptions(4096));

            Assert.True(engine.IsLoaded, engine.Error);
            Assert.False(engine.SeesPictures);
            Assert.Null(engine.Projector);
            Assert.Null(await engine.CountPictureTokensAsync(Picture(TestJpegs.Red)));
            // Given a picture all the same, the engine refuses; DOG.ai never does that: it sends a note instead.
            await Assert.ThrowsAsync<EngineException>(() => AskAsync(engine, [new("user", "What is this?", [Picture(TestJpegs.Red)])], Quiet));

            var id = (await store.CreateAsync(new Conversation())).Id;
            await chat.SendAsync(id, "Can you see the picture I attached? Answer yes or no.", [TestJpegs.Real(TestJpegs.Red)]);
            Assert.Null(chat.LastError);
            var shown = new MessageTree(await store.GetMessagesAsync(id)).PathTo((await store.GetAsync(id))!.ActiveLeafId);
            Assert.Single(shown[0].Pictures);
            Assert.Contains(AttachedPictures.UnseenNote, shown[1].PromptText);
            Assert.Equal(1, StoryTurn.Of(shown[1]).Unseen);
            Assert.Contains("no", shown[1].Content, StringComparison.OrdinalIgnoreCase);
            await engine.UnloadAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A page's own load stopped for a picture leaves the model set aside before the engine could say whether it sees.
    /// It has its projector, so it counts as seeing until it is back (found by the M17 review: the chat box and Write it
    /// for me said "cannot see pictures" of such a model).
    /// </summary>
    [Fact]
    public async Task A_model_set_aside_while_loading_still_counts_as_seeing()
    {
        if (Setup() is not var (model, projector)) return;
        using var http = new LoopbackHttp();
        using var engine = Engine(http);

        var pageLoad = engine.LoadAsync(model, new EngineOptions(4096, -1, projector.Path), automatic: true);
        for (var i = 0; i < 200 && engine.State != EngineState.Loading; i++) await Task.Delay(25);
        await Task.Delay(1000);
        Assert.True(await engine.SetAsideLoadAsync());
        await pageLoad.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(EngineState.Resting, engine.State);
        Assert.True(engine.SeesPictures);
        // The next request brings it in with the projector, and the engine's own word stands.
        Assert.InRange(await engine.CountTokensAsync("Hello there, how are you?"), 3, 12);
        Assert.True(engine.IsLoaded, engine.Error);
        Assert.True(engine.SeesPictures);
        Assert.Equal(projector.Path, engine.Projector);
        await engine.UnloadAsync();

        // Without a projector the same set-aside model does not.
        var blind = engine.LoadAsync(model, new EngineOptions(4096), automatic: true);
        for (var i = 0; i < 200 && engine.State != EngineState.Loading; i++) await Task.Delay(25);
        await Task.Delay(1000);
        Assert.True(await engine.SetAsideLoadAsync());
        await blind.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(engine.SeesPictures);
        await engine.UnloadAsync();
    }

    [Fact]
    public async Task A_projector_the_engine_stops_with_is_left_out_and_the_model_loads_without_it()
    {
        if (Setup() is not var (_, projector)) return;
        var other = Environment.GetEnvironmentVariable("DOGAI_TEST_OTHER_MODEL");
        if (string.IsNullOrEmpty(other)) return;
        // DOG.ai's own rule would never pass it: the two files' headers do not match.
        if (Path.GetDirectoryName(other) == Path.GetDirectoryName(projector.Path)) Assert.Null(ModelProjector.Find(other));
        using var http = new LoopbackHttp();
        using var engine = Engine(http);

        await engine.LoadAsync(other, new EngineOptions(2048, -1, projector.Path));

        Assert.True(engine.IsLoaded, engine.Error);
        Assert.False(engine.SeesPictures);
        Assert.Null(engine.Projector);
        Assert.Contains("was loaded without picture input", engine.PictureInputProblem);
        Assert.Contains(projector.Name, engine.PictureInputProblem);
        Assert.InRange(await engine.CountTokensAsync("Hello there, how are you?"), 3, 12);
        await engine.UnloadAsync();
    }
}
