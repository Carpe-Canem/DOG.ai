using System.Text.RegularExpressions;

namespace DogAI.Core.Models;

/// <summary>One file in a model repository, as Hugging Face lists it.</summary>
/// <param name="Sha256">The file's SHA-256 as published, used to check the download. Null when not published.</param>
public sealed record RemoteFile(string Path, long Size, string? Sha256);

/// <summary>One downloadable version of a model: a quantization, made of one file or several split parts.</summary>
public sealed record ModelVariant(string Name, string? Quant, IReadOnlyList<RemoteFile> Parts)
{
    public long Size => Parts.Sum(p => p.Size);

    /// <summary>The file llama-server is pointed at; for split models, the first part.</summary>
    public RemoteFile Main => Parts[0];

    public double Bits => Quantization.Bits(Quant);
}

/// <summary>Reads quantization labels from GGUF file names and picks the right one for this PC.</summary>
public static partial class Quantization
{
    [GeneratedRegex(@"-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitSuffix();

    [GeneratedRegex(@"(?:^|[-_.])((?:UD-)?(?:I?Q\d(?:_[A-Z0-9]+)*|BF16|F16|F32|MXFP4(?:_MOE)?|TQ\d_\d))(?=[-_.]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex QuantLabel();

    /// <summary>The quantization in a file name, e.g. "Q4_K_M" or "UD-Q4_K_XL". Null when there is none.</summary>
    public static string? Parse(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        name = SplitSuffix().Replace(name, ".gguf");
        if (name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        var matches = QuantLabel().Matches(name);
        return matches.Count == 0 ? null : matches[^1].Groups[1].Value.ToUpperInvariant();
    }

    /// <summary>Approximate bits per weight, used to rank quality. 0 when unknown.</summary>
    public static double Bits(string? quant)
    {
        if (quant is null) return 0;
        var q = quant.StartsWith("UD-", StringComparison.Ordinal) ? quant[3..] : quant;
        return q switch
        {
            "F32" => 32,
            "BF16" or "F16" => 16,
            "Q8_0" or "Q8_K_XL" => 8.5,
            "Q6_K" or "Q6_K_XL" => 6.6,
            "Q5_K_M" or "Q5_K_XL" => 5.7,
            "Q5_K_S" or "Q5_0" or "Q5_1" => 5.5,
            "MXFP4" or "MXFP4_MOE" => 4.25,
            "Q4_K_M" or "Q4_K_XL" => 4.85,
            "Q4_K_S" or "Q4_0" or "Q4_1" or "IQ4_NL" => 4.5,
            "IQ4_XS" => 4.25,
            "Q3_K_L" or "Q3_K_XL" => 4.1,
            "Q3_K_M" => 3.9,
            "IQ3_M" or "IQ3_S" => 3.6,
            "Q3_K_S" => 3.5,
            "IQ3_XS" or "IQ3_XXS" => 3.1,
            "Q2_K" or "Q2_K_XL" or "Q2_K_L" => 3.0,
            "IQ2_M" or "IQ2_S" => 2.7,
            "IQ2_XS" or "IQ2_XXS" => 2.3,
            _ when q.StartsWith("IQ1", StringComparison.Ordinal) || q.StartsWith("TQ1", StringComparison.Ordinal) => 1.8,
            _ when q.StartsWith("Q", StringComparison.Ordinal) && q.Length > 1 && char.IsDigit(q[1]) => q[1] - '0' + 0.5,
            _ when q.StartsWith("IQ", StringComparison.Ordinal) && q.Length > 2 && char.IsDigit(q[2]) => q[2] - '0' + 0.3,
            _ => 0,
        };
    }

    /// <summary>
    /// Turns a repository's file list into downloadable variants: joins split parts, and leaves out
    /// files that are not models on their own (vision add-ons, draft heads, importance matrices).
    /// </summary>
    public static IReadOnlyList<ModelVariant> Group(IEnumerable<RemoteFile> files)
    {
        var groups = new Dictionary<string, List<RemoteFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
            var name = System.IO.Path.GetFileName(file.Path);
            if (IsAddOn(name)) continue;
            var key = SplitSuffix().Replace(file.Path, ".gguf");
            if (!groups.TryGetValue(key, out var parts)) groups[key] = parts = [];
            parts.Add(file);
        }
        return groups
            .Select(g => new ModelVariant(
                System.IO.Path.GetFileNameWithoutExtension(g.Key),
                Parse(g.Key),
                g.Value.OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(v => v.Size)
            .ToList();
    }

    public static bool IsAddOn(string fileName) =>
        fileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("mtp-", StringComparison.OrdinalIgnoreCase) ||
        fileName.Contains("imatrix", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The variant to pre-select. Among those that fit fully on the card, the best quality up to 8-bit
    /// (bigger files are not noticeably better, only slower). If none fit, a 4-to-5-bit one that runs
    /// partly on the card. If nothing can run, null.
    /// </summary>
    public static ModelVariant? PickBest(IEnumerable<(ModelVariant Variant, FitResult Fit)> options)
    {
        var all = options.ToList();
        var full = all.Where(o => o.Fit.Kind == FitKind.Full && o.Variant.Bits is > 0 and <= 8.5).ToList();
        if (full.Count > 0)
            return full.OrderByDescending(o => o.Variant.Bits).ThenBy(o => o.Variant.Size).First().Variant;

        var partial = all.Where(o => o.Fit.Kind == FitKind.Partial).ToList();
        if (partial.Count == 0)
            return all.Where(o => o.Fit.Kind == FitKind.Full).OrderBy(o => o.Variant.Size).Select(o => o.Variant).FirstOrDefault();
        var sweet = partial.Where(o => o.Variant.Bits is >= 4.25 and <= 5)
            .OrderByDescending(o => o.Variant.Bits).ThenBy(o => o.Variant.Size).FirstOrDefault();
        if (sweet.Variant is not null) return sweet.Variant;
        return partial.Where(o => o.Variant.Bits is >= 3 and < 4.25).OrderByDescending(o => o.Variant.Bits).Select(o => o.Variant).FirstOrDefault()
            ?? partial.OrderBy(o => o.Variant.Size).First().Variant;
    }
}
