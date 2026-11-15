using DogAI.Core.Models;
using DogAI.Core.Settings;
using DogAI.Core.Images;
using DogAI.Engine;
using DogAI.Engine.Images;
using DogAI.Hub;

namespace DogAI.UI;

/// <summary>
/// What the Models screens share: this PC's measured hardware, the memory budget for fit badges,
/// the context size being planned for, and file lists fetched from Hugging Face this session.
/// </summary>
public sealed class ModelsContext
{
    private readonly Dictionary<string, Task<IReadOnlyList<ModelVariant>>> _variants = new(StringComparer.OrdinalIgnoreCase);
    private readonly HardwareProbe probe;
    private readonly EngineManager engine;
    private readonly ISettingsStore settings;
    private readonly HuggingFaceClient hf;
    private readonly ImageServer pictures;
    private readonly SdServerLocator pictureEngine;
    private readonly ImageModelCatalog catalog;

    public ModelsContext(HardwareProbe probe, EngineManager engine, ISettingsStore settings, HuggingFaceClient hf,
        ImageServer pictures, SdServerLocator pictureEngine, ImageModelCatalog catalog, ModelLauncher launcher)
    {
        this.probe = probe;
        this.engine = engine;
        this.settings = settings;
        this.hf = hf;
        this.pictures = pictures;
        this.pictureEngine = pictureEngine;
        this.catalog = catalog;
        // Another checkpoint was chosen: what a picture needs changes, and with it which models fit beside one.
        catalog.Changed += () => _ = RefreshPicturesAsync();
        // A moment after a load the model's real graphics memory is known (Windows' per-program counters lag behind the
        // load itself): measure again, so the readout, the fit badges and the "Fits beside pictures" marks are right.
        launcher.MemoryMeasured += () => { if (Measured) _ = RefreshAsync(); };
    }

    public HardwareProfile Hardware { get; private set; } = HardwareProfile.Unknown;
    public FitBudget Budget { get; private set; } = new(0, 0, 0, FitBudget.RamGbps);

    /// <summary>Graphics memory the loaded model holds now.</summary>
    public long EngineVram { get; private set; }

    /// <summary>Graphics memory the picture model holds now.</summary>
    public long PictureVram { get; private set; }

    /// <summary>
    /// What a Standard picture needs with the checkpoint in use (<see cref="BesidePictures.PictureNeed"/>); null when
    /// there is no picture engine or no checkpoint, and then no model is marked as fitting beside pictures.
    /// </summary>
    public long? PictureNeed { get; private set; }

    /// <summary>The checkpoint <see cref="PictureNeed"/> was worked out for, by name.</summary>
    public string? PictureCheckpoint { get; private set; }

    /// <summary>Graphics memory DOG.ai's own models can have: what is free, plus what its chat and picture models hold now.</summary>
    public long CardBytes => (Hardware.MainGpu?.FreeBytes ?? 0) + EngineVram + PictureVram;

    /// <summary>True for a chat model that can stay in graphics memory while a picture is drawn (the "Fits beside pictures" mark).</summary>
    /// <param name="measuredBytes">What the model really held when it was last loaded at this context size, when known (<see cref="ModelRecord.MeasuredAt"/>).</param>
    public bool FitsBesidePictures(FitResult? fit, long? measuredBytes = null) => Measured && BesidePictures.Fits(fit, CardBytes, PictureNeed, measuredBytes);

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
        PictureVram = pictures.ProcessId is int picturePid ? await probe.VramOfProcessAsync(picturePid) : 0;
        // What the picture model holds counts as free for a chat model: loading one lets the idle picture model go.
        Budget = FitBudget.From(Hardware, EngineVram + PictureVram);
        var saved = await settings.LoadAsync();
        await ReadPicturesAsync(saved);
        Context = saved.ContextLength;
        ShowAll = saved.ModelsShowAll;
        Filter = ModelFilters.Normalize(saved.ModelsFilter);
        Measured = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// Looks again at the picture engine and the checkpoint in use (a quick look at files; nothing is measured). Called
    /// when a Models tab opens, since either can change on another tab.
    /// </summary>
    public async Task RefreshPicturesAsync()
    {
        var (need, name) = (PictureNeed, PictureCheckpoint);
        await ReadPicturesAsync(await settings.LoadAsync());
        if (need != PictureNeed || name != PictureCheckpoint) Changed?.Invoke();
    }

    private async Task ReadPicturesAsync(AppSettings saved)
    {
        ImageFile? checkpoint = null;
        if (pictureEngine.Find() is not null)
        {
            var root = saved.ImageFolder();
            var checkpoints = (await Task.Run(() => ImageLibrary.Scan(root))).Where(f => f.Usable && f.Kind == ImageFileKind.Checkpoint).ToList();
            var chosen = catalog.ChosenAmong(checkpoints.Select(c => c.RelativePath).ToList());
            checkpoint = checkpoints.FirstOrDefault(c => c.RelativePath == chosen);
        }
        PictureNeed = checkpoint is null ? null : BesidePictures.PictureNeed(checkpoint.SizeBytes);
        PictureCheckpoint = checkpoint?.Name;
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

    private readonly Dictionary<string, IReadOnlyList<ModelAbout>> _about = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A description already fetched this session (possibly empty: nothing found), or null when not fetched yet.</summary>
    public IReadOnlyList<ModelAbout>? KnownAbout(string repo)
    {
        lock (_about) return _about.GetValueOrDefault(repo);
    }

    /// <summary>
    /// What the model cards say about a repository, following its base model when its own card only
    /// lists files. Kept for the session, including "nothing found"; a failed fetch is retried next time.
    /// </summary>
    public async Task<IReadOnlyList<ModelAbout>> AboutAsync(string repo, CancellationToken ct = default)
    {
        if (KnownAbout(repo) is { } known) return known;
        var about = await ModelCard.DescribeAsync(repo, hf.CardAsync, ct);
        lock (_about) _about[repo] = about;
        return about;
    }

    private async Task<IReadOnlyList<ModelVariant>> FetchAsync(string repo) =>
        Quantization.Group(await hf.FilesAsync(repo));

    /// <summary>The variant to pre-select for this PC.</summary>
    public ModelVariant? Best(IEnumerable<ModelVariant> variants, Func<long, ModelShape> shape) =>
        Quantization.PickBest(variants.Select(v => (v, Fit(shape(v.Size)))));

    public static string[] ContextSizes { get; } = DogAI.Core.Models.ContextSizes.Offered.Select(s => s.ToString()).ToArray();
}
