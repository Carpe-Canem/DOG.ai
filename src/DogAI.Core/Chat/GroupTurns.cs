using System.Text.RegularExpressions;

namespace DogAI.Core.Chat;

/// <summary>Decides who speaks next in a group chat. Pure logic, so the rules are easy to test.</summary>
public static class GroupTurns
{
    /// <summary>A member as turn order sees it: who, what they are called, and whether they only speak when picked.</summary>
    public sealed record Member(long Id, string Name, bool Muted);

    /// <summary>Names come from cards, which are untrusted; matching gets little time.</summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Who answers the user's newest message. Manual: nobody. In turn: every member who is not muted, in list order.
    /// Natural: the members the message names (in the order named), else the one who has been quiet longest.
    /// Muted members never answer on their own.
    /// </summary>
    /// <param name="history">The branch up to and including the user's message, oldest first.</param>
    public static IReadOnlyList<long> AfterUser(TurnOrder order, IReadOnlyList<Member> members, IReadOnlyList<ChatMessage> history)
    {
        var active = members.Where(m => !m.Muted).ToList();
        if (active.Count == 0) return [];
        switch (order)
        {
            case TurnOrder.Manual:
                return [];
            case TurnOrder.InTurn:
                return active.Select(m => m.Id).ToList();
            default:
                var text = history.Count > 0 ? history[^1].Content : "";
                var named = Mentioned(text, active);
                return named.Count > 0 ? named : [Quietest(active, history, exclude: null)];
        }
    }

    /// <summary>
    /// Who speaks when the user asks for "someone" without writing: a member named in the last message (other than
    /// its own speaker), else whoever has been quiet longest, not the one who just spoke. Null when nobody can.
    /// </summary>
    public static long? Next(IReadOnlyList<Member> members, IReadOnlyList<ChatMessage> history)
    {
        var active = members.Where(m => !m.Muted).ToList();
        if (active.Count == 0) active = members.ToList();
        if (active.Count == 0) return null;
        var last = history.Count > 0 ? history[^1] : null;
        var lastSpeaker = last?.Role == ChatRole.Assistant ? last.SpeakerId : null;
        if (last is not null)
        {
            var named = Mentioned(last.Content, active).Where(id => id != lastSpeaker).ToList();
            if (named.Count > 0) return named[0];
        }
        return Quietest(active, history, active.Count > 1 ? lastSpeaker : null);
    }

    /// <summary>Members named in <paramref name="text"/> as whole words (full name or first name), in order of first mention.</summary>
    public static IReadOnlyList<long> Mentioned(string text, IReadOnlyList<Member> members)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<(int At, long Id)>();
        foreach (var member in members)
        {
            var at = FirstMention(text, member.Name);
            if (at >= 0) found.Add((at, member.Id));
        }
        return found.OrderBy(f => f.At).Select(f => f.Id).Distinct().ToList();
    }

    /// <summary>The member whose last turn is furthest back (never spoken counts as furthest); ties go to list order.</summary>
    private static long Quietest(IReadOnlyList<Member> members, IReadOnlyList<ChatMessage> history, long? exclude)
    {
        var candidates = members.Where(m => m.Id != exclude).ToList();
        if (candidates.Count == 0) candidates = members.ToList();
        int LastSpoke(long id)
        {
            for (var i = history.Count - 1; i >= 0; i--)
                if (history[i].Role == ChatRole.Assistant && history[i].SpeakerId == id) return i;
            return -1;
        }
        return candidates.OrderBy(m => LastSpoke(m.Id)).First().Id;
    }

    private static int FirstMention(string text, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return -1;
        var forms = new List<string> { name };
        var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (first.Length >= 2 && first != name) forms.Add(first);
        var best = -1;
        foreach (var form in forms)
        {
            try
            {
                var match = Regex.Match(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(form) + @"(?![\p{L}\p{N}])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
                if (match.Success && (best < 0 || match.Index < best)) best = match.Index;
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }
        return best;
    }

    /// <summary>
    /// Removes a leading "Name:" (also "**Name:**" and similar) that models often copy from how the group's history
    /// is shown to them, so the saved reply holds only what the character says.
    /// </summary>
    public static string StripSpeaker(string text, string name)
    {
        if (name.Trim().Length == 0 || text.Length == 0) return text;
        try
        {
            var match = Regex.Match(text, @"^\s*[*_]*" + Regex.Escape(name.Trim()) + @"[*_]*\s*:[*_]*[ \t]*",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            return match.Success ? text[match.Length..] : text;
        }
        catch (RegexMatchTimeoutException)
        {
            return text;
        }
    }

    /// <summary>"Ann", "Ann and Bo", "Ann, Bo and Cy".</summary>
    public static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };
}
