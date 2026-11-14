namespace DogAI.Core.Chat;

/// <summary>A named folder in the General chat list. One level only: folders do not hold folders.</summary>
public sealed class ChatFolder
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Folded shut in the list, so only its heading shows.</summary>
    public bool Collapsed { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>One folder heading in the list with the chats under it.</summary>
public sealed record FolderGroup(ChatFolder Folder, IReadOnlyList<Conversation> Chats);

/// <summary>The General chat list in the order it is drawn: pinned chats, then folders, then chats in no folder.</summary>
public sealed record ChatListLayout(IReadOnlyList<Conversation> Pinned, IReadOnlyList<FolderGroup> Folders, IReadOnlyList<Conversation> Loose);

public static class ChatFolders
{
    public const int MaxNameLength = 80;

    /// <summary>A folder name as typed, tidied: trimmed, inner runs of spaces made one, cut to <see cref="MaxNameLength"/>. Null when nothing is left.</summary>
    public static string? CleanName(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;
        var name = string.Join(' ', typed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength].TrimEnd();
    }

    /// <summary>The folder with this name, ignoring case. Two folders may not share a name.</summary>
    public static ChatFolder? Named(IEnumerable<ChatFolder> folders, string name) =>
        folders.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>
    /// Sorts chats under their folders. Pinned chats stay at the top whatever folder they are in, and are not shown twice.
    /// A chat whose folder no longer exists counts as in no folder. Chats keep the order they were given in
    /// (most recently used first); folders are in name order.
    /// </summary>
    public static ChatListLayout Arrange(IEnumerable<ChatFolder> folders, IEnumerable<Conversation> chats)
    {
        var ordered = folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(f => f.Id).ToList();
        var known = ordered.Select(f => f.Id).ToHashSet();
        var all = chats.ToList();
        var pinned = all.Where(c => c.Pinned).ToList();
        var rest = all.Where(c => !c.Pinned).ToList();
        var groups = ordered
            .Select(f => new FolderGroup(f, rest.Where(c => c.FolderId == f.Id).ToList()))
            .ToList();
        var loose = rest.Where(c => c.FolderId is not { } id || !known.Contains(id)).ToList();
        return new ChatListLayout(pinned, groups, loose);
    }
}
