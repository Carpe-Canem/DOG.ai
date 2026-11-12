using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DogAI.Core.Chat;

namespace DogAI.Engine;

public enum EngineState
{
    Idle,
    Loading,
    Ready,
    Failed,
}

/// <param name="ContextLength">Context window to allocate, in tokens. Capped at what the model was trained for.</param>
/// <param name="GpuLayers">How many layers go on the graphics card; -1 lets the engine put on as many as fit.</param>
public sealed record EngineOptions(int ContextLength = 8192, int GpuLayers = -1);

/// <summary>
/// Owns the llama-server process: starts it for one model on a random port bound to 127.0.0.1,
/// waits until it is ready, and stops it on unload or exit. One model at a time.
/// </summary>
public sealed class EngineManager : IChatEngine, IDisposable
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProcessJob _job = new();
    private readonly Queue<string> _recentOutput = new();
    private Process? _process;
    private LlamaApi? _api;
    private CancellationTokenSource? _loadCts;

    public EngineState State { get; private set; }
    public string? ModelPath { get; private set; }
    public string? ModelName { get; private set; }
    public int ContextLength { get; private set; }

    /// <summary>Layers placed on the graphics card for the loaded model; -1 means "as many as fit".</summary>
    public int GpuLayers { get; private set; }

    /// <summary>Process id of the running engine, for measuring its graphics memory.</summary>
    public int? ProcessId => _process is { } p && !p.HasExited ? p.Id : null;

    /// <summary>Generation speed of the most recent reply, in tokens per second.</summary>
    public double LastTokensPerSecond { get; private set; }

    /// <summary>When the engine last loaded a model or answered.</summary>
    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.Now;

    /// <summary>Unload the model after this long without use, to free graphics memory. Null never unloads.</summary>
    public TimeSpan? IdleUnloadAfter { get; set; }

    /// <summary>What went wrong, in plain words, when <see cref="State"/> is Failed.</summary>
    public string? Error { get; private set; }

    public bool IsReady => State == EngineState.Ready;

    /// <summary>True while a reply or benchmark is being generated; idle unload waits for it.</summary>
    public bool IsBusy => Volatile.Read(ref _busy) > 0;
    private int _busy;
    private readonly Timer _idleTimer;

    private readonly LoopbackHttp _http;
    private readonly LlamaServerLocator _locator;

    public EngineManager(LoopbackHttp http, LlamaServerLocator locator)
    {
        _http = http;
        _locator = locator;
        _idleTimer = new Timer(_ => _ = UnloadIfIdleAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Raised on any state change. May come from a background thread.</summary>
    public event Action? Changed;

    /// <summary>Lines the engine prints, for the app log.</summary>
    public event Action<string>? Output;

    public string? FindExecutable() => _locator.Find();

    /// <summary>Loads a model, replacing any model already loaded. Problems end up in <see cref="Error"/>, not as exceptions.</summary>
    public async Task LoadAsync(string modelPath, EngineOptions options)
    {
        _loadCts?.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        try
        {
            StopProcess();
            var info = GgufReader.TryRead(modelPath);
            ModelPath = modelPath;
            ModelName = info?.DisplayName(modelPath) ?? Path.GetFileNameWithoutExtension(modelPath);
            Error = null;
            Set(EngineState.Loading);

            if (!File.Exists(modelPath)) throw new EngineException("That model file no longer exists.");
            if (info is null) throw new EngineException("That file is not a model DOG AI can load (it is not a GGUF file).");
            var exe = _locator.Find() ?? throw new EngineException(
                "The model engine (llama-server) was not found on this PC. Install it with: winget install llama.cpp");

            var context = Math.Max(512, options.ContextLength);
            if (info.ContextLength is > 0 and var trained) context = Math.Min(context, trained);

            var port = FreePort();
            var key = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            foreach (var arg in new[]
            {
                "--model", modelPath, "--host", "127.0.0.1", "--port", port.ToString(),
                "--ctx-size", context.ToString(), "--gpu-layers", options.GpuLayers < 0 ? "999" : options.GpuLayers.ToString(),
                "--parallel", "1", "--no-webui", "--jinja",
            })
                start.ArgumentList.Add(arg);
            // The key keeps other programs on this PC from using the engine; it goes in the environment, not the command line.
            start.Environment["LLAMA_API_KEY"] = key;

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Remember(e.Data);
            process.ErrorDataReceived += (_, e) => Remember(e.Data);
            process.Exited += (_, _) => OnExited(process);
            lock (_recentOutput) _recentOutput.Clear();
            process.Start();
            _job.Add(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            var api = new LlamaApi(_http.Client, port, key);
            await WaitUntilReadyAsync(process, api, cts.Token).ConfigureAwait(false);
            ContextLength = await api.ContextLengthAsync(cts.Token).ConfigureAwait(false) ?? context;
            GpuLayers = options.GpuLayers;
            LastTokensPerSecond = 0;
            LastActivity = DateTimeOffset.Now;
            _api = api;
            Set(EngineState.Ready);
        }
        catch (OperationCanceledException)
        {
            StopProcess();
            ModelPath = ModelName = null;
            Set(EngineState.Idle);
        }
        catch (Exception e)
        {
            StopProcess();
            Error = e is EngineException ? e.Message : "The model could not be loaded: " + e.Message;
            Set(EngineState.Failed);
        }
        finally
        {
            if (_loadCts == cts) _loadCts = null;
            _gate.Release();
        }
    }

    /// <summary>Stops the engine and frees the model's memory. Also cancels a load in progress.</summary>
    public async Task UnloadAsync()
    {
        _loadCts?.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopProcess();
            ModelPath = ModelName = null;
            Error = null;
            Set(EngineState.Idle);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<int> CountTokensAsync(string text, CancellationToken ct = default) => Api().TokenizeAsync(text, ct);

    public Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct = default) =>
        Api().ApplyTemplateAsync(messages, ct);

    public IAsyncEnumerable<ChatDelta> StreamChatAsync(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, CancellationToken ct = default) =>
        TrackAsync(Api().StreamChatAsync(messages, sampler, ct));

    public IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, SamplerPreset sampler, CancellationToken ct = default) =>
        TrackAsync(Api().StreamCompletionAsync(prompt, sampler, ct));

    /// <summary>Counts the reply as engine activity (for idle unload) and reports its speed.</summary>
    private async IAsyncEnumerable<ChatDelta> TrackAsync(IAsyncEnumerable<ChatDelta> source)
    {
        Interlocked.Increment(ref _busy);
        try
        {
            await foreach (var delta in source.ConfigureAwait(false))
            {
                if (delta.Stats is { TokensPerSecond: > 0 } stats)
                {
                    LastTokensPerSecond = stats.TokensPerSecond;
                    Changed?.Invoke();
                }
                yield return delta;
            }
        }
        finally
        {
            LastActivity = DateTimeOffset.Now;
            Interlocked.Decrement(ref _busy);
        }
    }

    /// <summary>
    /// Measures the loaded model: reads a fixed prompt and writes <paramref name="tokens"/> tokens,
    /// returning the engine's own timings.
    /// </summary>
    public async Task<BenchmarkTimings> BenchmarkAsync(int tokens = 160, CancellationToken ct = default)
    {
        var api = Api();
        Interlocked.Increment(ref _busy);
        try
        {
            return await api.BenchmarkAsync(tokens, ct).ConfigureAwait(false);
        }
        finally
        {
            LastActivity = DateTimeOffset.Now;
            Interlocked.Decrement(ref _busy);
        }
    }

    private async Task UnloadIfIdleAsync()
    {
        if (IdleUnloadAfter is not { } limit || State != EngineState.Ready || IsBusy) return;
        if (DateTimeOffset.Now - LastActivity < limit) return;
        await UnloadAsync().ConfigureAwait(false);
        UnloadedForIdle?.Invoke();
    }

    /// <summary>Raised after the model was unloaded because it sat unused.</summary>
    public event Action? UnloadedForIdle;

    public void Dispose()
    {
        _idleTimer.Dispose();
        _loadCts?.Cancel();
        StopProcess();
        _job.Dispose();
    }

    private LlamaApi Api() => _api is { } api && IsReady ? api : throw new EngineException("No model is loaded.");

    private async Task WaitUntilReadyAsync(Process process, LlamaApi api, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) throw new EngineException(ExitMessage("The model engine stopped while loading the model."));
            if (await api.HealthAsync(ct).ConfigureAwait(false) == true) return;
            if (clock.Elapsed > LoadTimeout) throw new EngineException("The model took more than ten minutes to load, so DOG AI gave up.");
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    private void OnExited(Process process)
    {
        // Only a surprise exit of the current, ready engine matters; planned stops clear _process first.
        if (!ReferenceEquals(_process, process) || State != EngineState.Ready) return;
        _api = null;
        _process = null;
        Error = ExitMessage("The model engine stopped unexpectedly.");
        Set(EngineState.Failed);
        process.Dispose();
    }

    private string ExitMessage(string lead)
    {
        string[] lines;
        lock (_recentOutput) lines = _recentOutput.ToArray();
        var hint = lines.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("failed", StringComparison.OrdinalIgnoreCase));
        return hint is null ? lead : $"{lead} It said: {hint.Trim()}";
    }

    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_recentOutput)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > 60) _recentOutput.Dequeue();
        }
        Output?.Invoke(line);
    }

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        _api = null;
        ContextLength = 0;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        process.Dispose();
    }

    private void Set(EngineState state)
    {
        State = state;
        Changed?.Invoke();
    }

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
