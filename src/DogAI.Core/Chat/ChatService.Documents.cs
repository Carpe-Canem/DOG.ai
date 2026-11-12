using System.Text;
using DogAI.Core.Documents;

namespace DogAI.Core.Chat;

// Documents and recall. Before a reply, the chat's own files and the collections attached to it are searched with the
// newest message, and the best passages go with that message, numbered so the reply can cite them. In character chats,
// groups and stories, when older turns are left out to fit, the left-out messages that match the newest one are found
// the same way and sent along ("recall"), next to the rolling summary.
public sealed partial class ChatService
{
    private const int GeneralPassages = 6;
    private const int RoleplayPassages = 4;
    private const int RecallCount = 3;
    private const int RecallMessageChars = 800;

    /// <summary>
    /// Below this a left-out message is not recalled. Higher than for passages: a wrong memory misleads more than a missing one.
    /// Measured with Qwen3 Embedding 0.6B: a message about the same thing scored 0.6 to 0.7, unrelated chat 0.26 to 0.41.
    /// </summary>
    public const float RecallMinSimilarity = 0.5f;

    /// <summary>The similarity a left-out message needs to be recalled. Depends on the search model; tests lower it for their fake one.</summary>
    public float RecallThreshold { get; set; } = RecallMinSimilarity;

    /// <summary>Standing instruction for General chats that have documents. It stays the same every turn, so it does not upset the prompt cache.</summary>
    public const string CiteInstruction =
        "The user's messages may come with numbered passages from their documents. Use them to answer, and cite each passage you use " +
        "by its number in square brackets, like [2], right after the sentence it supports. If the passages do not hold the answer, say so, " +
        "then answer from what you know if you can.";

    /// <summary>Starts the passages in a General chat. The numbers that exist are stated, because documents have numbered
    /// sections of their own, and a model asked to cite tends to cite those too.</summary>
    public const string PassagesHeader = "[Passages from the user's documents";

    private static string PassagesHeading(int count) =>
        count == 1 ? PassagesHeader + ": one passage, [1]. Cite it as [1].]" : $"{PassagesHeader}, numbered 1 to {count}. Cite only these numbers.]";
    public const string BackgroundHeader = "[Background from reference documents. Use it for facts; it is not part of the conversation.]";

    /// <summary>
    /// Searches the chat's documents with the newest message and returns what to add to the prompt layout, or null when
    /// the chat has no documents. Problems (no search model) are noted on <paramref name="sources"/>, never thrown.
    /// </summary>
    private async Task<Action<PromptLayout>?> FindPassagesAsync(Conversation conversation, IReadOnlyList<ChatMessage> rawPath, ActiveReply active,
        ReplySources sources, CancellationToken ct)
    {
        if (documents is null) return null;
        var ids = new List<long>();
        if (await documents.Store.ForConversationAsync(conversation.Id, create: false, ct) is { } own) ids.Add(own.Id);
        ids.AddRange(await documents.Store.AttachedAsync(conversation.Id, ct));
        if (ids.Count == 0) return null;

        var general = conversation.Mode == "general";
        Action<PromptLayout> instruction = general ? l => l.Add(PromptPartKind.Instruction, CiteInstruction) : _ => { };
        var query = SearchQuery(rawPath);
        if (query.Length == 0) return instruction;
        if (await documents.ModelIdAsync(ct) is null)
        {
            sources.Note = "Documents were not searched: there is no document search model yet. Get one in Models → Recommended → Document search.";
            return instruction;
        }

        active.Status = "Searching your documents";
        Changed?.Invoke();
        IReadOnlyList<FoundPassage> found;
        try
        {
            found = await documents.SearchAsync(ids, query, general ? GeneralPassages : RoleplayPassages, ct: ct);
        }
        catch (Core.Documents.EmbedderUnavailableException e)
        {
            sources.Note = "Documents were not searched: " + e.Message;
            return instruction;
        }
        if (found.Count == 0) return instruction;

        var budget = engine.ContextLength / (general ? 3 : 6);
        var used = 0;
        var text = new StringBuilder();
        foreach (var passage in found)
        {
            var number = sources.Passages.Count + 1;
            var source = new SourcePassage(number, passage.Chunk.Id, passage.Chunk.DocumentId, passage.FileName, passage.Chunk.Page,
                passage.Chunk.Heading, passage.Similarity);
            var block = $"\n\n[{number}] {source.Label}\n{passage.Chunk.Text.Trim()}";
            var tokens = await CountTextAsync(block, ct);
            if (used + tokens > budget) continue;
            used += tokens;
            text.Append(block);
            sources.Passages.Add(source);
        }
        if (sources.Passages.Count == 0) return instruction;
        var reference = (general ? PassagesHeading(sources.Passages.Count) : BackgroundHeader) + text;
        return layout =>
        {
            instruction(layout);
            layout.Add(PromptPartKind.Documents, reference);
        };
    }

    /// <summary>
    /// Long character chats, groups and stories: adds the left-out messages that best match the newest one. Quietly does
    /// nothing without a search model, when nothing is left out, or when recall is off.
    /// </summary>
    private async Task AddRecallAsync(Conversation conversation, IReadOnlyList<ChatMessage> path, IReadOnlyList<ChatMessage> rawPath, PromptLayout layout,
        Names names, ActiveReply active, ReplySources sources, CancellationToken ct)
    {
        if (documents is null || conversation.Mode == "general" || !conversation.MemoryEnabled || !conversation.RecallEnabled) return;
        var query = SearchQuery(rawPath);
        if (query.Length == 0 || await documents.ModelIdAsync(ct) is null) return;
        var built = await BuildPromptAsync(layout, path, PromptEnd.Reply, names, ct);
        if (built.Trimmed == 0) return;

        active.Status = "Looking back through earlier messages";
        Changed?.Invoke();
        IReadOnlyList<(ChatMessage Message, float Similarity)> hits;
        try
        {
            hits = await documents.RecallAsync(conversation.Id, path.Take(built.Trimmed).ToList(), names.Label, query, RecallCount, RecallThreshold, ct);
        }
        catch (Core.Documents.EmbedderUnavailableException)
        {
            return;
        }
        if (hits.Count == 0) return;

        var budget = engine.ContextLength / 10;
        var used = 0;
        var text = new StringBuilder(conversation.Mode == Stories.Story.Mode ? "[Earlier in the story, possibly relevant now]" : "[Earlier in this chat, possibly relevant now]");
        foreach (var (message, _) in hits)
        {
            var content = message.Content.Trim();
            if (content.Length > RecallMessageChars) content = content[..RecallMessageChars].TrimEnd() + "…";
            var block = "\n\n" + names.Label(message) + ": " + content;
            var tokens = await CountTextAsync(block, ct);
            if (used + tokens > budget) continue;
            used += tokens;
            text.Append(block);
            sources.Recalled.Add(message.Id);
        }
        if (sources.Recalled.Count > 0) layout.Add(PromptPartKind.Recall, text.ToString());
    }

    /// <summary>
    /// What to search for: the newest user message. A short one (under eight words, like "and the second?") gets the reply before it and the
    /// user's previous message too, so follow-ups find what they refer to. With no user text (a story's "write next"),
    /// the end of the last reply.
    /// </summary>
    public static string SearchQuery(IReadOnlyList<ChatMessage> path)
    {
        var at = -1;
        for (var i = path.Count - 1; i >= 0; i--)
            if (path[i].Role == ChatRole.User) { at = i; break; }
        var text = at >= 0 ? path[at].Content.Trim() : "";
        var before = at > 0 ? path[at - 1] : at < 0 && path.Count > 0 ? path[^1] : null;
        var replyTail = before is { Role: ChatRole.Assistant } ? Tail(before.Content.Trim(), 400) : "";
        if (text.Length == 0) return Tail(replyTail, 600);
        if (text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 8) return text;

        string? earlier = null;
        for (var i = at - 1; i >= 0; i--)
            if (path[i].Role == ChatRole.User) { earlier = path[i].Content.Trim(); break; }
        return string.Join("\n", new[] { earlier is null ? "" : Tail(earlier, 400), replyTail, text }.Where(s => s.Length > 0));
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : text[^max..];
}
