using System.Security.Cryptography;
using System.Text;

namespace DogAI.Core.Privacy;

/// <summary>
/// The key the owner prints or writes down when turning the lock on. It opens the data when the password is forgotten.
/// 24 letters and digits (120 random bits), shown in groups of four. Letters and digits that look alike on paper
/// (I and 1, O and 0) are never used.
/// </summary>
public static class RecoveryKey
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 24;
    public const int GroupSize = 4;

    /// <summary>A new random key, without separators.</summary>
    public static string New() => new(RandomNumberGenerator.GetItems<char>(Alphabet, Length));

    /// <summary>The key as shown to the owner: XXXX-XXXX-XXXX-XXXX-XXXX-XXXX.</summary>
    public static string Format(string key)
    {
        var text = new StringBuilder(key.Length + key.Length / GroupSize);
        for (var i = 0; i < key.Length; i++)
        {
            if (i > 0 && i % GroupSize == 0) text.Append('-');
            text.Append(key[i]);
        }
        return text.ToString();
    }

    /// <summary>
    /// Reads a key back as the owner typed it: small letters, spaces, dashes and other separators are forgiven.
    /// Null when what is left is not a key (wrong length, or a character a key never has).
    /// </summary>
    public static string? Read(string? typed)
    {
        if (typed is null) return null;
        var key = new StringBuilder(Length);
        foreach (var c in typed)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '_' or '.' or ',' or '‐' or '‑' or '‒' or '–' or '—') continue;
            var upper = char.ToUpperInvariant(c);
            if (!Alphabet.Contains(upper) || key.Length == Length) return null;
            key.Append(upper);
        }
        return key.Length == Length ? key.ToString() : null;
    }
}
