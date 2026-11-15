using DogAI.Core;
using DogAI.Core.Characters;
using DogAI.Core.Images;
using DogAI.Engine;
using Xunit.Abstractions;

namespace DogAI.Tests;

/// <summary>
/// The picture jobs with a real chat model: drafting looks, planning a portrait and planning a passage, each put
/// together into a prompt and written to the test output for reading. Skipped unless DOGAI_TEST_MODEL points at a GGUF
/// file. Nothing is drawn here.
/// </summary>
public class PictureWriterSmokeTests(ITestOutputHelper output)
{
    private static readonly Character Mara = new()
    {
        Name = "Mara",
        Description = "{{char}} is a scout of the northern marches, twenty-six, lean and weathered. Her silver hair is kept in a long braid, " +
                      "her eyes are a sharp green, and an old blade scar runs across her left cheek. She wears worn leather armor under a dark green cloak " +
                      "and never goes anywhere without her longbow. She speaks little and misses nothing.",
        Personality = "watchful, dry, loyal",
        Scenario = "{{user}} hires {{char}} as a guide through the Greywood.",
    };

    private static readonly Character Rask = new()
    {
        Name = "Rask",
        Description = "{{char}} is a wolf mercenary: an anthropomorphic grey wolf, tall and broad, with yellow eyes and a notch in his right ear. " +
                      "He favours a battered leather jacket and keeps a short sword at his hip. He laughs easily and fights dirty.",
        Personality = "brash, cheerful, shrewd",
    };

    private const string Passage =
        "The rain had not let up since dusk. Mara pushed the tavern door shut behind her and shook the water from her cloak, scanning the room out of habit. " +
        "Rask was already there, boots up on a bench by the hearth, a mug in each hand. \"You're late,\" he said, grinning, and held one out to her. " +
        "She took it without a word and sat down across from him, close enough to the fire that her cloak began to steam. " +
        "\"The bridge is out,\" she said at last. \"We go through the Greywood, or we don't go.\" Rask's grin faded. He had heard the stories about the Greywood.";

    private const string Place =
        "By morning the storm had passed. From the ridge the whole valley lay open below them: the river running silver between dark pines, " +
        "mist lifting off the water, and far off the snow on the mountains turning pink with the sunrise. Nobody spoke for a long time.";

    [Fact]
    public async Task Drafts_looks_and_plans_pictures_with_a_real_model()
    {
        var model = Environment.GetEnvironmentVariable("DOGAI_TEST_MODEL");
        if (string.IsNullOrEmpty(model)) return;

        using var http = new LoopbackHttp();
        using var engine = new EngineManager(http, new LlamaServerLocator(new AppPaths(Path.GetTempPath())));
        await engine.LoadAsync(model, new EngineOptions(ContextLength: 8192));
        Assert.True(engine.IsReady, engine.Error);
        var writer = new PictureWriter(engine);
        var illustrious = CheckpointSettings.ForFamily(ImageFamily.Illustrious);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // 1. Looks drafted from each card.
        var looks = new Dictionary<string, Looks>();
        foreach (var card in new[] { Mara, Rask })
        {
            clock.Restart();
            var draft = await writer.DraftLooksAsync(card.Name, PictureWriter.About(card), PromptStyle.Tags);
            output.WriteLine($"LOOKS {card.Name} ({clock.Elapsed.TotalSeconds:0.0} s): {draft.Tags}");
            Note(draft.Notes);
            Assert.Matches("^1(girl|boy|other), .+", draft.Tags);
            looks[card.Name] = new Looks(draft.Tags);
        }
        Assert.StartsWith("1girl", looks["Mara"].Tags);
        Assert.Contains("hair", looks["Mara"].Tags);

        // 2. A portrait of each, with their looks coming from the card.
        foreach (var card in new[] { Mara, Rask })
        {
            clock.Restart();
            var scene = await writer.PortraitAsync(card.Name, PictureWriter.About(card), hasLooks: true, PromptStyle.Tags);
            var picture = PicturePrompt.Assemble(scene, new PictureOrder(illustrious, [new PersonLooks(card.Name, looks[card.Name])]));
            output.WriteLine($"PORTRAIT {card.Name} ({clock.Elapsed.TotalSeconds:0.0} s): {scene.Moment}");
            output.WriteLine($"  PROMPT: {picture.Request.Prompt}");
            Note(picture.Notes);
            Assert.Equal(card.Name, Assert.Single(scene.People).Name);
            Assert.StartsWith(illustrious.QualityBefore + ", 1", picture.Request.Prompt);
            Assert.Contains("solo", picture.Subject);
            Assert.Empty(picture.Request.Problems());
        }

        // 3. One moment of a passage, three times (what "Try again" gives): Mara's looks from her card, Rask's from the model.
        for (var i = 0; i < 3; i++)
        {
            clock.Restart();
            var scene = await writer.SceneAsync(Passage, [new CastEntry("Mara", true, PictureWriter.About(Mara)), new CastEntry("Rask", false, PictureWriter.About(Rask))], PromptStyle.Tags,
                context: new SceneContext(Setting: "A low-fantasy border country."));
            var picture = PicturePrompt.Assemble(scene, new PictureOrder(illustrious, [new PersonLooks("Mara", looks["Mara"])], Shape: PictureShape.Wide));
            output.WriteLine($"PASSAGE {i + 1} ({clock.Elapsed.TotalSeconds:0.0} s): {scene.Moment}");
            output.WriteLine($"  PEOPLE: {string.Join("; ", scene.People.Select(p => $"{p.Name} ({p.Kind})"))}");
            output.WriteLine($"  PROMPT: {picture.Request.Prompt}");
            Note(picture.Notes);
            Assert.True(scene.HasContent);
            Assert.InRange(scene.People.Count, 0, 2);
            Assert.All(scene.People, p => Assert.Contains(p.Name, new[] { "Mara", "Rask", PictureScene.SomeoneElse }));
            Assert.Empty(picture.Request.Problems());
            // Her hair is her card's, whatever the model thought.
            if (scene.People.Any(p => p.Name == "Mara")) Assert.Contains(looks["Mara"].Pieces()[1], picture.Subject);
        }

        // 4. A passage with nobody to show.
        clock.Restart();
        var valley = await writer.SceneAsync(Place, [new CastEntry("Mara", true), new CastEntry("Rask", false)], PromptStyle.Tags);
        var wide = PicturePrompt.Assemble(valley, new PictureOrder(illustrious, Shape: PictureShape.Wide));
        output.WriteLine($"PLACE ({clock.Elapsed.TotalSeconds:0.0} s): {valley.Moment}");
        output.WriteLine($"  PEOPLE: {valley.People.Count}");
        output.WriteLine($"  PROMPT: {wide.Request.Prompt}");
        Note(wide.Notes);

        // 5. The same portrait for a checkpoint that reads sentences.
        clock.Restart();
        var sdxl = CheckpointSettings.ForFamily(ImageFamily.Sdxl);
        var phrase = await writer.DraftLooksAsync("Mara", PictureWriter.About(Mara), PromptStyle.Sentences);
        var plain = await writer.PortraitAsync("Mara", PictureWriter.About(Mara), hasLooks: true, PromptStyle.Sentences);
        var sentence = PicturePrompt.Assemble(plain, new PictureOrder(sdxl, [new PersonLooks("Mara", new Looks(phrase.Tags))]));
        output.WriteLine($"SENTENCES ({clock.Elapsed.TotalSeconds:0.0} s): looks: {phrase.Tags}");
        output.WriteLine($"  PROMPT: {sentence.Request.Prompt}");
        Assert.Contains(phrase.Tags, sentence.Request.Prompt);

        await engine.UnloadAsync();

        void Note(TagNotes notes)
        {
            if (notes.Corrected.Count > 0) output.WriteLine("  corrected: " + string.Join("; ", notes.Corrected.Select(c => $"{c.Written} -> {c.Used}")));
            if (notes.Dropped.Count > 0) output.WriteLine("  dropped: " + string.Join(", ", notes.Dropped));
        }
    }
}
