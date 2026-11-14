namespace DogAI.UI.Components;

public sealed record ModeInfo(string Id, string Label, string Headline, string Blurb);

/// <summary>The four top-level areas of the app. The Id is also the route and the icon name.</summary>
public static class Modes
{
    public static IReadOnlyList<ModeInfo> All { get; } =
    [
        new("general", "General", "Ask and get help", "Questions, code, writing, documents."),
        new("characters", "Characters", "Talk to a character", "Your cast, imported cards, group scenes."),
        new("story", "Story", "Tell a story", "Write one together or play it as an adventure."),
        new("models", "Models", "Get models", "Browse and download what fits your PC."),
    ];
}
