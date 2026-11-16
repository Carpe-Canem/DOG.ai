using System.Text;

namespace DogAI.Tests;

/// <summary>
/// JPEG files for tests. <see cref="Make"/> writes one that is whole as far as its structure goes (which is all DOG.ai
/// reads: it never decodes a picture); the files in the Pictures folder are real ones, for the real engine.
/// </summary>
internal static class TestJpegs
{
    /// <summary>Bytes that appear nowhere by chance, put inside the test pictures themselves.</summary>
    public static readonly byte[] Marker = Encoding.ASCII.GetBytes("\u0001ATTACHED-PICTURE-BYTES-BANDICOOT\u0002");

    /// <summary>One part of a JPEG file: FF, its marker, its length, its body.</summary>
    public static byte[] Segment(byte marker, params byte[] body) =>
        [0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];

    public static byte[] Jfif(byte thumbWidth = 0, byte thumbHeight = 0) =>
        Segment(0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, thumbWidth, thumbHeight]);

    public static byte[] Profile() => Segment(0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, .. new byte[40]]);

    public static byte[] Exif() => Segment(0xE1, [.. "Exif\0\0"u8, .. "GPS 51.5N 0.1W"u8]);

    public static byte[] Comment(string words) => Segment(0xFE, Encoding.ASCII.GetBytes(words));

    /// <param name="extra">Parts written after the JFIF header: a colour profile, EXIF, a comment.</param>
    /// <param name="frame">The frame's marker: C0 baseline, C2 progressive.</param>
    public static byte[] Make(int width, int height, int seed = 1, int filler = 3000, byte[]? marker = null, byte frame = 0xC0, int components = 3,
        int bits = 8, byte[]? after = null, params byte[][] extra)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        stream.Write(Jfif());
        foreach (var part in extra) stream.Write(part);
        stream.Write(Segment(0xDB, new byte[65]));
        byte[] sof = [(byte)bits, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)components,
            .. Enumerable.Range(1, components).SelectMany(i => new byte[] { (byte)i, 0x11, 0 })];
        stream.Write(Segment(frame, sof));
        stream.Write(Segment(0xC4, new byte[20]));
        stream.Write(Segment(0xDA, [(byte)components, .. Enumerable.Range(1, components).SelectMany(i => new byte[] { (byte)i, 0 }), 0, 63, 0]));
        // Picture data: anything but a marker. A stuffed FF 00 and a restart are in it, as real files have them.
        var data = new byte[filler];
        new Random(seed).NextBytes(data);
        for (var i = 0; i < data.Length; i++)
            if (data[i] == 0xFF) data[i] = 0xFE;
        (marker ?? Marker).CopyTo(data.AsSpan(filler / 2));
        stream.Write(data);
        stream.Write([0xFF, 0x00, 0x12, 0xFF, 0xD3, 0x34]);
        stream.Write([0xFF, 0xD9]);
        if (after is not null) stream.Write(after);
        return stream.ToArray();
    }

    /// <summary>A real JPEG from the Pictures folder (made with Windows' own encoder at quality 90).</summary>
    public static byte[] Real(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Pictures", name));

    public const string Red = "red-256x160.jpg", Sign = "sign-1024x640.jpg", Yellow = "yellow-1024.jpg";
}
