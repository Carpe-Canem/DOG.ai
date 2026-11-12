using DogAI.Core.Models;

namespace DogAI.Engine;

/// <summary>
/// Loads models with settings that suit this PC: measured ones from a benchmark when there are any,
/// otherwise the number of graphics-card layers the fit estimate says will fit.
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

    public async Task LoadAsync(string path, int context)
    {
        var record = await records.FindAsync(path).ConfigureAwait(false);
        int layers;
        if (record is { TunedGpuLayers: { } tuned, TunedContext: { } tunedContext } && tunedContext == context)
        {
            layers = tuned;
        }
        else
        {
            var file = ModelLibrary.Describe(path);
            var plan = file is null ? null : await PlanAsync(file, context).ConfigureAwait(false);
            // A full fit lets the engine take every layer; a partial fit names how many go on the card.
            layers = plan is { Kind: FitKind.Partial } ? plan.GpuLayers : -1;
        }
        await engine.LoadAsync(path, new EngineOptions(context, layers)).ConfigureAwait(false);
    }
}
