namespace DogAI.Core.Text;

/// <summary>
/// Which speaker colour a character's spoken lines take in a group chat or a story: the --say-1 … --say-6 tokens in
/// app.css, chosen by where the character's id falls among the others, so the colour does not depend on the order
/// they were added.
/// </summary>
public static class SpeakerTones
{
    /// <summary>How many speaker colours app.css defines.</summary>
    public const int Count = 6;

    /// <summary>The colour (1 to <see cref="Count"/>) of <paramref name="id"/> among <paramref name="ids"/>; null when it is not one of them.</summary>
    public static int? Of(IEnumerable<long> ids, long id)
    {
        var all = ids.Distinct().ToList();
        return all.Contains(id) ? all.Count(other => other < id) % Count + 1 : null;
    }
}
