using System.Text.RegularExpressions;

namespace DogAI.Core.Documents;

/// <summary>A found passage and how well it matched.</summary>
/// <param name="Similarity">Cosine similarity between the query and the passage, -1 to 1.</param>
public sealed record SearchHit(IndexedChunk Chunk, float Similarity, double Score);

/// <summary>
/// The passages of one collection, prepared for searching: their vectors, and word counts for keyword matching.
/// Built once and kept in memory until the collection changes.
/// </summary>
public sealed partial class ChunkIndex
{
    public ChunkIndex(IReadOnlyList<IndexedChunk> chunks)
    {
        Chunks = chunks;
        Terms = new Dictionary<string, int>[chunks.Count];
        Lengths = new int[chunks.Count];
        for (var i = 0; i < chunks.Count; i++)
        {
            var counts = new Dictionary<string, int>();
            foreach (var word in Words(chunks[i].Text))
            {
                counts[word] = counts.GetValueOrDefault(word) + 1;
                Lengths[i]++;
            }
            Terms[i] = counts;
            foreach (var word in counts.Keys) DocumentFrequency[word] = DocumentFrequency.GetValueOrDefault(word) + 1;
        }
    }

    public IReadOnlyList<IndexedChunk> Chunks { get; }
    internal Dictionary<string, int>[] Terms { get; }
    internal int[] Lengths { get; }
    internal Dictionary<string, int> DocumentFrequency { get; } = [];

    private static readonly HashSet<string> StopWords =
    [
        "the", "and", "for", "are", "but", "not", "you", "all", "any", "can", "had", "her", "was", "one", "our", "out", "has", "him", "his",
        "how", "its", "may", "who", "did", "get", "use", "she", "too", "that", "with", "have", "this", "will", "your", "from", "they", "been",
        "were", "what", "when", "which", "their", "there", "would", "about", "into", "than", "then", "them", "these", "some", "does", "also",
        "is", "it", "of", "to", "in", "on", "at", "by", "or", "an", "as", "be", "do", "if", "so", "no", "we", "my", "me", "he", "up", "a", "i",
    ];

    /// <summary>Lower-case words of two or more letters or digits, without very common English words.</summary>
    internal static IEnumerable<string> Words(string text)
    {
        foreach (Match m in Word().Matches(text))
        {
            var w = m.Value.ToLowerInvariant();
            if (w.Length >= 2 && !StopWords.Contains(w)) yield return w;
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex Word();
}

/// <summary>
/// Finds the passages that best match a query. Meaning (vector similarity) and exact words (BM25 keyword scoring) are
/// ranked separately and combined by rank, so a passage naming the exact term asked about is found even when the
/// search model rates it only fairly similar, and a passage that says the same thing in other words is found too.
/// </summary>
public static class Retriever
{
    /// <summary>Below this similarity a passage is not used unless its exact words match well.</summary>
    public const float DefaultMinSimilarity = 0.35f;

    private const double RankConstant = 60;

    public static IReadOnlyList<SearchHit> Search(IReadOnlyList<ChunkIndex> indexes, float[] query, string queryText, int count,
        float minSimilarity = DefaultMinSimilarity)
    {
        var all = new List<(ChunkIndex Index, int Row, float Similarity, double Keyword)>();
        var total = indexes.Sum(x => x.Chunks.Count);
        if (total == 0 || count <= 0) return [];

        // Keyword scores (BM25) over all passages of all the collections searched.
        var words = ChunkIndex.Words(queryText).Distinct().ToList();
        var averageLength = Math.Max(1.0, indexes.Sum(x => x.Lengths.Sum()) / (double)total);
        var frequency = words.ToDictionary(w => w, w => indexes.Sum(x => x.DocumentFrequency.GetValueOrDefault(w)));
        foreach (var index in indexes)
        {
            for (var row = 0; row < index.Chunks.Count; row++)
            {
                double keyword = 0;
                foreach (var w in words)
                {
                    if (!index.Terms[row].TryGetValue(w, out var tf)) continue;
                    var df = frequency[w];
                    var idf = Math.Log(1 + (total - df + 0.5) / (df + 0.5));
                    keyword += idf * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * index.Lengths[row] / averageLength));
                }
                all.Add((index, row, VectorMath.Dot(query, index.Chunks[row].Vector), keyword));
            }
        }

        var byMeaning = all.Select((c, i) => (c, i)).OrderByDescending(x => x.c.Similarity).Select(x => x.i).ToArray();
        var byWords = all.Select((c, i) => (c, i)).Where(x => x.c.Keyword > 0).OrderByDescending(x => x.c.Keyword).Select(x => x.i).ToArray();
        var score = new double[all.Count];
        for (var r = 0; r < byMeaning.Length; r++) score[byMeaning[r]] += 1 / (RankConstant + r + 1);
        var keywordRank = new int[all.Count];
        Array.Fill(keywordRank, int.MaxValue);
        for (var r = 0; r < byWords.Length; r++)
        {
            score[byWords[r]] += 1 / (RankConstant + r + 1);
            keywordRank[byWords[r]] = r;
        }

        // A passage must mean something close to the query, or be among the best exact-word matches and not far off.
        return Enumerable.Range(0, all.Count)
            .Where(i => all[i].Similarity >= minSimilarity || (keywordRank[i] < 3 && all[i].Similarity >= minSimilarity - 0.15f))
            .OrderByDescending(i => score[i])
            .Take(count)
            .Select(i => new SearchHit(all[i].Index.Chunks[all[i].Row], all[i].Similarity, score[i]))
            .ToList();
    }
}
