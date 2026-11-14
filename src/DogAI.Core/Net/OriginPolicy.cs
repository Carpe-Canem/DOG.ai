namespace DogAI.Core.Net;

/// <summary>Pure rules for what the embedded web view and the Hub may talk to.</summary>
public static class OriginPolicy
{
    /// <summary>The virtual origin BlazorWebView serves the app from. Nothing is listening there; it never leaves the process.</summary>
    public const string AppHost = "0.0.0.1";

    /// <summary>True when the web view may load this URI: the app's own origin or a non-network scheme.</summary>
    public static bool IsAppUri(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        return u.Scheme switch
        {
            "https" => u.Host == AppHost,
            "data" or "blob" => true,
            "about" => uri!.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static readonly string[] HubHosts = ["huggingface.co", "hf.co", "github.com", "githubusercontent.com"];

    /// <summary>True when the Hub may contact this URI: HTTPS to Hugging Face or GitHub (including their subdomains).</summary>
    public static bool IsAllowedHubUri(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true, Scheme: "https" }) return false;
        var host = uri.Host;
        return HubHosts.Any(h =>
            host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
    }
}
