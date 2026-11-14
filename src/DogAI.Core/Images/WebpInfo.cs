using System.Buffers.Binary;

namespace DogAI.Core.Images;

/// <summary>What a WebP file's own header says about it.</summary>
/// <param name="Lossless">True for lossless WebP (every pixel exact), false for the usual lossy kind.</param>
/// <param name="HasMetadata">True when the file carries an EXIF or XMP part, where a program could have written a prompt.</param>
public readonly record struct WebpInfo(int Width, int Height, bool Lossless, bool HasMetadata)
{
    /// <summary>
    /// Reads the header of a WebP file, or returns null when the bytes are not one. Only the container is read (a few
    /// dozen bytes and the part names); the picture is not decoded.
    /// </summary>
    public static WebpInfo? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8)) return null;
        // The RIFF size counts everything after its own eight bytes; a file cut short is not a picture.
        var riff = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        if (riff + 8 > (uint)data.Length) return null;

        int? width = null, height = null;
        bool? lossless = null;
        var metadata = false;
        var at = 12;
        while (at + 8 <= data.Length)
        {
            var name = data.Slice(at, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at + 4, 4));
            var body = at + 8;
            if (size > (uint)(data.Length - body)) return null;
            var chunk = data.Slice(body, (int)size);

            if (name.SequenceEqual("VP8X"u8) && chunk.Length >= 10)
            {
                // Extended file: flags, then the canvas size less one, three bytes each.
                metadata |= (chunk[0] & 0x0C) != 0;
                width = 1 + (chunk[4] | chunk[5] << 8 | chunk[6] << 16);
                height = 1 + (chunk[7] | chunk[8] << 8 | chunk[9] << 16);
            }
            else if (name.SequenceEqual("VP8 "u8) && chunk.Length >= 10)
            {
                // Lossy: three bytes of frame tag, the start code 9D 01 2A, then 14-bit width and height.
                if (chunk[3] != 0x9D || chunk[4] != 0x01 || chunk[5] != 0x2A) return null;
                lossless ??= false;
                width ??= BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(6, 2)) & 0x3FFF;
                height ??= BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(8, 2)) & 0x3FFF;
            }
            else if (name.SequenceEqual("VP8L"u8) && chunk.Length >= 5)
            {
                // Lossless: the signature 2F, then width less one and height less one, 14 bits each.
                if (chunk[0] != 0x2F) return null;
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(1, 4));
                lossless ??= true;
                width ??= 1 + (int)(bits & 0x3FFF);
                height ??= 1 + (int)(bits >> 14 & 0x3FFF);
            }
            else if (name.SequenceEqual("EXIF"u8) || name.SequenceEqual("XMP "u8))
            {
                metadata = true;
            }

            // Parts are padded to an even length.
            var next = (long)body + size + (size & 1);
            if (next > data.Length) break;
            at = (int)next;
        }

        if (width is not > 0 || height is not > 0 || lossless is null) return null;
        return new WebpInfo(width.Value, height.Value, lossless.Value, metadata);
    }
}
