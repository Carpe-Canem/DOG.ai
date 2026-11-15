using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DogAI.Core.Characters;
using DogAI.Core.Chat;

namespace DogAI.Core.Images;

/// <summary>Someone who may appear in a picture, as the chat model is told about them.</summary>
/// <param name="HasLooks">True when their looks come from their card, so the model must not describe them.</param>
/// <param name="About">A few words on who they are and what they usually wear (from their card), so the model does not invent it.</param>
public sealed record CastEntry(string Name, bool HasLooks, string? About = null);

/// <summary>Looks the chat model drafted from a description, for the owner to change before saving.</summary>
public sealed record LooksDraft(string Tags, TagNotes Notes);

/// <summary>
/// DOG.ai's model jobs for pictures: the loaded chat model plans a picture (<see cref="PortraitAsync"/>,
/// <see cref="SceneAsync"/>) or drafts a character's looks (<see cref="DraftLooksAsync"/>). Each answer is JSON that
/// must match a schema, is read leniently and checked before use. The model is told not to think first. Nothing here
/// draws: the scene goes to <see cref="PicturePrompt.Assemble"/>, and the drawing starts only after this job has
/// ended, because a picture may need the chat model's room on the graphics card.
/// </summary>
public sealed partial class PictureWriter(IChatEngine engine)
{
    /// <summary>Tokens the model may write. A filled form is 150 to 300; the rest is slack for a model that thinks anyway.</summary>
    public const int MaxTokens = 1200;

    public const int MaxAboutLength = 6000;
    public const int MaxPassageLength = 8000;
    public const int MaxCastAboutLength = 500;

    /// <summary>What a card says about its character, for <see cref="PortraitAsync"/> and <see cref="DraftLooksAsync"/>.</summary>
    public static string About(Character c)
    {
        var text = new StringBuilder(CharacterPrompt.Fill(c.Description, c.Name, "the user").Trim());
        if (c.Personality.Trim().Length > 0) text.Append("\n\nPersonality: ").Append(CharacterPrompt.Fill(c.Personality, c.Name, "the user").Trim());
        if (c.Scenario.Trim().Length > 0) text.Append("\n\nUsual setting: ").Append(CharacterPrompt.Fill(c.Scenario, c.Name, "the user").Trim());
        return text.ToString().Trim();
    }

    /// <summary>Plans a portrait of one person from what their card says about them.</summary>
    /// <exception cref="InvalidOperationException">No model is loaded, or its answer was not usable.</exception>
    /// <param name="rating">The rating from Settings → Pictures: the model is told what the picture may show.</param>
    public async Task<PictureScene> PortraitAsync(string name, string about, bool hasLooks, PromptStyle style, PictureRating rating = PictureRating.General,
        CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Give them a name first.");
        about = Cut(about, MaxAboutLength);
        var answer = await AskAsync(
            SceneInstruction(style, [new CastEntry(name, hasLooks)], 1, portrait: true, rating),
            $"Character: {name}\n\n{(about.Length > 0 ? about : "(Nothing is written about them yet.)")}",
            PictureScene.Schema([name], 1, onePersonExactly: true), 0.7, ct).ConfigureAwait(false);
        var scene = PictureScene.Parse(answer);
        // A portrait always shows its person, under their own name, whatever the model answered.
        var person = scene.People.FirstOrDefault() ?? new ScenePerson(name, "other", [], [], [], []);
        return scene with { People = [person with { Name = name }] };
    }

    /// <summary>
    /// Plans a picture of one moment of a passage. <paramref name="cast"/> are the people who may be in it;
    /// <paramref name="background"/> is anything that helps to understand the passage (the story's setting) without being drawn.
    /// </summary>
    /// <exception cref="InvalidOperationException">No model is loaded, or its answer was not usable.</exception>
    public async Task<PictureScene> SceneAsync(string passage, IReadOnlyList<CastEntry> cast, PromptStyle style, int maxPeople = 2,
        string? background = null, PictureRating rating = PictureRating.General, CancellationToken ct = default)
    {
        passage = Cut(passage, MaxPassageLength);
        if (passage.Length == 0) throw new InvalidOperationException("There is nothing to illustrate yet.");
        maxPeople = Math.Clamp(maxPeople, 1, PictureScene.MaxPeople);
        var names = cast.Select(c => c.Name.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var user = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(background))
            user.Append("Background, for understanding only (do not draw it):\n").Append(Cut(background, 1500)).Append("\n\n");
        var known = cast.Where(c => !string.IsNullOrWhiteSpace(c.About)).ToList();
        if (known.Count > 0)
        {
            user.Append("Who these people are (use it for what they wear when the passage does not say):\n");
            foreach (var c in known) user.Append("- ").Append(c.Name.Trim()).Append(": ").Append(Cut(c.About, MaxCastAboutLength).ReplaceLineEndings(" ")).Append('\n');
            user.Append('\n');
        }
        user.Append("Passage:\n").Append(passage);
        var answer = await AskAsync(SceneInstruction(style, cast, maxPeople, portrait: false, rating), user.ToString(),
            PictureScene.Schema(names, maxPeople), 0.7, ct).ConfigureAwait(false);
        var scene = PictureScene.Parse(answer);
        scene = scene with
        {
            People = scene.People.Take(maxPeople)
                .Select(p => p with { Name = names.FirstOrDefault(n => n.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) ?? PictureScene.SomeoneElse })
                .ToList(),
        };
        if (!scene.HasContent) throw new InvalidOperationException("The model's picture description was empty. Try again.");
        return scene;
    }

    /// <summary>
    /// Drafts a character's looks from their description: lasting, visible features only. For tag checkpoints every
    /// tag is checked against <paramref name="tags"/> (the bundled list when null). Nothing is saved.
    /// </summary>
    /// <exception cref="InvalidOperationException">No model is loaded, or its answer was not usable.</exception>
    public async Task<LooksDraft> DraftLooksAsync(string name, string about, PromptStyle style, TagList? tags = null, CancellationToken ct = default)
    {
        about = Cut(about, MaxAboutLength);
        if (about.Length == 0) throw new InvalidOperationException("Write a description first: the looks are drafted from it.");
        var answer = await AskAsync(LooksInstruction(style), $"Character: {name.Trim()}\n\n{about}", LooksSchema(style), 0.4, ct).ConfigureAwait(false);
        var o = Object(answer, "The model did not describe any looks. Try again, or try another model.");
        if (style == PromptStyle.Sentences)
        {
            var phrase = o["looks"] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim().TrimEnd('.') : "";
            if (phrase.Length == 0) throw new InvalidOperationException("The model did not describe any looks. Try again.");
            return new LooksDraft(new Looks(phrase).Clean().Tags, TagNotes.None);
        }

        var written = o["looks"] switch
        {
            JsonArray a => a.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var s) ? s : "").ToList(),
            JsonValue x when x.TryGetValue<string>(out var s) => s.Split(',').ToList(),
            _ => [],
        };
        // What the person is comes from "kind" and is written by code, so a count tag among the looks is not wanted twice.
        var (kept, notes) = (tags ?? TagList.Current).Check(written.SelectMany(w => w.Split(',')).Where(w => !PicturePrompt.IsCountTag(w)), 14);
        if (kept.Count == 0) throw new InvalidOperationException("The model did not describe any looks. Try again.");
        var kind = o["kind"] is JsonValue k && k.TryGetValue<string>(out var kindText) && PictureScene.Kinds.Contains(kindText.ToLowerInvariant())
            ? kindText.ToLowerInvariant()
            : "other";
        return new LooksDraft(new Looks($"1{kind}, " + string.Join(", ", kept.Select(TagList.ForPrompt))).Clean().Tags, notes);
    }

    /// <summary>The longest sentence the prompt helper takes.</summary>
    public const int MaxSentenceLength = 1500;

    /// <summary>The most tags the prompt helper keeps.</summary>
    public const int MaxPromptTags = 30;

    /// <summary>The longest prompt the helper keeps for a checkpoint that reads sentences.</summary>
    public const int MaxPromptLength = 600;

    /// <summary>
    /// "Write it for me…" (Images section, M16 step 4): turns the owner's description of a picture into a prompt for the
    /// owner to put in the box and change. For tag checkpoints the model writes tags, and every one is checked against
    /// <paramref name="tags"/> (<see cref="TagList.Current"/> when null): real ones kept, near misses corrected, the rest
    /// dropped (left-out tags included). No quality words, rating words or LoRA trigger words are asked for: the owner's
    /// boxes hold their own. Nothing is drawn or saved here.
    /// </summary>
    /// <param name="rating">The rating from Settings → Pictures: the model is told how far the picture may go.</param>
    /// <exception cref="InvalidOperationException">No model is loaded, the sentence is empty, or the answer was not usable.</exception>
    public async Task<PromptDraft> PromptAsync(string sentence, PromptStyle style, PictureRating rating = PictureRating.General, TagList? tags = null,
        CancellationToken ct = default)
    {
        sentence = Cut(sentence, MaxSentenceLength);
        if (sentence.Length == 0) throw new InvalidOperationException("Describe the picture first, in a sentence or two.");
        var answer = await AskAsync(PromptInstruction(style, rating), "Description of the picture:\n" + sentence, PromptSchema(style), 0.6, ct).ConfigureAwait(false);
        var o = Object(answer, "The model did not write a prompt. Try again, or try another model.");
        var moment = o["moment"] is JsonValue m && m.TryGetValue<string>(out var said) ? said.Trim() : "";
        if (moment.Length > 400) moment = moment[..400];
        if (style == PromptStyle.Sentences)
        {
            var text = o["prompt"] is JsonValue p && p.TryGetValue<string>(out var s) ? string.Join(' ', s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim() : "";
            if (text.Length > MaxPromptLength) text = text[..MaxPromptLength].TrimEnd();
            if (text.Length == 0) throw new InvalidOperationException("The model did not write a prompt. Try again, or describe it differently.");
            return new PromptDraft(moment, text, TagNotes.None);
        }

        var written = o["tags"] switch
        {
            JsonArray a => a.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var t) ? t : "").ToList(),
            JsonValue x when x.TryGetValue<string>(out var t) => t.Split(',').ToList(),
            _ => [],
        };
        // The model says first how many people the description mentions. With none, it must not bring a person in by the
        // tags (a fox written as "1girl, fox ears"): their count tags go, and the picture starts with "no humans".
        var nobody = o["people"] is JsonValue pv && pv.TryGetValue<int>(out var people) && people == 0;
        if (nobody) written = ["no humans", .. written.SelectMany(w => w.Split(',')).Where(w => !IsPersonCount(w))];
        var (kept, notes) = (tags ?? TagList.Current).Check(written, MaxPromptTags);
        if (kept.Count == 0 || kept.All(t => t == "no humans"))
            throw new InvalidOperationException("The model wrote no usable tags. Try again, or describe it differently.");
        return new PromptDraft(moment, string.Join(", ", kept.Select(TagList.ForPrompt)), notes);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\d+|multiple)\s?(girl|boy|other)s?$|^solo( focus)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PersonCount();

    /// <summary>"1girl", "2boys", "multiple girls", "solo": tags that put people in a picture.</summary>
    public static bool IsPersonCount(string tag) => PersonCount().IsMatch(TagList.Normalise(tag));

    public static string PromptInstruction(PromptStyle style, PictureRating rating = PictureRating.General) =>
        (style == PromptStyle.Tags
            ? "You turn a description of a picture into a prompt for an image generator that understands Danbooru tags. Fill in:\n" +
              "- moment: one plain sentence saying what the picture shows.\n" +
              "- people: how many people the description mentions. 0 when it mentions none: animals, creatures and things are not people.\n" +
              "- tags: 8 to 25 Danbooru tags, in this order: who is in it (\"1girl\" or \"1boy\" for each person the description mentions, as \"2girls\" or \"1girl, 1boy\" for two, and \"solo\" for one; \"no humans\" when it mentions no person); each person's hair, eyes and other lasting features; their clothing; their expression; their pose and what they are doing; animals and things in the picture (\"cat\", \"fox\", \"book stack\"); the place; time of day, light and weather; how much is shown (\"portrait\", \"upper body\", \"cowboy shot\", \"full body\", \"close-up\", \"wide shot\") and where it is seen from (\"from above\", \"from side\", \"from behind\") when it matters.\n" +
              "Never add a person the description does not mention, and never turn an animal into a person.\n" +
              "Each tag is lower case and one to three words, naming something that can be seen, like \"silver hair\", \"red dress\", \"smile\", \"sitting\", \"holding sword\", \"cat\", \"forest\", \"night\".\n" +
              "No sentences. No names of people, artists, characters or series. No quality or rating words (\"masterpiece\", \"best quality\", \"general\"): they are added separately.\n" +
              "Keep everything the description asks for, and add only what it clearly suggests.\n"
            : "You turn a description of a picture into a prompt for an image generator that reads plain English. Fill in:\n" +
              "- moment: one plain sentence saying what the picture shows.\n" +
              "- prompt: at most sixty words, as short phrases separated by commas: the subject first (who or what, how they look, what they wear and do), then the place, the light and the time of day, then how it is framed when it matters.\n" +
              "No names of artists, characters or series. No quality words (\"masterpiece\", \"best quality\"): they are added separately.\n" +
              "Keep everything the description asks for, and add only what it clearly suggests.\n")
        + PictureRatings.ForPlanner(rating) + "\nReply with the JSON object only.";

    public static string PromptSchema(PromptStyle style) => (style == PromptStyle.Tags
        ? new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["moment"] = new JsonObject { ["type"] = "string" },
                ["people"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 6 },
                ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 3, ["maxItems"] = MaxPromptTags },
            },
            ["required"] = new JsonArray("moment", "people", "tags"),
        }
        : new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["moment"] = new JsonObject { ["type"] = "string" },
                ["prompt"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("moment", "prompt"),
        }).ToJsonString();

    // ---- The instructions ----

    public static string SceneInstruction(PromptStyle style, IReadOnlyList<CastEntry> cast, int maxPeople, bool portrait,
        PictureRating rating = PictureRating.General)
    {
        var tags = style == PromptStyle.Tags;
        var text = new StringBuilder();
        text.Append(tags
            ? "You plan a single still picture for an image generator that understands Danbooru tags. You do not write the prompt: you fill in the form below.\n\n" +
              "Rules for every entry except \"moment\":\n" +
              "- Each entry is one Danbooru tag: lower case, one to three words, naming something that can be seen. Examples: \"red dress\", \"leather armor\", \"smile\", \"sitting\", \"arms crossed\", \"holding sword\", \"forest\", \"night\", \"candlelight\".\n" +
              "- No sentences, no names, no feelings or sounds, nothing that happened before or after.\n" +
              "- A few exact tags are better than many vague ones.\n\n"
            : "You plan a single still picture for an image generator that reads plain English. You do not write the prompt: you fill in the form below.\n\n" +
              "Rules for every entry except \"moment\":\n" +
              "- Each entry is a short plain phrase of a few words, naming something that can be seen. Examples: \"a long red dress\", \"smiling warmly\", \"sitting by the fire\", \"a candle-lit tavern\", \"at night\".\n" +
              "- No names, no feelings or sounds, nothing that happened before or after.\n\n");
        text.Append("The form:\n");
        if (portrait)
        {
            var name = cast[0].Name;
            text.Append($"- moment: one plain sentence describing a portrait of {name} that shows who they are: what they would wear, their usual expression, a place that suits them.\n");
            text.Append($"- people: exactly one entry, for {name}:\n");
        }
        else
        {
            text.Append("- moment: first choose ONE moment of the passage that works as a still picture: people or a place seen at a single instant, not a thought, a conversation's content or a chain of events. Prefer the moment with the strongest image. Say in one plain sentence what the picture shows.\n");
            text.Append($"- people: the people visible at that moment, at most {maxPeople}; fewer is better, and none for a picture of a place.");
            var names = cast.Select(c => c.Name.Trim()).Where(n => n.Length > 0).ToList();
            text.Append(names.Count > 0
                ? $" Use these names exactly: {string.Join(", ", names)}. Use \"{PictureScene.SomeoneElse}\" for anyone else.\n"
                : "\n");
            text.Append("  For each person:\n");
        }
        text.Append("  - kind: \"girl\" for anyone female, \"boy\" for anyone male, human or not; \"other\" only when they are neither.\n");
        var withLooks = cast.Where(c => c.HasLooks).Select(c => c.Name.Trim()).ToList();
        text.Append("  - looks: hair, eyes, skin and other lasting features");
        text.Append(withLooks.Count == 0
            ? ".\n"
            : withLooks.Count == cast.Count && portrait
                ? $". Leave it empty: {withLooks[0]}'s looks are already known and are added for you.\n"
                : $". Leave it empty for {string.Join(", ", withLooks)}: their looks are already known and are added for you. Fill it in for everyone else.\n");
        text.Append("  - clothing: what they wear at this moment.\n");
        text.Append(tags
            ? "  - expression: their face (\"smile\", \"frown\", \"closed eyes\", \"blush\", \"open mouth\").\n" +
              "  - pose: what their body is doing (\"standing\", \"sitting\", \"running\", \"looking back\", \"holding lantern\").\n" +
              "- place: where it is (\"forest\", \"bedroom\", \"tavern\", \"street\", \"indoors\", \"outdoors\").\n" +
              "- light: time of day, light and weather (\"night\", \"sunset\", \"sunlight\", \"moonlight\", \"candlelight\", \"rain\").\n"
            : "  - expression: their face.\n" +
              "  - pose: what their body is doing.\n" +
              "- place: where it is.\n" +
              "- light: time of day, light and weather.\n");
        text.Append(portrait
            ? "- framing: how much of them is shown: \"portrait\", \"upper body\", \"cowboy shot\" (head to thighs) or \"full body\".\n"
            : "- framing: how much is shown. \"close-up\" and \"portrait\" only for one person; \"upper body\" or \"cowboy shot\" (head to thighs) for two or three; \"full body\" when what they do with their whole body matters; \"wide shot\" for a place.\n");
        text.Append($"- view: where it is seen from. \"{PictureScene.StraightOn}\" unless another one clearly suits the moment.\n\n");
        text.Append(PictureRatings.ForPlanner(rating)).Append('\n');
        text.Append("Reply with the JSON object only.");
        return text.ToString();
    }

    public static string LooksInstruction(PromptStyle style) => style == PromptStyle.Tags
        ? "You describe how a character looks, for an image generator that understands Danbooru tags. Read the description and fill in:\n" +
          "- kind: \"girl\" for anyone female, \"boy\" for anyone male, human or not; \"other\" only when they are neither.\n" +
          "- looks: six to twelve Danbooru tags for lasting, visible features only. In this order: what they are when not human (\"elf\", \"fox girl\", \"anthro\", \"wolf\", \"robot\"); hair colour, hair length, hair style; eye colour; skin or fur colour; build and apparent age (\"muscular\", \"slim\", \"petite\", \"tall\", \"mature female\", \"old man\"); ears, tail, horns, wings; marks (\"scar on cheek\", \"freckles\", \"tattoo\"); things they are never without (\"glasses\", \"eyepatch\").\n" +
          "Each tag is lower case and one to three words, like \"silver hair\", \"long hair\", \"braid\", \"green eyes\", \"dark skin\", \"pointy ears\".\n" +
          "No clothes that change from day to day, no personality, no history, no names. Where the description does not say (hair or eye colour), choose what fits the character rather than leaving it out.\n" +
          "Reply with the JSON object only."
        : "You describe how a character looks, for an image generator that reads plain English. Read the description and fill in:\n" +
          "- looks: one phrase of at most thirty words on lasting, visible features only, starting with what they are: \"a tall woman in her thirties with long silver hair in a braid, green eyes and a scar on her left cheek\".\n" +
          "No clothes that change from day to day, no personality, no history, no names. Where the description does not say (hair or eye colour), choose what fits the character rather than leaving it out.\n" +
          "Reply with the JSON object only.";

    public static string LooksSchema(PromptStyle style) => (style == PromptStyle.Tags
        ? new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("girl", "boy", "other") },
                ["looks"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 3, ["maxItems"] = 14 },
            },
            ["required"] = new JsonArray("kind", "looks"),
        }
        : new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["looks"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("looks"),
        }).ToJsonString();

    // ---- Asking ----

    private async Task<string> AskAsync(string instruction, string user, string schema, double temperature, CancellationToken ct)
    {
        if (!engine.IsReady) throw new InvalidOperationException("No model is loaded. Load one first.");
        var preset = SamplerPresets.Get(SamplerPresets.DefaultId) with
        {
            Temperature = temperature,
            MaxTokens = MaxTokens,
            JsonSchema = schema,
            Thinking = false,
        };
        var prompt = new List<PromptMessage> { new("system", instruction), new("user", user) };
        var text = new StringBuilder();
        await foreach (var delta in engine.StreamChatAsync(prompt, preset, ct).WithCancellation(ct).ConfigureAwait(false))
            if (delta.Content is not null) text.Append(delta.Content);
        return text.ToString();
    }

    private static JsonObject Object(string answer, string refusal)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException(refusal);
        try
        {
            return JsonNode.Parse(answer[start..(end + 1)]) as JsonObject ?? throw new InvalidOperationException(refusal);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The model's answer was cut off or garbled. Try again, or try another model.");
        }
    }

    private static string Cut(string? text, int max)
    {
        text = (text ?? "").Trim();
        return text.Length <= max ? text : text[..max];
    }
}
