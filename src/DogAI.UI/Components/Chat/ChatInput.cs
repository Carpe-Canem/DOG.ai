namespace DogAI.UI.Components.Chat;

/// <summary>What the chat box sends where it takes pictures: the trimmed text and the pictures waiting, each a JPEG made in the page.</summary>
public sealed record ChatInput(string Text, IReadOnlyList<byte[]> Pictures);
