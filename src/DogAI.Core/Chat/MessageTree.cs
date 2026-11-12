namespace DogAI.Core.Chat;

/// <summary>
/// Read-only view over a conversation's messages as a tree. Regenerate, edit-and-branch and
/// (later) swipes and story branches are all "add a sibling, then point the active leaf at it".
/// </summary>
public sealed class MessageTree
{
    private readonly Dictionary<long, ChatMessage> _byId;
    private readonly ILookup<long?, ChatMessage> _children;

    public MessageTree(IEnumerable<ChatMessage> messages)
    {
        var all = messages.OrderBy(m => m.Id).ToList();
        _byId = all.ToDictionary(m => m.Id);
        _children = all.ToLookup(m => m.ParentId);
    }

    public int Count => _byId.Count;

    public ChatMessage? Find(long id) => _byId.GetValueOrDefault(id);

    /// <summary>The alternatives at this message's position, oldest first, including itself.</summary>
    public IReadOnlyList<ChatMessage> Siblings(ChatMessage message) => _children[message.ParentId].ToList();

    public IReadOnlyList<ChatMessage> Children(long? parentId) => _children[parentId].ToList();

    /// <summary>Messages from the first turn down to <paramref name="leafId"/>, in reading order.</summary>
    public IReadOnlyList<ChatMessage> PathTo(long? leafId)
    {
        var path = new List<ChatMessage>();
        var seen = new HashSet<long>();
        var current = leafId is { } id ? Find(id) : null;
        while (current is not null && seen.Add(current.Id))
        {
            path.Add(current);
            current = current.ParentId is { } parent ? Find(parent) : null;
        }
        path.Reverse();
        return path;
    }

    /// <summary>Follows the newest child at each step, so switching to a branch lands on its latest end.</summary>
    public long LatestLeafUnder(long id)
    {
        var seen = new HashSet<long>();
        while (seen.Add(id))
        {
            var newest = _children[id].LastOrDefault();
            if (newest is null) break;
            id = newest.Id;
        }
        return id;
    }

    /// <summary>The saved leaf if it still exists, otherwise the end of the newest branch, or null when empty.</summary>
    public long? ResolveLeaf(long? savedLeafId)
    {
        if (savedLeafId is { } saved && _byId.ContainsKey(saved)) return saved;
        var root = _children[null].LastOrDefault();
        return root is null ? null : LatestLeafUnder(root.Id);
    }
}
