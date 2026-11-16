namespace DogAI.Core.Models;

/// <summary>
/// The rules for a chat model's projector: the second file (a GGUF with "mmproj" in its name, made for one model
/// family) that lets the model see pictures (M17). The numbers are from the trial of 2026-11-16.
/// </summary>
public static class Projectors
{
    /// <summary>What a loaded projector holds on the graphics card beyond its file size.</summary>
    public const long ExtraBytes = Sizes.GiB / 4;

    /// <summary>Graphics memory a projector of this file size takes; 0 for none. (0.92 GB of file held 1.1 GB.)</summary>
    public static long Need(long fileBytes) => fileBytes <= 0 ? 0 : fileBytes + ExtraBytes;

    /// <summary>True for a file that can be a projector by its name alone; its header has the last word.</summary>
    public static bool IsProjectorName(string fileName) =>
        fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && fileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which of several projectors is taken, lowest first: F16, then BF16, then Q8_0, then any other; null for F32, which
    /// is never taken (twice the size for nothing).
    /// </summary>
    public static int? Rank(string fileName) => Quantization.Parse(fileName) switch
    {
        "F32" => null,
        "F16" => 0,
        "BF16" => 1,
        "Q8_0" => 2,
        _ => 3,
    };

    /// <summary>The projector to take among files that all belong to the model, or none.</summary>
    public static T? Pick<T>(IEnumerable<T> candidates, Func<T, string> fileName) where T : class =>
        candidates.Select(c => (Candidate: c, Name: fileName(c), Rank: Rank(fileName(c))))
            .Where(c => c.Rank is not null && IsProjectorName(c.Name))
            .OrderBy(c => c.Rank).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Candidate)
            .FirstOrDefault();

    // ---- In a repository on Hugging Face (M17 step 3) ----

    /// <summary>The download use case of a projector fetched on its own (Get picture input): it is no chat model and gets no library record.</summary>
    public const string UseCase = "projector";

    /// <summary>True when a repository's file list holds a projector DOG.ai would take (the "Sees pictures" mark on Recommended and Browse).</summary>
    public static bool AnyIn(IEnumerable<string> filePaths) => filePaths.Any(p => Rank(FileName(p)) is not null && IsProjectorName(FileName(p)));

    /// <summary>
    /// The projector offered with one version of a model, from its repository's file list, or null. Of the projectors
    /// beside the version's file (in the same folder of the repository, else at its top), one named for this model
    /// ("Huihui-Qwen3.5-9B-abliterated.mmproj-f16.gguf" for "Huihui-Qwen3.5-9B-abliterated.Q8_0") comes before one
    /// named for none ("mmproj-F16.gguf"), which comes before one named for another model; then <see cref="Pick"/>.
    /// Whether it really belongs is told from its header once it is here (the engine's <c>ModelProjector.Find</c>).
    /// </summary>
    public static RemoteFile? ForVariant(IEnumerable<RemoteFile> files, ModelVariant variant)
    {
        var projectors = files.Where(f => IsProjectorName(FileName(f.Path)) && Rank(FileName(f.Path)) is not null).ToList();
        var folder = FolderOf(variant.Main.Path);
        var near = projectors.Where(f => FolderOf(f.Path) == folder).ToList();
        if (near.Count == 0) near = projectors.Where(f => FolderOf(f.Path) == "").ToList();
        if (near.Count == 0) return null;
        var model = FileName(variant.Main.Path);
        var tier = near.Min(f => Tier(FileName(f.Path), model));
        return Pick(near.Where(f => Tier(FileName(f.Path), model) == tier), f => FileName(f.Path));
    }

    /// <summary>0: named for this model; 1: named for no model; 2: named for another.</summary>
    private static int Tier(string projector, string model)
    {
        var at = projector.IndexOf("mmproj", StringComparison.OrdinalIgnoreCase);
        var named = projector[..at].TrimEnd('.', '-', '_');
        if (named.Length == 0) return 1;
        return model.StartsWith(named, StringComparison.OrdinalIgnoreCase) && model.Length > named.Length && model[named.Length] is '.' or '-' or '_' ? 0 : 2;
    }

    private static string FileName(string path) => path.Split('/')[^1];
    private static string FolderOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    // ---- Wording (Models section) ----

    /// <summary>The mark on a chat model that can look at pictures.</summary>
    public const string Label = "Sees pictures";

    /// <summary>The line behind the mark's ⓘ.</summary>
    public const string Explanation =
        "It can look at pictures you put in a chat. That needs a second file beside the model, its picture input (a projector, about 0.9 GB), " +
        "which is loaded with the model whenever it is there and takes about 1.1 GB more graphics memory.";

    /// <summary>What a download with its projector says beside its size.</summary>
    public static string WithInput(long projectorBytes) => $"with picture input, + {Sizes.Gb(projectorBytes)}";

    /// <summary>The download list's line for a model fetched with its projector.</summary>
    public static string WithInputParts(int modelParts) =>
        modelParts > 1 ? $"in {modelParts} parts, with picture input" : "with picture input";
}
