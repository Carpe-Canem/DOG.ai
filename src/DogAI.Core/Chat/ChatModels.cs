namespace DogAI.Core.Chat;

public enum ChatRole
{
    User = 0,
    Assistant = 1,
}

/// <summary>Who answers in a group chat after the user writes.</summary>
public enum TurnOrder
{
    /// <summary>Whoever the message names answers; otherwise the member who has been quiet longest.</summary>
    Natural = 0,

    /// <summary>Every member who is not muted answers once, in list order.</summary>
    InTurn = 1,

    /// <summary>Nobody answers on their own; the user picks who speaks.</summary>
    Manual = 2,
}

/// <summary>One character taking part in a group chat.</summary>
public sealed class ChatMember
{
    public long Id { get; set; }
    public long ConversationId { get; set; }
    public long CharacterId { get; set; }

    /// <summary>Place in the member list, which is also the order for <see cref="TurnOrder.InTurn"/>.</summary>
    public int Position { get; set; }

    /// <summary>A muted member stays in the scene but only speaks when the user picks them.</summary>
    public bool Muted { get; set; }
}

/// <summary>One chat. Its messages form a tree; <see cref="ActiveLeafId"/> says which branch is on screen.</summary>
public sealed class Conversation
{
    public long Id { get; set; }

    /// <summary>Which area of the app owns it: "general", "characters" (one character), "group" (several), "story" (settings in <c>Story</c>).</summary>
    public string Mode { get; set; } = "general";

    public string Title { get; set; } = "New chat";

    /// <summary>Standing instructions sent ahead of the conversation. Empty means none.</summary>
    public string SystemPrompt { get; set; } = "";

    public string PresetId { get; set; } = SamplerPresets.DefaultId;

    /// <summary>The character this chat is with (Characters mode). Null for other modes.</summary>
    public long? CharacterId { get; set; }

    /// <summary>Who the user is in this chat (Characters mode). Null uses the default persona.</summary>
    public long? PersonaId { get; set; }

    /// <summary>The last message of the branch currently shown. Null while the chat is empty.</summary>
    public long? ActiveLeafId { get; set; }

    /// <summary>A reminder placed a few messages from the end of the prompt (Characters). Empty means none.</summary>
    public string AuthorsNote { get; set; } = "";

    /// <summary>How many messages from the end the author's note goes.</summary>
    public int AuthorsNoteDepth { get; set; } = PromptLayout.DefaultAuthorsNoteDepth;

    /// <summary>Rolling summary of older turns that no longer fit in the prompt. Empty means none yet.</summary>
    public string Summary { get; set; } = "";

    /// <summary>The last message <see cref="Summary"/> covers. The summary is used only while this message is on the branch shown.</summary>
    public long? SummaryThroughId { get; set; }

    /// <summary>Whether older turns are summarised when the chat outgrows the context window.</summary>
    public bool MemoryEnabled { get; set; } = true;

    /// <summary>Group chats: who answers after the user writes.</summary>
    public TurnOrder TurnOrder { get; set; }

    public bool IsGroup => Mode == GroupMode;

    public const string GroupMode = "group";

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

    /// <summary>Which character wrote a reply, for group chats. Null in one-to-one chats.</summary>
    public long? SpeakerId { get; set; }

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

    /// <summary>Names of the lore entries that were in the prompt, one per line. Null when none.</summary>
    public string? LoreUsed { get; set; }

    /// <summary>Which turns the summary in the prompt covered, as "1–40". Null when no summary was used.</summary>
    public string? MemoryUsed { get; set; }

    /// <summary>Story mode: choices, tracked state, dice roll or chapter as JSON (see <c>StoryTurn</c>). Null elsewhere.</summary>
    public string? ExtraJson { get; set; }
}

public interface IChatStore
{
    Task<IReadOnlyList<Conversation>> ListAsync(string mode, CancellationToken ct = default);
    Task<Conversation?> GetAsync(long id, CancellationToken ct = default);
    Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default);

    /// <summary>
    /// Saves title, instructions, preset, persona, author's note and the memory switch. The active branch and the
    /// summary are changed only through <see cref="SetActiveLeafAsync"/> and <see cref="SetSummaryAsync"/>.
    /// </summary>
    Task UpdateAsync(Conversation conversation, CancellationToken ct = default);

    Task SetSummaryAsync(long conversationId, string summary, long? throughId, CancellationToken ct = default);

    /// <summary>Chooses which branch is shown and marks the chat as just used.</summary>
    Task SetActiveLeafAsync(long conversationId, long? leafId, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);

    Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(long conversationId, CancellationToken ct = default);
    Task<ChatMessage> AddMessageAsync(ChatMessage message, CancellationToken ct = default);
    /// <summary>Saves a message's text, thinking, prompt and speed figures (used by edit and continue).</summary>
    Task UpdateMessageAsync(ChatMessage message, CancellationToken ct = default);

    /// <summary>Saves only a message's story details (<see cref="ChatMessage.ExtraJson"/>).</summary>
    Task SetMessageExtraAsync(long messageId, string? extraJson, CancellationToken ct = default);

    /// <summary>Chats with one character, newest first.</summary>
    Task<IReadOnlyList<Conversation>> ListForCharacterAsync(long characterId, CancellationToken ct = default);

    /// <summary>A group chat's members in list order. Empty for other chats.</summary>
    Task<IReadOnlyList<ChatMember>> GetMembersAsync(long conversationId, CancellationToken ct = default);

    /// <summary>Replaces a group chat's members; their order in the list becomes their position.</summary>
    Task SetMembersAsync(long conversationId, IReadOnlyList<ChatMember> members, CancellationToken ct = default);
}
