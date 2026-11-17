using System.Collections.Concurrent;
using DogAI.Core.Images;
using DogAI.Core.Video;

namespace DogAI.Engine.Video;

/// <summary>
/// Finds and recognises the files in the video models folder (<c>&lt;models&gt;\video</c>, M20 step 3), in the way
/// <see cref="Images.ImageLibrary"/> does for pictures: each .safetensors file's header and the text encoder's GGUF
/// header are read (opened read only, shared), never the weights; the old pickle formats are listed as refused without
/// being opened. Results are kept per file and read again only when its size or date changes.
/// </summary>
public static class VideoLibrary
{
    private static readonly ConcurrentDictionary<string, (long Size, DateTime Written, VideoFileVerdict Verdict)> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the video models folder and its subfolders when they are missing. Returns the folder.</summary>
    public static string Ensure(string root)
    {
        foreach (var folder in VideoFolders.All) Directory.CreateDirectory(Path.Combine(root, folder));
        return root;
    }

    /// <summary>
    /// What is in the folder: each of the recommended set's four files (ready, missing, arriving or wrong) and every other
    /// model file in the folder and its three subfolders. <paramref name="arriving"/> says which roles a download in the
    /// list is still bringing. A missing folder is simply empty.
    /// </summary>
    public static VideoLook Look(string? root, IReadOnlyCollection<VideoFileRole>? arriving = null)
    {
        if (root is null) return VideoLook.Nothing(null);
        var model = VideoFolders.ModelFor(root, VideoFiles.Wan22);
        var paths = new Dictionary<VideoFileRole, string>
        {
            [VideoFileRole.Model] = model.DiffusionPath,
            [VideoFileRole.Vae] = model.VaePath,
            [VideoFileRole.TextEncoder] = model.TextEncoderPath,
            [VideoFileRole.FastDecoder] = model.FastDecoderPath,
        };
        var found = VideoDownloads.Wan22.Select(d => Find(d, paths[d.Role], arriving?.Contains(d.Role) == true)).ToList();
        return new VideoLook(root, found, Others(root, paths.Values.ToHashSet(StringComparer.OrdinalIgnoreCase)));
    }

    private static VideoFileFound Find(VideoDownload d, string path, bool arriving)
    {
        var file = new FileInfo(path);
        if (arriving) return new VideoFileFound(d.Role, path, VideoFileStatus.Arriving, file.Exists ? file.Length : 0, "Being downloaded.");
        if (!file.Exists) return new VideoFileFound(d.Role, path, VideoFileStatus.Missing, 0, "Not on this PC.");
        var verdict = Recognise(file);
        if (verdict.Kind != VideoFileVerdict.KindFor(d.Role))
        {
            var why = verdict.Kind == VideoFileKind.Unknown || verdict.Kind == VideoFileKind.OtherWan || verdict.Kind == VideoFileKind.Refused
                ? verdict.Note
                : $"This is {Article(verdict)}, not {d.Title.ToLowerInvariant()}.";
            return new VideoFileFound(d.Role, path, VideoFileStatus.Wrong, file.Length, why);
        }
        var unusual = file.Length != d.Bytes;
        return new VideoFileFound(d.Role, path, VideoFileStatus.Ready, file.Length,
            unusual ? verdict.Note + " Not the size of the recommended file: it may work; DOG.ai has not tried it." : verdict.Note, unusual);
    }

    private static string Article(VideoFileVerdict v) => v.Kind switch
    {
        VideoFileKind.Wan22Model => "the model",
        VideoFileKind.Wan22Vae => "Wan 2.2's own decoder",
        VideoFileKind.Umt5Encoder => "the text encoder",
        VideoFileKind.Wan22FastDecoder => "the fast decoder",
        _ => "another file",
    };

    /// <summary>Every model file in the video folder (top and its three subfolders) that is not one of the four.</summary>
    private static List<VideoOtherFile> Others(string root, HashSet<string> four)
    {
        var result = new List<VideoOtherFile>();
        if (!Directory.Exists(root)) return result;
        foreach (var path in Files(root, recursive: false)) Add("", root, path);
        foreach (var folder in VideoFolders.All)
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Files(dir, recursive: true)) Add(folder, dir, path);
        }
        return [.. result.OrderBy(f => f.Folder, StringComparer.Ordinal).ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)];

        void Add(string folder, string dir, string path)
        {
            if (four.Contains(Path.GetFullPath(path))) return;
            var info = new FileInfo(path);
            result.Add(new VideoOtherFile(folder, Path.GetRelativePath(dir, path).Replace('\\', '/'), path, info.Length, Recognise(info)));
        }
    }

    /// <summary>Model files only: what the engine could be given, and the formats DOG.ai refuses (listed, never opened).</summary>
    private static IEnumerable<string> Files(string dir, bool recursive)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
                MaxRecursionDepth = 4,
            }).Where(f => VideoModel.IsOpenable(f) || ImageFileRecognizer.RefusedExtensions.Contains(Path.GetExtension(f))).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>What the file is, by its header. Never throws, and never opens a refused format.</summary>
    public static VideoFileVerdict Recognise(FileInfo file)
    {
        if (ImageFileRecognizer.RefusedExtensions.Contains(file.Extension))
            return new VideoFileVerdict(VideoFileKind.Refused, ImageFileRecognizer.RefusedNote);
        if (!VideoModel.IsOpenable(file.Name))
            return new VideoFileVerdict(VideoFileKind.Unknown, "Not a .safetensors or .gguf file.");
        if (Known.TryGetValue(file.FullName, out var known) && known.Size == file.Length && known.Written == file.LastWriteTimeUtc)
            return known.Verdict;
        VideoFileVerdict verdict;
        try
        {
            if (file.Extension.Equals(".gguf", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                var info = GgufReader.Read(stream);
                verdict = VideoFileRecognizer.ClassifyGguf(info.Architecture, info.EmbeddingLength, info.Name, info.BaseName, file.Name);
            }
            else
            {
                using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                verdict = VideoFileRecognizer.Classify(SafetensorsHeader.Read(stream));
            }
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or NotSupportedException)
        {
            verdict = new VideoFileVerdict(VideoFileKind.Unknown, "Not a readable model file (damaged, or still downloading).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use by another program: try again on the next look.
            return new VideoFileVerdict(VideoFileKind.Unknown, "DOG.ai could not open this file: " + e.Message);
        }
        Known[file.FullName] = (file.Length, file.LastWriteTimeUtc, verdict);
        return verdict;
    }

    /// <summary>
    /// Deletes the recommended set's four files from the folder (and any unfinished download of them), nothing else.
    /// Returns what could not be deleted, in words, or null.
    /// </summary>
    public static string? DeleteModel(string root)
    {
        var problems = new List<string>();
        foreach (var d in VideoDownloads.Wan22)
        {
            var path = Path.Combine(root, d.PartName);
            foreach (var p in new[] { path, path + ".part", path + ".part.segments" })
            {
                try
                {
                    if (File.Exists(p)) File.Delete(p);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"{d.FileName}: {e.Message}");
                }
            }
            Known.TryRemove(path, out _);
        }
        return problems.Count == 0 ? null : "Could not delete " + string.Join("; ", problems);
    }

    /// <summary>Deletes one other file in the video folder; refuses anything outside it. Returns why it failed, or null.</summary>
    public static string? DeleteOther(string root, string fullPath)
    {
        var folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(fullPath);
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return "That file is not in the video models folder.";
        try
        {
            if (File.Exists(path)) File.Delete(path);
            Known.TryRemove(path, out _);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Could not delete {Path.GetFileName(path)}: {e.Message}";
        }
    }
}
