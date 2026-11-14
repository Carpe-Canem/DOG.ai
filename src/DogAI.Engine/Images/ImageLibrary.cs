using System.Collections.Concurrent;
using DogAI.Core.Images;

namespace DogAI.Engine.Images;

/// <summary>One file in the image models folder and what it is.</summary>
/// <param name="Folder">The subfolder it is in ("checkpoints", "loras"…), or "" for the image folder itself.</param>
/// <param name="RelativePath">Its path inside that subfolder, with forward slashes.</param>
/// <param name="Misplaced">The folder it belongs in, when it is in another one (sd-server reads LoRAs only from the loras folder).</param>
public sealed record ImageFile(string Folder, string RelativePath, string FullPath, long SizeBytes, ImageFileVerdict Verdict, string? Misplaced)
{
    public string Name => Path.GetFileNameWithoutExtension(RelativePath);
    public ImageFileKind Kind => Verdict.Kind;

    /// <summary>Usable from where it is: the right kind in the right folder.</summary>
    public bool Usable => Misplaced is null && Kind is ImageFileKind.Checkpoint or ImageFileKind.Lora or ImageFileKind.Vae;
}

/// <summary>
/// Finds and recognises the files in the image models folder. Reads only each .safetensors file's header (opened
/// read-only, shared), never the weights; old pickle formats are listed as refused without being opened. Results are
/// kept per file and read again only when the file's size or date changes.
/// </summary>
public static class ImageLibrary
{
    private static readonly ConcurrentDictionary<string, (long Size, DateTime Written, ImageFileVerdict Verdict)> Known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the image models folder and its subfolders when they are missing. Returns the folder.</summary>
    public static string Ensure(string root)
    {
        foreach (var folder in ImageFolders.All) Directory.CreateDirectory(Path.Combine(root, folder));
        return root;
    }

    /// <summary>Every model file in the image folder and its subfolders. Missing folders are simply empty.</summary>
    public static IReadOnlyList<ImageFile> Scan(string? root)
    {
        var result = new List<ImageFile>();
        if (root is null || !Directory.Exists(root)) return result;
        foreach (var file in Files(root, recursive: false)) result.Add(Read(root, "", file));
        foreach (var folder in ImageFolders.All)
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Files(dir, recursive: true)) result.Add(Read(dir, folder, file));
        }
        return result.OrderBy(f => f.Folder, StringComparer.Ordinal).ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

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
            }).Where(f => ImageFileRecognizer.IsModelFile(f)).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static ImageFile Read(string dir, string folder, string path)
    {
        var info = new FileInfo(path);
        var relative = Path.GetRelativePath(dir, path).Replace('\\', '/');
        var verdict = Recognise(info);
        var home = ImageFolders.For(verdict.Kind);
        var misplaced = home is not null && !string.Equals(home, folder, StringComparison.OrdinalIgnoreCase) ? home : null;
        return new ImageFile(folder, relative, path, info.Length, verdict, misplaced);
    }

    /// <summary>What the file is. Never throws: a file that cannot be read is said to be unusable, with why.</summary>
    public static ImageFileVerdict Recognise(FileInfo file)
    {
        if (ImageFileRecognizer.RefusedExtensions.Contains(file.Extension))
            return new ImageFileVerdict(ImageFileKind.Refused, ImageFileRecognizer.RefusedNote);
        if (Known.TryGetValue(file.FullName, out var known) && known.Size == file.Length && known.Written == file.LastWriteTimeUtc)
            return known.Verdict;
        ImageFileVerdict verdict;
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            verdict = ImageFileRecognizer.Classify(SafetensorsHeader.Read(stream), file.Name);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            verdict = new ImageFileVerdict(ImageFileKind.Unusable, "Not a readable safetensors file (damaged, or still downloading).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // In use by another program, for example a browser still saving it: try again on the next look.
            return new ImageFileVerdict(ImageFileKind.Unusable, "DOG.ai could not open this file: " + e.Message);
        }
        Known[file.FullName] = (file.Length, file.LastWriteTimeUtc, verdict);
        return verdict;
    }
}
