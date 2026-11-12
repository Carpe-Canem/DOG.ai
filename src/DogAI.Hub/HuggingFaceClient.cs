using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DogAI.Core.Models;

namespace DogAI.Hub;

public enum HfSort
{
    Trending,
    Downloads,
    Recent,
    Likes,
}

/// <summary>One model repository in search results.</summary>
public sealed record HfModelSummary(string Id, long Downloads, int Likes, DateTime? LastModified, bool Gated, string? PipelineTag)
{
    public string Author => Id.Split('/')[0];
    public string Name => Id.Contains('/') ? Id[(Id.IndexOf('/') + 1)..] : Id;
}

/// <summary>Thrown when Hugging Face answers with an error the user should see.</summary>
public sealed class HubException(string message) : Exception(message);

/// <summary>
/// Reads public information from Hugging Face: search, file lists, model cards, and the first bytes
/// of a model file so its header can be read without downloading the whole file.
/// </summary>
public sealed class HuggingFaceClient(HttpClient http, HfToken token)
{
    public const string Site = "https://huggingface.co";

    /// <summary>Enough bytes to cover a GGUF header, including a large vocabulary.</summary>
    private const long HeaderBytes = 48L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<HfModelSummary>> SearchAsync(string? query, HfSort sort, int limit = 40, CancellationToken ct = default)
    {
        var sortKey = sort switch
        {
            HfSort.Downloads => "downloads",
            HfSort.Recent => "lastModified",
            HfSort.Likes => "likes",
            _ => "trendingScore",
        };
        var url = $"{Site}/api/models?filter=gguf&sort={sortKey}&direction=-1&limit={limit}" +
                  "&expand[]=downloads&expand[]=likes&expand[]=lastModified&expand[]=gated&expand[]=pipeline_tag";
        if (!string.IsNullOrWhiteSpace(query)) url += "&search=" + Uri.EscapeDataString(query.Trim());

        var items = await GetJsonAsync<List<ApiModel>>(url, ct).ConfigureAwait(false) ?? [];
        return items.Select(m => new HfModelSummary(m.Id, m.Downloads, m.Likes, m.LastModified, IsGated(m.Gated), m.PipelineTag)).ToList();
    }

    /// <summary>Every file in the repository with its size and published SHA-256.</summary>
    public async Task<IReadOnlyList<RemoteFile>> FilesAsync(string repo, CancellationToken ct = default)
    {
        var url = $"{Site}/api/models/{EscapeRepo(repo)}/tree/main?recursive=true";
        var items = await GetJsonAsync<List<ApiTreeItem>>(url, ct).ConfigureAwait(false) ?? [];
        return items.Where(i => i.Type == "file")
            .Select(i => new RemoteFile(i.Path, i.Lfs?.Size ?? i.Size, i.Lfs?.Oid))
            .ToList();
    }

    /// <summary>The model card text, without its metadata block. Empty when the repository has none.</summary>
    public async Task<string> ReadmeAsync(string repo, CancellationToken ct = default)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, ResolveUrl(repo, "README.md")), ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return "";
        await EnsureOkAsync(response, ct).ConfigureAwait(false);
        return StripFrontMatter(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Fetches the start of a model file and hands it to <paramref name="parse"/> (the GGUF header reader).</summary>
    public async Task<T?> ReadHeaderAsync<T>(string repo, string path, Func<Stream, T> parse, CancellationToken ct = default) where T : class
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ResolveUrl(repo, path));
        request.Headers.Range = new RangeHeaderValue(0, HeaderBytes - 1);
        using var response = await SendAsync(request, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        await EnsureOkAsync(response, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // The reader only consumes the header; disposing the response stops the rest of the transfer.
        return await Task.Run(() => parse(stream), ct).ConfigureAwait(false);
    }

    public static string ResolveUrl(string repo, string path) =>
        $"{Site}/{EscapeRepo(repo)}/resolve/main/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";

    public static string RepoPage(string repo) => $"{Site}/{EscapeRepo(repo)}";

    internal static string StripFrontMatter(string text)
    {
        var trimmed = text.TrimStart('﻿');
        if (!trimmed.StartsWith("---", StringComparison.Ordinal)) return text;
        var end = trimmed.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return text;
        var after = trimmed.IndexOf('\n', end + 4);
        return after < 0 ? "" : trimmed[(after + 1)..].TrimStart();
    }

    private static string EscapeRepo(string repo) => string.Join('/', repo.Split('/').Select(Uri.EscapeDataString));

    private static bool IsGated(JsonElement gated) => gated.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => gated.GetString() is { Length: > 0 } s && s != "false",
        _ => false,
    };

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
        await EnsureOkAsync(response, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct).ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct,
        HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        if (token.Value is { Length: > 0 } value)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value);
        return http.SendAsync(request, option, ct);
    }

    internal static Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return Task.CompletedTask;
        throw new HubException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "Hugging Face refused access. This model may be gated: accept its terms on the website and add an access token in the Models section.",
            HttpStatusCode.NotFound => "Hugging Face could not find that model or file.",
            HttpStatusCode.TooManyRequests => "Hugging Face is limiting requests right now. Wait a minute and try again.",
            _ => $"Hugging Face answered with an error (HTTP {(int)response.StatusCode}).",
        });
    }

    private sealed record ApiModel(
        string Id,
        long Downloads,
        int Likes,
        DateTime? LastModified,
        JsonElement Gated,
        [property: JsonPropertyName("pipeline_tag")] string? PipelineTag);

    private sealed record ApiTreeItem(string Type, string Path, long Size, ApiLfs? Lfs);

    private sealed record ApiLfs(string Oid, long Size);
}
