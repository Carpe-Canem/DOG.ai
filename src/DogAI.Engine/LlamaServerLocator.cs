using DogAI.Core;

namespace DogAI.Engine;

/// <summary>
/// Finds the llama-server program. Order: an explicit override, DOG.ai's own copy (installed by the
/// Models section), then one already on this PC, e.g. from winget. A portable install uses only its own copy,
/// so it never depends on anything outside its folder.
/// </summary>
public sealed class LlamaServerLocator(AppPaths paths)
{
    public const string OverrideVariable = "DOGAI_LLAMA_SERVER";
    private const string FileName = "llama-server.exe";

    public string? Find() => Candidates().FirstOrDefault(File.Exists);

    /// <summary>What to tell the user when <see cref="Find"/> comes back empty.</summary>
    public string MissingMessage => MissingText(paths.IsPortable);

    public static string MissingText(bool portable) => portable
        ? "The model engine (llama-server) is not in DOG.ai's folder yet. Get it in Models › Engine."
        : "The model engine (llama-server) was not found on this PC. Install it with: winget install llama.cpp";

    private IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } explicitPath)
            yield return explicitPath;

        yield return Path.Combine(paths.EngineDirectory, FileName);
        if (paths.IsPortable) yield break;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            // Prefer the real file over winget's shortcut so the engine's own DLLs sit next to it.
            string[] folders;
            try { folders = Directory.GetDirectories(packages, "ggml.llamacpp*"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { folders = []; }
            foreach (var folder in folders)
                yield return Path.Combine(folder, FileName);
        }

        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(folder.Trim('"'), FileName); }
            catch (ArgumentException) { continue; }
            yield return candidate;
        }
    }
}
