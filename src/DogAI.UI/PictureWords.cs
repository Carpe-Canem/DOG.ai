using DogAI.Core.Images;

namespace DogAI.UI;

/// <summary>What a picture is doing, in words, for every screen that draws one (the picture maker, the test picture in Models).</summary>
public static class PictureWords
{
    /// <summary>The line shown while a picture waits, makes room or is drawn.</summary>
    public static string Of(ImageProgress p) => p.Stage switch
    {
        CardCoordinator.Waiting => "Waiting for the graphics card (a reply or another picture is using it)…",
        CardCoordinator.MakingRoom => "Setting the chat model aside to make room…",
        "sampling" when p.Pass > 1 => $"Refining the enlarged picture, step {p.Step} of {p.TotalSteps}…",
        "sampling" => $"Drawing, step {p.Step} of {p.TotalSteps}…",
        CardCoordinator.Enlarging => "Enlarging the picture with the upscaler file (about ten seconds)…",
        "upscaling" or "vae_encode" => "Enlarging the picture…",
        "conditioning" or "preparing" => "Reading the prompt (the first picture also loads the checkpoint, a few seconds)…",
        _ => "Finishing the picture…",
    };
}
