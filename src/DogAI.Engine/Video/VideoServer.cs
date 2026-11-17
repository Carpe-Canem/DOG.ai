using System.Diagnostics;
using DogAI.Core;
using DogAI.Core.Images;
using DogAI.Core.Models;
using DogAI.Core.Video;
using DogAI.Engine.Images;

namespace DogAI.Engine.Video;

/// <summary>
/// The video engine: the picture engine's own program (stable-diffusion.cpp's sd-server) started with a video model
/// in place of a checkpoint. It listens only on 127.0.0.1 on a random port and dies with DOG.ai (<see cref="ProcessJob"/>).
/// <para>
/// <b>The program lives for one clip.</b> It is started when a clip is asked for (it answers after a second and reads
/// the model only then) and ended as soon as the clip has been collected, cancelled or has failed. So nothing is left
/// on the graphics card afterwards, a cancel always takes within seconds (the model's own decoder cannot be
/// interrupted any other way), and the decoder, which the engine decides at start, is always the one asked for.
/// </para>
/// <para>
/// Privacy, as measured in the M20 trial: the engine writes nothing to disk, and the clip exists only in its reply.
/// Two things keep it so. It is always started with <c>--disable-image-metadata</c>: without it the prompt, the
/// negative prompt and the model's file names are written inside every clip, and the request cannot ask for that.
/// Every clip is therefore checked by structure before it is handed on (<see cref="VideoClips.Problem"/>): one with
/// tags in it is refused. And its output lines reach the log only through <see cref="Logged"/>, which a
/// <see cref="PictureLogFilter"/> guards; <see cref="Output"/> is everything it prints and must never be logged.
/// </para>
/// </summary>
public sealed class VideoServer : IVideoEngine, IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly LoopbackHttp _http;
    private readonly SdServerLocator _locator;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _making = new(1, 1);
    private readonly ProcessJob _job = new();
    private readonly Queue<string> _recentOutput = new();
    private readonly PictureLogFilter _filter = new();
    private readonly object _processLock = new();
    private Process? _process;
    private int _busy;

    public VideoServer(LoopbackHttp http, SdServerLocator locator, AppPaths paths)
    {
        _http = http;
        _locator = locator;
        _paths = paths;
    }

    public bool IsBusy => Volatile.Read(ref _busy) > 0;

    public bool IsRunning => ProcessId is not null;

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

    /// <summary>Why the program last failed to start, in plain words. Cleared when it starts.</summary>
    public string? Error { get; private set; }

    /// <summary>When the last clip was finished (UTC), or null. Soon after, the model's files are still in Windows' cache and the next clip starts faster.</summary>
    public DateTime? LastMadeAt { get; private set; }

    public event Action? Changed;

    /// <summary>Every line the engine prints, unfiltered. For tests; never write these to a log.</summary>
    public event Action<string>? Output;

    /// <summary>The lines that are safe to log (see <see cref="PictureLogFilter"/>): status lines that share no word with a sentence sent.</summary>
    public event Action<string>? Logged;

    /// <summary>
    /// The program's start arguments for a video model. Public for tests, which pin the list: no checkpoint
    /// (<c>-m</c>), no LoRA or upscaler folder, and never <c>--offload-to-cpu</c> or <c>--mmap</c> (with 32 GB of RAM
    /// they left 2.7 GB free and were slower).
    /// </summary>
    /// <param name="plan">The <c>--max-vram</c> value (<see cref="VideoMemory.EnginePlan"/>): <c>-3</c> unless part of the card is in use.</param>
    public static IReadOnlyList<string> Arguments(VideoModel model, VideoDecoder decoder, int port, string blankPage, string plan = VideoMemory.WholeCardPlan)
    {
        var args = new List<string>
        {
            "--listen-ip", "127.0.0.1", "--listen-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--diffusion-model", model.DiffusionPath,
            "--vae", model.VaePath,
            "--t5xxl", model.TextEncoderPath,
            // The fast decoder is always loaded: it makes the previews, and the clip itself unless told otherwise below.
            "--taesd", model.FastDecoderPath,
        };
        // The model's own decoder for the clip; the fast one then only makes the previews.
        if (decoder == VideoDecoder.Own) args.Add("--taesd-preview-only");
        args.AddRange(
        [
            // The built-in web page cannot be switched off; serving a blank page in its place leaves nothing to click.
            "--serve-html-path", blankPage,
            // A finished clip stays in the engine's memory only briefly; DOG.ai collects it at once and ends the program.
            "--completed-ttl-seconds", "60",
            // Without this the prompt is written inside the clip. The request cannot ask for it; only this switch can.
            "--disable-image-metadata",
            // Keep 3 GB of the card free: without it the first clip of a fresh program once overflowed the card. When
            // part of the card is in use, a plan in GiB that DOG.ai worked out: the engine does not see what others hold.
            "--max-vram", plan,
        ]);
        return args;
    }

    public Task<VideoResult> MakeAsync(VideoModel model, VideoRequest request, IProgress<VideoProgress>? progress = null, CancellationToken ct = default) =>
        MakeAsync(model, request, null, progress, ct);

    public async Task<VideoResult> MakeAsync(VideoModel model, VideoRequest request, long? cardFreeBytes, IProgress<VideoProgress>? progress, CancellationToken ct)
    {
        if (request.Problems() is { Count: > 0 } problems) throw new VideoEngineException(string.Join(" ", problems));
        if (model.Problem() is { } missing) throw new VideoEngineException(missing);

        await _making.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _busy);
        Changed?.Invoke();
        try
        {
            progress?.Report(new VideoProgress(VideoStages.Starting));
            var api = await StartAsync(model, request.Decoder, VideoMemory.EnginePlan(cardFreeBytes, request.Decoder), ct).ConfigureAwait(false);
            try
            {
                var result = await MakeWithAsync(api, request, progress, ct).ConfigureAwait(false);
                LastMadeAt = DateTime.UtcNow;
                return result;
            }
            catch (HttpRequestException)
            {
                // The program died (or was stopped) mid-clip. Its output is not quoted: it could follow a sentence.
                throw new VideoEngineException("The video engine stopped while making the video. Try again; it will be started again.");
            }
        }
        finally
        {
            // Finished, cancelled or failed: the program goes, and with it everything it held on the card. A cancel
            // that the engine had not acted on (its own decoder cannot be interrupted) takes here.
            StopProcess();
            Interlocked.Decrement(ref _busy);
            _making.Release();
            if (_filter.TakeHeldBack() is > 0 and var held)
                Logged?.Invoke($"({held} of the engine's status lines were not logged because they share a word with a sentence)");
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The clip itself, with the program already answering: the log filter is told the sentence before the request
    /// leaves, and the file is checked before it is handed on. Internal so tests can run it against a pretend engine.
    /// </summary>
    internal async Task<VideoResult> MakeWithAsync(SdApi api, VideoRequest request, IProgress<VideoProgress>? progress, CancellationToken ct)
    {
        var seed = request.Seed >= 0 ? request.Seed : Random.Shared.NextInt64(0, uint.MaxValue);
        // Before the request leaves: from here on no line that shares a word with the sentence reaches the log.
        _filter.Sending(request.Prompt);
        var made = await api.MakeVideoAsync(request, seed, progress, ct).ConfigureAwait(false);
        // One with tags in it (an engine started without its metadata switch) or of another size is never shown or kept.
        if (VideoClips.Problem(made.File, request) is { } problem)
            throw new VideoEngineException("The video engine made a file DOG.ai does not show or keep. " + problem);
        var info = WebmInfo.Read(made.File)!.Value;
        return new VideoResult(made.File, info.Width, info.Height, info.Frames, request.Fps, seed);
    }

    /// <summary>Ends the program. A clip being made fails with a message; the next clip starts the program again.</summary>
    public Task StopAsync()
    {
        StopProcess();
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    private async Task<SdApi> StartAsync(VideoModel model, VideoDecoder decoder, string plan, CancellationToken ct)
    {
        StopProcess();
        Changed?.Invoke();
        try
        {
            var exe = _locator.Find() ?? throw new VideoEngineException("Videos are made by the picture engine. " + SdServerLocator.MissingMessage);
            var port = EngineManager.FreePort();
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            foreach (var arg in Arguments(model, decoder, port, ImageServer.BlankPageIn(_paths), plan))
                start.ArgumentList.Add(arg);
            EngineEnvironment.Clean(start);

            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => Remember(e.Data);
            process.ErrorDataReceived += (_, e) => Remember(e.Data);
            process.Exited += (_, _) => Changed?.Invoke();
            lock (_recentOutput) _recentOutput.Clear();
            // No Windows error box if a library is missing: the program then ends at once and the reason is said here.
            EngineEnvironment.StartQuietly(process);
            _job.Add(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            lock (_processLock) _process = process;

            var api = new SdApi(_http.Client, port);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited) throw new VideoEngineException(StoppedAtStart(process));
                if (await api.HealthAsync(ct).ConfigureAwait(false)) break;
                if (clock.Elapsed > StartTimeout)
                    throw new VideoEngineException($"The video engine was still not answering {StartTimeout.TotalSeconds:0} seconds after it started, so DOG.ai stopped it. Try the video again.");
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            Port = port;
            Error = null;
            Changed?.Invoke();
            return api;
        }
        catch (VideoEngineException e)
        {
            StopProcess();
            Error = e.Message;
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            StopProcess();
            Error = "The video engine could not start: " + e.Message;
            throw new VideoEngineException(Error);
        }
    }

    private void StopProcess()
    {
        Process? process;
        lock (_processLock)
        {
            process = _process;
            _process = null;
            Port = null;
        }
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

    /// <summary>Internal so tests can feed it the lines a program would print.</summary>
    internal void Remember(string? line)
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
    /// Why the program ended before it answered: Windows' reason when it could not start it at all, else its own
    /// lines. Only used while starting: this program was never sent a sentence, so nothing quoted can come from one.
    /// </summary>
    private string StoppedAtStart(Process process)
    {
        int code;
        try
        {
            code = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = 0;
        }
        if (EngineFailure.StartProblem(code) is { } why)
            return $"The video engine could not start: {why} It is the picture engine’s program: install that again in {ModelsPlaces.EnginesName}.";
        string[] lines;
        lock (_recentOutput) lines = _recentOutput.ToArray();
        return EngineFailure.Explain("The video engine stopped while starting.", lines);
    }

    public void Dispose()
    {
        StopProcess();
        _job.Dispose();
    }
}
