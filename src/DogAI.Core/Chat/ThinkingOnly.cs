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

    /// <summary>It finished, but whether room ran out cannot be told (another model, or none, is loaded now).</summary>
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
        ThinkingOnlyReason.Ended => $"No answer: the model thought for {Tokens:N0} tokens and then stopped without writing one.",
        _ => $"No answer: the model thought for {Tokens:N0} tokens and wrote nothing after it. Usually the thinking filled all the room it had.",
    };

    /// <summary>Whether a larger context size could help (not when the model simply stopped, or was stopped).</summary>
    public bool MoreRoomHelps => Reason is ThinkingOnlyReason.OutOfRoom or ThinkingOnlyReason.Unknown;
}

/// <summary>
/// A reply that is thinking only: the model "thought" and wrote no answer. The note is worked out from what the reply
/// already saved, so older replies get it too: speed figures arrive only when the model ends by itself, so a reply with
/// none was stopped by the owner; one whose prompt and reply together reach the context size ran out of room.
/// </summary>
public static class ThinkingOnly
{
    /// <summary>How close to the context size counts as full: the engine stops a few tokens short of the very end.</summary>
    public const int RoomMargin = 64;

    /// <summary>
    /// The note for <paramref name="message"/>, or null when it has an answer (or no thinking). <paramref name="contextLength"/>
    /// is the loaded model's context size when that model wrote the reply; null when it cannot be told.
    /// </summary>
    public static ThinkingOnlyNote? Of(ChatMessage message, int? contextLength)
    {
        if (message.Role != ChatRole.Assistant || !string.IsNullOrWhiteSpace(message.Content) || string.IsNullOrWhiteSpace(message.Reasoning))
            return null;
        if (message.CompletionTokens is not { } written || written <= 0)
            return new ThinkingOnlyNote(ThinkingOnlyReason.Stopped, null, null);
        if (contextLength is not { } room || room <= 0 || message.PromptTokens is not { } prompt)
            return new ThinkingOnlyNote(ThinkingOnlyReason.Unknown, written, null);
        return prompt + written >= room - RoomMargin
            ? new ThinkingOnlyNote(ThinkingOnlyReason.OutOfRoom, written, room)
            : new ThinkingOnlyNote(ThinkingOnlyReason.Ended, written, room);
    }
}
