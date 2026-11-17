using DogAI.Core.Models;
using DogAI.Core.Video;

namespace DogAI.Hub;

/// <summary>
/// The download of the video model (M20 step 3): one job holding the files of the recommended set that are not on this
/// PC yet, each into its own folder under <c>&lt;models&gt;\video</c> and each checked against its pinned SHA-256 before it
/// is moved into place. The three Hugging Face files are compared with what their repositories publish first, so a file
/// that changed there is refused before anything is fetched. The fast decoder comes from GitHub at a pinned commit, and
/// <see cref="FileTransfer"/> never sends the Hugging Face token to any host but Hugging Face.
/// </summary>
public static class VideoDownloadJobs
{
    public const string JobTitle = VideoDownloads.Title + " (video)";

    /// <summary>
    /// The job for <paramref name="roles"/>, in the set's own order (the model first, when it is wanted).
    /// <paramref name="listings"/> is each Hugging Face repository's file list (<see cref="HuggingFaceClient.FilesAsync"/>).
    /// Throws <see cref="DownloadRefusedException"/>, in words for the owner, when a file is not there in the form pinned.
    /// </summary>
    public static DownloadJob Build(string root, IReadOnlyCollection<VideoFileRole> roles, IReadOnlyDictionary<string, IReadOnlyList<RemoteFile>> listings)
    {
        var wanted = VideoDownloads.Wan22.Where(d => roles.Contains(d.Role)).ToList();
        if (wanted.Count == 0) throw new DownloadRefusedException("The video model is already on this PC.");
        var parts = new List<DownloadPart>();
        foreach (var d in wanted)
        {
            if (!d.FromGitHub)
            {
                var listed = listings.TryGetValue(d.Repo, out var files) ? files.FirstOrDefault(f => f.Path.Equals(d.SourcePath, StringComparison.Ordinal)) : null;
                if (listed is null)
                    throw new DownloadRefusedException($"{d.FileName} is no longer on Hugging Face ({d.Repo}), so the video model cannot be downloaded.");
                if (listed.Size != d.Bytes || listed.Sha256 is null ||
                    !string.Equals(listed.Sha256.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase), d.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new DownloadRefusedException($"{d.FileName} has changed on Hugging Face since DOG.ai was made ({d.Repo}), so it is not downloaded: DOG.ai downloads only the file it was tried with.");
            }
            parts.Add(new DownloadPart
            {
                Url = d.Url ?? HuggingFaceClient.ResolveUrl(d.Repo, d.SourcePath),
                FileName = d.PartName,
                Size = d.Bytes,
                Sha256 = d.Sha256,
            });
        }
        return new DownloadJob
        {
            Repo = wanted[0].Repo,
            Title = JobTitle,
            UseCase = VideoDownloads.UseCase,
            Folder = root,
            Parts = parts,
        };
    }

    /// <summary>The Hugging Face repositories whose file lists <see cref="Build"/> needs for these files.</summary>
    public static IReadOnlyList<string> ReposFor(IEnumerable<VideoFileRole> roles) =>
        [.. VideoDownloads.Wan22.Where(d => roles.Contains(d.Role) && !d.FromGitHub).Select(d => d.Repo).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The video download in the list that has not finished (queued, running, paused or failed), or null.</summary>
    public static DownloadJob? Unfinished(IEnumerable<DownloadJob> jobs, string? root) =>
        root is null ? null : jobs.LastOrDefault(j => j.UseCase == VideoDownloads.UseCase && j.Status != DownloadStatus.Done && SameFolder(j.Folder, root));

    /// <summary>
    /// The files a video download in the list is still bringing into <paramref name="root"/> (queued, running, paused or
    /// being checked). A failed one brings nothing until it is tried again: its files count as missing.
    /// </summary>
    public static IReadOnlyList<VideoFileRole> Arriving(IEnumerable<DownloadJob> jobs, string? root)
    {
        if (root is null) return [];
        var names = jobs.Where(j => j.UseCase == VideoDownloads.UseCase && j.Status is not (DownloadStatus.Done or DownloadStatus.Failed) && SameFolder(j.Folder, root))
            .SelectMany(j => j.Parts.Where(p => !p.Done).Select(p => p.FileName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. VideoDownloads.Wan22.Where(d => names.Contains(d.PartName)).Select(d => d.Role)];
    }

    private static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
