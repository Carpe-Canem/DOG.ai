using System.Security.Cryptography;
using System.Text;

namespace DogAI.Hub;

/// <summary>
/// The optional Hugging Face access token, needed only for gated models. It is kept in settings
/// encrypted with Windows DPAPI, so only this Windows user on this PC can read it back.
/// </summary>
public sealed class HfToken
{
    private static readonly byte[] Entropy = "DOG-AI/hf-token"u8.ToArray();

    /// <summary>The token in plain text, held in memory only. Null when none is set.</summary>
    public string? Value { get; private set; }

    public bool IsSet => !string.IsNullOrEmpty(Value);

    /// <summary>Loads the token from its encrypted form in settings. A token that cannot be decrypted is ignored.</summary>
    public void Load(string? protectedBase64) => Value = Unprotect(protectedBase64);

    /// <summary>Sets the token and returns the encrypted form to save in settings (null to clear).</summary>
    public string? Set(string? token)
    {
        Value = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        return Protect(Value);
    }

    public static string? Protect(string? token)
    {
        if (string.IsNullOrEmpty(token) || !OperatingSystem.IsWindows()) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64) || !OperatingSystem.IsWindows()) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
