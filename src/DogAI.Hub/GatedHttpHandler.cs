using DogAI.Core.Net;

namespace DogAI.Hub;

/// <summary>
/// Every outbound request in DOG.ai passes through here. It refuses to send unless the
/// online gate is open and the destination is on the allow-list, and it re-checks on redirects.
/// </summary>
public sealed class GatedHttpHandler(OnlineGate gate, NetworkAudit audit, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const int MaxRedirects = 8;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            Check(request.RequestUri);
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);

            var location = response.Headers.Location;
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || location is null)
                return response;
            if (hop >= MaxRedirects)
                throw new HttpRequestException("Too many redirects.");

            // Follow redirects ourselves so each hop is checked against the allow-list.
            var next = location.IsAbsoluteUri ? location : new Uri(request.RequestUri!, location);
            var keepMethod = (int)response.StatusCode is 307 or 308;
            var redirected = new HttpRequestMessage(keepMethod ? request.Method : HttpMethod.Get, next);
            foreach (var header in request.Headers)
            {
                // Never forward credentials to a different host (model files redirect to a CDN).
                if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                    !next.Host.Equals(request.RequestUri!.Host, StringComparison.OrdinalIgnoreCase))
                    continue;
                redirected.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            response.Dispose();
            request = redirected;
        }
    }

    private void Check(Uri? uri)
    {
        var target = uri?.GetLeftPart(UriPartial.Path) ?? "(none)";
        if (!gate.IsOnline)
        {
            audit.Record("hub", target, allowed: false, "offline");
            throw new OfflineException("DOG.ai is offline. Go online from the Models section to allow this.");
        }
        if (!OriginPolicy.IsAllowedHubUri(uri))
        {
            audit.Record("hub", target, allowed: false, "host not allowed");
            throw new HostNotAllowedException($"Blocked request to a host that is not on the allow-list: {uri?.Host}");
        }
        audit.Record("hub", target, allowed: true);
    }
}

/// <summary>The only HttpClient in the application. Inject this; never construct an HttpClient elsewhere.</summary>
public sealed class HubHttp : IDisposable
{
    public HubHttp(OnlineGate gate, NetworkAudit audit)
    {
        var sockets = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        Client = new HttpClient(new GatedHttpHandler(gate, audit, sockets)) { Timeout = Timeout.InfiniteTimeSpan };
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("DOG-AI/0.1");
    }

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}
