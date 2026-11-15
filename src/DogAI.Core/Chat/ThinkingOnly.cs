namespace DogAI.Core.Chat;

/// <summary>Why a reply has thinking and no answer.</summary>
public enum ThinkingOnlyReason
{
    /// <summary>The owner pressed Stop while the model was still thinking.</summary>
    Stopped,

    /// <summary>The thinking filled the whole context, so there was no room left to answer.</summary>
    OutOfRoom,

    /// <summary>The model ended after thinking without answering, with room to spare.</summary>
    Ended,

    /// <summary>The thinking was cut by its budget or by Answer now, and the model still wrote nothing after it.</summary>
    Cut,

    /// <summary>It finished, but whether room ran out cannot be told (the prompt's size was not recorded).</summary>
    Unknown,
}

/// <summary>What the note in an empty reply says: the reason, how long the thinking ran, and the room the model had.</summary>
public sealed record ThinkingOnlyNote(ThinkingOnlyReason Reason, int? Tokens, int? ContextLength)
{
    /// <summary>The first sentence of the note, in plain words.</summary>
    public string Text => Reason switch
    {
        ThinkingOnlyReason.Stopped => "Stopped while the model was still thinking, so there is no answer.",
        ThinkingOnlyReason.OutOfRoom =>
            $"No answer: the model's thinking ({Tokens:N0} tokens) filled all the room it had ({ContextLength:N0} tokens, the context size), so it could not write one.",
        ThinkingOnlyReason.Cut => "No answer: the model wrote nothing after its thinking was cut short.",
        ThinkingOnlyReason.Ended => $"No answer: the model thought for {Tokens:N0} tokens and then stopped without writing one.",
        _ => $"No answer: the model thought for {Tokens:N0} tokens and wrote nothing after it. Usually the thinking filled all the room it had.",
    };

    /// <summary>Whether a larger context size could help (not when the model simply stopped, or was stopped).</summary>
    public bool MoreRoomHelps => Reason is ThinkingOnlyReason.OutOfRoom or ThinkingOnlyReason.Unknown;
}

/// <summary>
/// A reply that is thinking only: the model "thought" and wrote no answer. The note is worked out from what the reply
/// already saved, so older replies get it too: speed figures arrive only when the model ends by itself, so a reply with
/// none was stopped by the owner; one whose prompt and reply together reach the context size ran out of room; one whose
/// thinking was cut by its budget or by Answer now (<see cref="ThinkingCut"/>) and still has no answer ignored the cut.
/// </summary>
public static class ThinkingOnly
{
    /// <summary>How close to the context size counts as full: the engine stops a few tokens short of the very end.</summary>
    public const int RoomMargin = 64;

    /// <summary>
    /// The note for <paramref name="message"/>, or null when it has an answer (or no thinking). The engine stops exactly at
    /// the context size it had when it wrote the reply, so a prompt plus reply that ends just under one of the sizes DOG.ai
    /// offers (or <paramref name="contextLength"/>, the loaded model's size, for a model trained for less) ran out of room
    /// at that size, even after the size was changed since. Anything else ended by itself.
    /// </summary>
    public static ThinkingOnlyNote? Of(ChatMessage message, int? contextLength = null)
    {
        if (message.Role != ChatRole.Assistant || !string.IsNullOrWhiteSpace(message.Content) || string.IsNullOrWhiteSpace(message.Reasoning))
            return null;
        if (message.CompletionTokens is not { } written || written <= 0)
            return new ThinkingOnlyNote(ThinkingOnlyReason.Stopped, null, null);
        if (message.PromptTokens is not { } prompt || prompt <= 0)
            return new ThinkingOnlyNote(ThinkingOnlyReason.Unknown, written, null);
        var total = prompt + written;
        var sizes = contextLength is > 0 and var loaded ? Models.ContextSizes.Offered.Append(loaded) : Models.ContextSizes.Offered;
        return sizes.Where(size => total <= size && total >= size - RoomMargin).Order().FirstOrDefault() is var room and > 0
            ? new ThinkingOnlyNote(ThinkingOnlyReason.OutOfRoom, written, room)
            : ThinkingCut.Of(message) is { Kind: ThinkingCutKind.Budget or ThinkingCutKind.AnswerNow }
                ? new ThinkingOnlyNote(ThinkingOnlyReason.Cut, written, null)
                : new ThinkingOnlyNote(ThinkingOnlyReason.Ended, written, null);
    }
}
