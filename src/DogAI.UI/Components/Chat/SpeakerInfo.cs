namespace DogAI.UI.Components.Chat;

/// <summary>How a reply's writer is shown in a thread: a name and an optional picture (a data: URL).</summary>
public sealed record SpeakerInfo(string Name, string? Avatar)
{
    /// <summary>The letter shown when there is no picture.</summary>
    public string? Letter => Name.Length > 0 && char.IsLetterOrDigit(Name[0]) ? Name[..1].ToUpperInvariant() : null;
}
