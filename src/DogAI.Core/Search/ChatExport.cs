using System.Text;
using DogAI.Core.Chat;

namespace DogAI.Core.Search;

/// <summary>Writes the branch of a chat that is on screen as a markdown file.</summary>
public static class ChatExport
{
    public static string ToMarkdown(Conversation conversation, IReadOnlyList<ChatMessage> branch, string userName = "You", string assistantName = "DOG AI",
        Func<ChatMessage, string?>? speaker = null)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(conversation.Title.ReplaceLineEndings(" ")).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(conversation.SystemPrompt))
            text.Append("> Instructions: ").Append(conversation.SystemPrompt.Trim().ReplaceLineEndings("\n> ")).Append("\n\n");
        foreach (var message in branch)
        {
            var who = message.Role == ChatRole.User ? userName : speaker?.Invoke(message) ?? assistantName;
            text.Append("**").Append(who).Append(":**\n\n").Append(message.Content.Trim()).Append("\n\n");
        }
        return text.ToString().TrimEnd() + "\n";
    }

    /// <summary>A file name made from a chat title: letters, digits, spaces, dashes.</summary>
    public static string FileName(string title)
    {
        var clean = new string(title.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : ' ').ToArray());
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > 60) clean = clean[..60].TrimEnd();
        return (clean.Length == 0 ? "chat" : clean) + ".md";
    }
}
