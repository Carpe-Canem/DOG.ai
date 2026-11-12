namespace DogAI.Core.Chat;

public enum ChatRole
{
    User = 0,
    Assistant = 1,
}

/// <summary>One chat. Its messages form a tree; <see cref="ActiveLeafId"/> says which branch is on screen.</summary>
public sealed class Conversation
{
    public long Id { get; set; }

    /// <summary>Which area of the app owns it: "general" now, "characters" and "story" later.</summary>
    public string Mode { get; set; } = "general";

    public string Title { get; set; } = "New chat";

    /// <summary>Standing instructions sent ahead of the conversation. Empty means none.</summary>
    public string SystemPrompt { get; set; } = "";

    public string PresetId { get; set; } = SamplerPresets.DefaultId;

    /// <summary>The last message of the branch currently shown. Null while the chat is empty.</summary>
    public long? ActiveLeafId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One turn. Messages that share a parent are alternatives to each other: regenerating a reply
/// or editing a question adds a sibling instead of overwriting anything.
/// </summary>
public sealed class ChatMessage
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public long? ParentId { get; set; }
    public ChatRole Role { get; set; }
    public string Content { get; set; } = "";

    /// <summary>The model's "thinking" text, when a reasoning model produced any.</summary>
    public string? Reasoning { get; set; }

    public DateTime CreatedAt { get; set; }

    // The rest is filled in for replies only, for the prompt inspector and the speed readout.

    /// <summary>The exact text the model was given to produce this reply.</summary>
    public string? PromptText { get; set; }
    public string? ModelName { get; set; }
    public string? PresetId { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public double? TokensPerSecond { get; set; }

    /// <summary>How many earlier messages were left out of the prompt to fit the context window.</summary>
    public int? TrimmedMessages { get; set; }
}

public interface IChatStore
{
    Task<IReadOnlyList<Conversation>> ListAsync(string mode, CancellationToken ct = default);
    Task<Conversation?> GetAsync(long id, CancellationToken ct = default);
    Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default);

    /// <summary>Saves title, instructions and preset. The active branch is changed only through <see cref="SetActiveLeafAsync"/>.</summary>
    Task UpdateAsync(Conversation conversation, CancellationToken ct = default);

    /// <summary>Chooses which branch is shown and marks the chat as just used.</summary>
    Task SetActiveLeafAsync(long conversationId, long? leafId, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(long conversationId, CancellationToken ct = default);
    Task<ChatMessage> AddMessageAsync(ChatMessage message, CancellationToken ct = default);
    Task UpdateMessageAsync(ChatMessage message, CancellationToken ct = default);
}
