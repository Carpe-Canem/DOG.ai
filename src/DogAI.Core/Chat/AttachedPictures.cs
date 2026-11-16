using System.Globalization;
using DogAI.Core.Images;

namespace DogAI.Core.Chat;

/// <summary>
/// A picture the owner put on a chat message for the model to look at (M17). A row of its own table, not of
/// <c>Pictures</c> (those are pictures DOG.ai drew, with their settings). It lives only in the database, so the lock,
/// backups and a portable move cover it, and it goes with its message. <see cref="Image"/> is the last column on
/// purpose: SQLite walks a large value's pages to reach any column behind it.
/// </summary>
public sealed class AttachedPicture
{
    public long Id { get; set; }
    public long MessageId { get; set; }

    /// <summary>The message's chat, so a chat's pictures are found without reading its messages.</summary>
    public long ConversationId { get; set; }

    /// <summary>Place among the message's pictures, from 0.</summary>
    public int Position { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>The size of <see cref="Image"/> in bytes, readable without it.</summary>
    public int Bytes { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// The picture: a JPEG that passed <see cref="AttachedPictures.Problem"/>. These exact bytes are what the model is
    /// sent, every time. Null in lists (<see cref="ChatMessage.Pictures"/>), which leave it out.
    /// </summary>
    public byte[]? Image { get; set; }
}

/// <summary>The rules for pictures on a message: what may be kept and sent, what one costs, and the words that stand in for one.</summary>
public static class AttachedPictures
{
    /// <summary>The most pictures one message carries.</summary>
    public const int MaxPerMessage = 4;

    /// <summary>The longest side, in pixels. The page reduces a larger picture before it reaches Core (owner's decision, 2026-11-16).</summary>
    public const int MaxSide = 1024;

    /// <summary>The JPEG quality the page writes pictures at.</summary>
    public const int Quality = 90;

    /// <summary>The most bytes a picture may have. Noise at 1,024 × 1,024 and quality 90 is about 0.9 MB.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>What the chat engine is told a picture may cost at most (<c>--image-max-tokens</c>).</summary>
    public const int EngineMaxTokens = 1024;

    /// <summary>The most a picture costs by <see cref="TokensByRule"/>: 32 × 32 patches and two markers.</summary>
    public const int MaxTokens = 1026;

    /// <summary>What a text-only job gets where a picture was: summaries, recalled messages, exports, a story made from a chat.</summary>
    public const string TextNote = "[picture]";

    /// <summary>
    /// What stands for a message that is pictures alone where the pictures are left out and nothing may be in brackets
    /// (Write mine: a bracketed note makes the model write one too).
    /// </summary>
    public const string PictureOnly = "Look at this.";

    /// <summary>What a model that cannot see pictures is given in a picture's place.</summary>
    public const string UnseenNote = "[The user attached a picture here. You cannot see pictures, so say so if it matters.]";

    /// <summary>The size to reduce a picture of this size to: the same shape, the longest side at most <see cref="MaxSide"/>. Never enlarged.</summary>
    public static (int Width, int Height) SizeFor(int width, int height)
    {
        if (width <= 0 || height <= 0) return (0, 0);
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// The picture as it is kept: without the colour profile the browser's encoder adds. Anything else comes back
    /// unchanged, for <see cref="Problem"/> to judge.
    /// </summary>
    public static byte[] Plain(byte[] jpeg) => JpegInfo.WithoutProfile(jpeg) ?? jpeg;

    /// <summary>
    /// What stops these bytes from being a picture on a message, in plain words, or null. Asked before a picture is
    /// stored and again before it is sent: the chat engine is given nothing but a JPEG that passes (on bytes it cannot
    /// read it tries to start another program).
    /// </summary>
    public static string? Problem(byte[]? jpeg)
    {
        if (jpeg is null || JpegInfo.Read(jpeg) is not { } info) return "The picture is not a JPEG image.";
        if (jpeg.Length > MaxBytes) return "The picture is too large.";
        if (info.Width > MaxSide || info.Height > MaxSide) return $"A picture is at most {MaxSide:N0} pixels on its longest side.";
        // Where a camera writes the place and the date, and where a program could have written anything.
        if (info.HasProfile || info.HasExtras) return "The picture carries extra information besides the picture.";
        return null;
    }

    /// <summary>The size of a picture that passed <see cref="Problem"/>.</summary>
    public static (int Width, int Height) SizeOf(byte[] jpeg) => JpegInfo.Read(jpeg) is { } info ? (info.Width, info.Height) : (0, 0);

    /// <summary>
    /// What a picture costs in tokens when the engine cannot be asked: round(width ÷ 32) × round(height ÷ 32) + 2, halves
    /// to the even number, at least 9 + 2 and at most <see cref="MaxTokens"/>. The rule of the Qwen3.5 family (seen in
    /// the trial, 2026-11-16); the engine's own count is used whenever a model is loaded.
    /// </summary>
    public static int TokensByRule(int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        var across = Math.Max(1, (int)Math.Round(width / 32.0, MidpointRounding.ToEven));
        var down = Math.Max(1, (int)Math.Round(height / 32.0, MidpointRounding.ToEven));
        return Math.Clamp(across * down, 9, MaxTokens - 2) + 2;
    }

    /// <summary>What the prompt inspector shows where a picture is: its size and what it costs.</summary>
    public static string Marker(int width, int height, int tokens) =>
        $"[picture {width.ToString("N0", CultureInfo.InvariantCulture)} × {height.ToString("N0", CultureInfo.InvariantCulture)}, {tokens.ToString("N0", CultureInfo.InvariantCulture)} tokens]";

    /// <summary>A message's text for a text-only job: <see cref="TextNote"/> once per picture, then what was typed.</summary>
    public static string TextOf(ChatMessage message) => WithNotes(message.Content, message.Pictures.Count, TextNote);

    /// <summary>A message's text for a model that cannot see: <see cref="UnseenNote"/> once per picture, then what was typed.</summary>
    /// <param name="pictures">How many of its pictures get a note; all of them when null.</param>
    public static string UnseenTextOf(ChatMessage message, int? pictures = null) => WithNotes(message.Content, pictures ?? message.Pictures.Count, UnseenNote);

    private static string WithNotes(string content, int pictures, string note)
    {
        if (pictures <= 0) return content;
        var notes = string.Join(" ", Enumerable.Repeat(note, pictures));
        return content.Trim().Length == 0 ? notes : notes + "\n" + content;
    }
}

/// <summary>Where the pictures on messages are kept. The only way one reaches the disk.</summary>
public interface IAttachedPictureStore
{
    /// <summary>
    /// Keeps pictures on a message, after any it has, in the order given. Every one must pass
    /// <see cref="AttachedPictures.Problem"/> and the message may not end up with more than
    /// <see cref="AttachedPictures.MaxPerMessage"/>; otherwise nothing is kept.
    /// </summary>
    /// <returns>The new rows, without their images.</returns>
    Task<IReadOnlyList<AttachedPicture>> AddAsync(long messageId, IReadOnlyList<byte[]> jpegs, CancellationToken ct = default);

    /// <summary>
    /// Gives a message the same pictures another has, byte for byte (an edited question keeps its pictures): all of
    /// them, or only those in <paramref name="only"/>, in their order.
    /// </summary>
    Task<IReadOnlyList<AttachedPicture>> CopyAsync(long fromMessageId, long toMessageId, IReadOnlyCollection<long>? only = null, CancellationToken ct = default);

    /// <summary>One picture with its image, or null.</summary>
    Task<AttachedPicture?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>The images of these pictures, by id. Ids that no longer exist are left out.</summary>
    Task<IReadOnlyDictionary<long, byte[]>> ImagesAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);
}
