using System.Text.RegularExpressions;

namespace DogAI.Engine;

public sealed record ModelFile(string Path, string Name, long SizeBytes)
{
    public string SizeText => $"{SizeBytes / 1024d / 1024d / 1024d:0.0} GB";
}

/// <summary>Finds model files on disk. (Downloading and the full library screen arrive in milestone 2.)</summary>
public static partial class ModelLibrary
{
    [GeneratedRegex(@"-(\d{5})-of-\d{5}$")]
    private static partial Regex SplitPart();

    /// <summary>Lists the .gguf files under a folder, skipping vision add-ons and the later parts of split models.</summary>
    public static IReadOnlyList<ModelFile> Scan(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return [];
        var found = new List<ModelFile>();
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 };
            foreach (var path in Directory.EnumerateFiles(directory, "*.gguf", options))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                if (name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) continue;
                var part = SplitPart().Match(name);
                if (part.Success && part.Groups[1].Value != "00001") continue;
                found.Add(new ModelFile(path, name, new FileInfo(path).Length));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return found.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
