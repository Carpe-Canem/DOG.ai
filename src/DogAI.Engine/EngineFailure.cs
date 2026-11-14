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

    /// <summary>
    /// What Windows' exit code says about a program that ended before it printed anything: the codes Windows gives when
    /// it could not even start the program. Null for any other code (the program's own lines say more then).
    /// </summary>
    public static string? StartProblem(int exitCode) => unchecked((uint)exitCode) switch
    {
        // STATUS_DLL_NOT_FOUND
        0xC0000135 => "Windows could not find a file it needs (one of the libraries that come with it).",
        // STATUS_ENTRYPOINT_NOT_FOUND, STATUS_ORDINAL_NOT_FOUND, STATUS_INVALID_IMAGE_FORMAT, STATUS_DLL_INIT_FAILED
        0xC0000139 or 0xC0000138 or 0xC000007B or 0xC0000142 => "one of its files does not fit the others (a wrong version, or a damaged file).",
        _ => null,
    };

    [GeneratedRegex(@"unknown model architecture:\s*'([^']+)'", RegexOptions.IgnoreCase)]
    private static partial Regex UnknownArchitecture();

    [GeneratedRegex(@"unknown pre-tokenizer type|unknown tokenizer", RegexOptions.IgnoreCase)]
    private static partial Regex UnknownTokenizer();

    /// <summary>The engine's own function name in front of a log line, e.g. "llama_model_load: ".</summary>
    [GeneratedRegex(@"^[a-z_]+:\s+")]
    private static partial Regex Prefix();
}
