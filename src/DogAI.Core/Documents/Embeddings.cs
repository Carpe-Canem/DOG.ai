using System.Buffers.Binary;

namespace DogAI.Core.Documents;

/// <summary>Thrown when document search cannot run; the message says why in plain words.</summary>
public sealed class EmbedderUnavailableException(string message) : Exception(message);

/// <summary>
/// Turns text into vectors with the document search (embedding) model. Implemented by a second engine process that
/// starts when first needed; faked in tests.
/// </summary>
public interface IEmbedder
{
    /// <summary>
    /// The model that would be used, as a stable id (its file name), or null when no document search model is set up.
    /// Vectors are only compared with vectors from the same model.
    /// </summary>
    Task<string?> ModelIdAsync(CancellationToken ct = default);

    /// <summary>One vector per text, in order. Starts the model if needed.</summary>
    /// <exception cref="EmbedderUnavailableException">No model is set up, or it could not be started.</exception>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>
/// How a model family wants its inputs worded. Several embedding models are trained with an instruction in front of
/// search queries (and sometimes a tag on documents); without it their results get noticeably worse.
/// </summary>
public static class EmbeddingFormat
{
    public static string Query(string? modelId, string text)
    {
        var id = (modelId ?? "").ToLowerInvariant();
        if (id.Contains("qwen3") && id.Contains("embed"))
            return "Instruct: Given a question or a message, retrieve passages that help answer or continue it\nQuery: " + text;
        if (id.Contains("nomic")) return "search_query: " + text;
        if (id.Contains("e5")) return "query: " + text;
        if (id.Contains("bge") && !id.Contains("m3")) return "Represent this sentence for searching relevant passages: " + text;
        return text;
    }

    public static string Document(string? modelId, string text)
    {
        var id = (modelId ?? "").ToLowerInvariant();
        if (id.Contains("nomic")) return "search_document: " + text;
        if (id.Contains("e5") && !(id.Contains("qwen3") && id.Contains("embed"))) return "passage: " + text;
        return text;
    }
}

public static class VectorMath
{
    /// <summary>Scales to length 1, so similarity is a plain dot product. A zero vector stays zero.</summary>
    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        if (sum <= 0) return v;
        var scale = (float)(1 / Math.Sqrt(sum));
        var result = new float[v.Length];
        for (var i = 0; i < v.Length; i++) result[i] = v[i] * scale;
        return result;
    }

    public static float Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        var width = System.Numerics.Vector<float>.Count;
        var acc = System.Numerics.Vector<float>.Zero;
        var i = 0;
        for (; i <= a.Length - width; i += width)
            acc += new System.Numerics.Vector<float>(a, i) * new System.Numerics.Vector<float>(b, i);
        var sum = System.Numerics.Vector.Dot(acc, System.Numerics.Vector<float>.One);
        for (; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    public static byte[] ToBytes(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), v[i]);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        var v = new float[bytes.Length / 4];
        for (var i = 0; i < v.Length; i++) v[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        return v;
    }

    /// <summary>A hash of text that is the same in every run (string.GetHashCode is not), for spotting edited messages.</summary>
    public static long StableHash(string text)
    {
        unchecked
        {
            var hash = (long)14695981039346656037UL;
            foreach (var c in text)
            {
                hash ^= c;
                hash *= 1099511628211L;
            }
            return hash;
        }
    }
}
