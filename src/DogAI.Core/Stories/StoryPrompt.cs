using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DogAI.Core.Characters;
using DogAI.Core.Chat;

namespace DogAI.Core.Stories;

/// <summary>How a selected piece of an authored story is changed.</summary>
public enum ReviseKind
{
    Rewrite = 0,
    Expand = 1,
    Shorten = 2,

    /// <summary>Rewrite in a tone the user names.</summary>
    Tone = 3,

    /// <summary>Rewrite following the user's own instruction.</summary>
    Custom = 4,
}

/// <summary>Turns a story's bible, outline, state and turns into what the model is given.</summary>
public static partial class StoryPrompt
{
    /// <summary>The user turn that opens a story's prompt, since chat templates refuse a conversation that starts with a reply.</summary>
    public const string OpeningTurn = "[Begin the story.]";

    public const string NarratorInstruction =
        "You are the narrator of an interactive story. The player is its main character and decides what the main character does. " +
        "After each of the player's actions, write what happens next: about {words} words of vivid story text. " +
        "Show how the world and the other characters react, and move the story forward. " +
        "Never decide the main character's choices, and never add to what the player said they do. " +
        "Do not offer options, lists or questions such as \"What do you do?\"; the choices are added separately. " +
        "Stop at a moment where the player has to decide what to do next. " +
        "Write story text only: no headings, notes or commentary.";

    /// <summary>The short reminder placed with the player's newest action.</summary>
    public const string NarratorReminder =
        "[Narrate what happens next in about {words} words. End on the situation itself; do not ask the player what they do.]";

    public const string DiceInstruction =
        "When the player's action comes with a dice result, that result decides how the action turns out: narrate exactly that outcome.";

    public const string StateInstruction =
        "Keep to the current state shown with the player's latest action: where the main character is, what they carry, their stats and the facts.";

    public const string AuthorInstruction =
        "You are a skilled fiction writer working on a story with the user, one passage at a time. Follow the story bible and the outline. " +
        "Each time, write the next passage: about {words} words, continuing seamlessly from where the text stops. " +
        "Never repeat or summarise what is already written, and do not rush ahead of the outline: stop at a natural pause so the next passage can pick up from there. " +
        "Follow any direction the user gives for the passage. " +
        "Write story text only: no titles, chapter headings, notes or commentary.";

    /// <summary>The request for the rolling summary of a story. {0} is "an updated " or "a ".</summary>
    public const string SummaryTask =
        "[Task: write {0}summary of the story so far, so it can be remembered after the text itself is forgotten. " +
        "Combine the summary so far (if any) with the text above. Write in the past tense, under 300 words. Keep names, facts about " +
        "the characters and the world, what happened, what the main character chose, promises, and anything left unresolved. " +
        "Reply with the summary only.]";

    /// <summary>
    /// The prompt parts for a story: the narrator's or author's instruction, the bible, the outline (authored), the
    /// summary of earlier text, and the tracked state placed with the newest user turn.
    /// </summary>
    /// <param name="opening">True when nothing is written yet, so the prompt needs a user turn asking for the start.</param>
    /// <param name="cast">The story's character cards that still exist, in list order.</param>
    /// <param name="lore">The entries of those cards' lore that fired.</param>
    public static PromptLayout Layout(Story story, string memory = "", StoryState? state = null, bool opening = false,
        IReadOnlyList<StoryCastMember>? cast = null, IReadOnlyList<LoreEntry>? lore = null)
    {
        var layout = new PromptLayout { OpeningTurn = OpeningTurn };
        var words = Math.Clamp(story.PassageWords, 50, 1500).ToString();
        if (story.Kind == StoryKind.Interactive)
        {
            var instruction = NarratorInstruction.Replace("{words}", words);
            if (story.Dice) instruction += " " + DiceInstruction;
            if (story.TrackState) instruction += " " + StateInstruction;
            layout.Add(PromptPartKind.Instruction, instruction);
        }
        else
        {
            layout.Add(PromptPartKind.Instruction, AuthorInstruction.Replace("{words}", words));
            layout.Add(PromptPartKind.ChatInstructions, OutlineText(story));
        }
        layout.Add(PromptPartKind.Card, BibleText(story.Bible));
        if (cast is { Count: > 0 })
        {
            layout.Add(PromptPartKind.Cast, CastText(story, cast));
            layout.Add(PromptPartKind.Examples, ExamplesText(story, cast));
            // Lore is written with {{char}} and {{user}} too; several books share one block, so the first card the model writes names them.
            var first = cast.FirstOrDefault(m => !m.Main) ?? cast[0];
            CharacterPrompt.AddLore(layout, lore, text => CharacterPrompt.Fill(text, first.Character.Name, UserNameFor(first, cast)));
        }
        if (memory.Trim().Length > 0) layout.Add(PromptPartKind.Memory, "[The story so far]\n" + memory.Trim());
        if (story.Kind == StoryKind.Interactive)
        {
            // Said again next to the player's newest action, where models heed it most.
            var reminder = NarratorReminder.Replace("{words}", words);
            layout.FinalInstruction = story.TrackState && state is { IsEmpty: false }
                ? "[Current state]\n" + state.ToPromptText() + "\n\n" + reminder
                : reminder;
        }
        if (opening) layout.Directive = OpeningTurn;
        return layout;
    }

    /// <summary>The bible as the model is shown it. Empty fields are left out; empty when nothing is filled in.</summary>
    public static string BibleText(StoryBible bible)
    {
        var text = new StringBuilder();
        void Line(string label, string value)
        {
            if (value.Trim().Length > 0) text.Append(label).Append(": ").Append(value.Trim()).Append('\n');
        }
        void Block(string label, string value)
        {
            if (value.Trim().Length > 0) text.Append(label).Append(":\n").Append(value.Trim()).Append('\n');
        }
        Line("Premise", bible.Premise);
        Line("Genre", bible.Genre);
        Line("Tone", bible.Tone);
        Line("Point of view", bible.PointOfView);
        Line("Tense", bible.Tense);
        Block("Cast", bible.Cast);
        Block("World", bible.World);
        Block("Style guide", bible.StyleGuide);
        return text.Length == 0 ? "" : "[Story bible]\n" + text.ToString().TrimEnd();
    }

    public static string OutlineText(Story story)
    {
        if (story.Outline.Count == 0) return "";
        var text = new StringBuilder("[Outline]");
        for (var i = 0; i < story.Outline.Count; i++)
        {
            text.Append('\n').Append(ChapterName(story, i));
            if (story.Outline[i].Plan.Trim() is { Length: > 0 } plan) text.Append('\n').Append(plan);
            if (i < story.Outline.Count - 1) text.Append('\n');
        }
        return text.ToString();
    }

    /// <summary>"Chapter 2: The Ferry", or "Chapter 2" when it has no title.</summary>
    public static string ChapterName(Story story, int index)
    {
        var title = index >= 0 && index < story.Outline.Count ? story.Outline[index].Title.Trim() : "";
        return title.Length == 0 ? $"Chapter {index + 1}" : $"Chapter {index + 1}: {title}";
    }

    /// <summary>
    /// The branch as the model sees it. The user's turns are rewritten: an action gets its dice result; a direction
    /// becomes a request for the next passage naming its chapter. Passages are left as they are.
    /// </summary>
    public static IReadOnlyList<ChatMessage> ForPrompt(Story story, IReadOnlyList<ChatMessage> path)
    {
        var result = new List<ChatMessage>(path.Count);
        int? previousChapter = null;
        var first = true;
        foreach (var m in path)
        {
            if (m.Role != ChatRole.User)
            {
                result.Add(m);
                continue;
            }
            var turn = StoryTurn.Of(m);
            string content;
            if (story.Kind == StoryKind.Interactive)
            {
                content = m.Content.Trim();
                if (turn.Roll is { } roll) content += $"\n\n[Dice: rolled {roll} out of 20, {DiceOutcome(roll)}]";
            }
            else
            {
                var chapter = Math.Clamp(turn.Chapter ?? previousChapter ?? 0, 0, Math.Max(0, story.Outline.Count - 1));
                content = Direction(story, chapter, chapter != previousChapter, first, m.Content);
                previousChapter = chapter;
            }
            first = false;
            result.Add(new ChatMessage
            {
                Id = m.Id,
                ConversationId = m.ConversationId,
                ParentId = m.ParentId,
                Role = m.Role,
                Content = content,
                ExtraJson = m.ExtraJson,
                CreatedAt = m.CreatedAt,
            });
        }
        return result;
    }

    /// <summary>The request for an authored story's next passage.</summary>
    public static string Direction(Story story, int chapter, bool opensChapter, bool opensStory, string steering)
    {
        var length = $" (about {Math.Clamp(story.PassageWords, 50, 1500)} words)";
        string request;
        if (story.Outline.Count == 0)
            request = (opensStory ? "[Write the opening passage of the story" : "[Write the next passage") + length + ".]";
        else
            request = "[Write the next passage of " + Lower(ChapterName(story, chapter)) + length + "."
                      + (opensChapter ? " This passage opens the chapter." : "") + "]";
        steering = steering.Trim();
        return steering.Length == 0 ? request : request + "\nDirection: " + steering;

        static string Lower(string name) => char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>What a d20 roll means for a risky action, as the narrator is told it.</summary>
    public static string DiceOutcome(int roll) => roll switch
    {
        <= 1 => "a critical failure. The attempt fails badly and leaves things worse than before.",
        <= 7 => "a failure. The attempt does not work: narrate how it fails.",
        <= 12 => "a partial success. It works only in part, or at a cost.",
        <= 19 => "a success. The attempt works.",
        _ => "a critical success. It works better than hoped, and something extra is gained.",
    };

    /// <summary>The short form shown next to the player's action.</summary>
    public static string DiceLabel(int roll) => roll switch
    {
        <= 1 => "critical failure",
        <= 7 => "failure",
        <= 12 => "partial success",
        <= 19 => "success",
        _ => "critical success",
    };

    public const int MaxChoices = 4;

    /// <summary>The JSON shape for the choices after a passage, with the updated state when the story tracks one.</summary>
    public static string ChoicesSchema(Story story)
    {
        static JsonObject Text() => new() { ["type"] = "string" };
        static JsonObject List(JsonObject items, int max, int min = 0)
        {
            var list = new JsonObject { ["type"] = "array", ["items"] = items, ["maxItems"] = max };
            if (min > 0) list["minItems"] = min;
            return list;
        }

        var choice = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["text"] = Text() },
            ["required"] = new JsonArray("text"),
        };
        if (story.Dice)
        {
            choice["properties"]!["risky"] = new JsonObject { ["type"] = "boolean" };
            choice["required"] = new JsonArray("text", "risky");
        }
        var properties = new JsonObject { ["choices"] = List(choice, MaxChoices, 2) };
        var required = new JsonArray("choices");
        if (story.TrackState)
        {
            var stat = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["name"] = Text(), ["value"] = new JsonObject { ["type"] = "integer" } },
                ["required"] = new JsonArray("name", "value"),
            };
            properties["state"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["location"] = Text(),
                    ["inventory"] = List(Text(), 12),
                    ["stats"] = List(stat, 6),
                    ["facts"] = List(Text(), 12),
                },
                ["required"] = new JsonArray("location", "inventory", "stats", "facts"),
            };
            required.Add("state");
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required }.ToJsonString();
    }

    /// <summary>The request, placed after a passage, for the player's choices and the updated state.</summary>
    public static string ChoicesTask(Story story)
    {
        var task = new StringBuilder(
            "[Task: the passage above has just ended. Do not continue the story. Reply with a JSON object only.\n" +
            "\"choices\": two to four different things the main character could do next. Each is one short command that starts " +
            "with a plain verb, such as \"Open the door\" or \"Ask Mara about the key\" (never \"Opens\" or \"You open\"), and each takes the story somewhere different.");
        if (story.Dice)
            task.Append(" Set \"risky\" to true only when the action could plainly fail or go wrong, so that a dice roll should decide it.");
        if (story.TrackState)
            task.Append(
                "\n\"state\": where the story stands after this passage, updated from the current state shown earlier. " +
                "\"location\" is where the main character is now. \"inventory\" is what they carry. \"stats\" are numbers worth tracking, " +
                "such as health or money: keep the same names and change a value only when the story changed it. \"facts\" are short " +
                "lasting facts worth remembering (things done, promises, discoveries), twelve at most; drop the ones that no longer matter.");
        return task.Append(']').ToString();
    }

    /// <summary>
    /// Reads the model's choices (and state) into a turn. Tolerates text around the object and choices given as
    /// plain strings; refuses an answer with no usable choice.
    /// </summary>
    public static StoryTurn ParseChoices(string answer, Story story)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        JsonObject? o = null;
        if (start >= 0 && end > start)
        {
            try
            {
                o = JsonNode.Parse(answer[start..(end + 1)]) as JsonObject;
            }
            catch (JsonException)
            {
            }
        }
        if (o is null) throw new InvalidOperationException("The model did not give choices. Type what you do, or ask for choices again.");

        var choices = new List<StoryChoice>();
        foreach (var item in o["choices"] as JsonArray ?? [])
        {
            var text = item is JsonObject c ? Str(c["text"]) : Str(item);
            text = Limit(Numbering().Replace(text.Trim(), "").Trim(), 200);
            if (text.Length == 0 || choices.Any(x => x.Text.Equals(text, StringComparison.OrdinalIgnoreCase))) continue;
            var risky = story.Dice && item is JsonObject r && r["risky"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
            choices.Add(new StoryChoice { Text = text, Risky = risky });
            if (choices.Count == MaxChoices) break;
        }
        if (choices.Count == 0) throw new InvalidOperationException("The model did not give choices. Type what you do, or ask for choices again.");

        var turn = new StoryTurn { Choices = choices };
        if (story.TrackState && o["state"] is JsonObject s)
        {
            turn.State = new StoryState
            {
                Location = Limit(Str(s["location"]).Trim(), 120),
                Inventory = Strings(s["inventory"], 20, 80),
                Facts = Strings(s["facts"], 12, 200),
                Stats = (s["stats"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Select(x => new StoryStat { Name = Limit(Str(x["name"]).Trim(), 30), Value = Int(x["value"]) })
                    .Where(x => x.Name.Length > 0)
                    .DistinctBy(x => x.Name.ToLowerInvariant())
                    .Take(8)
                    .ToList(),
            };
        }
        return turn;
    }

    /// <summary>
    /// A passage without the question to the player that narrators like to end on ("What do you do next?"),
    /// since the choices are shown right below. Questions inside dialogue are left alone.
    /// </summary>
    public static string TrimClosingQuestion(string passage)
    {
        var trimmed = ClosingQuestion().Replace(passage.TrimEnd(), "").TrimEnd();
        return trimmed.Length == 0 ? passage : trimmed;
    }

    public const string ReviseSystem =
        "You are a careful fiction editor. You are shown part of a story with one piece of it selected. You rewrite only the selected " +
        "text as asked and reply with the replacement text only: no quotation marks around it, no notes, no explanation. " +
        "Keep the names, facts, point of view and tense, and make the replacement fit between the text before and after it.";

    /// <summary>The prompt for changing a selected piece of a passage. Only nearby text is shown, to keep it short.</summary>
    public static List<PromptMessage> RevisePrompt(Story story, string before, string selected, string after, ReviseKind kind, string detail = "")
    {
        detail = detail.Trim();
        // Saying the length in words keeps the replacement from running away: asked only to "change the tone" of one
        // sentence, a model will happily write a page.
        var words = Math.Max(1, selected.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        var task = kind switch
        {
            ReviseKind.Expand => $"Expand the selected text to about twice its length (about {words * 2} words) with more detail, action or dialogue, without adding new plot events.",
            ReviseKind.Shorten => $"Shorten the selected text to about half its length (about {Math.Max(1, words / 2)} words), keeping what matters.",
            ReviseKind.Tone when detail.Length > 0 => $"Rewrite the selected text in this tone: {detail}. Keep the events the same and the length about the same (about {words} words).",
            ReviseKind.Custom when detail.Length > 0 => "Rewrite the selected text as follows: " + detail,
            _ => $"Rewrite the selected text in different words, keeping its meaning and roughly its length (about {words} words).",
        };
        var style = new StoryBible
        {
            Genre = story.Bible.Genre,
            Tone = story.Bible.Tone,
            PointOfView = story.Bible.PointOfView,
            Tense = story.Bible.Tense,
            StyleGuide = story.Bible.StyleGuide,
        };
        var system = BibleText(style) is { Length: > 0 } bible ? ReviseSystem + "\n\n" + bible : ReviseSystem;
        before = before.TrimEnd();
        after = after.TrimStart();
        if (before.Length > 1500) before = "…" + before[^1500..];
        if (after.Length > 500) after = after[..500] + "…";
        var user = new StringBuilder();
        if (before.Length > 0) user.Append("[Text before]\n").Append(before).Append("\n\n");
        user.Append("[Selected text]\n").Append(selected.Trim()).Append("\n\n");
        if (after.Length > 0) user.Append("[Text after]\n").Append(after).Append("\n\n");
        user.Append("[Task: ").Append(task).Append(" Reply with the replacement for the selected text only.]");
        return [new PromptMessage("system", system), new PromptMessage("user", user.ToString())];
    }

    /// <summary>The most tokens a replacement may take, given the selection's size: room to do the job, not to run on.</summary>
    public static int ReviseTokenLimit(ReviseKind kind, int selectedTokens) => kind switch
    {
        ReviseKind.Expand => selectedTokens * 3 + 64,
        ReviseKind.Shorten => selectedTokens + 32,
        _ => selectedTokens * 2 + 48,
    };

    /// <summary>
    /// The model's replacement made ready to drop in: quotation marks it wrapped around the whole answer are removed
    /// (unless the selection had them), and an answer cut off at the length limit is ended at its last full sentence.
    /// </summary>
    public static string TidyRevision(string replacement, string selected)
    {
        var text = replacement.Trim();
        selected = selected.Trim();
        const string open = "\"“", close = "\"”";
        if (text.Length > 1 && open.Contains(text[0]) && close.Contains(text[^1]) && text[1..^1].IndexOfAny([.. open, .. close]) < 0
            && !(selected.Length > 0 && open.Contains(selected[0])))
            text = text[1..^1].Trim();
        const string ends = ".!?…\"”'’)*";
        if (text.Length > 0 && selected.Length > 0 && ends.Contains(selected[^1]) && !ends.Contains(text[^1]))
        {
            var cut = text.LastIndexOfAny(['.', '!', '?', '…']);
            if (cut > text.Length / 3) text = text[..(cut + 1)];
        }
        return text;
    }

    private static string Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static int Int(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && d is > int.MinValue and < int.MaxValue) return (int)Math.Round(d);
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed) ? parsed : 0;
    }

    private static List<string> Strings(JsonNode? node, int max, int length) =>
        (node as JsonArray ?? [])
            .Select(x => Limit(Str(x).Trim(), length))
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();

    private static string Limit(string text, int length) => text.Length <= length ? text : text[..length].TrimEnd();

    // From a sentence that starts "What do you do…?", "What's your move…?" or "What now…?" (optionally "So," or "Now,") to the end,
    // together with any further short questions after it.
    [GeneratedRegex(@"(?<=^|[.!?…""”\n]\s*)(?:(?:So|Now|And|But),?\s+)?What(?:(?:['’]s|\s+is|\s+do|\s+will|\s+would|\s+should|\s+are)\b[^.!?\n]{0,60}\b(?:you|your)\b[^.!?\n]{0,60}|\s+(?:now|next)\b[^.!?\n]{0,40})\?(?:\s*[^.!?\n]{0,160}\?)*\s*$",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 200)]
    private static partial Regex ClosingQuestion();

    [GeneratedRegex(@"^(\d+[.):]|[-*•])\s*")]
    private static partial Regex Numbering();
}
