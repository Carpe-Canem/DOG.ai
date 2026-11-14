using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DogAI.Core.Chat;
using DogAI.Core.Stories;
using DogAI.Core.Text;

namespace DogAI.Tests;

/// <summary>Story prompts, choices, the branch map and exports on their own.</summary>
public sealed class StoryCoreTests
{
    private static Story Adventure(bool state = true, bool dice = true) => new()
    {
        Kind = StoryKind.Interactive,
        TrackState = state,
        Dice = dice,
        Bible = new StoryBible { Premise = "A courier crosses a flooded city.", Genre = "Adventure", PointOfView = "Second person", Cast = "Ivo: the courier" },
    };

    private static Story Novel() => new()
    {
        Kind = StoryKind.Authored,
        Bible = new StoryBible { Premise = "Two sisters inherit a lighthouse.", Tense = "Past" },
        Outline = [new StoryChapter { Title = "The Letter", Plan = "The letter arrives." }, new StoryChapter { Title = "The Crossing", Plan = "They sail." }],
    };

    private static ChatMessage User(long id, long? parent, string text, StoryTurn? turn = null) =>
        new() { Id = id, ParentId = parent, Role = ChatRole.User, Content = text, ExtraJson = turn?.ToJson() };

    private static ChatMessage Passage(long id, long? parent, string text, StoryTurn? turn = null) =>
        new() { Id = id, ParentId = parent, Role = ChatRole.Assistant, Content = text, ExtraJson = turn?.ToJson() };

    [Fact]
    public void An_interactive_layout_has_the_narrator_the_bible_and_the_state_with_the_newest_turn()
    {
        var state = new StoryState { Location = "The docks", Inventory = ["a sealed letter"], Stats = [new StoryStat { Name = "Health", Value = 8 }] };
        var layout = StoryPrompt.Layout(Adventure(), "Ivo left the depot.", state);

        var system = layout.SystemText();
        Assert.StartsWith("You are the narrator of an interactive story.", system);
        Assert.Contains("about 250 words", system);
        Assert.Contains("dice result", system);
        Assert.Contains("[Story bible]\nPremise: A courier crosses a flooded city.\nGenre: Adventure\nPoint of view: Second person\nCast:\nIvo: the courier", system);
        Assert.EndsWith("[The story so far]\nIvo left the depot.", system);
        Assert.Equal("[Current state]\nLocation: The docks\nCarrying: a sealed letter\nStats: Health 8\n\n" +
                     "[Narrate what happens next in about 250 words. End on the situation itself; do not ask the player what they do.]", layout.FinalInstruction);
        Assert.Equal("", layout.Directive);
        Assert.Equal(StoryPrompt.OpeningTurn, StoryPrompt.Layout(Adventure(), opening: true).Directive);
    }

    [Fact]
    public void State_and_dice_are_left_out_when_the_story_does_not_use_them()
    {
        var layout = StoryPrompt.Layout(Adventure(state: false, dice: false), state: new StoryState { Location = "Somewhere" });
        Assert.StartsWith("[Narrate what happens next", layout.FinalInstruction);
        Assert.DoesNotContain("dice", layout.SystemText());
        Assert.DoesNotContain("current state", layout.SystemText());
    }

    [Fact]
    public void An_authored_layout_carries_the_outline()
    {
        var system = StoryPrompt.Layout(Novel()).SystemText();
        Assert.StartsWith("You are a skilled fiction writer", system);
        Assert.Contains("[Outline]\nChapter 1: The Letter\nThe letter arrives.\n\nChapter 2: The Crossing\nThey sail.", system);
    }

    [Fact]
    public void Directions_name_their_chapter_and_say_when_it_opens()
    {
        var path = new[]
        {
            User(1, null, "", new StoryTurn { Chapter = 0 }),
            Passage(2, 1, "The letter came on a Tuesday."),
            User(3, 2, "Make the sister angry."),
            Passage(4, 3, "Ada tore it in half."),
            User(5, 4, "", new StoryTurn { Chapter = 1 }),
        };

        var sent = StoryPrompt.ForPrompt(Novel(), path);

        Assert.Equal("[Write the next passage of chapter 1: The Letter (about 250 words). This passage opens the chapter.]", sent[0].Content);
        Assert.Equal("The letter came on a Tuesday.", sent[1].Content);
        Assert.Equal("[Write the next passage of chapter 1: The Letter (about 250 words).]\nDirection: Make the sister angry.", sent[2].Content);
        Assert.Equal("[Write the next passage of chapter 2: The Crossing (about 250 words). This passage opens the chapter.]", sent[4].Content);
        Assert.Equal("", path[0].Content); // the saved turns are not changed
        Assert.Equal(1, StoryTurn.LatestChapter(path));
    }

    [Fact]
    public void A_story_without_an_outline_still_gets_a_request_for_each_passage()
    {
        var story = new Story { Kind = StoryKind.Authored };
        var sent = StoryPrompt.ForPrompt(story, [User(1, null, ""), Passage(2, 1, "Once."), User(3, 2, "Add rain.")]);
        Assert.Equal("[Write the opening passage of the story (about 250 words).]", sent[0].Content);
        Assert.Equal("[Write the next passage (about 250 words).]\nDirection: Add rain.", sent[2].Content);
    }

    [Fact]
    public void A_rolled_action_tells_the_narrator_the_result()
    {
        var sent = StoryPrompt.ForPrompt(Adventure(), [User(1, null, "Jump the gap", new StoryTurn { Roll = 4 }), User(2, 1, "Wave")]);
        Assert.Equal("Jump the gap\n\n[Dice: rolled 4 out of 20, a failure. The attempt does not work: narrate how it fails.]", sent[0].Content);
        Assert.Equal("Wave", sent[1].Content);
    }

    [Theory]
    [InlineData(1, "critical failure")]
    [InlineData(7, "failure")]
    [InlineData(8, "partial success")]
    [InlineData(13, "success")]
    [InlineData(20, "critical success")]
    public void Dice_bands(int roll, string label) => Assert.Equal(label, StoryPrompt.DiceLabel(roll));

    [Fact]
    public void The_choices_schema_asks_for_state_and_risk_only_when_the_story_uses_them()
    {
        var full = JsonNode.Parse(StoryPrompt.ChoicesSchema(Adventure()))!;
        Assert.Equal(2, (int)full["properties"]!["choices"]!["minItems"]!);
        Assert.Equal(4, (int)full["properties"]!["choices"]!["maxItems"]!);
        Assert.NotNull(full["properties"]!["choices"]!["items"]!["properties"]!["risky"]);
        Assert.NotNull(full["properties"]!["state"]!["properties"]!["inventory"]);

        var plain = JsonNode.Parse(StoryPrompt.ChoicesSchema(Adventure(state: false, dice: false)))!;
        Assert.Null(plain["properties"]!["state"]);
        Assert.Null(plain["properties"]!["choices"]!["items"]!["properties"]!["risky"]);
        Assert.Contains("\"state\"", StoryPrompt.ChoicesTask(Adventure()));
        Assert.DoesNotContain("\"state\"", StoryPrompt.ChoicesTask(Adventure(state: false)));
    }

    [Fact]
    public void Choices_are_read_tidied_and_limited()
    {
        var answer = """
            Here you go: {"choices":[{"text":"1. Open the door","risky":false},{"text":"open the door","risky":true},
            {"text":" Climb the wall ","risky":true},"Run","Hide","Shout"],
            "state":{"location":"The yard","inventory":["rope","rope","lamp"],"stats":[{"name":"Health","value":7.0},{"name":"","value":1}],"facts":["The gate is locked"]}}
            """;

        var turn = StoryPrompt.ParseChoices(answer, Adventure());

        Assert.Equal(["Open the door", "Climb the wall", "Run", "Hide"], turn.Choices!.Select(c => c.Text));
        Assert.Equal([false, true, false, false], turn.Choices!.Select(c => c.Risky));
        Assert.Equal("The yard", turn.State!.Location);
        Assert.Equal(["rope", "lamp"], turn.State.Inventory);
        Assert.Equal(7, Assert.Single(turn.State.Stats).Value);
        Assert.Equal(["The gate is locked"], turn.State.Facts);

        // Without dice or state tracking, those parts are ignored even if the model sends them.
        var plain = StoryPrompt.ParseChoices(answer, Adventure(state: false, dice: false));
        Assert.Null(plain.State);
        Assert.All(plain.Choices!, c => Assert.False(c.Risky));
    }

    [Theory]
    [InlineData("The door creaks open. What do you do next?", "The door creaks open.")]
    [InlineData("Rain falls.\n\nWhat's your move, Eli?", "Rain falls.")]
    [InlineData("It is now or never. What do you do, Eli? Head into the docks or find another way?", "It is now or never.")]
    [InlineData("The water rises. So, what will you do?", "The water rises.")]
    [InlineData("The box pulses in your hands. What now, Eli?", "The box pulses in your hands.")]
    [InlineData("She turns. \"What do you want?\"", "She turns. \"What do you want?\"")]
    [InlineData("What was that sound? You freeze.", "What was that sound? You freeze.")]
    [InlineData("What do you do?", "What do you do?")]
    public void A_closing_question_to_the_player_is_removed(string passage, string expected) =>
        Assert.Equal(expected, StoryPrompt.TrimClosingQuestion(passage));

    [Theory]
    [InlineData("I cannot do that.")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"text\":\"\"}]}")]
    [InlineData("{broken")]
    public void An_answer_without_choices_is_refused(string answer) =>
        Assert.Throws<InvalidOperationException>(() => StoryPrompt.ParseChoices(answer, Adventure()));

    [Fact]
    public void A_turn_round_trips_through_json_and_empty_turns_store_nothing()
    {
        Assert.Null(new StoryTurn().ToJson());
        var message = new ChatMessage { ExtraJson = new StoryTurn { Roll = 12, Choices = [new StoryChoice { Text = "Go", Risky = true }] }.ToJson() };
        var turn = StoryTurn.Of(message);
        Assert.Equal(12, turn.Roll);
        Assert.True(Assert.Single(turn.Choices!).Risky);
        Assert.Null(turn.State);
        Assert.Null(StoryTurn.Of(new ChatMessage { ExtraJson = "not json" }).Choices);
    }

    [Fact]
    public void A_revision_prompt_shows_nearby_text_and_the_task()
    {
        var story = Novel();
        story.Bible.Tone = "Wry";
        var prompt = StoryPrompt.RevisePrompt(story, new string('a', 2000), "She laughed.", "Then the lamp went out.", ReviseKind.Tone, "menacing");

        Assert.Equal("system", prompt[0].Role);
        Assert.Contains("Tone: Wry", prompt[0].Content);
        Assert.DoesNotContain("Premise", prompt[0].Content);
        Assert.Contains("[Text before]\n…" + new string('a', 1500) + "\n\n[Selected text]\nShe laughed.\n\n[Text after]\nThen the lamp went out.", prompt[1].Content);
        Assert.Contains("in this tone: menacing. Keep the events the same and the length about the same (about 2 words).", prompt[1].Content);
        Assert.Contains("about half its length", StoryPrompt.RevisePrompt(story, "", "x", "", ReviseKind.Shorten)[1].Content);
        // A tone with nothing typed falls back to a plain rewrite.
        Assert.Contains("in different words", StoryPrompt.RevisePrompt(story, "", "x", "", ReviseKind.Tone)[1].Content);
    }

    [Theory]
    [InlineData("\"She cackled.\"", "She laughed.", "She cackled.")]
    [InlineData("\"Go,\" she said. \"Now.\"", "\"Leave,\" she said.", "\"Go,\" she said. \"Now.\"")]
    [InlineData("\"Go,\" she said. \"Now.\"", "She told him to go.", "\"Go,\" she said. \"Now.\"")]
    [InlineData("She cackled. The lamp guttered. Then the wind rose and the whole", "She laughed.", "She cackled. The lamp guttered.")]
    [InlineData("a darker shade of", "pale blue", "a darker shade of")]
    public void A_replacement_is_tidied_before_it_is_offered(string answer, string selected, string expected) =>
        Assert.Equal(expected, StoryPrompt.TidyRevision(answer, selected));

    [Fact]
    public void The_branch_map_puts_the_first_branch_on_the_same_line_and_forks_below()
    {
        // Start, then "Go north" (written twice: Snow, Ice) and "Go south" (Sand); "Dig" follows Snow.
        var messages = new[]
        {
            Passage(1, null, "You wake."),
            User(2, 1, "Go north"), Passage(3, 2, "Snow."),
            User(4, 1, "Go south"), Passage(5, 4, "Sand."),
            User(6, 3, "Dig"), Passage(7, 6, "A door."),
            Passage(8, 2, "Ice."),
        };

        var map = BranchMap.Build(messages, activeLeafId: 7);

        Assert.Equal(3, map.Columns);
        Assert.Equal(3, map.Rows);
        var byId = map.Nodes.ToDictionary(n => n.Id);
        Assert.Equal((0, 0, "Start"), (byId[1].Column, byId[1].Row, byId[1].Label));
        Assert.Equal((1, 0, "Go north"), (byId[3].Column, byId[3].Row, byId[3].Label));
        Assert.Equal((2, 0), (byId[7].Column, byId[7].Row));
        Assert.Equal((1, 1, "Go south"), (byId[5].Column, byId[5].Row, byId[5].Label));
        Assert.Equal((1, 2, "Go north"), (byId[8].Column, byId[8].Row, byId[8].Label));
        Assert.Equal(1, byId[5].ParentId);
        Assert.Equal([1L, 3L, 7L], map.Nodes.Where(n => n.OnPath).Select(n => n.Id).Order());
        Assert.True(byId[7].IsCurrent);
        Assert.Single(map.Nodes, n => n.IsCurrent);
    }

    [Fact]
    public void Several_openings_each_start_their_own_line_and_an_empty_story_has_no_map()
    {
        var map = BranchMap.Build([Passage(1, null, "One."), Passage(2, null, "Two.")], 2);
        Assert.Equal([0, 1], map.Nodes.Select(n => n.Row));
        Assert.True(map.Nodes[1].IsCurrent);
        Assert.Equal(0, BranchMap.Build([], null).Rows);
    }

    [Fact]
    public void Markdown_export_splits_an_authored_story_at_its_chapters_and_leaves_directions_out()
    {
        var path = new[]
        {
            User(1, null, "", new StoryTurn { Chapter = 0 }), Passage(2, 1, "The letter came."),
            User(3, 2, "Make her angry"), Passage(4, 3, "Ada tore it."),
            User(5, 4, "", new StoryTurn { Chapter = 1 }), Passage(6, 5, "They sailed."),
        };

        var text = StoryExport.ToMarkdown("The Light", Novel(), path);

        Assert.Equal("# The Light\n\n## Chapter 1: The Letter\n\nThe letter came.\n\nAda tore it.\n\n## Chapter 2: The Crossing\n\nThey sailed.\n", text);
    }

    [Fact]
    public void An_interactive_export_keeps_the_players_actions_between_passages()
    {
        var path = new[] { Passage(1, null, "You wake."), User(2, 1, "Jump", new StoryTurn { Roll = 20 }), Passage(3, 2, "You *soar*.") };

        Assert.Equal("# Run\n\nYou wake.\n\n> Jump (rolled 20: critical success)\n\nYou *soar*.\n", StoryExport.ToMarkdown("Run", Adventure(), path));

        var html = StoryExport.ToHtml("Run <1>", Adventure(), path);
        Assert.Contains("<title>Run &lt;1&gt;</title>", html);
        Assert.Contains("<p class=\"action\">Jump (rolled 20: critical success)</p>", html);
        Assert.Contains("<p>You <em>soar</em>.</p>", html);
        Assert.DoesNotContain("http", html);
    }

    [Fact]
    public void The_epub_is_a_valid_package_with_one_page_per_chapter()
    {
        var path = new[]
        {
            User(1, null, "", new StoryTurn { Chapter = 0 }), Passage(2, 1, "The letter came.\n\n<b>raw</b> & \"quotes\"\n\n---\n\nline one  \nline two"),
            User(3, 2, "", new StoryTurn { Chapter = 1 }), Passage(4, 3, "They sailed."),
        };

        var bytes = StoryExport.ToEpub("The Light", Novel(), path, new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc));

        // The first entry must be the uncompressed mimetype.
        Assert.Equal("mimetype", Encoding.ASCII.GetString(bytes, 30, 8));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 8));
        using var zip = new ZipArchive(new MemoryStream(bytes));
        string Read(string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }
        Assert.Equal("application/epub+zip", Read("mimetype"));
        Assert.Contains("OEBPS/content.opf", Read("META-INF/container.xml"));

        var opf = XDocument.Parse(Read("OEBPS/content.opf"));
        XNamespace ns = "http://www.idpf.org/2007/opf";
        var hrefs = opf.Descendants(ns + "item").Select(i => (string)i.Attribute("href")!).ToList();
        Assert.Equal(["nav.xhtml", "style.css", "title.xhtml", "chapter-1.xhtml", "chapter-2.xhtml"], hrefs);
        Assert.All(hrefs, href => Assert.NotNull(zip.GetEntry("OEBPS/" + href)));
        Assert.Equal(3, opf.Descendants(ns + "itemref").Count());
        Assert.Contains("2026-11-12T09:00:00Z", opf.ToString());

        // Every page must be well-formed XML, with raw HTML from the story shown as text.
        foreach (var page in hrefs.Where(h => h.EndsWith(".xhtml")))
            XDocument.Parse(Read("OEBPS/" + page).Replace("<!DOCTYPE html>", ""));
        var first = Read("OEBPS/chapter-1.xhtml");
        Assert.Contains("<h2>Chapter 1: The Letter</h2>", first);
        Assert.Contains("&lt;b&gt;raw&lt;/b&gt;", first);
        Assert.Contains("Chapter 2: The Crossing", Read("OEBPS/nav.xhtml"));
    }

    [Theory]
    [InlineData("He ran fast.", "ran fast", 3, 8)]
    [InlineData("He *ran* fast and far.", "ran fast", 3, 10)]
    [InlineData("One line\nthen another.", "line then", 4, 9)]
    [InlineData("It was un*believ*able, really.", "unbelievable", 7, 14)]
    public void A_selection_is_found_in_its_markdown_source(string source, string selection, int start, int length) =>
        Assert.Equal((start, length), TextSpan.Find(source, selection));

    [Fact]
    public void A_selection_that_is_not_there_is_not_found()
    {
        Assert.Null(TextSpan.Find("He ran fast.", "walked"));
        Assert.Null(TextSpan.Find("He ran fast.", "   "));
    }

    [Fact]
    public void A_drafted_plan_is_read_with_its_outline_and_lists_become_lines()
    {
        var draft = StoryBuilder.Parse("""
            ```json
            {"title":"The Light","premise":"Two sisters inherit a lighthouse.","genre":"Mystery","tone":"Quiet",
             "cast":["Ada: the elder","Bea: the younger"],"world":"An island.","style_guide":"Short sentences.",
             "chapters":[{"title":"The Letter","plan":"It arrives."},{"title":"","plan":""},{"title":"The Crossing","plan":"They sail."}]}
            ```
            """);

        Assert.Equal("The Light", draft.Title);
        Assert.Equal("Ada: the elder\nBea: the younger", draft.Bible.Cast);
        Assert.Equal(["The Letter", "The Crossing"], draft.Outline.Select(c => c.Title));
        Assert.Throws<InvalidOperationException>(() => StoryBuilder.Parse("{\"title\":\"x\"}"));
        Assert.Throws<InvalidOperationException>(() => StoryBuilder.Parse("no plan here"));
        Assert.NotNull(JsonNode.Parse(StoryBuilder.Schema(StoryKind.Authored))!["properties"]!["chapters"]);
        Assert.Null(JsonNode.Parse(StoryBuilder.Schema(StoryKind.Interactive))!["properties"]!["chapters"]);
    }
}

/// <summary>Stories through the real chat service and SQLite store.</summary>
public sealed partial class ChatServiceTests
{
    private const string ChoicesJson =
        """{"choices":[{"text":"Open the door","risky":false},{"text":"Climb the wall","risky":true}],"state":{"location":"The yard","inventory":["rope"],"stats":[{"name":"Health","value":9}],"facts":["The gate is locked"]}}""";

    /// <summary>Prose for passages; the choices JSON when the request carries a schema.</summary>
    private void ScriptStory(string passage = "You wake in a yard.", string choices = ChoicesJson) =>
        _engine.Reply = _ => FakeEngine.Say(_engine.Samplers[^1].JsonSchema is null ? passage : choices);

    private async Task<long> NewStoryAsync(StoryKind kind, bool state = true, bool dice = true)
    {
        var story = new Story
        {
            Kind = kind,
            TrackState = state,
            Dice = dice,
            Bible = new StoryBible { Premise = "A courier crosses a flooded city." },
            Outline = kind == StoryKind.Authored ? [new StoryChapter { Title = "One", Plan = "It begins." }, new StoryChapter { Title = "Two", Plan = "It ends." }] : [],
        };
        return (await _chat.StartStoryAsync("The Run", story)).Id;
    }

    [Fact]
    public async Task Beginning_an_interactive_story_writes_an_opening_then_asks_for_choices()
    {
        var id = await NewStoryAsync(StoryKind.Interactive);
        ScriptStory();

        await _chat.BeginStoryAsync(id);

        var passage = Assert.Single(await ShownAsync(id));
        Assert.Equal("You wake in a yard.", passage.Content);
        Assert.Null(passage.ParentId);
        var turn = StoryTurn.Of(passage);
        Assert.Equal(["Open the door", "Climb the wall"], turn.Choices!.Select(c => c.Text));
        Assert.Equal("The yard", turn.State!.Location);

        // First request: instruction and bible, then a user turn asking for the start.
        var first = _engine.Prompts[0];
        Assert.Equal(["system", "user"], first.Select(m => m.Role));
        Assert.Contains("[Story bible]\nPremise: A courier crosses a flooded city.", first[0].Content);
        Assert.Equal(StoryPrompt.OpeningTurn, first[1].Content);
        Assert.Null(_engine.Samplers[0].JsonSchema);
        Assert.Equal("creative", _engine.Samplers[0].Id);

        // Second request: the same story with the passage, then the task, held to the schema.
        var second = _engine.Prompts[1];
        Assert.Equal(["system", "user", "assistant", "user"], second.Select(m => m.Role));
        Assert.Equal("You wake in a yard.", second[2].Content);
        Assert.StartsWith("[Task: the passage above has just ended.", second[3].Content);
        Assert.NotNull(_engine.Samplers[1].JsonSchema);
        Assert.False(_chat.IsBusy);
        Assert.Null(_chat.LastError);
        Assert.Equal("The Run", (await _store.GetAsync(id))!.Title);
    }

    [Fact]
    public async Task An_action_is_sent_with_its_dice_result_and_the_state_so_far()
    {
        var id = await NewStoryAsync(StoryKind.Interactive);
        ScriptStory();
        await _chat.BeginStoryAsync(id);
        ScriptStory("You scramble over.", """{"choices":["Run","Hide"],"state":{"location":"The street","inventory":[],"stats":[],"facts":[]}}""");

        await _chat.ActAsync(id, "Climb the wall", roll: true);

        var shown = await ShownAsync(id);
        Assert.Equal(["You wake in a yard.", "Climb the wall", "You scramble over."], shown.Select(m => m.Content));
        var roll = StoryTurn.Of(shown[1]).Roll;
        Assert.InRange(roll!.Value, 1, 20);
        var prompt = _engine.Prompts[2];
        Assert.Equal(["system", "user", "assistant", "user"], prompt.Select(m => m.Role));
        Assert.Equal($"Climb the wall\n\n[Dice: rolled {roll} out of 20, {StoryPrompt.DiceOutcome(roll.Value)}]\n\n" +
                     "[Current state]\nLocation: The yard\nCarrying: rope\nStats: Health 9\nFacts:\n- The gate is locked\n\n" +
                     StoryPrompt.NarratorReminder.Replace("{words}", "250"), prompt[3].Content);
        Assert.Equal("The street", StoryTurn.Of(shown[2]).State!.Location);
        Assert.Equal(["Run", "Hide"], StoryTurn.Of(shown[2]).Choices!.Select(c => c.Text));
    }

    [Fact]
    public async Task A_passage_that_ends_by_asking_the_player_is_saved_without_the_question()
    {
        var id = await NewStoryAsync(StoryKind.Interactive);
        ScriptStory("You wake in a yard. What do you do next?");

        await _chat.BeginStoryAsync(id);

        Assert.Equal("You wake in a yard.", Assert.Single(await ShownAsync(id)).Content);
        Assert.Equal("You wake in a yard.", _engine.Prompts[1][2].Content);
    }

    [Fact]
    public async Task No_dice_are_rolled_when_the_story_does_not_use_them()
    {
        var id = await NewStoryAsync(StoryKind.Interactive, state: false, dice: false);
        ScriptStory();
        await _chat.BeginStoryAsync(id);
        await _chat.ActAsync(id, "Climb the wall", roll: true);

        var shown = await ShownAsync(id);
        Assert.Null(shown[1].ExtraJson);
        Assert.Null(StoryTurn.Of(shown[2]).State);
        Assert.Equal("Climb the wall\n\n" + StoryPrompt.NarratorReminder.Replace("{words}", "250"), _engine.Prompts[2][3].Content);
    }

    [Fact]
    public async Task Unusable_choices_leave_the_passage_in_place_and_report_a_problem()
    {
        var id = await NewStoryAsync(StoryKind.Interactive);
        ScriptStory(choices: "Sorry, no.");

        await _chat.BeginStoryAsync(id);

        var passage = Assert.Single(await ShownAsync(id));
        Assert.Null(StoryTurn.Of(passage).Choices);
        Assert.Equal(id, _chat.LastError!.Value.ConversationId);

        ScriptStory();
        await _chat.SuggestChoicesAsync(id, passage.Id);
        Assert.Equal(2, StoryTurn.Of(Assert.Single(await ShownAsync(id))).Choices!.Count);
        Assert.Null(_chat.LastError);
    }

    [Fact]
    public async Task Another_opening_sits_beside_the_first_and_going_back_rewinds()
    {
        var id = await NewStoryAsync(StoryKind.Interactive);
        ScriptStory();
        await _chat.BeginStoryAsync(id);
        var first = Assert.Single(await ShownAsync(id));
        await _chat.ActAsync(id, "Open the door");

        ScriptStory("You wake on a roof.");
        await _chat.RegeneratePassageAsync(id, first.Id);
        var second = Assert.Single(await ShownAsync(id));
        Assert.Equal("You wake on a roof.", second.Content);
        Assert.Equal(2, new MessageTree(await _store.GetMessagesAsync(id)).Siblings(second).Count);

        await _chat.GoToAsync(id, first.Id);
        Assert.Equal(first.Id, Assert.Single(await ShownAsync(id)).Id);

        // Acting again from the rewound point starts a new branch beside the old action.
        await _chat.ActAsync(id, "Climb the wall");
        var shown = await ShownAsync(id);
        Assert.Equal("Climb the wall", shown[1].Content);
        Assert.Equal(2, new MessageTree(await _store.GetMessagesAsync(id)).Siblings(shown[1]).Count);
        var map = BranchMap.Build(await _store.GetMessagesAsync(id), (await _store.GetAsync(id))!.ActiveLeafId);
        Assert.Equal(4, map.Nodes.Count);
        Assert.Equal(3, map.Rows);
    }

    [Fact]
    public async Task A_corrected_state_is_kept_with_the_choices_and_sent_next_time()
    {
        var id = await NewStoryAsync(StoryKind.Interactive);
        ScriptStory();
        await _chat.BeginStoryAsync(id);
        var passage = Assert.Single(await ShownAsync(id));

        await _chat.SetStoryStateAsync(id, passage.Id, new StoryState { Location = "The cellar" });

        var turn = StoryTurn.Of(Assert.Single(await ShownAsync(id)));
        Assert.Equal("The cellar", turn.State!.Location);
        Assert.Equal(2, turn.Choices!.Count);
        await _chat.ActAsync(id, "Listen");
        Assert.Contains("\n\n[Current state]\nLocation: The cellar\n\n[Narrate", _engine.Prompts[2][3].Content);
    }

    [Fact]
    public async Task Writing_the_next_passage_of_an_authored_story_sends_a_direction_and_no_choices()
    {
        var id = await NewStoryAsync(StoryKind.Authored);
        _engine.Reply = _ => FakeEngine.Say("It began with rain.");

        await _chat.WriteNextAsync(id);
        await _chat.WriteNextAsync(id, "Bring in the sister.");
        await _chat.WriteNextAsync(id, "", chapter: 1);

        var shown = await ShownAsync(id);
        Assert.Equal(["", "It began with rain.", "Bring in the sister.", "It began with rain.", "", "It began with rain."], shown.Select(m => m.Content));
        Assert.Equal(3, _engine.Prompts.Count); // no second request for choices
        Assert.All(_engine.Samplers, s => Assert.Null(s.JsonSchema));
        var last = _engine.Prompts[2];
        Assert.Contains("[Outline]\nChapter 1: One", last[0].Content);
        Assert.Equal("[Write the next passage of chapter 1: One (about 250 words). This passage opens the chapter.]", last[1].Content);
        Assert.Equal("[Write the next passage of chapter 1: One (about 250 words).]\nDirection: Bring in the sister.", last[3].Content);
        Assert.Equal("[Write the next passage of chapter 2: Two (about 250 words). This passage opens the chapter.]", last[5].Content);
        AssertAlternates(last);
        Assert.Equal(1, StoryTurn.LatestChapter(shown));

        var story = (await _stories.GetAsync(id))!;
        Assert.Equal(["Chapter 1: One", "Chapter 2: Two"], StoryExport.Sections(story, shown).Select(s => s.Heading));
    }

    [Fact]
    public async Task A_direction_that_got_no_passage_is_replaced_rather_than_followed_by_another()
    {
        var id = await NewStoryAsync(StoryKind.Authored);
        _engine.Reply = _ => FakeEngine.Say("");
        await _chat.WriteNextAsync(id, "First try");
        Assert.Single(await ShownAsync(id));

        _engine.Reply = _ => FakeEngine.Say("It began.");
        await _chat.WriteNextAsync(id, "Second try");

        var shown = await ShownAsync(id);
        Assert.Equal(["Second try", "It began."], shown.Select(m => m.Content));
        AssertAlternates(_engine.Prompts[^1]);
    }

    [Fact]
    public async Task Revising_a_selection_drafts_a_replacement_and_saves_it_only_when_applied()
    {
        var id = await NewStoryAsync(StoryKind.Authored);
        _engine.Reply = _ => FakeEngine.Say("Rain fell. She laughed. Then the lamp went out.");
        await _chat.WriteNextAsync(id);
        var passage = (await ShownAsync(id))[1];
        var (start, length) = TextSpan.Find(passage.Content, "She laughed.")!.Value;

        _engine.Reply = _ => FakeEngine.Say(" She cackled. ");
        var draft = await _chat.ReviseAsync(id, passage.Id, start, length, ReviseKind.Tone, "menacing");

        Assert.Equal("She cackled.", draft);
        // Two words selected (two tokens in the fake engine): the answer may take 2 x 2 + 48 tokens, at a calmer temperature.
        Assert.Equal(52, _engine.Samplers[^1].MaxTokens);
        Assert.Equal(0.8, _engine.Samplers[^1].Temperature);
        Assert.Contains("[Selected text]\nShe laughed.", _engine.Prompts[^1][1].Content);
        Assert.Equal("Rain fell. She laughed. Then the lamp went out.", (await ShownAsync(id))[1].Content);

        await _chat.ApplyRevisionAsync(id, passage.Id, start, length, draft);
        Assert.Equal("Rain fell. She cackled. Then the lamp went out.", (await ShownAsync(id))[1].Content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _chat.ReviseAsync(id, passage.Id, 40, 100, ReviseKind.Rewrite));
    }

    [Fact]
    public async Task A_long_story_is_summarised_with_the_story_wording()
    {
        var id = (await _chat.StartStoryAsync("Long", new Story { Kind = StoryKind.Authored })).Id;
        _engine.ContextLength = 600; // 450 tokens for the prompt; each direction and passage pair is about 65
        var words = string.Join(' ', Enumerable.Repeat("word", 38));
        _engine.Reply = _ => IsSummaryRequest() ? FakeEngine.Say("SUMMARY of the tale so far") : FakeEngine.Say(words);

        for (var i = 0; i < 8; i++) await _chat.WriteNextAsync(id, $"step {i}");

        Assert.Equal("SUMMARY of the tale so far", (await _store.GetAsync(id))!.Summary);
        var last = (await ShownAsync(id))[^1];
        Assert.Contains("[The story so far]\nSUMMARY of the tale so far", last.PromptText);
        Assert.True(last.TrimmedMessages > 0);
        Assert.NotNull(last.MemoryUsed);
        var request = _engine.Prompts.Last(p => p[0].Content.Contains("summarise stories"));
        Assert.Contains("Story: word", request[1].Content);
        Assert.Contains("Direction: [Write the", request[1].Content);
        Assert.Contains("summary of the story so far, so it can be remembered after the text itself is forgotten", request[1].Content);
    }

    [Fact]
    public async Task A_story_keeps_its_settings_and_goes_with_its_conversation()
    {
        var id = await NewStoryAsync(StoryKind.Authored);
        var story = (await _stories.GetAsync(id))!;
        Assert.Equal("One", story.Outline[0].Title);
        Assert.Equal("A courier crosses a flooded city.", story.Bible.Premise);

        story.Bible.Tone = "Grim";
        story.Outline.Add(new StoryChapter { Title = "Three" });
        await _stories.SaveAsync(story);
        var saved = (await _stories.GetAsync(id))!;
        Assert.Equal("Grim", saved.Bible.Tone);
        Assert.Equal(3, saved.Outline.Count);
        Assert.Single(await _stories.ListAsync());
        Assert.Single(await _store.ListAsync(Story.Mode));

        await _store.DeleteAsync(id);
        Assert.Null(await _stories.GetAsync(id));
    }
}
