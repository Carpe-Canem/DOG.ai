using DogAI.Core.Chat;
using DogAI.Core.Stories;
using DogAI.Engine;

namespace DogAI.Tests;

/// <summary>A thinking model is asked to answer straight away in stories, unless the owner allows thinking.</summary>
public sealed class StoryThinkingRequestTests
{
    private static readonly PromptMessage[] Messages = [new("user", "Hi")];

    [Fact]
    public void Turning_thinking_off_reaches_the_chat_template_and_otherwise_nothing_is_sent()
    {
        var off = LlamaApi.ChatRequestBody(Messages, SamplerPresets.Get(null) with { Thinking = false });
        Assert.False(off["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.False(off["chat_template_kwargs"]!["thinking"]!.GetValue<bool>());

        Assert.Null(LlamaApi.ChatRequestBody(Messages, SamplerPresets.Get(null))["chat_template_kwargs"]);
        Assert.Null(LlamaApi.ChatRequestBody(Messages, SamplerPresets.Get(null) with { Thinking = true })["chat_template_kwargs"]);
    }

    [Fact]
    public void The_switch_is_never_saved_with_a_style() =>
        Assert.DoesNotContain("Thinking", System.Text.Json.JsonSerializer.Serialize(SamplerPresets.Get(null) with { Thinking = false }));
}

/// <summary>Which requests are told not to think.</summary>
public sealed partial class ChatServiceTests
{
    [Fact]
    public async Task With_story_thinking_off_passages_choices_and_drafts_are_asked_not_to_think()
    {
        var options = new StoryOptions(new VoiceFlowTests.MemorySettings());
        await options.SetLevelAsync(ThinkingLevel.Off);
        var chat = new ChatService(_store, _engine, _presets, _characters, _stories, storyOptions: options);
        var id = await NewStoryWithAsync(StoryKind.Interactive, null);
        ScriptStory();
        await chat.BeginStoryAsync(id);
        Assert.Equal(2, _engine.Samplers.Count); // the passage, then its choices
        Assert.All(_engine.Samplers, s => Assert.False(s.Thinking));

        _engine.Reply = _ => FakeEngine.Say("""{"title":"Winter","premise":"A dog keeps watch.","genre":"","tone":"","cast":"","world":"","style_guide":""}""");
        await new StoryBuilder(_engine, options).DraftAsync("A dog in winter", StoryKind.Interactive);
        Assert.False(_engine.Samplers[^1].Thinking);

        // As stories start (Short), the plan draft thinks briefly and keeps its room for the plan.
        await new StoryBuilder(_engine).DraftAsync("A dog in winter", StoryKind.Interactive);
        Assert.Null(_engine.Samplers[^1].Thinking);
        Assert.Equal(512, _engine.Samplers[^1].ThinkingBudget);
        Assert.Equal(StoryBuilder.MaxTokens + 512, _engine.Samplers[^1].MaxTokens);
    }
}
