using DogAI.Core.Images;

namespace DogAI.Core.Models;

/// <summary>
/// The "Fits beside pictures" mark: a chat model that can stay in graphics memory while a picture is drawn, so asking
/// for a picture in a chat does not set it aside and the next message does not wait for it to load again.
/// <para>
/// It is the graphics-card coordinator's own rule (<see cref="PictureMemory.FitsBeside"/>) asked ahead of time, with
/// estimates in place of measurements: what the chat model needs at the chosen context size
/// (<see cref="FitEstimator"/>), and what a Standard picture needs with the checkpoint in use
/// (<see cref="PictureMemory.Need"/>). Both estimates err on the high side, so a marked model is kept; a model just
/// under the line may be kept too without being marked.
/// </para>
/// <para>
/// The chat model's estimate can be a couple of gigabytes high (seen 2026-11-15: a 9B model at Q8_0 judged at 10.6 GB
/// held 8.3 GB and stayed beside a picture). So once a model has been loaded whole on the card, what it really held
/// at that context size is used instead (<see cref="ModelRecord.MeasuredAt"/>).
/// </para>
/// </summary>
public static class BesidePictures
{
    public const string Label = "Fits beside pictures";

    /// <summary>
    /// What one Standard picture needs with a checkpoint of this size, in the largest of the three shapes and with no
    /// LoRA (they are small beside the rest).
    /// </summary>
    public static long PictureNeed(long checkpointBytes) =>
        Enum.GetValues<PictureShape>().Max(shape =>
        {
            var (width, height) = PictureLevels.Size(shape);
            return PictureMemory.Need(checkpointBytes, 0, new ImageRequest("x", Width: width, Height: height));
        });

    /// <summary>
    /// True when the chat model, loaded whole on the graphics card, leaves room for a picture.
    /// </summary>
    /// <param name="chat">How the chat model was judged at the chosen context size.</param>
    /// <param name="cardBytes">
    /// Graphics memory DOG.ai's models can have: what is free now, plus what its own chat model and picture model hold
    /// (they are what is being planned). What Windows and other programs use is not in it.
    /// </param>
    /// <param name="pictureNeed"><see cref="PictureNeed"/>, or null when there is no picture engine or no checkpoint: then nothing is marked.</param>
    /// <param name="measuredBytes">What the model really held when it was last loaded at this context size, when known; it replaces the estimate.</param>
    public static bool Fits(FitResult? chat, long cardBytes, long? pictureNeed, long? measuredBytes = null) =>
        pictureNeed is > 0 and var picture && chat is { Kind: FitKind.Full }
        && cardBytes - (measuredBytes is > 0 and var measured ? measured : chat.NeededBytes) >= picture + PictureMemory.Margin;

    /// <summary>The line behind the mark's ⓘ.</summary>
    public const string Explanation =
        "It can stay in memory while a picture is drawn. A model without this mark is usually set aside for the picture and loads again with your next message.";

    /// <summary>The line in "How fit is judged".</summary>
    public static string Rule(string checkpoint, long pictureNeed) =>
        $"{Label}: the model, plus about {Sizes.Gb(pictureNeed)} for a Standard picture with {checkpoint}, fits in graphics memory together. " +
        "Other models are set aside while a picture is drawn.";

    /// <summary>The rest of the rule, behind its ⓘ.</summary>
    public const string RuleMore =
        "It is judged from a model’s size until the model has been loaded once at this context size, then from the graphics memory it really took, which is often less. " +
        "A Fine picture needs more room and can still set a marked model aside.";
}

/// <summary>Wording for downloading a model that is judged too large for this PC.</summary>
public static class TooLargeDownload
{
    public const string Title = "Download a model that is too large for this PC?";

    /// <summary>The one confirmation: the size of the download and why the model will not run here.</summary>
    public static string Warning(string name, long downloadBytes, int context, FitResult fit, FitBudget budget) =>
        $"{name} is a {Sizes.Gb(downloadBytes)} download. With a context of {context:N0} tokens it needs about {Sizes.Gb(fit.NeededBytes)} of memory, " +
        $"and this PC has about {Sizes.Gb(budget.VramBytes + budget.RamBytes)} of graphics and system memory free, so DOG.ai will not load it here.";

    public const string Advice = "Download it only if you want the file anyway: for another PC, or for when this one has more memory.";
}
