using System.Text.RegularExpressions;

namespace DogAI.Engine;

/// <summary>
/// Turns the last lines llama-server printed before it quit into a message a person can act on.
/// </summary>
public static partial class EngineFailure
{
    /// <param name="lead">What happened, e.g. "The model engine stopped while loading the model."</param>
    /// <param name="lines">The engine's recent output, oldest first.</param>
    public static string Explain(string lead, IReadOnlyList<string> lines)
    {
        // A model newer than the engine: the engine does not know its design or its tokenizer.
        foreach (var line in lines)
        {
            if (UnknownArchitecture().Match(line) is { Success: true } arch)
                return $"{lead} This model uses a newer design (\"{arch.Groups[1].Value}\") than your copy of llama-server knows. " +
                       "Update the engine in Models → Engine, then load the model again.";
            if (UnknownTokenizer().IsMatch(line))
                return $"{lead} This model is newer than your copy of llama-server. " +
                       "Update the engine in Models → Engine, then load the model again.";
        }

        // Otherwise the first error is usually the cause; the lines after it only say that loading stopped.
        var hint = lines.FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                   ?? lines.FirstOrDefault(l => l.Contains("failed", StringComparison.OrdinalIgnoreCase));
        return hint is null ? lead : $"{lead} It said: {Prefix().Replace(hint.Trim(), "")}";
    }

    [GeneratedRegex(@"unknown model architecture:\s*'([^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex UnknownArchitecture();

    [GeneratedRegex(@"unknown pre-tokenizer type|unknown tokenizer", RegexOptions.IgnoreCase)]
    private static partial Regex UnknownTokenizer();

    /// <summary>The engine's own function name in front of a log line, e.g. "llama_model_load: ".</summary>
    [GeneratedRegex(@"^[a-z_]+:\s+")]
    private static partial Regex Prefix();
}
