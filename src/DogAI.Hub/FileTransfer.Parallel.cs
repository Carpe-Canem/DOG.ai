using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace DogAI.Hub;

/// <summary>How a file is fetched. Big files use several connections because one stream is often capped well below the line speed.</summary>
internal sealed record TransferOptions(long ParallelFrom = 64L << 20, int Connections = 8)
{
    public static TransferOptions Default { get; } = new();
}

internal sealed class RangesNotSupportedException() : Exception("The server does not serve byte ranges.");

internal static partial class FileTransfer
{
    private const int SegmentAttempts = 3;

    /// <summary>
    /// What a multi-connection download has already written: the file is cut into equal segments and
    /// Done[i] is how many bytes of segment i are on disk. It sits next to the .part file and is only ever
    /// behind the truth, never ahead, so a resume at worst fetches a little twice.
    /// </summary>
    internal sealed record SegmentState(long Size, long[] Done);

    private static string StatePath(string partPath) => partPath + ".segments";

    private static long SegmentLength(long size, int count) => (size + count - 1) / count;

    /// <summary>The saved record for a part file, or null (and any stale files removed) when there is none that fits.</summary>
    private static SegmentState? LoadState(string partPath, long size)
    {
        var path = StatePath(partPath);
        if (!File.Exists(path)) return null;
        try
        {
            var state = JsonSerializer.Deserialize<SegmentState>(File.ReadAllText(path));
            if (state is { Done.Length: >= 1 and <= 64 } && state.Size == size && File.Exists(partPath) &&
                new FileInfo(partPath).Length == size)
            {
                var length = SegmentLength(size, state.Done.Length);
                var fits = true;
                for (var i = 0; i < state.Done.Length; i++)
                {
                    var segment = Math.Min(length, size - i * length);
                    if (state.Done[i] < 0 || state.Done[i] > Math.Max(segment, 0)) fits = false;
                }
                if (fits) return state;
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        DeletePart(partPath);
        return null;
    }

    private static void SaveState(string partPath, SegmentState state)
    {
        var path = StatePath(partPath);
        try
        {
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes a part file and the record beside it.</summary>
    public static void DeletePart(string partPath)
    {
        foreach (var path in new[] { partPath, StatePath(partPath), StatePath(partPath) + ".tmp" })
        {
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>How much of a file is really downloaded (a multi-connection part file is full-size from the start).</summary>
    public static long BytesOnDisk(string partPath, long size)
    {
        if (!File.Exists(partPath)) return 0;
        var path = StatePath(partPath);
        if (File.Exists(path))
        {
            try
            {
                var state = JsonSerializer.Deserialize<SegmentState>(File.ReadAllText(path));
                if (state is not null) return Math.Min(size, state.Done.Sum());
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
            return 0;
        }
        return Math.Min(size, new FileInfo(partPath).Length);
    }

    private static async Task DownloadParallelAsync(HttpClient http, string url, string partPath, long size, string? bearer,
        Action<long> progress, SegmentState? resumed, int connections, CancellationToken ct)
    {
        SegmentState state;
        if (resumed is not null) state = resumed;
        else
        {
            var length = SegmentLength(size, connections);
            state = new SegmentState(size, new long[(size + length - 1) / length]);
        }
        var segments = state.Done.Length;
        var segmentLength = SegmentLength(size, segments);

        using var handle = File.OpenHandle(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous);
        if (RandomAccess.GetLength(handle) != size) RandomAccess.SetLength(handle, size); // claims the disk space up front
        SaveState(partPath, state);

        var gate = new object();
        var lastSave = Environment.TickCount64;
        void Report()
        {
            lock (gate)
            {
                progress(state.Done.Sum());
                if (Environment.TickCount64 - lastSave < 1000) return;
                lastSave = Environment.TickCount64;
                SaveState(partPath, state);
            }
        }

        Report();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, segments).Select(async i =>
        {
            try { await FetchSegmentAsync(i).ConfigureAwait(false); }
            catch { stop.Cancel(); throw; }
        }).ToArray();

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            if (ct.IsCancellationRequested) throw;
            // Report the cause, not the cancellations it set off in the other connections.
            var cause = workers.Where(w => w.IsFaulted).SelectMany(w => w.Exception!.InnerExceptions)
                .FirstOrDefault(e => e is not OperationCanceledException);
            if (cause is not null) ExceptionDispatchInfo.Capture(cause).Throw();
            throw;
        }
        finally
        {
            lock (gate) SaveState(partPath, state);
        }

        if (state.Done.Sum() != size) throw new IOException("The download ended early. It will continue from where it stopped when resumed.");
        File.Delete(StatePath(partPath));
        progress(size);

        async Task FetchSegmentAsync(int i)
        {
            var start = i * segmentLength;
            var end = Math.Min(size, start + segmentLength); // exclusive
            var buffer = new byte[BufferSize];
            for (var attempt = 1; ; attempt++)
            {
                var from = start + state.Done[i];
                if (from >= end) return;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(from, end - 1);
                    if (bearer is { Length: > 0 }) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.OK) throw new RangesNotSupportedException();
                    if (!response.IsSuccessStatusCode && !IsHuggingFace(url))
                        throw new HubException($"The download server answered with an error (HTTP {(int)response.StatusCode}).");
                    await HuggingFaceClient.EnsureOkAsync(response, stop.Token).ConfigureAwait(false);

                    await using var source = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
                    int read;
                    while (from < end && (read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - from)), stop.Token).ConfigureAwait(false)) > 0)
                    {
                        await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), from, stop.Token).ConfigureAwait(false);
                        from += read;
                        lock (gate) state.Done[i] = from - start;
                        Report();
                    }
                    if (from < end) throw new IOException("The download ended early. It will continue from where it stopped when resumed.");
                }
                catch (Exception e) when (attempt < SegmentAttempts && !stop.IsCancellationRequested && e is IOException or HttpRequestException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), stop.Token).ConfigureAwait(false);
                }
            }
        }
    }
}
