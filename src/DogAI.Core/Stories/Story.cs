using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DogAI.Core.Chat;

namespace DogAI.Core.Stories;

public enum StoryKind
{
    /// <summary>A story written passage by passage along an outline, which the user steers and edits.</summary>
    Authored = 0,

    /// <summary>A choose-your-own-adventure: the model narrates, the user picks or types what to do.</summary>
    Interactive = 1,
}

/// <summary>What the story is and how it is told. Sent with every prompt and editable at any time.</summary>
public sealed class StoryBible
{
    public string Premise { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Tone { get; set; } = "";
    public string PointOfView { get; set; } = "";
    public string Tense { get; set; } = "";
    public string Cast { get; set; } = "";
    public string World { get; set; } = "";
    public string StyleGuide { get; set; } = "";
}

/// <summary>One chapter of an authored story's outline.</summary>
public sealed class StoryChapter
{
    public string Title { get; set; } = "";

    /// <summary>What happens in the chapter: a summary or a list of scene beats.</summary>
    public string Plan { get; set; } = "";
}

/// <summary>
/// A character card linked to a story. The card itself stays in Characters, so later changes to it reach the story.
/// </summary>
public sealed class StoryCharacter
{
    public long CharacterId { get; set; }

    /// <summary>
    /// True for the story's main character: in an interactive story the player plays them and the narrator never
    /// decides what they do. False for a character the model writes. At most one card is the main character.
    /// </summary>
    public bool Main { get; set; }
}

/// <summary>
/// The story settings that go with a "story" conversation. The text itself is the conversation's message tree:
/// narrator passages are replies; the user's turns are actions (interactive) or directions (authored).
/// </summary>
public sealed class Story
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public StoryKind Kind { get; set; }
    public StoryBible Bible { get; set; } = new();
    public List<StoryChapter> Outline { get; set; } = [];

    /// <summary>Character cards in the story, in list order. Cards that have since been deleted are skipped.</summary>
    public List<StoryCharacter> Characters { get; set; } = [];

    /// <summary>Interactive: the model keeps location, inventory, stats and facts up to date after each passage.</summary>
    public bool TrackState { get; set; }

    /// <summary>Interactive: risky actions are decided by a dice roll.</summary>
    public bool Dice { get; set; }

    /// <summary>Roughly how long each passage should be.</summary>
    public int PassageWords { get; set; } = DefaultPassageWords;

    public const int DefaultPassageWords = 250;

    /// <summary>The voice passages are read with (a <see cref="DogAI.Core.Voice.VoiceId"/> as text). Null means the default voice.</summary>
    public string? NarratorVoice { get; set; }
    public const string Mode = "story";
}

public sealed class StoryStat
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public int Value { get; set; }
}

/// <summary>Where an interactive story stands after a passage. Kept on the passage, so every branch has its own.</summary>
public sealed class StoryState
{
    [JsonPropertyName("location")] public string Location { get; set; } = "";
    [JsonPropertyName("inventory")] public List<string> Inventory { get; set; } = [];
    [JsonPropertyName("stats")] public List<StoryStat> Stats { get; set; } = [];
    [JsonPropertyName("facts")] public List<string> Facts { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => Location.Trim().Length == 0 && Inventory.Count == 0 && Stats.Count == 0 && Facts.Count == 0;

    /// <summary>The state as the model is shown it.</summary>
    public string ToPromptText()
    {
        var text = new StringBuilder();
        if (Location.Trim().Length > 0) text.Append("Location: ").Append(Location.Trim()).Append('\n');
        if (Inventory.Count > 0) text.Append("Carrying: ").Append(string.Join(", ", Inventory)).Append('\n');
        if (Stats.Count > 0) text.Append("Stats: ").Append(string.Join(", ", Stats.Select(s => $"{s.Name} {s.Value}"))).Append('\n');
        if (Facts.Count > 0) text.Append("Facts:\n").Append(string.Join("\n", Facts.Select(f => "- " + f))).Append('\n');
        return text.ToString().TrimEnd();
    }
}

public sealed class StoryChoice
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    /// <summary>The action could plainly fail; with dice on, a roll decides it.</summary>
    [JsonPropertyName("risky")] public bool Risky { get; set; }
}

/// <summary>
/// Story details kept with one message (<see cref="ChatMessage.ExtraJson"/>): on a passage, the choices offered
/// after it and the state it left; on the user's turn, the dice roll (interactive) or the chapter (authored).
/// </summary>
public sealed class StoryTurn
{
    [JsonPropertyName("choices"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StoryChoice>? Choices { get; set; }

    [JsonPropertyName("state"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StoryState? State { get; set; }

    /// <summary>The d20 roll made for this action. Null when no dice were rolled.</summary>
    [JsonPropertyName("roll"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Roll { get; set; }

    /// <summary>Authored: the outline chapter (from 0) this direction's passage belongs to.</summary>
    [JsonPropertyName("chapter"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Chapter { get; set; }

    public static StoryTurn Of(ChatMessage message)
    {
        if (string.IsNullOrEmpty(message.ExtraJson)) return new StoryTurn();
        try
        {
            return JsonSerializer.Deserialize<StoryTurn>(message.ExtraJson) ?? new StoryTurn();
        }
        catch (JsonException)
        {
            return new StoryTurn();
        }
    }

    public string? ToJson() =>
        Choices is null && State is null && Roll is null && Chapter is null ? null : JsonSerializer.Serialize(this);

    /// <summary>The state left by the newest passage on <paramref name="path"/> that has one.</summary>
    public static StoryState? LatestState(IReadOnlyList<ChatMessage> path)
    {
        for (var i = path.Count - 1; i >= 0; i--)
            if (path[i].Role == ChatRole.Assistant && Of(path[i]).State is { } state) return state;
        return null;
    }

    /// <summary>Authored: the chapter the newest direction on <paramref name="path"/> names; 0 when none does.</summary>
    public static int LatestChapter(IReadOnlyList<ChatMessage> path)
    {
        for (var i = path.Count - 1; i >= 0; i--)
            if (path[i].Role == ChatRole.User && Of(path[i]).Chapter is { } chapter) return chapter;
        return 0;
    }
}

public interface IStoryStore
{
    /// <summary>Every story's settings (without text), for lists.</summary>
    Task<IReadOnlyList<Story>> ListAsync(CancellationToken ct = default);

    Task<Story?> GetAsync(long conversationId, CancellationToken ct = default);

    /// <summary>Adds the story or replaces the one already saved for its conversation.</summary>
    Task SaveAsync(Story story, CancellationToken ct = default);
}
