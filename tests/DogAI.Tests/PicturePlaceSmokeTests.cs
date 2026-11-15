using DogAI.Core;
using DogAI.Core.Characters;
using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Stories;
using DogAI.Engine;
using Xunit.Abstractions;

namespace DogAI.Tests;

/// <summary>
/// M18 step 3 with a real chat model: the trial's place cases through the app's own planner input (the setting, what
/// happened just before, the last picture's place). Each case is planned three times and printed; a case passes when two
/// of three runs get it right (a model's answer varies). Skipped unless DOGAI_TEST_MODEL points at a GGUF file. Nothing
/// is drawn here. Run with <c>--filter PicturePlaceSmokeTests --logger "console;verbosity=detailed"</c> to read the prompts.
/// </summary>
public class PicturePlaceSmokeTests(ITestOutputHelper output)
{
    private static readonly CheckpointSettings Illustrious = CheckpointSettings.ForFamily(ImageFamily.Illustrious);

    private static ChatMessage Passage(long id, string text, long? picture = null, PicturePlace? where = null)
    {
        var turn = new StoryTurn { Picture = picture };
        if (where is not null) turn.Where = where;
        return new ChatMessage { Id = id, ParentId = id > 1 ? id - 1 : null, Role = ChatRole.Assistant, Content = text, ExtraJson = turn.ToJson() };
    }

    private static ChatMessage Turn(long id, string text) => new() { Id = id, ParentId = id > 1 ? id - 1 : null, Role = ChatRole.User, Content = text };

    private const string Campfire =
        "They made camp where the old pines thinned out. Rask piled dry branches and Mara struck a spark, and soon the campfire was crackling, " +
        "throwing orange light over the moss and the roots. Above them the stars came out one by one; somewhere an owl called.";

    private const string Letter =
        "Mara unfolded the letter and read it twice before she spoke. \"It is from my sister,\" she said. \"She says the bridge is guarded now.\" " +
        "Rask frowned and asked who would guard a bridge nobody used. \"Someone who knows we are coming,\" Mara said, and folded the letter away.";

    private const string Alone =
        "Mara sat alone, the letter open on her knees, reading it again by the fire's light. Her lips moved over one line, then she folded it small and tucked it into her glove.";

    private const string Cabin =
        "The woodcutter let them in without a word. Inside, the cabin smelled of resin and smoke; a kettle hung over the hearth, and a narrow bed stood under the one small window. " +
        "Mara set her bow against the table and sat down on the bench while the old man poured them each a cup.";

    private static readonly Character Mara = new() { Id = 1, Name = "Mara", Description = "A scout with silver hair in a braid and a green cloak." };
    private static readonly Character Rask = new() { Id = 2, Name = "Rask", Description = "A grey wolf mercenary in a leather jacket." };

    [Fact]
    public async Task The_trial_place_cases_with_a_real_model()
    {
        var model = Environment.GetEnvironmentVariable("DOGAI_TEST_MODEL");
        if (string.IsNullOrEmpty(model)) return;

        using var http = new LoopbackHttp();
        using var engine = new EngineManager(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())));
        await engine.LoadAsync(model, new EngineOptions(ContextLength: 8192));
        Assert.True(engine.IsReady, engine.Error);
        var writer = new PictureWriter(engine);
        var story = new Story { Kind = StoryKind.Interactive, Bible = new StoryBible { Premise = "Two travellers carry a message north." } };
        var cast = Illustrations.People(story, [new StoryCastMember(Mara, true), new StoryCastMember(Rask, false)]);

        // 1. The place was set only in the passage before; World box empty; this passage is only talk.
        var before = new List<ChatMessage> { Passage(1, Campfire), Turn(2, "Read the letter"), Passage(3, Letter) };
        var forest = await RunAsync("PLACE ONLY BEFORE", () => Illustrations.PlanAsync(writer, story, cast, before, before[2], PromptStyle.Tags, PictureRating.General),
            p => Has(p, "forest", "campfire", "tree", "pine", "outdoors") && !Has(p, "indoors", "tavern", "bedroom", "room"));
        Assert.True(forest >= 2, $"forest {forest} of 3");

        // 2. The last picture's place, no passage before that says it (the passage before is talk too).
        var last = new List<ChatMessage>
        {
            Passage(1, "\"Who sent it?\" Rask asked. Mara did not answer at once.", picture: 7, where: new PicturePlace(["forest", "outdoors", "campfire"], ["night"])),
            Turn(2, "Read the letter"), Passage(3, Letter),
        };
        var stayed = await RunAsync("LAST PICTURE'S PLACE", () => Illustrations.PlanAsync(writer, story, cast, last, last[2], PromptStyle.Tags, PictureRating.General),
            p => Has(p, "forest", "campfire", "tree", "pine", "outdoors") && !Has(p, "indoors", "tavern", "bedroom", "room"));
        Assert.True(stayed >= 2, $"last place {stayed} of 3");

        // 3. A passage that moves indoors wins over the setting and the last place.
        var forestStory = new Story { Kind = StoryKind.Interactive, Bible = new StoryBible { World = "The Greywood: an old pine forest, misty and dark." } };
        var inside = new List<ChatMessage>
        {
            Passage(1, Campfire, picture: 7, where: new PicturePlace(["forest", "outdoors", "campfire"], ["night"])),
            Turn(2, "Knock on the cabin door"), Passage(3, Cabin),
        };
        var indoors = await RunAsync("MOVES INDOORS", () => Illustrations.PlanAsync(writer, forestStory, cast, inside, inside[2], PromptStyle.Tags, PictureRating.General),
            p => Has(p, "indoors", "cabin", "room", "hearth", "fireplace", "table", "bed", "window", "wooden"));
        Assert.True(indoors >= 2, $"indoors {indoors} of 3");

        // 4. One person in the passage: nobody else is added.
        var alone = new List<ChatMessage> { Passage(1, Campfire), Turn(2, "Read it again"), Passage(3, Alone) };
        var one = await RunAsync("ONE PERSON", () => Illustrations.PlanAsync(writer, story, cast, alone, alone[2], PromptStyle.Tags, PictureRating.General),
            (p, scene) => scene.People.Count <= 1);
        Assert.True(one >= 2, $"one person {one} of 3");

        // 5. A character chat with a long card: the beach Scenario reaches the planner whole.
        var nell = new Character
        {
            Id = 3, Name = "Nell",
            Description = "{{char}} keeps the old lighthouse on the point. " + string.Concat(Enumerable.Repeat("She is patient and quiet, with weathered hands and a slow smile; she knows every rock of the coast. ", 6)),
            Personality = "calm, wry, protective",
            Scenario = "{{char}} meets {{user}} on the beach below the lighthouse at dawn, the tide going out.",
        };
        Assert.True(PictureWriter.About(nell).Length > PictureWriter.MaxCastAboutLength);
        var people = ChatPictures.People([nell], new Persona { Name = "Sam" });
        var chat = new[] { Passage(1, "Morning. You're up early."), Turn(2, "Couldn't sleep. What's that you're carrying?"), Passage(3, "Nell lifts the basket so you can see. \"Mussels. Breakfast, if you help me pick the rest.\"") };
        var beach = await RunAsync("CHAT, LONG CARD", () => ChatPictures.PlanAsync(writer, people, null, chat, chat[2], m => m.Role == ChatRole.User ? "Sam" : "Nell", PromptStyle.Tags, PictureRating.General),
            p => Has(p, "beach", "shore", "sand", "coast", "ocean", "sea"));
        Assert.True(beach >= 2, $"beach {beach} of 3");

        await engine.UnloadAsync();
    }

    private Task<int> RunAsync(string name, Func<Task<PictureScene>> plan, Func<PicturePlace?, bool> right) =>
        RunAsync(name, plan, (p, _) => right(p));

    private async Task<int> RunAsync(string name, Func<Task<PictureScene>> plan, Func<PicturePlace?, PictureScene, bool> right)
    {
        var good = 0;
        for (var i = 0; i < 3; i++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var scene = await plan();
            var picture = PicturePrompt.Assemble(scene, new PictureOrder(Illustrious, Shape: PictureShape.Wide));
            var ok = right(picture.Place, scene);
            if (ok) good++;
            output.WriteLine($"{name} {i + 1} ({clock.Elapsed.TotalSeconds:0.0} s) {(ok ? "right" : "WRONG")}: {scene.Moment}");
            output.WriteLine($"  PEOPLE {scene.People.Count}: {string.Join("; ", scene.People.Select(p => p.Name))}");
            output.WriteLine($"  WRITTEN place: {string.Join(", ", scene.Place)} | light: {string.Join(", ", scene.Light)}");
            output.WriteLine($"  KEPT {picture.Place?.Line}");
            output.WriteLine($"  PROMPT {picture.Subject}");
        }
        return good;
    }

    private static bool Has(PicturePlace? place, params string[] words) =>
        place is not null && place.Place.Concat(place.Light).Any(w => words.Any(x => w.Contains(x, StringComparison.OrdinalIgnoreCase)));
}
