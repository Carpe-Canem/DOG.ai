using System.Globalization;

namespace DogAI.Core.Images;

/// <summary>
/// One picture drawn on the Images section's Make tab, held in memory only. It keeps everything as it was when drawn
/// (the exact request with the seed the engine used, the checkpoint, VAE and prediction), so Keep and "Use these
/// settings" never read the side panel as it is now: the owner may have changed a control since.
/// </summary>
/// <param name="Number">Counts up from 1 while DOG.ai is open, so tries can be told apart.</param>
/// <param name="Request">The one-picture request sent to the engine, with the seed it drew from.</param>
/// <param name="Took">How long the picture took, from asking the engine to having it.</param>
/// <param name="KeptId">The <see cref="Picture"/> row it was kept as, once kept.</param>
public sealed record ImagesTry(
    int Number,
    string Checkpoint,
    string? Vae,
    ImagePrediction Prediction,
    ImageRequest Request,
    byte[] Image,
    int ImageWidth,
    int ImageHeight,
    TimeSpan Took,
    long? KeptId = null)
{
    /// <summary>The row to keep in the Images section: this picture as it was drawn. Add a preview before keeping it.</summary>
    public Picture ToKeep() => ImagesDraw.KeepAs(Checkpoint, Vae, Prediction, Request, new ImageResult([Image], ImageFormat.Webp, Request.Seed));

    // Kept beside the picture's bytes, not in the record: a record's fields count when two tries are compared.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], string> Addresses = new();

    /// <summary>The picture as a data: address (from memory; never a file), made once per picture.</summary>
    public string Src => Addresses.GetValue(Image, static image => "data:image/webp;base64," + Convert.ToBase64String(image));
}

/// <summary>
/// The Images section's Make tab while DOG.ai is open: the two prompt boxes, the strip of tries and a Draw in progress.
/// All of it is in memory only (a singleton): nothing here is written anywhere, and it is gone after a restart or a lock.
/// A try reaches the disk only when the owner presses Keep (<see cref="ImagesTry.ToKeep"/>, then
/// <see cref="IPictureStore.AddAsync"/>).
/// <para>
/// <see cref="PictureTries"/> (the picture maker's) is not used: it levels every request from its base at a quality and
/// shape, which would replace a typed size, the hires fix and the step count, and it loads the checkpoint with its card's
/// VAE. Here the request goes out as <see cref="ImagesDraw"/> made it.
/// </para>
/// </summary>
public sealed class ImagesSession
{
    /// <summary>
    /// The most tries held at once: four Draws of eight. Each is 100 to 600 KB; the oldest goes first, never the one shown.
    /// </summary>
    public const int Max = 32;

    private readonly List<ImagesTry> _tries = [];
    private int _number;
    private CancellationTokenSource? _cts;
    private int _run;

    /// <summary>Raised when the boxes, the tries or a Draw in progress change. May come from a background thread.</summary>
    public event Action? Changed;

    /// <summary>What is typed in the prompt box. Never saved.</summary>
    public string Prompt { get; private set; } = "";

    /// <summary>What is typed in the negative prompt box. Never saved.</summary>
    public string Negative { get; private set; } = "";

    /// <summary>True once the boxes were filled for this run of DOG.ai (they are filled only once).</summary>
    public bool Started { get; private set; }

    public IReadOnlyList<ImagesTry> Tries => _tries;

    /// <summary>The try shown large: the one picked, else the newest.</summary>
    public ImagesTry? Selected => _selected is { } s && _tries.Contains(s) ? s : _tries.LastOrDefault();
    private ImagesTry? _selected;

    /// <summary>True when the last picture added had to let an old try go.</summary>
    public bool Dropped { get; private set; }

    public bool IsDrawing => _cts is not null;

    /// <summary>The picture being drawn now (1-based) and how many this Draw asks for; 0 when nothing is drawn.</summary>
    public int Drawing { get; private set; }
    public int Of { get; private set; }

    /// <summary>The engine's last report for the picture being drawn, or null before the first.</summary>
    public ImageProgress? Progress { get; private set; }

    /// <summary>The newest in-progress picture (PNG) of the picture being drawn, or null.</summary>
    public byte[]? Preview { get; private set; }

    /// <summary>
    /// Fills the boxes the first time the Make tab opens after a start (with <see cref="SavedPicturePrompts.StartingBoxes"/>).
    /// Later visits keep what is typed, also when the checkpoint is changed.
    /// </summary>
    public void Start(Func<(string Prompt, string Negative)> boxes)
    {
        if (Started) return;
        (Prompt, Negative) = boxes();
        Started = true;
        Changed?.Invoke();
    }

    /// <summary>What is typed now (the page tells it on every key, so leaving and coming back keeps it).</summary>
    public void Type(string prompt, string negative)
    {
        Started = true;
        if (Prompt == prompt && Negative == negative) return;
        (Prompt, Negative) = (prompt ?? "", negative ?? "");
        Changed?.Invoke();
    }

    public void Select(ImagesTry shown)
    {
        _selected = shown;
        Changed?.Invoke();
    }

    /// <summary>Marks a try as kept, so Keep is not offered twice for it.</summary>
    public ImagesTry Kept(ImagesTry kept, long id)
    {
        var index = _tries.IndexOf(kept);
        var marked = kept with { KeptId = id };
        if (index >= 0) _tries[index] = marked;
        if (ReferenceEquals(_selected, kept)) _selected = marked;
        Changed?.Invoke();
        return marked;
    }

    /// <summary>Lets every try go (nothing kept is touched: kept pictures are in the database).</summary>
    public void Clear()
    {
        _tries.Clear();
        _selected = null;
        Changed?.Invoke();
    }

    /// <summary>Stops the Draw in progress. The pictures already finished stay in the strip.</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>
    /// Draws what <paramref name="request"/> asks for (<see cref="ImagesDraw.Request"/>) one picture after another
    /// (<see cref="ImagesDraw.Singles"/>), each into the strip as soon as it is done, through the engine (the
    /// graphics-card coordinator in the app). Cancel or a failure keeps the pictures already finished; the exception
    /// still comes out (<see cref="OperationCanceledException"/> for Cancel). Returns how many were drawn.
    /// </summary>
    /// <param name="model">The checkpoint with the VAE of the controls (<see cref="ImagesDraw.Model"/>).</param>
    /// <exception cref="InvalidOperationException">Another Draw is in progress, or the request has a problem.</exception>
    public async Task<int> DrawAsync(IImageEngine engine, ImageModel model, string checkpoint, string? vae, ImagePrediction prediction,
        ImageRequest request, CancellationToken ct = default)
    {
        if (request.Problems() is { Count: > 0 } problems) throw new InvalidOperationException(string.Join(" ", problems));
        if (_cts is not null) throw new InvalidOperationException("A Draw is already in progress.");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        var run = ++_run;
        var singles = ImagesDraw.Singles(request);
        var drawn = 0;
        try
        {
            Of = singles.Count;
            Set(1, null, null);
            if (engine.Model != model) await engine.LoadAsync(model, cts.Token).ConfigureAwait(false);
            foreach (var single in singles)
            {
                cts.Token.ThrowIfCancellationRequested();
                Set(drawn + 1, null, null);
                var index = drawn + 1;
                var reporter = new Reporter(p =>
                {
                    // A late report, after the picture finished or another Draw began, is about something else.
                    if (run != _run || Drawing != index || _cts is null) return;
                    Progress = p;
                    if (p.Preview is { } preview) Preview = preview;
                    Changed?.Invoke();
                });
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var result = await engine.DrawAsync(single with { Preview = true, PreviewEvery = 4 }, reporter, cts.Token).ConfigureAwait(false);
                var image = result.Images[0];
                var size = WebpInfo.Read(image);
                Add(new ImagesTry(++_number, ImageModelCatalog.Normalise(checkpoint), vae, prediction, single with { Seed = result.Seed },
                    image, size?.Width ?? 0, size?.Height ?? 0, clock.Elapsed));
                drawn++;
            }
            return drawn;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            Set(0, null, null);
            Of = 0;
            Changed?.Invoke();
        }
    }

    private void Set(int drawing, ImageProgress? progress, byte[]? preview)
    {
        Drawing = drawing;
        Progress = progress;
        Preview = preview;
        Changed?.Invoke();
    }

    private void Add(ImagesTry next)
    {
        Dropped = false;
        _tries.Add(next);
        while (_tries.Count > Max && _tries.FindIndex(t => !ReferenceEquals(t, next) && !ReferenceEquals(t, _selected)) is var i and >= 0)
        {
            _tries.RemoveAt(i);
            Dropped = true;
        }
        _selected = next;
        Changed?.Invoke();
    }

    /// <summary>Reports straight through (no captured context): the page switches to its renderer itself.</summary>
    private sealed class Reporter(Action<ImageProgress> report) : IProgress<ImageProgress>
    {
        public void Report(ImageProgress value) => report(value);
    }
}

/// <summary>
/// The Images section's places, fold ids and one-line wording, so the screens stay consistent and the rules can be
/// tested. The side panel's folds are plain <c>Fold</c>s with these ids (remembered in <c>InterfaceMemory</c>).
/// </summary>
public static class ImagesPanels
{
    /// <summary>The Make tab's address.</summary>
    public const string Address = "images";

    /// <summary>The Kept pictures tab's address: reserved for step 3 of M16, no page answers it yet.</summary>
    public const string KeptAddress = "images/kept";

    public const string Model = "images-model", Loras = "images-loras", Size = "images-size", Sampling = "images-sampling",
        Seed = "images-seed", Hires = "images-hires", Count = "images-count";

    /// <summary>Every fold of the side panel in order, and whether it starts open.</summary>
    public static readonly IReadOnlyList<(string Id, string Title, bool Open)> Folds =
    [
        (Model, "Model", true), (Loras, "LoRAs", true), (Size, "Size", true), (Sampling, "Sampling", false),
        (Seed, "Seed", true), (Hires, "Hires fix", false), (Count, "Pictures per Draw", true),
    ];

    public static bool OpensByDefault(string id) => Folds.FirstOrDefault(f => f.Id == id) is { Id: not null } f ? f.Open : true;

    public static string Title(string id) => Folds.First(f => f.Id == id).Title;

    public static string LorasSummary(int ticked) => ticked == 0 ? "None" : $"{ticked} on";

    public static string SizeSummary(int width, int height) => $"{width} × {height}";

    public static string SamplingSummary(ImagesSampling s) => $"{SamplerNames.Title(s.Sampler)} · {s.Steps} steps";

    public static string SeedSummary(long? seed) => seed is { } n ? n.ToString(CultureInfo.InvariantCulture) : "New each time";

    public static string HiresSummary(bool on, HiresFix hires) =>
        on ? $"×{hires.Scale.ToString("0.##", CultureInfo.InvariantCulture)} · {hires.Steps} steps" : "Off";

    public static string CountSummary(int count) => count.ToString(CultureInfo.InvariantCulture);

    /// <summary>The line of facts under the picture shown: "Try 3 · 832 × 1216 · seed 2817463950 · Euler, 28 steps · 31 s".</summary>
    public static string Meta(ImagesTry t) =>
        $"Try {t.Number} · {t.ImageWidth} × {t.ImageHeight} · seed {t.Request.Seed.ToString(CultureInfo.InvariantCulture)} · " +
        $"{SamplerNames.Title(t.Request.Sampler)}, {t.Request.Steps} steps · {Seconds(t.Took)}" + (t.KeptId is null ? "" : " · kept");

    /// <summary>What a Draw in progress says: "Picture 2 of 4 · Drawing, step 12 of 28".</summary>
    public static string Doing(int drawing, int of, string? stage) =>
        (of > 1 ? $"Picture {drawing} of {of}" : "Picture") + (string.IsNullOrEmpty(stage) ? " · Starting…" : " · " + stage);

    private static string Seconds(TimeSpan took) => took.TotalSeconds < 10
        ? took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : Math.Round(took.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
}
