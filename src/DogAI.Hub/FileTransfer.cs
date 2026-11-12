using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace DogAI.Hub;

/// <summary>Downloads one file into a ".part" file, resuming where an earlier attempt stopped, and checks its hash.</summary>
internal static class FileTransfer
{
    private const int BufferSize = 1 << 20;

    /// <param name="progress">Called with the number of bytes of this file now on disk.</param>
    public static async Task DownloadAsync(HttpClient http, string url, string partPath, long expectedSize, string? bearer,
        Action<long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        for (var attempt = 0; ; attempt++)
        {
            var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (expectedSize > 0 && existing > expectedSize)
            {
                File.Delete(partPath);
                existing = 0;
            }
            if (expectedSize > 0 && existing == expectedSize)
            {
                progress(existing);
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            if (bearer is { Length: > 0 }) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && attempt == 0)
            {
                // The partial file does not match what the server has; start it again.
                File.Delete(partPath);
                continue;
            }
            if (!response.IsSuccessStatusCode && !IsHuggingFace(url))
                throw new HubException($"The download server answered with an error (HTTP {(int)response.StatusCode}).");
            await HuggingFaceClient.EnsureOkAsync(response, ct).ConfigureAwait(false);

            // A plain 200 means the server ignored the range and is sending the whole file.
            var append = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append) existing = 0;

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);
            var buffer = new byte[BufferSize];
            var written = existing;
            progress(written);
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                progress(written);
            }
            await target.FlushAsync(ct).ConfigureAwait(false);
            if (expectedSize > 0 && written != expectedSize)
                throw new IOException("The download ended early. It will continue from where it stopped when resumed.");
            return;
        }
    }

    /// <summary>SHA-256 of a file as lowercase hex, reporting how many bytes have been read.</summary>
    public static async Task<string> Sha256Async(string path, Action<long> progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize * 4, useAsync: true);
        var buffer = new byte[BufferSize * 4];
        long done = 0;
        var clock = Stopwatch.StartNew();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            if (clock.ElapsedMilliseconds > 200)
            {
                progress(done);
                clock.Restart();
            }
        }
        progress(done);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool IsHuggingFace(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Host == "huggingface.co" || u.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase));

    public static bool HashMatches(string actual, string? expected) =>
        expected is null || string.Equals(actual, expected.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
}
