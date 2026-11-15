using System.Text.Json.Nodes;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Stories;

namespace DogAI.Tests;

/// <summary>
/// M18 step 3: the place in story and chat pictures. What the planner is given (the setting to draw, what happened just
/// before, the last picture's place), the place line of the form, place and light words in the prompt, and the place kept
/// with a picture.
/// </summary>
public sealed class PicturePlaceTests
{
    private static readonly CheckpointSettings Illustrious = CheckpointSettings.ForFamily(ImageFamily.Illustrious);

    private static readonly TagList Tags = TagList.Of("1girl", "1boy", "solo", "forest", "outdoors", "indoors", "campfire", "moss", "fern", "tree", "log",
        "pebble", "night", "moonlight", "fog", "tavern", "candlelight", "beach", "dawn", "sitting", "smile", "cowboy shot", "wide shot", "scenery", "sunset");

    private static ChatMessage Passage(long id, string text, long? picture = null, IReadOnlyList<string>? place = null, IReadOnlyList<string>? light = null)
    {
        var turn = new StoryTurn { Picture = picture };
        if (place is not null || light is not null) turn.Where = new PicturePlace(place ?? [], light ?? []);
        return new ChatMessage { Id = id, ParentId = id > 1 ? id - 1 : null, Role = ChatRole.Assistant, Content = text, ExtraJson = turn.ToJson() };
    }

    private static ChatMessage Turn(long id, string text) => new() { Id = id, ParentId = id > 1 ? id - 1 : null, Role = ChatRole.User, Content = text };

    private static PictureScene Scene(string[] place, string[] light, int people = 1) =>
        new("A moment.", Enumerable.Range(0, people).Select(i => new ScenePerson(i == 0 ? "Mara" : "Rask", i == 0 ? "girl" : "boy", [], [], ["smile"], ["sitting"])).ToList(),
            place, light, ["cowboy shot"]);

    // ---- What the planner is given ----

    [Fact]
    public void The_blocks_come_in_order_each_with_its_own_heading_and_room()
    {
        var blocks = PictureWriter.SceneBlocks(new SceneContext(SceneSource.Story, "World: a pine forest.", "They sat by the campfire.",
            new PicturePlace(["forest", "campfire"], ["night"])));

        var setting = blocks.IndexOf("Setting: where the story takes place. It is to be drawn: take the place, the light and the weather from it, unless the passage clearly says this moment is somewhere else.\nWorld: a pine forest.", StringComparison.Ordinal);
        var before = blocks.IndexOf("What happened just before (it tells where they are):\nThey sat by the campfire.", StringComparison.Ordinal);
        var last = blocks.IndexOf("The last picture of the story took place in: forest, campfire, night. Stay there unless the passage or the setting says otherwise.", StringComparison.Ordinal);
        Assert.True(setting >= 0 && before > setting && last > before, blocks);
        Assert.DoesNotContain("do not draw", blocks);

        // An empty block is left out; a chat says "this chat" and "what was said".
        Assert.Equal("", PictureWriter.SceneBlocks(new SceneContext()));
        var chat = PictureWriter.SceneBlocks(new SceneContext(SceneSource.Chat, Before: "Sam: Hi."));
        Assert.Equal("What was said just before (it tells where they are):\nSam: Hi.\n\n", chat);
        Assert.Contains("where this chat takes place", PictureWriter.SceneBlocks(new SceneContext(SceneSource.Chat, Setting: "A beach.")));

        // Each block has its own room: a long setting does not push out what happened before, and "before" keeps its end.
        var full = PictureWriter.SceneBlocks(new SceneContext(SceneSource.Story, new string('s', 5000), "start " + new string('b', 3000) + " the very end."));
        Assert.Contains(new string('s', PictureWriter.MaxSettingLength), full);
        Assert.DoesNotContain(new string('s', PictureWriter.MaxSettingLength + 1), full);
        Assert.Contains("the very end.", full);
        Assert.DoesNotContain("start", full);
        Assert.True(PictureWriter.CutStart(new string('x', 3000), 100).Length <= 100);
        Assert.StartsWith("…", PictureWriter.CutStart("one two three four five six seven", 20));
        Assert.Equal("short", PictureWriter.CutStart(" short ", 20));
    }

    [Fact]
    public void A_story_passage_gets_the_end_of_the_passage_before_and_the_turn_that_asked_for_it()
    {
        var story = new Story { Kind = StoryKind.Interactive, Bible = new StoryBible { World = "", Premise = "Two travellers." } };
        var path = new List<ChatMessage>
        {
            Passage(1, "They made camp in the forest. The campfire crackled under the pines."),
            Turn(2, "Read the letter"),
            Passage(3, "\"It is from my sister,\" she said."),
            Turn(4, "Ask about the sister"),
            Passage(5, "\"She lives in the city,\" Mara said."),
        };

        // The passage before, then the action, as typed: never the passage itself, never a later one.
        Assert.Equal("They made camp in the forest. The campfire crackled under the pines.\n\nThe reader's action: Read the letter", Illustrations.Before(story, path, path[2]));
        Assert.Equal("\"It is from my sister,\" she said.\n\nThe reader's action: Ask about the sister", Illustrations.Before(story, path, path[4]));
        Assert.Equal("", Illustrations.Before(story, path, path[0]));
        Assert.Equal("", Illustrations.Before(story, path, Passage(99, "Not on the path.")));

        // An authored story's turn is the author's direction; a long passage keeps its end.
        var authored = new Story { Kind = StoryKind.Authored };
        var longPath = new List<ChatMessage> { Passage(1, "Once in a city. " + new string('x', 3000) + " They reached the forest."), Turn(2, "Go on"), Passage(3, "They talked.") };
        var before = Illustrations.Before(authored, longPath, longPath[2]);
        Assert.EndsWith("They reached the forest.\n\nThe author's direction: Go on", before);
        Assert.True(before.Length <= PictureWriter.MaxBeforeLength, before.Length.ToString());

        // The story's setting is the World box, then the premise.
        Assert.Equal("Premise: Two travellers.", Illustrations.Setting(story));
    }

    [Fact]
    public void The_last_place_skips_the_passage_itself_and_passages_after_it()
    {
        var path = new List<ChatMessage>
        {
            Passage(1, "Camp.", picture: 10, place: ["forest", "campfire"], light: ["night"]),
            Turn(2, "Talk"),
            Passage(3, "They talk.", picture: 11, place: ["tavern"]),
            Turn(4, "More"),
            Passage(5, "More talk.", picture: 12, place: ["beach"]),
        };

        Assert.Equal("tavern", Illustrations.LastPlace(path, path[4])!.Line);
        Assert.Equal("forest, campfire, night", Illustrations.LastPlace(path, path[2])!.Line);
        Assert.Null(Illustrations.LastPlace(path, path[0]));

        // A place without a picture (the picture was taken off) does not count.
        var gone = new List<ChatMessage> { Passage(1, "Camp.", picture: null, place: ["forest"]), Turn(2, "Talk"), Passage(3, "Talk.") };
        Assert.Null(Illustrations.LastPlace(gone, gone[2]));
    }

    [Fact]
    public async Task The_planner_is_told_the_story_setting_before_and_last_place()
    {
        var engine = new Answer("""{"moment":"They talk by the fire.","people":[],"place":["forest"],"light":["night"],"framing":"wide shot","view":"straight on"}""");
        var story = new Story { Kind = StoryKind.Interactive, Bible = new StoryBible { World = "The Greywood, an old pine forest." } };
        var path = new List<ChatMessage>
        {
            Passage(1, "They made camp; the campfire crackled.", picture: 10, place: ["forest", "campfire"], light: ["night"]),
            Turn(2, "Read the letter"),
            Passage(3, "\"It is from my sister,\" she said."),
        };

        await Illustrations.PlanAsync(new PictureWriter(engine), story, [], path, path[2], PromptStyle.Tags, PictureRating.General);

        Assert.Contains("Setting: where the story takes place. It is to be drawn", engine.User);
        Assert.Contains("World: The Greywood, an old pine forest.", engine.User);
        Assert.Contains("What happened just before (it tells where they are):\nThey made camp; the campfire crackled.", engine.User);
        Assert.Contains("The last picture of the story took place in: forest, campfire, night.", engine.User);
        Assert.EndsWith("Passage:\n\"It is from my sister,\" she said.", engine.User);
        Assert.DoesNotContain("for understanding only", engine.User);
    }

    // ---- Chats: the Scenario in its own block ----

    private static readonly string LongDescription = "{{char}} keeps the lighthouse. " + string.Concat(Enumerable.Repeat("She has weathered hands and a calm voice. ", 16));

    [Fact]
    public async Task A_long_card_keeps_its_scenario_whole_in_the_setting_not_in_about()
    {
        var engine = new Answer("""{"moment":"Nell on the sand.","people":[],"place":["beach"],"light":["dawn"],"framing":"wide shot","view":"straight on"}""");
        var nell = new Character { Id = 1, Name = "Nell", Description = LongDescription, Scenario = "{{char}} meets {{user}} on the beach below the lighthouse at dawn, the tide going out." };
        Assert.True(PictureWriter.About(nell).Length > PictureWriter.MaxCastAboutLength + 100);
        var people = ChatPictures.People([nell], new Persona { Name = "Sam" });

        Assert.Equal("Nell meets Sam on the beach below the lighthouse at dawn, the tide going out.", people[0].Setting);
        Assert.DoesNotContain("Usual setting", people[0].About);
        var path = new[] { Turn(1, "Hello."), Passage(2, "Nell waves.") };

        await ChatPictures.PlanAsync(new PictureWriter(engine), people, null, path, path[1], _ => "Nell", PromptStyle.Tags, PictureRating.General);

        Assert.Contains("Setting: where this chat takes place. It is to be drawn", engine.User);
        Assert.Contains("Nell meets Sam on the beach below the lighthouse at dawn, the tide going out.", engine.User);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(engine.User, "the tide going out"));

        // Portraits and drafted looks keep the scenario in "about".
        Assert.Contains("Usual setting: Nell meets the user", PictureWriter.About(nell));
    }

    [Fact]
    public void A_group_names_each_scenario_once_and_the_summary_counts_only_before_the_reply()
    {
        var ann = new Character { Id = 1, Name = "Ann", Scenario = "A harbour town." };
        var bo = new Character { Id = 2, Name = "Bo", Scenario = "A harbour town." };
        var cy = new Character { Id = 3, Name = "Cy", Scenario = "{{char}}'s farm." };
        var people = ChatPictures.People([ann, bo, cy], null);
        var path = new[] { Turn(1, "Hi."), Passage(2, "Hello."), Turn(3, "Where now?"), Passage(4, "The docks.") };

        Assert.Equal("Ann and Bo: A harbour town.\n\nCy: Cy's farm.", ChatPictures.Setting(people, null, path, path[3]));

        var summarised = new Conversation { Summary = "They sailed\nto the isles.", SummaryThroughId = 2 };
        Assert.EndsWith("What has happened in the chat so far: They sailed to the isles.", ChatPictures.Setting(people, summarised, path, path[3]));
        // The summary covers the reply itself or later: not given (it would tell what came after).
        Assert.DoesNotContain("sailed", ChatPictures.Setting(people, summarised, path, path[1]));
        // The summary's message is not on the branch shown: not given.
        Assert.DoesNotContain("sailed", ChatPictures.Setting(people, new Conversation { Summary = "Elsewhere.", SummaryThroughId = 99 }, path, path[3]));

        // One character: its scenario alone; none: nothing.
        Assert.Equal("A harbour town.", ChatPictures.Setting(ChatPictures.People([ann], null), null, path, path[3]));
        Assert.Equal("", ChatPictures.Setting(ChatPictures.People([new Character { Name = "Dee" }], null), null, path, path[3]));
    }

    [Fact]
    public void A_story_cast_card_leaves_its_scenario_out()
    {
        var mara = new Character { Id = 1, Name = "Mara", Description = "A scout.", Scenario = "A tavern in the city." };
        var people = Illustrations.People(new Story(), [new StoryCastMember(mara, true)]);
        Assert.Equal("A scout.", people[0].About);
        Assert.Null(people[0].Setting);
    }

    // ---- The form ----

    [Fact]
    public void The_place_line_asks_for_the_place_first_and_from_the_setting()
    {
        var scene = PictureWriter.SceneInstruction(PromptStyle.Tags, [new CastEntry("Mara", false)], 2, portrait: false);
        Assert.Contains("the place itself first", scene);
        Assert.Contains("then \"indoors\" or \"outdoors\"", scene);
        Assert.Contains("take it from the setting", scene);
        Assert.Contains($"up to {PicturePlace.MaxWritten} tags", scene);
        Assert.Contains("the place itself first", PictureWriter.SceneInstruction(PromptStyle.Sentences, [], 2, portrait: false));

        // A portrait keeps its own short line.
        var portrait = PictureWriter.SceneInstruction(PromptStyle.Tags, [new CastEntry("Mara", false)], 1, portrait: true);
        Assert.DoesNotContain("the place itself first", portrait);
        Assert.Contains("- place: where it is (", portrait);

        var schema = JsonNode.Parse(PictureScene.Schema(["Mara"], 2))!;
        Assert.Equal(PicturePlace.MaxWritten, (int)schema["properties"]!["place"]!["maxItems"]!);
    }

    // ---- The prompt ----

    [Fact]
    public void Up_to_five_place_words_and_three_light_words_and_a_light_word_leaves_the_place()
    {
        var planned = PicturePrompt.Assemble(Scene(["forest", "night", "outdoors", "campfire", "moss", "fern", "tree"], ["moonlight"]), new PictureOrder(Illustrious), Tags);

        // "night" was written as a place word: it goes to the light, and five real place words are kept.
        Assert.Equal(["forest", "outdoors", "campfire", "moss", "fern"], planned.Place!.Place);
        Assert.Equal(["moonlight", "night"], planned.Place.Light);
        Assert.Contains("sitting, forest, outdoors, campfire, moss, fern, moonlight, night, cowboy shot", planned.Subject);
        Assert.DoesNotContain("tree", planned.Subject);

        // A word in both lists is used once, as light.
        var twice = PicturePrompt.Assemble(Scene(["forest", "fog"], ["fog", "night", "moonlight", "dawn"]), new PictureOrder(Illustrious), Tags);
        Assert.Equal(["forest"], twice.Place!.Place);
        Assert.Equal(["fog", "night", "moonlight"], twice.Place.Light);

        // No place and no light: nothing to keep.
        Assert.Null(PicturePrompt.Assemble(Scene([], []), new PictureOrder(Illustrious), Tags).Place);
    }

    [Fact]
    public void A_place_of_two_tags_in_one_keeps_both()
    {
        // Seen in step 3 in the app: "forest floor" became "floor", losing the forest.
        var list = TagList.Of("forest", "floor", "kitchen", "table", "stone floor", "bed");
        Assert.Equal(["forest", "floor", "stone floor", "narrow bed", "kitchen", "table"], PicturePrompt.SplitPlaces(["forest floor", "stone floor", "narrow bed", "kitchen table"], list));
        var planned = PicturePrompt.Assemble(Scene(["forest floor", "campfire"], ["night"]), new PictureOrder(Illustrious), TagList.Of("forest", "floor", "campfire", "night", "smile", "sitting"));
        Assert.Equal(["forest", "floor", "campfire"], planned.Place!.Place);
    }

    [Fact]
    public void Pines_are_not_a_pin()
    {
        // Seen in step 3's smoke run: "pines" became "pin" by taking "-es" off. "-es" comes off only after s, x, z, o, ch, sh.
        var list = TagList.Of("pin", "pine tree", "box", "dress", "branch", "potato");
        Assert.Empty(list.Check(["pines"]).Tags);
        Assert.Equal(["box", "dress", "branch", "potato"], list.Check(["boxes", "dresses", "branches", "potatoes"]).Tags);
        Assert.DoesNotContain("pin", TagList.Bundled.Check(["pines", "old pines"]).Tags);
    }

    [Fact]
    public void A_place_typed_in_the_review_is_used_as_written()
    {
        var scene = Scene(["tavern"], ["candlelight"]);
        var typed = new PicturePlace(PicturePlace.Words("forest, outdoors,  my_own words "), PicturePlace.Words("night"));
        var planned = PicturePrompt.Assemble(scene, new PictureOrder(Illustrious, TypedPlace: typed), Tags);

        Assert.Contains("sitting, forest, outdoors, my_own words, night, cowboy shot", planned.Subject);
        Assert.DoesNotContain("tavern", planned.Subject);
        Assert.Same(typed, planned.Place);
        Assert.Empty(planned.Notes.Dropped);

        // Emptied boxes: no place at all.
        var none = PicturePrompt.Assemble(scene, new PictureOrder(Illustrious, TypedPlace: PicturePlace.None), Tags);
        Assert.Null(none.Place);
        Assert.Equal("1girl, solo, smile, sitting, cowboy shot", none.Subject);
    }

    [Fact]
    public void A_sentence_checkpoint_gets_the_same_room()
    {
        var sdxl = CheckpointSettings.ForFamily(ImageFamily.Sdxl);
        var planned = PicturePrompt.Assemble(Scene(["a pine forest", "outdoors", "a campfire", "moss", "ferns", "a log", "at night"], ["moonlight"]), new PictureOrder(sdxl), Tags);
        Assert.Equal(["a pine forest", "outdoors", "a campfire", "moss", "ferns"], planned.Place!.Place);
        Assert.Contains("a pine forest, outdoors, a campfire, moss, ferns, moonlight", planned.Subject);
    }

    [Fact]
    public void The_people_and_the_rating_are_as_before()
    {
        // At most two people still; the owner's Looks as typed; the rating words where they were.
        var scene = Scene(["forest"], ["night"], people: 2);
        var planned = PicturePrompt.Assemble(scene, new PictureOrder(Illustrious, [new PersonLooks("Mara", new Looks("1girl, Silver_Hair (long)"))], Rating: PictureRating.General), Tags);
        Assert.StartsWith("1girl, 1boy, Silver_Hair (long), smile, sitting", planned.Subject);
        Assert.Contains(Illustrious.RatingsOrDefault().General, planned.Request.Prompt);
    }

    // ---- Kept with the picture ----

    [Fact]
    public void Where_is_kept_on_the_message_and_read_cleaned()
    {
        var turn = new StoryTurn { Picture = 5, Where = new PicturePlace(["forest", "campfire"], ["night"]) };
        var read = StoryTurn.Of(new ChatMessage { Role = ChatRole.Assistant, ExtraJson = turn.ToJson() });
        Assert.Equal("forest, campfire, night", read.Where!.Line);

        // A place alone still writes the details; without a picture it is not read.
        Assert.NotNull(new StoryTurn { Place = ["forest"] }.ToJson());
        Assert.Null(new StoryTurn { Place = ["forest"] }.Where);

        // JSON on a message is untrusted: words are cut, repeats and overlong words dropped, at most five and three.
        var json = """{"picture":1,"place":["a","b","c","d","e","f","g","a\nb"],"light":["x","y","z","w","A"],"choices":null}""";
        var messy = StoryTurn.Of(new ChatMessage { ExtraJson = json }).Where!;
        Assert.Equal(["a", "b", "c", "d", "e"], messy.Place);
        Assert.Equal(["x", "y", "z"], messy.Light);
        var tooLong = StoryTurn.Of(new ChatMessage { ExtraJson = $$"""{"picture":1,"place":["{{new string('q', 200)}}"]}""" });
        Assert.Null(tooLong.Where);
    }

    [Fact]
    public void A_place_follows_a_try_while_its_prompt_holds_it()
    {
        var place = new PicturePlace(["tree (plant)", "forest"], ["night"]);
        Assert.True(place.StillIn("1girl, tree \\(plant\\), forest, night"));
        Assert.True(place.StillIn("1girl, TREE (PLANT), Forest, night, extra"));
        Assert.False(place.StillIn("1girl, forest, night"));
    }

    [Fact]
    public async Task Try_again_and_make_larger_keep_the_place_and_a_redraw_that_loses_it_drops_it()
    {
        var tries = new PictureTries(new PortraitTests.WebpEngine());
        var root = Path.Combine(Path.GetTempPath(), "dogai-image-root");
        var place = new PicturePlace(["forest"], ["night"]);
        var request = ImagePrompt.Compose(Illustrious, "1girl, forest, night", rating: PictureRating.General);
        var first = await tries.DrawAsync(root, "il.safetensors", Illustrious, request, PictureQuality.Standard, PictureShape.Wide, PictureSize.Small, "x", TagNotes.None, place: place);

        Assert.Same(place, first.Place);
        Assert.Same(place, (await tries.TryAgainAsync(root, first)).Place);
        Assert.Same(place, (await tries.MakeLargerAsync(root, first, PictureLevels.FineScale)).Place);
        Assert.Same(place, (await tries.RedrawAsync(root, first, first.Base.Prompt + ", smile", PictureQuality.Standard, PictureShape.Wide, PictureSize.Small)).Place);
        Assert.Null((await tries.RedrawAsync(root, first, "1girl, beach, night", PictureQuality.Standard, PictureShape.Wide, PictureSize.Small)).Place);
    }

    /// <summary>A chat model that answers one fixed text and remembers what it was asked.</summary>
    private sealed class Answer(string answer) : IChatEngine
    {
        public bool IsReady => true;
        public string? ModelName => "fake";
        public int ContextLength => 8192;
        public string User { get; private set; } = "";

        public Task<int> CountTokensAsync(string text, CancellationToken ct = default) => Task.FromResult(text.Length / 4);
        public Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct = default) => Task.FromResult("");

        public IAsyncEnumerable<ChatDelta> StreamChatAsync(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, CancellationToken ct = default)
        {
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

/// <summary>The place kept with a picture, through the real chat service and SQLite stores.</summary>
public sealed partial class ChatServiceTests
{
    private async Task<StoryTurn> TurnOfAsync(long story, long passage) =>
        StoryTurn.Of((await _store.GetMessagesAsync(story)).Single(m => m.Id == passage));

    [Fact]
    public async Task A_kept_picture_keeps_its_place_until_it_goes()
    {
        var (story, passage) = await IllustratedStoryAsync();
        var tries = new PictureTries(new PortraitTests.WebpEngine());
        var forest = new PicturePlace(["forest", "campfire"], ["night"]);

        var first = await Illustrations.KeepAsync(PictureStore, _chat, story, passage, DrawTry(tries) with { Place = forest });
        Assert.Equal("forest, campfire, night", (await TurnOfAsync(story, passage)).Where!.Line);

        // The kept picture chosen again knows no place of its own: the one recorded stays.
        var adopted = tries.Adopt((await PictureStore.GetAsync(first))!, CheckpointSettings.ForFamily(ImageFamily.Illustrious));
        Assert.Null(adopted.Place);
        await Illustrations.KeepAsync(PictureStore, _chat, story, passage, adopted);
        Assert.Equal("forest, campfire, night", (await TurnOfAsync(story, passage)).Where!.Line);

        // Another picture with no known place: the old place goes with the old picture.
        await Illustrations.KeepAsync(PictureStore, _chat, story, passage, DrawTry(tries));
        Assert.Null((await TurnOfAsync(story, passage)).Where);
        Assert.Null((await TurnOfAsync(story, passage)).Place);

        await Illustrations.KeepAsync(PictureStore, _chat, story, passage, DrawTry(tries) with { Place = new PicturePlace(["beach"], []) });
        Assert.Equal("beach", (await TurnOfAsync(story, passage)).Where!.Line);

        // Removing the picture removes its place, so it never tells the next picture where the last one was.
        await Illustrations.RemoveAsync(PictureStore, _chat, story, passage);
        var after = await TurnOfAsync(story, passage);
        Assert.Null(after.Where);
        Assert.Null(after.Place);
    }
}
