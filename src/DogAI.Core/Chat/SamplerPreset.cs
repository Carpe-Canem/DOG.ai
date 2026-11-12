namespace DogAI.Core.Chat;

/// <summary>
/// How the model picks each next word. Values are passed to the engine as they are;
/// a multiplier or probability of 0 switches that sampler off.
/// </summary>
public sealed record SamplerPreset(string Id, string Name, string Description)
{
    public double Temperature { get; init; } = 0.7;
    public double TopP { get; init; } = 0.95;
    public int TopK { get; init; } = 40;
    public double MinP { get; init; } = 0.05;
    public double RepeatPenalty { get; init; } = 1.0;

    /// <summary>DRY: penalises repeating earlier word sequences. 0 = off.</summary>
    public double DryMultiplier { get; init; }

    /// <summary>XTC: sometimes removes the most obvious words for more surprising prose. 0 = off.</summary>
    public double XtcProbability { get; init; }
    public double XtcThreshold { get; init; } = 0.1;

    /// <summary>Most tokens to write. Null means no limit. Set by DOG.ai for its own jobs (summaries), never saved with a style.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int? MaxTokens { get; init; }

    /// <summary>A JSON schema the answer must follow (structured output). Set by DOG.ai for its own jobs, never saved with a style.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? JsonSchema { get; init; }

    /// <summary>Text at which to stop writing. Set by DOG.ai for its own jobs, never saved with a style.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string>? Stop { get; init; }
}

public static class SamplerPresets
{
    public const string DefaultId = "balanced";

    public static IReadOnlyList<SamplerPreset> All { get; } =
    [
        new("precise", "Precise", "Sticks to the most likely answer. Best for code and facts.")
            { Temperature = 0.2, TopP = 0.9, TopK = 40, MinP = 0.1 },
        new(DefaultId, "Balanced", "A sensible default for everyday questions and writing.")
            { Temperature = 0.7, TopP = 0.95, TopK = 40, MinP = 0.05 },
        new("creative", "Creative", "Looser word choice for brainstorming and prose.")
            { Temperature = 1.0, TopP = 1.0, TopK = 0, MinP = 0.03, DryMultiplier = 0.8 },
        new("wild", "Wild", "Deliberately avoids the obvious. Expect surprises and the odd misstep.")
            { Temperature = 1.2, TopP = 1.0, TopK = 0, MinP = 0.02, DryMultiplier = 0.8, XtcProbability = 0.5 },
    ];

    public static SamplerPreset Get(string? id) => All.FirstOrDefault(p => p.Id == id) ?? All.First(p => p.Id == DefaultId);
}
