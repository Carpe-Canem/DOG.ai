using System.Globalization;

namespace DogAI.Core.Images;

/// <summary>What the seed row says: keep the picture's own seed, take a new one, or a number the owner typed.</summary>
public enum SeedMode
{
    Keep,
    New,
    Typed,
}

/// <summary>
/// The seed row's choice (M14; the picture maker's Edit and redraw, later the Images section). <see cref="Keep"/> is the
/// default and draws with the picture's own seed, exactly as before M14. <see cref="New"/> lets the engine pick one
/// (reported back in <see cref="ImageResult.Seed"/>). A typed number is read with <see cref="Typed"/>; anything that is
/// not a whole number from 0 to <see cref="Highest"/> carries a <see cref="Problem"/> in words and must not be drawn.
/// </summary>
public sealed record SeedChoice(SeedMode Mode, long Number = -1, string? Problem = null)
{
    /// <summary>The largest seed a typed number may be: the same range the engine picks from.</summary>
    public const long Highest = uint.MaxValue;

    public static readonly SeedChoice Keep = new(SeedMode.Keep);
    public static readonly SeedChoice New = new(SeedMode.New);

    public static string ProblemText { get; } =
        $"A seed is a whole number from 0 to {Highest.ToString("N0", CultureInfo.InvariantCulture)}. Clear the box or press New seed for a random one.";

    /// <summary>
    /// What the owner typed. Spaces and commas are allowed (a copied "1,234"); a decimal point is not. An empty box is a new seed; the picture's own
    /// number is the same as Keep.
    /// </summary>
    public static SeedChoice Typed(string? text, long original)
    {
        var cleaned = new string((text ?? "").Where(c => !char.IsWhiteSpace(c) && c != ',').ToArray());
        if (cleaned.Length == 0) return New;
        if (!cleaned.All(char.IsAsciiDigit) || !long.TryParse(cleaned, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > Highest)
            return new SeedChoice(SeedMode.Typed, -1, ProblemText);
        return n == original ? Keep : new SeedChoice(SeedMode.Typed, n);
    }

    /// <summary>The seed to draw with: the original for Keep, -1 (the engine picks) for New, else the typed number.</summary>
    /// <exception cref="InvalidOperationException">The typed number has a <see cref="Problem"/>.</exception>
    public long For(long original) => Mode switch
    {
        SeedMode.Keep => original,
        SeedMode.New => -1,
        _ => Problem is null ? Number : throw new InvalidOperationException(Problem),
    };

    /// <summary>What the number box shows for this choice: the original for Keep, nothing for New, the number typed.</summary>
    public string Shown(long original) => Mode switch
    {
        SeedMode.Keep => original >= 0 ? original.ToString(CultureInfo.InvariantCulture) : "",
        SeedMode.New => "",
        _ => Number >= 0 ? Number.ToString(CultureInfo.InvariantCulture) : "",
    };
}
