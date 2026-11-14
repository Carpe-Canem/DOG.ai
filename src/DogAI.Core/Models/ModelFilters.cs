namespace DogAI.Core.Models;

/// <summary>
/// The choices behind the Models lists: hide what this PC cannot run, and narrow the rest to the
/// best-liked models or the best-liked uncensored ones.
/// </summary>
public static class ModelFilters
{
    /// <summary>Everything that fits (the default).</summary>
    public const string Usable = "usable";

    /// <summary>Best-liked models that fit.</summary>
    public const string Top = "top";

    /// <summary>Best-liked uncensored models that fit.</summary>
    public const string Uncensored = "uncensored";

    public static string Normalize(string? filter) => filter is Top or Uncensored ? filter : Usable;

    /// <summary>The words that mark a model as having its refusals removed or never trained in.</summary>
    private static readonly string[] Markers = ["uncensored", "abliterated", "abliteration", "unfiltered", "decensored", "heretic", "unaligned"];

    /// <summary>Whether a repository calls itself uncensored, by its name or tags. Both are written by strangers, so this is only a hint.</summary>
    public static bool IsUncensored(string repoId, IEnumerable<string>? tags) =>
        Markers.Any(m => repoId.Contains(m, StringComparison.OrdinalIgnoreCase)) ||
        (tags?.Any(t => Markers.Any(m => t.Contains(m, StringComparison.OrdinalIgnoreCase))) ?? false);

    /// <summary>
    /// The smallest sensible download of a model with this many parameters (a 3-bit version), for deciding
    /// whether any version of it could run before the real file list is known.
    /// </summary>
    public static long SmallestBytes(long parameters) =>
        (long)(parameters * Quantization.Bits("Q3_K_M") / 8);
}
