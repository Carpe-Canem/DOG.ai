using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Stories;
using DogAI.Core.Voice;

namespace DogAI.Tests;

public sealed partial class ChatServiceTests
{
    private (ReplyVoice Voice, ReadAloud Reader, VoiceFlowTests.FakeVoice Synth) NewReplyVoice(bool autoRead)
    {
        var synth = new VoiceFlowTests.FakeVoice();
        var settings = new VoiceFlowTests.MemorySettings(new DogAI.Core.Settings.AppSettings { ReadRepliesAloud = autoRead });
        var reader = new ReadAloud(synth, new VoiceFlowTests.FakeSpeakers(), settings);
        var voice = new ReplyVoice(_chat, reader, _store, _characters, _stories, settings);
        voice.LoadAsync().GetAwaiter().GetResult();
        return (voice, reader, synth);
    }

    [Fact]
    public async Task With_read_aloud_on_a_character_reply_is_read_in_the_characters_voice()
    {
        var (voice, reader, synth) = NewReplyVoice(autoRead: true);
        using var _ = voice;
        var rex = await _characters.SaveAsync(new Character { Name = "Rex", FirstMessage = "Woof.", Voice = "kokoro:pack/am_adam" });
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        _engine.Reply = _ => FakeEngine.Say("I fetched ", "the ball. It was ", "red.");

        await _chat.SendAsync(chat.Id, "What did you do?");
        await reader.IdleAsync();

        Assert.Equal("I fetched the ball. It was red.", string.Join(" ", synth.Said.Select(s => s.Text)));
        Assert.All(synth.Said, s => Assert.Equal(new VoiceId(VoiceKind.Kokoro, "pack", "am_adam"), s.Voice));
    }

    [Fact]
    public async Task With_read_aloud_off_nothing_is_read_until_asked_and_a_story_uses_its_narrator()
    {
        var (voice, reader, synth) = NewReplyVoice(autoRead: false);
        using var _ = voice;
        var conversation = await _store.CreateAsync(new Conversation { Mode = Story.Mode, Title = "Tale" });
        await _stories.SaveAsync(new Story { ConversationId = conversation.Id, NarratorVoice = "piper:vits-piper-en_GB-alba-medium" });
        _engine.Reply = _ => FakeEngine.Say("The door creaks open.");

        await _chat.SendAsync(conversation.Id, "Open the door");
        await reader.IdleAsync();
        Assert.Empty(synth.Said);

        var passage = (await ShownAsync(conversation.Id))[^1];
        await voice.ToggleAsync(passage);
        Assert.True(voice.IsReading(passage));
        await reader.IdleAsync();
        Assert.Equal(("The door creaks open.", new VoiceId(VoiceKind.Piper, "vits-piper-en_GB-alba-medium")), Assert.Single(synth.Said));
    }

    [Fact]
    public async Task Drafts_are_never_read_and_general_chats_use_the_default_voice()
    {
        var (voice, reader, synth) = NewReplyVoice(autoRead: true);
        using var _ = voice;
        var rex = await _characters.SaveAsync(new Character { Name = "Rex", FirstMessage = "Woof.", Voice = "windows:Rex voice" });
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        _engine.Reply = _ => FakeEngine.Say("A draft of my message.");
        await _chat.ImpersonateAsync(chat.Id);
        await reader.IdleAsync();
        Assert.Empty(synth.Said);

        Assert.Null(await voice.VoiceForAsync(await NewChatAsync(), null));
        Assert.Equal(new VoiceId(VoiceKind.Windows, "Rex voice"), await voice.VoiceForAsync(chat.Id, null));
    }

    [Fact]
    public async Task In_a_group_each_reply_is_read_in_its_speakers_voice_one_after_another()
    {
        var (voice, reader, synth) = NewReplyVoice(autoRead: true);
        using var _ = voice;
        var ann = await _characters.SaveAsync(new Character { Name = "Ann", FirstMessage = "Hi.", Voice = "windows:Ann voice" });
        var bo = await _characters.SaveAsync(new Character { Name = "Bo", FirstMessage = "Yo.", Voice = "windows:Bo voice" });
        var group = await _chat.StartGroupChatAsync([ann.Id, bo.Id]);
        var replies = new Queue<string>(["Ann speaks first.", "Bo answers next."]);
        _engine.Reply = _ => FakeEngine.Say(replies.Dequeue());

        await _chat.SpeakAsync(group.Id, ann.Id);
        await _chat.SpeakAsync(group.Id, bo.Id);
        await reader.IdleAsync();

        Assert.Equal([("Ann speaks first.", "Ann voice"), ("Bo answers next.", "Bo voice")], synth.Said.Select(s => (s.Text, s.Voice.Model)));
    }
}
