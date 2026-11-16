using System.Diagnostics;
using System.Text;
using DogAI.Core.Characters;
using DogAI.Core.Documents;
using DogAI.Core.Stories;

namespace DogAI.Core.Chat;

/// <summary>What is being written: a reply in the chat, or a draft that is handed back without being saved (impersonate, revising a selection).</summary>
public enum ReplyKind
{
    Reply = 0,
    Draft = 1,

    /// <summary>Story mode: the choices (and state) that follow a passage are being worked out. Nothing streams into the thread.</summary>
    Choices = 2,
}

/// <summary>The reply being written right now. Read from the UI while it streams.</summary>
public sealed class ActiveReply(long conversationId, long? parentId, long? continuingId = null, string? startText = null, string? startReasoning = null,
    ReplyKind kind = ReplyKind.Reply, long? speakerId = null, string? speakerName = null)
{
    private readonly StringBuilder _content = new(startText ?? "");
    private readonly StringBuilder _reasoning = new(startReasoning ?? "");
    private readonly object _lock = new();

    public long ConversationId { get; } = conversationId;

    /// <summary>The message this reply follows (the user's question). Null for a greeting being continued.</summary>
    public long? ParentId { get; } = parentId;

    /// <summary>When continuing a stopped reply: that reply's id. Its text so far is already in <see cref="Content"/>.</summary>
    public long? ContinuingId { get; } = continuingId;

    public ReplyKind Kind { get; } = kind;

    /// <summary>When continuing: how long the text was before this continuation started.</summary>
    public int StartLength { get; } = startText?.Length ?? 0;

    /// <summary>Group chats: the character writing this reply. Null in one-to-one chats.</summary>
    public long? SpeakerId { get; } = speakerId;

    /// <summary>What is happening before the text starts, e.g. "Summarising older messages". Null while writing.</summary>
    public string? Status { get; internal set; }

    /// <summary>The text so far. In a group chat a leading "Name:" the model copied from the history is left out.</summary>
    public string Content
    {
        get
        {
            string text;
            lock (_lock) text = _content.ToString();
            return speakerName is null ? text : GroupTurns.StripSpeaker(text, speakerName);
        }
    }
    public string Reasoning { get { lock (_lock) return _reasoning.ToString(); } }

    private int _thinkingTokens;
    private bool _answering;
    private volatile string? _engineId;

    /// <summary>Thinking pieces that arrived in this request, counted here (one piece is one token; the engine gives no count).</summary>
    public int ThinkingTokens => Volatile.Read(ref _thinkingTokens);

    /// <summary>The thinking budget the request carries; null for no limit.</summary>
    public int? ThinkingBudget { get; private set; }

    /// <summary>True from the first thinking piece until the answer begins.</summary>
    public bool IsThinking => ThinkingTokens > 0 && !Volatile.Read(ref _answering);

    /// <summary>What became of Answer now for this reply.</summary>
    public AnswerNowState AnswerNow { get; internal set; }

    /// <summary>Whether Answer now can be pressed: the model is thinking, the engine named the reply, and it was not pressed yet.</summary>
    public bool CanAnswerNow => IsThinking && _engineId is not null && AnswerNow == AnswerNowState.No;

    /// <summary>The engine's name for this reply, once the stream gave it.</summary>
    internal string? EngineId => _engineId;

    /// <summary>The thinking as numbers, for the note under it; null when the model did not think.</summary>
    public ThinkingFacts? Thinking => ThinkingTokens is > 0 and var tokens
        ? new ThinkingFacts { Tokens = tokens, Budget = ThinkingBudget, AnswerNow = AnswerNow == AnswerNowState.Ended, Refused = AnswerNow == AnswerNowState.Refused }
        : null;

    /// <summary>Takes the request's thinking budget, for the count shown while it thinks.</summary>
    internal void Expect(SamplerPreset preset) => ThinkingBudget = preset.ThinkingBudget;

    /// <summary>Counts a piece and keeps the reply's name, without keeping its text (the choices step shows no text).</summary>
    internal void Track(ChatDelta delta)
    {
        if (delta.ReplyId is not null) _engineId = delta.ReplyId;
        if (delta.Reasoning is not null && !Volatile.Read(ref _answering)) Interlocked.Increment(ref _thinkingTokens);
        if (delta.Content is not null) Volatile.Write(ref _answering, true);
    }

    internal void Append(ChatDelta delta)
    {
        Track(delta);
        lock (_lock)
        {
            if (delta.Content is not null) _content.Append(delta.Content);
            if (delta.Reasoning is not null) _reasoning.Append(delta.Reasoning);
        }
    }
}

/// <summary>What became of Answer now for the reply being written.</summary>
public enum AnswerNowState
{
    No,

    /// <summary>Pressed; the engine has not answered yet.</summary>
    Asked,

    /// <summary>The engine ended the thinking.</summary>
    Ended,

    /// <summary>The engine could not: it is too old for thinking limits.</summary>
    Refused,
}

/// <summary>
/// Runs chat turns: stores the user's message, builds a prompt that fits the context window,
/// streams the reply and saves it. One reply at a time; it keeps going if the user looks at another screen.
/// Used by General, Characters and Story; a character chat gets its card and persona as standing instructions,
/// a story its bible (the story-only parts are in ChatService.Story.cs).
/// </summary>
public sealed partial class ChatService(IChatStore store, IChatEngine engine, PresetCatalog? presets = null, ICharacterStore? characters = null,
    IStoryStore? stories = null, DocumentLibrary? documents = null, StoryOptions? storyOptions = null, ChatOptions? chatOptions = null,
    IAttachedPictureStore? attached = null)
{
    /// <summary>Story-wide settings: whether a thinking model may think when it writes for a story.</summary>
    private readonly StoryOptions _storyOptions = storyOptions ?? new StoryOptions();

    /// <summary>Whether a thinking model may think in General, and in character and group chats.</summary>
    private readonly ChatOptions _chatOptions = chatOptions ?? new ChatOptions();

    public const string UntitledChat = "New chat";
    private static readonly TimeSpan UiInterval = TimeSpan.FromMilliseconds(50);

    private readonly PresetCatalog _presets = presets ?? new PresetCatalog();
    private readonly Dictionary<long, (int Hash, int Tokens)> _tokenCache = [];
    private readonly Dictionary<string, int> _textTokenCache = [];

    /// <summary>Per chat: after a summary attempt gave nothing usable, the branch length at which to try again.</summary>
    private readonly Dictionary<long, int> _summaryRetryAt = [];
    private string? _tokenCacheModel;
    private CancellationTokenSource? _cts;

    /// <summary>What the engine said each picture costs, by picture id, for the model in <see cref="_tokenCacheModel"/>. Never a guess.</summary>
    private readonly Dictionary<long, int> _pictureTokenCache = [];

    /// <summary>The images of the pictures in the prompt being built, read once for the reply being written.</summary>
    private readonly Dictionary<long, byte[]> _pictureImages = [];

    /// <summary>Set while group members answer one after another, so nothing else starts between their replies.</summary>
    private bool _chain;
    private bool _chainStopped;

    public ActiveReply? Active { get; private set; }
    public bool IsBusy => Active is not null || _chain;

    /// <summary>Why the last reply failed, and in which chat. Cleared when the next one starts.</summary>
    public (long ConversationId, string Message)? LastError { get; private set; }

    /// <summary>Raised while a reply streams (at most every 50 ms) and when it starts or ends.</summary>
    public event Action? Changed;

    public void ClearError()
    {
        LastError = null;
        Changed?.Invoke();
    }

    public void Stop()
    {
        if (_chain) _chainStopped = true;
        _cts?.Cancel();
    }

    /// <summary>
    /// Answer now: ends the thinking of the reply being written, so the model answers at once. Stop still stops everything.
    /// Does nothing unless the reply is thinking (<see cref="ActiveReply.CanAnswerNow"/>). An engine too old for it leaves
    /// the reply thinking; the reply's note then says to update the engine.
    /// </summary>
    public async Task AnswerNowAsync()
    {
        if (Active is not { CanAnswerNow: true, EngineId: { } id } active) return;
        active.AnswerNow = AnswerNowState.Asked;
        Changed?.Invoke();
        ThinkingEnd end;
        try
        {
            end = await engine.EndThinkingAsync(id).ConfigureAwait(false);
        }
        catch (Exception)
        {
            end = ThinkingEnd.TooLate; // the engine went away meanwhile; the reply itself reports that
        }
        active.AnswerNow = end switch
        {
            ThinkingEnd.Ended => AnswerNowState.Ended,
            ThinkingEnd.NotSupported => AnswerNowState.Refused,
            _ => AnswerNowState.No,
        };
        Changed?.Invoke();
    }

    /// <summary>Asks the model through the chat route, telling <paramref name="active"/> the thinking budget the request carries.</summary>
    private IAsyncEnumerable<ChatDelta> AskAsync(ActiveReply active, IReadOnlyList<PromptMessage> prompt, SamplerPreset preset, CancellationToken ct)
    {
        active.Expect(preset);
        return engine.StreamChatAsync(prompt, preset, ct);
    }

    /// <summary>Adds the user's message to the end of the visible branch and answers it.</summary>
    public Task SendAsync(long conversationId, string text, CancellationToken ct = default) => SendAsync(conversationId, text, null, ct);

    /// <summary>
    /// The same with pictures on the message for the model to look at (General, character chats and group chats; at most
    /// <see cref="AttachedPictures.MaxPerMessage"/>, each a JPEG that passes <see cref="AttachedPictures.Problem"/>).
    /// Nothing is stored when one of them is refused. A message may be pictures alone.
    /// </summary>
    public async Task SendAsync(long conversationId, string text, IReadOnlyList<byte[]>? pictures, CancellationToken ct = default)
    {
        EnsureCanStart();
        var conversation = await store.GetAsync(conversationId, ct) ?? throw new InvalidOperationException("That chat no longer exists.");
        var jpegs = CheckPictures(conversation, pictures, 0);
        var messages = await store.GetMessagesAsync(conversationId, ct);
        var leaf = new MessageTree(messages).ResolveLeaf(conversation.ActiveLeafId);

        // Name the chat after its first question (a character's greeting does not count).
        if (conversation.Title == UntitledChat && messages.All(m => m.Role != ChatRole.User))
        {
            conversation.Title = text.Trim().Length == 0 && jpegs.Count > 0 ? PictureChat : TitleFrom(text);
            await store.UpdateAsync(conversation, ct);
        }
        await AskAsync(conversationId, leaf, text, ct, jpegs.Count == 0 ? null : question => attached!.AddAsync(question.Id, jpegs, ct));
    }

    /// <summary>The name of a chat whose first message is a picture with no words.</summary>
    public const string PictureChat = "A picture";

    /// <summary>Replaces a question with a new version on its own branch and answers that. The original stays reachable.</summary>
    public Task EditAndResendAsync(long conversationId, long userMessageId, string text, CancellationToken ct = default) =>
        EditAndResendAsync(conversationId, userMessageId, text, null, null, ct);

    /// <summary>
    /// The same, saying what becomes of the question's pictures: the new version keeps those in <paramref name="keep"/>
    /// (all of them when null), byte for byte, then gets <paramref name="added"/>.
    /// </summary>
    public async Task EditAndResendAsync(long conversationId, long userMessageId, string text, IReadOnlyCollection<long>? keep,
        IReadOnlyList<byte[]>? added, CancellationToken ct = default)
    {
        EnsureCanStart();
        var original = await FindAsync(conversationId, userMessageId, ChatRole.User, ct);
        var conversation = await store.GetAsync(conversationId, ct) ?? throw new InvalidOperationException("That chat no longer exists.");
        var kept = original.Pictures.Where(p => keep is null || keep.Contains(p.Id)).Select(p => p.Id).ToList();
        var jpegs = CheckPictures(conversation, added, kept.Count);
        await AskAsync(conversationId, original.ParentId, text, ct, kept.Count + jpegs.Count == 0 || attached is null ? null : async question =>
        {
            if (kept.Count > 0) await attached.CopyAsync(original.Id, question.Id, kept, ct);
            if (jpegs.Count > 0) await attached.AddAsync(question.Id, jpegs, ct);
        });
    }

    /// <summary>The pictures to put on a new message, each as it is kept; throws, in words for the owner, when they may not go on it.</summary>
    /// <param name="already">How many pictures the message has before these.</param>
    private List<byte[]> CheckPictures(Conversation conversation, IReadOnlyList<byte[]>? pictures, int already)
    {
        if (pictures is not { Count: > 0 }) return [];
        if (attached is null) throw new InvalidOperationException("Pictures cannot be kept here.");
        if (conversation.Mode == Story.Mode) throw new InvalidOperationException("Stories do not take pictures.");
        if (already + pictures.Count > AttachedPictures.MaxPerMessage)
            throw new InvalidOperationException($"A message takes at most {AttachedPictures.MaxPerMessage} pictures.");
        var jpegs = pictures.Select(AttachedPictures.Plain).ToList();
        if (jpegs.Select(AttachedPictures.Problem).FirstOrDefault(p => p is not null) is { } problem) throw new InvalidOperationException(problem);
        return jpegs;
    }

    /// <summary>Writes another reply to the same question, kept alongside the earlier ones.</summary>
    public async Task RegenerateAsync(long conversationId, long assistantMessageId, CancellationToken ct = default)
    {
        EnsureCanStart();
        var reply = await FindAsync(conversationId, assistantMessageId, ChatRole.Assistant, ct);
        if (reply.ParentId is not { } question) throw new InvalidOperationException("That reply has no question to answer again.");
        // Another version comes from the same speaker, in a group chat too.
        await GenerateAsync(conversationId, question, speakerId: reply.SpeakerId);
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

    /// <summary>Answers a question that has no reply yet, e.g. after an error or an immediate stop. In a group, turn order says who.</summary>
    public async Task ReplyToAsync(long conversationId, long userMessageId, CancellationToken ct = default)
    {
        EnsureCanStart();
        await FindAsync(conversationId, userMessageId, ChatRole.User, ct);
        await RespondAsync(conversationId, userMessageId, ct);
    }

    /// <summary>
    /// Starts a group chat. The members' greetings (group-only ones first) are all alternatives for the opening
    /// message, so the user flips through them to choose who opens the scene and how.
    /// </summary>
    public async Task<Conversation> StartGroupChatAsync(IReadOnlyList<long> characterIds, long? personaId = null,
        TurnOrder turnOrder = TurnOrder.Natural, CancellationToken ct = default)
    {
        if (characters is null) throw new InvalidOperationException("Characters are not available.");
        var cast = new List<Character>();
        foreach (var id in characterIds.Distinct())
            if (await characters.GetAsync(id, ct) is { } c) cast.Add(c);
        if (cast.Count == 0) throw new InvalidOperationException("Pick at least one character for the group.");
        var persona = await characters.PersonaForAsync(personaId, ct);
        var conversation = await store.CreateAsync(new Conversation
        {
            Mode = Conversation.GroupMode,
            Title = GroupTurns.JoinNames(cast.Select(c => c.Name).ToList()),
            PersonaId = persona?.Id,
            TurnOrder = turnOrder,
            PresetId = cast[0].PresetId is { } p && _presets.All.Any(x => x.Id == p) ? p : SamplerPresets.DefaultId,
        }, ct);
        await store.SetMembersAsync(conversation.Id, cast.Select(c => new ChatMember { CharacterId = c.Id }).ToList(), ct);

        long? first = null;
        foreach (var character in cast)
        {
            foreach (var greeting in CharacterCard.GroupGreetings(character).Concat(character.Greetings()))
            {
                var message = await store.AddMessageAsync(new ChatMessage
                {
                    ConversationId = conversation.Id,
                    Role = ChatRole.Assistant,
                    SpeakerId = character.Id,
                    Content = CharacterPrompt.Fill(greeting, character.Name, persona?.Name ?? "User").Trim(),
                }, ct);
                first ??= message.Id;
            }
        }
        if (first is not null)
        {
            await store.SetActiveLeafAsync(conversation.Id, first, ct);
            conversation.ActiveLeafId = first;
        }
        return conversation;
    }

    /// <summary>Group chats: <paramref name="characterId"/> speaks now, after whatever is last on the branch shown.</summary>
    public async Task SpeakAsync(long conversationId, long characterId, CancellationToken ct = default)
    {
        EnsureCanStart();
        var conversation = await store.GetAsync(conversationId, ct) ?? throw new InvalidOperationException("That chat no longer exists.");
        if (!conversation.IsGroup) throw new InvalidOperationException("Only group chats have several speakers.");
        var leaf = new MessageTree(await store.GetMessagesAsync(conversationId, ct)).ResolveLeaf(conversation.ActiveLeafId);
        await GenerateAsync(conversationId, leaf, speakerId: characterId);
    }

    /// <summary>Group chats: someone speaks without the user writing; turn order picks who (see <see cref="GroupTurns.Next"/>).</summary>
    public async Task SpeakNextAsync(long conversationId, CancellationToken ct = default)
    {
        EnsureCanStart();
        var conversation = await store.GetAsync(conversationId, ct) ?? throw new InvalidOperationException("That chat no longer exists.");
        var tree = new MessageTree(await store.GetMessagesAsync(conversationId, ct));
        var leaf = tree.ResolveLeaf(conversation.ActiveLeafId);
        var who = GroupTurns.Next(await TurnMembersAsync(conversationId, ct), tree.PathTo(leaf))
                  ?? throw new InvalidOperationException("This group has no members. Add a character first.");
        await GenerateAsync(conversationId, leaf, speakerId: who);
    }

    /// <summary>The group's members for turn order, leaving out any whose character was deleted.</summary>
    private async Task<IReadOnlyList<GroupTurns.Member>> TurnMembersAsync(long conversationId, CancellationToken ct)
    {
        var result = new List<GroupTurns.Member>();
        if (characters is null) return result;
        foreach (var member in await store.GetMembersAsync(conversationId, ct))
            if (await characters.GetAsync(member.CharacterId, ct) is { } c)
                result.Add(new GroupTurns.Member(c.Id, c.Name, member.Muted));
        return result;
    }

    /// <summary>Answers the user's message: one reply in a one-to-one chat; in a group, whoever turn order picks, one after another.</summary>
    private async Task RespondAsync(long conversationId, long questionId, CancellationToken ct)
    {
        var conversation = await store.GetAsync(conversationId, ct);
        if (conversation is not { IsGroup: true })
        {
            await GenerateAsync(conversationId, questionId);
            return;
        }
        var path = new MessageTree(await store.GetMessagesAsync(conversationId, ct)).PathTo(questionId);
        var speakers = GroupTurns.AfterUser(conversation.TurnOrder, await TurnMembersAsync(conversationId, ct), path);
        if (speakers.Count == 0)
        {
            Changed?.Invoke();
            return;
        }
        _chain = true;
        _chainStopped = false;
        try
        {
            long? parent = questionId;
            foreach (var speaker in speakers)
            {
                var saved = await GenerateAsync(conversationId, parent, speakerId: speaker);
                // Stopping, a problem, or an empty reply ends the round.
                if (saved is null || _chainStopped) break;
                parent = saved;
            }
        }
        finally
        {
            _chain = false;
            _chainStopped = false;
            Changed?.Invoke();
        }
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
            Mode = CharacterChatMode,
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

    /// <param name="pictures">Puts the question's pictures on it, before anything reads the chat again. Null when it has none.</param>
    private async Task AskAsync(long conversationId, long? parentId, string text, CancellationToken ct, Func<ChatMessage, Task>? pictures = null)
    {
        var question = await store.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversationId,
            ParentId = parentId,
            Role = ChatRole.User,
            Content = text.Trim(),
        }, ct);
        if (pictures is not null) await pictures(question);
        await store.SetActiveLeafAsync(conversationId, question.Id, ct);
        await RespondAsync(conversationId, question.Id, ct);
    }

    /// <summary>
    /// Writes a draft of the user's next message in a character chat ("impersonate"). Nothing is saved: the text
    /// is returned for the message box, where the user can change it before sending. Empty when stopped early.
    /// </summary>
    public async Task<string> ImpersonateAsync(long conversationId, CancellationToken ct = default)
    {
        EnsureCanStart();
        var conversation = await store.GetAsync(conversationId, ct) ?? throw new InvalidOperationException("That chat no longer exists.");
        var leaf = new MessageTree(await store.GetMessagesAsync(conversationId, ct)).ResolveLeaf(conversation.ActiveLeafId);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var active = new ActiveReply(conversationId, leaf, kind: ReplyKind.Draft);
        Begin(active, cts);
        string? error = null;
        IReadOnlyList<string> stops = [];
        try
        {
            var token = cts.Token;
            var path = new MessageTree(await store.GetMessagesAsync(conversationId, token)).PathTo(leaf);
            // In a group the prompt is built around the member who spoke last; the draft must not speak for any of them.
            var speaker = conversation.IsGroup ? path.LastOrDefault(m => m.Role == ChatRole.Assistant && m.SpeakerId is not null)?.SpeakerId : null;
            var (layout, names) = await PrepareAsync(conversation, path, active, speaker, token);
            layout.FinalInstruction = ""; // the card's final instruction is about the character's replies
            layout.Directive = CharacterPrompt.Fill(CharacterPrompt.ImpersonateDirective, names.Everyone, names.User);
            layout.Add(PromptPartKind.Instruction, CharacterPrompt.Fill(CharacterPrompt.ImpersonateHint, names.Everyone, names.User));
            var built = await BuildPromptAsync(layout, path, PromptEnd.UserTurn, names, token);
            var preset = ThinkingFor(conversation, _presets.Get(conversation.PresetId));
            // Writing inside an open user turn makes the model speak as the user; asking it to is the fallback.
            // Stop where the template would close the user's turn, so the draft never runs on into the character's reply.
            var stream = built.OpenUserTurn
                ? engine.StreamCompletionAsync(built.Raw, built.RawPictures, preset with { Stop = built.TurnEnds }, token)
                : AskAsync(active, built.Prompt, preset, token);
            stops = built.TurnEnds;
            await StreamAsync(stream, active, null, 0, token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            error = e.Message;
        }
        finally
        {
            End(conversationId, error);
            cts.Dispose();
        }
        return PromptLayout.CutAtFirst(active.Content, stops).Trim();
    }

    /// <summary>Forgets the chat's summary; it is written again from scratch when next needed.</summary>
    public async Task ForgetSummaryAsync(long conversationId, CancellationToken ct = default)
    {
        await store.SetSummaryAsync(conversationId, "", null, ct);
        _summaryRetryAt.Remove(conversationId);
        Changed?.Invoke();
    }

    /// <summary><paramref name="preset"/> with the thinking level of this kind of chat: told not to think, or how long it may.</summary>
    private SamplerPreset ThinkingFor(Conversation conversation, SamplerPreset preset) =>
        conversation.Mode == Story.Mode ? _storyOptions.Apply(preset) : _chatOptions.Apply(preset, conversation.Mode);

    private void Begin(ActiveReply active, CancellationTokenSource cts)
    {
        _cts = cts;
        Active = active;
        LastError = null;
        Changed?.Invoke();
    }

    private void End(long conversationId, string? error)
    {
        Active = null;
        _cts = null;
        _pictureImages.Clear(); // pictures are held only while a reply is written
        if (error is not null) LastError = (conversationId, error);
        Changed?.Invoke();
    }

    /// <param name="questionId">The message the reply follows.</param>
    /// <param name="continuing">When set, the reply to make longer instead of writing a new one.</param>
    /// <param name="speakerId">Group chats: the member writing the reply (a continued reply keeps its own).</param>
    /// <returns>The saved reply's id when it finished with some answer text, without being stopped or failing; otherwise null.</returns>
    private async Task<long?> GenerateAsync(long conversationId, long? questionId, ChatMessage? continuing = null, long? speakerId = null)
    {
        speakerId = continuing?.SpeakerId ?? speakerId;
        var speakerName = speakerId is { } sid && characters is not null ? (await characters.GetAsync(sid))?.Name : null;
        if (speakerId is not null && speakerName is null) throw new InvalidOperationException("That character no longer exists.");

        var cts = new CancellationTokenSource();
        var active = new ActiveReply(conversationId, questionId, continuing?.Id, continuing?.Content, continuing?.Reasoning,
            speakerId: speakerId, speakerName: speakerName);
        await store.SetActiveLeafAsync(conversationId, continuing?.Id ?? questionId);
        Begin(active, cts);

        var reply = continuing ?? new ChatMessage
        {
            ConversationId = conversationId,
            ParentId = questionId,
            Role = ChatRole.Assistant,
            SpeakerId = speakerId,
        };
        long? result = null;
        var textBefore = continuing?.Content;
        var tokensBefore = continuing?.CompletionTokens ?? 0;
        var unseen = 0;
        string? error = null;
        try
        {
            var ct = cts.Token;
            var conversation = await store.GetAsync(conversationId, ct) ?? throw new InvalidOperationException("That chat no longer exists.");
            var rawPath = new MessageTree(await store.GetMessagesAsync(conversationId, ct)).PathTo(continuing?.Id ?? questionId);
            var path = rawPath;
            // A story's user turns are sent as requests to the narrator, not as they were typed.
            if (await StoryOfAsync(conversation, ct) is { } story) path = StoryPrompt.ForPrompt(story, path);
            var preset = _presets.Get(conversation.PresetId);
            reply.PresetId = preset.Id;
            preset = ThinkingFor(conversation, preset);
            reply.ModelName = engine.ModelName;

            var sources = new ReplySources();
            var passages = await FindPassagesAsync(conversation, rawPath, active, sources, ct);
            var (layout, names) = await PrepareAsync(conversation, path, active, speakerId, ct, passages);
            await AddRecallAsync(conversation, path, rawPath, layout, names, active, sources, ct);
            var built = await BuildPromptAsync(layout, path, continuing is not null ? PromptEnd.Continue : PromptEnd.Reply, names, ct);
            reply.PromptText = built.Text;
            reply.PromptTokens = built.Tokens;
            reply.TrimmedMessages = built.Trimmed;
            reply.LoreUsed = built.Has(PromptPartKind.LoreBefore) || built.Has(PromptPartKind.LoreAfter) ? string.Join("\n", layout.LoreNames) : null;
            reply.MemoryUsed = built.Has(PromptPartKind.Memory) ? layout.MemoryCovers : null;
            // Parts left out to fit were not seen by the model, so they are not listed as sources either.
            if (!built.Has(PromptPartKind.Documents)) sources.Passages.Clear();
            if (!built.Has(PromptPartKind.Recall)) sources.Recalled.Clear();
            reply.SourcesJson = sources.ToJson();

            // In a group, stop if the model starts writing someone else's line.
            if (names.Group) preset = preset with { Stop = names.StopsFor(names.Character) };
            unseen = built.Unseen;
            var stream = continuing is null
                ? AskAsync(active, built.Prompt, preset, ct)
                : engine.StreamCompletionAsync(built.Raw, built.RawPictures, preset, ct);
            await StreamAsync(stream, active, reply, tokensBefore, ct);
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
                result = reply.Id;
            }
            else if (reply.Content.Length > 0 || reply.Reasoning is not null)
            {
                // How long it thought and under which budget, for the note under the thinking (numbers only).
                // And how many pictures the model could not be shown, for the line on their message.
                if (active.Thinking is not null || unseen > 0)
                    reply.ExtraJson = new StoryTurn { Thinking = active.Thinking, Unseen = unseen > 0 ? unseen : null }.ToJson();
                var saved = await store.AddMessageAsync(reply);
                await store.SetActiveLeafAsync(conversationId, saved.Id);
                // A reply that is thinking only is kept (its note says why), but nothing follows it: no next speaker, no choices.
                result = reply.Content.Length > 0 ? saved.Id : null;
            }
        }
        catch (Exception e)
        {
            error ??= e.Message;
        }
        finally
        {
            if (error is not null || cts.IsCancellationRequested) result = null;
            End(conversationId, error);
            cts.Dispose();
        }
        return result;
    }

    /// <summary>Feeds a stream into the active reply, recording speed figures on <paramref name="reply"/> when given.</summary>
    private async Task StreamAsync(IAsyncEnumerable<ChatDelta> stream, ActiveReply active, ChatMessage? reply, int tokensBefore, CancellationToken ct)
    {
        active.Status = null;
        Changed?.Invoke();
        var lastRaise = Stopwatch.StartNew();
        await foreach (var delta in stream.WithCancellation(ct).ConfigureAwait(false))
        {
            active.Append(delta);
            if (delta.Stats is { } stats && reply is not null)
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

    /// <summary>
    /// Who is who in a prompt. <see cref="Character"/> is the one writing (in a group, the speaker); <see cref="Everyone"/>
    /// names all the characters ("Ann and Bo" in a group). In a group, <see cref="Speakers"/> names each reply's writer.
    /// </summary>
    /// <param name="SummaryTask">The wording of the summary request, when not the character-chat one.</param>
    private sealed record Names(string Character, string User, string Everyone, IReadOnlyDictionary<long, string>? Speakers = null,
        string? SummaryTask = null)
    {
        public bool Group => Speakers is not null;

        /// <summary>Whose turn a message is, as written in the history and in summaries.</summary>
        public string Label(ChatMessage m) =>
            m.Role == ChatRole.User ? User
            : Speakers is not null && m.SpeakerId is { } id && Speakers.TryGetValue(id, out var name) ? name
            : Speakers is not null ? "Someone" : Character;

        /// <summary>Stop strings for <paramref name="speaker"/>'s reply: the start of a line by anyone else.</summary>
        public IReadOnlyList<string> StopsFor(string speaker) =>
            (Speakers?.Values ?? []).Append(User)
                .Where(n => n.Trim().Length > 0 && !n.Equals(speaker, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => "\n" + n.Trim() + ":")
                .ToList();
    }

    /// <summary>
    /// The prompt parts for this chat. General chats get their instructions. Character chats get the card, persona,
    /// the lore that fired (within its budget), the author's note and, when older turns no longer fit, the rolling
    /// summary, which is brought up to date first if it has fallen behind. Group chats get the speaker's card, a note
    /// on each other member, and lore from every member's book.
    /// </summary>
    /// <param name="extra">Adds parts every version of the layout must have (found passages), so the memory summary allows for their size.</param>
    private async Task<(PromptLayout Layout, Names Names)> PrepareAsync(Conversation conversation, IReadOnlyList<ChatMessage> path, ActiveReply active,
        long? speakerId, CancellationToken ct, Action<PromptLayout>? extra = null)
    {
        if (await StoryOfAsync(conversation, ct) is { } story) return await PrepareStoryAsync(conversation, story, path, active, ct, extra);
        if (conversation.IsGroup && characters is not null) return await PrepareGroupAsync(conversation, path, active, speakerId, ct, extra);
        if (conversation.CharacterId is not { } characterId || characters is null || await characters.GetAsync(characterId, ct) is not { } character)
        {
            var plain = PromptLayout.Plain(conversation.SystemPrompt);
            extra?.Invoke(plain);
            return (plain, new Names("Assistant", "User", "Assistant"));
        }

        var persona = await characters.PersonaForAsync(conversation.PersonaId, ct);
        var names = new Names(character.Name, persona?.Name is { Length: > 0 } n ? n : "User", character.Name);
        var lore = await PickLoreAsync([Lorebook.For(character)], path, ct);
        return await WithMemoryAsync(conversation, path, active, names, memory => CharacterPrompt.Layout(character, persona, conversation.SystemPrompt, lore,
            conversation.AuthorsNote, conversation.AuthorsNoteDepth, memory), ct, extra);
    }

    private async Task<(PromptLayout Layout, Names Names)> PrepareGroupAsync(Conversation conversation, IReadOnlyList<ChatMessage> path, ActiveReply active,
        long? speakerId, CancellationToken ct, Action<PromptLayout>? extra = null)
    {
        var members = new List<Character>();
        foreach (var member in await store.GetMembersAsync(conversation.Id, ct))
            if (await characters!.GetAsync(member.CharacterId, ct) is { } c) members.Add(c);
        // A reply can come from someone who has since left the group (regenerating an old reply); they still speak as themselves.
        var speaker = members.FirstOrDefault(c => c.Id == speakerId)
                      ?? (speakerId is { } sid ? await characters!.GetAsync(sid, ct) : null)
                      ?? members.FirstOrDefault()
                      ?? throw new InvalidOperationException("This group has no members. Add a character first.");
        var others = members.Where(c => c.Id != speaker.Id).ToList();

        // Names for every reply on the branch, including members who have left.
        var speakers = members.Append(speaker).DistinctBy(c => c.Id).ToDictionary(c => c.Id, c => c.Name);
        foreach (var id in path.Select(m => m.SpeakerId).OfType<long>().Distinct().Where(id => !speakers.ContainsKey(id)))
            if (await characters!.GetAsync(id, ct) is { } gone) speakers[id] = gone.Name;

        var persona = await characters!.PersonaForAsync(conversation.PersonaId, ct);
        var everyone = GroupTurns.JoinNames(members.Count > 0 ? members.Select(c => c.Name).ToList() : [speaker.Name]);
        var names = new Names(speaker.Name, persona?.Name is { Length: > 0 } n ? n : "User", everyone, speakers);
        var lore = await PickLoreAsync(members.Append(speaker).DistinctBy(c => c.Id).Select(Lorebook.For).ToList(), path, ct);
        return await WithMemoryAsync(conversation, path, active, names, memory => CharacterPrompt.GroupLayout(speaker, others, persona,
            conversation.SystemPrompt, lore, conversation.AuthorsNote, conversation.AuthorsNoteDepth, memory), ct, extra);
    }

    /// <summary>
    /// The layout <paramref name="make"/> gives, with the rolling summary in it when older turns no longer fit
    /// (bringing the summary up to date first if it has fallen behind).
    /// </summary>
    private async Task<(PromptLayout Layout, Names Names)> WithMemoryAsync(Conversation conversation, IReadOnlyList<ChatMessage> path, ActiveReply active,
        Names names, Func<string, PromptLayout> makeBase, CancellationToken ct, Action<PromptLayout>? extra = null)
    {
        PromptLayout make(string memory)
        {
            var made = makeBase(memory);
            extra?.Invoke(made);
            return made;
        }

        var layout = make("");
        if (!conversation.MemoryEnabled) return (layout, names);

        // Summarise only when older turns are being left out, and only the turns that are.
        var plain = await BuildPromptAsync(layout, path, PromptEnd.Reply, names, ct);
        if (plain.Trimmed == 0) return (layout, names);

        // The summary itself takes room, so with it in place more turns may be left out than without. Check again
        // after adding it, so every left-out turn is covered by the summary (a few passes at most).
        var (summary, through) = ValidSummary(conversation, path);
        var trimmed = plain.Trimmed;
        if (summary.Length > 0) trimmed = Math.Max(trimmed, (await BuildPromptAsync(make(summary), path, PromptEnd.Reply, names, ct)).Trimmed);
        for (var pass = 0; pass < 3 && through < trimmed - 1; pass++)
        {
            if (_summaryRetryAt.TryGetValue(conversation.Id, out var retryAt) && path.Count < retryAt) break;
            // Run a little ahead of what is trimmed, so the summary is not rewritten after every message.
            var target = Math.Min(trimmed - 1 + SummaryLead, Math.Max(trimmed - 1, path.Count - 1 - SummaryKeepsOut));
            active.Status = "Summarising older messages";
            Changed?.Invoke();
            (summary, through) = await SummariseAsync(conversation, path, summary, through + 1, target, names, ct);
            if (through < target)
            {
                // The model gave nothing usable: try again a few messages later rather than before every reply.
                _summaryRetryAt[conversation.Id] = path.Count + SummaryLead;
                break;
            }
            _summaryRetryAt.Remove(conversation.Id);
            trimmed = Math.Max(trimmed, (await BuildPromptAsync(make(summary), path, PromptEnd.Reply, names, ct)).Trimmed);
        }
        if (summary.Length == 0) return (layout, names);
        layout = make(summary);
        layout.MemoryCovers = through == 0 ? "the first message" : $"the first {through + 1} messages";
        return (layout, names);
    }

    /// <summary>How many messages past the trimmed ones a fresh summary also covers.</summary>
    private const int SummaryLead = 8;

    /// <summary>The newest messages are never summarised; they are always in the prompt anyway.</summary>
    private const int SummaryKeepsOut = 4;

    private const int SummaryMaxTokens = 1024;

    /// <summary>The chat's summary and the index on <paramref name="path"/> of the last message it covers; ("", -1) when it is missing or belongs to another branch.</summary>
    private static (string Summary, int Through) ValidSummary(Conversation conversation, IReadOnlyList<ChatMessage> path)
    {
        if (conversation.Summary.Trim().Length == 0 || conversation.SummaryThroughId is not { } id) return ("", -1);
        for (var i = 0; i < path.Count; i++)
            if (path[i].Id == id) return (conversation.Summary.Trim(), i);
        return ("", -1);
    }

    /// <summary>
    /// Folds messages <paramref name="from"/>..<paramref name="to"/> of the path into the summary, in chunks small
    /// enough for the context window, saving after each. Keeps the old summary if the model gives nothing.
    /// </summary>
    private async Task<(string Summary, int Through)> SummariseAsync(Conversation conversation, IReadOnlyList<ChatMessage> path,
        string summary, int from, int to, Names names, CancellationToken ct)
    {
        var through = from - 1;
        var chunkBudget = Math.Max(256, engine.ContextLength / 2 - SummaryMaxTokens);
        // Low temperature for accuracy, with repetition penalties because small models easily loop when summarising.
        // Never with thinking: a model that thinks first spends the summary's whole length on it and writes nothing
        // (seen with Qwen3.5), so the chat would never get a summary. Stories were already asked this way.
        var preset = SamplerPresets.Get("precise") with { MaxTokens = SummaryMaxTokens, RepeatPenalty = 1.1, DryMultiplier = 0.8, Thinking = false };
        while (through < to)
        {
            var lines = new StringBuilder();
            var used = await CountTextAsync(summary, ct);
            var end = through;
            while (end < to)
            {
                var m = path[end + 1];
                var cost = await CountAsync(m, ct) + 4;
                if (end > through && used + cost > chunkBudget) break;
                // A summary is a text-only job: a picture is "[picture]" in it, never the picture.
                lines.Append(names.Label(m)).Append(": ").Append(AttachedPictures.TextOf(m).Trim()).Append("\n\n");
                used += cost;
                end++;
            }

            // Not string.Format: that would turn {{char}} into {char} before the names are filled in.
            var task = CharacterPrompt.Fill((names.SummaryTask ?? CharacterPrompt.SummaryTask).Replace("{0}", summary.Length > 0 ? "an updated " : "a "), names.Everyone, names.User);
            var prompt = new List<PromptMessage>
            {
                new("system", CharacterPrompt.SummarySystem),
                new("user", (summary.Length > 0 ? "[Summary so far]\n" + summary + "\n\n" : "")
                            + "[Messages]\n" + lines.ToString().TrimEnd() + "\n\n" + task),
            };
            var text = new StringBuilder();
            var written = 0;
            await foreach (var delta in engine.StreamChatAsync(prompt, preset, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                if (delta.Content is not null) text.Append(delta.Content);
                if (delta.Stats is { } stats) written = stats.CompletionTokens;
            }
            // Keep what there was if the model gave nothing (e.g. a reasoning model that spent its tokens thinking),
            // ran into the length limit (a summary that never ends is usually stuck in a loop), or wrote junk.
            var result = text.ToString().Trim();
            if (written >= SummaryMaxTokens || !CharacterPrompt.IsUsableSummary(result)) break;
            summary = result;
            through = end;
            await store.SetSummaryAsync(conversation.Id, summary, path[through].Id, ct);
            conversation.Summary = summary;
            conversation.SummaryThroughId = path[through].Id;
        }
        return (summary, through);
    }

    /// <summary>
    /// The fired lore entries that fit the lore budget: the book's own (the largest, when a group brings several books),
    /// at most a quarter of the context window.
    /// </summary>
    private async Task<IReadOnlyList<LoreEntry>> PickLoreAsync(IReadOnlyList<Lorebook> books, IReadOnlyList<ChatMessage> path, CancellationToken ct)
    {
        books = books.Where(b => b.Entries.Count > 0).ToList();
        if (books.Count == 0) return [];
        var recent = path.Select(m => m.Content).ToList();
        var matched = books.SelectMany(b => LoreMatcher.Match(b, recent))
            .OrderByDescending(e => e.Constant).ThenByDescending(e => e.Priority).ThenBy(e => e.InsertionOrder)
            .ToList();
        var own = books.Select(b => b.TokenBudget ?? 0).Where(b => b > 0).DefaultIfEmpty(int.MaxValue).Max();
        var budget = Math.Min(own, engine.ContextLength / 4);
        var picked = new List<LoreEntry>();
        var used = 0;
        foreach (var entry in matched)
        {
            var tokens = await CountTextAsync(entry.Content, ct);
            if (used + tokens > budget) continue;
            picked.Add(entry);
            used += tokens;
        }
        return picked;
    }

    /// <summary>How the prompt ends: ready for a reply, inside a reply being continued, or inside an open user turn (impersonate).</summary>
    private enum PromptEnd
    {
        Reply,
        Continue,
        UserTurn,
    }

    /// <summary>Stands in for the user's text when finding where a user turn starts in the rendered template.</summary>
    private const string UserTurnMark = "@@DOGAI-USER-TURN-7f3a@@";

    /// <param name="OpenUserTurn">True when <see cref="Text"/> ends inside an empty user turn, for raw completion.</param>
    /// <param name="TurnEnds">With an open user turn: the text the template puts after a user's message, to stop at.</param>
    private sealed record BuiltPrompt(List<PromptMessage> Prompt, string Text, int Tokens, int Trimmed, IReadOnlyList<PromptPart> Parts,
        bool OpenUserTurn = false, IReadOnlyList<string>? TurnEnds = null)
    {
        public IReadOnlyList<string> TurnEnds { get; } = TurnEnds ?? [];

        /// <summary><see cref="Text"/> without the lines that stand for pictures: what a raw completion carries on from.</summary>
        public string Raw { get; init; } = Text;

        /// <summary>The pictures in the prompt, placed in <see cref="Raw"/>.</summary>
        public IReadOnlyList<PromptPicture> RawPictures { get; init; } = [];

        /// <summary>How many pictures the model was given a note for instead, because it cannot see (or their image is gone).</summary>
        public int Unseen { get; init; }

        public bool Has(PromptPartKind kind) => Parts.Any(p => p.Kind == kind);
    }

    /// <summary>
    /// Picks the messages that fit, then checks the real size using the model's own template and tokenizer,
    /// dropping further old turns until the prompt leaves room for a reply. If even the newest message does not fit,
    /// the least important parts go next (examples, then memory, then lore); the instruction and card always stay.
    /// With <see cref="PromptEnd.Continue"/>, the last message of <paramref name="path"/> is a reply to carry on: the
    /// prompt is the template up to where that reply starts, followed by its text so far. With <see cref="PromptEnd.UserTurn"/>,
    /// the text ends where a new user turn starts, found by rendering a marker in its place; when the chat already ends
    /// with the user (or the template hides the marker), the layout's directive asks for the user's turn instead.
    /// </summary>
    private async Task<BuiltPrompt> BuildPromptAsync(PromptLayout layout, IReadOnlyList<ChatMessage> path, PromptEnd end, Names names, CancellationToken ct)
    {
        var continuation = end == PromptEnd.Continue;
        if (_tokenCacheModel != engine.ModelName)
        {
            _tokenCache.Clear();
            _textTokenCache.Clear();
            _pictureTokenCache.Clear();
            _tokenCacheModel = engine.ModelName;
        }
        // Write mine drafts the owner's own words, and for that one request the pictures are left out altogether. With a
        // picture at the start of the owner's turns the model begins its draft with a stand-in in brackets
        // ("[Start a new chat]", "[User] …": 6 of 6 tries with Huihui Qwen3.5 9B in M17 step 6, against 2 of 6 without),
        // and a note in the picture's place does the same (6 of 6). A message that is pictures alone keeps one word.
        var withPictures = end != PromptEnd.UserTurn;
        // A model that cannot see gets a note in each picture's place; one that can gets the stored pictures themselves.
        var sees = withPictures && engine.SeesPictures && attached is not null;

        var parts = layout.Parts.ToList();
        var partTokens = new Dictionary<PromptPart, int>();
        foreach (var part in parts) partTokens[part] = await CountTextAsync(part.Text, ct);
        var inserted = layout.InsertedText().Trim();
        var insertedTokens = inserted.Length == 0 ? 0 : await CountTextAsync(inserted, ct) + ContextBudgeter.PerMessageOverhead;
        int Fixed() => (parts.Count == 0 ? 0 : parts.Sum(p => partTokens[p]) + 2 * (parts.Count - 1) + ContextBudgeter.PerMessageOverhead) + insertedTokens;

        var items = new List<BudgetItem>(path.Count);
        // A picture goes only with its message, so its cost is the message's.
        foreach (var message in path)
            items.Add(new BudgetItem(message.Role, await CountAsync(message, ct) + (withPictures ? await PictureCostAsync(message, sees, ct) : 0)));

        bool DropPart()
        {
            if (!parts.Any(p => p.DropOrder > 0)) return false;
            var order = parts.Where(p => p.DropOrder > 0).Min(p => p.DropOrder);
            parts.RemoveAll(p => p.DropOrder == order);
            return true;
        }

        var budget = engine.ContextLength - ContextBudgeter.ReplyReserve(engine.ContextLength);
        var fit = ContextBudgeter.Fit(budget, Fixed(), items);
        while (fit.UsedTokens > budget && DropPart())
            fit = ContextBudgeter.Fit(budget, Fixed(), items);

        while (true)
        {
            var included = path.Skip(fit.FirstIncluded).ToList();
            var history = new List<PromptMessage>();
            var unseen = 0;
            // A character chat or story opens with a reply; give the template the user turn it expects first.
            if (included.Count > 0 && included[0].Role == ChatRole.Assistant)
                history.Add(new PromptMessage("user", layout.OpeningTurn ?? CharacterPrompt.ChatStart));
            foreach (var m in continuation ? included[..^1] : included)
            {
                var role = m.Role == ChatRole.User ? "user" : "assistant";
                // The message's pictures come first in it. Those that cannot be shown to the model become a note each.
                var pictures = sees && m.Pictures.Count > 0 ? await PromptPicturesAsync(m, ct) : null;
                var noted = withPictures ? m.Pictures.Count - (pictures?.Count ?? 0) : 0;
                unseen += noted;
                var text = noted > 0 ? AttachedPictures.UnseenTextOf(m, noted)
                    : !withPictures && m.Pictures.Count > 0 && m.Content.Trim().Length == 0 ? AttachedPictures.PictureOnly
                    : m.Content;
                if (!names.Group)
                {
                    history.Add(new PromptMessage(role, text, pictures));
                    continue;
                }
                // In a group every reply is the "assistant", so say whose it is. Turns in a row from the same side are
                // joined into one, because strict chat templates refuse two assistant (or two user) turns in a row.
                var content = role == "assistant" ? names.Label(m) + ": " + text : text;
                if (history.Count > 0 && history[^1].Role == role)
                {
                    // A joined turn keeps each picture in front of its own message's text.
                    var before = history[^1].Content.Length + 2;
                    List<PromptPicture>? all = history[^1].Pictures is null && pictures is null ? null
                        : [.. history[^1].Pictures ?? [], .. (pictures ?? []).Select(p => p with { After = p.After + before })];
                    history[^1] = history[^1] with { Content = history[^1].Content + "\n\n" + content, Pictures = all };
                }
                else
                    history.Add(new PromptMessage(role, content, pictures));
            }

            var system = layout.SystemText(parts);
            RenderedPrompt rendered;
            List<PromptMessage> prompt;
            var open = false;
            IReadOnlyList<string> turnEnds = [];
            if (end == PromptEnd.UserTurn && (history.Count == 0 || history[^1].Role == "assistant"))
            {
                prompt = layout.Arrange(system, history, directive: false, parts: parts);
                rendered = await RenderAsync([.. prompt, new PromptMessage("user", UserTurnMark)], ct);
                var cut = rendered.Raw.IndexOf(UserTurnMark, StringComparison.Ordinal);
                var shownCut = rendered.Shown.IndexOf(UserTurnMark, StringComparison.Ordinal);
                open = cut >= 0 && shownCut >= 0;
                turnEnds = open ? PromptLayout.TurnEnds(rendered.Raw[(cut + UserTurnMark.Length)..]) : [];
                if (open) rendered = rendered with { Raw = rendered.Raw[..cut], Shown = rendered.Shown[..shownCut] };
            }
            else
            {
                prompt = [];
                rendered = new RenderedPrompt("", "", [], 0);
            }
            if (!open)
            {
                prompt = layout.Arrange(system, history, parts: parts);
                // A reply is carried on from its answer, never from inside the model's thinking: the template closes
                // the thinking block first. Left open (as it was until M17 step 6), a thinking model took the answer so
                // far for its thoughts, went on planning in the reply and wrote the answer a second time.
                rendered = await RenderAsync(prompt, ct, thinking: !continuation);
                if (continuation)
                {
                    var soFar = (names.Group ? names.Label(included[^1]) + ": " : "") + included[^1].Content;
                    rendered = rendered with { Raw = rendered.Raw + soFar, Shown = rendered.Shown + soFar };
                }
            }
            // Text is counted without the pictures; each picture costs what the engine says it does.
            var tokens = await engine.CountTokensAsync(rendered.Raw, ct) + rendered.PictureTokens;
            BuiltPrompt Built() => new(prompt, rendered.Shown, tokens, fit.Trimmed, parts, open, turnEnds)
            {
                Raw = rendered.Raw,
                RawPictures = rendered.Pictures,
                Unseen = unseen,
            };
            if (tokens <= budget) return Built();
            var next = ContextBudgeter.DropOldest(fit, items);
            if (next != fit) fit = next;
            else if (!DropPart()) return Built();
        }
    }

    /// <param name="Shown">The prompt as the inspector shows it: a line with its size and cost where each picture is.</param>
    /// <param name="Raw">The prompt's text alone, as it is counted and as a raw completion is given it.</param>
    /// <param name="Pictures">The pictures, each placed by how many characters of <paramref name="Raw"/> come before it.</param>
    /// <param name="PictureTokens">What the pictures cost together.</param>
    private sealed record RenderedPrompt(string Shown, string Raw, IReadOnlyList<PromptPicture> Pictures, int PictureTokens);

    /// <summary>Stands in for a picture while the chat template is applied; a new one every run, so no message can hold it.</summary>
    private static readonly string PictureMarkRun = Guid.NewGuid().ToString("N")[..12];

    private static string PictureMark(int index) => $"@@DOGAI-PICTURE-{PictureMarkRun}-{index}@@";

    /// <summary>
    /// Applies the model's chat template. The template is given text only: a mark of DOG.ai's own stands where each
    /// picture is, and is taken out again (the engine's own marker counts wrongly as text, and a prompt that holds its
    /// words is refused).
    /// </summary>
    /// <param name="thinking">False for a reply that is carried on: the template then closes the model's thinking block.</param>
    private async Task<RenderedPrompt> RenderAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct, bool thinking = true)
    {
        var pictures = messages.SelectMany(m => (m.Pictures ?? []).OrderBy(p => p.After)).ToList();
        if (pictures.Count == 0)
        {
            var plain = await engine.RenderPromptAsync(messages, thinking, ct);
            return new RenderedPrompt(plain, plain, [], 0);
        }

        var marked = new List<PromptMessage>(messages.Count);
        var number = 0;
        foreach (var message in messages) marked.Add(message.AsText((index, _) => PictureMark(index), ref number));
        var text = await engine.RenderPromptAsync(marked, thinking, ct);

        var raw = new StringBuilder(text.Length);
        var shown = new StringBuilder(text.Length + 64 * pictures.Count);
        var placed = new List<PromptPicture>(pictures.Count);
        var cost = 0;
        var at = 0;
        for (var i = 0; i < pictures.Count; i++)
        {
            var mark = PictureMark(i);
            var found = text.IndexOf(mark, at, StringComparison.Ordinal);
            if (found >= 0)
            {
                raw.Append(text, at, found - at);
                shown.Append(text, at, found - at);
                at = found + mark.Length;
            }
            var tokens = await PictureTokensAsync(pictures[i], ct);
            cost += tokens;
            placed.Add(pictures[i] with { After = raw.Length });
            shown.Append(AttachedPictures.Marker(pictures[i].Width, pictures[i].Height, tokens));
        }
        raw.Append(text, at, text.Length - at);
        shown.Append(text, at, text.Length - at);
        return new RenderedPrompt(shown.ToString(), raw.ToString(), placed, cost);
    }

    /// <summary>A message's pictures as they go into a prompt, in order, in front of its text. One whose image is gone or damaged is left out.</summary>
    private async Task<List<PromptPicture>?> PromptPicturesAsync(ChatMessage message, CancellationToken ct)
    {
        var missing = message.Pictures.Select(p => p.Id).Where(id => !_pictureImages.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            foreach (var (id, image) in await attached!.ImagesAsync(missing, ct)) _pictureImages[id] = image;
        var pictures = new List<PromptPicture>();
        foreach (var picture in message.Pictures.OrderBy(p => p.Position).ThenBy(p => p.Id))
            if (_pictureImages.TryGetValue(picture.Id, out var jpeg) && AttachedPictures.Problem(jpeg) is null)
                pictures.Add(new PromptPicture(picture.Id, jpeg, picture.Width, picture.Height));
        return pictures.Count == 0 ? null : pictures;
    }

    /// <summary>What a message's pictures add to its cost when the oldest messages are weighed: no image is read for it.</summary>
    private async Task<int> PictureCostAsync(ChatMessage message, bool sees, CancellationToken ct)
    {
        if (message.Pictures.Count == 0) return 0;
        if (!sees) return message.Pictures.Count * (await CountTextAsync(AttachedPictures.UnseenNote, ct) + 1);
        return message.Pictures.Sum(PictureTokens);
    }

    /// <summary>What a picture on a message costs: what the engine counted for the loaded model when it was last sent, else the rule.</summary>
    public int PictureTokens(AttachedPicture picture) =>
        _pictureTokenCache.TryGetValue(picture.Id, out var counted) ? counted : AttachedPictures.TokensByRule(picture.Width, picture.Height);

    /// <summary>
    /// What a picture that is not on a message yet would cost (for the context meter): the engine's own count when a
    /// model that sees is in memory, else the rule. Never brings a model back.
    /// </summary>
    public async Task<int> CountPictureAsync(byte[] jpeg, CancellationToken ct = default)
    {
        if (AttachedPictures.Problem(jpeg) is not null) return 0;
        var (width, height) = AttachedPictures.SizeOf(jpeg);
        return await engine.CountPictureTokensAsync(new PromptPicture(0, jpeg, width, height), ct) is > 0 and var counted
            ? counted
            : AttachedPictures.TokensByRule(width, height);
    }

    private async Task<int> PictureTokensAsync(PromptPicture picture, CancellationToken ct)
    {
        if (_pictureTokenCache.TryGetValue(picture.Id, out var known)) return known;
        if (await engine.CountPictureTokensAsync(picture, ct) is > 0 and var counted) return _pictureTokenCache[picture.Id] = counted;
        return AttachedPictures.TokensByRule(picture.Width, picture.Height);
    }

    private async Task<int> CountAsync(ChatMessage message, CancellationToken ct)
    {
        var hash = message.Content.GetHashCode();
        if (_tokenCache.TryGetValue(message.Id, out var cached) && cached.Hash == hash) return cached.Tokens;
        var tokens = await engine.CountTokensAsync(message.Content, ct);
        _tokenCache[message.Id] = (hash, tokens);
        return tokens;
    }

    /// <summary>Counts any text, remembering recent results (card parts and lore repeat on every reply).</summary>
    private async Task<int> CountTextAsync(string text, CancellationToken ct)
    {
        if (text.Length == 0) return 0;
        if (_textTokenCache.TryGetValue(text, out var cached)) return cached;
        var tokens = await engine.CountTokensAsync(text, ct);
        if (_textTokenCache.Count > 500) _textTokenCache.Clear();
        _textTokenCache[text] = tokens;
        return tokens;
    }
}
