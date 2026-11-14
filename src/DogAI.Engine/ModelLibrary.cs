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

    public static IReadOnlyList<ModelFile> ScanAll(IEnumerable<string?> directories)
    {
        var found = new Dictionary<string, ModelFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
                foreach (var path in Directory.EnumerateFiles(directory, "*.gguf", options))
                {
                    if (found.ContainsKey(path)) continue;
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
