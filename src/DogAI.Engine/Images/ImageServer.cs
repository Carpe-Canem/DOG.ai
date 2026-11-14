using System.Diagnostics;
using DogAI.Core;
using DogAI.Core.Images;

namespace DogAI.Engine.Images;

/// <summary>
/// The picture engine: stable-diffusion.cpp's sd-server, run like the document search model (<see cref="EmbeddingServer"/>).
/// It starts when a checkpoint is loaded or a picture is asked for, listens only on 127.0.0.1 on a random port, dies
/// with DOG.ai (<see cref="ProcessJob"/>), and stops after a few idle minutes; the chosen checkpoint stays chosen, so the
/// next picture starts it again.
/// <para>
/// A checkpoint is loaded by starting the program with it (<c>-m</c>). sd-server's API can also load models, but only
/// files under its own folder layout chosen by a "recipe", and v-prediction and a separate VAE are start options anyway.
/// </para>
/// <para>
/// Privacy: never started with verbose logging (at its normal level it prints no prompt), never told to save files, its
/// built-in web page is replaced by a blank one, finished jobs are dropped from its memory after a minute, and pictures
/// carry no prompt inside them. Its output lines reach the log only through <see cref="Logged"/>, which a
/// <see cref="PictureLogFilter"/> guards; <see cref="Output"/> is everything it prints and must never be logged.
/// </para>
/// </summary>
public sealed class ImageServer : IImageEngine, IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly LoopbackHttp _http;
    private readonly SdServerLocator _locator;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _drawing = new(1, 1);
    private readonly ProcessJob _job = new();
    private readonly Queue<string> _recentOutput = new();
    private readonly PictureLogFilter _filter = new();
    private readonly Timer _idleTimer;
    private Process? _process;
    private SdApi? _api;
    private ImageModel? _runningModel;
    private CancellationTokenSource? _drawCancel;
    private DateTime _lastUse = DateTime.UtcNow;
    private int _busy;

    public ImageServer(LoopbackHttp http, SdServerLocator locator, AppPaths paths)
    {
        _http = http;
        _locator = locator;
        _paths = paths;
        _idleTimer = new Timer(_ => StopIfIdle(), null, IdleCheck, IdleCheck);
    }

    /// <summary>How often the idle time is looked at; the engine stops at most this long after <see cref="IdleStop"/>.</summary>
    private static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the engine may sit unused, counted from the end of the last picture, before it is stopped. Two minutes:
    /// long enough to look at a picture, change the prompt and draw again without paying for a reload, and short because
    /// keeping it costs 8 GB of graphics memory (a third of a 24 GB card) while getting it back costs about 4 seconds.
    /// The chat model does not wait for this: the card coordinator lets the picture model go as soon as the chat model
    /// needs the room.
    /// </summary>
    public TimeSpan IdleStop { get; set; } = TimeSpan.FromMinutes(2);

    public ImageModel? Model { get; private set; }

    public bool IsBusy => Volatile.Read(ref _busy) > 0;

    /// <summary>True while the program is running (with or without its model in graphics memory yet).</summary>
    public bool IsRunning => _api is not null && _process is { HasExited: false };

    public bool IsStarting { get; private set; }

    /// <summary>Process id of the running program, for measuring its graphics memory.</summary>
    public int? ProcessId
    {
        get
        {
            try { return _process is { HasExited: false } p ? p.Id : null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    /// <summary>The port the running program listens on (tests only).</summary>
    internal int? Port { get; private set; }

    public string? Error { get; private set; }

    public event Action? Changed;

    /// <summary>Every line the engine prints, unfiltered. For tests; never write these to a log.</summary>
    public event Action<string>? Output;

    /// <summary>
    /// The lines that are safe to log: the engine's status lines, with anything that could carry a prompt held back
    /// (see <see cref="PictureLogFilter"/>). After each picture a line says how many were held back, if any.
    /// </summary>
    public event Action<string>? Logged;

    public async Task LoadAsync(ImageModel model, CancellationToken ct = default)
    {
        Check(model);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Model = model;
            await EnsureStartedLockedAsync(model, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async Task UnloadAsync(CancellationToken ct = default)
    {
        // A picture in progress is stopped first; the server refuses to unload while it has work.
        try { _drawCancel?.Cancel(); }
        catch (ObjectDisposedException) { }
        await _drawing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Model = null;
                if (_api is { } api && _process is { HasExited: false })
                {
                    // Give the memory back the polite way, then end the program: loading another checkpoint means
                    // starting it again anyway, and an empty server still holds a little graphics memory.
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await api.UnloadAsync(limit.Token).ConfigureAwait(false); }
                    catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
                }
                StopProcess();
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            _drawing.Release();
        }
        Changed?.Invoke();
    }

    public async Task<ImageResult> DrawAsync(ImageRequest request, IProgress<ImageProgress>? progress = null, CancellationToken ct = default)
    {
        if (request.Problems() is { Count: > 0 } problems) throw new ImageEngineException(string.Join(" ", problems));

        await _drawing.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _busy);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _drawCancel = cancel;
        Changed?.Invoke();
        try
        {
            SdApi api;
            await _gate.WaitAsync(cancel.Token).ConfigureAwait(false);
            try
            {
                var model = Model ?? throw new ImageEngineException("No picture model is loaded. Choose a checkpoint first.");
                api = await EnsureStartedLockedAsync(model, cancel.Token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            var seed = request.Seed >= 0 ? request.Seed : Random.Shared.NextInt64(0, uint.MaxValue);
            // Before the request leaves: from here on no line that shares a word with its prompts reaches the log.
            _filter.Sending(request);
            try
            {
                return await api.DrawAsync(request, seed, progress, cancel.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                // The program died (or was stopped) mid-picture. Its output is not quoted: it could follow a prompt.
                await StopAsync().ConfigureAwait(false);
                throw new ImageEngineException("The picture engine stopped while drawing. Try again; it will be started again.");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && cancel.IsCancellationRequested)
        {
            // Cancelled by UnloadAsync rather than by the caller.
            throw new OperationCanceledException("The picture was cancelled because the picture model was unloaded.", null, cancel.Token);
        }
        finally
        {
            _drawCancel = null;
            _lastUse = DateTime.UtcNow;
            Interlocked.Decrement(ref _busy);
            _drawing.Release();
            if (_filter.TakeHeldBack() is > 0 and var held)
                Logged?.Invoke($"({held} of the engine's status lines were not logged because they share a word with a prompt)");
            Changed?.Invoke();
        }
    }

    /// <summary>What the running engine offers (samplers, schedulers, LoRAs found). Starts it if needed.</summary>
    public async Task<SdCapabilities> CapabilitiesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        SdApi api;
        try
        {
            var model = Model ?? throw new ImageEngineException("No picture model is loaded. Choose a checkpoint first.");
            api = await EnsureStartedLockedAsync(model, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        _lastUse = DateTime.UtcNow;
        return await api.CapabilitiesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Gives the graphics memory back by ending the program; the checkpoint stays chosen (see <see cref="StopAsync"/>).</summary>
    public Task ReleaseAsync(CancellationToken ct = default) => StopAsync();

    /// <summary>Ends the program. The chosen checkpoint stays chosen; the next picture starts the program again.</summary>
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

    private static void Check(ImageModel model)
    {
        if (!File.Exists(model.CheckpointPath))
            throw new ImageEngineException($"The checkpoint file was not found: {model.CheckpointPath}");
        if (!model.CheckpointPath.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            throw new ImageEngineException("Only .safetensors checkpoints can be used. Older .ckpt files can carry program code, so DOG.ai does not open them.");
        if (model.VaePath is { } vae && !File.Exists(vae))
            throw new ImageEngineException($"The VAE file was not found: {vae}");
        if (model.VaePath is { } v && !v.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            throw new ImageEngineException("Only .safetensors VAE files can be used.");
        if (model.LoraFolder is { } folder && !Directory.Exists(folder))
            throw new ImageEngineException($"The LoRA folder was not found: {folder}");
    }

    /// <summary>The program's start arguments for <paramref name="model"/>. Public for tests.</summary>
    public static IReadOnlyList<string> Arguments(ImageModel model, int port, string blankPage)
    {
        var args = new List<string>
        {
            "--listen-ip", "127.0.0.1", "--listen-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-m", model.CheckpointPath,
            // The built-in web page cannot be switched off; serving a blank page in its place leaves nothing to click.
            "--serve-html-path", blankPage,
            // Finished pictures stay in the engine's memory only briefly; DOG.ai collects them at once.
            "--completed-ttl-seconds", "60",
            "--disable-image-metadata",
        };
        if (model.LoraFolder is { } loras) args.AddRange(["--lora-model-dir", loras]);
        if (model.VaePath is { } vae) args.AddRange(["--vae", vae]);
        if (model.Prediction == ImagePrediction.V) args.AddRange(["--prediction", "v"]);
        return args;
    }

    private async Task<SdApi> EnsureStartedLockedAsync(ImageModel model, CancellationToken ct)
    {
        if (_api is { } ready && _process is { HasExited: false } && _runningModel == model) return ready;
        StopProcess();
        Check(model);
        IsStarting = true;
        Changed?.Invoke();
        try
        {
            var exe = _locator.Find() ?? throw new ImageEngineException(SdServerLocator.MissingMessage);
            var port = EngineManager.FreePort();
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            foreach (var arg in Arguments(model, port, BlankPage()))
                start.ArgumentList.Add(arg);
            EngineEnvironment.Clean(start);

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Remember(e.Data);
            process.ErrorDataReceived += (_, e) => Remember(e.Data);
            process.Exited += (_, _) => Changed?.Invoke();
            lock (_recentOutput) _recentOutput.Clear();
            process.Start();
            _job.Add(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            var api = new SdApi(_http.Client, port);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited) throw new ImageEngineException(ExitMessage("The picture engine stopped while starting."));
                if (await api.HealthAsync(ct).ConfigureAwait(false)) break;
                if (clock.Elapsed > StartTimeout) throw new ImageEngineException("The picture engine took too long to start.");
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            _api = api;
            Port = port;
            _runningModel = model;
            _lastUse = DateTime.UtcNow;
            Error = null;
            return api;
        }
        catch (ImageEngineException e)
        {
            StopProcess();
            Error = e.Message;
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            StopProcess();
            Error = "The picture engine could not start: " + e.Message;
            throw new ImageEngineException(Error);
        }
        catch
        {
            StopProcess();
            throw;
        }
        finally
        {
            IsStarting = false;
            Changed?.Invoke();
        }
    }

    /// <summary>A blank page for the engine to serve at its root in place of its own web page.</summary>
    private string BlankPage()
    {
        var folder = _paths.ImageEngineDirectory;
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "dogai-blank.html");
        const string Page = "<!doctype html><meta charset=\"utf-8\"><title>DOG.ai</title>";
        if (!File.Exists(file) || File.ReadAllText(file) != Page) File.WriteAllText(file, Page);
        return file;
    }

    private void StopIfIdle()
    {
        if (_api is null || IsBusy || DateTime.UtcNow - _lastUse < IdleStop) return;
        _ = StopIfStillIdleAsync();
    }

    /// <summary>
    /// Checks again while holding the start lock: a picture counts as busy before it takes the lock, so one that began
    /// after the timer looked is never cut off. If it takes the lock after this, it starts the program again.
    /// </summary>
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
        Port = null;
        _runningModel = null;
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
        if (_filter.Allows(line)) Logged?.Invoke(line);
    }

    /// <summary>
    /// Explains a failed start from the program's own lines. Only used while starting, before any prompt was sent, so
    /// nothing quoted can come from a picture request.
    /// </summary>
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
