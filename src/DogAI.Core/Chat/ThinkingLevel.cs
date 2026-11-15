using System.Text.Json.Serialization;
using DogAI.Core.Settings;

namespace DogAI.Core.Chat;

/// <summary>
/// How long a model that "thinks" may do so before it answers, per kind of chat (M18; in place of the on/off switches).
/// Saved as its number: keep the order.
/// </summary>
public enum ThinkingLevel
{
    /// <summary>The chat template is told not to think at all.</summary>
    Off = 0,
    Short = 1,
    Normal = 2,
    Long = 3,

    /// <summary>The model thinks as long as it likes.</summary>
    NoLimit = 4,
}

/// <summary>What the chat engine said when asked to end the thinking of a reply in progress.</summary>
public enum ThinkingEnd
{
    /// <summary>The thinking was ended; the answer follows.</summary>
    Ended,

    /// <summary>The reply was no longer being written (it finished or was stopped in the meantime).</summary>
    TooLate,

    /// <summary>The engine has no such route, or did not take the request: it is too old for thinking limits.</summary>
    NotSupported,
}

/// <summary>The levels' numbers and words, and how a level reaches a request.</summary>
public static class ThinkingLevels
{
    public static IReadOnlyList<ThinkingLevel> All { get; } =
        [ThinkingLevel.Off, ThinkingLevel.Short, ThinkingLevel.Normal, ThinkingLevel.Long, ThinkingLevel.NoLimit];

    /// <summary>Where each kind of chat starts (owner's decision, 2026-11-15).</summary>
    public const ThinkingLevel GeneralStart = ThinkingLevel.NoLimit, RoleplayStart = ThinkingLevel.Short, StoryStart = ThinkingLevel.Short;

    /// <summary>
    /// How far from the budget the count of thinking pieces may be and still mean "cut by the budget": the engine marks
    /// no cut in the stream, and in the trial a 512 budget ended at 489 to 493 pieces, 1,536 at 1,487 to 1,504, 4,096 at 4,013.
    /// </summary>
    public const int CutMargin = 100;

    /// <summary>The most thinking tokens at <paramref name="level"/>; null for no limit (and for Off, which is not a budget).</summary>
    public static int? Budget(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Short => 512,
        ThinkingLevel.Normal => 1536,
        ThinkingLevel.Long => 4096,
        _ => null,
    };

    public static string Name(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "Off",
        ThinkingLevel.Short => "Short",
        ThinkingLevel.Normal => "Normal",
        ThinkingLevel.Long => "Long",
        _ => "No limit",
    };

    /// <summary>The name with its limit, as the Settings lists show it: "Short (up to 512 tokens)".</summary>
    public static string Title(ThinkingLevel level) =>
        Budget(level) is { } budget ? $"{Name(level)} (up to {budget:N0} tokens)" : Name(level);

    /// <summary>One line on what a level means for the owner.</summary>
    public static string Blurb(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => "Models that think answer at once.",
        ThinkingLevel.Short => "A model that thinks plans briefly, then answers: a few seconds more.",
        ThinkingLevel.Normal => "A model that thinks gets room to plan; the answer starts later.",
        ThinkingLevel.Long => "A model that thinks may plan at length; the answer can take most of a minute to start.",
        _ => "A model that thinks plans as long as it likes. Slower, and a long think can leave no room to answer.",
    };

    /// <summary>A saved value that is one of the levels; anything else (a number from a damaged or newer file) is none.</summary>
    private static ThinkingLevel? Known(ThinkingLevel? level) => level is { } l && Enum.IsDefined(l) ? l : null;

    /// <summary>
    /// General's level. Settings from before M18 have only the old switch, which is read as it was left: on is No limit,
    /// off is Off. A backup from before M18 restores the same way, because nothing is rewritten.
    /// </summary>
    public static ThinkingLevel General(AppSettings settings) =>
        Known(settings.GeneralThinkingLevel) ?? (settings.GeneralThinking ? GeneralStart : ThinkingLevel.Off);

    /// <summary>Character and group chats. Their old switch is not read: it was off by default, so "off" cannot be told from untouched.</summary>
    public static ThinkingLevel Roleplay(AppSettings settings) => Known(settings.RoleplayThinkingLevel) ?? RoleplayStart;

    /// <summary>Stories. Their old switch is not read, for the same reason as <see cref="Roleplay"/>.</summary>
    public static ThinkingLevel Story(AppSettings settings) => Known(settings.StoryThinkingLevel) ?? StoryStart;

    /// <summary>
    /// <paramref name="preset"/> as a request at <paramref name="level"/> sends it. Off turns the chat template's switch off.
    /// Any other level asks for the engine's "end thinking now" and, with a limit, gives the budget. A request with a size
    /// limit of its own (<see cref="SamplerPreset.MaxTokens"/>: choices, a rewrite, the story bible draft) counts its
    /// thinking against that limit, so there the thinking is never longer than the answer may be, at any level, and the
    /// limit grows by the budget: the answer keeps the room it was given.
    /// </summary>
    public static SamplerPreset Apply(SamplerPreset preset, ThinkingLevel level)
    {
        if (level == ThinkingLevel.Off) return preset with { Thinking = false, ThinkingBudget = null, ThinkingControl = false };
        var budget = Budget(level);
        if (preset.MaxTokens is not (> 0 and var max)) return preset with { ThinkingBudget = budget, ThinkingControl = true };
        var within = Math.Min(budget ?? max, max);
        return preset with { ThinkingBudget = within, ThinkingControl = true, MaxTokens = max + within };
    }

    /// <summary>What a reply shows while it thinks: "Thinking… 1,200 / 1,536", or without the limit when there is none.</summary>
    public static string Counter(int tokens, int? budget) =>
        tokens <= 0 ? "Thinking…" : budget is { } b ? $"Thinking… {tokens:N0} / {b:N0}" : $"Thinking… {tokens:N0}";
}

/// <summary>
/// What a reply's thinking was, kept with the reply (<c>thinking</c> in <see cref="ChatMessage.ExtraJson"/>, through
/// <see cref="Stories.StoryTurn"/>): numbers only, never the thinking's words.
/// </summary>
public sealed class ThinkingFacts
{
    /// <summary>Thinking pieces counted by DOG.ai as they arrived (one piece is one token; the engine reports no count).</summary>
    [JsonPropertyName("tokens")] public int Tokens { get; set; }

    /// <summary>The budget the request carried; null for no limit.</summary>
    [JsonPropertyName("budget"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? Budget { get; set; }

    /// <summary>The owner pressed Answer now and the engine ended the thinking.</summary>
    [JsonPropertyName("now"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool AnswerNow { get; set; }

    /// <summary>The owner pressed Answer now and the engine could not do it (too old).</summary>
    [JsonPropertyName("refused"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool Refused { get; set; }
}

/// <summary>How a reply's thinking was cut short, if it was.</summary>
public enum ThinkingCutKind
{
    /// <summary>It reached the budget of its level.</summary>
    Budget,

    /// <summary>The owner pressed Answer now.</summary>
    AnswerNow,

    /// <summary>The engine ignored the budget or could not end the thinking: it needs updating.</summary>
    EngineIgnored,
}

/// <summary>The small note under a reply's thinking: that it was cut by the budget or by Answer now, or that the engine cannot.</summary>
public sealed record ThinkingCut(ThinkingCutKind Kind, int Tokens, int? Budget)
{
    public string Text => Kind switch
    {
        ThinkingCutKind.Budget => $"The thinking was cut at its limit of {Budget:N0} tokens.",
        ThinkingCutKind.AnswerNow => $"The thinking was ended with Answer now after {Tokens:N0} tokens.",
        _ when Budget is { } b && Tokens > b => $"The chat engine did not keep to the thinking limit of {b:N0} tokens (it thought for {Tokens:N0}).",
        _ => "The chat engine could not end the thinking when Answer now was pressed.",
    };

    /// <summary>
    /// The note for a reply whose thinking is described by <paramref name="facts"/>; null when it ended by itself. The engine
    /// puts no mark in the stream at a cut, so "cut by the budget" is told from the count: within
    /// <see cref="ThinkingLevels.CutMargin"/> of the budget. Well past the budget, the engine did not know the field.
    /// </summary>
    public static ThinkingCut? Of(ThinkingFacts? facts)
    {
        if (facts is not { Tokens: > 0 }) return null;
        var budget = facts.Budget is > 0 ? facts.Budget : null;
        if (budget is { } b && facts.Tokens > b + ThinkingLevels.CutMargin) return new(ThinkingCutKind.EngineIgnored, facts.Tokens, b);
        if (facts.Refused) return new(ThinkingCutKind.EngineIgnored, facts.Tokens, null);
        if (facts.AnswerNow) return new(ThinkingCutKind.AnswerNow, facts.Tokens, budget);
        return budget is { } limit && facts.Tokens >= limit - ThinkingLevels.CutMargin ? new(ThinkingCutKind.Budget, facts.Tokens, limit) : null;
    }

    /// <summary>The note for a saved reply; null without thinking (nothing is read then).</summary>
    public static ThinkingCut? Of(ChatMessage message) =>
        message.Role != ChatRole.Assistant || string.IsNullOrEmpty(message.Reasoning) || string.IsNullOrEmpty(message.ExtraJson)
            ? null
            : Of(Stories.StoryTurn.Of(message).Thinking);
}
