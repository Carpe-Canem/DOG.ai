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

    /// <summary>
    /// Set aside to make room for a picture: the model is out of graphics memory but remembered, and loads again by
    /// itself the next time it is asked for. Not the same as unloaded by the owner.
    /// </summary>
    Resting,
}

/// <param name="ContextLength">Context window to allocate, in tokens. Capped at what the model was trained for.</param>
/// <param name="GpuLayers">How many layers go on the graphics card; -1 lets the engine put on as many as fit.</param>
/// <param name="Projector">
/// The model's projector file, loaded beside it so it can see pictures (M17; <see cref="ModelProjector.Find"/>). Null
/// loads the model exactly as before: the engine never takes a projector by itself.
/// </param>
public sealed record EngineOptions(int ContextLength = 8192, int GpuLayers = -1, string? Projector = null);

/// <summary>
/// Owns the llama-server process: starts it for one model on a random port bound to 127.0.0.1,
/// waits until it is ready, and stops it on unload or exit. One model at a time.
/// </summary>
public sealed class EngineManager : IChatEngine, DogAI.Core.Images.IChatModelSlot, IDisposable
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _waking = new(1, 1);

    /// <summary>Guards "a request starts" against "the model is set aside", so a request is never cut off by a picture.</summary>
    private readonly object _use = new();
    private EngineOptions _options = new();
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

    /// <summary>
    /// True when the model can be given pictures: it was loaded with its projector and the engine says it sees. Stays
    /// true while the model is set aside for a picture (it comes back the same).
    /// </summary>
    public bool SeesPictures => _sees && State is EngineState.Ready or EngineState.Resting;
    private bool _sees;

    /// <summary>The projector file the model was loaded with, or null.</summary>
    public string? Projector { get; private set; }

    /// <summary>
    /// Set when the model has a projector but was loaded without it, because the engine stopped when given it (an
    /// engine too old for the switches, or a projector it cannot use): what to tell the owner. Null otherwise.
    /// </summary>
    public string? PictureInputProblem { get; private set; }

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

    /// <summary>
    /// True when the model can be asked: it is loaded, or it was set aside for a picture and comes back by itself with
    /// the next request. Use <see cref="IsLoaded"/> for things that should not bring it back (such as counting tokens
    /// while typing).
    /// </summary>
    public bool IsReady => State is EngineState.Ready or EngineState.Resting;

    /// <summary>True when the model is in graphics memory now.</summary>
    public bool IsLoaded => State == EngineState.Ready;

    /// <summary>True when the model was set aside for a picture (see <see cref="EngineState.Resting"/>).</summary>
    public bool IsResting => State == EngineState.Resting;

    /// <summary>
    /// Set by the graphics-card coordinator. Everything that loads a chat model takes a turn first
    /// (<see cref="TakeCardTurnAsync"/>), so a load never races a picture.
    /// </summary>
    public DogAI.Core.Images.CardTurn? CardTurn { get; set; }

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

    // The load under way: whether nobody asked for it (a page loading its default), and whether it is being set aside.
    private bool _loadAutomatic, _restInstead;
    private TaskCompletionSource? _loadEnded;

    /// <summary>
    /// Loads a model, replacing any model already loaded. Problems end up in <see cref="Error"/>, not as exceptions.
    /// Callers take a turn on the graphics card first (<see cref="TakeCardTurnAsync"/>) and keep it until this returns.
    /// </summary>
    /// <param name="automatic">True when nobody asked for this model yet: a page loading its mode's default when it opens.
    /// Such a load may be set aside before it finishes (<see cref="SetAsideLoadAsync"/>).</param>
    public async Task LoadAsync(string modelPath, EngineOptions options, bool automatic = false)
    {
        _loadCts?.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        var cts = new CancellationTokenSource();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_use)
        {
            _loadCts = cts;
            _loadEnded = ended;
            _loadAutomatic = automatic;
            _restInstead = false;
        }
        var planned = 0;
        try
        {
            StopProcess();
            _options = options;
            var info = GgufReader.TryRead(modelPath);
            ModelPath = modelPath;
            ModelName = info?.DisplayName(modelPath) ?? Path.GetFileNameWithoutExtension(modelPath);
            Error = null;
            Projector = PictureInputProblem = null;
            _sees = false;
            Set(EngineState.Loading);

            if (!File.Exists(modelPath)) throw new EngineException("That model file no longer exists.");
            if (info is null) throw new EngineException("That file is not a model DOG.ai can load (it is not a GGUF file).");
            var exe = _locator.Find() ?? throw new EngineException(_locator.MissingMessage);

            var context = Math.Max(512, options.ContextLength);
            if (info.ContextLength is > 0 and var trained) context = Math.Min(context, trained);
            planned = context;

            var projector = options.Projector is { } file && File.Exists(file) ? file : null;
            LlamaApi api;
            try
            {
                api = await StartAsync(exe, modelPath, context, options.GpuLayers, projector, cts.Token).ConfigureAwait(false);
            }
            catch (EngineException) when (projector is not null && !cts.IsCancellationRequested && _process is null or { HasExited: true })
            {
                // The engine stopped with the projector (one too old for the switches stops at once; a projector it
                // cannot use stops it after the model is in). The model is loaded again as before M17, and says so.
                StopProcess();
                PictureInputProblem = PictureInputStopped(ModelName ?? "The model", Path.GetFileName(projector));
                Output?.Invoke("DOG.ai: " + PictureInputProblem); // for the log: names the two files, nothing else
                projector = null;
                api = await StartAsync(exe, modelPath, context, options.GpuLayers, null, cts.Token).ConfigureAwait(false);
            }
            var props = await api.PropsAsync(cts.Token).ConfigureAwait(false);
            ContextLength = props.ContextLength ?? context;
            Projector = projector;
            _sees = projector is not null && props.Vision;
            GpuLayers = options.GpuLayers;
            LastTokensPerSecond = 0;
            LastActivity = DateTimeOffset.Now;
            _api = api;
            Set(EngineState.Ready);
        }
        catch (OperationCanceledException)
        {
            StopProcess();
            bool rest;
            lock (_use) rest = _restInstead && ReferenceEquals(_loadEnded, ended);
            if (rest && ModelPath is not null)
            {
                // Set aside before it finished: remembered with its settings, and loaded when it is next asked for.
                ContextLength = planned > 0 ? planned : Math.Max(512, options.ContextLength);
                GpuLayers = options.GpuLayers;
                // It never got as far as saying whether it sees. It comes back with its projector when it has one, so
                // until then it counts as seeing: otherwise the chat box and Write it for me would say "cannot see
                // pictures" of a model that can (found by the M17 review). The engine's own word replaces this at the load.
                _sees = options.Projector is { } beside && File.Exists(beside);
                Set(EngineState.Resting);
            }
            else
            {
                ModelPath = ModelName = null;
                Set(EngineState.Idle);
            }
        }
        catch (Exception e)
        {
            StopProcess();
            Error = e is EngineException ? e.Message : "The model could not be loaded: " + e.Message;
            // Nothing was loaded, so "loaded without picture input" would be untrue: the error above is the whole story.
            PictureInputProblem = null;
            Set(EngineState.Failed);
        }
        finally
        {
            lock (_use)
            {
                if (_loadCts == cts) _loadCts = null;
                if (ReferenceEquals(_loadEnded, ended))
                {
                    _loadEnded = null;
                    _loadAutomatic = _restInstead = false;
                }
            }
            ended.TrySetResult();
            _gate.Release();
        }
    }

    /// <summary>What <see cref="PictureInputProblem"/> says when the engine stopped with the projector.</summary>
    internal static string PictureInputStopped(string model, string projector) =>
        $"{model} was loaded without picture input: the model engine stopped when it was given {projector}. " +
        $"If the engine is an older one, update it in {DogAI.Core.Models.ModelsPlaces.EnginesName}.";

    /// <summary>The switches a model is started with. With a projector, two more; without, exactly those of before M17.</summary>
    internal static IReadOnlyList<string> Arguments(string modelPath, int port, int context, int gpuLayers, string? projector)
    {
        var arguments = new List<string>
        {
            "--model", modelPath, "--host", "127.0.0.1", "--port", port.ToString(),
            "--ctx-size", context.ToString(), "--gpu-layers", gpuLayers < 0 ? "999" : gpuLayers.ToString(),
            "--parallel", "1", "--no-webui", "--jinja",
        };
        // The cap keeps a picture at 1,026 tokens whatever its size. Never --media-path, --mmproj-url or --mmproj-auto:
        // pictures arrive only as bytes in a request, and nothing is fetched.
        if (projector is not null)
            arguments.AddRange(["--mmproj", projector, "--image-max-tokens", AttachedPictures.EngineMaxTokens.ToString()]);
        return arguments;
    }

    /// <summary>Starts the engine for a model and waits until it answers.</summary>
    private async Task<LlamaApi> StartAsync(string exe, string modelPath, int context, int gpuLayers, string? projector, CancellationToken ct)
    {
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
        foreach (var arg in Arguments(modelPath, port, context, gpuLayers, projector)) start.ArgumentList.Add(arg);
        // The key keeps other programs on this PC from using the engine; it goes in the environment, not the command line.
        EngineEnvironment.Clean(start);
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
        await WaitUntilReadyAsync(process, api, ct).ConfigureAwait(false);
        return api;
    }

    /// <summary>
    /// Sets a model aside before it has finished loading, when nobody asked for it yet (a page loading its default, see
    /// <c>LoadAsync(…, automatic: true)</c>): the load stops and the model is remembered as set aside
    /// (<see cref="EngineState.Resting"/>), so the next request loads it. A picture uses this instead of waiting half a
    /// minute for a model it would set aside at once. A load the owner started, or one bringing the model back for a
    /// request, is never stopped here. True when the model is now set aside; returns once the load has stopped.
    /// </summary>
    public async Task<bool> SetAsideLoadAsync()
    {
        Task ended;
        lock (_use)
        {
            if (State != EngineState.Loading || !_loadAutomatic || _loadCts is not { } cts || _loadEnded is not { } loadEnded) return false;
            _restInstead = true;
            cts.Cancel();
            ended = loadEnded.Task;
        }
        await ended.ConfigureAwait(false);
        return State == EngineState.Resting;
    }

    /// <summary>
    /// Stops a load nobody asked for (a page loading its default) because the owner asked for a model: the load ends and
    /// its model is forgotten, not set aside, since another model is about to take its place. A load the owner started,
    /// or one bringing a model back for a request, is never stopped here. True when such a load was stopped; returns
    /// once it has ended. Without this, a wanted load would wait behind the page's for its turn on the graphics card.
    /// </summary>
    public async Task<bool> DropAutomaticLoadAsync()
    {
        Task ended;
        lock (_use)
        {
            if (State != EngineState.Loading || !_loadAutomatic || _loadCts is not { } cts || _loadEnded is not { } loadEnded) return false;
            _restInstead = false;
            cts.Cancel();
            ended = loadEnded.Task;
        }
        await ended.ConfigureAwait(false);
        return true;
    }

    /// <summary>Stops the engine and frees the model's memory. Also cancels a load in progress.</summary>
    public async Task UnloadAsync()
    {
        lock (_use) _restInstead = false; // the owner's unload wins over setting a load aside
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

    /// <summary>
    /// Sets the loaded model aside for a picture: its program ends and the graphics memory is given back, but the model,
    /// its context size and its settings are remembered, and the next request loads it again (<see cref="EngineState.Resting"/>).
    /// False, with nothing changed, when no model is loaded or a request is in progress.
    /// </summary>
    public async Task<bool> RestAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_use)
            {
                if (State != EngineState.Ready || _busy > 0) return false;
                _api = null;
                State = EngineState.Resting;
            }
            var context = ContextLength;
            StopProcess();
            // Still shown, and still what prompts are measured against: the model comes back with the same settings.
            ContextLength = context;
            Changed?.Invoke();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Takes a turn on the graphics card before a chat model is loaded: waits for a picture being drawn and makes room
    /// (see <see cref="DogAI.Core.Images.CardCoordinator"/>). Dispose the result when the load has ended. Without a
    /// coordinator there is nothing to wait for.
    /// </summary>
    /// <param name="waking">True only when the model that was set aside is coming back.</param>
    public Task<IDisposable> TakeCardTurnAsync(bool waking, CancellationToken ct = default) =>
        CardTurn is { } take ? take(waking, ct) : Task.FromResult<IDisposable>(NoTurn.Instance);

    private sealed class NoTurn : IDisposable
    {
        public static readonly NoTurn Instance = new();
        public void Dispose() { }
    }

    public async Task<int> CountTokensAsync(string text, CancellationToken ct = default)
    {
        var api = await BeginUseAsync(ct).ConfigureAwait(false);
        try
        {
            return await api.TokenizeAsync(text, ct).ConfigureAwait(false);
        }
        finally
        {
            EndUse();
        }
    }

    public Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct = default) =>
        RenderPromptAsync(messages, thinking: true, ct);

    public async Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, bool thinking, CancellationToken ct = default)
    {
        var api = await BeginUseAsync(ct).ConfigureAwait(false);
        try
        {
            return await api.ApplyTemplateAsync(messages, ct, thinking).ConfigureAwait(false);
        }
        finally
        {
            EndUse();
        }
    }

    public IAsyncEnumerable<ChatDelta> StreamChatAsync(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, CancellationToken ct = default) =>
        TrackAsync(api => api.StreamChatAsync(messages, sampler, ct), ct);

    public IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, SamplerPreset sampler, CancellationToken ct = default) =>
        TrackAsync(api => api.StreamCompletionAsync(prompt, [], sampler, ct), ct);

    public IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, IReadOnlyList<PromptPicture> pictures, SamplerPreset sampler, CancellationToken ct = default) =>
        TrackAsync(api => api.StreamCompletionAsync(prompt, pictures, sampler, ct), ct);

    /// <summary>
    /// What a picture costs, by the engine's own count. Asked only of a model that is in memory and sees: counting never
    /// brings a model back that was set aside. Null when it cannot be asked, or the engine cannot count.
    /// </summary>
    public async Task<int?> CountPictureTokensAsync(PromptPicture picture, CancellationToken ct = default)
    {
        LlamaApi api;
        lock (_use)
        {
            if (State != EngineState.Ready || !_sees || _api is not { } ready) return null;
            api = ready;
            _busy++;
        }
        try
        {
            return await api.CountPictureTokensAsync(picture.Jpeg, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or EngineException)
        {
            return null;
        }
        finally
        {
            EndUse();
        }
    }

    /// <summary>
    /// Answer now: asks the running engine to end the thinking of the reply named <paramref name="replyId"/>. It takes no
    /// turn and wakes nothing: the reply is being written, so the engine is there, or there is nothing to end.
    /// </summary>
    public async Task<ThinkingEnd> EndThinkingAsync(string replyId, CancellationToken ct = default)
    {
        if (_api is not { } api) return ThinkingEnd.TooLate;
        try
        {
            return await api.EndThinkingAsync(replyId, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return ThinkingEnd.TooLate;
        }
    }

    /// <summary>Counts the reply as engine activity (for idle unload) and reports its speed.</summary>
    private async IAsyncEnumerable<ChatDelta> TrackAsync(Func<LlamaApi, IAsyncEnumerable<ChatDelta>> start,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var api = await BeginUseAsync(ct).ConfigureAwait(false);
        try
        {
            await foreach (var delta in start(api).ConfigureAwait(false))
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
            EndUse();
        }
    }

    /// <summary>
    /// Measures the loaded model: reads a fixed prompt and writes <paramref name="tokens"/> tokens,
    /// returning the engine's own timings.
    /// </summary>
    public async Task<BenchmarkTimings> BenchmarkAsync(int tokens = 160, CancellationToken ct = default)
    {
        var api = await BeginUseAsync(ct).ConfigureAwait(false);
        try
        {
            return await api.BenchmarkAsync(tokens, ct).ConfigureAwait(false);
        }
        finally
        {
            LastActivity = DateTimeOffset.Now;
            EndUse();
        }
    }

    /// <summary>
    /// Starts a request to the model and counts it as busy until <see cref="EndUse"/>. A model set aside for a picture
    /// is loaded again first: the request waits for any picture being drawn, then for the load.
    /// </summary>
    /// <exception cref="EngineException">No model is loaded, or the model that was set aside could not be loaded again.</exception>
    private async Task<LlamaApi> BeginUseAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_use)
            {
                if (State == EngineState.Ready && _api is { } api)
                {
                    _busy++;
                    return api;
                }
                if (State != EngineState.Resting)
                    throw new EngineException(State == EngineState.Failed && Error is { } why ? why : "No model is loaded.");
            }
            await WakeAsync(ct).ConfigureAwait(false);
        }
    }

    private void EndUse()
    {
        lock (_use) _busy--;
    }

    /// <summary>
    /// Loads the model that was set aside again, with the settings it had. Stopping the request that asked for it
    /// (<paramref name="ct"/>) does not stop the load: the model is wanted again either way, and the turn on the
    /// graphics card is kept until the load has ended.
    /// </summary>
    private async Task WakeAsync(CancellationToken ct)
    {
        var turn = await TakeCardTurnAsync(waking: true, ct).ConfigureAwait(false);
        Task load;
        try
        {
            load = WakeLockedAsync();
        }
        catch
        {
            turn.Dispose();
            throw;
        }
        _ = load.ContinueWith(_ => turn.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await load.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task WakeLockedAsync()
    {
        await _waking.WaitAsync().ConfigureAwait(false);
        try
        {
            // Someone else may have brought it back, or the owner unloaded it, while this request waited.
            if (State != EngineState.Resting || ModelPath is not { } path) return;
            await LoadAsync(path, _options).ConfigureAwait(false);
        }
        finally
        {
            _waking.Release();
        }
    }

    private async Task UnloadIfIdleAsync()
    {
        // A model set aside for a picture holds no memory, so there is nothing to give back; it stays remembered.
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

    private async Task WaitUntilReadyAsync(Process process, LlamaApi api, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) throw new EngineException(ExitMessage("The model engine stopped while loading the model."));
            if (await api.HealthAsync(ct).ConfigureAwait(false) == true) return;
            if (clock.Elapsed > LoadTimeout) throw new EngineException("The model took more than ten minutes to load, so DOG.ai gave up.");
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
        return EngineFailure.Explain(lead, lines);
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
