using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Stories;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

/// <summary>Pictures on messages (M17 step 2): what the chat service stores, and what the model is given.</summary>
public sealed partial class ChatServiceTests
{
    private static readonly byte[] Wide = TestJpegs.Make(1024, 640, seed: 1);   // 642 tokens by the rule
    private static readonly byte[] Small = TestJpegs.Make(256, 160, seed: 2);   // 42 tokens

    private IAttachedPictureStore Attached => _services.GetRequiredService<IAttachedPictureStore>();

    /// <summary>A reply without speed figures, so the prompt's size stays the one DOG.ai counted.</summary>
    private static async IAsyncEnumerable<ChatDelta> Plainly(string text)
    {
        await Task.Yield();
        yield return new ChatDelta(text, null, null);
    }

    [Fact]
    public async Task A_picture_goes_to_a_model_that_sees_as_the_stored_bytes_in_front_of_its_message()
    {
        _engine.Sees = true;
        _engine.Reply = _ => Plainly("A dog.");
        var id = await NewChatAsync("Be brief.");

        await _chat.SendAsync(id, "What is this?", [Wide]);

        var shown = await ShownAsync(id);
        var kept = Assert.Single(shown[0].Pictures);
        Assert.Equal((1024, 640, Wide.Length), (kept.Width, kept.Height, kept.Bytes));
        // The model is given the question with the stored picture, byte for byte, in front of its text.
        var prompt = _engine.Prompts[^1];
        Assert.Equal(["system", "user"], prompt.Select(m => m.Role));
        Assert.Null(prompt[0].Pictures);
        Assert.Equal("What is this?", prompt[1].Content);
        var sent = Assert.Single(prompt[1].Pictures!);
        Assert.Equal(Wide, sent.Jpeg);
        Assert.Equal((kept.Id, 1024, 640, 0), (sent.Id, sent.Width, sent.Height, sent.After));

        // The chat template was given text only, and what was counted as text held no stand-in for the picture.
        Assert.All(_engine.Rendered.SelectMany(m => m), m => Assert.Null(m.Pictures));
        Assert.DoesNotContain(_engine.Counted, t => t.Contains("DOGAI-PICTURE") || t.Contains("[picture"));
        // The inspector's text says where the picture is, how large, and what it costs; the size is text plus picture.
        var reply = shown[1];
        Assert.Equal("<system> Be brief. <user> [picture 1,024 × 640, 642 tokens]What is this?", reply.PromptText);
        Assert.Equal(7 + 642, reply.PromptTokens);
        Assert.Null(StoryTurn.Of(reply).Unseen);
        Assert.Equal("What is this?", (await _store.GetAsync(id))!.Title);
    }

    [Fact]
    public async Task The_engines_own_count_of_a_picture_is_used_and_remembered()
    {
        _engine.Sees = true;
        _engine.Reply = _ => Plainly("ok");
        var asked = 0;
        _engine.CountPicture = _ => { asked++; return 700; };
        var id = await NewChatAsync();

        await _chat.SendAsync(id, "One", [Wide]);

        var shown = await ShownAsync(id);
        Assert.Equal("<user> [picture 1,024 × 640, 700 tokens]One", shown[1].PromptText);
        Assert.Equal(2 + 700, shown[1].PromptTokens);
        Assert.Equal(700, _chat.PictureTokens(shown[0].Pictures[0]));
        // Asked once for the picture, however many times the prompt is built.
        await _chat.RegenerateAsync(id, shown[1].Id);
        Assert.Equal(1, asked);
        // A picture not on a message yet (the context meter): the engine's count, else the rule; nothing for bad bytes.
        Assert.Equal(700, await _chat.CountPictureAsync(Small));
        _engine.CountPicture = null;
        Assert.Equal(42, await _chat.CountPictureAsync(Small));
        Assert.Equal(0, await _chat.CountPictureAsync("words"u8.ToArray()));
    }

    [Fact]
    public async Task A_model_that_cannot_see_gets_a_note_and_the_reply_remembers_it()
    {
        _engine.Reply = _ => Plainly("I cannot see it.");
        var id = await NewChatAsync();

        await _chat.SendAsync(id, "What is this?", [Wide, Small]);

        var shown = await ShownAsync(id);
        Assert.Equal(2, shown[0].Pictures.Count);
        Assert.Equal("What is this?", shown[0].Content);
        var user = _engine.Prompts[^1][^1];
        Assert.Null(user.Pictures);
        Assert.Equal($"{AttachedPictures.UnseenNote} {AttachedPictures.UnseenNote}\nWhat is this?", user.Content);
        Assert.Equal(2, StoryTurn.Of(shown[1]).Unseen);
        Assert.DoesNotContain("[picture ", shown[1].PromptText);

        // The same question with a model that sees: the pictures themselves, and nothing to remember.
        _engine.Sees = true;
        await _chat.RegenerateAsync(id, shown[1].Id);
        Assert.Equal([Wide, Small], _engine.Prompts[^1][^1].Pictures!.Select(p => p.Jpeg));
        Assert.Equal("What is this?", _engine.Prompts[^1][^1].Content);
        Assert.Null(StoryTurn.Of((await ShownAsync(id))[1]).Unseen);
    }

    [Fact]
    public async Task Regenerate_and_edit_keep_the_questions_pictures_byte_for_byte()
    {
        _engine.Sees = true;
        var id = await NewChatAsync();
        await _chat.SendAsync(id, "Compare these", [Wide, Small]);
        var first = await ShownAsync(id);
        var ids = first[0].Pictures.Select(p => p.Id).ToList();

        await _chat.RegenerateAsync(id, first[1].Id);
        Assert.Equal(ids, _engine.Prompts[^1][^1].Pictures!.Select(p => p.Id));
        Assert.Equal([Wide, Small], _engine.Prompts[^1][^1].Pictures!.Select(p => p.Jpeg));

        // An edited question is a new message with pictures of its own: the same bytes, so the engine knows them.
        await _chat.EditAndResendAsync(id, first[0].Id, "Compare these two");
        var edited = await ShownAsync(id);
        Assert.NotEqual(first[0].Id, edited[0].Id);
        Assert.Equal(2, edited[0].Pictures.Count);
        Assert.Empty(edited[0].Pictures.Select(p => p.Id).Intersect(ids));
        Assert.Equal([Wide, Small], _engine.Prompts[^1][^1].Pictures!.Select(p => p.Jpeg));

        // Keeping one and adding another; then keeping none.
        var third = TestJpegs.Make(512, 512, seed: 3);
        await _chat.EditAndResendAsync(id, first[0].Id, "And this", [ids[1]], [third]);
        Assert.Equal([Small, third], _engine.Prompts[^1][^1].Pictures!.Select(p => p.Jpeg));
        await _chat.EditAndResendAsync(id, first[0].Id, "No pictures now", [], null);
        Assert.Null(_engine.Prompts[^1][^1].Pictures);
        Assert.Empty((await ShownAsync(id))[0].Pictures);
        // The first version still has its two.
        Assert.Equal(ids, (await _store.GetMessagesAsync(id)).Single(m => m.Id == first[0].Id).Pictures.Select(p => p.Id));
    }

    [Fact]
    public async Task Pictures_that_do_not_belong_are_refused_before_anything_is_stored()
    {
        var id = await NewChatAsync();

        var tooMany = await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.SendAsync(id, "Five", [Small, Small, Small, Small, Small]));
        Assert.Equal("A message takes at most 4 pictures.", tooMany.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.SendAsync(id, "Not a JPEG", [KeptPictureTests.Webp(64, 64)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.SendAsync(id, "Too large", [Small, TestJpegs.Make(1536, 1024)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.SendAsync(id, "With a place and a date", [TestJpegs.Make(64, 64, extra: TestJpegs.Exif())]));
        Assert.Empty(await _store.GetMessagesAsync(id));
        Assert.Empty(_engine.Prompts);

        var story = await _store.CreateAsync(new Conversation { Mode = Story.Mode });
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.SendAsync(story.Id, "Look", [Small]));
        Assert.Equal("Stories do not take pictures.", refused.Message);

        // The browser's colour profile is taken out; what is kept is the plain picture.
        await _chat.SendAsync(id, "", [TestJpegs.Make(256, 160, seed: 2, extra: TestJpegs.Profile())]);
        var question = (await ShownAsync(id))[0];
        Assert.Equal(Small, (await Attached.GetAsync(question.Pictures[0].Id))!.Image);
        // A message may be a picture alone; the chat is named for it.
        Assert.Equal("", question.Content);
        Assert.Equal(ChatService.PictureChat, (await _store.GetAsync(id))!.Title);
    }

    [Fact]
    public async Task A_picture_is_left_out_only_with_its_message_when_room_runs_out()
    {
        _engine.Sees = true;
        _engine.ContextLength = 1000; // 750 for the prompt: one 642-token picture fits, two do not
        _engine.Reply = _ => Plainly("Seen.");
        var id = await NewChatAsync();

        await _chat.SendAsync(id, "one", [Wide]);
        await _chat.SendAsync(id, "two", [TestJpegs.Make(1024, 640, seed: 9)]);

        var shown = await ShownAsync(id);
        var prompt = _engine.Prompts[^1];
        // The first question went, with its picture and its answer; the newest has its own.
        Assert.Equal(["user"], prompt.Select(m => m.Role));
        Assert.Equal("two", prompt[0].Content);
        Assert.Equal(shown[2].Pictures[0].Id, Assert.Single(prompt[0].Pictures!).Id);
        Assert.Equal(2, shown[3].TrimmedMessages);
        Assert.Equal(2 + 642, shown[3].PromptTokens);
    }

    [Fact]
    public async Task A_reply_is_carried_on_with_its_pictures_in_place()
    {
        _engine.Sees = true;
        var id = await NewChatAsync();
        await _chat.SendAsync(id, "Describe it", [Small]);
        var reply = (await ShownAsync(id))[1];

        _engine.Reply = _ => FakeEngine.Say(" and more");
        await _chat.ContinueAsync(id, reply.Id);

        // The raw text holds no stand-in of any kind; the picture is placed where the question's text starts.
        var (text, pictures) = Assert.Single(_engine.PictureCompletions);
        Assert.Equal("<user> Describe itHello there", text);
        var placed = Assert.Single(pictures);
        Assert.Equal(Small, placed.Jpeg);
        Assert.Equal("<user> ".Length, placed.After);
        Assert.Empty(_engine.Completions);
        Assert.Equal("Hello there and more", (await ShownAsync(id))[1].Content);
        // What the inspector shows for the continued reply names the picture.
        Assert.StartsWith("<user> [picture 256 × 160, 42 tokens]Describe it", (await ShownAsync(id))[1].PromptText);

        // With a model that cannot see, the same reply is carried on from text alone.
        _engine.Sees = false;
        await _chat.ContinueAsync(id, reply.Id);
        Assert.Single(_engine.PictureCompletions);
        Assert.Equal($"<user> {AttachedPictures.UnseenNote}\nDescribe itHello there and more", Assert.Single(_engine.Completions));
    }

    /// <summary>
    /// Write mine leaves the pictures out of its one request: with a picture at the start of the owner's turns the model
    /// began its draft with a stand-in in brackets every time (M17 step 6), and a note in its place did the same. The
    /// replies before and after still get the pictures.
    /// </summary>
    [Fact]
    public async Task Write_mine_drafts_from_text_alone_and_puts_no_note_where_a_picture_was()
    {
        _engine.Sees = true;
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        await _chat.SendAsync(chat.Id, "What does the sign say?", [Small]);
        await _chat.SendAsync(chat.Id, "", [Small]); // a message that is a picture alone
        var replies = _engine.Prompts.Count;

        _engine.Reply = _ => FakeEngine.Say("And the next one?");
        Assert.Equal("And the next one?", await _chat.ImpersonateAsync(chat.Id));

        // One request more, in words alone: no picture on any turn, and nothing in brackets where the pictures were.
        Assert.Empty(_engine.PictureCompletions);
        Assert.Empty(_engine.Completions);
        Assert.Equal(replies + 1, _engine.Prompts.Count);
        var draftPrompt = _engine.Prompts[^1];
        Assert.All(draftPrompt, m => Assert.Null(m.Pictures));
        Assert.Contains(draftPrompt, m => m is { Role: "user", Content: "What does the sign say?" });
        Assert.Contains(draftPrompt, m => m.Role == "user" && m.Content == AttachedPictures.PictureOnly);
        Assert.All(draftPrompt.Skip(1).SkipLast(1), m =>
            Assert.DoesNotContain("picture", m.Content.Replace(AttachedPictures.PictureOnly, ""), StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("[Write ", draftPrompt[^1].Content);

        // The next reply has both pictures again, and no reply was told the model could not see.
        await _chat.SendAsync(chat.Id, "Thanks");
        Assert.Equal(2, _engine.Prompts[^1].Sum(m => m.Pictures?.Count ?? 0));
        Assert.All((await _store.GetMessagesAsync(chat.Id)).Where(m => m.Role == ChatRole.Assistant), m => Assert.Null(StoryTurn.Of(m).Unseen));
    }

    [Fact]
    public async Task In_a_group_each_picture_stays_in_front_of_its_own_message()
    {
        _engine.Sees = true;
        var ann = await _characters.SaveAsync(new Character { Name = "Ann", Description = "A painter." });
        var bo = await _characters.SaveAsync(new Character { Name = "Bo", Description = "A critic." });
        var chat = await _chat.StartGroupChatAsync([ann.Id, bo.Id], turnOrder: TurnOrder.Manual);

        // Nobody answers by themselves, so the two messages reach the model as one turn.
        await _chat.SendAsync(chat.Id, "First picture", [Wide]);
        await _chat.SendAsync(chat.Id, "Second picture", [Small]);
        await _chat.SpeakAsync(chat.Id, ann.Id);

        var turn = _engine.Prompts[^1].Single(m => m.Pictures is not null);
        Assert.Equal("user", turn.Role);
        Assert.StartsWith("First picture\n\nSecond picture", turn.Content);
        Assert.Equal([(0, 1024), ("First picture\n\n".Length, 256)], turn.Pictures!.Select(p => (p.After, p.Width)));
        Assert.Contains("[picture 1,024 × 640, 642 tokens]First picture\n\n[picture 256 × 160, 42 tokens]Second picture", (await ShownAsync(chat.Id))[^1].PromptText);
    }

    [Fact]
    public async Task A_summary_is_asked_for_in_words_alone()
    {
        _engine.Sees = true;
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        _engine.ContextLength = 400;
        var words = string.Join(' ', Enumerable.Repeat("word", 38));
        var summaryText = "SUMMARY of the story " + string.Join(' ', Enumerable.Range(0, 60).Select(i => $"fact{i}"));
        _engine.Reply = _ => IsSummaryRequest() ? FakeEngine.Say(summaryText) : FakeEngine.Say(words);

        await _chat.SendAsync(chat.Id, "turn 0 " + words, [Small]);
        for (var i = 1; i < 6; i++) await _chat.SendAsync(chat.Id, $"turn {i} " + words);

        var requests = _engine.Prompts.Where(p => p[0].Content.Contains("summarise stories")).ToList();
        Assert.NotEmpty(requests);
        Assert.All(requests.SelectMany(r => r), m => Assert.Null(m.Pictures));
        Assert.Contains(requests, r => r[1].Content.Contains("User: [picture]\nturn 0"));
    }

    [Fact]
    public async Task A_story_made_from_a_chat_gets_a_note_where_a_picture_was()
    {
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        await _chat.SendAsync(chat.Id, "Look at my garden", [Small]);
        await _chat.SendAsync(chat.Id, "Shall we plant something?");

        var story = await _chat.StoryFromChatAsync(chat.Id, "The garden", new Story());

        var copied = await _store.GetMessagesAsync(story.Id);
        Assert.Contains(copied, m => m.Content == "[picture]\nLook at my garden");
        Assert.All(copied, m => Assert.Empty(m.Pictures));
        // The chat keeps its picture.
        Assert.Contains(await _store.GetMessagesAsync(chat.Id), m => m.Pictures.Count == 1);
    }

    /// <summary>
    /// The story editor says how many messages come along with the same rule the copy uses (found by the M17 review:
    /// a message that is pictures alone was copied as "[picture]" but not counted).
    /// </summary>
    [Fact]
    public async Task A_message_of_pictures_alone_counts_for_a_story_and_is_copied_as_a_note()
    {
        var rex = await SaveCharacterAsync();
        var chat = await _chat.StartCharacterChatAsync(rex.Id);
        await _chat.SendAsync(chat.Id, "", [Small]);
        await _chat.SendAsync(chat.Id, "And this one?");
        var shown = new MessageTree(await _store.GetMessagesAsync(chat.Id)).PathTo((await _store.GetAsync(chat.Id))!.ActiveLeafId);

        var counted = ChatService.MessagesForStory(shown);
        var story = await _chat.StoryFromChatAsync(chat.Id, "The picture", new Story());

        var copied = await _store.GetMessagesAsync(story.Id);
        Assert.Equal(counted.Count, copied.Count);
        Assert.Contains(counted, m => m.Content == "[picture]");
        Assert.Contains(copied, m => m.Role == ChatRole.User && m.Content == "[picture]");
    }

    [Fact]
    public void Text_put_in_front_of_a_turn_moves_its_pictures_with_it()
    {
        var picture = new PromptPicture(1, Small, 256, 160);
        var layout = PromptLayout.Plain("Be brief.");
        layout.Add(PromptPartKind.Documents, "PASSAGES");
        layout.AuthorsNote = "stay calm";
        layout.AuthorsNoteDepth = 0;

        var arranged = layout.Arrange("Be brief.", [new("user", "What is this?", [picture])]);

        // Found passages come first, then the picture, then the question; a note after it moves nothing.
        Assert.Equal("PASSAGES\n\nWhat is this?\n\n[Author's note: stay calm]", arranged[1].Content);
        Assert.Equal("PASSAGES\n\n".Length, Assert.Single(arranged[1].Pictures!).After);
        // A turn that is a picture alone: the picture goes after the passages.
        layout.AuthorsNote = "";
        var bare = layout.Arrange("", [new("assistant", "Hello"), new("user", "", [picture])]);
        Assert.Equal("PASSAGES", bare[^1].Content);
        Assert.Equal("PASSAGES".Length, Assert.Single(bare[^1].Pictures!).After);

        // As text for the chat template: something of DOG.ai's own where each picture is, numbered across the prompt.
        var number = 3;
        var text = new PromptMessage("user", "ab", [picture with { After = 1 }, picture with { After = 99 }]).AsText((i, _) => $"<{i}>", ref number);
        Assert.Equal(("a<3>b<4>", 5), (text.Content, number));
        Assert.Null(text.Pictures);
    }
}
