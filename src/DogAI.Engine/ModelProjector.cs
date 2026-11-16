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
    public static ProjectorFile? Find(string modelPath, GgufInfo? model = null)
    {
        if (Path.GetDirectoryName(modelPath) is not { Length: > 0 } folder || !Directory.Exists(folder)) return null;
        model ??= GgufReader.TryRead(modelPath);
        // A document search model is given no pictures, and a model without the number cannot be matched.
        if (model?.EmbeddingLength is not > 0 || model.IsEmbedding(modelPath)) return null;

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
            return null;
        }
        return Projectors.Pick(belonging, p => p.Name);
    }
}
