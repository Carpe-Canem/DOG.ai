using System.Text;
using DogAI.Core;
using DogAI.Core.Chat;
using DogAI.Engine;

namespace DogAI.Tests;

/// <summary>
/// Runs a real model through the real engine. Skipped unless DOGAI_TEST_MODEL points at a GGUF file,
/// because it needs llama-server installed and several GB of memory.
/// </summary>
public class EngineSmokeTests
{
    /// <summary>
    /// A model a page loads by itself can be set aside before it finishes (a picture does this instead of waiting for it),
    /// and the next request loads it. A load the owner started is never stopped this way.
    /// </summary>
    [Fact]
    public async Task A_load_nobody_asked_for_can_be_set_aside_before_it_finishes()
    {
        var model = Environment.GetEnvironmentVariable("DOGAI_TEST_MODEL");
        if (string.IsNullOrEmpty(model)) return;

        using var http = new LoopbackHttp();
        using var engine = new EngineManager(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())));
        async Task Loading()
        {
            for (var i = 0; i < 200 && engine.State != EngineState.Loading; i++) await Task.Delay(25);
            Assert.Equal(EngineState.Loading, engine.State);
            await Task.Delay(1500); // well into the load: the program is running and reading the model
        }

        Assert.False(await engine.SetAsideLoadAsync()); // nothing is loading

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var pageLoad = engine.LoadAsync(model, new EngineOptions(ContextLength: 4096), automatic: true);
        await Loading();
        Assert.True(await engine.SetAsideLoadAsync());
        await pageLoad.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), clock.Elapsed.ToString());
        Assert.Equal(EngineState.Resting, engine.State);
        Assert.Equal(model, engine.ModelPath);
        Assert.Null(engine.ProcessId);
        Assert.True(engine.IsReady);
        Assert.False(engine.IsLoaded);
        Assert.Equal(4096, engine.ContextLength);

        // The next request brings it in, with the settings it was given.
        Assert.InRange(await engine.CountTokensAsync("Hello there, how are you?"), 3, 12);
        Assert.True(engine.IsLoaded, engine.Error);
        Assert.Equal(4096, engine.ContextLength);

        // The owner's own load is never stopped.
        await engine.UnloadAsync();
        var ownerLoad = engine.LoadAsync(model, new EngineOptions(ContextLength: 4096));
        await Loading();
        Assert.False(await engine.SetAsideLoadAsync());
        await ownerLoad;
        Assert.True(engine.IsLoaded, engine.Error);
        await engine.UnloadAsync();
        Assert.Equal(EngineState.Idle, engine.State);
    }

    [Fact]
    public async Task Loads_a_model_streams_a_reply_and_unloads()
    {
        var model = Environment.GetEnvironmentVariable("DOGAI_TEST_MODEL");
        if (string.IsNullOrEmpty(model)) return;

        using var http = new LoopbackHttp();
        using var engine = new EngineManager(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())));

        await engine.LoadAsync(model, new EngineOptions(ContextLength: 4096));
        Assert.True(engine.IsReady, engine.Error);
        Assert.Equal(4096, engine.ContextLength);
        Assert.False(string.IsNullOrEmpty(engine.ModelName));

        Assert.InRange(await engine.CountTokensAsync("Hello there, how are you?"), 3, 12);

        PromptMessage[] prompt = [new("system", "You are terse."), new("user", "Reply with the single word: woof")];
        var text = await engine.RenderPromptAsync(prompt);
        Assert.Contains("Reply with the single word: woof", text);
        Assert.Contains("You are terse.", text);

        var reply = new StringBuilder();
        GenerationStats? stats = null;
        await foreach (var delta in engine.StreamChatAsync(prompt, SamplerPresets.Get("precise")))
        {
            reply.Append(delta.Content);
            stats = delta.Stats ?? stats;
        }
        // What it says is up to the model (a roleplay model will not obey "one word"); only that it spoke matters.
        Assert.False(string.IsNullOrWhiteSpace(reply.ToString()));
        Assert.NotNull(stats);
        Assert.True(stats.PromptTokens > 0 && stats.CompletionTokens > 0 && stats.TokensPerSecond > 0, stats.ToString());

        // Continue: raw text through /completion carries on mid-reply instead of starting a new turn. The template is
        // asked to close the model's thinking first (as ChatService does for Continue): left open, a thinking model
        // takes the text so far for its thoughts and writes "</think>" and the answer again (M17 step 6).
        var start = await engine.RenderPromptAsync([new("user", "Count from one to ten in words, separated by commas.")], thinking: false);
        var more = new StringBuilder();
        GenerationStats? moreStats = null;
        await foreach (var delta in engine.StreamCompletionAsync(start + "one, two, three,", SamplerPresets.Get("precise")))
        {
            more.Append(delta.Content);
            moreStats = delta.Stats ?? moreStats;
        }
        Assert.Contains("four", more.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</think>", more.ToString());
        Assert.DoesNotContain("one, two", more.ToString(), StringComparison.OrdinalIgnoreCase); // carried on, not begun again
        Assert.NotNull(moreStats);
        Assert.True(moreStats.CompletionTokens > 0 && moreStats.PromptTokens > 0, moreStats.ToString());

        // A character chat's prompt: system, the "start" user turn, the greeting, then the user. Strict templates
        // (Mistral, Gemma) must accept this shape.
        PromptMessage[] character =
        [
            new("system", "You are Rex, a dog."), new("user", Core.Characters.CharacterPrompt.ChatStart),
            new("assistant", "*wags* Hello!"), new("user", "Good boy."),
        ];
        Assert.Contains("*wags* Hello!", await engine.RenderPromptAsync(character));
        var said = new StringBuilder();
        // Asked without thinking, as every real-engine test is: at 4,096 tokens a model that thinks first can spend all
        // its room on it and answer nothing (this line failed that way with Huihui Qwen3.5 9B).
        await foreach (var delta in engine.StreamChatAsync(character, SamplerPresets.Get("precise") with { Thinking = false }))
            said.Append(delta.Content);
        Assert.False(string.IsNullOrWhiteSpace(said.ToString()));

        await engine.UnloadAsync();
        Assert.Equal(EngineState.Idle, engine.State);
        await Assert.ThrowsAsync<EngineException>(() => engine.CountTokensAsync("x"));
    }

    [Fact]
    public async Task A_file_that_is_not_a_model_fails_with_a_plain_message()
    {
        var path = Path.Combine(Path.GetTempPath(), "dogai-not-a-model-" + Guid.NewGuid().ToString("N") + ".gguf");
        await File.WriteAllTextAsync(path, "this is not a model");
        try
        {
            using var http = new LoopbackHttp();
            using var engine = new EngineManager(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())));
            await engine.LoadAsync(path, new EngineOptions());

            Assert.Equal(EngineState.Failed, engine.State);
            Assert.Contains("not a GGUF", engine.Error);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
