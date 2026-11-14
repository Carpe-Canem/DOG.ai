using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Stories;

namespace DogAI.Tests;

/// <summary>Pictures in character chats and group chats: who may be in one and what the planner is given.</summary>
public sealed class ChatPictureTests
{
    private static ChatMessage Said(long id, ChatRole role, string text, long? speaker = null) =>
        new() { Id = id, ParentId = id > 1 ? id - 1 : null, Role = role, Content = text, SpeakerId = speaker };

    [Fact]
    public void The_people_are_the_characters_then_the_persona_each_with_their_own_looks()
    {
        var mara = new Character { Id = 1, Name = "Mara", Description = "A scout in a green cloak." };
        new Looks("1girl, grey hair, braid").SaveTo(mara);
        var garrick = new Character { Id = 2, Name = "Garrick" };
        var sam = new Persona { Name = "Sam", Description = "A farmer." };
        new Looks("1boy, red hair").SaveTo(sam);

        var people = ChatPictures.People([mara, garrick], sam);

        Assert.Equal(["Mara", "Garrick", "Sam"], people.Select(p => p.Name));
        Assert.Equal("1girl, grey hair, braid", people[0].Looks.Tags);
        Assert.Equal("A scout in a green cloak.", people[0].About);
        Assert.False(people[1].HasLooks);
        Assert.Null(people[1].About);
        Assert.Equal("1boy, red hair", people[2].Looks.Tags);
        Assert.Equal("A farmer.", people[2].About);

        // No persona: only the characters. A persona named like a character is left out (names tell people apart).
        Assert.Equal(["Mara"], ChatPictures.People([mara], null).Select(p => p.Name));
        Assert.Equal(["Mara"], ChatPictures.People([mara], new Persona { Name = "mara" }).Select(p => p.Name));
    }

    [Fact]
    public void The_background_is_the_newest_messages_before_the_reply_with_names()
    {
        var path = new List<ChatMessage>();
        for (var i = 1; i <= 10; i++)
            path.Add(Said(i, i % 2 == 1 ? ChatRole.Assistant : ChatRole.User, $"line {i}"));
        string Name(ChatMessage m) => m.Role == ChatRole.User ? "Sam" : "Rex";

        var background = ChatPictures.Background(path, path[8], Name);

        // Six messages at most, oldest first, never the reply itself or anything after it.
        Assert.Equal("Rex: line 3\nSam: line 4\nRex: line 5\nSam: line 6\nRex: line 7\nSam: line 8", background);
        Assert.Equal("", ChatPictures.Background(path, path[0], Name));
        Assert.Equal("Rex: line 9", ChatPictures.Passage(path[8], Name));

        // Long messages are cut, and the whole stays within the planner's limit by dropping the oldest.
        var longPath = Enumerable.Range(1, 8).Select(i => Said(i, ChatRole.User, new string((char)('a' + i), 900))).ToList();
        var cut = ChatPictures.Background(longPath, longPath[7], Name);
        Assert.True(cut.Length <= ChatPictures.BackgroundLength);
        Assert.DoesNotContain("bbb", cut);
        Assert.Contains(new string('h', ChatPictures.BackgroundMessageLength) + "…", cut);
        Assert.EndsWith("…", cut);
    }

    [Fact]
    public async Task The_planner_gets_the_reply_under_its_writers_name_and_what_came_before()
    {
        var engine = new Answer("""{"moment":"Rex sits by the gate.","people":[{"name":"Rex","kind":"other","looks":[],"clothing":[],"expression":[],"pose":["sitting"]},{"name":"Sam","kind":"boy","looks":[],"clothing":[],"expression":[],"pose":[]},{"name":"Ann","kind":"girl","looks":[],"clothing":[],"expression":[],"pose":[]}],"place":["farm"],"light":["day"],"framing":"cowboy shot","view":"straight on"}""");
        var rex = new Character { Id = 1, Name = "Rex", Description = "A loyal dog." };
        new Looks("dog, brown fur").SaveTo(rex);
        var people = ChatPictures.People([rex, new Character { Id = 2, Name = "Ann" }], new Persona { Name = "Sam" });
        var path = new[] { Said(1, ChatRole.Assistant, "Woof.", 1), Said(2, ChatRole.User, "Guard the gate."), Said(3, ChatRole.Assistant, "I sit by the gate.", 1) };

        var scene = await ChatPictures.PlanAsync(new PictureWriter(engine), people, path, path[2],
            m => m.Role == ChatRole.User ? "Sam" : "Rex", PromptStyle.Tags, PictureRating.General);

        Assert.Equal(ChatPictures.MaxPeople, scene.People.Count);
        Assert.Contains("Passage:\nRex: I sit by the gate.", engine.User);
        Assert.Contains("What was said just before:\nRex: Woof.\nSam: Guard the gate.", engine.User);
        Assert.Contains("Rex: A loyal dog.", engine.User);
        Assert.Contains("at most 2", engine.System);
        Assert.Contains("Leave it empty for Rex", engine.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ChatPictures.PlanAsync(new PictureWriter(engine), people, path,
            Said(4, ChatRole.Assistant, "  "), _ => "Rex", PromptStyle.Tags, PictureRating.General));
    }

    /// <summary>A chat model that answers one fixed text and remembers what it was asked.</summary>
    private sealed class Answer(string answer) : IChatEngine
    {
        public bool IsReady => true;
        public string? ModelName => "fake";
        public int ContextLength => 8192;
        public string System { get; private set; } = "";
        public string User { get; private set; } = "";

        public Task<int> CountTokensAsync(string text, CancellationToken ct = default) => Task.FromResult(text.Length / 4);
        public Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct = default) => Task.FromResult("");

        public IAsyncEnumerable<ChatDelta> StreamChatAsync(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, CancellationToken ct = default)
        {
            System = messages[0].Content;
            User = messages[^1].Content;
            return One();
        }

        public IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, SamplerPreset sampler, CancellationToken ct = default) => One();

        private async IAsyncEnumerable<ChatDelta> One()
        {
            await Task.Yield();
            yield return new ChatDelta(answer, null, null);
        }
    }
}

/// <summary>Chat pictures through the real chat service and SQLite stores.</summary>
public sealed partial class ChatServiceTests
{
    [Fact]
    public async Task A_chat_reply_keeps_a_picture_and_another_version_of_it_starts_with_none()
    {
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        await _chat.SendAsync(chat.Id, "Guard the gate.");
        var reply = (await ShownAsync(chat.Id))[^1];
        var tries = new PictureTries(new PortraitTests.WebpEngine());

        var kept = await Illustrations.KeepAsync(PictureStore, _chat, chat.Id, reply.Id, DrawTry(tries));

        Assert.Equal(kept, Illustrations.Shown((await ShownAsync(chat.Id))[^1]));
        Assert.Equal([kept], (await PictureStore.ListAsync(PictureOwners.Message, reply.Id)).Select(p => p.Id));

        // Regenerate: the new version has no picture; going back to the first shows its picture again.
        await _chat.RegenerateAsync(chat.Id, reply.Id);
        var again = (await ShownAsync(chat.Id))[^1];
        Assert.NotEqual(reply.Id, again.Id);
        Assert.Null(Illustrations.Shown(again));
        await _chat.SelectBranchAsync(chat.Id, reply.Id);
        Assert.Equal(kept, Illustrations.Shown((await ShownAsync(chat.Id))[^1]));

        // The picture is not part of what the model is sent.
        await _chat.SendAsync(chat.Id, "Good dog.");
        Assert.DoesNotContain(_engine.Prompts[^1], m => m.Content.Contains("picture", StringComparison.OrdinalIgnoreCase));

        await Illustrations.RemoveAsync(PictureStore, _chat, chat.Id, reply.Id);
        Assert.Empty(await PictureStore.ListAsync(PictureOwners.Message, reply.Id));
    }

    [Fact]
    public async Task A_story_made_from_a_chat_carries_no_pictures_and_deleting_the_chat_takes_its_own()
    {
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        await _chat.SendAsync(chat.Id, "Guard the gate.");
        var reply = (await ShownAsync(chat.Id))[^1];
        var kept = await Illustrations.KeepAsync(PictureStore, _chat, chat.Id, reply.Id, DrawTry(new PictureTries(new PortraitTests.WebpEngine())));

        var story = await _chat.StoryFromChatAsync(chat.Id, "The gate", new Story());

        var passages = await ShownAsync(story.Id);
        Assert.NotEmpty(passages);
        Assert.All(passages, p => Assert.Null(StoryTurn.Of(p).Picture));
        Assert.NotNull(await PictureStore.GetAsync(kept));

        await _store.DeleteAsync(chat.Id);
        Assert.Null(await PictureStore.GetAsync(kept));
    }

    [Fact]
    public async Task A_group_reply_keeps_a_picture_too()
    {
        var ann = await _characters.SaveAsync(new Character { Name = "Ann", FirstMessage = "Hello from Ann." });
        var bo = await _characters.SaveAsync(new Character { Name = "Bo" });
        var group = await _chat.StartGroupChatAsync([ann.Id, bo.Id]);
        var reply = (await ShownAsync(group.Id)).Last(m => m.Role == ChatRole.Assistant);

        var kept = await Illustrations.KeepAsync(PictureStore, _chat, group.Id, reply.Id, DrawTry(new PictureTries(new PortraitTests.WebpEngine())));

        var shown = (await ShownAsync(group.Id)).Single(m => m.Id == reply.Id);
        Assert.Equal(kept, Illustrations.Shown(shown));
        Assert.Equal(reply.SpeakerId, shown.SpeakerId);
    }
}
