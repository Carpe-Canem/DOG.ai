namespace DogAI.Core.Chat;

/// <summary>One entry of the conversation as the model sees it. Role is "system", "user" or "assistant".</summary>
public sealed record PromptMessage(string Role, string Content);

public sealed record GenerationStats(int PromptTokens, int CompletionTokens, double TokensPerSecond);

/// <summary>A piece of a streaming reply. Any of the three parts may be absent.</summary>
public readonly record struct ChatDelta(string? Content, string? Reasoning, GenerationStats? Stats);

/// <summary>What chat logic needs from a loaded model. Implemented by the engine manager; faked in tests.</summary>
public interface IChatEngine
{
    bool IsReady { get; }
    string? ModelName { get; }

    /// <summary>Size of the loaded model's context window in tokens.</summary>
    int ContextLength { get; }

    Task<int> CountTokensAsync(string text, CancellationToken ct = default);

    /// <summary>Applies the model's own chat template and returns the exact prompt text.</summary>
    Task<string> RenderPromptAsync(IReadOnlyList<PromptMessage> messages, CancellationToken ct = default);

    IAsyncEnumerable<ChatDelta> StreamChatAsync(IReadOnlyList<PromptMessage> messages, SamplerPreset sampler, CancellationToken ct = default);

    /// <summary>Carries on writing exactly where <paramref name="prompt"/> ends (raw text, no chat template applied).</summary>
    IAsyncEnumerable<ChatDelta> StreamCompletionAsync(string prompt, SamplerPreset sampler, CancellationToken ct = default);
}
