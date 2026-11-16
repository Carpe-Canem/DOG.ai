namespace DogAI.Core.Chat;

/// <summary>One entry of the conversation as the model sees it. Role is "system", "user" or "assistant".</summary>
/// <param name="Pictures">
/// The pictures in this turn, each with its place in <paramref name="Content"/> (M17). Null when there are none, and
/// always null for a model that cannot see: it gets a note in the text instead.
/// </param>
public sealed record PromptMessage(string Role, string Content, IReadOnlyList<PromptPicture>? Pictures = null)
{
    /// <summary>
    /// The turn as text alone, with what <paramref name="mark"/> gives written where each picture is: for the chat
    /// template, which is given no picture. The marks are numbered across a whole prompt from <paramref name="first"/>.
    /// </summary>
    public PromptMessage AsText(Func<int, PromptPicture, string> mark, ref int first)
    {
        if (Pictures is not { Count: > 0 } pictures) return this;
        var text = new System.Text.StringBuilder(Content.Length + 64);
        var at = 0;
        foreach (var picture in pictures.OrderBy(p => p.After))
        {
            var place = Math.Clamp(picture.After, at, Content.Length);
            text.Append(Content, at, place - at).Append(mark(first++, picture));
            at = place;
        }
        text.Append(Content, at, Content.Length - at);
        return new PromptMessage(Role, text.ToString());
    }
}

/// <summary>
/// A picture as it goes into a prompt: the stored JPEG, byte for byte (the engine reads the same picture again when its
/// bytes differ), and where it goes.
/// </summary>
/// <param name="Id">The <see cref="AttachedPicture"/> it is.</param>
/// <param name="Jpeg">A JPEG that passed <see cref="AttachedPictures.Problem"/>.</param>
/// <param name="After">How many characters of the turn's text come before it. 0 puts it first.</param>
public sealed record PromptPicture(long Id, byte[] Jpeg, int Width, int Height, int After = 0);

public sealed record GenerationStats(int PromptTokens, int CompletionTokens, double TokensPerSecond);

/// <summary>A piece of a streaming reply. Any of the parts may be absent.</summary>
/// <param name="ReplyId">The engine's name for the reply being written, sent once, first, for a request that asked for
/// <see cref="SamplerPreset.ThinkingControl"/>: what <see cref="IChatEngine.EndThinkingAsync"/> takes.</param>
public readonly record struct ChatDelta(string? Content, string? Reasoning, GenerationStats? Stats, string? ReplyId = null);

/// <summary>What chat logic needs from a loaded model. Implemented by the engine manager; faked in tests.</summary>
public interface IChatEngine
{
    bool IsReady { get; }
    string? ModelName { get; }

    /// <summary>Size of the loaded model's context window in tokens.</summary>
    int ContextLength { get; }

    Task<int> CountTokensAsync(string text, CancellationToken ct = default);

    /// <summary>True when the model can be given pictures (it was loaded with its projector).</summary>
    bool SeesPictures => false;

    /// <summary>
    /// What one picture costs in tokens, counted by the engine itself, or null when it cannot be asked now: no model in
    /// memory (counting never brings one back), a model that cannot see, or an engine too old to count.
    /// </summary>
    Task<int?> CountPictureTokensAsync(PromptPicture picture, CancellationToken ct = default) => Task.FromResult<int?>(null);

    /// <summary>Applies the model's own chat template and returns the exact prompt text.</summary>
    Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct = default);

    IAsyncEnumerable<ChatDelta> StreamChatAsync(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, CancellationToken ct = default);

    /// <summary>
    /// Ends the thinking of the reply named <paramref name="replyId"/> while it is being written, so the model answers at
    /// once (Answer now). Never throws for an engine that cannot: it says <see cref="ThinkingEnd.NotSupported"/>.
    /// </summary>
    Task<ThinkingEnd> EndThinkingAsync(string replyId, CancellationToken ct = default) => Task.FromResult(ThinkingEnd.NotSupported);

    /// <summary>Carries on writing exactly where <paramref name="prompt"/> ends (raw text, no chat template applied).</summary>
    IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, SamplerPreset sampler, CancellationToken ct = default);

    /// <summary>
    /// The same with pictures in the text: each <see cref="PromptPicture.After"/> counts characters of
    /// <paramref name="prompt"/>. An engine that takes no pictures this way refuses.
    /// </summary>
    IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, IReadOnlyList<PromptPicture> pictures, SamplerPreset sampler, CancellationToken ct = default) =>
        pictures.Count == 0 ? StreamCompletionAsync(prompt, sampler, ct) : throw new NotSupportedException("This model engine cannot carry on a reply that has pictures before it.");
}
