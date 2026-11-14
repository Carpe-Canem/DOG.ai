using System.Net;
using DogAI.Core.Net;
using DogAI.Hub;

namespace DogAI.Tests;

public class OfflineGuardTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static (HttpClient client, OnlineGate gate, NetworkAudit audit, StubHandler stub) Build(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var gate = new OnlineGate();
        var audit = new NetworkAudit();
        var stub = new StubHandler(respond ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)));
        return (new HttpClient(new GatedHttpHandler(gate, audit, stub)), gate, audit, stub);
    }

    [Fact]
    public async Task Gate_is_closed_by_default_and_nothing_is_sent()
    {
        var (client, gate, audit, stub) = Build();

        Assert.False(gate.IsOnline);
        await Assert.ThrowsAsync<OfflineException>(() => client.GetAsync("https://huggingface.co/api/models"));
        Assert.Empty(stub.Seen);
        Assert.False(Assert.Single(audit.Snapshot()).Allowed);
    }

    [Fact]
    public async Task Online_allows_hugging_face()
    {
        var (client, gate, _, stub) = Build();
        gate.Set(true);

        var response = await client.GetAsync("https://huggingface.co/api/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(stub.Seen);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://huggingface.co/")]
    [InlineData("https://huggingface.co.evil.example/")]
    [InlineData("https://nothuggingface.co/")]
    public async Task Online_still_blocks_hosts_off_the_allow_list(string url)
    {
        var (client, gate, _, stub) = Build();
        gate.Set(true);

        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.GetAsync(url));
        Assert.Empty(stub.Seen);
    }

    [Fact]
    public async Task Redirect_to_a_disallowed_host_is_blocked()
    {
        var (client, gate, _, stub) = Build(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://example.com/file");
            return r;
        });
        gate.Set(true);

        await Assert.ThrowsAsync<HostNotAllowedException>(() => client.GetAsync("https://huggingface.co/x/resolve/main/y.gguf"));
        Assert.Single(stub.Seen);
    }

    [Fact]
    public async Task Redirect_to_an_allowed_cdn_is_followed_without_the_token()
    {
        HttpRequestMessage? second = null;
        var (client, gate, _, stub) = Build(req =>
        {
            if (req.RequestUri!.Host == "huggingface.co")
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri("https://cdn-lfs.hf.co/blob");
                return r;
            }
            second = req;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        gate.Set(true);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://huggingface.co/x/resolve/main/y.gguf");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer test-token");
        request.Headers.TryAddWithoutValidation("Range", "bytes=100-");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Seen.Count);
        Assert.NotNull(second);
        Assert.False(second!.Headers.Contains("Authorization"));
        Assert.True(second.Headers.Contains("Range"));
    }

    [Theory]
    [InlineData("https://0.0.0.1/", true)]
    [InlineData("https://0.0.0.1/_framework/blazor.webview.js", true)]
    [InlineData("about:blank", true)]
    [InlineData("data:image/png;base64,AAAA", true)]
    [InlineData("http://0.0.0.1/", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://0.0.0.1.example.com/", false)]
    [InlineData("wss://example.com/socket", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("not a uri", false)]
    [InlineData(null, false)]
    public void Web_view_only_loads_the_app_origin(string? uri, bool expected) =>
        Assert.Equal(expected, OriginPolicy.IsAppUri(uri));
}
