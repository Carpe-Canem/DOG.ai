using System.Text;

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
        key.EndsWith(".block_count", StringComparison.Ordinal);

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
