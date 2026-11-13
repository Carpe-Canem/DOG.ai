using System.Diagnostics;

namespace DogAI.Engine;

/// <summary>
/// What llama-server is allowed to take from the environment it inherits. DOG.ai passes every option it wants on the
/// command line; settings left in the environment by something else are dropped, because some of them make llama-server
/// write to disk what it is given (LLAMA_ARG_SLOT_SAVE_PATH saves conversations' slots, LLAMA_ARG_LOG_FILE and a high
/// LLAMA_ARG_LOG_VERBOSITY write prompts to a log). With the data lock on, chat and document text must not leave the
/// encrypted database that way.
/// </summary>
public static class EngineEnvironment
{
    private static readonly string[] DroppedPrefixes = ["LLAMA_ARG_", "LLAMA_LOG_"];

    /// <summary>Removes inherited llama.cpp settings from <paramref name="start"/>'s environment. Returns the names removed.</summary>
    public static IReadOnlyList<string> Clean(ProcessStartInfo start)
    {
        var removed = start.Environment.Keys
            .Where(name => DroppedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var name in removed) start.Environment.Remove(name);
        return removed;
    }
}
