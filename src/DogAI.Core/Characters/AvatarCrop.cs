using System.Buffers.Binary;

namespace DogAI.Core.Characters;

/// <summary>The square of a picture used as an avatar, in the picture's own pixels.</summary>
public readonly record struct AvatarCrop(int X, int Y, int Side)
{
    /// <summary>Smallest square that can be chosen, so a crop never becomes a few blurry pixels.</summary>
    public const int MinSide = 32;

    /// <summary>The square used when nobody chose one: centred across, a little below the top, where faces usually are.</summary>
    public static AvatarCrop Default(int width, int height)
    {
        var side = Math.Min(width, height);
        return new AvatarCrop((width - side) / 2, (height - side) / 4, side);
    }

    /// <summary>The same square moved and shrunk as needed to lie inside a <paramref name="width"/> by <paramref name="height"/> picture.</summary>
    public AvatarCrop Within(int width, int height)
    {
        var side = Math.Clamp(Side, Math.Min(MinSide, Math.Min(width, height)), Math.Max(1, Math.Min(width, height)));
        return new AvatarCrop(Math.Clamp(X, 0, width - side), Math.Clamp(Y, 0, height - side), side);
    }

    /// <summary>Resizes the square around its centre, keeping it inside the picture.</summary>
    public AvatarCrop Resized(int side, int width, int height)
    {
        var cx = X + Side / 2;
        var cy = Y + Side / 2;
        return new AvatarCrop(cx - side / 2, cy - side / 2, side).Within(width, height);
    }

    /// <summary>Width and height of a PNG, read from its header; null when the bytes are not a PNG.</summary>
    public static (int Width, int Height)? PngSize(byte[] png)
    {
        // Signature (8 bytes), then the IHDR chunk: length (4), type (4), width (4), height (4).
        if (!PngText.IsPng(png) || png.Length < 24) return null;
        var w = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
        var h = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
        return w > 0 && h > 0 ? (w, h) : null;
    }
}
