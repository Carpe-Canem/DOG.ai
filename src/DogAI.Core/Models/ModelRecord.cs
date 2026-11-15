namespace DogAI.Core.Models;

/// <summary>
/// What DOG.ai remembers about one installed model file: where it came from and how it ran
/// when benchmarked. Files found on disk without a record still show in the library.
/// </summary>
public sealed class ModelRecord
{
    public long Id { get; set; }

    /// <summary>Full path of the model file (the first part, for split models).</summary>
    public required string Path { get; set; }

    /// <summary>Hugging Face repository it was downloaded from, e.g. "unsloth/gemma-4-12b-it-GGUF".</summary>
    public string? Repo { get; set; }

    public string? Quant { get; set; }
    public DateTime AddedAt { get; set; }

    /// <summary>Use-case tag from the curated list: general, characters, story, embedding.</summary>
    public string? UseCase { get; set; }

    public double? BenchTokensPerSecond { get; set; }
    public double? BenchReadPerSecond { get; set; }

    /// <summary>The settings the benchmark settled on. Used when the model is loaded later.</summary>
    public int? TunedGpuLayers { get; set; }
    public int? TunedContext { get; set; }
    public DateTime? BenchAt { get; set; }

    /// <summary>
    /// Graphics memory the model really held the last time it was loaded whole on the card, measured a moment after
    /// the load, and the context size it was loaded with. The "Fits beside pictures" mark uses it in place of the
    /// estimate, which errs high (see <see cref="BesidePictures"/>).
    /// </summary>
    public long? MeasuredVramBytes { get; set; }
    public int? MeasuredVramContext { get; set; }

    /// <summary>What the model really took at this context size, or null when it was never loaded whole at it.</summary>
    public long? MeasuredAt(int context) => MeasuredVramBytes is > 0 and var bytes && MeasuredVramContext == context ? bytes : null;
}

public interface IModelRecordStore
{
    Task<IReadOnlyList<ModelRecord>> AllAsync(CancellationToken ct = default);
    Task<ModelRecord?> FindAsync(string path, CancellationToken ct = default);

    /// <summary>Adds the record, or updates the one with the same path.</summary>
    Task SaveAsync(ModelRecord record, CancellationToken ct = default);

    Task DeleteAsync(string path, CancellationToken ct = default);
}

/// <summary>Named JSON documents kept in the database, for small state such as the download queue.</summary>
public interface IDocumentStore
{
    Task<T?> LoadAsync<T>(string key, CancellationToken ct = default) where T : class;
    Task SaveAsync<T>(string key, T value, CancellationToken ct = default) where T : class;
}
