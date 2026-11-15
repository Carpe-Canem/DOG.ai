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

    // A picture is added from the thread that drew it while the page may be drawing the strip. So the list is changed
    // only under _gate, and readers get a copy that never changes (Tries); a List read while it is changed throws.
    private readonly object _gate = new();
    private readonly List<ImagesTry> _tries = [];
    private volatile IReadOnlyList<ImagesTry> _shown = [];
    private int _number;
    private CancellationTokenSource? _cts;
    private int _run;

    /// <summary>Raised when the boxes, the tries or a Draw in progress change. May come from a background thread.</summary>
    public event Action? Changed;

    /// <summary>What is typed in the prompt box. Never saved.</summary>
    public string Prompt { get; private set; } = "";

    /// <summary>What is typed in the negative prompt box. Never saved.</summary>
    public string Negative { get; private set; } = "";

    /// <summary>What was typed into "Write it for me…" (the prompt helper's sentence). Never saved.</summary>
    public string Sentence { get; private set; } = "";

    /// <summary>The helper's sentence as typed (the dialog tells it on every key, so closing and opening it again keeps it).</summary>
    public void Describe(string sentence) => Sentence = sentence ?? "";

    /// <summary>True once the boxes were filled for this run of DOG.ai (they are filled only once).</summary>
    public bool Started { get; private set; }

    /// <summary>The tries, oldest first, as they are now: a list of its own that later changes do not touch.</summary>
    public IReadOnlyList<ImagesTry> Tries => _shown;

    /// <summary>The try shown large: the one picked, else the newest.</summary>
    public ImagesTry? Selected
    {
        get
        {
            var (all, picked) = (_shown, _selected);
            return picked is not null && all.Contains(picked) ? picked : all.LastOrDefault();
        }
    }
    private volatile ImagesTry? _selected;

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
        var marked = kept with { KeptId = id };
        lock (_gate)
        {
            var index = _tries.IndexOf(kept);
            if (index >= 0) _tries[index] = marked;
            if (ReferenceEquals(_selected, kept)) _selected = marked;
            _shown = [.. _tries];
        }
        Changed?.Invoke();
        return marked;
    }

    /// <summary>
    /// A kept picture was removed (Kept pictures › Remove): any try kept as that row is marked not kept again, so Keep is
    /// offered for it once more. True when a try was marked.
    /// </summary>
    public bool Unkept(long id)
    {
        var any = false;
        lock (_gate)
        {
            for (var i = 0; i < _tries.Count; i++)
            {
                if (_tries[i].KeptId != id) continue;
                var was = _tries[i];
                _tries[i] = was with { KeptId = null };
                if (ReferenceEquals(_selected, was)) _selected = _tries[i];
                any = true;
            }
            if (any) _shown = [.. _tries];
        }
        if (any) Changed?.Invoke();
        return any;
    }

    /// <summary>Lets every try go (nothing kept is touched: kept pictures are in the database).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _tries.Clear();
            _selected = null;
            _shown = [];
        }
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
        var singles = ImagesDraw.Singles(request);
        var cts = Take(ct);
        var run = ++_run;
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
            Interlocked.CompareExchange(ref _cts, null, cts);
            cts.Dispose();
            Set(0, null, null);
            Of = 0;
            Changed?.Invoke();
        }
    }

    /// <summary>True while the Draw in progress is an Upscale.</summary>
    public bool IsUpscaling { get; private set; }

    /// <summary>
    /// Upscale: makes a larger picture from <paramref name="source"/> and returns it as a try, in memory like every
    /// try. It counts as a Draw (one at a time; <see cref="Cancel"/> stops it; progress is reported the same way).
    /// <see cref="UpscaleWay.Redraw"/> draws the picture again at <paramref name="scale"/> with its own model;
    /// <see cref="UpscaleWay.Enlarge"/> hands the picture to the upscaler file <paramref name="upscaler"/> and keeps
    /// the request it was drawn with.
    /// </summary>
    /// <param name="model">The picture's own model (a redraw loads it; enlarging uses whatever is chosen, else this).</param>
    /// <param name="add">True to put the result into the strip (the Make tab); false to hand it back only (a kept picture).</param>
    /// <exception cref="InvalidOperationException">Another Draw is in progress, or what was asked for cannot be done.</exception>
    public async Task<ImagesTry> UpscaleAsync(IImageEngine engine, ImageModel model, UpscaleSource source, UpscaleWay way,
        double scale, string? upscaler, bool add, CancellationToken ct = default)
    {
        var redraw = way == UpscaleWay.Redraw ? PictureUpscale.Redraw(source.Request, scale) : null;
        if (redraw?.Problems() is { Count: > 0 } problems) throw new InvalidOperationException(string.Join(" ", problems));
        if (redraw is { Seed: < 0 }) throw new InvalidOperationException("The picture's seed is not known, so it cannot be drawn again.");
        if (redraw is null && !ImageEnlarge.IsUpscalerName(upscaler)) throw new InvalidOperationException("Choose an upscaler file.");
        var cts = Take(ct);
        var run = ++_run;
        try
        {
            IsUpscaling = true;
            Of = 1;
            Set(1, null, null);
            var reporter = new Reporter(p =>
            {
                if (run != _run || _cts is null) return;
                Progress = p;
                if (p.Preview is { } preview) Preview = preview;
                Changed?.Invoke();
            });
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var checkpoint = ImageModelCatalog.Normalise(source.Checkpoint);
            ImagesTry made;
            if (redraw is not null)
            {
                if (engine.Model != model) await engine.LoadAsync(model, cts.Token).ConfigureAwait(false);
                var result = await engine.DrawAsync(redraw with { Preview = true, PreviewEvery = 4 }, reporter, cts.Token).ConfigureAwait(false);
                var image = result.Images[0];
                var size = WebpInfo.Read(image);
                made = new ImagesTry(add ? ++_number : 0, checkpoint, source.Vae, source.Prediction, redraw with { Seed = result.Seed },
                    image, size?.Width ?? 0, size?.Height ?? 0, clock.Elapsed);
            }
            else
            {
                // Enlarging draws nothing, so the checkpoint in use stays; the engine only has to be running.
                if (engine.Model is null) await engine.LoadAsync(model, cts.Token).ConfigureAwait(false);
                var larger = await engine.EnlargeAsync(new ImageEnlarge(source.Image, upscaler!), reporter, cts.Token).ConfigureAwait(false);
                made = new ImagesTry(add ? ++_number : 0, checkpoint, source.Vae, source.Prediction, source.Request with { Count = 1, Preview = false },
                    larger.Image, larger.Width, larger.Height, clock.Elapsed);
            }
            if (add) Add(made);
            return made;
        }
        finally
        {
            Interlocked.CompareExchange(ref _cts, null, cts);
            cts.Dispose();
            IsUpscaling = false;
            Set(0, null, null);
            Of = 0;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Takes the one place a Draw or an Upscale has. Checked and taken in one step: two calls that arrive together
    /// (they come from pool threads) cannot both get it.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another Draw is in progress.</exception>
    private CancellationTokenSource Take(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (Interlocked.CompareExchange(ref _cts, cts, null) is null) return cts;
        cts.Dispose();
        throw new InvalidOperationException("A Draw is already in progress.");
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
        lock (_gate)
        {
            Dropped = false;
            _tries.Add(next);
            while (_tries.Count > Max && _tries.FindIndex(t => !ReferenceEquals(t, next) && !ReferenceEquals(t, _selected)) is var i and >= 0)
            {
                _tries.RemoveAt(i);
                Dropped = true;
            }
            _selected = next;
            _shown = [.. _tries];
        }
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

    /// <summary>The Kept pictures tab's address (a second route of the same page, so a Draw goes on while it is open).</summary>
    public const string KeptAddress = "images/kept";

    /// <summary>The two tabs in the header, as (address, title).</summary>
    public static readonly IReadOnlyList<(string Id, string Title)> Tabs = [(Address, "Make"), (KeptAddress, "Kept pictures")];

    /// <summary>The short note in the side panel of the Kept pictures tab (in place of the controls).</summary>
    public const string KeptAbout = "Kept inside DOG.ai, and locked with the rest when the lock is on. A picture leaves only through Save as a file.";

    /// <summary>How a checkpoint is named in the filter and the facts: its file name without the folder or ".safetensors".</summary>
    public static string CheckpointName(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "Unknown checkpoint" : System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]);

    /// <summary>
    /// The line under the grid: "18 kept pictures", "312 kept pictures · showing 40", "3 found", or what it says when there
    /// is nothing. <paramref name="filtered"/> is true when words, a checkpoint or a shape narrow the list.
    /// </summary>
    public static string KeptCount(int total, int shown, bool filtered)
    {
        if (total <= 0) return filtered ? "No kept picture matches." : "No kept pictures yet. Pictures you keep on the Make tab show here.";
        var count = total.ToString("N0", CultureInfo.InvariantCulture);
        var head = filtered ? $"{count} found" : total == 1 ? "1 kept picture" : $"{count} kept pictures";
        return shown < total ? $"{head} · showing {shown.ToString("N0", CultureInfo.InvariantCulture)}" : head;
    }

    /// <summary>
    /// The facts of a kept picture in its large view: "832 × 1216 · seed 2817463950 · Euler, 28 steps · guidance 5.5 ·
    /// hires ×1.5 · illustrious · kept 15 Nov 2026, 14:03". <paramref name="keptLocal"/> is when it was kept, in local time.
    /// </summary>
    public static string KeptMeta(Picture p, DateTime keptLocal)
    {
        var parts = new List<string>
        {
            $"{p.ImageWidth} × {p.ImageHeight}",
            "seed " + p.Seed.ToString(CultureInfo.InvariantCulture),
            $"{SamplerNames.Title(p.Sampler)}, {p.Steps} steps",
            "guidance " + p.Guidance.ToString("0.##", CultureInfo.InvariantCulture),
        };
        if (p.Hires is { } h) parts.Add("hires ×" + h.Scale.ToString("0.##", CultureInfo.InvariantCulture));
        if (PictureUpscale.EnlargedBy(p) is { } by) parts.Add($"enlarged ×{by}");
        if (p.Loras.Count > 0) parts.Add(p.Loras.Count == 1 ? "1 LoRA" : $"{p.Loras.Count} LoRAs");
        parts.Add(CheckpointName(p.Checkpoint));
        parts.Add("kept " + keptLocal.ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }

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

    /// <summary>"Small · 640 × 960", or the pixels alone for a size typed by hand.</summary>
    public static string SizeSummary(int width, int height) =>
        (PictureLevels.Of(width, height) is { } named ? $"{named.Size} · " : "") + $"{width} × {height}";

    public static string SamplingSummary(ImagesSampling s) => $"{SamplerNames.Title(s.Sampler)} · {s.Steps} steps";

    public static string SeedSummary(long? seed) => seed is { } n ? n.ToString(CultureInfo.InvariantCulture) : "New each time";

    public static string HiresSummary(bool on, HiresFix hires) =>
        on ? $"×{hires.Scale.ToString("0.##", CultureInfo.InvariantCulture)} · {hires.Steps} steps" : "Off";

    public static string CountSummary(int count) => count.ToString(CultureInfo.InvariantCulture);

    /// <summary>The line of facts under the picture shown: "Try 3 · 832 × 1216 · seed 2817463950 · Euler, 28 steps · 31 s".</summary>
    public static string Meta(ImagesTry t) =>
        $"Try {t.Number} · {t.ImageWidth} × {t.ImageHeight} · " + (PictureUpscale.EnlargedBy(t) is { } by ? $"enlarged ×{by} · " : "") +
        $"seed {t.Request.Seed.ToString(CultureInfo.InvariantCulture)} · " +
        $"{SamplerNames.Title(t.Request.Sampler)}, {t.Request.Steps} steps · {Seconds(t.Took)}" + (t.KeptId is null ? "" : " · kept");

    /// <summary>What a Draw in progress says: "Picture 2 of 4 · Drawing, step 12 of 28".</summary>
    public static string Doing(int drawing, int of, string? stage) =>
        (of > 1 ? $"Picture {drawing} of {of}" : "Picture") + (string.IsNullOrEmpty(stage) ? " · Starting…" : " · " + stage);

    /// <summary>What an Upscale in progress says: "Upscale · Refining the enlarged picture, step 3 of 14…".</summary>
    public static string Upscaling(string? stage) => "Upscale" + (string.IsNullOrEmpty(stage) ? " · Starting…" : " · " + stage);

    /// <summary>How long something took, as the Images section writes it: "8.8 s", "25 s".</summary>
    public static string Seconds(TimeSpan took) => took.TotalSeconds < 10
        ? took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
        : Math.Round(took.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
}
