using System.Text.RegularExpressions;
using DogAI.Core;

namespace DogAI.Hub;

/// <summary>
/// Installs and updates DOG.ai's copy of the picture engine, stable-diffusion.cpp, from its GitHub releases. It needs
/// two files: the CUDA 12 build and NVIDIA's runtime for it. Both are checked against the checksums the release
/// publishes (a file without one is refused), unpacked to a side folder and swapped in only when complete, so a failed
/// update leaves the old engine. It goes in <see cref="AppPaths.ImageEngineDirectory"/>, a subfolder of the chat engine's
/// folder; the two never share a folder because both ship DLLs with the same names.
/// </summary>
public sealed partial class ImageEngineUpdater(HttpClient http, AppPaths paths)
{
    private const string Api = "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases";
    private const string VersionFile = "dogai-image-engine.txt";
    private const string ProgramName = "sd-server.exe";

    /// <summary>The CUDA version the build needs from the graphics driver.</summary>
    public static readonly Version NeedsCuda = new(12, 0);

    /// <summary>The release DOG.ai installed, e.g. "master-1094-3bad892". Null when it has not installed one.</summary>
    public string? InstalledTag
    {
        get
        {
            var file = Path.Combine(paths.ImageEngineDirectory, VersionFile);
            try { return File.Exists(file) ? File.ReadAllText(file).Split('|')[0].Trim() : null; }
            catch (IOException) { return null; }
        }
    }

    public bool IsInstalled => File.Exists(Path.Combine(paths.ImageEngineDirectory, ProgramName));

    /// <summary>What to say instead of offering a download when this PC cannot run the CUDA build. Null when it can.</summary>
    public static string? Unsuitable(Version? driverCuda) => driverCuda switch
    {
        null => "The picture engine needs an NVIDIA graphics card, and no NVIDIA driver was found.",
        { } v when v < NeedsCuda => $"The picture engine needs a graphics driver that supports CUDA 12; this one supports CUDA {v}. Update the NVIDIA driver, then check again.",
        _ => null,
    };

    /// <summary>The newest release with a Windows CUDA 12 build and its runtime, or null when the newest has neither yet.</summary>
    /// <exception cref="HubException">This PC cannot run it, or GitHub refused.</exception>
    public async Task<EngineRelease?> LatestAsync(Version? driverCuda, CancellationToken ct = default)
    {
        if (Unsuitable(driverCuda) is { } reason) throw new HubException(reason);
        var latest = await EngineUpdater.GetGitHubAsync<EngineUpdater.ApiRelease>(http, $"{Api}/latest", ct).ConfigureAwait(false)
                     ?? throw new HubException("GitHub did not return the latest stable-diffusion.cpp release.");
        var picked = Pick(latest.Assets);
        return picked is null ? null : picked with { Tag = latest.TagName, Published = latest.PublishedAt };
    }

    // The tag is "master-1094-3bad892", but the file names carry only the commit: "sd-master-3bad892-bin-win-cuda12-x64.zip".
    [GeneratedRegex(@"^sd-master-[0-9a-f]+-bin-win-cuda12-x64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramAsset();

    [GeneratedRegex(@"^cudart-sd-bin-win-cu12-x64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeAsset();

    /// <summary>Both files, or null when either is missing (a release's files appear a few minutes after it is made).</summary>
    internal static EngineRelease? Pick(IReadOnlyList<EngineUpdater.ApiAsset> assets)
    {
        var program = assets.FirstOrDefault(a => ProgramAsset().IsMatch(a.Name));
        var runtime = assets.FirstOrDefault(a => RuntimeAsset().IsMatch(a.Name));
        return program is null || runtime is null
            ? null
            : new EngineRelease("", "CUDA 12", EngineUpdater.ToAsset(program), EngineUpdater.ToAsset(runtime), null);
    }

    /// <summary>
    /// Downloads, checks and unpacks a release into the picture engine's folder. Call only with the picture engine
    /// stopped: Windows keeps a running program's files locked. The chat engine is not touched.
    /// </summary>
    /// <param name="progress">Bytes downloaded so far, out of <see cref="EngineRelease.TotalBytes"/>.</param>
    public async Task InstallAsync(EngineRelease release, Action<long> progress, CancellationToken ct = default)
    {
        var work = Path.Combine(paths.Root, "image-engine-download");
        var staged = Path.Combine(paths.Root, "image-engine-new");
        var previous = Path.Combine(paths.Root, "image-engine-old");
        Directory.CreateDirectory(work);
        EngineUpdater.DeleteFolder(staged);

        var zips = await EngineUpdater.DownloadCheckedAsync(http, [release.Program, release.Runtime], work, progress, ct, requireChecksum: true)
            .ConfigureAwait(false);

        Directory.CreateDirectory(staged);
        foreach (var zip in zips)
            EngineUpdater.Extract(zip, staged);
        var root = Directory.EnumerateFiles(staged, ProgramName, SearchOption.AllDirectories)
                       .OrderBy(p => p.Length)
                       .Select(Path.GetDirectoryName)
                       .FirstOrDefault()
                   ?? throw new HubException("The downloaded picture engine does not contain sd-server.exe.");
        if (!string.Equals(root, staged, StringComparison.OrdinalIgnoreCase))
        {
            // The program sits in a subfolder of the archive; the runtime DLLs must sit next to it.
            foreach (var file in Directory.EnumerateFiles(staged, "*.dll", SearchOption.TopDirectoryOnly))
                File.Move(file, Path.Combine(root, Path.GetFileName(file)), overwrite: true);
        }
        await File.WriteAllTextAsync(Path.Combine(root, VersionFile), $"{release.Tag}|{release.Flavor}", ct).ConfigureAwait(false);

        // Swap: current → old, new → current, then drop old. If the second move fails, put the old one back.
        EngineUpdater.DeleteFolder(previous);
        Directory.CreateDirectory(paths.EngineDirectory);
        var hadEngine = Directory.Exists(paths.ImageEngineDirectory);
        if (hadEngine) Directory.Move(paths.ImageEngineDirectory, previous);
        try
        {
            Directory.Move(root, paths.ImageEngineDirectory);
        }
        catch
        {
            if (hadEngine) Directory.Move(previous, paths.ImageEngineDirectory);
            throw;
        }
        EngineUpdater.DeleteFolder(previous);
        EngineUpdater.DeleteFolder(staged);
        EngineUpdater.DeleteFolder(work);
    }
}
