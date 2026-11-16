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
}
