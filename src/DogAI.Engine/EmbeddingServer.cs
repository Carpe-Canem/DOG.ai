using System.Diagnostics;
using DogAI.Core.Documents;
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Core.Settings;

namespace DogAI.Engine;

/// <summary>
/// The document search model: a second, small llama-server started with --embedding on its own port, next to whatever
/// chat model is loaded. It starts the first time something needs a vector and stops after two idle minutes
/// (<see cref="IdleStop"/>), or at once when the graphics card needs the room (it is a <see cref="ICardGuest"/>), but
/// never while it is being used: a request in progress, or documents being indexed (<see cref="KeepWhile"/>).
/// While a video is being made it never comes onto the card (M21): it asks at the <see cref="CardDoor"/> before it
/// starts, and with the door closed it starts on the processor (<see cref="OnProcessor"/>), slower but at once; the
/// first request after the video starts it again on the card.
/// The model is the one chosen as "Default for Document search" in the library; without a choice, the first embedding
/// model found in the model folders.
/// </summary>
public sealed class EmbeddingServer : IEmbedder, ICardGuest, IDisposable
{
    /// <summary>Key in <see cref="AppSettings.DefaultModels"/> for the document search model.</summary>
    public const string UseCase = "embedding";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(3);

    /// <summary>How often the idle time is looked at; the model stops at most this long after <see cref="IdleStop"/>.</summary>
    private static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ChoiceCache = TimeSpan.FromSeconds(20);

    private readonly LoopbackHttp _http;
    private readonly LlamaServerLocator _locator;
    private readonly ISettingsStore _settings;
    private readonly IModelRecordStore _records;
    private readonly CardDoor? _door;
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
    private int _answering;
    private int _leaving;
    private volatile bool _onProcessor;

    /// <param name="door">Asked before the model starts on the graphics card; null where nothing shares the card (tests).</param>
    public EmbeddingServer(LoopbackHttp http, LlamaServerLocator locator, ISettingsStore settings, IModelRecordStore records, CardDoor? door = null)
    {
        _door = door;
        if (door is not null) door.Changed += () => Changed?.Invoke();
        _http = http;
        _locator = locator;
        _settings = settings;
        _records = records;
        _idleTimer = new Timer(_ => StopIfIdle(), null, IdleCheck, IdleCheck);
    }

    /// <summary>
    /// How long the model may sit unused, counted from the end of its last request, before it is stopped. Two minutes,
    /// like the picture model: it holds graphics memory the chat model and pictures can use, and starting it again
    /// takes a couple of seconds.
    /// </summary>
    public TimeSpan IdleStop { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Set by the app: true while something will need the model again in a moment (documents are being indexed, one
    /// batch of passages after another). The model is not stopped while it says so.
    /// </summary>
    public Func<bool>? KeepWhile { get; set; }

    /// <summary>True while the search model is running.</summary>
    public bool IsRunning => _api is not null && _process is { HasExited: false };

    /// <summary>Process id of the running program, for measuring its graphics memory.</summary>
    public int? ProcessId
    {
        get
        {
            try { return _process is { HasExited: false } p ? p.Id : null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    /// <summary>True while a request is being answered or documents are being indexed: it must not be stopped.</summary>
    public bool IsBusy => Volatile.Read(ref _busy) > 0 || KeepWhile?.Invoke() == true;

    string ICardGuest.Name => "the document search model";

    /// <summary>In graphics memory, or on its way in. One that runs on the processor holds none of it.</summary>
    bool ICardGuest.HoldsMemory => _process is { } p && Alive(p) && !_onProcessor;

    /// <summary>
    /// True while the model runs on the processor, because a video was being made when it was needed. It is slower
    /// there. The first request after the video starts it again on the graphics card.
    /// </summary>
    public bool OnProcessor => _onProcessor && _process is { } p && Alive(p);

    /// <summary>Running where it should be: on the card, or on the processor while a video is still being made.</summary>
    private bool InPlace => !_onProcessor || _door is { IsClosed: true };

    private static bool Alive(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

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
                    Interlocked.Increment(ref _answering);
                    IReadOnlyList<float[]> result;
                    try
                    {
                        result = await api.EmbedAsync(texts, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _answering);
                    }
                    _lastUse = DateTime.UtcNow;
                    return result;
                }
                catch (HttpRequestException) when (attempt == 0)
                {
                    // The process died between requests; start it again once. Only the program this request asked:
                    // another request may have started the model again meanwhile (after it left the card for a
                    // video), and that one is answering.
                    await StopAsync(api).ConfigureAwait(false);
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
                   ?? throw new EmbedderUnavailableException("There is no document search model yet. Get one in " + DogAI.Core.Models.ModelsPlaces.RecommendedName + ", under Document search.");
        // Not while the model is leaving the card for a video: a request that slipped in here would keep the program
        // answering, and the video waiting. It queues behind the leaving instead and then starts on the processor.
        if (_api is { } ready && _process is { HasExited: false } && _runningPath == path && InPlace && Volatile.Read(ref _leaving) == 0) return ready;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_api is { } api && _process is { HasExited: false } && _runningPath == path && InPlace) return api;
            // Moving between the card and the processor (or to another model): what is being answered by the
            // program that is to go is waited for, not cut off.
            while (_process is not null && Volatile.Read(ref _answering) > 0) await Task.Delay(20, ct).ConfigureAwait(false);
            StopProcess();
            // While a video is being made nothing new comes onto the card: the door answers null and the model starts
            // on the processor. Otherwise the entry is kept until the model is in, so a video asked for meanwhile
            // measures the card with it there.
            using var entry = _door?.Enter();
            var onProcessor = _door is not null && entry is null;
            _onProcessor = onProcessor;
            IsStarting = true;
            Changed?.Invoke();
            var exe = _locator.Find() ?? throw new EmbedderUnavailableException(
                "The model engine (llama-server) was not found on this PC, so documents cannot be searched. Install it in " + DogAI.Core.Models.ModelsPlaces.EnginesName + ".");

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
            foreach (var arg in Arguments(path, port, onProcessor))
                start.ArgumentList.Add(arg);
            EngineEnvironment.Clean(start);
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

    /// <summary>
    /// The program's start arguments. Public for tests, which pin both lists: on the card it is started exactly as
    /// before M21. For the processor it gets no layers on the card and, with <c>--device none</c>, no card at all:
    /// with <c>--gpu-layers 0</c> alone the program still took 454 MiB of the card (the installed CUDA engine; 128 MiB
    /// with the Vulkan one), and with both it took none on either.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string modelPath, int port, bool onProcessor)
    {
        // Each of the four slots gets 2,048 tokens, and a whole passage must fit in one batch (embedding models
        // read their input at once). Passages are at most 1,500 characters, so they always fit.
        var args = new List<string>
        {
            "--model", modelPath, "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--embedding",
            "--ctx-size", "8192", "--parallel", "4", "--batch-size", "2048", "--ubatch-size", "2048",
            "--gpu-layers", onProcessor ? "0" : "999", "--no-webui",
        };
        if (onProcessor) args.AddRange(["--device", "none"]);
        return args;
    }

    public Task StopAsync() => StopAsync(null);

    /// <param name="only">Stop the program only if it is still the one this client talks to; null stops whatever runs.</param>
    private async Task StopAsync(LlamaApi? only)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (only is not null && !ReferenceEquals(only, _api)) return;
            StopProcess();
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Gives the graphics memory back because the card needs the room, or the owner unloads everything. False, with the
    /// model left running, while it is being used. The next search or document starts it again.
    /// </summary>
    public async Task<bool> LetGoAsync()
    {
        if (IsBusy) return false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Looked at again with the start lock held: a request counts as busy before it takes the lock.
            if (IsBusy) return false;
            StopProcess();
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Leaves the card for a video, also while documents are being indexed: the batch being answered right now is
    /// waited for (never cut off), then the program is stopped. The next batch finds the door closed and starts the
    /// model on the processor; a batch that reaches the program just as it stops starts it again once, as after any
    /// program that died between two requests. True when the model is off the card.
    /// </summary>
    public async Task<bool> StepOffAsync(CancellationToken ct = default)
    {
        // From now no new request reaches the running program (they wait for the lock below), so what is being
        // answered runs out: with batches following each other closely the wait could otherwise go on for many.
        Interlocked.Increment(ref _leaving);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_process is null || _onProcessor) return true;
                while (Volatile.Read(ref _answering) > 0) await Task.Delay(20, ct).ConfigureAwait(false);
                StopProcess();
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _leaving);
        }
        Changed?.Invoke();
        return true;
    }

    private void StopIfIdle()
    {
        if (_api is null || IsBusy || DateTime.UtcNow - _lastUse < IdleStop) return;
        _ = StopIfStillIdleAsync();
    }

    /// <summary>Checks again while holding the start lock, so a request that began after the timer looked is not cut off.</summary>
    private async Task StopIfStillIdleAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_api is null || IsBusy || DateTime.UtcNow - _lastUse < IdleStop) return;
            StopProcess();
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        _api = null;
        _runningPath = null;
        _onProcessor = false;
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
        return EngineFailure.Explain(lead, lines);
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        StopProcess();
        _job.Dispose();
    }
}
