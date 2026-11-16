namespace DogAI.Core.Images;

/// <summary>What a JPEG file's own structure says about it.</summary>
/// <param name="Components">1 for a grey picture, 3 for a colour one.</param>
/// <param name="Progressive">True for a progressive file, false for the usual baseline kind.</param>
/// <param name="HasProfile">True when the file carries a colour profile (browsers add one of about 450 bytes).</param>
/// <param name="HasExtras">
/// True when the file carries anything besides the picture and a colour profile: EXIF or XMP (where a camera writes the
/// place and the date), a comment, a thumbnail, or a part DOG.ai does not know.
/// </param>
public readonly record struct JpegInfo(int Width, int Height, int Components, bool Progressive, bool HasProfile, bool HasExtras)
{
    /// <summary>
    /// Reads a JPEG's structure, or returns null when the bytes are not a whole JPEG of the kind the chat engine reads:
    /// 8 bits a sample, grey or colour, Huffman coded, from its first marker to its last with nothing after it. Every
    /// part of the file is walked (the picture data too, to find where it ends), but nothing is decoded.
    /// </summary>
    public static JpegInfo? Read(ReadOnlySpan<byte> data) => Walk(data, null);

    /// <summary>
    /// The same picture without its colour profile: the browser's encoder writes one beside the picture. Nothing else is
    /// taken out, so a file that carries a description of any kind still says so (<see cref="HasExtras"/>), for the
    /// caller to refuse. Null when the bytes are not a JPEG.
    /// </summary>
    public static byte[]? WithoutProfile(byte[] jpeg)
    {
        if (Read(jpeg) is not { } info) return null;
        if (!info.HasProfile) return jpeg;
        var plain = new List<byte>(jpeg.Length);
        return Walk(jpeg, plain) is null ? null : [.. plain];
    }

    private static bool IsProfile(ReadOnlySpan<byte> body) => body.StartsWith("ICC_PROFILE\0"u8);

    /// <summary>Walks the file part by part. With <paramref name="copy"/>, every part but a colour profile is copied there.</summary>
    private static JpegInfo? Walk(ReadOnlySpan<byte> data, List<byte>? copy)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        copy?.AddRange(data[..2]);
        var at = 2;
        int width = 0, height = 0, components = 0, frames = 0, scans = 0;
        bool progressive = false, profile = false, extras = false;
        while (true)
        {
            if (at + 2 > data.Length || data[at] != 0xFF) return null;
            var marker = data[at + 1];
            if (marker == 0xD9)
            {
                // The end of the picture is the end of the file: nothing may follow it.
                if (at + 2 != data.Length || frames != 1 || scans == 0) return null;
                copy?.AddRange(data.Slice(at, 2));
                return new JpegInfo(width, height, components, progressive, profile, extras);
            }
            // A marker that stands alone here (a restart outside picture data, a second start) is not a plain JPEG.
            if (marker is 0x00 or 0xFF or 0x01 or 0xD8 || marker is >= 0xD0 and <= 0xD7) return null;
            if (at + 4 > data.Length) return null;
            var length = data[at + 2] << 8 | data[at + 3];
            if (length < 2 || at + 2 + length > data.Length) return null;
            var body = data.Slice(at + 4, length - 2);
            var keep = true;

            switch (marker)
            {
                case 0xC0 or 0xC1 or 0xC2:
                    // The frame: baseline, extended or progressive. 8 bits, height, width, components.
                    if (body.Length < 6 || body[0] != 8) return null;
                    height = body[1] << 8 | body[2];
                    width = body[3] << 8 | body[4];
                    components = body[5];
                    if (width <= 0 || height <= 0 || components is not (1 or 3) || body.Length != 6 + 3 * components) return null;
                    progressive = marker == 0xC2;
                    frames++;
                    break;
                case 0xC4 or 0xDB or 0xDD:
                    break; // Huffman tables, quantisation tables, the restart interval: part of the picture
                case 0xDA:
                    if (frames != 1) return null;
                    scans++;
                    break;
                case 0xE0:
                    // JFIF's own header. One with a thumbnail in it carries a second picture.
                    if (body.StartsWith("JFIF\0"u8)) extras |= body.Length >= 14 && (body[12] != 0 || body[13] != 0);
                    else extras = true;
                    break;
                case 0xE2 when IsProfile(body):
                    profile = true;
                    keep = false;
                    break;
                case 0xEE when body.StartsWith("Adobe"u8):
                    break; // says how the colours are stored; needed to read the picture right
                case >= 0xE0 and <= 0xEF or 0xFE:
                    extras = true; // EXIF, XMP, a comment, anything a program could have written words into
                    break;
                default:
                    return null; // lossless, arithmetic-coded and hierarchical JPEG: the chat engine reads none of them
            }

            var end = at + 2 + length;
            if (marker == 0xDA)
            {
                // Picture data runs to the next marker; inside it FF 00 is a data byte and FF D0 to D7 are restarts.
                while (true)
                {
                    var ff = data[end..].IndexOf((byte)0xFF);
                    if (ff < 0 || end + ff + 1 >= data.Length) return null;
                    end += ff;
                    var next = data[end + 1];
                    if (next == 0x00 || next is >= 0xD0 and <= 0xD7) end += 2;
                    else if (next == 0xFF) end += 1;
                    else break;
                }
            }
            if (keep) copy?.AddRange(data[at..end]);
            at = end;
        }
    }
}
