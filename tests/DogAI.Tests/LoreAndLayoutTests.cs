using System.Diagnostics;
using System.Text.Json.Nodes;
using DogAI.Core.Characters;
using DogAI.Core.Chat;

namespace DogAI.Tests;

public sealed class LorebookTests
{
    private const string Card = """
        {
          "spec": "chara_card_v2", "spec_version": "2.0",
          "data": {
            "name": "Rex", "description": "A dog.", "first_mes": "Woof!",
            "extensions": {},
            "character_book": {
              "name": "Farm", "scan_depth": 3, "token_budget": 300, "custom_book_field": 7,
              "entries": [
                { "keys": ["bone"], "content": "{{char}} loves bones.", "enabled": true, "insertion_order": 5,
                  "position": "before_char", "extensions": { "probability": 50 }, "id": 11, "comment": "Bones" },
                { "keys": ["barn"], "secondary_keys": ["night"], "selective": true, "content": "The barn is haunted.",
                  "enabled": true, "insertion_order": 1 }
              ]
            }
          }
        }
        """;

    [Fact]
    public void Reads_the_cards_lorebook()
    {
        var book = Lorebook.For(CharacterCard.FromJson(Card));

        Assert.Equal("Farm", book.Name);
        Assert.Equal(3, book.ScanDepth);
        Assert.Equal(300, book.TokenBudget);
        Assert.Equal(2, book.Entries.Count);
        var bone = book.Entries[0];
        Assert.Equal("Bones", bone.Name);
        Assert.Equal(["bone"], bone.Keys);
        Assert.Equal(LorePosition.BeforeCard, bone.Position);
        Assert.Equal(LorePosition.AfterCard, book.Entries[1].Position);
        Assert.True(book.Entries[1].Selective);
    }

    [Fact]
    public void Edits_are_saved_into_the_card_and_unknown_fields_survive_export()
    {
        var c = CharacterCard.FromJson(Card);
        var book = Lorebook.For(c);
        book.Entries[0].Content = "Rex buries bones.";
        book.Entries[0].Keys = ["bone", "bones"];
        book.Entries.RemoveAt(1);
        book.Entries.Add(new LoreEntry { Name = "Cat", Keys = ["cat"], Content = "The cat is called Tom." });
        book.SaveTo(c);

        var exported = JsonNode.Parse(CharacterCard.ToV2Json(c))!["data"]!["character_book"]!;
        Assert.Equal(7, (int?)exported["custom_book_field"]);
        var entries = exported["entries"]!.AsArray();
        Assert.Equal(2, entries.Count);
        Assert.Equal("Rex buries bones.", (string?)entries[0]!["content"]);
        Assert.Equal(50, (int?)entries[0]!["extensions"]!["probability"]);
        Assert.Equal(11, (int?)entries[0]!["id"]);
        Assert.Equal("before_char", (string?)entries[0]!["position"]);
        Assert.Equal("Cat", (string?)entries[1]!["comment"]);

        // And it reads back the same way, through a PNG card too.
        var again = Lorebook.For(CharacterCard.Read(CharacterCard.ToPng(c)));
        Assert.Equal(["bone", "bones"], again.Entries[0].Keys);
        Assert.Equal("The cat is called Tom.", again.Entries[1].Content);
    }

    [Fact]
    public void An_empty_book_is_removed_from_the_card()
    {
        var c = CharacterCard.FromJson(Card);
        var book = Lorebook.For(c);
        book.Entries.Clear();
        book.SaveTo(c);
        Assert.Null(CharacterCard.EmbeddedLorebookJson(c));
        Assert.Null(JsonNode.Parse(CharacterCard.ToV2Json(c))!["data"]!["character_book"]);
    }

    [Fact]
    public void Imports_sillytavern_world_info()
    {
        var json = """
            { "name": "World", "entries": {
                "0": { "uid": 0, "key": ["castle"], "keysecondary": [], "content": "The castle is old.", "comment": "Castle",
                       "constant": false, "selective": false, "order": 5, "position": 0, "disable": false },
                "1": { "uid": 1, "key": ["king"], "content": "The king is ill.", "disable": true, "position": 1, "order": 9 } } }
            """;
        var book = Lorebook.Import(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal(2, book.Entries.Count);
        Assert.Equal("Castle", book.Entries[0].Name);
        Assert.Equal(LorePosition.BeforeCard, book.Entries[0].Position);
        Assert.Equal(5, book.Entries[0].InsertionOrder);
        Assert.False(book.Entries[1].Enabled);
    }

    [Fact]
    public void Imports_the_book_inside_a_card_and_refuses_files_without_lore()
    {
        Assert.Equal(2, Lorebook.Import(System.Text.Encoding.UTF8.GetBytes(Card)).Entries.Count);
        Assert.Throws<CardFormatException>(() => Lorebook.Import("{\"hello\":1}"u8.ToArray()));
        Assert.Throws<CardFormatException>(() => Lorebook.Import("not json"u8.ToArray()));
    }

    [Fact]
    public void A_card_without_a_book_gives_an_empty_one() =>
        Assert.Empty(Lorebook.For(new Character { Name = "X" }).Entries);
}

public sealed class LoreMatcherTests
{
    private static Lorebook Book(params LoreEntry[] entries) => new() { Entries = [.. entries] };

    private static LoreEntry Entry(string key, string content = "x") => new() { Keys = [key], Content = content };

    [Theory]
    [InlineData("I see a cat.", "cat", true)]
    [InlineData("What category is it?", "cat", false)]
    [InlineData("The CAT sat.", "cat", true)]
    [InlineData("Mr. Smith arrived", "Mr. Smith", true)]
    [InlineData("I went to the café", "café", true)]
    [InlineData("Nothing here", "cat", false)]
    public void Keys_match_whole_words_ignoring_case(string text, string key, bool expected) =>
        Assert.Equal(expected, LoreMatcher.Contains(text, key, caseSensitive: false));

    [Fact]
    public void Case_sensitive_keys_respect_case()
    {
        Assert.False(LoreMatcher.Contains("the cat", "Cat", caseSensitive: true));
        Assert.True(LoreMatcher.Contains("the Cat", "Cat", caseSensitive: true));
    }

    [Fact]
    public void Pattern_keys_are_regular_expressions()
    {
        Assert.True(LoreMatcher.Contains("dragons everywhere", "/drag(on|ons)/", caseSensitive: true));
        Assert.True(LoreMatcher.Contains("DRAGON", "/dragon/i", caseSensitive: true));
        Assert.False(LoreMatcher.Contains("anything", "/([/", caseSensitive: false)); // broken pattern: no match, no crash
    }

    [Fact]
    public void A_runaway_pattern_from_a_card_gives_up_quickly()
    {
        var watch = Stopwatch.StartNew();
        var matched = LoreMatcher.Contains(new string('a', 40) + "!", "/(a+)+$x/", caseSensitive: false);
        Assert.False(matched);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Selective_entries_need_a_secondary_key_too()
    {
        var entry = new LoreEntry { Keys = ["barn"], SecondaryKeys = ["night"], Selective = true, Content = "Haunted." };
        Assert.Empty(LoreMatcher.Match(Book(entry), ["We went to the barn."]));
        Assert.Single(LoreMatcher.Match(Book(entry), ["We went to the barn at night."]));
    }

    [Fact]
    public void Constant_entries_always_fire_and_disabled_or_empty_ones_never_do()
    {
        var always = new LoreEntry { Constant = true, Content = "The year is 1850." };
        var off = new LoreEntry { Keys = ["cat"], Content = "Off.", Enabled = false };
        var empty = new LoreEntry { Keys = ["cat"], Content = "  " };
        Assert.Equal([always], LoreMatcher.Match(Book(always, off, empty), ["a cat"]));
    }

    [Fact]
    public void Only_the_latest_messages_are_searched()
    {
        var book = Book(Entry("cat"));
        book.ScanDepth = 2;
        Assert.Empty(LoreMatcher.Match(book, ["a cat", "hello", "hi"]));
        Assert.Single(LoreMatcher.Match(book, ["hello", "a cat", "hi"]));
    }

    [Fact]
    public void Recursive_scanning_lets_one_entry_bring_in_another()
    {
        var book = Book(Entry("castle", "The castle belongs to the king."), Entry("king", "The king is old."));
        Assert.Single(LoreMatcher.Match(book, ["the castle"]));
        book.RecursiveScanning = true;
        Assert.Equal(2, LoreMatcher.Match(book, ["the castle"]).Count);
    }

    [Fact]
    public void Higher_priority_entries_come_first()
    {
        var low = new LoreEntry { Keys = ["a"], Content = "low", Priority = 1 };
        var high = new LoreEntry { Keys = ["a"], Content = "high", Priority = 50 };
        Assert.Equal(["high", "low"], LoreMatcher.Match(Book(low, high), ["a"]).Select(e => e.Content));
    }
}

public sealed class PromptLayoutTests
{
    private static List<PromptMessage> History(params string[] roles) =>
        roles.Select((r, i) => new PromptMessage(r, $"m{i}")).ToList();

    [Fact]
    public void Notes_go_into_user_turns_and_never_add_a_system_message_mid_chat()
    {
        var layout = new PromptLayout { AuthorsNote = "Rain is falling.", AuthorsNoteDepth = 2, FinalInstruction = "Be brief." };
        var arranged = layout.Arrange("System.", History("user", "assistant", "user", "assistant", "user"));

        Assert.Equal(["system", "user", "assistant", "user", "assistant", "user"], arranged.Select(m => m.Role));
        Assert.Equal(1, arranged.Count(m => m.Role == "system"));
        // Depth 2 from the end is message index 2 (a user turn).
        Assert.Equal("m2\n\n[Author's note: Rain is falling.]", arranged[3].Content);
        Assert.Equal("m4\n\nBe brief.", arranged[5].Content);
    }

    [Fact]
    public void A_note_aimed_at_a_reply_goes_to_the_user_turn_before_it()
    {
        var layout = new PromptLayout { AuthorsNote = "N", AuthorsNoteDepth = 1 };
        var arranged = layout.Arrange("", History("user", "assistant", "user", "assistant"));
        Assert.Equal("m2\n\n[Author's note: N]", arranged[2].Content);
    }

    [Fact]
    public void A_deep_note_in_a_short_chat_goes_to_the_first_user_turn()
    {
        var layout = new PromptLayout { AuthorsNote = "N", AuthorsNoteDepth = 10 };
        var arranged = layout.Arrange("", History("assistant", "user"));
        Assert.Equal("m1\n\n[Author's note: N]", arranged[1].Content);
    }

    [Fact]
    public void A_directive_becomes_the_last_user_turn_or_joins_it()
    {
        var layout = new PromptLayout { Directive = "[Write the user's turn.]" };
        var afterReply = layout.Arrange("S", History("user", "assistant"));
        Assert.Equal(["system", "user", "assistant", "user"], afterReply.Select(m => m.Role));
        Assert.Equal("[Write the user's turn.]", afterReply[^1].Content);

        var afterUser = layout.Arrange("S", History("user"));
        Assert.Equal(["system", "user"], afterUser.Select(m => m.Role));
        Assert.Equal("m0\n\n[Write the user's turn.]", afterUser[^1].Content);
    }

    [Fact]
    public void Lore_is_placed_before_or_after_the_card_in_insertion_order()
    {
        var c = new Character { Name = "Rex", Description = "A dog." };
        var lore = new List<LoreEntry>
        {
            new() { Content = "Second after.", InsertionOrder = 9 },
            new() { Content = "First after, about {{char}}.", InsertionOrder = 1 },
            new() { Content = "Before.", Position = LorePosition.BeforeCard },
        };
        var system = CharacterPrompt.Layout(c, null, lore: lore).SystemText();

        var before = system.IndexOf("Before.", StringComparison.Ordinal);
        var card = system.IndexOf("A dog.", StringComparison.Ordinal);
        var first = system.IndexOf("First after, about Rex.", StringComparison.Ordinal);
        var second = system.IndexOf("Second after.", StringComparison.Ordinal);
        Assert.True(before >= 0 && before < card && card < first && first < second, system);
    }

    [Fact]
    public void The_final_instruction_is_no_longer_in_the_system_message()
    {
        var layout = CharacterPrompt.Layout(new Character { Name = "R", PostHistoryInstructions = "Stay short, {{user}}." }, new Persona { Name = "Sam" });
        Assert.DoesNotContain("Stay short", layout.SystemText());
        Assert.Equal("Stay short, Sam.", layout.FinalInstruction);
    }
}

public sealed class SummaryCheckTests
{
    [Fact]
    public void A_looping_summary_is_rejected()
    {
        var looped = "[User 2's summary so far: Dawn comes over Ilsund. User 2's messages: " + string.Concat(Enumerable.Repeat("User 2: ", 80));
        Assert.False(CharacterPrompt.IsUsableSummary(looped));
    }

    [Fact]
    public void An_ordinary_summary_is_kept()
    {
        const string summary =
            "Sam joined the airship Kestrel as navigator under Captain Mara after being expelled from the Naval College. " +
            "They refuelled at Varrow, where Sela Vane's gang in red scarves demanded a crate; Mara paid with her silver compass. " +
            "Sam fought off a saboteur on the gantry and was cut on the left arm. A ticking crate held a clockwork bird carrying " +
            "a warning to the Harbourmaster of Ilsund: the lanterns will burn red and no one in a red scarf can be trusted. " +
            "The crew plans to land outside Ilsund and send Pip ahead to find the Harbourmaster.";
        Assert.True(CharacterPrompt.IsUsableSummary(summary));
    }

    [Fact]
    public void The_request_repeated_back_is_rejected() =>
        Assert.False(CharacterPrompt.IsUsableSummary("[Task: write an updated summary of the story so far between Rex and Sam]"));
}
