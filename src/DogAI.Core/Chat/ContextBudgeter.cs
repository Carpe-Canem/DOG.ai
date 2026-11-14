namespace DogAI.Core.Chat;

public readonly record struct BudgetItem(ChatRole Role, int Tokens);

/// <param name="FirstIncluded">Index of the oldest message that still fits; everything before it is left out.</param>
/// <param name="UsedTokens">Estimated prompt size for the instructions plus the included messages.</param>
public readonly record struct BudgetResult(int FirstIncluded, int UsedTokens)
{
    public int Trimmed => FirstIncluded;
}

/// <summary>
/// Decides which messages go into the prompt when the whole chat no longer fits.
/// Priority, highest first: instructions, the newest message, then older turns from newest to oldest.
/// (Card, lore and memory slots join this order in later milestones.)
/// </summary>
public static class ContextBudgeter
{
    /// <summary>Rough cost of the role markers a chat template wraps around each message.</summary>
    public const int PerMessageOverhead = 8;

    /// <summary>Room kept free for the reply: a quarter of the window, at most 2048 tokens.</summary>
    public static int ReplyReserve(int contextLength) => Math.Min(2048, Math.Max(0, contextLength) / 4);

    /// <param name="promptBudget">Tokens available for the prompt (context length minus the reply reserve).</param>
    /// <param name="messages">The active branch, oldest first.</param>
    public static BudgetResult Fit(int promptBudget, int systemTokens, IReadOnlyList<BudgetItem> messages)
    {
        if (messages.Count == 0) return new BudgetResult(0, systemTokens);

        var used = systemTokens;
        var first = messages.Count;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var cost = messages[i].Tokens + PerMessageOverhead;
            // The newest message always goes in, even if it alone overflows; the engine reports that honestly.
            if (i < messages.Count - 1 && used + cost > promptBudget) break;
            used += cost;
            first = i;
        }
        return StartOnUserTurn(new BudgetResult(first, used), messages);
    }

    /// <summary>Drops one more message from the front; used when the exact count still comes out too large.</summary>
    public static BudgetResult DropOldest(BudgetResult current, IReadOnlyList<BudgetItem> messages)
    {
        if (current.FirstIncluded >= messages.Count - 1) return current;
        var removed = messages[current.FirstIncluded].Tokens + PerMessageOverhead;
        return StartOnUserTurn(new BudgetResult(current.FirstIncluded + 1, current.UsedTokens - removed), messages);
    }

    // Many chat templates reject a conversation that opens with a reply, so trimming never leaves one at the front.
    private static BudgetResult StartOnUserTurn(BudgetResult result, IReadOnlyList<BudgetItem> messages)
    {
        var (first, used) = result;
        if (first == 0) return result;
        while (first < messages.Count - 1 && messages[first].Role != ChatRole.User)
        {
            used -= messages[first].Tokens + PerMessageOverhead;
            first++;
        }
        return new BudgetResult(first, used);
    }
}
