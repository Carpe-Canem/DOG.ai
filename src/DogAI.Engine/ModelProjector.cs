using System.Collections.Concurrent;
using DogAI.Core.Models;

namespace DogAI.Engine;

/// <summary>A projector file found beside a chat model.</summary>
public sealed record ProjectorFile(string Path, long SizeBytes)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Finds the projector of a chat model: the file that lets it see pictures (M17). The rule: a .gguf with "mmproj" in its
/// name, in the model's own folder, whose header says it is a projector for pictures and whose output width equals the
/// model's embedding length. A projector that does not belong is never passed to the engine: it would load the whole
/// model and then stop. Nothing is saved: the file is looked for at each load, so a moved folder needs no rewriting.
/// </summary>
public static class ModelProjector
{
    private static readonly ConcurrentDictionary<(string Path, long Size, long Written), GgufInfo?> Headers = new();

    /// <summary>The projector to load with this model, or null when it has none that belongs.</summary>
    public static ProjectorFile? Find(string modelPath, GgufInfo? model = null) => Projectors.Pick(Belonging(modelPath, model), p => p.Name);

    /// <summary>
    /// Every projector in the model's folder that belongs to it, F32 included (<see cref="Find"/> picks among them).
    /// Remove picture input deletes them all, or the next one would be picked.
    /// </summary>
    public static IReadOnlyList<ProjectorFile> Belonging(string modelPath, GgufInfo? model = null)
    {
        if (Path.GetDirectoryName(modelPath) is not { Length: > 0 } folder || !Directory.Exists(folder)) return [];
        model ??= GgufReader.TryRead(modelPath);
        // A document search model is given no pictures, and a model without the number cannot be matched.
        if (model?.EmbeddingLength is not > 0 || model.IsEmbedding(modelPath)) return [];

        var belonging = new List<ProjectorFile>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.gguf", SearchOption.TopDirectoryOnly))
            {
                if (!Projectors.IsProjectorName(Path.GetFileName(path))) continue;
                var file = new FileInfo(path);
                var header = Headers.GetOrAdd((path.ToLowerInvariant(), file.Length, file.LastWriteTimeUtc.Ticks), _ => GgufReader.TryRead(path));
                if (header is { IsVisionProjector: true } && header.ProjectionDim == model.EmbeddingLength)
                    belonging.Add(new ProjectorFile(path, file.Length));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return belonging;
    }

    /// <summary>True when a projector's header says it belongs to a model with this embedding length (checked before Get picture input downloads it).</summary>
    public static bool Fits(GgufInfo? projector, int? embeddingLength) =>
        projector is { IsVisionProjector: true } && embeddingLength is > 0 && projector.ProjectionDim == embeddingLength;

    /// <summary>The other chat models in the same folder (its top only, as <see cref="Find"/> looks) that these projector files belong to.</summary>
    public static IReadOnlyList<ModelFile> AlsoUsedBy(string modelPath, IEnumerable<string> projectorPaths)
    {
        var folder = Path.GetDirectoryName(modelPath);
        if (folder is null) return [];
        var wanted = projectorPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return [];
        return ModelLibrary.Scan(folder)
            .Where(m => string.Equals(Path.GetDirectoryName(m.Path), folder, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(Path.GetFullPath(m.Path), Path.GetFullPath(modelPath), StringComparison.OrdinalIgnoreCase))
            .Where(m => Belonging(m.Path).Any(p => wanted.Contains(Path.GetFullPath(p.Path))))
            .ToList();
    }

    /// <summary>
    /// The projector files to delete with a model (Delete): those that belong to it and to no other model in its folder.
    /// One another model there still uses stays.
    /// </summary>
    public static (IReadOnlyList<ProjectorFile> Delete, IReadOnlyList<ProjectorFile> Keep) WithModel(string modelPath, GgufInfo? model = null)
    {
        var mine = Belonging(modelPath, model);
        var delete = new List<ProjectorFile>();
        var keep = new List<ProjectorFile>();
        foreach (var projector in mine)
            (AlsoUsedBy(modelPath, [projector.Path]).Count > 0 ? keep : delete).Add(projector);
        return (delete, keep);
    }
}
