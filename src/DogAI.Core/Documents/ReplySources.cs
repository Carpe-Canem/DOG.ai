using System.Text.Json;
using DogAI.Core.Chat;

namespace DogAI.Core.Documents;

/// <summary>One passage that was in a reply's prompt, numbered as the model saw it ("[2]").</summary>
public sealed record SourcePassage(int Number, long ChunkId, long DocumentId, string FileName, int? Page, string? Heading, float Similarity)
{
    /// <summary>"report.pdf, page 3" or "notes.md › Setup".</summary>
    public string Label => FileName + (Page is { } p ? $", page {p}" : "") + (Heading is { Length: > 0 } h ? " › " + h : "");
}

/// <summary>
/// What a reply was given besides the chat: passages from documents and earlier messages recalled from a long chat.
/// Kept with the reply (<see cref="ChatMessage.SourcesJson"/>) for the sources list and the prompt inspector.
/// </summary>
public sealed class ReplySources
{
    public List<SourcePassage> Passages { get; set; } = [];

    /// <summary>Ids of earlier messages that were recalled.</summary>
    public List<long> Recalled { get; set; } = [];

    /// <summary>Why documents or recall were not used when they would have been, in plain words. Null when all went well.</summary>
    public string? Note { get; set; }

    public bool IsEmpty => Passages.Count == 0 && Recalled.Count == 0 && Note is null;

    public string? ToJson() => IsEmpty ? null : JsonSerializer.Serialize(this);

    public static ReplySources? Of(ChatMessage message)
    {
        if (string.IsNullOrEmpty(message.SourcesJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<ReplySources>(message.SourcesJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
