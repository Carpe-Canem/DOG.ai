namespace DogAI.Core;

/// <summary>
/// Where DOG.ai keeps its files. Everything the app writes lives under <see cref="Root"/>, except models.
/// In portable mode (a <see cref="PortableMarker"/> file next to DOG.ai.exe) both live inside the program's folder:
/// <c>data\</c> and <c>models\</c>, and nothing is written anywhere else.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Set this environment variable to relocate all app data (portable installs, tests).</summary>
    public const string RootOverrideVariable = "DOGAI_DATA_DIR";

    /// <summary>Set this to choose the models folder a fresh data folder starts with (test runs).</summary>
    public const string ModelsOverrideVariable = "DOGAI_MODELS_DIR";

    /// <summary>A file with this name next to DOG.ai.exe turns on portable mode.</summary>
    public const string PortableMarker = "portable.txt";

    /// <summary>A program folder with this name may keep the marker, data and models in its parent folder instead.</summary>
    public const string AppSubfolder = "app";

    /// <summary>The database file's name. Before the rename to DOG.ai it was <see cref="LegacyDatabaseName"/>.</summary>
    public const string DatabaseName = "DOG.ai.db";

    /// <summary>The database file's earlier name, still found and renamed at start (see <see cref="RenameLegacyDatabase"/>).</summary>
    public const string LegacyDatabaseName = "dogai.db";

    private static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DOG.ai");

    private static string LegacyDefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DOG-AI");

    public AppPaths(string? root = null)
    {
        Root = root
            ?? Environment.GetEnvironmentVariable(RootOverrideVariable)
            ?? DefaultRoot;
        IsDefaultRoot = root is null && Environment.GetEnvironmentVariable(RootOverrideVariable) is null;
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
    public static AppPaths ForProgram()
    {
        var paths = Resolve(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(RootOverrideVariable));
        // Only the real per-user folder is ever moved; test runs and portable installs name their own.
        return paths.IsDefaultRoot ? new AppPaths(AdoptLegacyFolder(DefaultRoot, LegacyDefaultRoot)) { IsDefaultRoot = true } : paths;
    }

    /// <summary>
    /// The folder to use for per-user data after the rename from DOG-AI to DOG.ai: the new folder when it exists or nothing older
    /// is there; otherwise the old folder is moved to the new name. If the move fails (something still has a file open), the old
    /// folder is used as it is for this run, so nothing looks lost, and the move is tried again next start.
    /// </summary>
    public static string AdoptLegacyFolder(string newRoot, string legacyRoot)
    {
        if (Directory.Exists(newRoot) || !Directory.Exists(legacyRoot)) return newRoot;
        try
        {
            Directory.Move(legacyRoot, newRoot);
            return newRoot;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return legacyRoot;
        }
    }

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

    /// <summary>True when <see cref="Root"/> is the usual per-user folder rather than a portable or overridden one.</summary>
    public bool IsDefaultRoot { get; private init; }

    /// <summary>The program's own folder when running portable; null otherwise.</summary>
    public string? PortableHome { get; }

    public bool IsPortable => PortableHome is not null;

    /// <summary>
    /// The database. A folder that still holds only the earlier-named file keeps using it until <see cref="RenameLegacyDatabase"/>
    /// succeeds, so a failed rename can never make the app start on an empty database.
    /// </summary>
    public string DatabaseFile
    {
        get
        {
            var current = Path.Combine(Root, DatabaseName);
            return !File.Exists(current) && File.Exists(Path.Combine(Root, LegacyDatabaseName)) ? Path.Combine(Root, LegacyDatabaseName) : current;
        }
    }

    /// <summary>
    /// Renames dogai.db (and its -wal and -shm files) to DOG.ai.db when only the old name exists. Call at start, before anything
    /// opens the database. Returns true when it renamed; a failure leaves the old file in use.
    /// </summary>
    public bool RenameLegacyDatabase()
    {
        var oldFile = Path.Combine(Root, LegacyDatabaseName);
        var newFile = Path.Combine(Root, DatabaseName);
        if (!File.Exists(oldFile) || File.Exists(newFile)) return false;
        try
        {
            // Sidecars first: if the main file then fails to move, the pair is never left split under two names.
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(oldFile + suffix)) File.Move(oldFile + suffix, newFile + suffix);
            File.Move(oldFile, newFile);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (!File.Exists(oldFile + suffix) && File.Exists(newFile + suffix)) File.Move(newFile + suffix, oldFile + suffix);
            return false;
        }
    }

    /// <summary>
    /// The lock file (see <c>DogAI.Core.Privacy.LockFile</c>). It exists only while the data is locked with a password,
    /// and sits beside the database so a moved or copied data folder still opens with the password.
    /// </summary>
    public string LockFile => Path.Combine(Root, "lock.json");

    /// <summary>Counts wrong tries at the lock screen, so restarting DOG.ai does not clear the wait.</summary>
    public string UnlockTriesFile => Path.Combine(Root, "unlock-tries.json");

    public string LogsDirectory => Path.Combine(Root, "logs");
    public string WebViewDataDirectory => Path.Combine(Root, "webview");

    /// <summary>Scratch files. Portable runs point TEMP here so libraries and the engine write nothing outside the folder.</summary>
    public string TempDirectory => Path.Combine(Root, "temp");

    /// <summary>Where DOG.ai's own copy of the model engine goes once the Models section installs one.</summary>
    public string EngineDirectory => Path.Combine(Root, "engine");

    /// <summary>
    /// DOG.ai's copy of the picture engine (stable-diffusion.cpp's sd-server). A subfolder of the engine folder because
    /// the two engines ship DLLs with the same names in different versions; updating llama.cpp carries it across.
    /// </summary>
    public string ImageEngineDirectory => Path.Combine(EngineDirectory, ImageEngineFolderName);

    /// <summary>Name of <see cref="ImageEngineDirectory"/> inside the engine folder.</summary>
    public const string ImageEngineFolderName = "images";

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
                return Path.Combine(best.RootDirectory.FullName, "DOG.ai", "Models");
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
