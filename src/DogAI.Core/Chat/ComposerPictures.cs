using DogAI.Core.Models;

namespace DogAI.Core.Chat;

/// <summary>How full the model's room is once the pictures waiting in the chat box are sent with the chat (M17 step 4).</summary>
public enum PictureRoom
{
    /// <summary>Nothing waits, or it all fits.</summary>
    Fits,

    /// <summary>The pictures fit, but the oldest messages will be left out to make room (as they are for long chats).</summary>
    OlderLeftOut,

    /// <summary>The pictures alone take the room a message may have: the engine would refuse it, or leave no room for a reply.</summary>
    TooLarge,
}

/// <summary>A line naming a place in Models, so the page can make the name a link.</summary>
/// <param name="Text">The sentence before the place.</param>
/// <param name="PlaceName">The place as messages name it (<see cref="ModelsPlaces.LibraryName"/>).</param>
/// <param name="Address">Its address (<see cref="ModelsPlaces.Address"/>).</param>
public sealed record PlaceLine(string Text, string PlaceName, string Address);

/// <summary>
/// The rules and words for pictures in the chat box of General, character chats and group chats (M17 step 4). The page
/// opens a file, reduces it to <see cref="AttachedPictures.MaxSide"/> pixels and writes it again as a JPEG at
/// <see cref="AttachedPictures.Quality"/>; what reaches Core is that JPEG, judged by <see cref="AttachedPictures.Problem"/>.
/// </summary>
public static class ComposerPictures
{
    /// <summary>What the page may open: checked by the file's first bytes, never by its name or type. Never SVG.</summary>
    public const string Kinds = "PNG, JPEG, WebP, BMP or GIF";

    /// <summary>The file chooser's filter. The page checks the bytes again.</summary>
    public const string Accept = "image/png,image/jpeg,image/webp,image/bmp,image/gif";

    /// <summary>The largest file the page opens, in bytes. A larger one is refused before it is read.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>The one line of help shown with pictures waiting to be sent (the trial: sentences on a full-HD screenshot were read right, a file name's odd letters were not).</summary>
    public const string Help = "The model looks at each picture at 1,024 pixels, so very small print may be misread.";

    /// <summary>The attach button's tooltip.</summary>
    public const string ButtonTitle = "Add pictures for the model to look at (you can also paste or drop them here)";

    /// <summary>
    /// Said when pictures are pasted or dropped while the chat's model cannot see them, and while pictures wait for a model
    /// that cannot. <paramref name="loaded"/> is whether a chat model is loaded (or set aside) at all.
    /// </summary>
    public static PlaceLine CannotSee(bool loaded) => new(
        loaded
            ? $"The model loaded cannot see pictures. Models that can are marked “{Projectors.Label}” in"
            : $"Load a model that can see pictures first. They are marked “{Projectors.Label}” in",
        ModelsPlaces.LibraryName, ModelsPlaces.Address(ModelsPlaces.Library));

    /// <summary>Said under the box when pictures wait to be sent but the model loaded cannot see them: it would only be told they are there.</summary>
    public static PlaceLine WaitingUnseen => new(
        $"The model loaded cannot see pictures: it will only be told they are there. Models that can are marked “{Projectors.Label}” in",
        ModelsPlaces.LibraryName, ModelsPlaces.Address(ModelsPlaces.Library));

    /// <summary>Said when more pictures were offered than a message takes.</summary>
    public static string TooMany(int leftOut) =>
        $"A message takes at most {AttachedPictures.MaxPerMessage} pictures, so {leftOut} {(leftOut == 1 ? "was" : "were")} left out.";

    /// <summary>Said when files could not be opened as pictures.</summary>
    public static string Refused(int count) => count == 1
        ? $"That file could not be added: only {Kinds} pictures can be."
        : $"{count} files could not be added: only {Kinds} pictures can be.";

    /// <summary>
    /// The note on a reply whose model could not see pictures (<see cref="Stories.StoryTurn.Unseen"/>): how many it was only
    /// told about. They may be on earlier messages too, so it does not say "this picture" for more than one.
    /// </summary>
    public static string Unseen(int count) => count == 1
        ? "The model could not see this picture: it cannot see pictures, so it was only told one is there."
        : $"The model could not see the {count} pictures in this chat: it cannot see pictures, so it was only told they are there.";

    /// <summary>The room a message and its pictures may take: the context less what is kept for the reply.</summary>
    public static int MessageRoom(int contextLength) => Math.Max(0, contextLength - ContextBudgeter.ReplyReserve(contextLength));

    /// <summary>
    /// Whether the pictures waiting (<paramref name="pending"/> tokens, as the engine counts them) fit beside the chat as it
    /// is (<paramref name="used"/> tokens: the last prompt and its reply).
    /// </summary>
    public static PictureRoom Judge(int contextLength, int used, int pending)
    {
        if (pending <= 0 || contextLength <= 0) return PictureRoom.Fits;
        var room = MessageRoom(contextLength);
        if (pending > room) return PictureRoom.TooLarge;
        return used + pending > room ? PictureRoom.OlderLeftOut : PictureRoom.Fits;
    }

    /// <summary>The line under the meter for <see cref="Judge"/>'s answer; null when it all fits.</summary>
    public static string? RoomLine(PictureRoom room, int contextLength, int pending) => room switch
    {
        PictureRoom.TooLarge =>
            $"These pictures take {pending:N0} tokens, more than a message may have with this model ({MessageRoom(contextLength):N0} of its {contextLength:N0}; " +
            $"the rest is kept for the reply). Remove some, or choose a larger context size in {ModelsPlaces.LibraryName}.",
        PictureRoom.OlderLeftOut => "With these pictures the chat is longer than the model's room, so its oldest messages will be left out to fit.",
        _ => null,
    };

    /// <summary>What the meter adds for the pictures waiting: "· 1,284 for 2 pictures".</summary>
    public static string MeterPart(int pending, int count) =>
        count <= 0 ? "" : $" · {pending:N0} for {(count == 1 ? "the picture" : $"{count} pictures")}";
}
