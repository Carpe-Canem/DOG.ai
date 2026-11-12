namespace DogAI.Core;

/// <summary>Where DOG AI keeps its files. Everything the app writes lives under <see cref="Root"/>, except models.</summary>
public sealed class AppPaths
{
    /// <summary>Set this environment variable to relocate all app data (portable installs, tests).</summary>
    public const string RootOverrideVariable = "DOGAI_DATA_DIR";

    /// <summary>Set this to choose the models folder a fresh data folder starts with (test runs).</summary>
    public const string ModelsOverrideVariable = "DOGAI_MODELS_DIR";

    public AppPaths(string? root = null)
    {
        Root = root
            ?? Environment.GetEnvironmentVariable(RootOverrideVariable)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DOG-AI");
    }

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "dogai.db");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string WebViewDataDirectory => Path.Combine(Root, "webview");

    /// <summary>Where DOG AI's own copy of the model engine goes once the Models section installs one.</summary>
    public string EngineDirectory => Path.Combine(Root, "engine");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(WebViewDataDirectory);
    }

    /// <summary>
    /// Models are large, so the default location is the fixed drive with the most free space,
    /// falling back to the app data folder if no drive can be inspected.
    /// </summary>
    public string SuggestModelsDirectory()
    {
        if (Environment.GetEnvironmentVariable(ModelsOverrideVariable) is { Length: > 0 } explicitPath)
            return explicitPath;
        try
        {
            var best = DriveInfo.GetDrives()
                .Where(d => d is { DriveType: DriveType.Fixed, IsReady: true })
                .OrderByDescending(d => d.AvailableFreeSpace)
                .FirstOrDefault();
            if (best is not null)
                return Path.Combine(best.RootDirectory.FullName, "DOG-AI", "Models");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return Path.Combine(Root, "Models");
    }
}
