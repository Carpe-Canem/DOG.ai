using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DogAI.Core;
using DogAI.Core.Net;

namespace DogAI.Hub;

/// <summary>A downloadable file in a llama.cpp release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>The engine build DOG.ai would install: the program, plus the CUDA runtime it needs if any.</summary>
public sealed record EngineRelease(string Tag, string Flavor, ReleaseAsset Program, ReleaseAsset? Runtime, DateTime? Published)
{
    public long TotalBytes => Program.Size + (Runtime?.Size ?? 0);
}

/// <summary>
/// Installs and updates DOG.ai's own copy of llama.cpp from its GitHub releases: the CUDA build
/// matching the graphics driver on NVIDIA cards, otherwise the Vulkan build. Files are hash-checked,
/// unpacked to a side folder, and swapped in only when complete, so a failed update leaves the old engine.
/// </summary>
public sealed partial class EngineUpdater(HttpClient http, AppPaths paths)
{
    private const string Api = "https://api.github.com/repos/ggml-org/llama.cpp/releases";
    private const string VersionFile = "dogai-engine.txt";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The build tag DOG.ai installed, e.g. "b12110". Null when it has not installed one.</summary>
    public string? InstalledTag
    {
        get
        {
            var file = Path.Combine(paths.EngineDirectory, VersionFile);
            try { return File.Exists(file) ? File.ReadAllText(file).Split('|')[0].Trim() : null; }
            catch (IOException) { return null; }
        }
    }

    public bool IsInstalled => File.Exists(Path.Combine(paths.EngineDirectory, "llama-server.exe"));

    /// <summary>
    /// Finds the newest stable build. The project marks numbered builds as pre-releases and publishes a
    /// stable release whose "nightly-tag.txt" names the build it stands for.
    /// </summary>
    public async Task<EngineRelease?> LatestAsync(Version? driverCuda, CancellationToken ct = default)
    {
        var latest = await GetAsync<ApiRelease>($"{Api}/latest", ct).ConfigureAwait(false)
            ?? throw new HubException("GitHub did not return the latest llama.cpp release.");
        var release = latest;
        if (Pick(latest.Assets, driverCuda) is null &&
            latest.Assets.FirstOrDefault(a => a.Name.Equals("nightly-tag.txt", StringComparison.OrdinalIgnoreCase)) is { } pointer)
        {
            var tag = (await GetStringAsync(pointer.BrowserDownloadUrl, ct).ConfigureAwait(false)).Trim();
            if (!TagPattern().IsMatch(tag)) throw new HubException("The llama.cpp release pointed to a build DOG.ai does not recognise.");
            release = await GetAsync<ApiRelease>($"{Api}/tags/{tag}", ct).ConfigureAwait(false)
                ?? throw new HubException($"GitHub did not return llama.cpp build {tag}.");
        }
        var picked = Pick(release.Assets, driverCuda);
        return picked is null ? null : picked with { Tag = release.TagName, Published = release.PublishedAt };
    }

    [GeneratedRegex(@"^b\d+$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^llama-(?<tag>b\d+)-bin-win-(?<kind>cuda|vulkan)(?:-(?<ver>\d+\.\d+))?-x64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramAsset();

    [GeneratedRegex(@"^cudart-llama-bin-win-cuda-(?<ver>\d+\.\d+)-x64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeAsset();

    /// <summary>
    /// The CUDA build for the newest CUDA version the driver supports, with its runtime; the Vulkan build
    /// when there is no NVIDIA driver or no CUDA build suits it.
    /// </summary>
    internal static EngineRelease? Pick(IReadOnlyList<ApiAsset> assets, Version? driverCuda)
    {
        if (driverCuda is not null)
        {
            var cuda = assets
                .Select(a => (Asset: a, Match: ProgramAsset().Match(a.Name)))
                .Where(x => x.Match.Success && x.Match.Groups["kind"].Value.Equals("cuda", StringComparison.OrdinalIgnoreCase)
                            && Version.TryParse(x.Match.Groups["ver"].Value, out var v) && v <= driverCuda)
                .OrderByDescending(x => Version.Parse(x.Match.Groups["ver"].Value))
                .FirstOrDefault();
            if (cuda.Asset is not null)
            {
                var version = cuda.Match.Groups["ver"].Value;
                var runtime = assets.FirstOrDefault(a => RuntimeAsset().Match(a.Name) is { Success: true } m && m.Groups["ver"].Value == version);
                return new EngineRelease("", $"CUDA {version}", ToAsset(cuda.Asset), runtime is null ? null : ToAsset(runtime), null);
            }
        }
        var vulkan = assets.FirstOrDefault(a => ProgramAsset().Match(a.Name) is { Success: true } m &&
                                                m.Groups["kind"].Value.Equals("vulkan", StringComparison.OrdinalIgnoreCase));
        return vulkan is null ? null : new EngineRelease("", "Vulkan", ToAsset(vulkan), null, null);
    }

    /// <summary>
    /// Downloads, checks and unpacks a release into the engine folder. Call only with the engine stopped:
    /// Windows keeps a running program's files locked.
    /// </summary>
    /// <param name="progress">Bytes downloaded so far, out of <see cref="EngineRelease.TotalBytes"/>.</param>
    public async Task InstallAsync(EngineRelease release, Action<long> progress, CancellationToken ct = default)
    {
        var work = Path.Combine(paths.Root, "engine-download");
        var staged = Path.Combine(paths.Root, "engine-new");
        var previous = Path.Combine(paths.Root, "engine-old");
        Directory.CreateDirectory(work);
        DeleteFolder(staged);

        long before = 0;
        var zips = new List<string>();
        foreach (var asset in new[] { release.Program, release.Runtime }.OfType<ReleaseAsset>())
        {
            var zip = Path.Combine(work, asset.Name);
            var part = zip + ".part";
            if (!File.Exists(zip))
            {
                var offset = before;
                await FileTransfer.DownloadAsync(http, asset.Url, part, asset.Size, bearer: null, n => progress(offset + n), ct).ConfigureAwait(false);
                var hash = await FileTransfer.Sha256Async(part, _ => { }, ct).ConfigureAwait(false);
                if (!FileTransfer.HashMatches(hash, asset.Sha256))
                {
                    File.Delete(part);
                    throw new HubException($"{asset.Name} arrived damaged (its checksum did not match). Try again.");
                }
                File.Move(part, zip, overwrite: true);
            }
            before += asset.Size;
            progress(before);
            zips.Add(zip);
        }

        Directory.CreateDirectory(staged);
        foreach (var zip in zips)
            Extract(zip, staged);
        var root = FindProgramFolder(staged)
            ?? throw new HubException("The downloaded engine does not contain llama-server.exe.");
        if (!string.Equals(root, staged, StringComparison.OrdinalIgnoreCase))
        {
            // The program sits in a subfolder of the archive; the runtime DLLs must sit next to it.
            foreach (var file in Directory.EnumerateFiles(staged, "*.dll", SearchOption.TopDirectoryOnly))
                File.Move(file, Path.Combine(root, Path.GetFileName(file)), overwrite: true);
        }
        await File.WriteAllTextAsync(Path.Combine(root, VersionFile), $"{release.Tag}|{release.Flavor}", ct).ConfigureAwait(false);

        // Swap: current → old, new → current, then drop old. If the second move fails, put the old one back.
        DeleteFolder(previous);
        var hadEngine = Directory.Exists(paths.EngineDirectory);
        if (hadEngine) Directory.Move(paths.EngineDirectory, previous);
        try
        {
            Directory.Move(root, paths.EngineDirectory);
        }
        catch
        {
            if (hadEngine) Directory.Move(previous, paths.EngineDirectory);
            throw;
        }
        DeleteFolder(previous);
        DeleteFolder(staged);
        DeleteFolder(work);
    }

    /// <summary>Unpacks an archive, refusing entries that would land outside the target folder.</summary>
    internal static void Extract(string zip, string target)
    {
        var full = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(full, entry.FullName));
            if (!destination.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                throw new HubException("The engine archive contains a file with an unsafe path, so it was not installed.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static string? FindProgramFolder(string folder) =>
        Directory.EnumerateFiles(folder, "llama-server.exe", SearchOption.AllDirectories)
            .OrderBy(p => p.Length)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault();

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private static ReleaseAsset ToAsset(ApiAsset a) =>
        new(a.Name, a.BrowserDownloadUrl, a.Size, a.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null);

    private async Task<T?> GetAsync<T>(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HubException((int)response.StatusCode == 403
                ? "GitHub is limiting requests right now. Try again in a few minutes."
                : $"GitHub answered with an error (HTTP {(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct).ConfigureAwait(false);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HubException($"GitHub answered with an error (HTTP {(int)response.StatusCode}).");
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    internal sealed record ApiRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("published_at")] DateTime? PublishedAt,
        IReadOnlyList<ApiAsset> Assets);

    internal sealed record ApiAsset(
        string Name,
        long Size,
        string? Digest,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}
