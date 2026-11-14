namespace DogAI.Core.Images;

/// <summary>The SDXL families DOG.ai knows. Every SDXL checkpoint has the same shape, so the family is told by name.</summary>
public enum ImageFamily
{
    /// <summary>Plain SDXL or a family DOG.ai does not recognise: prompts in sentences.</summary>
    Sdxl,
    Illustrious,
    NoobAI,
    Pony,
}

/// <summary>How a checkpoint wants its prompt written.</summary>
public enum PromptStyle
{
    /// <summary>Danbooru-style tags separated by commas ("1girl, silver hair, forest").</summary>
    Tags,

    /// <summary>Plain sentences ("A woman with silver hair standing in a forest").</summary>
    Sentences,
}

/// <summary>The engine's sampler and scheduler names, as sd-server takes them (read from its --help, build master-1094).</summary>
public static class SamplerNames
{
    public static IReadOnlyList<(string Id, string Title)> Samplers { get; } =
    [
        ("euler_a", "Euler a"),
        ("euler", "Euler"),
        ("dpm++2m", "DPM++ 2M"),
        ("dpm++2s_a", "DPM++ 2S a"),
        ("dpm++2m_sde", "DPM++ 2M SDE"),
        ("dpm2", "DPM2"),
        ("heun", "Heun"),
        ("ipndm", "iPNDM"),
        ("lcm", "LCM"),
        ("ddim_trailing", "DDIM"),
        ("tcd", "TCD"),
    ];

    /// <summary>Null is the engine's own choice for the sampler.</summary>
    public static IReadOnlyList<(string? Id, string Title)> Schedulers { get; } =
    [
        (null, "Engine default"),
        ("karras", "Karras"),
        ("discrete", "Discrete"),
        ("exponential", "Exponential"),
        ("sgm_uniform", "SGM uniform"),
        ("simple", "Simple"),
        ("ays", "Align Your Steps"),
    ];

    public static string Title(string id) => Samplers.FirstOrDefault(s => s.Id == id).Title ?? id;
}

/// <summary>
/// What a checkpoint is drawn with. Known families start from <see cref="ForFamily"/>; the owner can change any of it on
/// the checkpoint's settings card. Saved as JSON in the settings store (see <see cref="ImageModelCatalog"/>).
/// </summary>
/// <param name="QualityBefore">Words put before every prompt ("masterpiece, best quality…").</param>
/// <param name="QualityAfter">Words put after every prompt.</param>
/// <param name="Vae">A file in the image models' vae folder, by name; null for the VAE inside the checkpoint.</param>
public sealed record CheckpointSettings(
    ImageFamily Family,
    PromptStyle Style,
    string QualityBefore,
    string QualityAfter,
    string Negative,
    string Sampler,
    string? Scheduler,
    int Steps,
    double Guidance,
    int? ClipSkip,
    ImagePrediction Prediction,
    string? Vae = null)
{
    public const int MinSteps = 1;
    public const int MaxSteps = ImageRequest.MaxSteps;

    /// <summary>
    /// The starting settings for a family. Sources: Illustrious from the M9 trial (docs/PLAN.md); NoobAI and NoobAI V-Pred
    /// from Laxhar's model cards on Hugging Face (2026-11-14); Pony from the author's model page (via its Hugging Face
    /// mirror); plain SDXL is the usual SDXL starting point. All to be tuned in M9 step 6.
    /// </summary>
    public static CheckpointSettings ForFamily(ImageFamily family, bool vPrediction = false) => family switch
    {
        ImageFamily.Illustrious => new(family, PromptStyle.Tags,
            "masterpiece, best quality, amazing quality, very aesthetic, absurdres", "",
            "worst quality, low quality, bad quality, lowres, bad anatomy, bad hands, jpeg artifacts, signature, watermark, text",
            "euler_a", null, 28, 5.5, 2, vPrediction ? ImagePrediction.V : ImagePrediction.Auto),
        // The card's negative also lists a rating word and furry terms; ratings come with Settings → Pictures (step 7)
        // and the subject is the owner's choice, so only the quality part is kept.
        ImageFamily.NoobAI when vPrediction => new(family, PromptStyle.Tags,
            "masterpiece, best quality, newest, absurdres, highres", "",
            "worst quality, old, early, low quality, lowres, signature, username, logo, bad hands, mutated hands",
            "euler", null, 30, 4.5, 2, ImagePrediction.V),
        ImageFamily.NoobAI => new(family, PromptStyle.Tags,
            "masterpiece, best quality, newest, absurdres, highres", "",
            "worst quality, old, early, low quality, lowres, signature, username, logo, bad hands, mutated hands",
            "euler_a", null, 28, 5.5, 2, ImagePrediction.Auto),
        ImageFamily.Pony => new(family, PromptStyle.Tags,
            "score_9, score_8_up, score_7_up, score_6_up, score_5_up, score_4_up", "",
            "",
            "euler_a", null, 25, 7, 2, vPrediction ? ImagePrediction.V : ImagePrediction.Auto),
        _ => new(ImageFamily.Sdxl, PromptStyle.Sentences,
            "", "high quality, detailed",
            "lowres, blurry, worst quality, low quality, watermark, text, signature",
            "dpm++2m", "karras", 30, 7, null, vPrediction ? ImagePrediction.V : ImagePrediction.Auto),
    };

    /// <summary>What is wrong with these settings in plain words, or nothing.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Steps is < MinSteps or > MaxSteps) problems.Add($"Steps must be between {MinSteps} and {MaxSteps}.");
        if (Guidance is < 0 or > 30 || double.IsNaN(Guidance)) problems.Add("Guidance must be between 0 and 30.");
        if (ClipSkip is < 1 or > 12) problems.Add("Clip skip must be between 1 and 12.");
        if (string.IsNullOrWhiteSpace(Sampler)) problems.Add("Choose a sampler.");
        if (Vae is not null && !ImageRequest.IsSafeRelativePath(Vae)) problems.Add("The VAE must be a file in the vae folder.");
        return problems;
    }
}

/// <summary>A LoRA's own settings: the words that call it up, and how strongly it is used unless a picture says otherwise.</summary>
public sealed record LoraSettings(string TriggerWords, double Strength = 1.0)
{
    public IReadOnlyList<string> Problems() =>
        Strength is < -5 or > 5 || double.IsNaN(Strength) ? ["The strength must be between -5 and 5."] : [];
}

public static class FamilyNames
{
    public static string Title(ImageFamily family) => family switch
    {
        ImageFamily.Illustrious => "Illustrious",
        ImageFamily.NoobAI => "NoobAI",
        ImageFamily.Pony => "Pony",
        _ => "SDXL",
    };
}
