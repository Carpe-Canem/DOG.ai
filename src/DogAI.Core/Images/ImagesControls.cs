using DogAI.Core.Models;

namespace DogAI.Core.Images;

/// <summary>
/// The Images section's Sampling fold and VAE: the numbers a checkpoint is drawn with there. They start as the
/// checkpoint's own settings card says and are remembered per checkpoint once the owner changes one.
/// </summary>
/// <param name="Vae">A file in the vae folder, by its path inside it; null for the VAE inside the checkpoint.</param>
public sealed record ImagesSampling(string Sampler, string? Scheduler, int Steps, double Guidance, int? ClipSkip, string? Vae = null)
{
    /// <summary>What the checkpoint's settings card says.</summary>
    public static ImagesSampling Of(CheckpointSettings card) => new(card.Sampler,
        string.IsNullOrWhiteSpace(card.Scheduler) ? null : card.Scheduler, card.Steps, card.Guidance, card.ClipSkip,
        string.IsNullOrWhiteSpace(card.Vae) ? null : ImageModelCatalog.Normalise(card.Vae));

    /// <summary>What is wrong with these numbers in plain words, or nothing.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Sampler)) problems.Add("Choose a sampler.");
        else if (Sampler.Length > ImagesControls.MaxNameLength) problems.Add("That sampler's name is too long.");
        if (Scheduler is { Length: > ImagesControls.MaxNameLength }) problems.Add("That scheduler's name is too long.");
        if (Steps is < 1 or > ImageRequest.MaxSteps) problems.Add($"Steps must be between 1 and {ImageRequest.MaxSteps}.");
        if (Guidance is < 0 or > 30 || double.IsNaN(Guidance)) problems.Add("Guidance must be between 0 and 30.");
        if (ClipSkip is < 1 or > 12) problems.Add("Clip skip must be between 1 and 12.");
        if (Vae is not null && !ImageRequest.IsSafeRelativePath(Vae)) problems.Add("The VAE must be a file in the vae folder.");
        return problems;
    }
}

/// <summary>
/// Every control of the Images section except the two prompt boxes and the checkpoint: what the side panel's folds
/// hold. This is what <see cref="ImagesMemory"/> remembers. The prompts are not part of it on purpose: a prompt
/// reaches the disk only inside a kept picture's row, or as a saved prompt the owner chose to save.
/// </summary>
/// <param name="Loras">The ticked LoRAs, each as its path inside the loras folder, with its strength.</param>
/// <param name="Seed">The seed to draw with; null for a new one at every Draw.</param>
/// <param name="HiresOn">Whether the hires fix is used. <paramref name="Hires"/> keeps its numbers while it is off.</param>
/// <param name="Count">Pictures per Draw, 1 to <see cref="ImageRequest.MaxCount"/>.</param>
public sealed record ImagesControls(
    ImagesSampling Sampling,
    int Width,
    int Height,
    IReadOnlyList<LoraUse> Loras,
    long? Seed,
    bool HiresOn,
    HiresFix Hires,
    int Count)
{
    public const int MaxLoras = 20, MaxNameLength = 40;

    /// <summary>The shape a new Images section starts with: Tall, the one people are drawn in.</summary>
    public const PictureShape StartShape = PictureShape.Tall;

    /// <summary>
    /// The controls before the owner has changed anything: the checkpoint's own card, the starting shape at
    /// <paramref name="size"/> (Settings › Pictures, "Start pictures at"), no LoRAs, a new seed each time, hires fix off
    /// (with the numbers Fine uses, ready for when it is switched on), one picture.
    /// </summary>
    public static ImagesControls StartFor(CheckpointSettings card, PictureSize size)
    {
        var (width, height) = PictureLevels.Size(StartShape, size);
        return new(ImagesSampling.Of(card), width, height, [], null, false, PictureLevels.FineHires(card.Steps), 1);
    }

    /// <summary>
    /// The same controls at one of the three shapes (the Tall / Square / Wide shortcuts), at the size shown now (Small
    /// or Normal; Small for a size typed by hand that is neither).
    /// </summary>
    public ImagesControls At(PictureShape shape) => At(shape, Size ?? PictureSize.Small);

    /// <summary>The same controls at one of the two sizes (the Small / Normal shortcuts), in the shape shown now.</summary>
    public ImagesControls At(PictureSize size) => At(Shape ?? PictureTries.ShapeOf(Width, Height), size);

    public ImagesControls At(PictureShape shape, PictureSize size)
    {
        var (width, height) = PictureLevels.Size(shape, size);
        return this with { Width = width, Height = height };
    }

    /// <summary>The shape shortcut the size matches, or null for a size typed by hand.</summary>
    public PictureShape? Shape => PictureLevels.Of(Width, Height)?.Shape;

    /// <summary>The size shortcut the size matches, or null for a size typed by hand.</summary>
    public PictureSize? Size => PictureLevels.Of(Width, Height)?.Size;

    /// <summary>What is wrong with the controls in plain words, or nothing. An empty prompt is not looked at here.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>(Sampling.Problems());
        if (Seed is < 0 or > SeedChoice.Highest) problems.Add(SeedChoice.ProblemText);
        if (Loras.Count > MaxLoras) problems.Add($"At most {MaxLoras} LoRAs can be used in one picture.");
        if (Loras.Select(l => ImageModelCatalog.Normalise(l.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Loras.Count)
            problems.Add("A LoRA is ticked twice.");
        // Everything else is the request's own rules (sizes, strengths, the hires fix, how many pictures).
        problems.AddRange(ImagesDraw.Request("x", "", this with { Sampling = ImagesSampling.Of(Valid), Seed = null }).Problems());
        return [.. problems.Distinct()];
    }

    /// <summary>Numbers nothing is wrong with, so the request's rules can be asked about the rest alone.</summary>
    private static readonly CheckpointSettings Valid = CheckpointSettings.ForFamily(ImageFamily.Sdxl);
}

/// <summary>
/// The raw request of the Images section: what is typed and set, turned into what the engine is sent, with nothing
/// added. No quality words, no rating words, no LoRA trigger words, no tag check: the prompt boxes go out exactly as
/// typed (the owner's decision, 2026-11-15). Everything still draws through <see cref="IImageEngine"/> (the card
/// coordinator), which tells the log filter about the prompt before it is sent.
/// </summary>
public static class ImagesDraw
{
    /// <summary>
    /// One request for everything a Draw asks for: <see cref="ImagesControls.Count"/> pictures, kept the way kept
    /// pictures are (so any of them can be kept as it is). Check <see cref="ImageRequest.Problems"/> before drawing.
    /// </summary>
    public static ImageRequest Request(string prompt, string negativePrompt, ImagesControls controls) => new ImageRequest(
        prompt ?? "",
        negativePrompt ?? "",
        controls.Width, controls.Height,
        controls.Seed ?? -1,
        controls.Sampling.Steps, controls.Sampling.Sampler, controls.Sampling.Scheduler, controls.Sampling.Guidance, controls.Sampling.ClipSkip,
        [.. controls.Loras.Select(l => l with { Path = ImageModelCatalog.Normalise(l.Path) })],
        controls.HiresOn ? controls.Hires : null,
        Count: controls.Count).ForKeeping();

    /// <summary>
    /// The same Draw as one request per picture, to draw one after another so that each shows as soon as it is done and
    /// Cancel keeps the finished ones. With a seed set, the pictures take it, then the next number and so on (the
    /// engine's own rule for several pictures); with a new seed each picture gets its own.
    /// </summary>
    public static IReadOnlyList<ImageRequest> Singles(ImageRequest request) =>
        [.. Enumerable.Range(0, Math.Max(1, request.Count)).Select(i => request with
        {
            Count = 1,
            Seed = request.Seed < 0 ? -1 : (request.Seed + i) % (SeedChoice.Highest + 1),
        })];

    /// <summary>What stops a Draw, in plain words, or nothing: the controls' problems and the request's (an empty prompt).</summary>
    public static IReadOnlyList<string> Problems(string prompt, string negativePrompt, ImagesControls controls) =>
        [.. controls.Problems().Concat(Request(prompt, negativePrompt, controls).Problems()).Distinct()];

    /// <summary>The model to load: the checkpoint with the VAE chosen in the controls and the prediction its card says.</summary>
    public static ImageModel Model(string imageRoot, string checkpoint, CheckpointSettings card, ImagesControls controls) =>
        ImageFolders.ModelFor(imageRoot, ImageModelCatalog.Normalise(checkpoint), controls.Sampling.Vae, card.Prediction);

    /// <summary>
    /// A drawn picture as a row to keep in the Images section. <paramref name="request"/> is the request that drew
    /// <paramref name="result"/>; <paramref name="index"/> is the picture's place in it. A preview can be added before
    /// or after keeping (<see cref="Picture.Preview"/>, <see cref="IPictureStore.SetPreviewAsync"/>).
    /// </summary>
    public static Picture Keep(string checkpoint, CheckpointSettings card, ImagesControls controls, ImageRequest request, ImageResult result, int index = 0) =>
        KeepAs(checkpoint, controls.Sampling.Vae, card.Prediction, request, result, index);

    /// <summary>The same from what a try recorded when it was drawn (the Make tab keeps from that, never from the controls now).</summary>
    public static Picture KeepAs(string checkpoint, string? vae, ImagePrediction prediction, ImageRequest request, ImageResult result, int index = 0) =>
        Picture.Of(PictureOwners.Images, PictureOwners.ImagesOwner, checkpoint, vae, prediction, request, result, index);

    /// <summary>
    /// "Use these settings" and "Make one like this": a kept picture (or a try made into one) as the checkpoint, the two
    /// prompt boxes and the controls that draw it again, seed included. <paramref name="now"/> gives what a picture does
    /// not record (the hires numbers while it is off, how many pictures per Draw).
    /// </summary>
    public static ImagesLike Like(Picture picture, ImagesControls now) => new(
        picture.Checkpoint,
        picture.Prompt,
        picture.NegativePrompt,
        now with
        {
            Sampling = new(picture.Sampler, picture.Scheduler, picture.Steps, picture.Guidance, picture.ClipSkip, picture.Vae),
            Width = picture.Width,
            Height = picture.Height,
            Loras = [.. picture.Loras],
            Seed = picture.Seed is >= 0 and <= SeedChoice.Highest ? picture.Seed : null,
            HiresOn = picture.Hires is not null,
            Hires = picture.Hires ?? now.Hires,
        });
}

/// <summary>What "Make one like this" puts on the Make tab.</summary>
public sealed record ImagesLike(string Checkpoint, string Prompt, string NegativePrompt, ImagesControls Controls);

/// <summary>
/// What the Images section remembers between runs: its checkpoint and its controls. One JSON document of its own in
/// the settings store (key "images"), in <c>InterfaceMemory</c>'s way and apart from <c>AppSettings</c>, so saving it never races a
/// screen that saves settings from an older copy. It goes into backups and the lock covers it.
/// <para>
/// The prompt boxes are never in it. A prompt reaches the disk only inside a kept picture's row, or as a saved prompt
/// (<see cref="SavedPicturePrompts"/>); after a restart the boxes start from <see cref="SavedPicturePrompts.StartingBoxes"/>.
/// </para>
/// Call <see cref="LoadAsync"/> once at startup; after that every lookup is synchronous.
/// </summary>
public sealed class ImagesMemory(IDocumentStore? documents = null)
{
    public const string Key = "images";

    /// <summary>How many checkpoints' sampling numbers are kept at most (a damaged document must not grow without end).</summary>
    public const int MaxCheckpoints = 200;

    private ImagesDocument _doc = new();

    public event Action? Changed;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (documents is null) return;
        _doc = Clean(await documents.LoadAsync<ImagesDocument>(Key, ct));
        Changed?.Invoke();
    }

    /// <summary>
    /// The checkpoint the Images section draws with, as its path inside the checkpoints folder; null until the owner
    /// picks one there, and then it follows the Pictures default (<see cref="ImageModelCatalog.Chosen"/>). Picking one
    /// here does not change that default: portraits and story pictures keep theirs.
    /// </summary>
    public string? Checkpoint => _doc.Checkpoint;

    /// <summary>The checkpoint to draw with among those installed: the one picked here, else the Pictures default, else the first.</summary>
    public string? CheckpointAmong(IReadOnlyList<string> installed, ImageModelCatalog catalog) =>
        installed.FirstOrDefault(c => string.Equals(ImageModelCatalog.Normalise(c), _doc.Checkpoint, StringComparison.OrdinalIgnoreCase))
        ?? catalog.ChosenAmong(installed);

    /// <summary>Picks the Images section's checkpoint; null follows the Pictures default again.</summary>
    public async Task ChooseAsync(string? checkpoint, CancellationToken ct = default)
    {
        if (checkpoint is not null && !ImageRequest.IsSafeRelativePath(checkpoint))
            throw new InvalidOperationException("The checkpoint must be named by its path inside the checkpoints folder.");
        var chosen = checkpoint is null ? null : ImageModelCatalog.Normalise(checkpoint);
        if (chosen == _doc.Checkpoint) return;
        _doc.Checkpoint = chosen;
        await PersistAsync(ct);
    }

    /// <summary>
    /// The controls for a checkpoint: each one as the owner left it, else as <see cref="ImagesControls.StartFor"/>
    /// gives it from the checkpoint's card. The sampling numbers and the VAE are remembered per checkpoint (a card
    /// differs from checkpoint to checkpoint); size, LoRAs, seed, hires fix and the count are shared.
    /// </summary>
    /// <param name="start">Settings › Pictures, "Start pictures at": the size until the owner picks another one here.</param>
    public ImagesControls For(string checkpoint, CheckpointSettings card, PictureSize startSize)
    {
        var start = ImagesControls.StartFor(card, startSize);
        var sampling = _doc.Sampling.TryGetValue(ImageModelCatalog.Normalise(checkpoint), out var saved) ? saved : start.Sampling;
        return new ImagesControls(
            sampling,
            _doc.Width ?? start.Width,
            _doc.Height ?? start.Height,
            [.. _doc.Loras ?? []],
            _doc.Seed,
            _doc.HiresOn,
            _doc.Hires ?? PictureLevels.FineHires(sampling.Steps),
            _doc.Count ?? start.Count);
    }

    /// <summary>True when the checkpoint's sampling numbers here are the owner's, not its card's.</summary>
    public bool SamplingChanged(string checkpoint) => _doc.Sampling.ContainsKey(ImageModelCatalog.Normalise(checkpoint));

    /// <summary>
    /// Remembers the controls as they are now. Sampling numbers equal to the card's are forgotten rather than kept, so
    /// a checkpoint whose numbers the owner never changed goes on following its card (and a changed card reaches it).
    /// Throws <see cref="InvalidOperationException"/> with <see cref="ImagesControls.Problems"/> when something is wrong.
    /// </summary>
    /// <param name="startSize">As for <see cref="For"/>. A size equal to where the section starts is not remembered, so
    /// saving another control never fixes the size: it goes on following Settings until the owner picks another.</param>
    public async Task SaveAsync(string checkpoint, CheckpointSettings card, ImagesControls controls, PictureSize startSize, CancellationToken ct = default)
    {
        if (!ImageRequest.IsSafeRelativePath(checkpoint))
            throw new InvalidOperationException("The checkpoint must be named by its path inside the checkpoints folder.");
        if (controls.Problems() is { Count: > 0 } problems) throw new InvalidOperationException(string.Join(" ", problems));

        var name = ImageModelCatalog.Normalise(checkpoint);
        // Hires numbers nobody set go on following the step count, also when this very save changes the steps.
        var following = _doc.Hires is null && controls.Hires == For(checkpoint, card, startSize).Hires;
        var sampling = controls.Sampling with
        {
            Scheduler = string.IsNullOrWhiteSpace(controls.Sampling.Scheduler) ? null : controls.Sampling.Scheduler,
            Vae = string.IsNullOrWhiteSpace(controls.Sampling.Vae) ? null : ImageModelCatalog.Normalise(controls.Sampling.Vae),
        };
        if (sampling == ImagesSampling.Of(card)) _doc.Sampling.Remove(name);
        else if (_doc.Sampling.ContainsKey(name) || _doc.Sampling.Count < MaxCheckpoints) _doc.Sampling[name] = sampling;

        var start = ImagesControls.StartFor(card, startSize);
        var chosen = (controls.Width, controls.Height) != (start.Width, start.Height);
        _doc.Width = chosen ? controls.Width : null;
        _doc.Height = chosen ? controls.Height : null;
        _doc.SizeChosen = true;
        _doc.Loras = controls.Loras.Count == 0 ? null : [.. controls.Loras.Select(l => l with { Path = ImageModelCatalog.Normalise(l.Path) })];
        _doc.Seed = controls.Seed;
        _doc.HiresOn = controls.HiresOn;
        _doc.Hires = following || controls.Hires == PictureLevels.FineHires(sampling.Steps) ? null : controls.Hires;
        _doc.Count = controls.Count;
        await PersistAsync(ct);
    }

    /// <summary>Forgets the owner's sampling numbers for a checkpoint: it follows its settings card again.</summary>
    public async Task ResetSamplingAsync(string checkpoint, CancellationToken ct = default)
    {
        if (_doc.Sampling.Remove(ImageModelCatalog.Normalise(checkpoint))) await PersistAsync(ct);
    }

    /// <summary>A document read from the store with everything that could not have been saved by this class left out.</summary>
    private static ImagesDocument Clean(ImagesDocument? doc)
    {
        var clean = new ImagesDocument();
        if (doc is null) return clean;
        clean.Checkpoint = ImageRequest.IsSafeRelativePath(doc.Checkpoint) ? ImageModelCatalog.Normalise(doc.Checkpoint!) : null;
        foreach (var (name, sampling) in doc.Sampling ?? [])
        {
            if (clean.Sampling.Count >= MaxCheckpoints) break;
            if (ImageRequest.IsSafeRelativePath(name) && sampling is not null && sampling.Sampler is not null && sampling.Problems().Count == 0)
                clean.Sampling[ImageModelCatalog.Normalise(name)] = sampling;
        }
        bool Side(int? side) => side is >= ImageRequest.MinSide and <= ImageRequest.MaxSide && side % 8 == 0;
        // Before M18 every save wrote the size, chosen or not, so a size from then is forgotten once (the owner's
        // decision, 2026-11-15): the section starts at Settings' size until a size is picked here.
        if (doc.SizeChosen && Side(doc.Width) && Side(doc.Height)) (clean.Width, clean.Height) = (doc.Width, doc.Height);
        clean.SizeChosen = doc.SizeChosen;
        var loras = (doc.Loras ?? [])
            .Where(l => l is not null && ImageRequest.IsSafeRelativePath(l.Path) && l.Strength is >= -5 and <= 5)
            .Select(l => l with { Path = ImageModelCatalog.Normalise(l.Path) })
            .DistinctBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .Take(ImagesControls.MaxLoras).ToList();
        clean.Loras = loras.Count == 0 ? null : loras;
        clean.Seed = doc.Seed is >= 0 and <= SeedChoice.Highest ? doc.Seed : null;
        clean.Count = doc.Count is >= 1 and <= ImageRequest.MaxCount ? doc.Count : null;
        // A hires fix the request would refuse at the remembered size is dropped, and with it the switch.
        if (doc.Hires is { } hires && hires.Upscaler is not null &&
            new ImageRequest("x", Width: clean.Width ?? ImageRequest.MinSide, Height: clean.Height ?? ImageRequest.MinSide, Hires: hires).Problems().Count == 0)
            clean.Hires = hires;
        clean.HiresOn = doc.HiresOn && (doc.Hires is null || clean.Hires is not null);
        return clean;
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        if (documents is not null) await documents.SaveAsync(Key, _doc, ct);
        Changed?.Invoke();
    }

    /// <summary>What is saved. No prompt, and no full path: files are named by their path inside their own folder.</summary>
    public sealed class ImagesDocument
    {
        public string? Checkpoint { get; set; }
        public Dictionary<string, ImagesSampling> Sampling { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int? Width { get; set; }
        public int? Height { get; set; }

        /// <summary>
        /// Written since M18, where a size is saved only when the owner picked one. False in a document from before,
        /// whose size is then not used.
        /// </summary>
        public bool SizeChosen { get; set; }
        public List<LoraUse>? Loras { get; set; }
        public long? Seed { get; set; }
        public bool HiresOn { get; set; }
        public HiresFix? Hires { get; set; }
        public int? Count { get; set; }
    }
}
