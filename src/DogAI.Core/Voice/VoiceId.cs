namespace DogAI.Core.Voice;

/// <summary>The kinds of voice DOG AI can speak with.</summary>
public enum VoiceKind
{
    /// <summary>A voice that comes with Windows. Always there, no download.</summary>
    Windows,

    /// <summary>A speaker inside a downloaded Kokoro voice pack (one model, many voices).</summary>
    Kokoro,

    /// <summary>A downloaded Piper voice (one model, one voice).</summary>
    Piper,
}

/// <summary>
/// Names one voice, saved as text in settings, on characters and on stories:
/// "windows:Microsoft Zira Desktop", "kokoro:kokoro-multi-lang-v1_0/af_heart", "piper:vits-piper-en_US-amy-medium".
/// Downloaded voices are named by their folder inside the voice folder, so moving the models folder keeps them.
/// </summary>
public sealed record VoiceId(VoiceKind Kind, string Model, string? Speaker = null)
{
    public override string ToString() => Kind switch
    {
        VoiceKind.Windows => "windows:" + Model,
        VoiceKind.Kokoro => $"kokoro:{Model}/{Speaker}",
        _ => "piper:" + Model,
    };

    /// <summary>Reads a saved voice name. Null for empty or unreadable text, which means "the default voice".</summary>
    public static VoiceId? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return null;
        var rest = text[(colon + 1)..].Trim();
        switch (text[..colon].Trim().ToLowerInvariant())
        {
            case "windows":
                return new VoiceId(VoiceKind.Windows, rest);
            case "piper":
                return rest.Contains('/') || rest.Contains('\\') ? null : new VoiceId(VoiceKind.Piper, rest);
            case "kokoro":
                var slash = rest.LastIndexOf('/');
                if (slash <= 0 || slash == rest.Length - 1) return null;
                var folder = rest[..slash];
                return folder.Contains('/') || folder.Contains('\\') ? null : new VoiceId(VoiceKind.Kokoro, folder, rest[(slash + 1)..]);
            default:
                return null;
        }
    }
}

/// <summary>A voice that can be picked, with the words shown for it.</summary>
/// <param name="Group">Heading in pickers: "Windows", or the voice pack's name.</param>
public sealed record VoiceOption(VoiceId Id, string Label, string Group, string? Language = null);
