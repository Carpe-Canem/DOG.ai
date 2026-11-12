using DogAI.Core.Models;

namespace DogAI.Engine;

public sealed record BenchmarkResult(double TokensPerSecond, double ReadPerSecond, int GpuLayers, int Context, int Attempts);

/// <summary>
/// Measures a model on this PC and remembers settings that work. It loads the model with the number of
/// graphics-card layers the fit estimate suggests; if loading fails (usually out of graphics memory) it
/// retries with fewer layers. The speed and the layer count that worked are saved for later loads.
/// </summary>
public sealed class ModelBenchmark(EngineManager engine, ModelLauncher launcher, IModelRecordStore records)
{
    private const int MaxAttempts = 4;

    public bool IsRunning { get; private set; }

    /// <param name="status">Plain-words progress for the screen.</param>
    public async Task<BenchmarkResult> RunAsync(ModelFile file, int context, Action<string> status, CancellationToken ct = default)
    {
        if (IsRunning) throw new EngineException("A benchmark is already running.");
        IsRunning = true;
        try
        {
            status("Working out how much fits on the graphics card…");
            var plan = await launcher.PlanAsync(file, context, ct).ConfigureAwait(false);
            var layerCount = plan?.Layers ?? 0;
            var layers = plan is { Kind: FitKind.Partial } ? plan.GpuLayers : -1;

            for (var attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                status(attempt == 1 ? "Loading the model…" : $"Retrying with {layers} layers on the graphics card…");
                await engine.LoadAsync(file.Path, new EngineOptions(context, layers)).ConfigureAwait(false);
                if (engine.IsReady) break;
                if (attempt >= MaxAttempts || layerCount <= 0 || layers == 0)
                    throw new EngineException(engine.Error ?? "The model could not be loaded.");
                // Step down by about a sixth of the model each time.
                var from = layers < 0 ? layerCount : layers;
                layers = Math.Max(0, from - Math.Max(1, layerCount / 6));
            }

            status("Measuring speed…");
            // A short warm-up first, so one-off start-up costs do not count against the model.
            await engine.BenchmarkAsync(16, ct).ConfigureAwait(false);
            var timings = await engine.BenchmarkAsync(160, ct).ConfigureAwait(false);

            var record = await records.FindAsync(file.Path, ct).ConfigureAwait(false) ?? new ModelRecord { Path = file.Path, Quant = file.Quant };
            record.BenchTokensPerSecond = timings.GenerateTokensPerSecond;
            record.BenchReadPerSecond = timings.ReadTokensPerSecond;
            record.TunedGpuLayers = layers;
            record.TunedContext = engine.ContextLength > 0 ? context : null;
            record.BenchAt = DateTime.UtcNow;
            await records.SaveAsync(record, ct).ConfigureAwait(false);

            return new BenchmarkResult(timings.GenerateTokensPerSecond, timings.ReadTokensPerSecond, layers, engine.ContextLength, 0);
        }
        finally
        {
            IsRunning = false;
        }
    }
}
