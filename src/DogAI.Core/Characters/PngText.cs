using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace DogAI.Core.Characters;

/// <summary>Reads and writes the text chunks of a PNG file, which is where character cards keep their data.</summary>
public static class PngText
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static bool IsPng(ReadOnlySpan<byte> bytes) => bytes.Length >= 8 && bytes[..8].SequenceEqual(Signature);

    /// <summary>The uncompressed text entries (tEXt and iTXt), by keyword. Later entries win.</summary>
    public static Dictionary<string, string> ReadText(byte[] png)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, start, length) in Chunks(png))
        {
            var data = png.AsSpan(start, length);
            if (type == "tEXt")
            {
                var zero = data.IndexOf((byte)0);
                if (zero <= 0) continue;
                result[Encoding.Latin1.GetString(data[..zero])] = Encoding.Latin1.GetString(data[(zero + 1)..]);
            }
            else if (type == "iTXt")
            {
                // keyword \0 compressed-flag method language \0 translated-keyword \0 text
                var zero = data.IndexOf((byte)0);
                if (zero <= 0 || zero + 3 > data.Length || data[zero + 1] != 0) continue;
                var rest = data[(zero + 3)..];
                var lang = rest.IndexOf((byte)0);
                if (lang < 0) continue;
                rest = rest[(lang + 1)..];
                var translated = rest.IndexOf((byte)0);
                if (translated < 0) continue;
                result[Encoding.Latin1.GetString(data[..zero])] = Encoding.UTF8.GetString(rest[(translated + 1)..]);
            }
        }
        return result;
    }

    /// <summary>
    /// A copy of <paramref name="png"/> with any text entries under the given keywords removed and the new ones
    /// added just before the end of the file. Values must be Latin-1 (cards use base64, which is).
    /// </summary>
    public static byte[] WithText(byte[] png, IReadOnlyDictionary<string, string> entries)
    {
        if (!IsPng(png)) throw new InvalidDataException("That picture is not a PNG file.");
        using var output = new MemoryStream(png.Length + entries.Sum(e => e.Value.Length + 64));
        output.Write(Signature);
        var wroteText = false;
        foreach (var (type, start, length) in Chunks(png))
        {
            if (type is "tEXt" or "iTXt" or "zTXt" && entries.ContainsKey(Keyword(png.AsSpan(start, length)))) continue;
            if (type == "IEND")
            {
                foreach (var (key, value) in entries)
                    WriteChunk(output, "tEXt", [.. Encoding.Latin1.GetBytes(key), 0, .. Encoding.Latin1.GetBytes(value)]);
                wroteText = true;
            }
            output.Write(png, start - 8, length + 12);
        }
        if (!wroteText) throw new InvalidDataException("That PNG file is incomplete.");
        return output.ToArray();
    }

    /// <summary>A plain one-colour square PNG, used when a card has no picture of its own.</summary>
    public static byte[] SolidSquare(int size, byte r, byte g, byte b)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8;  // bit depth
        header[9] = 2;  // colour type: RGB

        var row = new byte[1 + size * 3];
        for (var x = 0; x < size; x++) (row[1 + x * 3], row[2 + x * 3], row[3 + x * 3]) = (r, g, b);
        using var pixels = new MemoryStream();
        using (var z = new ZLibStream(pixels, CompressionLevel.Optimal, leaveOpen: true))
            for (var y = 0; y < size; y++) z.Write(row);

        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", pixels.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Each chunk's type and where its data sits. Stops quietly at the first damaged chunk.</summary>
    private static IEnumerable<(string Type, int Start, int Length)> Chunks(byte[] png)
    {
        if (!IsPng(png)) throw new InvalidDataException("That file is not a PNG picture.");
        var pos = 8;
        while (pos + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            if (length < 0 || pos + 12L + length > png.Length) yield break;
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            yield return (type, pos + 8, length);
            if (type == "IEND") yield break;
            pos += 12 + length;
        }
    }

    private static string Keyword(ReadOnlySpan<byte> data)
    {
        var zero = data.IndexOf((byte)0);
        return zero <= 0 ? "" : Encoding.Latin1.GetString(data[..zero]);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32.Append(Crc32.Append(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
        {
            var c = (uint)n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        public static uint Append(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
