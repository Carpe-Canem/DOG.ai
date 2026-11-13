namespace DogAI.Core;

/// <summary>
/// Where DOG.ai keeps its files. Everything the app writes lives under <see cref="Root"/>, except models.
/// In portable mode (a <see cref="PortableMarker"/> file next to DogAI.exe) both live inside the program's folder:
/// <c>data\</c> and <c>models\</c>, and nothing is written anywhere else.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Set this environment variable to relocate all app data (portable installs, tests).</summary>
    public const string RootOverrideVariable = "DOGAI_DATA_DIR";

    /// <summary>Set this to choose the models folder a fresh data folder starts with (test runs).</summary>
    public const string ModelsOverrideVariable = "DOGAI_MODELS_DIR";

    /// <summary>A file with this name next to DogAI.exe turns on portable mode.</summary>
    public const string PortableMarker = "portable.txt";

    /// <summary>A program folder with this name may keep the marker, data and models in its parent folder instead.</summary>
    public const string AppSubfolder = "app";

    public AppPaths(string? root = null)
    {
        Root = root
            ?? Environment.GetEnvironmentVariable(RootOverrideVariable)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DOG-AI");
    }

    private AppPaths(string root, string portableHome)
    {
        Root = root;
        PortableHome = portableHome;
    }

    /// <summary>
    /// The paths for this run of the program. In order: <see cref="RootOverrideVariable"/> (test runs), portable mode
    /// when <see cref="PortableMarker"/> sits in <paramref name="programFolder"/>, else the usual per-user folder.
    /// </summary>
    public static AppPaths ForProgram() =>
        Resolve(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(RootOverrideVariable));

    /// <summary><see cref="ForProgram"/> with its inputs given (tests).</summary>
    public static AppPaths Resolve(string programFolder, string? overrideRoot)
    {
        if (overrideRoot is { Length: > 0 } root)
            return new AppPaths(root);
        var program = Path.GetFullPath(programFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (File.Exists(Path.Combine(program, PortableMarker)))
            return new AppPaths(Path.Combine(program, "data"), program);
        // Tidy layout: the program files sit in an "app" folder and the marker, data and models in its parent.
        if (string.Equals(Path.GetFileName(program), AppSubfolder, StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(program) is { Length: > 0 } parent
            && File.Exists(Path.Combine(parent, PortableMarker)))
            return new AppPaths(Path.Combine(parent, "data"), parent);
        return new AppPaths();
    }

    public string Root { get; }

    /// <summary>The program's own folder when running portable; null otherwise.</summary>
    public string? PortableHome { get; }

    public bool IsPortable => PortableHome is not null;

    public string DatabaseFile => Path.Combine(Root, "dogai.db");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string WebViewDataDirectory => Path.Combine(Root, "webview");

    /// <summary>Scratch files. Portable runs point TEMP here so libraries and the engine write nothing outside the folder.</summary>
    public string TempDirectory => Path.Combine(Root, "temp");

    /// <summary>Where DOG.ai's own copy of the model engine goes once the Models section installs one.</summary>
    public string EngineDirectory => Path.Combine(Root, "engine");

    /// <summary>The models folder a portable install always uses.</summary>
    public string? PortableModelsDirectory => PortableHome is null ? null : Path.Combine(PortableHome, "models");

    /// <summary>Remembers which folder a portable install last ran from, so paths can follow it when the folder moves.</summary>
    public string HomeRecordFile => Path.Combine(Root, "home.txt");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(WebViewDataDirectory);
        if (PortableModelsDirectory is { } models) Directory.CreateDirectory(models);
    }

    /// <summary>
    /// Models are large, so the default location is the fixed drive with the most free space,
    /// falling back to the app data folder if no drive can be inspected. Portable installs use their own models folder.
    /// </summary>
    public string SuggestModelsDirectory()
    {
        if (Environment.GetEnvironmentVariable(ModelsOverrideVariable) is { Length: > 0 } explicitPath)
            return explicitPath;
        if (PortableModelsDirectory is { } portable)
            return portable;
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

    /// <summary>
    /// Portable runs only: sends the TEMP and TMP of this process (and of the engines it starts) to <see cref="TempDirectory"/>,
    /// after clearing what an earlier run left there.
    /// </summary>
    public void UseOwnTempFolder()
    {
        if (!IsPortable) return;
        try { if (Directory.Exists(TempDirectory)) Directory.Delete(TempDirectory, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Directory.CreateDirectory(TempDirectory);
        Environment.SetEnvironmentVariable("TEMP", TempDirectory);
        Environment.SetEnvironmentVariable("TMP", TempDirectory);
    }
}
