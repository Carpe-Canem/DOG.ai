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

    [Fact]
    public async Task Allowing_thinking_is_saved_in_settings_and_lets_story_requests_think()
    {
        var settings = new VoiceFlowTests.MemorySettings();
        var options = new StoryOptions(settings);
        await options.LoadAsync();
        Assert.False(options.Thinking);
        Assert.False(options.Apply(SamplerPresets.Get(null)).Thinking);

        await options.SetThinkingAsync(true);
        Assert.True((await settings.LoadAsync()).StoryThinking);
        Assert.Null(options.Apply(SamplerPresets.Get(null)).Thinking);
    }
}

/// <summary>Which requests are told not to think.</summary>
public sealed partial class ChatServiceTests
{
    [Fact]
    public async Task Story_passages_choices_and_drafts_are_asked_not_to_think_and_chats_are_not()
    {
        var id = await NewStoryWithAsync(StoryKind.Interactive, null);
        ScriptStory();
        await _chat.BeginStoryAsync(id);
        Assert.Equal(2, _engine.Samplers.Count); // the passage, then its choices
        Assert.All(_engine.Samplers, s => Assert.False(s.Thinking));

        _engine.Reply = _ => FakeEngine.Say("""{"title":"Winter","premise":"A dog keeps watch.","genre":"","tone":"","cast":"","world":"","style_guide":""}""");
        await new StoryBuilder(_engine).DraftAsync("A dog in winter", StoryKind.Interactive);
        Assert.False(_engine.Samplers[^1].Thinking);

        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        _engine.Reply = _ => FakeEngine.Say("*sits*");
        await _chat.SendAsync(chat.Id, "Sit.");
        Assert.Null(_engine.Samplers[^1].Thinking);
    }
}
