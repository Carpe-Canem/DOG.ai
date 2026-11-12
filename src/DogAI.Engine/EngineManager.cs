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
public sealed class EngineManager(LoopbackHttp http, LlamaServerLocator locator) : IChatEngine, IDisposable
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

    /// <summary>What went wrong, in plain words, when <see cref="State"/> is Failed.</summary>
    public string? Error { get; private set; }

    public bool IsReady => State == EngineState.Ready;

    /// <summary>Raised on any state change. May come from a background thread.</summary>
    public event Action? Changed;

    /// <summary>Lines the engine prints, for the app log.</summary>
    public event Action<string>? Output;

    public string? FindExecutable() => locator.Find();

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
            ModelName = string.IsNullOrWhiteSpace(info?.Name) ? Path.GetFileNameWithoutExtension(modelPath) : info.Name;
            Error = null;
            Set(EngineState.Loading);

            if (!File.Exists(modelPath)) throw new EngineException("That model file no longer exists.");
            if (info is null) throw new EngineException("That file is not a model DOG AI can load (it is not a GGUF file).");
            var exe = locator.Find() ?? throw new EngineException(
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

            var api = new LlamaApi(http.Client, port, key);
            await WaitUntilReadyAsync(process, api, cts.Token).ConfigureAwait(false);
            ContextLength = await api.ContextLengthAsync(cts.Token).ConfigureAwait(false) ?? context;
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
        Api().StreamChatAsync(messages, sampler, ct);

    public void Dispose()
    {
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

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
