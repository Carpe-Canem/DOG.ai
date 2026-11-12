using System.Net;
using System.Net.Sockets;
using DogAI.Core.Net;

namespace DogAI.Engine;

/// <summary>Refuses any request that is not plain HTTP to 127.0.0.1.</summary>
public sealed class LoopbackOnlyHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public static bool IsLoopback(Uri? uri) =>
        uri is { IsAbsoluteUri: true, Scheme: "http", Host: "127.0.0.1" };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!IsLoopback(request.RequestUri))
            throw new HostNotAllowedException("The engine client only talks to this computer (127.0.0.1).");
        return base.SendAsync(request, ct);
    }
}

/// <summary>
/// The client used to talk to the local model engine. It cannot reach the network: requests are
/// checked by <see cref="LoopbackOnlyHandler"/>, and the socket itself is always opened to 127.0.0.1
/// whatever address was asked for. Internet access goes through DogAI.Hub instead.
/// </summary>
public sealed class LoopbackHttp : IDisposable
{
    public LoopbackHttp()
    {
        var sockets = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        Client = new HttpClient(new LoopbackOnlyHandler(sockets)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}
