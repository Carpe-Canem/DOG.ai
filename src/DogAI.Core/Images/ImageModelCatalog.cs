using DogAI.Core.Models;

namespace DogAI.Core.Images;

/// <summary>
/// The image models folder: <c>&lt;models&gt;\image</c> with one subfolder per kind of file. The subfolders match
/// sd-server's own layout, so loading through its API stays possible later.
/// </summary>
public static class ImageFolders
{
    public const string Name = "image";
    public const string Checkpoints = "checkpoints";
    public const string Loras = "loras";
    public const string Vae = "vae";
    public const string Upscalers = "upscalers";

    public static IReadOnlyList<string> All { get; } = [Checkpoints, Loras, Vae, Upscalers];

    /// <summary>The folder a kind of file belongs in, or null when it belongs nowhere.</summary>
    public static string? For(ImageFileKind kind) => kind switch
    {
        ImageFileKind.Checkpoint => Checkpoints,
        ImageFileKind.Lora => Loras,
        ImageFileKind.Vae => Vae,
        ImageFileKind.Upscaler => Upscalers,
        _ => null,
    };

    /// <summary>
    /// The model the engine loads for a checkpoint with these settings. <paramref name="root"/> is the image models folder,
    /// <paramref name="checkpoint"/> a path relative to its checkpoints folder.
    /// </summary>
    public static ImageModel ModelFor(string root, string checkpoint, CheckpointSettings settings) => new(
        Path.Combine(root, Checkpoints, checkpoint.Replace('/', Path.DirectorySeparatorChar)),
        Path.Combine(root, Loras),
        settings.Vae is { } vae ? Path.Combine(root, Vae, vae.Replace('/', Path.DirectorySeparatorChar)) : null,
        settings.Prediction);
}

/// <summary>What is saved: settings the owner changed, by file, and the checkpoint chosen to draw with.</summary>
/// <remarks>
/// Files are named by their path inside their own folder ("illustrious.safetensors", "style/ink.safetensors"), never by a
/// full path, so moving the models folder or a portable install needs no rewriting.
/// </remarks>
public sealed class ImageModelDocument
{
    public Dictionary<string, CheckpointSettings> Checkpoints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, LoraSettings> Loras { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Chosen { get; set; }
}

/// <summary>
/// Settings cards for checkpoints and LoRAs, and which checkpoint draws. Kept as one JSON document in the settings store
/// (key "image-models"), beside the assistants and prompts: it goes into backups, the lock covers it, and it needs no
/// table. A card is saved only once the owner changes it; until then it follows its family's defaults, so better defaults
/// in a later version reach every untouched checkpoint.
/// </summary>
public sealed class ImageModelCatalog(IDocumentStore? documents = null)
{
    public const string Key = "image-models";

    private ImageModelDocument _doc = new();

    public event Action? Changed;

    public string? Chosen => _doc.Chosen;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (documents is null) return;
        var doc = await documents.LoadAsync<ImageModelDocument>(Key, ct);
        _doc = new ImageModelDocument
        {
            Checkpoints = new(doc?.Checkpoints ?? [], StringComparer.OrdinalIgnoreCase),
            Loras = new(doc?.Loras ?? [], StringComparer.OrdinalIgnoreCase),
            Chosen = doc?.Chosen,
        };
        Changed?.Invoke();
    }

    /// <summary>The saved card, or the family's defaults when the owner has not changed it.</summary>
    public CheckpointSettings CheckpointFor(string checkpoint, ImageFileVerdict verdict) =>
        _doc.Checkpoints.TryGetValue(Normalise(checkpoint), out var saved) ? saved
            : CheckpointSettings.ForFamily(verdict.Family ?? ImageFamily.Sdxl, verdict.VPrediction);

    public bool IsChanged(string checkpoint) => _doc.Checkpoints.ContainsKey(Normalise(checkpoint));

    public async Task SaveCheckpointAsync(string checkpoint, CheckpointSettings settings, CancellationToken ct = default)
    {
        if (settings.Problems() is { Count: > 0 } problems) throw new InvalidOperationException(string.Join(" ", problems));
        _doc.Checkpoints[Normalise(checkpoint)] = settings with
        {
            QualityBefore = settings.QualityBefore.Trim(),
            QualityAfter = settings.QualityAfter.Trim(),
            Negative = settings.Negative.Trim(),
            Vae = string.IsNullOrWhiteSpace(settings.Vae) ? null : settings.Vae,
            Scheduler = string.IsNullOrWhiteSpace(settings.Scheduler) ? null : settings.Scheduler,
        };
        await PersistAsync(ct);
    }

    /// <summary>Forgets the owner's changes: the card follows its family's defaults again.</summary>
    public async Task ResetCheckpointAsync(string checkpoint, CancellationToken ct = default)
    {
        if (_doc.Checkpoints.Remove(Normalise(checkpoint))) await PersistAsync(ct);
    }

    /// <summary>The saved card, or the trigger words found in the file at strength 1.</summary>
    public LoraSettings LoraFor(string lora, LoraFacts? facts) =>
        _doc.Loras.TryGetValue(Normalise(lora), out var saved) ? saved : new LoraSettings(string.Join(", ", facts?.Triggers ?? []));

    public bool IsLoraChanged(string lora) => _doc.Loras.ContainsKey(Normalise(lora));

    public async Task SaveLoraAsync(string lora, LoraSettings settings, CancellationToken ct = default)
    {
        if (settings.Problems() is { Count: > 0 } problems) throw new InvalidOperationException(string.Join(" ", problems));
        _doc.Loras[Normalise(lora)] = settings with { TriggerWords = settings.TriggerWords.Trim() };
        await PersistAsync(ct);
    }

    public async Task ResetLoraAsync(string lora, CancellationToken ct = default)
    {
        if (_doc.Loras.Remove(Normalise(lora))) await PersistAsync(ct);
    }

    public async Task ChooseAsync(string? checkpoint, CancellationToken ct = default)
    {
        _doc.Chosen = checkpoint is null ? null : Normalise(checkpoint);
        await PersistAsync(ct);
    }

    /// <summary>The checkpoint to draw with among those installed: the chosen one, else the first.</summary>
    public string? ChosenAmong(IReadOnlyList<string> installed) =>
        installed.FirstOrDefault(c => string.Equals(Normalise(c), _doc.Chosen, StringComparison.OrdinalIgnoreCase)) ?? installed.FirstOrDefault();

    /// <summary>Paths inside a folder are kept with forward slashes, so the same file always has the same name.</summary>
    public static string Normalise(string relative) => relative.Replace('\\', '/');

    private async Task PersistAsync(CancellationToken ct)
    {
        if (documents is not null) await documents.SaveAsync(Key, _doc, ct);
        Changed?.Invoke();
    }
}

/// <summary>Puts a picture request together from a checkpoint's card, the LoRAs chosen and what to draw.</summary>
public static class ImagePrompt
{
    /// <summary>The fixed subject of the test picture on Models → Images, in each prompt style.</summary>
    public static string TestSubject(PromptStyle style) => style == PromptStyle.Tags
        ? "1girl, solo, silver hair, green eyes, leather armor, green cloak, forest, dappled sunlight, upper body"
        : "A woman with silver hair and green eyes, wearing leather armor and a green cloak, standing in a forest in dappled sunlight";

    /// <summary>
    /// Quality words before, the LoRAs' trigger words, the subject, quality words after; the card's negative prompt and
    /// numbers; each LoRA at its strength. Empty pieces are left out.
    /// </summary>
    public static ImageRequest Compose(CheckpointSettings settings, string subject, IReadOnlyList<(string Path, LoraSettings Settings)>? loras = null,
        int width = 832, int height = 1216, long seed = -1)
    {
        loras ??= [];
        var pieces = new[] { settings.QualityBefore }
            .Concat(loras.Select(l => l.Settings.TriggerWords))
            .Append(subject)
            .Append(settings.QualityAfter)
            .Select(p => p.Trim().Trim(',').Trim())
            .Where(p => p.Length > 0);
        return new ImageRequest(
            string.Join(", ", pieces),
            settings.Negative.Trim(),
            width, height, seed,
            settings.Steps, settings.Sampler, settings.Scheduler, settings.Guidance, settings.ClipSkip,
            loras.Select(l => new LoraUse(ImageModelCatalog.Normalise(l.Path), l.Settings.Strength)).ToList());
    }
}
