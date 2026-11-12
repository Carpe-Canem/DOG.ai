using DogAI.Core.Chat;

namespace DogAI.Core.Stories;

/// <summary>One passage on the branch map.</summary>
/// <param name="Id">The passage's message id.</param>
/// <param name="ParentId">The passage it follows; null for an opening.</param>
/// <param name="Column">How many passages deep it is (0 for an opening).</param>
/// <param name="Row">Its line on the map; a new line starts wherever the story forks.</param>
/// <param name="Label">What led here: the player's action, or "Start".</param>
/// <param name="Preview">The first words of the passage.</param>
/// <param name="OnPath">True when it is part of the branch being read.</param>
/// <param name="IsCurrent">True for the passage the story currently stands at.</param>
public sealed record MapNode(long Id, long? ParentId, int Column, int Row, string Label, string Preview, bool OnPath, bool IsCurrent);

/// <summary>
/// Lays out every path taken through an interactive story as a tree of passages, left to right.
/// The first branch from any passage continues on the same line; each further branch starts a new line below.
/// </summary>
public static class BranchMap
{
    public sealed record Layout(IReadOnlyList<MapNode> Nodes, int Columns, int Rows);

    public static Layout Build(IReadOnlyList<ChatMessage> messages, long? activeLeafId)
    {
        var tree = new MessageTree(messages);
        var path = tree.PathTo(tree.ResolveLeaf(activeLeafId));
        var onPath = path.Select(m => m.Id).ToHashSet();
        var current = path.LastOrDefault(m => m.Role == ChatRole.Assistant)?.Id;

        // A passage's parent passage is two steps up: past the user's action to the passage before it.
        var passages = messages.Where(m => m.Role == ChatRole.Assistant).OrderBy(m => m.Id).ToList();
        (long? Parent, string Label) Origin(ChatMessage passage)
        {
            if (passage.ParentId is not { } actionId || tree.Find(actionId) is not { } action) return (null, "Start");
            if (action.Role == ChatRole.Assistant) return (action.Id, "Continued");
            var label = action.Content.Trim().Length > 0 ? Shorten(action.Content, 60) : "Next passage";
            // Walk up past any stacked user turns to the passage they follow.
            var above = action;
            while (above.ParentId is { } up && tree.Find(up) is { } next)
            {
                above = next;
                if (above.Role == ChatRole.Assistant) return (above.Id, label);
            }
            return (null, label);
        }

        var origins = passages.ToDictionary(p => p.Id, Origin);
        var children = passages.ToLookup(p => origins[p.Id].Parent);
        var nodes = new List<MapNode>(passages.Count);
        var nextRow = 0;
        var columns = 0;

        void Place(ChatMessage passage, int column)
        {
            columns = Math.Max(columns, column + 1);
            nodes.Add(new MapNode(passage.Id, origins[passage.Id].Parent, column, nextRow, origins[passage.Id].Label,
                Shorten(passage.Content, 140), onPath.Contains(passage.Id), passage.Id == current));
            var first = true;
            foreach (var child in children[passage.Id])
            {
                if (!first) nextRow++;
                first = false;
                Place(child, column + 1);
            }
        }

        var firstRoot = true;
        foreach (var root in children[null])
        {
            if (!firstRoot) nextRow++;
            firstRoot = false;
            Place(root, 0);
        }
        return new Layout(nodes, columns, nodes.Count == 0 ? 0 : nextRow + 1);
    }

    private static string Shorten(string text, int max)
    {
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
    }
}
