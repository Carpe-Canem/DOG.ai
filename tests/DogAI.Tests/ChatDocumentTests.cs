using System.Text;
using DogAI.Core.Chat;
using DogAI.Core.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace DogAI.Tests;

// Documents and recall in chats: the chat service with the real document library and a fake search model.
public sealed partial class ChatServiceTests
{
    private (ChatService Chat, DocumentLibrary Library, FakeEmbedder Embedder) WithDocuments()
    {
        var embedder = new FakeEmbedder();
        var library = new DocumentLibrary(_services.GetRequiredService<ICollectionStore>(), embedder);
        return (new ChatService(_store, _engine, _presets, _characters, _stories, library), library, embedder);
    }

    private const string HouseNotes =
        "# Boiler\n\nThe boiler pressure should sit between one and two bar. Top it up with the filling loop under the boiler.\n\n" +
        "# Garden\n\nWater the tomatoes every evening in summer, and feed them weekly with seaweed.";

    [Fact]
    public async Task A_general_chat_with_a_file_sends_numbered_passages_with_the_question_and_records_them()
    {
        var (chat, library, _) = WithDocuments();
        var id = await NewChatAsync("Be brief.");
        var own = await library.Store.ForConversationAsync(id, create: true);
        await library.AddAsync(own!.Id, "house.md", Encoding.UTF8.GetBytes(HouseNotes));
        await library.WaitForIndexingAsync();

        await chat.SendAsync(id, "What pressure should the boiler be at, and how do I top it up?");

        var prompt = _engine.Prompts[^1];
        Assert.Equal("system", prompt[0].Role);
        Assert.Contains(ChatService.CiteInstruction, prompt[0].Content);
        Assert.Contains("Be brief.", prompt[0].Content);
        Assert.DoesNotContain("filling loop", prompt[0].Content); // passages never go in the system message
        var question = prompt[^1];
        Assert.Equal("user", question.Role);
        Assert.StartsWith(ChatService.PassagesHeader, question.Content);
        Assert.Contains("[1] house.md › Boiler\n", question.Content);
        Assert.Contains("filling loop", question.Content);
        Assert.EndsWith("What pressure should the boiler be at, and how do I top it up?", question.Content);

        var reply = (await ShownAsync(id))[^1];
        var sources = ReplySources.Of(reply)!;
        var first = sources.Passages[0];
        Assert.Equal(1, first.Number);
        Assert.Equal("house.md", first.FileName);
        Assert.Equal("Boiler", first.Heading);
        Assert.Null(sources.Note);
        // The saved question stays as typed.
        Assert.Equal("What pressure should the boiler be at, and how do I top it up?", (await ShownAsync(id))[^2].Content);
    }

    [Fact]
    public async Task Without_a_search_model_the_reply_still_comes_and_says_why_documents_were_not_used()
    {
        var (chat, library, embedder) = WithDocuments();
        var id = await NewChatAsync();
        var own = await library.Store.ForConversationAsync(id, create: true);
        await library.AddAsync(own!.Id, "house.md", Encoding.UTF8.GetBytes(HouseNotes));
        await library.WaitForIndexingAsync();
        embedder.Model = null;

        await chat.SendAsync(id, "What pressure should the boiler be at?");

        var reply = (await ShownAsync(id))[^1];
        Assert.Equal("Hello there", reply.Content);
        Assert.Contains("no document search model", ReplySources.Of(reply)!.Note);
        Assert.DoesNotContain(ChatService.PassagesHeader, _engine.Prompts[^1][^1].Content);
    }

    [Fact]
    public async Task A_chat_without_documents_is_not_searched_and_has_no_sources()
    {
        var (chat, _, embedder) = WithDocuments();
        var id = await NewChatAsync();
        await chat.SendAsync(id, "Hi there");
        Assert.Equal(0, embedder.Calls);
        Assert.Null((await ShownAsync(id))[^1].SourcesJson);
        Assert.DoesNotContain(ChatService.CiteInstruction, _engine.Prompts[^1][0].Content);
    }

    [Fact]
    public async Task Attached_collections_are_searched_in_character_chats_as_background_without_citing()
    {
        var (chat, library, _) = WithDocuments();
        var rex = await SaveCharacterAsync();
        var conversation = await chat.StartCharacterChatAsync(rex.Id);
        var world = await library.Store.CreateAsync(new DocumentCollection { Name = "World" });
        await library.AddAsync(world.Id, "world.md", "# Harbour\n\nThe harbour town of Vell has a lighthouse kept by old Brannoc."u8.ToArray());
        await library.WaitForIndexingAsync();
        await library.Store.SetAttachedAsync(conversation.Id, [world.Id]);

        await chat.SendAsync(conversation.Id, "Who keeps the lighthouse in Vell harbour?");

        var last = _engine.Prompts[^1][^1].Content;
        Assert.StartsWith(ChatService.BackgroundHeader, last);
        Assert.Contains("Brannoc", last);
        Assert.DoesNotContain(ChatService.CiteInstruction, _engine.Prompts[^1][0].Content);
    }

    [Fact]
    public void Search_query_adds_context_to_a_short_follow_up()
    {
        ChatMessage M(ChatRole role, string text) => new() { Role = role, Content = text };
        Assert.Equal("What is the warranty period for the boiler in the manual?",
            ChatService.SearchQuery([M(ChatRole.User, "Hi"), M(ChatRole.Assistant, "Hello"), M(ChatRole.User, "What is the warranty period for the boiler in the manual?")]));
        var followUp = ChatService.SearchQuery([M(ChatRole.User, "Tell me about the boiler warranty"), M(ChatRole.Assistant, "It lasts five years."), M(ChatRole.User, "And the pump?")]);
        Assert.Equal("Tell me about the boiler warranty\nIt lasts five years.\nAnd the pump?", followUp);
        Assert.Equal("The end of the passage.", ChatService.SearchQuery([M(ChatRole.Assistant, "The end of the passage."), M(ChatRole.User, "")]));
    }

    [Fact]
    public async Task Long_character_chats_recall_the_left_out_message_that_matches_the_newest_one()
    {
        var (chat, _, _) = WithDocuments();
        chat.RecallThreshold = 0.15f; // the fake search model only counts shared words
        var rex = await SaveCharacterAsync();
        var conversation = await chat.StartCharacterChatAsync(rex.Id);
        var saved = (await _store.GetAsync(conversation.Id))!;
        saved.MemoryEnabled = true;
        await _store.UpdateAsync(saved);
        _engine.ContextLength = 600; // 450 for the prompt; each turn below is about 40
        // Every turn says different things, so only shared words make two messages alike.
        var n = 0;
        string Filler() => string.Join(' ', Enumerable.Range(0, 36).Select(j => $"t{n}w{j}"));
        _engine.Reply = _ => IsSummaryRequest() ? FakeEngine.Say("A short summary.") : FakeEngine.Say(Filler() + $" reply{n++}");

        await chat.SendAsync(conversation.Id, "My sister Mara keeps bees on the hill behind the mill " + Filler());
        for (var i = 0; i < 8; i++) { n++; await chat.SendAsync(conversation.Id, $"turn {i} " + Filler()); }
        await chat.SendAsync(conversation.Id, "Do you remember what Mara keeps on the hill?");

        var reply = (await ShownAsync(conversation.Id))[^1];
        Assert.True(reply.TrimmedMessages > 2);
        var sources = ReplySources.Of(reply)!;
        var recalled = sources.Recalled[0];
        var mara = (await ShownAsync(conversation.Id)).First(m => m.Content.StartsWith("My sister Mara"));
        Assert.Equal(mara.Id, recalled);
        var last = _engine.Prompts[^1][^1].Content;
        Assert.StartsWith("[Earlier in this chat, possibly relevant now]", last);
        Assert.Contains("User: My sister Mara keeps bees", last);
        Assert.EndsWith("Do you remember what Mara keeps on the hill?", last);
    }

    [Fact]
    public async Task Recall_can_be_switched_off_per_chat()
    {
        var (chat, _, embedder) = WithDocuments();
        var rex = await SaveCharacterAsync();
        var conversation = await chat.StartCharacterChatAsync(rex.Id);
        var saved = (await _store.GetAsync(conversation.Id))!;
        saved.RecallEnabled = false;
        await _store.UpdateAsync(saved);
        Assert.False((await _store.GetAsync(conversation.Id))!.RecallEnabled);
        _engine.ContextLength = 600;
        var filler = string.Join(' ', Enumerable.Repeat("word", 36));
        _engine.Reply = _ => IsSummaryRequest() ? FakeEngine.Say("A short summary.") : FakeEngine.Say(filler);

        for (var i = 0; i < 10; i++) await chat.SendAsync(conversation.Id, $"turn {i} Mara bees " + filler);

        Assert.Equal(0, embedder.Calls);
        Assert.Null(ReplySources.Of((await ShownAsync(conversation.Id))[^1]));
    }
}
