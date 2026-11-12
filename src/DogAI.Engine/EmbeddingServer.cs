using System.Diagnostics;
using DogAI.Core.Documents;
using DogAI.Core.Models;
using DogAI.Core.Settings;

namespace DogAI.Engine;

/// <summary>
/// The document search model: a second, small llama-server started with --embedding on its own port, next to whatever
/// chat model is loaded. It starts the first time something needs a vector and stops after ten idle minutes.
/// The model is the one chosen as "Default for Document search" in the library; without a choice, the first embedding
/// model found in the model folders.
/// </summary>
public sealed class EmbeddingServer : IEmbedder, IDisposable
{
    /// <summary>Key in <see cref="AppSettings.DefaultModels"/> for the document search model.</summary>
    public const string UseCase = "embedding";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan IdleStop = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ChoiceCache = TimeSpan.FromSeconds(20);

    private readonly LoopbackHttp _http;
    private readonly LlamaServerLocator _locator;
    private readonly ISettingsStore _settings;
    private readonly IModelRecordStore _records;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ProcessJob _job = new();
    private readonly Queue<string> _recentOutput = new();
    private readonly Timer _idleTimer;
    private Process? _process;
    private LlamaApi? _api;
    private string? _runningPath;
    private (string? Path, DateTime At)? _choice;
    private DateTime _lastUse = DateTime.UtcNow;
    private int _busy;

    public EmbeddingServer(LoopbackHttp http, LlamaServerLocator locator, ISettingsStore settings, IModelRecordStore records)
    {
        _http = http;
        _locator = locator;
        _settings = settings;
        _records = records;
        _idleTimer = new Timer(_ => StopIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>True while the search model is running.</summary>
    public bool IsRunning => _api is not null && _process is { HasExited: false };

    public bool IsStarting { get; private set; }

    /// <summary>Why the search model last failed to start, in plain words. Cleared when it starts.</summary>
    public string? Error { get; private set; }

    /// <summary>Raised when the server starts, stops or fails. May come from a background thread.</summary>
    public event Action? Changed;

    /// <summary>Lines the engine prints, for the app log.</summary>
    public event Action<string>? Output;

    /// <summary>Forget the remembered model choice, e.g. after the user picks another one. A running model is stopped if it changed.</summary>
    public void ChoiceChanged()
    {
        _choice = null;
        Changed?.Invoke();
    }

    /// <summary>The model file document search uses, or null when there is none.</summary>
    public async Task<string?> ModelPathAsync(CancellationToken ct = default)
    {
        if (_choice is { } c && DateTime.UtcNow - c.At < ChoiceCache) return c.Path;
        var path = await FindModelAsync(ct).ConfigureAwait(false);
        _choice = (path, DateTime.UtcNow);
        return path;
    }

    public async Task<string?> ModelIdAsync(CancellationToken ct = default) =>
        await ModelPathAsync(ct).ConfigureAwait(false) is { } path ? Path.GetFileNameWithoutExtension(path) : null;

    private async Task<string?> FindModelAsync(CancellationToken ct)
    {
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        if (settings.DefaultModels.TryGetValue(UseCase, out var chosen) && File.Exists(chosen)) return chosen;
        var folders = new[] { settings.ModelsDirectory }.Concat(settings.ExtraModelFolders).ToList();
        var records = await _records.AllAsync(ct).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var files = ModelLibrary.ScanAll(folders);
            // Downloaded as a search model first, then anything whose header says it is one; smallest first.
            return files.Where(f => records.Any(r => r.UseCase == UseCase && string.Equals(r.Path, Path.GetFullPath(f.Path), StringComparison.OrdinalIgnoreCase)))
                .Concat(files.OrderBy(f => f.SizeBytes).Where(f => GgufReader.TryRead(f.Path)?.IsEmbedding(f.Path) == true))
                .Select(f => f.Path)
                .FirstOrDefault();
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];
        Interlocked.Increment(ref _busy);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var api = await EnsureStartedAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await api.EmbedAsync(texts, ct).ConfigureAwait(false);
                    _lastUse = DateTime.UtcNow;
                    return result;
                }
                catch (HttpRequestException) when (attempt == 0)
                {
                    // The process died between requests; start it again once.
                    await StopAsync().ConfigureAwait(false);
                }
                catch (HttpRequestException e)
                {
                    throw new EmbedderUnavailableException("The document search model stopped answering: " + e.Message);
                }
                catch (EngineException e)
                {
                    throw new EmbedderUnavailableException("The document search model refused the request. " + e.Message);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
        }
    }

    private async Task<LlamaApi> EnsureStartedAsync(CancellationToken ct)
    {
        var path = await ModelPathAsync(ct).ConfigureAwait(false)
                   ?? throw new EmbedderUnavailableException("There is no document search model yet. Get one in Models → Recommended → Document search.");
        if (_api is { } ready && _process is { HasExited: false } && _runningPath == path) return ready;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_api is { } api && _process is { HasExited: false } && _runningPath == path) return api;
            StopProcess();
            IsStarting = true;
            Changed?.Invoke();
            var exe = _locator.Find() ?? throw new EmbedderUnavailableException(
                "The model engine (llama-server) was not found on this PC, so documents cannot be searched. Install it in Models → Engine.");

            var port = EngineManager.FreePort();
            var key = Guid.NewGuid().ToString("N");
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            // Each of the four slots gets 2,048 tokens, and a whole passage must fit in one batch (embedding models
            // read their input at once). Passages are at most 1,500 characters, so they always fit.
            foreach (var arg in new[]
            {
                "--model", path, "--host", "127.0.0.1", "--port", port.ToString(), "--embedding",
                "--ctx-size", "8192", "--parallel", "4", "--batch-size", "2048", "--ubatch-size", "2048",
                "--gpu-layers", "999", "--no-webui",
            })
                start.ArgumentList.Add(arg);
            start.Environment["LLAMA_API_KEY"] = key;

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Remember(e.Data);
            process.ErrorDataReceived += (_, e) => Remember(e.Data);
            lock (_recentOutput) _recentOutput.Clear();
            process.Start();
            _job.Add(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            var api2 = new LlamaApi(_http.Client, port, key);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited) throw new EmbedderUnavailableException(ExitMessage("The document search model stopped while starting."));
                if (await api2.HealthAsync(ct).ConfigureAwait(false) == true) break;
                if (clock.Elapsed > StartTimeout) throw new EmbedderUnavailableException("The document search model took too long to start.");
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            _api = api2;
            _runningPath = path;
            _lastUse = DateTime.UtcNow;
            Error = null;
            return api2;
        }
        catch (EmbedderUnavailableException e)
        {
            StopProcess();
            Error = e.Message;
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            StopProcess();
            Error = "The document search model could not start: " + e.Message;
            throw new EmbedderUnavailableException(Error);
        }
        finally
        {
            IsStarting = false;
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopProcess();
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    private void StopIfIdle()
    {
        if (_api is null || Volatile.Read(ref _busy) > 0 || DateTime.UtcNow - _lastUse < IdleStop) return;
        _ = StopAsync();
    }

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        _api = null;
        _runningPath = null;
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

    private string ExitMessage(string lead)
    {
        string[] lines;
        lock (_recentOutput) lines = _recentOutput.ToArray();
        var hint = lines.LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("failed", StringComparison.OrdinalIgnoreCase));
        return hint is null ? lead : $"{lead} It said: {hint.Trim()}";
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        StopProcess();
        _job.Dispose();
    }
}
