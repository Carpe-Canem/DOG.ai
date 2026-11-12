using System.Text;
using DogAI.Core.Models;

namespace DogAI.Engine;

/// <summary>The facts DOG AI reads from a model file's header.</summary>
public sealed record GgufInfo
{
    public string? Name { get; init; }
    public string? Architecture { get; init; }

    /// <summary>The context window the model was trained for, in tokens.</summary>
    public int? ContextLength { get; init; }
    public int? BlockCount { get; init; }

    /// <summary>Parameter count as the author labelled it, e.g. "12B".</summary>
    public string? SizeLabel { get; init; }
    public bool HasChatTemplate { get; init; }

    public int? EmbeddingLength { get; init; }
    public int? HeadCount { get; init; }
    public int? HeadCountKv { get; init; }
    public int? KeyLength { get; init; }
    public int? ValueLength { get; init; }
    public int? ExpertCount { get; init; }
    public int? ExpertUsedCount { get; init; }

    /// <summary>How the model pools token vectors into one; set only by embedding models.</summary>
    public int? PoolingType { get; init; }

    /// <summary>
    /// A document search (embedding) model rather than one that writes: it says how it pools, or its name or architecture
    /// says so (BERT-family models only make embeddings).
    /// </summary>
    public bool IsEmbedding(string path) =>
        PoolingType is > 0
        || Architecture is { } a && (a.Contains("bert", StringComparison.OrdinalIgnoreCase) || a.Equals("t5encoder", StringComparison.OrdinalIgnoreCase))
        || DisplayName(path).Contains("embed", StringComparison.OrdinalIgnoreCase)
        || System.IO.Path.GetFileName(path).Contains("embed", StringComparison.OrdinalIgnoreCase);

    /// <summary>The model's own name, or the file name when the header has none or only punctuation.</summary>
    public string DisplayName(string path) =>
        Name is { } n && n.Any(char.IsLetterOrDigit) ? n.Trim() : System.IO.Path.GetFileNameWithoutExtension(path);

    /// <summary>Context cache per token of context, or null when the header lacks the numbers.</summary>
    public long? KvBytesPerToken
    {
        get
        {
            var key = KeyLength ?? (EmbeddingLength is > 0 && HeadCount is > 0 ? EmbeddingLength / HeadCount : null);
            return ModelShape.KvPerToken(BlockCount, HeadCountKv ?? HeadCount, key, ValueLength ?? key);
        }
    }

    /// <summary>The facts the fit estimate needs, for a model whose weights take <paramref name="weightBytes"/>.</summary>
    public ModelShape ToShape(long weightBytes) => new()
    {
        WeightBytes = weightBytes,
        Layers = BlockCount,
        KvBytesPerToken = KvBytesPerToken,
        TrainedContext = ContextLength,
        ExpertCount = ExpertCount,
        ExpertsUsed = ExpertUsedCount,
    };
}

/// <summary>Reads the metadata at the start of a GGUF model file without loading the model.</summary>
public static class GgufReader
{
    private const uint Magic = 0x46554747; // "GGUF"
    private const int MaxStringBytes = 64 * 1024 * 1024;

    /// <summary>Returns null when the file is missing, unreadable or not a GGUF file.</summary>
    public static GgufInfo? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            return Read(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    public static GgufInfo Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            if (reader.ReadUInt32() != Magic) throw new InvalidDataException("Not a GGUF file.");
            var version = reader.ReadUInt32();
            if (version is < 2 or > 3) throw new InvalidDataException($"Unsupported GGUF version {version}.");
            reader.ReadUInt64(); // tensor count
            var pairs = reader.ReadUInt64();

            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            for (ulong i = 0; i < pairs; i++)
            {
                var key = ReadString(reader);
                var value = ReadValue(reader, reader.ReadUInt32(), keep: IsWanted(key));
                if (value is not null) values[key] = value;
            }

            var arch = values.GetValueOrDefault("general.architecture") as string;
            return new GgufInfo
            {
                Name = values.GetValueOrDefault("general.name") as string,
                Architecture = arch,
                SizeLabel = values.GetValueOrDefault("general.size_label") as string,
                ContextLength = ToInt(values.GetValueOrDefault($"{arch}.context_length")),
                BlockCount = ToInt(values.GetValueOrDefault($"{arch}.block_count")),
                HasChatTemplate = values.ContainsKey("tokenizer.chat_template"),
                EmbeddingLength = ToInt(values.GetValueOrDefault($"{arch}.embedding_length")),
                HeadCount = ToInt(values.GetValueOrDefault($"{arch}.attention.head_count")),
                HeadCountKv = ToInt(values.GetValueOrDefault($"{arch}.attention.head_count_kv")),
                KeyLength = ToInt(values.GetValueOrDefault($"{arch}.attention.key_length")),
                ValueLength = ToInt(values.GetValueOrDefault($"{arch}.attention.value_length")),
                ExpertCount = ToInt(values.GetValueOrDefault($"{arch}.expert_count")),
                ExpertUsedCount = ToInt(values.GetValueOrDefault($"{arch}.expert_used_count")),
                PoolingType = ToInt(values.GetValueOrDefault($"{arch}.pooling_type")),
            };
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("The GGUF header is cut short.");
        }
    }

    private static bool IsWanted(string key) =>
        key.StartsWith("general.", StringComparison.Ordinal) ||
        key == "tokenizer.chat_template" ||
        key.EndsWith(".context_length", StringComparison.Ordinal) ||
        key.EndsWith(".block_count", StringComparison.Ordinal) ||
        key.EndsWith(".embedding_length", StringComparison.Ordinal) ||
        key.EndsWith(".attention.head_count", StringComparison.Ordinal) ||
        key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal) ||
        key.EndsWith(".attention.key_length", StringComparison.Ordinal) ||
        key.EndsWith(".attention.value_length", StringComparison.Ordinal) ||
        key.EndsWith(".expert_count", StringComparison.Ordinal) ||
        key.EndsWith(".expert_used_count", StringComparison.Ordinal) ||
        key.EndsWith(".pooling_type", StringComparison.Ordinal);

    private static int? ToInt(object? value) => value switch
    {
        null or string or bool => null,
        IConvertible number => (int)Math.Clamp(number.ToDouble(null), 0, int.MaxValue),
        _ => null,
    };

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        if (length > MaxStringBytes) throw new InvalidDataException("Implausible string length in GGUF header.");
        var bytes = reader.ReadBytes((int)length);
        if (bytes.Length != (int)length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes);
    }

    private static object? ReadValue(BinaryReader reader, uint type, bool keep)
    {
        switch (type)
        {
            case 0: return reader.ReadByte();
            case 1: return reader.ReadSByte();
            case 2: return reader.ReadUInt16();
            case 3: return reader.ReadInt16();
            case 4: return reader.ReadUInt32();
            case 5: return reader.ReadInt32();
            case 6: return reader.ReadSingle();
            case 7: return reader.ReadByte() != 0;
            case 8:
                if (keep) return ReadString(reader);
                Skip(reader, reader.ReadUInt64());
                return null;
            case 9:
                // Arrays (the vocabulary, mostly) are never needed here, only stepped over.
                var elementType = reader.ReadUInt32();
                var count = reader.ReadUInt64();
                var size = FixedSize(elementType);
                if (size > 0) Skip(reader, count * (ulong)size);
                else for (ulong i = 0; i < count; i++) ReadValue(reader, elementType, keep: false);
                return null;
            case 10: return reader.ReadUInt64();
            case 11: return reader.ReadInt64();
            case 12: return reader.ReadDouble();
            default: throw new InvalidDataException($"Unknown GGUF value type {type}.");
        }
    }

    private static int FixedSize(uint type) => type switch
    {
        0 or 1 or 7 => 1,
        2 or 3 => 2,
        4 or 5 or 6 => 4,
        10 or 11 or 12 => 8,
        _ => 0,
    };

    private static void Skip(BinaryReader reader, ulong bytes)
    {
        var stream = reader.BaseStream;
        if (stream.CanSeek)
        {
            if (bytes > (ulong)(stream.Length - stream.Position)) throw new EndOfStreamException();
            stream.Seek((long)bytes, SeekOrigin.Current);
            return;
        }
        Span<byte> scratch = stackalloc byte[4096];
        while (bytes > 0)
        {
            var read = stream.Read(scratch[..(int)Math.Min(bytes, (ulong)scratch.Length)]);
            if (read == 0) throw new EndOfStreamException();
            bytes -= (ulong)read;
        }
    }
}
