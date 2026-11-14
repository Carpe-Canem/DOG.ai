namespace DogAI.UI.Components.Chat;

/// <summary>How a reply's writer is shown in a thread: a name, an optional picture (a data: URL), and in group
/// chats which of the speaker colours (1 to <see cref="Tones"/>) their spoken lines take.</summary>
public sealed record SpeakerInfo(string Name, string? Avatar, int? Tone = null)
{
    /// <summary>How many speaker colours app.css defines (--say-1 … --say-6).</summary>
    public const int Tones = DogAI.Core.Text.SpeakerTones.Count;

    /// <summary>The letter shown when there is no picture.</summary>
    public string? Letter => Name.Length > 0 && char.IsLetterOrDigit(Name[0]) ? Name[..1].ToUpperInvariant() : null;
}
