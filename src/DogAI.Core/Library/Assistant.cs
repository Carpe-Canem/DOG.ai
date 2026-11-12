namespace DogAI.Core.Library;

/// <summary>
/// A reusable way to start a General chat: standing instructions, a style, and optionally a model.
/// Starting a chat from an assistant copies these into the chat, so changing an assistant later never rewrites old chats.
/// </summary>
public sealed record Assistant
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>One line shown in the list.</summary>
    public string Description { get; init; } = "";

    /// <summary>Standing instructions sent ahead of the chat.</summary>
    public string Instructions { get; init; } = "";

    /// <summary>The style (sampler preset) the chat starts with. Empty uses the default style.</summary>
    public string PresetId { get; init; } = "";

    /// <summary>The model file this assistant likes, or null for whichever model is loaded.</summary>
    public string? ModelPath { get; init; }
}

/// <summary>A saved prompt with fill-in blanks, written as [[name]] or [[name|default]] (see <see cref="PromptVariables"/>).</summary>
public sealed record PromptEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Text { get; init; } = "";
}
