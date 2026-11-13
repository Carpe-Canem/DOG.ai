using DogAI.Core.Models;
using DogAI.Core.Settings;
using DogAI.Engine;
using DogAI.Hub;

namespace DogAI.UI;

/// <summary>
/// What the Models screens share: this PC's measured hardware, the memory budget for fit badges,
/// the context size being planned for, and file lists fetched from Hugging Face this session.
/// </summary>
public sealed class ModelsContext(HardwareProbe probe, EngineManager engine, ISettingsStore settings, HuggingFaceClient hf)
{
    private readonly Dictionary<string, Task<IReadOnlyList<ModelVariant>>> _variants = new(StringComparer.OrdinalIgnoreCase);

    public HardwareProfile Hardware { get; private set; } = HardwareProfile.Unknown;
    public FitBudget Budget { get; private set; } = new(0, 0, 0, FitBudget.RamGbps);

    /// <summary>Graphics memory the loaded model holds now.</summary>
    public long EngineVram { get; private set; }

    public int Context { get; private set; } = 8192;
    public bool Measured { get; private set; }

    /// <summary>Lists also show models too large for this PC.</summary>
    public bool ShowAll { get; private set; }

    /// <summary>Which models to list: see <see cref="ModelFilters"/>.</summary>
    public string Filter { get; private set; } = ModelFilters.Usable;

    public event Action? Changed;

    public async Task RefreshAsync()
    {
        Hardware = await probe.ProbeAsync();
        EngineVram = engine.ProcessId is int pid ? await probe.VramOfProcessAsync(pid) : 0;
        Budget = FitBudget.From(Hardware, EngineVram);
        var saved = await settings.LoadAsync();
        Context = saved.ContextLength;
        ShowAll = saved.ModelsShowAll;
        Filter = ModelFilters.Normalize(saved.ModelsFilter);
        Measured = true;
        Changed?.Invoke();
    }

    public async Task SetShowAllAsync(bool showAll)
    {
        await settings.SaveAsync(await settings.LoadAsync() with { ModelsShowAll = showAll });
        ShowAll = showAll;
        Changed?.Invoke();
    }

    public async Task SetFilterAsync(string filter)
    {
        filter = ModelFilters.Normalize(filter);
        await settings.SaveAsync(await settings.LoadAsync() with { ModelsFilter = filter });
        Filter = filter;
        Changed?.Invoke();
    }

    /// <summary>
    /// Whether this PC can run the smallest version of a model with this many parameters entirely on the
    /// graphics card. Models whose size is unknown are given the benefit of the doubt.
    /// </summary>
    public bool CanRun(long? parameters) =>
        parameters is not > 0 || Fit(new ModelShape { WeightBytes = ModelFilters.SmallestBytes(parameters.Value) }).Kind == FitKind.Full;

    public async Task SetContextAsync(int context)
    {
        await settings.SaveAsync(await settings.LoadAsync() with { ContextLength = context });
        Context = context;
        Changed?.Invoke();
    }

    public FitResult Fit(ModelShape shape) => FitEstimator.Estimate(shape, Context, Budget);

    /// <summary>A repository's downloadable variants. Fetched once per session; a failed fetch is retried next time.</summary>
    public async Task<IReadOnlyList<ModelVariant>> VariantsAsync(string repo)
    {
        Task<IReadOnlyList<ModelVariant>> task;
        lock (_variants)
        {
            if (!_variants.TryGetValue(repo, out task!))
                _variants[repo] = task = FetchAsync(repo);
        }
        try
        {
            return await task;
        }
        catch
        {
            lock (_variants) _variants.Remove(repo);
            throw;
        }
    }

    public bool HasVariants(string repo)
    {
        lock (_variants) return _variants.TryGetValue(repo, out var t) && t.IsCompletedSuccessfully;
    }

    private async Task<IReadOnlyList<ModelVariant>> FetchAsync(string repo) =>
        Quantization.Group(await hf.FilesAsync(repo));

    /// <summary>The variant to pre-select for this PC.</summary>
    public ModelVariant? Best(IEnumerable<ModelVariant> variants, Func<long, ModelShape> shape) =>
        Quantization.PickBest(variants.Select(v => (v, Fit(shape(v.Size)))));

    public static string[] ContextSizes { get; } = ["4096", "8192", "16384", "32768", "65536", "131072"];
}
