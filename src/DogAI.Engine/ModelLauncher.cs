using DogAI.Core.Models;

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

    /// <exception cref="ModelRefusedException">The model cannot fit or cannot chat; nothing was unloaded.</exception>
    public async Task LoadAsync(string path, int context)
    {
        var info = await Task.Run(() => GgufReader.TryRead(path)).ConfigureAwait(false);
        if (ChatRefusal(info, path) is { } cannotChat) throw new ModelRefusedException(cannotChat);
        var decision = await DecideAsync(path, context).ConfigureAwait(false);
        if (decision.Refusal is { } why) throw new ModelRefusedException(why);
        await engine.LoadAsync(path, new EngineOptions(context, decision.GpuLayers)).ConfigureAwait(false);
    }
}
