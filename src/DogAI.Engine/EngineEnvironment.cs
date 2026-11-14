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

    private const uint FailCriticalErrors = 0x0001; // SEM_FAILCRITICALERRORS

    private static readonly object ErrorModeLock = new();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    /// <summary>
    /// Starts an engine program so that Windows shows no error box when the program cannot start (a library it needs is
    /// missing, for example). With the box, the program stays alive behind it until someone presses OK, and DOG.ai waits
    /// for a program that will never answer. Without it the program ends at once with an exit code, which
    /// <see cref="EngineFailure.StartProblem"/> puts into words. A program inherits this setting from the one that starts
    /// it, so DOG.ai's own is changed only for the moment of the start.
    /// </summary>
    public static void StartQuietly(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            process.Start();
            return;
        }
        lock (ErrorModeLock)
        {
            var before = GetErrorMode();
            SetErrorMode(before | FailCriticalErrors);
            try
            {
                process.Start();
            }
            finally
            {
                SetErrorMode(before);
            }
        }
    }
}
