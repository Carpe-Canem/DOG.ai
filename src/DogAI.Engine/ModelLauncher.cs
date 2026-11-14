using DogAI.Core.Models;
using DogAI.Core.Settings;

namespace DogAI.Engine;

/// <summary>
/// Thrown when a model is refused for chat: it cannot fit on this PC, or it is a document search model that cannot write.
/// The message says why, in plain words.
/// </summary>
public sealed class ModelRefusedException(string message) : Exception(message);

/// <summary>
/// Loads models with settings that suit this PC: measured ones from a benchmark when there are any,
/// otherwise the number of graphics-card layers the fit estimate says will fit. Models that cannot
/// fit at all are refused before anything is unloaded.
/// </summary>
public sealed class ModelLauncher(EngineManager engine, HardwareProbe probe, IModelRecordStore records)
{
    /// <summary>How this file would run at the given context size, judged from its header and free memory now.</summary>
    public async Task<FitResult?> PlanAsync(ModelFile file, int context, CancellationToken ct = default)
    {
        var info = await Task.Run(() => GgufReader.TryRead(file.Path), ct).ConfigureAwait(false);
        if (info is null) return null;
        var budget = await BudgetAsync(ct).ConfigureAwait(false);
        return FitEstimator.Estimate(info.ToShape(file.SizeBytes), context, budget);
    }

    /// <summary>The memory a new model may use. Counts what the loaded model holds, since loading another frees it.</summary>
    public async Task<FitBudget> BudgetAsync(CancellationToken ct = default)
    {
        var hw = await probe.ProbeAsync(ct).ConfigureAwait(false);
        var held = engine.ProcessId is int pid ? await probe.VramOfProcessAsync(pid, ct).ConfigureAwait(false) : 0;
        return FitBudget.From(hw, held);
    }

    /// <summary>Decides how to load a model, without loading it. <see cref="LoadDecision.Refusal"/> is set when it cannot fit.</summary>
    public async Task<LoadDecision> DecideAsync(string path, int context, CancellationToken ct = default)
    {
        var record = await records.FindAsync(path, ct).ConfigureAwait(false);
        var file = ModelLibrary.Describe(path);
        var info = file is null ? null : await Task.Run(() => GgufReader.TryRead(file.Path), ct).ConfigureAwait(false);
        if (file is null || info is null) return LoadDecision.Decide(Path.GetFileNameWithoutExtension(path), context, null, null, record);
        var budget = await BudgetAsync(ct).ConfigureAwait(false);
        var plan = FitEstimator.Estimate(info.ToShape(file.SizeBytes), context, budget);
        return LoadDecision.Decide(info.DisplayName(path), context, plan, budget, record);
    }

    /// <summary>
    /// Why a model file cannot be loaded for chat regardless of memory, or null when nothing stands in the way.
    /// Document search (embedding) models only turn text into numbers; asked to write, they repeat one word until the limit.
    /// </summary>
    public static string? ChatRefusal(GgufInfo? info, string path) =>
        info?.IsEmbedding(path) == true
            ? $"{info.DisplayName(path)} is a document search model. It finds passages in documents but cannot write replies. Pick a chat model instead."
            : null;

    private int _loadingDefault;
    private volatile bool _heldUnloaded;

    /// <summary>
    /// Unloads (or stops loading) because the user asked. Defaults are then not loaded on their own until the user
    /// loads a model again; unloading after idle time does not count, and neither does the graphics-card coordinator
    /// setting the model aside for a picture (<see cref="EngineManager.RestAsync"/>), which never comes through here.
    /// A model that was set aside is forgotten by this too, so it does not come back with the next message.
    /// </summary>
    public async Task UnloadByUserAsync()
    {
        _heldUnloaded = true;
        await engine.UnloadAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Makes sure a chat model is ready for one of DOG.ai's own jobs (drafting looks, planning a picture): waits for a load
    /// that is under way (opening a page often starts loading the mode's default), else loads the mode's default. True
    /// when a model is ready (or set aside, which the job's first request brings back).
    /// </summary>
    /// <exception cref="ModelRefusedException">The default cannot fit or cannot chat.</exception>
    public async Task<bool> EnsureReadyAsync(AppSettings settings, string mode, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            while (engine.State == EngineState.Loading || Volatile.Read(ref _loadingDefault) == 1)
                await Task.Delay(200, ct).ConfigureAwait(false);
            if (engine.IsReady) return true;
            if (attempt == 0) await LoadDefaultIfIdleAsync(settings, mode).ConfigureAwait(false);
        }
        return engine.IsReady;
    }

    /// <summary>
    /// Loads the default model for a mode ("general", "characters", "story") when no model is loaded, loading or failed.
    /// Returns the path that was loaded, or null when there was nothing to do (a model is already in, no default is set,
    /// the file is gone, the engine is missing, or another page is already loading a default).
    /// </summary>
    /// <exception cref="ModelRefusedException">The default cannot fit or cannot chat.</exception>
    public async Task<string?> LoadDefaultIfIdleAsync(AppSettings settings, string mode)
    {
        // A model set aside for a picture is not Idle: opening a page must not bring it (or a default) back early.
        if (engine.State != EngineState.Idle || _heldUnloaded) return null;
        if (!settings.DefaultModels.TryGetValue(mode, out var path) || !File.Exists(path)) return null;
        if (engine.FindExecutable() is null) return null;
        if (Interlocked.Exchange(ref _loadingDefault, 1) == 1) return null;
        try
        {
            if (engine.State != EngineState.Idle) return null;
            // Nobody has asked this model anything yet: a picture may set it aside before it finishes loading.
            await LoadAsync(path, settings.ContextLength, automatic: true).ConfigureAwait(false);
            return engine.State == EngineState.Ready ? path : null;
        }
        finally
        {
            Volatile.Write(ref _loadingDefault, 0);
        }
    }

    /// <exception cref="ModelRefusedException">
    /// The model cannot fit or cannot chat; no loaded model was unloaded (a default that a page was still loading by
    /// itself may have been stopped: it had not been asked anything).
    /// </exception>
    /// <param name="automatic">True for a mode's default loaded because a page opened (see <see cref="EngineManager.SetAsideLoadAsync"/>).</param>
    public async Task LoadAsync(string path, int context, bool automatic = false)
    {
        var info = await Task.Run(() => GgufReader.TryRead(path)).ConfigureAwait(false);
        if (ChatRefusal(info, path) is { } cannotChat) throw new ModelRefusedException(cannotChat);
        // Waits for a picture being drawn and lets the idle picture model go, before measuring what fits: otherwise the
        // picture model's 8 GB would count against this model and push part of it off the graphics card.
        using var turn = automatic
            ? await engine.TakeCardTurnAsync(waking: false).ConfigureAwait(false)
            : await TakeTurnAheadOfPageLoadsAsync().ConfigureAwait(false);
        // A page's own load that had to wait for its turn: a model that was loaded or set aside meanwhile stays.
        if (automatic && engine.State != EngineState.Idle) return;
        var decision = await DecideAsync(path, context).ConfigureAwait(false);
        if (decision.Refusal is { } why) throw new ModelRefusedException(why);
        _heldUnloaded = false;
        await engine.LoadAsync(path, new EngineOptions(context, decision.GpuLayers), automatic).ConfigureAwait(false);
    }

    /// <summary>
    /// The turn on the graphics card for a model somebody asked for. Such a load does not wait behind one nobody asked
    /// for: a page's own load keeps the turn until it ends (half a minute for a large model), so it is stopped
    /// (<see cref="EngineManager.DropAutomaticLoadAsync"/>). That load may hold the turn a moment before the engine says
    /// it is loading (while it measures what fits), so this looks again until the turn is ours. A picture being drawn,
    /// or another load somebody asked for, is waited for as before.
    /// </summary>
    private async Task<IDisposable> TakeTurnAheadOfPageLoadsAsync()
    {
        var turn = engine.TakeCardTurnAsync(waking: false);
        while (!turn.IsCompleted)
        {
            await engine.DropAutomaticLoadAsync().ConfigureAwait(false);
            await Task.WhenAny(turn, Task.Delay(100)).ConfigureAwait(false);
        }
        return await turn.ConfigureAwait(false);
    }
}
