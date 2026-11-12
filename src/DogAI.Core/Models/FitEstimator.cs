namespace DogAI.Core.Models;

/// <summary>
/// What the fit estimate needs to know about a model. Every field except the size is optional;
/// missing facts are filled with rough guesses so a file listed online still gets a badge.
/// </summary>
public sealed record ModelShape
{
    /// <summary>Size of the weights on disk (all parts of a split model together).</summary>
    public required long WeightBytes { get; init; }

    public int? Layers { get; init; }

    /// <summary>Memory the context cache needs per token of context, from the model header.</summary>
    public long? KvBytesPerToken { get; init; }

    public int? TrainedContext { get; init; }

    /// <summary>Mixture-of-experts models only read part of their weights for each token.</summary>
    public int? ExpertCount { get; init; }
    public int? ExpertsUsed { get; init; }

    /// <summary>
    /// Context cache per token for a standard transformer: keys and values (2) for every layer,
    /// for each key/value head, at 16-bit (2 bytes) precision.
    /// </summary>
    public static long? KvPerToken(int? layers, int? kvHeads, int? keyLength, int? valueLength)
    {
        if (layers is not > 0 || kvHeads is not > 0 || keyLength is not > 0) return null;
        var value = valueLength is > 0 ? valueLength.Value : keyLength.Value;
        return (long)layers.Value * kvHeads.Value * (keyLength.Value + value) * 2;
    }

    /// <summary>A guess for files whose header has not been read: about 128 KB per token for an 8B model, growing slowly with size.</summary>
    internal long KvPerTokenOrGuess() =>
        KvBytesPerToken is > 0 ? KvBytesPerToken.Value
            : (long)(128 * 1024 * Math.Sqrt(Math.Max(0.1, WeightBytes / (4.5 * Sizes.GiB))));

    /// <summary>Share of the weights read for each generated token.</summary>
    internal double ActiveShare =>
        ExpertCount is > 1 && ExpertsUsed is > 0 ? Math.Min(1, 0.2 + 0.8 * ExpertsUsed.Value / ExpertCount.Value) : 1;
}

/// <summary>How much memory DOG AI may plan to use, and how fast it is.</summary>
/// <param name="VramBytes">Graphics memory available to a model, after other programs.</param>
/// <param name="RamBytes">System memory available for the part of a model that does not fit on the card.</param>
public sealed record FitBudget(long VramBytes, long RamBytes, double GpuGBps, double RamGBps)
{
    /// <summary>Kept free on the card for Windows and other programs.</summary>
    public const long VramMargin = 512L * 1024 * 1024;

    /// <summary>Kept free in system memory so Windows and the app itself stay responsive.</summary>
    public const long RamMargin = 4 * Sizes.GiB;

    /// <param name="engineVramBytes">Graphics memory DOG AI's own engine holds now; it is given back when another model loads.</param>
    public static FitBudget From(HardwareProfile hw, long engineVramBytes = 0)
    {
        var gpu = hw.MainGpu;
        var vram = gpu is null ? 0 : Math.Max(0, gpu.FreeBytes + engineVramBytes - VramMargin);
        var ram = Math.Max(0, hw.RamFreeBytes - RamMargin);
        return new FitBudget(vram, ram, GpuBandwidth.For(gpu?.Name), RamGbps);
    }

    /// <summary>Typical real-world speed of dual-channel DDR5 memory.</summary>
    public const double RamGbps = 60;
}

public enum FitKind
{
    /// <summary>Everything is on the graphics card: fast.</summary>
    Full,
    /// <summary>Part of the model runs from system memory: works, but slower.</summary>
    Partial,
    /// <summary>Does not fit even with system memory helping.</summary>
    TooLarge,
}

/// <param name="GpuLayers">Layers to put on the graphics card. Equal to the layer count for a full fit.</param>
/// <param name="NeededBytes">Total memory the model needs at this context size.</param>
/// <param name="TokensPerSecond">A rough speed estimate; replaced by a measured value after a benchmark.</param>
public sealed record FitResult(FitKind Kind, int GpuLayers, int Layers, long NeededBytes, double TokensPerSecond)
{
    public string Label => Kind switch
    {
        FitKind.Full => "Fits on GPU",
        FitKind.Partial => "Partly on GPU",
        _ => "Too large for this PC",
    };

    public string SpeedText => Kind == FitKind.TooLarge || TokensPerSecond <= 0 ? "" : $"~{Math.Max(1, Math.Round(TokensPerSecond)):0} tok/s";
}

/// <summary>
/// Judges whether a model fits: its weights, plus the context cache for the chosen context length,
/// plus working memory, compared with free graphics memory. Speed comes from how many bytes each
/// generated token has to read and how fast the card (and system memory) can read them.
/// </summary>
public static class FitEstimator
{
    /// <summary>Working memory the engine needs on the card besides weights and cache.</summary>
    public static long Overhead(int context) => 640L * 1024 * 1024 + 8L * 1024 * context;

    /// <summary>Real decoding reaches about this share of the memory's rated speed.</summary>
    private const double Efficiency = 0.6;

    private const int AssumedLayers = 32;

    public static FitResult Estimate(ModelShape model, int context, FitBudget budget)
    {
        if (model.TrainedContext is > 0 and var trained) context = Math.Min(context, trained);
        context = Math.Max(512, context);

        var layers = model.Layers is > 0 ? model.Layers.Value : AssumedLayers;
        var weights = Math.Max(1, model.WeightBytes);
        var kv = model.KvPerTokenOrGuess() * context;
        var overhead = Overhead(context);
        var needed = weights + kv + overhead;
        var perToken = weights * model.ActiveShare;

        if (needed <= budget.VramBytes)
            return new FitResult(FitKind.Full, layers, layers, needed, Efficiency * budget.GpuGBps * 1e9 / perToken);

        // Partial offload: each layer brings its share of weights and cache onto the card.
        var perLayer = (weights + kv) / (double)layers;
        var onGpu = budget.VramBytes <= overhead ? 0 : (int)Math.Clamp(Math.Floor((budget.VramBytes - overhead) / perLayer), 0, layers - 1);
        var gpuShare = onGpu / (double)layers;
        var inRam = (long)((weights + kv) * (1 - gpuShare));
        if (inRam > budget.RamBytes)
            return new FitResult(FitKind.TooLarge, 0, layers, needed, 0);

        var seconds = (gpuShare > 0 ? perToken * gpuShare / (Efficiency * budget.GpuGBps * 1e9) : 0) +
                      perToken * (1 - gpuShare) / (Efficiency * budget.RamGBps * 1e9);
        return new FitResult(FitKind.Partial, onGpu, layers, needed, 1 / seconds);
    }
}

/// <summary>What to do when asked to load a model: how many layers to put on the card, or why not to load it.</summary>
/// <param name="GpuLayers">-1 means every layer.</param>
/// <param name="Refusal">Set when the model should not be loaded; says why in plain words.</param>
public sealed record LoadDecision(int GpuLayers, string? Refusal)
{
    /// <summary>
    /// Uses settings a benchmark proved for this context size; otherwise follows the fit estimate.
    /// A model that does not fit even with system memory is refused, because trying only ends in an
    /// out-of-memory failure. With no estimate (header unreadable) it is loaded and left to the engine.
    /// </summary>
    public static LoadDecision Decide(string name, int context, FitResult? plan, FitBudget? budget, ModelRecord? record)
    {
        if (record is { TunedGpuLayers: { } tuned, TunedContext: { } tunedContext } && tunedContext == context)
            return new LoadDecision(tuned, null);
        return plan?.Kind switch
        {
            FitKind.Partial => new LoadDecision(plan.GpuLayers, null),
            FitKind.TooLarge => new LoadDecision(0, TooLargeText(name, context, plan, budget)),
            _ => new LoadDecision(-1, null),
        };
    }

    private static string TooLargeText(string name, int context, FitResult plan, FitBudget? budget)
    {
        var free = budget is null ? "" : $", but only about {Sizes.Gb(budget.VramBytes + budget.RamBytes)} of graphics and system memory is free";
        return $"{name} is too large for this PC. With a context of {context:N0} tokens it needs about {Sizes.Gb(plan.NeededBytes)}{free}. " +
               "Try a smaller context size, a smaller version of the model, or closing other programs.";
    }
}

/// <summary>Rated memory speed of common graphics cards, used for speed estimates before a benchmark.</summary>
public static class GpuBandwidth
{
    private static readonly (string Name, double GBps)[] Known =
    [
        ("5090", 1792), ("4090", 1008), ("3090 Ti", 1008), ("3090", 936), ("5080", 960), ("4080", 717),
        ("5070 Ti", 896), ("4070 Ti SUPER", 672), ("4070 Ti", 504), ("5070", 672), ("4070", 504), ("3080 Ti", 912),
        ("3080", 760), ("4060 Ti", 288), ("3070", 448), ("5060", 448), ("4060", 272), ("3060", 360),
        ("7900 XTX", 960), ("7900 XT", 800), ("A6000", 768),
    ];

    /// <summary>Falls back to a modest mid-range figure for cards not on the list.</summary>
    public static double For(string? gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return 0;
        foreach (var (name, gbps) in Known)
            if (gpuName.Contains(name, StringComparison.OrdinalIgnoreCase)) return gbps;
        return 400;
    }
}
