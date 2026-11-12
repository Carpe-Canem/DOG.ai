using System.Diagnostics;
using System.Text.Json.Serialization;
using DogAI.Core.Models;
using DogAI.Core.Net;

namespace DogAI.Hub;

public enum DownloadStatus
{
    Queued,
    Running,
    Paused,
    Verifying,
    Done,
    Failed,
}

public sealed class DownloadPart
{
    public required string Url { get; init; }

    /// <summary>File name inside the job's folder.</summary>
    public required string FileName { get; init; }

    public long Size { get; init; }
    public string? Sha256 { get; init; }
    public bool Done { get; set; }
}

/// <summary>One model being downloaded: one file, or all parts of a split model.</summary>
public sealed class DownloadJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Repo { get; init; }
    public required string Title { get; init; }
    public string? Quant { get; init; }

    /// <summary>Curated use case it was picked for, if any; copied to the library record.</summary>
    public string? UseCase { get; init; }

    public required string Folder { get; init; }
    public required List<DownloadPart> Parts { get; init; }
    public DownloadStatus Status { get; set; }

    /// <summary>Why it failed or paused, in plain words.</summary>
    public string? Message { get; set; }

    [JsonIgnore] public long BytesDone { get; internal set; }
    [JsonIgnore] public double BytesPerSecond { get; internal set; }

    /// <summary>While verifying: bytes of the current file checked so far.</summary>
    [JsonIgnore] public long BytesVerified { get; internal set; }

    public long TotalBytes => Parts.Sum(p => p.Size);
    public string MainPath => Path.Combine(Folder, Parts[0].FileName);
    public bool IsActive => Status is DownloadStatus.Running or DownloadStatus.Verifying;
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp(BytesDone / (double)TotalBytes, 0, 1);

    public TimeSpan? Remaining => BytesPerSecond > 1 && Status == DownloadStatus.Running
        ? TimeSpan.FromSeconds((TotalBytes - BytesDone) / BytesPerSecond)
        : null;
}

/// <summary>Thrown when a download cannot be queued, e.g. for lack of disk space.</summary>
public sealed class DownloadRefusedException(string message) : Exception(message);

/// <summary>
/// Downloads models one at a time. Each file goes to a ".part" file next to its final name and is only
/// renamed once its SHA-256 matches the one Hugging Face publishes. Pausing keeps the partial file,
/// and resuming continues from it. The queue is saved, so it survives a restart (paused).
/// </summary>
public sealed class DownloadManager : IDisposable
{
    private const string StoreKey = "downloads";

    /// <summary>Kept free on the target drive on top of the download itself.</summary>
    public const long DiskMargin = 2 * Sizes.GiB;

    private readonly HttpClient _http;
    private readonly IDocumentStore _store;
    private readonly OnlineSession _online;
    private readonly HfToken _token;
    private readonly object _lock = new();
    private readonly List<DownloadJob> _jobs = [];
    private readonly Func<string, long?> _freeSpace;
    private DownloadJob? _current;
    private CancellationTokenSource? _currentCts;
    private bool _removing;

    public DownloadManager(HubHttp hub, IDocumentStore store, OnlineSession online, HfToken token)
        : this(hub.Client, store, online, token, FreeSpace)
    {
    }

    /// <param name="freeSpace">Free bytes on the drive holding a folder; replaced in tests.</param>
    public DownloadManager(HttpClient http, IDocumentStore store, OnlineSession online, HfToken token, Func<string, long?> freeSpace)
    {
        _http = http;
        _store = store;
        _online = online;
        _token = token;
        _freeSpace = freeSpace;
    }

    /// <summary>Raised when anything changes, including progress (at most a few times a second). May come from a background thread.</summary>
    public event Action? Changed;

    /// <summary>Raised when a model has finished downloading and passed its check.</summary>
    public event Action<DownloadJob>? Completed;

    public IReadOnlyList<DownloadJob> Jobs
    {
        get { lock (_lock) return _jobs.ToList(); }
    }

    public bool HasPending => Jobs.Any(j => j.Status is DownloadStatus.Queued or DownloadStatus.Running or DownloadStatus.Verifying);

    /// <summary>Restores the saved queue. Anything that was running is paused until the user resumes it online.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var saved = await _store.LoadAsync<List<DownloadJob>>(StoreKey, ct).ConfigureAwait(false) ?? [];
        lock (_lock)
        {
            _jobs.Clear();
            foreach (var job in saved)
            {
                if (job.Status is DownloadStatus.Running or DownloadStatus.Verifying or DownloadStatus.Queued)
                {
                    job.Status = DownloadStatus.Paused;
                    job.Message = null;
                }
                job.BytesDone = BytesOnDisk(job);
                _jobs.Add(job);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Adds a model to the queue and starts it if nothing else is downloading.</summary>
    public async Task<DownloadJob> EnqueueAsync(DownloadJob job)
    {
        lock (_lock)
        {
            if (_jobs.Any(j => j.Status != DownloadStatus.Done && SamePath(j.MainPath, job.MainPath)))
                throw new DownloadRefusedException("That file is already in the download list.");
        }
        if (job.Parts.All(p => File.Exists(Path.Combine(job.Folder, p.FileName))))
            throw new DownloadRefusedException("That model is already downloaded.");

        var needed = job.TotalBytes - BytesOnDisk(job) + DiskMargin;
        var queuedOnSameDrive = Jobs.Where(j => j.Status != DownloadStatus.Done && SameDrive(j.Folder, job.Folder))
            .Sum(j => j.TotalBytes - j.BytesDone);
        if (_freeSpace(job.Folder) is { } free && free < needed + queuedOnSameDrive)
            throw new DownloadRefusedException(
                $"Not enough free space on {DriveName(job.Folder)}: this needs {Sizes.Gb(needed + queuedOnSameDrive)} " +
                $"(including other queued downloads and room to spare) and {Sizes.Gb(free)} is free.");

        job.Status = DownloadStatus.Queued;
        job.BytesDone = BytesOnDisk(job);
        lock (_lock) _jobs.Add(job);
        await SaveAsync().ConfigureAwait(false);
        Changed?.Invoke();
        Pump();
        return job;
    }

    public void Pause(Guid id)
    {
        var job = Find(id);
        if (job is null) return;
        lock (_lock)
        {
            if (job.Status == DownloadStatus.Queued) job.Status = DownloadStatus.Paused;
            else if (ReferenceEquals(job, _current)) _currentCts?.Cancel();
        }
        _ = SaveAsync();
        Changed?.Invoke();
    }

    public void Resume(Guid id)
    {
        var job = Find(id);
        if (job is null || job.Status is not (DownloadStatus.Paused or DownloadStatus.Failed)) return;
        job.Status = DownloadStatus.Queued;
        job.Message = null;
        _ = SaveAsync();
        Changed?.Invoke();
        Pump();
    }

    /// <summary>Removes a job from the list. Unfinished downloads also lose their partial files.</summary>
    public async Task RemoveAsync(Guid id)
    {
        var job = Find(id);
        if (job is null) return;
        Task? running = null;
        lock (_lock)
        {
            if (ReferenceEquals(job, _current))
            {
                _removing = true;
                _currentCts?.Cancel();
                running = _runner;
            }
        }
        if (running is not null)
        {
            try { await running.ConfigureAwait(false); } catch { /* reported on the job, which is going away */ }
        }
        lock (_lock) _jobs.Remove(job);
        if (job.Status != DownloadStatus.Done)
        {
            foreach (var part in job.Parts.Where(p => !p.Done))
                TryDelete(Path.Combine(job.Folder, part.FileName) + ".part");
        }
        await SaveAsync().ConfigureAwait(false);
        Changed?.Invoke();
        Pump();
    }

    /// <summary>Drops finished downloads from the list (the files stay).</summary>
    public async Task ClearFinishedAsync()
    {
        lock (_lock) _jobs.RemoveAll(j => j.Status == DownloadStatus.Done);
        await SaveAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Waits for the download in progress, if any. For tests.</summary>
    public Task IdleAsync()
    {
        lock (_lock) return _runner ?? Task.CompletedTask;
    }

    private Task? _runner;

    private void Pump()
    {
        lock (_lock)
        {
            if (_current is not null) return;
            var next = _jobs.FirstOrDefault(j => j.Status == DownloadStatus.Queued);
            if (next is null) return;
            _current = next;
            _currentCts = new CancellationTokenSource();
            next.Status = DownloadStatus.Running;
            var ct = _currentCts.Token;
            _runner = Task.Run(() => RunAsync(next, ct));
        }
    }

    private async Task RunAsync(DownloadJob job, CancellationToken ct)
    {
        using var work = _online.BeginWork();
        Changed?.Invoke();
        try
        {
            long finished = job.Parts.Where(p => p.Done).Sum(p => p.Size);
            foreach (var part in job.Parts)
            {
                if (part.Done) continue;
                var final = Path.Combine(job.Folder, part.FileName);
                var temp = final + ".part";
                job.Status = DownloadStatus.Running;
                var speed = new SpeedMeter();
                var throttle = Stopwatch.StartNew();
                await FileTransfer.DownloadAsync(_http, part.Url, temp, part.Size, _token.Value, written =>
                {
                    job.BytesDone = finished + written;
                    job.BytesPerSecond = speed.Add(written);
                    if (throttle.ElapsedMilliseconds < 250) return;
                    throttle.Restart();
                    Changed?.Invoke();
                }, ct).ConfigureAwait(false);

                job.Status = DownloadStatus.Verifying;
                job.BytesVerified = 0;
                Changed?.Invoke();
                var hash = await FileTransfer.Sha256Async(temp, read =>
                {
                    job.BytesVerified = read;
                    Changed?.Invoke();
                }, ct).ConfigureAwait(false);
                if (!FileTransfer.HashMatches(hash, part.Sha256))
                {
                    TryDelete(temp);
                    throw new HubException($"{part.FileName} arrived damaged (its checksum did not match), so it was deleted. Try the download again.");
                }

                File.Move(temp, final, overwrite: true);
                part.Done = true;
                finished += part.Size;
                job.BytesDone = finished;
                await SaveAsync().ConfigureAwait(false);
            }

            job.Status = DownloadStatus.Done;
            job.Message = null;
            job.BytesPerSecond = 0;
            await SaveAsync().ConfigureAwait(false);
            Completed?.Invoke(job);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Status = DownloadStatus.Paused;
            job.Message = null;
        }
        catch (OfflineException)
        {
            job.Status = DownloadStatus.Paused;
            job.Message = "Paused because DOG AI went offline. Go online in the Models section to continue.";
        }
        catch (Exception e) when (e is HubException or HttpRequestException or IOException or HostNotAllowedException or UnauthorizedAccessException)
        {
            job.Status = DownloadStatus.Failed;
            job.Message = e is HttpRequestException ? "The connection failed: " + e.Message : e.Message;
        }
        finally
        {
            job.BytesPerSecond = 0;
            bool removing;
            lock (_lock)
            {
                removing = _removing;
                _current = null;
                _currentCts?.Dispose();
                _currentCts = null;
                _removing = false;
            }
            if (!removing) await SaveAsync().ConfigureAwait(false);
            Changed?.Invoke();
            Pump();
        }
    }

    private DownloadJob? Find(Guid id)
    {
        lock (_lock) return _jobs.FirstOrDefault(j => j.Id == id);
    }

    private async Task SaveAsync()
    {
        List<DownloadJob> snapshot;
        lock (_lock) snapshot = _jobs.ToList();
        await _store.SaveAsync(StoreKey, snapshot).ConfigureAwait(false);
    }

    private static long BytesOnDisk(DownloadJob job) => job.Parts.Sum(p =>
    {
        if (p.Done) return p.Size;
        var temp = Path.Combine(job.Folder, p.FileName) + ".part";
        return File.Exists(temp) ? Math.Min(p.Size, new FileInfo(temp).Length) : 0;
    });

    private static long? FreeSpace(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string DriveName(string folder) => Path.GetPathRoot(Path.GetFullPath(folder))?.TrimEnd('\\') ?? folder;

    private static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _currentCts?.Cancel();

    /// <summary>Download speed over the last few seconds.</summary>
    private sealed class SpeedMeter
    {
        private readonly Queue<(long Ticks, long Bytes)> _samples = new();

        public double Add(long bytes)
        {
            var now = Stopwatch.GetTimestamp();
            _samples.Enqueue((now, bytes));
            while (_samples.Count > 2 && Stopwatch.GetElapsedTime(_samples.Peek().Ticks, now) > TimeSpan.FromSeconds(5))
                _samples.Dequeue();
            var (firstTicks, firstBytes) = _samples.Peek();
            var seconds = Stopwatch.GetElapsedTime(firstTicks, now).TotalSeconds;
            return seconds < 0.5 ? 0 : (bytes - firstBytes) / seconds;
        }
    }
}
