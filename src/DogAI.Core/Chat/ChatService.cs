using System.Diagnostics;
using System.Text;
using DogAI.Core.Characters;

namespace DogAI.Core.Chat;

/// <summary>The reply being written right now. Read from the UI while it streams.</summary>
public sealed class ActiveReply(long conversationId, long? parentId, long? continuingId = null, string? startText = null, string? startReasoning = null)
{
    private readonly StringBuilder _content = new(startText ?? "");
    private readonly StringBuilder _reasoning = new(startReasoning ?? "");
    private readonly object _lock = new();

    public long ConversationId { get; } = conversationId;

    /// <summary>The message this reply follows (the user's question). Null for a greeting being continued.</summary>
    public long? ParentId { get; } = parentId;

    /// <summary>When continuing a stopped reply: that reply's id. Its text so far is already in <see cref="Content"/>.</summary>
    public long? ContinuingId { get; } = continuingId;

    public string Content { get { lock (_lock) return _content.ToString(); } }
    public string Reasoning { get { lock (_lock) return _reasoning.ToString(); } }

    internal void Append(ChatDelta delta)
    {
        lock (_lock)
        {
            if (delta.Content is not null) _content.Append(delta.Content);
            if (delta.Reasoning is not null) _reasoning.Append(delta.Reasoning);
        }
    }
}

/// <summary>
/// Runs chat turns: stores the user's message, builds a prompt that fits the context window,
/// streams the reply and saves it. One reply at a time; it keeps going if the user looks at another screen.
/// Used by General and Characters; a character chat gets its card and persona as standing instructions.
/// </summary>
public sealed class ChatService(IChatStore store, IChatEngine engine, PresetCatalog? presets = null, ICharacterStore? characters = null)
{
    public const string UntitledChat = "New chat";
    private static readonly TimeSpan UiInterval = TimeSpan.FromMilliseconds(50);

    private readonly PresetCatalog _presets = presets ?? new PresetCatalog();
    private readonly Dictionary<long, (int Hash, int Tokens)> _tokenCache = [];
    private string? _tokenCacheModel;
    private CancellationTokenSource? _cts;

    public ActiveReply? Active { get; private set; }
    public bool IsBusy => Active is not null;

    /// <summary>Why the last reply failed, and in which chat. Cleared when the next one starts.</summary>
    public (long ConversationId, string Message)? LastError { get; private set; }

    /// <summary>Raised while a reply streams (at most every 50 ms) and when it starts or ends.</summary>
    public event Action? Changed;

    public void ClearError()
    {
        LastError = null;
        Changed?.Invoke();
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>Adds the user's message to the end of the visible branch and answers it.</summary>
    public async Task SendAsync(long conversationId, string text, CancellationToken ct = default)
    {
        EnsureCanStart();
        var conversation = await store.GetAsync(conversationId, ct) ?? throw new InvalidOperationException("That chat no longer exists.");
        var messages = await store.GetMessagesAsync(conversationId, ct);
        var leaf = new MessageTree(messages).ResolveLeaf(conversation.ActiveLeafId);

        // Name the chat after its first question (a character's greeting does not count).
        if (conversation.Title == UntitledChat && messages.All(m => m.Role != ChatRole.User))
        {
            conversation.Title = TitleFrom(text);
            await store.UpdateAsync(conversation, ct);
        }
        await AskAsync(conversationId, leaf, text, ct);
    }

    /// <summary>Replaces a question with a new version on its own branch and answers that. The original stays reachable.</summary>
    public async Task EditAndResendAsync(long conversationId, long userMessageId, string text, CancellationToken ct = default)
    {
        EnsureCanStart();
        var original = await FindAsync(conversationId, userMessageId, ChatRole.User, ct);
        await AskAsync(conversationId, original.ParentId, text, ct);
    }

    /// <summary>Writes another reply to the same question, kept alongside the earlier ones.</summary>
    public async Task RegenerateAsync(long conversationId, long assistantMessageId, CancellationToken ct = default)
    {
        EnsureCanStart();
        var reply = await FindAsync(conversationId, assistantMessageId, ChatRole.Assistant, ct);
        if (reply.ParentId is not { } question) throw new InvalidOperationException("That reply has no question to answer again.");
        await GenerateAsync(conversationId, question);
    }

    /// <summary>
    /// Carries on a reply that was stopped or ended too soon. The new text is added to the same message,
    /// since it is the same reply made longer, not an alternative to it.
    /// </summary>
    public async Task ContinueAsync(long conversationId, long assistantMessageId, CancellationToken ct = default)
    {
        EnsureCanStart();
        var reply = await FindAsync(conversationId, assistantMessageId, ChatRole.Assistant, ct);
        await GenerateAsync(conversationId, reply.ParentId, reply);
    }

    /// <summary>Answers a question that has no reply yet, e.g. after an error or an immediate stop.</summary>
    public async Task ReplyToAsync(long conversationId, long userMessageId, CancellationToken ct = default)
    {
        EnsureCanStart();
        await FindAsync(conversationId, userMessageId, ChatRole.User, ct);
        await GenerateAsync(conversationId, userMessageId);
    }

    /// <summary>
    /// Starts a chat with a character. Its greetings become the first message, each one an alternative
    /// the user can flip through before replying.
    /// </summary>
    public async Task<Conversation> StartCharacterChatAsync(long characterId, long? personaId = null, CancellationToken ct = default)
    {
        if (characters is null) throw new InvalidOperationException("Characters are not available.");
        var character = await characters.GetAsync(characterId, ct) ?? throw new InvalidOperationException("That character no longer exists.");
        var persona = await characters.PersonaForAsync(personaId, ct);
        var conversation = await store.CreateAsync(new Conversation
        {
            Mode = "characters",
            Title = UntitledChat,
            CharacterId = character.Id,
            PersonaId = persona?.Id,
            PresetId = character.PresetId is { } p && _presets.All.Any(x => x.Id == p) ? p : SamplerPresets.DefaultId,
        }, ct);

        long? first = null;
        foreach (var greeting in character.Greetings())
        {
            var message = await store.AddMessageAsync(new ChatMessage
            {
                ConversationId = conversation.Id,
                Role = ChatRole.Assistant,
                Content = CharacterPrompt.Fill(greeting, character.Name, persona?.Name ?? "User").Trim(),
            }, ct);
            first ??= message.Id;
        }
        if (first is not null)
        {
            await store.SetActiveLeafAsync(conversation.Id, first, ct);
            conversation.ActiveLeafId = first;
        }
        return conversation;
    }

    /// <summary>Shows the branch that runs through <paramref name="messageId"/>, down to its newest end.</summary>
    public async Task SelectBranchAsync(long conversationId, long messageId, CancellationToken ct = default)
    {
        var tree = new MessageTree(await store.GetMessagesAsync(conversationId, ct));
        if (tree.Find(messageId) is null) return;
        await store.SetActiveLeafAsync(conversationId, tree.LatestLeafUnder(messageId), ct);
        Changed?.Invoke();
    }

    public static string TitleFrom(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        line = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0) return UntitledChat;
        return line.Length <= 60 ? line : line[..57].TrimEnd() + "...";
    }

    private void EnsureCanStart()
    {
        if (IsBusy) throw new InvalidOperationException("A reply is already being written. Stop it first.");
        if (!engine.IsReady) throw new InvalidOperationException("No model is loaded. Load one first.");
    }

    private async Task<ChatMessage> FindAsync(long conversationId, long messageId, ChatRole role, CancellationToken ct)
    {
        var message = (await store.GetMessagesAsync(conversationId, ct)).FirstOrDefault(m => m.Id == messageId);
        if (message is null || message.Role != role) throw new InvalidOperationException("That message no longer exists.");
        return message;
    }

    private async Task AskAsync(long conversationId, long? parentId, string text, CancellationToken ct)
    {
        var question = await store.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversationId,
            ParentId = parentId,
            Role = ChatRole.User,
            Content = text.Trim(),
        }, ct);
        await store.SetActiveLeafAsync(conversationId, question.Id, ct);
        await GenerateAsync(conversationId, question.Id);
    }

    /// <param name="questionId">The message the reply follows.</param>
    /// <param name="continuing">When set, the reply to make longer instead of writing a new one.</param>
    private async Task GenerateAsync(long conversationId, long? questionId, ChatMessage? continuing = null)
    {
        var cts = new CancellationTokenSource();
        var active = new ActiveReply(conversationId, questionId, continuing?.Id, continuing?.Content, continuing?.Reasoning);
        _cts = cts;
        Active = active;
        LastError = null;
        await store.SetActiveLeafAsync(conversationId, continuing?.Id ?? questionId);
        Changed?.Invoke();

        var reply = continuing ?? new ChatMessage
        {
            ConversationId = conversationId,
            ParentId = questionId,
            Role = ChatRole.Assistant,
        };
        var textBefore = continuing?.Content;
        var tokensBefore = continuing?.CompletionTokens ?? 0;
        string? error = null;
        try
        {
            var ct = cts.Token;
            var conversation = await store.GetAsync(conversationId, ct) ?? throw new InvalidOperationException("That chat no longer exists.");
            var path = new MessageTree(await store.GetMessagesAsync(conversationId, ct)).PathTo(continuing?.Id ?? questionId);
            var preset = _presets.Get(conversation.PresetId);
            reply.PresetId = preset.Id;
            reply.ModelName = engine.ModelName;

            var system = await SystemPromptAsync(conversation, ct);
            var (prompt, promptText, promptTokens, trimmed) = await BuildPromptAsync(system, path, continuing is not null, ct);
            reply.PromptText = promptText;
            reply.PromptTokens = promptTokens;
            reply.TrimmedMessages = trimmed;

            var stream = continuing is null
                ? engine.StreamChatAsync(prompt, preset, ct)
                : engine.StreamCompletionAsync(promptText, preset, ct);
            var lastRaise = Stopwatch.StartNew();
            await foreach (var delta in stream.ConfigureAwait(false))
            {
                active.Append(delta);
                if (delta.Stats is { } stats)
                {
                    if (stats.PromptTokens > 0) reply.PromptTokens = stats.PromptTokens;
                    reply.CompletionTokens = tokensBefore + stats.CompletionTokens;
                    reply.TokensPerSecond = stats.TokensPerSecond;
                }
                if (lastRaise.Elapsed >= UiInterval)
                {
                    lastRaise.Restart();
                    Changed?.Invoke();
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Stopped by the user: keep whatever was written so far.
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        try
        {
            // A continued reply keeps its leading text exactly; only the far end is tidied.
            reply.Content = continuing is null ? active.Content.Trim() : active.Content.TrimEnd();
            var reasoning = active.Reasoning.Trim();
            reply.Reasoning = reasoning.Length == 0 ? null : reasoning;
            if (continuing is not null)
            {
                // Only touch the saved reply if something was actually added.
                if (reply.Content != textBefore) await store.UpdateMessageAsync(reply);
                await store.SetActiveLeafAsync(conversationId, reply.Id);
            }
            else if (reply.Content.Length > 0 || reply.Reasoning is not null)
            {
                var saved = await store.AddMessageAsync(reply);
                await store.SetActiveLeafAsync(conversationId, saved.Id);
            }
        }
        catch (Exception e)
        {
            error ??= e.Message;
        }
        finally
        {
            Active = null;
            _cts = null;
            cts.Dispose();
            if (error is not null) LastError = (conversationId, error);
            Changed?.Invoke();
        }
    }

    /// <summary>The standing instructions: the chat's own, or for a character chat the card, persona and the chat's own.</summary>
    private async Task<string> SystemPromptAsync(Conversation conversation, CancellationToken ct)
    {
        if (conversation.CharacterId is not { } characterId || characters is null) return conversation.SystemPrompt;
        var character = await characters.GetAsync(characterId, ct);
        if (character is null) return conversation.SystemPrompt;
        var persona = await characters.PersonaForAsync(conversation.PersonaId, ct);
        return CharacterPrompt.Build(character, persona, conversation.SystemPrompt);
    }

    /// <summary>
    /// Picks the messages that fit, then checks the real size using the model's own template and
    /// tokenizer, dropping further old turns until the prompt leaves room for a reply.
    /// When <paramref name="continuation"/> is set, the last message of <paramref name="path"/> is a reply to
    /// carry on: the prompt is the template up to where that reply starts, followed by its text so far.
    /// </summary>
    private async Task<(List<PromptMessage> Prompt, string Text, int Tokens, int Trimmed)> BuildPromptAsync(
        string systemPrompt, IReadOnlyList<ChatMessage> path, bool continuation, CancellationToken ct)
    {
        if (_tokenCacheModel != engine.ModelName)
        {
            _tokenCache.Clear();
            _tokenCacheModel = engine.ModelName;
        }

        var system = systemPrompt.Trim();
        var systemTokens = system.Length == 0 ? 0 : await engine.CountTokensAsync(system, ct) + ContextBudgeter.PerMessageOverhead;
        var items = new List<BudgetItem>(path.Count);
        foreach (var message in path)
            items.Add(new BudgetItem(message.Role, await CountAsync(message, ct)));

        var budget = engine.ContextLength - ContextBudgeter.ReplyReserve(engine.ContextLength);
        var fit = ContextBudgeter.Fit(budget, systemTokens, items);
        while (true)
        {
            var prompt = new List<PromptMessage>();
            if (system.Length > 0) prompt.Add(new PromptMessage("system", system));
            var included = path.Skip(fit.FirstIncluded).ToList();

            // A character chat opens with the character speaking; give the template the user turn it expects first.
            if (included.Count > 0 && included[0].Role == ChatRole.Assistant)
                prompt.Add(new PromptMessage("user", CharacterPrompt.ChatStart));
            prompt.AddRange((continuation ? included[..^1] : included).Select(m =>
                new PromptMessage(m.Role == ChatRole.User ? "user" : "assistant", m.Content)));

            var text = await engine.RenderPromptAsync(prompt, ct);
            if (continuation) text += included[^1].Content;
            var tokens = await engine.CountTokensAsync(text, ct);
            var next = ContextBudgeter.DropOldest(fit, items);
            if (tokens <= budget || next == fit) return (prompt, text, tokens, fit.Trimmed);
            fit = next;
        }
    }

    private async Task<int> CountAsync(ChatMessage message, CancellationToken ct)
    {
        var hash = message.Content.GetHashCode();
        if (_tokenCache.TryGetValue(message.Id, out var cached) && cached.Hash == hash) return cached.Tokens;
        var tokens = await engine.CountTokensAsync(message.Content, ct);
        _tokenCache[message.Id] = (hash, tokens);
        return tokens;
    }
}
