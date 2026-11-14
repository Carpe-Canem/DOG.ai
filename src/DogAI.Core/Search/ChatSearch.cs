using System.Text;
using DogAI.Core.Chat;

namespace DogAI.Core.Search;

/// <summary>One place a search found something: a message in a chat or story, or (with no message) the chat's own title.</summary>
public sealed record ChatHit(long ConversationId, string Mode, string Title, long? MessageId, ChatRole? Role, string Snippet, DateTime UpdatedAt)
{
    /// <summary>Where this chat is opened in the app.</summary>
    public string Route => ChatSearch.RouteFor(Mode, ConversationId);
}

public interface ISearchStore
{
    /// <summary>Chats and stories whose title or messages contain the words, best matches first.</summary>
    Task<IReadOnlyList<ChatHit>> SearchAsync(string query, int limit = 40, CancellationToken ct = default);
}

/// <summary>A piece of a snippet, and whether it is one of the words that matched.</summary>
public readonly record struct SnippetPart(string Text, bool Match);

public static class ChatSearch
{
    /// <summary>Wrap matched words in snippets. Control characters never occur in chat text, so they cannot be confused with it.</summary>
    public const char MatchStart = '\u0001';
    public const char MatchEnd = '\u0002';

    /// <summary>
    /// The words of a typed search as an FTS5 query: every word must appear, in any order, and the last one may be unfinished
    /// ("dragon cav" finds "dragon cave"). Each word is quoted, so nothing typed can act as query syntax. Null when there are no words.
    /// </summary>
    public static string? ToFtsQuery(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;
        var words = Words(typed);
        if (words.Count == 0) return null;
        var query = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0) query.Append(' ');
            query.Append('"').Append(words[i]).Append('"');
            if (i == words.Count - 1) query.Append('*');
        }
        return query.ToString();
    }

    /// <summary>The letters and digits runs of a text, at most 12 of them.</summary>
    public static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) current.Append(ch);
            else Flush();
        }
        Flush();
        return words.Count > 12 ? words[..12] : words;

        void Flush()
        {
            if (current.Length > 0) words.Add(current.ToString());
            current.Clear();
        }
    }

    /// <summary>Cuts a snippet marked with <see cref="MatchStart"/> and <see cref="MatchEnd"/> into plain and matched pieces, for drawing as text.</summary>
    public static IReadOnlyList<SnippetPart> Parts(string snippet)
    {
        var parts = new List<SnippetPart>();
        var match = false;
        var current = new StringBuilder();
        foreach (var ch in snippet)
        {
            if (ch is MatchStart or MatchEnd)
            {
                if (current.Length > 0) parts.Add(new SnippetPart(current.ToString(), match));
                current.Clear();
                match = ch == MatchStart;
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0) parts.Add(new SnippetPart(current.ToString(), match));
        return parts;
    }

    /// <summary>A line of plain text around the first word of a title that matched, for title hits.</summary>
    public static string MarkTitle(string title, IReadOnlyList<string> words)
    {
        foreach (var word in words)
        {
            var at = title.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return title[..at] + MatchStart + title.Substring(at, word.Length) + MatchEnd + title[(at + word.Length)..];
        }
        return title;
    }

    public static string RouteFor(string mode, long conversationId) => mode switch
    {
        "characters" => $"characters/chat/{conversationId}",
        "group" => $"characters/group/{conversationId}",
        "story" => $"story/{conversationId}",
        _ => $"general/{conversationId}",
    };

    public static string ModeLabel(string mode) => mode switch
    {
        "characters" => "Character chat",
        "group" => "Group chat",
        "story" => "Story",
        _ => "General",
    };
}
