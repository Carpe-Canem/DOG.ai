using System.Collections.Concurrent;

namespace DogAI.UI;

/// <summary>Small display helpers shared by pages.</summary>
public static class Format
{
    /// <summary>"14:05" for today, "3 Nov" for older dates.</summary>
    public static string When(DateTime utc)
    {
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("d MMM");
    }

    private static readonly ConcurrentDictionary<(string Kind, long Id), (DateTime Stamp, int Length, string Url)> Pictures = new();

    /// <summary>
    /// A data: URL for a small PNG (the page CSP allows data: images and nothing remote).
    /// Cached per item and change time so long lists do not re-encode on every render.
    /// </summary>
    public static string? PictureUrl(string kind, long id, DateTime stamp, byte[]? png)
    {
        if (png is not { Length: > 0 }) return null;
        if (Pictures.TryGetValue((kind, id), out var cached) && cached.Stamp == stamp && cached.Length == png.Length) return cached.Url;
        var url = "data:image/png;base64," + Convert.ToBase64String(png);
        Pictures[(kind, id)] = (stamp, png.Length, url);
        return url;
    }
}
