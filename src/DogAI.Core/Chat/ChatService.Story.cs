using System.Text;
using DogAI.Core.Stories;

namespace DogAI.Core.Chat;

// Story mode. A story is a conversation whose replies are passages. In an interactive story the user's turns are
// actions, and each passage is followed by a second, schema-constrained request for the choices (and the state).
// In an authored story the user's turns are directions for the next passage.
public sealed partial class ChatService
{
    private const int ChoicesMaxTokens = 800;
    private readonly Random _dice = new();

    private async Task<Story?> StoryOfAsync(Conversation conversation, CancellationToken ct) =>
        conversation.Mode == Story.Mode && stories is not null ? await stories.GetAsync(conversation.Id, ct) : null;

    private async Task<Story> RequireStoryAsync(long conversationId, CancellationToken ct)
    {
        var conversation = await store.GetAsync(conversationId, ct) ?? throw new InvalidOperationException("That story no longer exists.");
        return await StoryOfAsync(conversation, ct) ?? throw new InvalidOperationException("That story no longer exists.");
    }

    /// <summary>Creates a story with its settings. Nothing is written until the user starts it.</summary>
    public async Task<Conversation> StartStoryAsync(string title, Story story, CancellationToken ct = default)
    {
        if (stories is null) throw new InvalidOperationException("Stories are not available.");
        var conversation = await store.CreateAsync(new Conversation
        {
            Mode = Story.Mode,
            Title = title.Trim().Length > 0 ? title.Trim() : "Untitled story",
            PresetId = "creative",
        }, ct);
        story.Id = 0;
        story.ConversationId = conversation.Id;
        await stories.SaveAsync(story, ct);
        return conversation;
    }

    /// <summary>Interactive: writes an opening passage. Called again, it writes another opening beside the first.</summary>
    public async Task BeginStoryAsync(long conversationId, CancellationToken ct = default)
    {
        EnsureCanStart();
        await NarrateAsync(await RequireStoryAsync(conversationId, ct), conversationId, null);
    }

    /// <summary>Interactive: the player does something (a choice or their own words) and the narrator answers.</summary>
    /// <param name="roll">Roll a d20 for this action, when the story uses dice.</param>
    public async Task ActAsync(long conversationId, string text, bool roll = false, CancellationToken ct = default)
    {
        EnsureCanStart();
        var story = await RequireStoryAsync(conversationId, ct);
        var turn = new StoryTurn { Roll = roll && story.Dice ? _dice.Next(1, 21) : null };
        var question = await AddUserTurnAsync(conversationId, text, turn, ct);
        await NarrateAsync(story, conversationId, question);
    }

    /// <summary>Authored: writes the next passage, optionally steered, in the given outline chapter (the current one when null).</summary>
    public async Task WriteNextAsync(long conversationId, string steering = "", int? chapter = null, CancellationToken ct = default)
    {
        EnsureCanStart();
        var story = await RequireStoryAsync(conversationId, ct);
        var conversation = await store.GetAsync(conversationId, ct);
        var tree = new MessageTree(await store.GetMessagesAsync(conversationId, ct));
        var path = tree.PathTo(tree.ResolveLeaf(conversation?.ActiveLeafId));
        var turn = new StoryTurn { Chapter = Math.Clamp(chapter ?? StoryTurn.LatestChapter(path), 0, Math.Max(0, story.Outline.Count - 1)) };
        var question = await AddUserTurnAsync(conversationId, steering, turn, ct);
        await NarrateAsync(story, conversationId, question);
    }

    /// <summary>Writes another version of a passage, kept beside the earlier ones. Works for an opening too.</summary>
    public async Task RegeneratePassageAsync(long conversationId, long passageId, CancellationToken ct = default)
    {
        EnsureCanStart();
        var passage = await FindAsync(conversationId, passageId, ChatRole.Assistant, ct);
        await NarrateAsync(await RequireStoryAsync(conversationId, ct), conversationId, passage.ParentId);
    }

    /// <summary>Makes a passage longer. In an interactive story its choices are then worked out again.</summary>
    public async Task ContinuePassageAsync(long conversationId, long passageId, CancellationToken ct = default)
    {
        EnsureCanStart();
        var passage = await FindAsync(conversationId, passageId, ChatRole.Assistant, ct);
        await NarrateAsync(await RequireStoryAsync(conversationId, ct), conversationId, passage.ParentId, passage);
    }

    /// <summary>Answers an action or direction that has no passage yet (after a problem or an immediate stop).</summary>
    public async Task AnswerTurnAsync(long conversationId, long userMessageId, CancellationToken ct = default)
    {
        EnsureCanStart();
        await FindAsync(conversationId, userMessageId, ChatRole.User, ct);
        await NarrateAsync(await RequireStoryAsync(conversationId, ct), conversationId, userMessageId);
    }

    /// <summary>Interactive: works out the choices after a passage again (and its state, when the story tracks one).</summary>
    public async Task SuggestChoicesAsync(long conversationId, long passageId, CancellationToken ct = default)
    {
        EnsureCanStart();
        await FindAsync(conversationId, passageId, ChatRole.Assistant, ct);
        await ChoicesAsync(conversationId, passageId);
    }

    /// <summary>Makes <paramref name="messageId"/> the end of the branch on screen: rewinding to it, or jumping to another branch.</summary>
    public async Task GoToAsync(long conversationId, long messageId, CancellationToken ct = default)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the writing to finish, or stop it first.");
        if (new MessageTree(await store.GetMessagesAsync(conversationId, ct)).Find(messageId) is null) return;
        await store.SetActiveLeafAsync(conversationId, messageId, ct);
        Changed?.Invoke();
    }

    /// <summary>Saves a state the user corrected by hand on the passage it belongs to, keeping that passage's choices.</summary>
    public async Task SetStoryStateAsync(long conversationId, long passageId, StoryState state, CancellationToken ct = default)
    {
        var passage = await FindAsync(conversationId, passageId, ChatRole.Assistant, ct);
        var turn = StoryTurn.Of(passage);
        turn.State = state;
        await store.SetMessageExtraAsync(passageId, turn.ToJson(), ct);
        Changed?.Invoke();
    }

    /// <summary>
    /// Rewrites part of a passage (rewrite, expand, shorten, change tone). Nothing is saved: the replacement text is
    /// returned for the user to accept, and is readable from <see cref="Active"/> while it is written. Empty when stopped early.
    /// </summary>
    public async Task<string> ReviseAsync(long conversationId, long passageId, int start, int length, ReviseKind kind, string detail = "",
        CancellationToken ct = default)
    {
        EnsureCanStart();
        var story = await RequireStoryAsync(conversationId, ct);
        var passage = await FindAsync(conversationId, passageId, ChatRole.Assistant, ct);
        if (start < 0 || length <= 0 || start + length > passage.Content.Length) throw new InvalidOperationException("That selection is no longer in the passage.");

        var selected = passage.Content.Substring(start, length);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var active = new ActiveReply(conversationId, passage.ParentId, kind: ReplyKind.Draft);
        Begin(active, cts);
        string? error = null;
        try
        {
            var prompt = StoryPrompt.RevisePrompt(story, passage.Content[..start], selected, passage.Content[(start + length)..], kind, detail);
            var conversation = await store.GetAsync(conversationId, cts.Token);
            // Editing wants a steadier hand than drafting: the story's style, but never hotter than 0.8, and a length limit
            // tied to the selection so a one-sentence change cannot turn into a page.
            var style = _presets.Get(conversation?.PresetId);
            var preset = style with
            {
                Temperature = Math.Min(style.Temperature, 0.8),
                XtcProbability = 0,
                MaxTokens = StoryPrompt.ReviseTokenLimit(kind, await engine.CountTokensAsync(selected, cts.Token)),
            };
            await StreamAsync(engine.StreamChatAsync(prompt, preset, cts.Token), active, null, 0, cts.Token);
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
        return StoryPrompt.TidyRevision(active.Content, selected);
    }

    /// <summary>Puts <paramref name="replacement"/> where the selection was and saves the passage.</summary>
    public async Task ApplyRevisionAsync(long conversationId, long passageId, int start, int length, string replacement, CancellationToken ct = default)
    {
        var passage = await FindAsync(conversationId, passageId, ChatRole.Assistant, ct);
        if (start < 0 || length <= 0 || start + length > passage.Content.Length) throw new InvalidOperationException("That selection is no longer in the passage.");
        passage.Content = passage.Content[..start] + replacement.Trim() + passage.Content[(start + length)..];
        await store.UpdateMessageAsync(passage, ct);
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds the user's turn at the end of the branch shown. If that branch ends with a turn that never got a passage,
    /// the new one takes its place beside it, so two user turns never follow each other.
    /// </summary>
    private async Task<long> AddUserTurnAsync(long conversationId, string text, StoryTurn turn, CancellationToken ct)
    {
        var conversation = await store.GetAsync(conversationId, ct) ?? throw new InvalidOperationException("That story no longer exists.");
        var tree = new MessageTree(await store.GetMessagesAsync(conversationId, ct));
        var leaf = tree.ResolveLeaf(conversation.ActiveLeafId);
        var parent = leaf is { } id && tree.Find(id) is { Role: ChatRole.User } unanswered ? unanswered.ParentId : leaf;
        var question = await store.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversationId,
            ParentId = parent,
            Role = ChatRole.User,
            Content = text.Trim(),
            ExtraJson = turn.ToJson(),
        }, ct);
        await store.SetActiveLeafAsync(conversationId, question.Id, ct);
        return question.Id;
    }

    /// <summary>Writes (or continues) a passage; in an interactive story the choices follow unless it was stopped or failed.</summary>
    private async Task NarrateAsync(Story story, long conversationId, long? questionId, ChatMessage? continuing = null)
    {
        if (story.Kind != StoryKind.Interactive)
        {
            await GenerateAsync(conversationId, questionId, continuing);
            return;
        }
        _chain = true;
        _chainStopped = false;
        try
        {
            var saved = await GenerateAsync(conversationId, questionId, continuing);
            if (saved is not { } passage || _chainStopped) return;
            // The choices are shown under the passage, so a closing "What do you do?" is only noise.
            if ((await store.GetMessagesAsync(conversationId)).FirstOrDefault(m => m.Id == passage) is { } written
                && StoryPrompt.TrimClosingQuestion(written.Content) is var tidy && tidy != written.Content)
            {
                written.Content = tidy;
                await store.UpdateMessageAsync(written);
            }
            await ChoicesAsync(conversationId, passage);
        }
        finally
        {
            _chain = false;
            _chainStopped = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Asks for the choices after a passage, as JSON the engine is held to, and saves them on the passage together
    /// with the updated state. The request reuses the passage's own prompt, so the model has the same view of the story.
    /// </summary>
    private async Task ChoicesAsync(long conversationId, long passageId)
    {
        var cts = new CancellationTokenSource();
        var active = new ActiveReply(conversationId, passageId, kind: ReplyKind.Choices) { Status = "Working out what you could do next" };
        Begin(active, cts);
        string? error = null;
        try
        {
            var ct = cts.Token;
            var conversation = await store.GetAsync(conversationId, ct) ?? throw new InvalidOperationException("That story no longer exists.");
            var story = await StoryOfAsync(conversation, ct) ?? throw new InvalidOperationException("That story no longer exists.");
            var tree = new MessageTree(await store.GetMessagesAsync(conversationId, ct));
            var passage = tree.Find(passageId) ?? throw new InvalidOperationException("That passage no longer exists.");
            var path = StoryPrompt.ForPrompt(story, tree.PathTo(passageId));

            var (layout, names) = await PrepareAsync(conversation, path, active, null, ct);
            active.Status = "Working out what you could do next";
            Changed?.Invoke();
            var built = await BuildPromptAsync(layout, path, PromptEnd.Reply, names, ct);
            List<PromptMessage> prompt = [.. built.Prompt, new PromptMessage("user", StoryPrompt.ChoicesTask(story))];
            var preset = SamplerPresets.Get(SamplerPresets.DefaultId) with { MaxTokens = ChoicesMaxTokens, JsonSchema = StoryPrompt.ChoicesSchema(story) };

            var text = new StringBuilder();
            await foreach (var delta in engine.StreamChatAsync(prompt, preset, ct).WithCancellation(ct).ConfigureAwait(false))
                if (delta.Content is not null) text.Append(delta.Content);

            var parsed = StoryPrompt.ParseChoices(text.ToString(), story);
            var turn = StoryTurn.Of(passage);
            turn.Choices = parsed.Choices;
            if (parsed.State is not null) turn.State = parsed.State;
            await store.SetMessageExtraAsync(passageId, turn.ToJson(), ct);
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
    }

    private async Task<(PromptLayout Layout, Names Names)> PrepareStoryAsync(Conversation conversation, Story story, IReadOnlyList<ChatMessage> path,
        ActiveReply active, CancellationToken ct)
    {
        var names = story.Kind == StoryKind.Interactive
            ? new Names("Narrator", "Player", "Narrator", SummaryTask: StoryPrompt.SummaryTask)
            : new Names("Story", "Direction", "Story", SummaryTask: StoryPrompt.SummaryTask);
        var state = StoryTurn.LatestState(path);
        return await WithMemoryAsync(conversation, path, active, names, memory => StoryPrompt.Layout(story, memory, state, path.Count == 0), ct);
    }
}
