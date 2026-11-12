using System.Text.Json;

namespace DogAI.Core.Models;

/// <summary>A model DOG.ai recommends for one use case.</summary>
/// <param name="Params">Parameter count in billions, for estimating sizes before the file list is fetched.</param>
/// <param name="Experts">Mixture-of-experts model: much faster than its size suggests.</param>
public sealed record CuratedPick(string UseCase, string Repo, string Title, double Params, string Blurb, bool Experts = false)
{
    /// <summary>
    /// Rough downloadable versions from the parameter count alone, so the list can show a fit
    /// while offline. Replaced by the real file list once online.
    /// </summary>
    public IReadOnlyList<ModelVariant> EstimatedVariants() =>
        new[] { "Q3_K_M", "IQ4_XS", "Q4_K_M", "Q5_K_M", "Q6_K", "Q8_0" }
            .Select(q => new ModelVariant(q, q, [new RemoteFile(q, (long)(Params * 1e9 * Quantization.Bits(q) / 8), null)]))
            .ToList();

    /// <summary>Shape used for fit estimates before the real header is read.</summary>
    public ModelShape EstimatedShape(long weightBytes) => new()
    {
        WeightBytes = weightBytes,
        // Typical mixture-of-experts models use about one sixteenth of their experts per token.
        ExpertCount = Experts ? 128 : null,
        ExpertsUsed = Experts ? 8 : null,
    };
}

/// <summary>The recommended models shipped with the app (curated-models.json).</summary>
public static class CuratedPicks
{
    public static IReadOnlyList<string> UseCases { get; } = ["general", "characters", "story", "embedding"];

    public static string Label(string useCase) => useCase switch
    {
        "general" => "General",
        "characters" => "Characters",
        "story" => "Story",
        "embedding" => "Document search",
        _ => useCase,
    };

    public static string Updated { get; private set; } = "";

    public static IReadOnlyList<CuratedPick> All { get; } = Load();

    private static IReadOnlyList<CuratedPick> Load()
    {
        using var stream = typeof(CuratedPicks).Assembly.GetManifestResourceStream("DogAI.Core.Models.curated-models.json");
        if (stream is null) return [];
        var file = JsonSerializer.Deserialize<CuratedFile>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Updated = file?.Updated ?? "";
        return file?.Picks ?? [];
    }

    private sealed record CuratedFile(string? Updated, List<CuratedPick> Picks);
}
