using System.Net;
using System.Text;
using DogAI.Core.Net;
using DogAI.Engine;

namespace DogAI.Tests;

public class GgufReaderTests
{
    /// <summary>Writes a small but valid GGUF header by hand.</summary>
    private static MemoryStream Header(Action<BinaryWriter>? extra = null, int pairs = 6, uint version = 3)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes("GGUF"));
        w.Write(version);
        w.Write(0UL);
        w.Write((ulong)pairs);

        Str(w, "general.architecture"); w.Write(8u); Str(w, "llama");
        Str(w, "general.name"); w.Write(8u); Str(w, "Test Model 7B");
        Str(w, "llama.context_length"); w.Write(4u); w.Write(32768u);
        Str(w, "llama.block_count"); w.Write(4u); w.Write(32u);
        // A string array (like the vocabulary) that the reader has to step over.
        Str(w, "tokenizer.ggml.tokens"); w.Write(9u); w.Write(8u); w.Write(3UL); Str(w, "a"); Str(w, "bb"); Str(w, "ccc");
        Str(w, "tokenizer.chat_template"); w.Write(8u); Str(w, "{{ messages }}");
        extra?.Invoke(w);
        w.Flush();
        stream.Position = 0;
        return stream;
    }

    private static void Str(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        w.Write((ulong)bytes.Length);
        w.Write(bytes);
    }

    [Fact]
    public void Reads_name_architecture_and_context_length()
    {
        var info = GgufReader.Read(Header());
        Assert.Equal("Test Model 7B", info.Name);
        Assert.Equal("llama", info.Architecture);
        Assert.Equal(32768, info.ContextLength);
        Assert.Equal(32, info.BlockCount);
        Assert.True(info.HasChatTemplate);
    }

    [Fact]
    public void Steps_over_number_arrays_and_other_value_types()
    {
        var info = GgufReader.Read(Header(w =>
        {
            Str(w, "some.floats"); w.Write(9u); w.Write(6u); w.Write(2UL); w.Write(1f); w.Write(2f);
            Str(w, "some.flag"); w.Write(7u); w.Write((byte)1);
            Str(w, "general.size_label"); w.Write(8u); Str(w, "7B");
        }, pairs: 9));
        Assert.Equal("7B", info.SizeLabel);
    }

    [Fact]
    public void Rejects_files_that_are_not_gguf()
    {
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(new MemoryStream("not a model at all"u8.ToArray())));
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(Header(version: 1)));
        Assert.Null(GgufReader.TryRead(Path.Combine(Path.GetTempPath(), "does-not-exist.gguf")));
    }

    [Fact]
    public void Rejects_a_header_that_is_cut_short()
    {
        var bytes = Header().ToArray();
        Assert.Throws<InvalidDataException>(() => GgufReader.Read(new MemoryStream(bytes[..40])));
    }
}

public class ModelLibraryTests
{
    [Fact]
    public void Lists_models_but_not_vision_addons_or_later_split_parts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dogai-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            foreach (var name in new[] { "b-model.gguf", "sub/a-model.gguf", "mmproj-vision.gguf", "big-00001-of-00002.gguf", "big-00002-of-00002.gguf", "notes.txt" })
                File.WriteAllText(Path.Combine(dir, name), "x");

            Assert.Equal(["a-model", "b-model", "big-00001-of-00002"], ModelLibrary.Scan(dir).Select(m => m.Name));
            Assert.Empty(ModelLibrary.Scan(Path.Combine(dir, "missing")));
            Assert.Empty(ModelLibrary.Scan(null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class LoopbackGuardTests
{
    private sealed class Stub : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/health", true)]
    [InlineData("https://127.0.0.1:8080/health", false)]
    [InlineData("http://localhost:8080/health", false)]
    [InlineData("http://192.168.1.10:8080/", false)]
    [InlineData("http://127.0.0.1.example.com/", false)]
    [InlineData("https://huggingface.co/", false)]
    public async Task Engine_client_only_talks_to_this_computer(string url, bool allowed)
    {
        var stub = new Stub();
        var client = new HttpClient(new LoopbackOnlyHandler(stub));

        if (allowed)
        {
            await client.GetAsync(url);
            Assert.Equal(1, stub.Calls);
        }
        else
        {
            await Assert.ThrowsAsync<HostNotAllowedException>(() => client.GetAsync(url));
            Assert.Equal(0, stub.Calls);
        }
    }
}
