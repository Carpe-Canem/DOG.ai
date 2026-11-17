using System.Text.RegularExpressions;
using DogAI.Core.Models;

namespace DogAI.Engine;

/// <param name="Path">The file to load (the first part, for split models).</param>
/// <param name="SizeBytes">Size of all parts together.</param>
/// <param name="Parts">Every file that belongs to the model, so deleting removes them all.</param>
public sealed record ModelFile(string Path, string Name, long SizeBytes, IReadOnlyList<string> Parts)
{
    public ModelFile(string path, string name, long sizeBytes) : this(path, name, sizeBytes, [path]) { }

    public string SizeText => Sizes.Gb(SizeBytes);
    public string? Quant => Quantization.Parse(Path);
}

/// <summary>Finds model files on disk.</summary>
public static partial class ModelLibrary
{
    [GeneratedRegex(@"-(\d{5})-of-(\d{5})$")]
    private static partial Regex SplitPart();

    /// <summary>Lists the .gguf files under a folder, skipping add-ons (vision, draft heads) and joining split models.</summary>
    public static IReadOnlyList<ModelFile> Scan(string? directory) => ScanAll([directory]);

    /// <remarks>
    /// The video models folder (<c>&lt;folder&gt;\video</c> of every folder looked in) is skipped: the video model's text
    /// encoder is a .gguf, and its header even counts as a document search model's (<see cref="GgufInfo.IsEmbedding"/>), so
    /// it would be offered as a chat model and could be picked for document search (M20 step 3). Skipped for every folder
    /// looked in, also when one of them holds another (an extra folder above the models folder).
    /// </remarks>
    public static IReadOnlyList<ModelFile> ScanAll(IEnumerable<string?> directories)
    {
        var found = new Dictionary<string, ModelFile>(StringComparer.OrdinalIgnoreCase);
        var roots = directories.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var skipped = roots.Select(VideoFolderOf).OfType<string>().ToList();
        foreach (var directory in roots)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
                foreach (var path in Directory.EnumerateFiles(directory, "*.gguf", options))
                {
                    if (found.ContainsKey(path)) continue;
                    if (InVideoFolder(path, skipped)) continue;
                    var name = System.IO.Path.GetFileNameWithoutExtension(path);
                    if (Quantization.IsAddOn(name)) continue;
                    var part = SplitPart().Match(name);
                    if (part.Success && part.Groups[1].Value != "00001") continue;
                    var parts = part.Success ? SplitParts(path, name[..part.Index], part.Groups[2].Value) : [path];
                    var size = parts.Sum(p => new FileInfo(p).Length);
                    found[path] = new ModelFile(path, part.Success ? name[..part.Index] : name, size, parts);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return found.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool InVideoFolder(string path, List<string> videoFolders)
    {
        if (videoFolders.Count == 0) return false;
        var full = System.IO.Path.GetFullPath(path);
        return videoFolders.Any(v => full.StartsWith(v, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The video models folder inside a models folder, ending in a separator, or null for a folder that cannot be one.</summary>
    private static string? VideoFolderOf(string directory)
    {
        try
        {
            return System.IO.Path.Combine(System.IO.Path.GetFullPath(directory), DogAI.Core.Video.VideoFolders.Name) + System.IO.Path.DirectorySeparatorChar;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static List<string> SplitParts(string first, string stem, string total)
    {
        var folder = System.IO.Path.GetDirectoryName(first)!;
        var count = int.Parse(total);
        var parts = new List<string>();
        for (var i = 1; i <= count; i++)
        {
            var candidate = System.IO.Path.Combine(folder, $"{stem}-{i:00000}-of-{total}.gguf");
            if (File.Exists(candidate)) parts.Add(candidate);
        }
        return parts;
    }

    /// <summary>Describes one model file without loading it. Reads only the header.</summary>
    public static ModelFile? Describe(string path)
    {
        if (!File.Exists(path)) return null;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var part = SplitPart().Match(name);
        if (!part.Success) return new ModelFile(path, name, new FileInfo(path).Length);
        var parts = SplitParts(path, name[..part.Index], part.Groups[2].Value);
        return new ModelFile(path, name[..part.Index], parts.Sum(p => new FileInfo(p).Length), parts);
    }
}
