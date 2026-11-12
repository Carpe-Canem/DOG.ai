using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DogAI.Core;
using DogAI.Core.Models;
using DogAI.Core.Net;
using DogAI.Hub;

namespace DogAI.Tests;

internal sealed class MemoryDocumentStore : IDocumentStore
{
    private readonly Dictionary<string, string> _docs = [];

    public Task<T?> LoadAsync<T>(string key, CancellationToken ct = default) where T : class =>
        Task.FromResult(_docs.TryGetValue(key, out var json) ? System.Text.Json.JsonSerializer.Deserialize<T>(json) : null);

    public Task SaveAsync<T>(string key, T value, CancellationToken ct = default) where T : class
    {
        lock (_docs) _docs[key] = System.Text.Json.JsonSerializer.Serialize(value);
        return Task.CompletedTask;
    }
}

/// <summary>Serves fixed files by URL, honouring Range requests, like a download server.</summary>
internal sealed class FileServer : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = [];
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Stop sending after this many bytes of a response, to simulate a dropped connection.</summary>
    public long? CutAfter { get; set; }

    /// <summary>Wait on this before sending each response body, to hold a download open.</summary>
    public TaskCompletionSource? Hold { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        if (Hold is { } hold) await hold.Task.WaitAsync(ct);
        if (!Files.TryGetValue(request.RequestUri!.ToString(), out var data))
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        long from = request.Headers.Range?.Ranges.First().From ?? 0;
        if (from >= data.Length && data.Length > 0) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
        var body = data[(int)from..];
        if (CutAfter is { } cut && body.Length > cut) body = body[..(int)cut];
        var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
        return response;
    }
}

public sealed class DownloadManagerTests : IDisposable
{
    private const string Url = "https://huggingface.co/acme/tiny-GGUF/resolve/main/tiny-Q4_K_M.gguf";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dogai-test-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _data = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123);
    private readonly FileServer _server = new();
    private readonly OnlineGate _gate = new();
    private readonly OnlineSession _session;

    public DownloadManagerTests()
    {
        _server.Files[Url] = _data;
        _session = new OnlineSession(_gate);
        _session.EnterModels();
        _session.SetWanted(true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private DownloadManager Manager(long? free = null) =>
        new(new HttpClient(new GatedHttpHandler(_gate, new NetworkAudit(), _server)), new MemoryDocumentStore(), _session, new HfToken(), _ => free);

    private DownloadJob Job(string? sha = null) => new()
    {
        Repo = "acme/tiny-GGUF",
        Title = "tiny-Q4_K_M",
        Folder = _folder,
        Parts = [new DownloadPart { Url = Url, FileName = "tiny-Q4_K_M.gguf", Size = _data.Length, Sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(_data)) }],
    };

    private string Final => Path.Combine(_folder, "tiny-Q4_K_M.gguf");

    [Fact]
    public async Task Downloads_checks_and_renames_the_file()
    {
        var manager = Manager();
        DownloadJob? completed = null;
        manager.Completed += j => completed = j;

        var job = await manager.EnqueueAsync(Job());
        await manager.IdleAsync();

        Assert.Equal(DownloadStatus.Done, job.Status);
        Assert.Same(job, completed);
        Assert.Equal(_data, await File.ReadAllBytesAsync(Final));
        Assert.False(File.Exists(Final + ".part"));
    }

    [Fact]
    public async Task A_dropped_connection_resumes_from_the_partial_file()
    {
        var manager = Manager();
        _server.CutAfter = 1024 * 1024;

        var job = await manager.EnqueueAsync(Job());
        await manager.IdleAsync();
        Assert.Equal(DownloadStatus.Failed, job.Status);
        Assert.Equal(1024 * 1024, new FileInfo(Final + ".part").Length);

        _server.CutAfter = null;
        manager.Resume(job.Id);
        await manager.IdleAsync();

        Assert.Equal(DownloadStatus.Done, job.Status);
        Assert.Equal(_data, await File.ReadAllBytesAsync(Final));
        Assert.Equal(1024 * 1024, _server.Requests[^1].Headers.Range!.Ranges.First().From);
    }

    [Fact]
    public async Task A_damaged_file_is_deleted_and_reported()
    {
        var manager = Manager();
        var job = await manager.EnqueueAsync(Job(sha: new string('0', 64)));
        await manager.IdleAsync();

        Assert.Equal(DownloadStatus.Failed, job.Status);
        Assert.Contains("damaged", job.Message);
        Assert.False(File.Exists(Final));
        Assert.False(File.Exists(Final + ".part"));
    }

    [Fact]
    public async Task Refuses_when_the_drive_is_too_full()
    {
        var manager = Manager(free: 1024);
        var error = await Assert.ThrowsAsync<DownloadRefusedException>(() => manager.EnqueueAsync(Job()));
        Assert.Contains("Not enough free space", error.Message);
        Assert.Empty(manager.Jobs);
    }

    [Fact]
    public async Task Pausing_keeps_the_partial_file_and_resuming_finishes()
    {
        var manager = Manager();
        _server.Hold = new TaskCompletionSource();
        var job = await manager.EnqueueAsync(Job());

        manager.Pause(job.Id);
        await manager.IdleAsync();
        Assert.Equal(DownloadStatus.Paused, job.Status);

        _server.Hold = null;
        manager.Resume(job.Id);
        await manager.IdleAsync();
        Assert.Equal(DownloadStatus.Done, job.Status);
    }

    [Fact]
    public async Task Going_offline_pauses_with_a_reason()
    {
        var manager = Manager();
        _session.SetWanted(false);

        var job = await manager.EnqueueAsync(Job());
        await manager.IdleAsync();

        Assert.Equal(DownloadStatus.Paused, job.Status);
        Assert.Contains("offline", job.Message);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Removing_an_unfinished_download_deletes_its_partial_file()
    {
        var manager = Manager();
        _server.CutAfter = 1000;
        var job = await manager.EnqueueAsync(Job());
        await manager.IdleAsync();
        Assert.True(File.Exists(Final + ".part"));

        await manager.RemoveAsync(job.Id);

        Assert.Empty(manager.Jobs);
        Assert.False(File.Exists(Final + ".part"));
    }

    [Fact]
    public async Task The_queue_survives_a_restart_paused()
    {
        var store = new MemoryDocumentStore();
        _server.Hold = new TaskCompletionSource();
        var first = new DownloadManager(new HttpClient(new GatedHttpHandler(_gate, new NetworkAudit(), _server)), store, _session, new HfToken(), _ => null);
        await first.EnqueueAsync(Job());

        var second = new DownloadManager(new HttpClient(_server), store, _session, new HfToken(), _ => null);
        await second.LoadAsync();

        Assert.Equal(DownloadStatus.Paused, Assert.Single(second.Jobs).Status);
        first.Dispose();
        _server.Hold.SetResult();
        await first.IdleAsync();
    }
}

public class EngineUpdaterTests
{
    private static EngineUpdater.ApiAsset Asset(string name) => new(name, 100, "sha256:" + new string('a', 64), "https://github.com/x/" + name);

    private static readonly EngineUpdater.ApiAsset[] Assets =
    [
        Asset("cudart-llama-bin-win-cuda-12.4-x64.zip"),
        Asset("cudart-llama-bin-win-cuda-13.5-x64.zip"),
        Asset("cudart-llama-bin-win-cuda-13.5-arm64.zip"),
        Asset("llama-b12318-bin-win-cpu-x64.zip"),
        Asset("llama-b12318-bin-win-cuda-12.4-x64.zip"),
        Asset("llama-b12318-bin-win-cuda-13.5-x64.zip"),
        Asset("llama-b12318-bin-win-cuda-13.5-arm64.zip"),
        Asset("llama-b12318-bin-win-vulkan-x64.zip"),
    ];

    [Fact]
    public void Picks_the_newest_cuda_build_the_driver_supports_with_its_runtime()
    {
        var pick = EngineUpdater.Pick(Assets, new Version(13, 5))!;
        Assert.Equal("llama-b12318-bin-win-cuda-13.5-x64.zip", pick.Program.Name);
        Assert.Equal("cudart-llama-bin-win-cuda-13.5-x64.zip", pick.Runtime?.Name);
        Assert.Equal(new string('a', 64), pick.Program.Sha256);
    }

    [Fact]
    public void An_older_driver_gets_the_older_cuda_build()
    {
        var pick = EngineUpdater.Pick(Assets, new Version(12, 8))!;
        Assert.Equal("llama-b12318-bin-win-cuda-12.4-x64.zip", pick.Program.Name);
        Assert.Equal("cudart-llama-bin-win-cuda-12.4-x64.zip", pick.Runtime?.Name);
    }

    [Fact]
    public void Without_an_nvidia_driver_it_picks_vulkan()
    {
        var pick = EngineUpdater.Pick(Assets, null)!;
        Assert.Equal("Vulkan", pick.Flavor);
        Assert.Null(pick.Runtime);
    }

    [Fact]
    public void Refuses_archives_that_write_outside_the_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "dogai-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zip = Path.Combine(root, "evil.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using var w = new StreamWriter(archive.CreateEntry("../escaped.txt").Open());
                w.Write("x");
            }
            Assert.Throws<HubException>(() => EngineUpdater.Extract(zip, Path.Combine(root, "out")));
            Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Installs_from_the_stable_pointer_and_swaps_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "dogai-eng-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        try
        {
            using var zipBytes = new MemoryStream();
            using (var archive = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var w = new StreamWriter(archive.CreateEntry("llama-server.exe").Open());
                w.Write("pretend program");
            }
            var program = zipBytes.ToArray();
            var sha = Convert.ToHexStringLower(SHA256.HashData(program));

            var server = new FileServer();
            server.Files["https://api.github.com/repos/ggml-org/llama.cpp/releases/latest"] = Utf8("""
                {"tag_name":"v0.9.0","published_at":"2026-11-04T13:08:06Z","assets":[{"name":"nightly-tag.txt","size":7,"digest":null,"browser_download_url":"https://github.com/r/nightly-tag.txt"}]}
                """);
            server.Files["https://github.com/r/nightly-tag.txt"] = Utf8("b12110\n");
            server.Files["https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/b12110"] = Utf8($$"""
                {"tag_name":"b12110","published_at":"2026-11-01T00:00:00Z","assets":[
                 {"name":"llama-b12110-bin-win-vulkan-x64.zip","size":{{program.Length}},"digest":"sha256:{{sha}}","browser_download_url":"https://github.com/r/v.zip"}]}
                """);
            server.Files["https://github.com/r/v.zip"] = program;

            var updater = new EngineUpdater(new HttpClient(server), paths);
            var release = await updater.LatestAsync(driverCuda: null);
            Assert.Equal("b12110", release?.Tag);

            Directory.CreateDirectory(paths.EngineDirectory);
            await File.WriteAllTextAsync(Path.Combine(paths.EngineDirectory, "old.txt"), "old");
            await updater.InstallAsync(release!, _ => { });

            Assert.True(updater.IsInstalled);
            Assert.Equal("b12110", updater.InstalledTag);
            Assert.False(File.Exists(Path.Combine(paths.EngineDirectory, "old.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s);
}

public class HuggingFaceClientTests
{
    [Fact]
    public async Task Reads_search_results_and_file_lists()
    {
        var server = new FileServer();
        server.Files["https://huggingface.co/api/models?filter=gguf&sort=downloads&direction=-1&limit=40&expand[]=downloads&expand[]=likes&expand[]=lastModified&expand[]=gated&expand[]=pipeline_tag&search=gemma"] =
            System.Text.Encoding.UTF8.GetBytes("""
                [{"_id":"x","id":"unsloth/gemma-4-12b-it-GGUF","gated":false,"lastModified":"2026-11-07T06:28:34.000Z","likes":12,"downloads":2534530,"pipeline_tag":"image-text-to-text"},
                 {"_id":"y","id":"acme/locked-GGUF","gated":"manual","likes":1,"downloads":5}]
                """);
        server.Files["https://huggingface.co/api/models/unsloth/gemma-4-12b-it-GGUF/tree/main?recursive=true"] =
            System.Text.Encoding.UTF8.GetBytes("""
                [{"type":"directory","oid":"a","size":0,"path":"MTP"},
                 {"type":"file","oid":"b","size":31650,"path":"README.md"},
                 {"type":"file","oid":"c","size":7121861440,"lfs":{"oid":"0a270ec9fe6b34f4a0d33992b6135117b484ebc4766ab76b51d4ae8c457e4c42","size":7121861440,"pointerSize":135},"path":"gemma-4-12b-it-Q4_K_M.gguf"}]
                """);
        var client = new HuggingFaceClient(new HttpClient(server), new HfToken());

        var results = await client.SearchAsync("gemma", HfSort.Downloads);
        Assert.Equal(2, results.Count);
        Assert.Equal("gemma-4-12b-it-GGUF", results[0].Name);
        Assert.False(results[0].Gated);
        Assert.True(results[1].Gated);

        var files = await client.FilesAsync("unsloth/gemma-4-12b-it-GGUF");
        Assert.Equal(2, files.Count);
        var model = files.Single(f => f.Path.EndsWith(".gguf"));
        Assert.Equal(7121861440, model.Size);
        Assert.StartsWith("0a270ec9", model.Sha256);
    }

    [Fact]
    public void Model_card_metadata_is_dropped() =>
        Assert.Equal("# Title\nBody", HuggingFaceClient.StripFrontMatter("---\nlicense: mit\ntags: [a]\n---\n\n# Title\nBody"));
}
