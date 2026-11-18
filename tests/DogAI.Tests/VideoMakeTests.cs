using DogAI.Core.Chat;
using DogAI.Core.Images;
using DogAI.Core.Search;
using DogAI.Core.Video;

namespace DogAI.Tests;

/// <summary>The Video section's Make tab (M20 step 4): the session, the remembered controls, the bar, the first frame and the card rule.</summary>
public class VideoMakeTests : IDisposable
{
    private const long Gb = FakeCard.Gb;
    private const long MiB = 1L << 20;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-videomake-" + Guid.NewGuid().ToString("N"));
    private readonly VideoModel _model;

    public VideoMakeTests()
    {
        _model = VideoFolders.ModelFor(_root, VideoFiles.Wan22);
        foreach (var (path, _) in _model.Files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static VideoRequest Quick(long seed = -1, byte[]? frame = null, PictureShape shape = PictureShape.Wide) =>
        VideoRequest.For("A fox runs through the snow.", VideoQuality.Quick, shape, seed, frame);

    private static VideoEstimate Estimate(VideoRequest r) => VideoEstimate.For(r);

    // ── The session ──

    [Fact]
    public async Task A_clip_is_made_with_previews_and_kept_in_memory_with_the_seed_the_engine_used()
    {
        var session = new VideoSession();
        var engine = new FakeVideoEngine();
        var request = Quick();
        var reports = new List<VideoProgress?>();
        session.Changed += () => reports.Add(session.Progress);

        var made = await session.MakeAsync(engine, _model, request, Estimate(request));

        // Previews are asked for; what is kept is the request with the seed the clip was made from.
        Assert.True(engine.Made[0].Request.Preview);
        Assert.Equal(4321, made.Result.Seed);
        Assert.Equal(4321, made.Request.Seed);
        Assert.False(made.Request.Preview);
        Assert.Equal(request with { Seed = 4321 }, made.Request);
        Assert.Equal(1, made.Number);
        Assert.Equal(Estimate(request).Total, made.Expected);
        Assert.Same(made, session.Selected);
        Assert.Equal([made], session.Tries);
        Assert.Contains(reports, p => p is { Stage: VideoStages.Drawing, Step: 2, TotalSteps: 3 });
        // Nothing of the clip in progress stays once it is done.
        Assert.False(session.IsMaking);
        Assert.Null(session.Progress);
        Assert.Null(session.Estimate);
        Assert.Null(session.Making);
        Assert.Equal(0, session.BarAt(DateTime.UtcNow));

        // The clip as kept: the Video section's own row, which the store would take as it is.
        var row = made.ToKeep();
        Assert.Equal((ClipOwners.Video, ClipOwners.VideoOwner), (row.OwnerKind, row.OwnerId));
        Assert.Equal("A fox runs through the snow.", row.Prompt);
        Assert.Equal(4321, row.Seed);
        Assert.Same(made.Result.Webm, row.Video);
        Assert.Empty(row.Problems());
        Assert.Equal(VideoFiles.Wan22, row.Files);
    }

    [Fact]
    public async Task A_clip_from_a_picture_carries_its_first_frame_and_keeps_it()
    {
        var frame = TestJpegs.Make(1216, 832);
        var session = new VideoSession();
        var request = Quick(seed: 7, frame: frame);
        var made = await session.MakeAsync(new FakeVideoEngine(), _model, request, Estimate(request));
        Assert.True(made.FromPicture);
        Assert.Same(frame, made.ToKeep().StartFrame);
        Assert.Empty(made.ToKeep().Problems());
        Assert.Contains("from a picture", VideoPanels.Meta(made));
    }

    [Fact]
    public async Task Only_a_few_clips_are_held_and_the_one_shown_is_never_let_go()
    {
        var session = new VideoSession();
        var engine = new FakeVideoEngine();
        var first = await session.MakeAsync(engine, _model, Quick(1), Estimate(Quick(1)));
        for (var i = 2; i <= VideoSession.Max; i++) await session.MakeAsync(engine, _model, Quick(i), Estimate(Quick(i)));
        Assert.False(session.Dropped);
        // The owner is looking at the first when the next one arrives: the oldest of the others goes, never that one.
        session.Select(first);
        await session.MakeAsync(engine, _model, Quick(9), Estimate(Quick(9)));

        Assert.Equal(VideoSession.Max, session.Tries.Count);
        Assert.True(session.Dropped);
        Assert.Equal([1, 3, 4, 5], session.Tries.Select(t => t.Number));
        // The new clip is the one shown.
        Assert.Equal(5, session.Selected!.Number);
        Assert.Equal(4, VideoSession.Max);
    }

    [Fact]
    public async Task Cancel_stops_the_clip_and_two_clips_never_run_at_once()
    {
        var session = new VideoSession();
        var engine = new FakeVideoEngine { Hold = new TaskCompletionSource() };
        var making = Task.Run(() => session.MakeAsync(engine, _model, Quick(), Estimate(Quick())));
        await engine.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(session.IsMaking);
        Assert.NotNull(session.Estimate);
        Assert.Equal(VideoDecoder.Fast, session.Making!.Decoder);

        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => session.MakeAsync(engine, _model, Quick(), Estimate(Quick())));
        Assert.Equal("A video is already being made.", again.Message);

        session.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => making);
        Assert.False(session.IsMaking);
        Assert.Empty(session.Tries);
        Assert.Single(engine.Made);

        // A request with a problem reaches nothing.
        var empty = await Assert.ThrowsAsync<InvalidOperationException>(() => session.MakeAsync(engine, _model, Quick() with { Prompt = " " }, Estimate(Quick())));
        Assert.Contains("Say in a sentence", empty.Message);
        Assert.Single(engine.Made);

        // A refusal comes out as it is, and leaves no try.
        engine.Hold = null;
        engine.FailWith = "A video needs about 15.0 GB of graphics memory.";
        var refused = await Assert.ThrowsAsync<VideoEngineException>(() => session.MakeAsync(engine, _model, Quick(), Estimate(Quick())));
        Assert.Equal("A video needs about 15.0 GB of graphics memory.", refused.Message);
        Assert.Empty(session.Tries);
    }

    [Fact]
    public async Task Keep_still_and_remove_mark_the_try_without_changing_the_clip()
    {
        var session = new VideoSession();
        var made = await session.MakeAsync(new FakeVideoEngine(), _model, Quick(3), Estimate(Quick(3)));
        var still = KeptPictureTests.Webp(384, 256);

        var withStill = session.WithStill(made, still);
        Assert.Same(still, withStill.Still);
        Assert.Same(made.Result, withStill.Result);
        Assert.Same(withStill, session.Selected);
        Assert.StartsWith("data:image/webp;base64,", withStill.StillSrc);
        Assert.Same(still, withStill.ToKeep().Still);

        var kept = session.Kept(withStill, 42);
        Assert.Equal(42, kept.KeptId);
        Assert.EndsWith("kept", VideoPanels.Meta(kept));
        Assert.True(session.Unkept(42));
        Assert.Null(session.Selected!.KeptId);
        Assert.False(session.Unkept(42));
    }

    /// <summary>
    /// M20 step 6 (the review): Keep can be pressed while the clip's still is being made, and then holds the record
    /// from before the still arrived. Marking that old record must mark the clip in the strip, or Keep is offered again
    /// and the same clip is written twice.
    /// </summary>
    [Fact]
    public async Task A_try_is_marked_by_whoever_holds_an_older_record_of_it()
    {
        var session = new VideoSession();
        var made = await session.MakeAsync(new FakeVideoEngine(), _model, Quick(3), Estimate(Quick(3)));
        var still = KeptPictureTests.Webp(384, 256);

        // The still arrives; then Keep, which read the try before that, marks it.
        session.WithStill(made, still);
        var kept = session.Kept(made, 7);
        Assert.Equal(7, kept.KeptId);
        Assert.Same(still, kept.Still);
        Assert.Same(kept, Assert.Single(session.Tries));
        Assert.Same(kept, session.Selected);

        // A second still for the same try (two places asked at once) changes nothing: the first one stays.
        var changes = 0;
        session.Changed += () => changes++;
        var second = session.WithStill(made, KeptPictureTests.Webp(384, 256));
        Assert.Same(kept, second);
        Assert.Equal(0, changes);

        // A try that was let go meanwhile is handed back changed, and is not put back.
        session.Clear();
        Assert.Equal(9, session.Kept(made, 9).KeptId);
        Assert.Empty(session.Tries);
    }

    [Fact]
    public void The_session_holds_nothing_that_could_be_saved_and_the_controls_document_holds_no_sentence()
    {
        // In memory only: no store is given to it, and no constructor takes one.
        Assert.All(typeof(VideoSession).GetConstructors(), c => Assert.Empty(c.GetParameters()));
        // What is remembered: four controls (Faster since M21), never the sentence, the frame or a clip.
        Assert.Equal(["Faster", "Quality", "Seed", "Shape"], typeof(VideoChoices.VideoDocument).GetProperties().Select(p => p.Name).Order());
        Assert.Equal(["Faster", "Quality", "Seed", "Shape"], typeof(VideoControls).GetProperties().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task The_controls_are_remembered_and_what_could_not_have_been_saved_is_left_out()
    {
        var docs = new MemoryDocumentStore();
        var choices = new VideoChoices(docs);
        await choices.LoadAsync();
        Assert.Equal(new VideoControls(VideoQuality.Quick, PictureShape.Wide, null), choices.Controls);

        await choices.SaveAsync(new VideoControls(VideoQuality.Better, PictureShape.Tall, 77));
        var again = new VideoChoices(docs);
        await again.LoadAsync();
        Assert.Equal(new VideoControls(VideoQuality.Better, PictureShape.Tall, 77), again.Controls);

        // A damaged document: a quality and shape that do not exist, a seed out of range.
        await docs.SaveAsync(VideoChoices.Key, new { Quality = 7, Shape = -2, Seed = -5L });
        await again.LoadAsync();
        Assert.Equal(new VideoControls(), again.Controls);
        await docs.SaveAsync(VideoChoices.Key, new { Quality = 1, Shape = 1, Seed = SeedChoice.Highest + 1 });
        await again.LoadAsync();
        Assert.Equal(new VideoControls(VideoQuality.Better, PictureShape.Square, null), again.Controls);
    }

    // ── The bar ──

    [Fact]
    public void The_bar_counts_steps_while_drawing_and_runs_on_the_clock_where_nothing_is_counted()
    {
        var e = new VideoEstimate(Start: 10, Drawing: 80, Decoding: 2, Encoding: 8); // 100 s in all
        Assert.Equal(0, VideoBar.Share(e, VideoBar.PartOf(VideoStages.Waiting), null, null, 50));
        Assert.Equal(0, VideoBar.Share(e, VideoBar.PartOf(VideoStages.MakingRoom), null, null, 50));
        Assert.Equal(0.05, VideoBar.Share(e, VideoBar.PartOf(VideoStages.ReadingSentence), null, null, 5), 6);
        // A start that runs long stops short of its end, and never reaches drawing's share by itself.
        Assert.Equal(0.095, VideoBar.Share(e, VideoPart.Start, null, null, 500), 6);
        Assert.Equal(0.10, VideoBar.Share(e, VideoBar.PartOf(VideoStages.Drawing), 0, 20, 0), 6);
        Assert.Equal(0.50, VideoBar.Share(e, VideoPart.Drawing, 10, 20, 0), 6);
        Assert.Equal(0.90, VideoBar.Share(e, VideoPart.Drawing, 20, 20, 0), 6);
        Assert.Equal(0.91, VideoBar.Share(e, VideoBar.PartOf(VideoStages.Decoding), null, null, 1), 6);
        // Decoding may go by unreported (the fast decoder takes a second): encoding counts it as done.
        Assert.Equal(0.96, VideoBar.Share(e, VideoBar.PartOf(VideoStages.Encoding), null, null, 4), 6);
        // Never full before the clip is there, however long encoding runs.
        Assert.Equal(VideoBar.Highest, VideoBar.Share(e, VideoPart.Encoding, null, null, 1_000), 6);
        Assert.Equal(VideoPart.Start, VideoBar.PartOf(VideoStages.ReadingFrame));
        Assert.Equal(VideoPart.Start, VideoBar.PartOf(VideoStages.Starting));
        Assert.Equal(VideoPart.None, VideoBar.PartOf("something new"));
    }

    [Fact]
    public async Task The_bar_never_goes_back_while_a_clip_is_made()
    {
        var session = new VideoSession();
        var engine = new FakeVideoEngine { Hold = new TaskCompletionSource() };
        var request = Quick();
        var making = Task.Run(() => session.MakeAsync(engine, _model, request, Estimate(request)));
        await engine.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Step 1 of 3 is reported: the start is done and a third of drawing.
        var at = session.BarAt(DateTime.UtcNow);
        var e = Estimate(request);
        Assert.Equal((e.Start + e.Drawing / 3) / e.Total, at, 6);
        Assert.Equal(at, session.BarAt(DateTime.UtcNow.AddSeconds(-100)));
        engine.Hold.SetResult();
        await making;
    }

    // ── The first frame ──

    [Theory]
    [InlineData(4000, 3000, PictureShape.Wide, 0, 131, 4000, 2737)]
    [InlineData(3000, 4000, PictureShape.Tall, 131, 0, 2737, 4000)]
    [InlineData(1000, 1000, PictureShape.Square, 0, 0, 1000, 1000)]
    [InlineData(1216, 832, PictureShape.Wide, 0, 0, 1216, 832)]
    [InlineData(960, 640, PictureShape.Wide, 12, 0, 935, 640)]
    [InlineData(1920, 1080, PictureShape.Wide, 171, 0, 1578, 1080)]
    [InlineData(1100, 1000, PictureShape.Square, 50, 0, 1000, 1000)]
    [InlineData(640, 960, PictureShape.Tall, 0, 12, 640, 935)]
    public void A_picture_is_cut_from_its_middle_to_the_nearest_shape_at_betters_size(int w, int h, PictureShape shape, int x, int y, int cw, int ch)
    {
        var cut = VideoFrames.Cut(w, h);
        Assert.Equal(new FrameCut(shape, x, y, cw, ch, VideoRequest.SizeOf(VideoQuality.Better, shape).Width, VideoRequest.SizeOf(VideoQuality.Better, shape).Height), cut);
        Assert.InRange(cut.X + cut.Width, 0, w);
        Assert.InRange(cut.Y + cut.Height, 0, h);
        // What is cut has the shape of the frame written, within a pixel.
        Assert.True(VideoStartFrame.SameShape(cut.Width, cut.Height, cut.OutWidth, cut.OutHeight));
        // The frame written at Better's size starts a clip of that shape at either quality.
        foreach (var quality in Enum.GetValues<VideoQuality>())
        {
            var (vw, vh) = VideoRequest.SizeOf(quality, shape);
            Assert.True(VideoStartFrame.SameShape(cut.OutWidth, cut.OutHeight, vw, vh));
        }
    }

    [Fact]
    public void A_frame_the_page_made_is_held_with_its_shape_and_anything_else_is_refused_in_words()
    {
        var wide = TestJpegs.Make(1216, 832);
        var (frame, problem) = VideoFrame.Of(wide, "a file");
        Assert.Null(problem);
        Assert.Equal((1216, 832, PictureShape.Wide, "a file"), (frame!.Width, frame.Height, frame.Shape, frame.From));
        Assert.Same(wide, frame.Bytes);
        Assert.StartsWith("data:image/jpeg;base64,", frame.Src);

        // A WebP DOG.ai drew, at Small's tall size: taken as it is (step 5 hands such pictures over).
        Assert.Equal(PictureShape.Tall, VideoFrame.Of(KeptPictureTests.Webp(640, 960), "a kept picture").Frame!.Shape);

        // Nothing, not a picture, another shape, too small.
        Assert.Contains(ComposerPictures.Kinds, VideoFrame.Of(null, "a file").Problem);
        Assert.Contains(ComposerPictures.Kinds, VideoFrame.Of("not a picture"u8.ToArray(), "a file").Problem);
        Assert.Contains("not the shape of the video", VideoFrame.Of(TestJpegs.Make(1000, 600), "a file").Problem);
        Assert.Contains("between 64 and 4096", VideoFrame.Of(TestJpegs.Make(48, 32), "a file").Problem);
    }

    // ── The card ──

    [Fact]
    public void Better_is_not_offered_on_a_sixteen_gigabyte_card_and_quick_is()
    {
        // What the cards report: this PC's 24 GB card, and a 16 GB one (about 8 MiB under its size).
        long sixteen = 16_376 * MiB, twentyFour = 24_564 * MiB;
        Assert.True(VideoMemory.CardCanMake(twentyFour, VideoQuality.Quick));
        Assert.True(VideoMemory.CardCanMake(twentyFour, VideoQuality.Better));
        Assert.Null(VideoMemory.CannotMake(twentyFour, VideoQuality.Better));
        Assert.True(VideoMemory.CardCanMake(sixteen, VideoQuality.Quick));
        Assert.False(VideoMemory.CardCanMake(sixteen, VideoQuality.Better));
        Assert.Equal("Better needs about 16.0 GB of free graphics memory, more than this card (16.0 GB) has once Windows has its part. Quick works on it.",
            VideoMemory.CannotMake(sixteen, VideoQuality.Better));
        // Models offers the model to the 16 GB card, so it can make at least Quick.
        Assert.True(VideoDownloads.Offered(sixteen));
        // A card that could not be measured is not held back. A 12 GB card makes Quick by a plan since M21 step 5
        // (CardGuestsAndSizesTests has every size); a 6 GB card makes neither.
        Assert.True(VideoMemory.CardCanMake(null, VideoQuality.Better));
        Assert.True(VideoMemory.CardCanMake(12 * Gb, VideoQuality.Quick));
        Assert.Contains("Quick works", VideoMemory.CannotMake(12 * Gb, VideoQuality.Better));
        Assert.False(VideoMemory.CardCanMake(6 * Gb, VideoQuality.Quick));
        Assert.DoesNotContain("Quick works", VideoMemory.CannotMake(6 * Gb, VideoQuality.Better));
        // Room on a cleared card counts within the same slack.
        Assert.True(VideoMemory.RoomEnough(15 * Gb - 400 * MiB, 15 * Gb));
        Assert.False(VideoMemory.RoomEnough(14 * Gb, 15 * Gb));
    }

    [Fact]
    public async Task On_a_sixteen_gigabyte_card_quick_is_made_and_better_is_refused_before_anything_moves()
    {
        var chat = new FakeChatModel();
        chat.Loaded();
        var images = new FakeImageEngine();
        var video = new FakeVideoEngine();
        // Windows holds 1 GB of a card that reports 16,376 MiB: 15.0 GB free once the chat model is set aside.
        var card = new FakeCard(chat, images) { Total = 16_376 * MiB, Other = 1 * Gb, ChatBytes = 8 * Gb };
        var coordinator = new CardCoordinator(images, chat, card, _ => (long)(6.5 * Gb), [], video) { PollInterval = TimeSpan.FromMilliseconds(5) };

        var better = VideoRequest.For("A fox runs.", VideoQuality.Better, PictureShape.Wide, 3);
        var refused = await Assert.ThrowsAsync<VideoEngineException>(() => ((IVideoEngine)coordinator).MakeAsync(_model, better));
        Assert.Equal("A video needs about 16.0 GB of graphics memory, and this PC’s graphics card has 16.0 GB.", refused.Message);
        Assert.True(chat.IsLoaded);
        Assert.Empty(video.Made);

        var clip = await ((IVideoEngine)coordinator).MakeAsync(_model, Quick(3));
        Assert.Equal(3, clip.Seed);
        Assert.Single(video.Made);
    }

    // ── Wording, places ──

    [Fact]
    public void Video_is_a_main_screen_in_ctrl_k_and_its_folds_clash_with_nothing()
    {
        var screen = Assert.Single(QuickJump.Screens, s => s.Route == VideoPanels.Address);
        Assert.True(screen.Main);
        Assert.Equal("Video", screen.Title);
        Assert.Contains(QuickJump.Find([], "clip"), t => t.Route == VideoPanels.Address);
        Assert.Equal("Video", ReturnPlace.NameOf(VideoPanels.Address));

        var ids = VideoPanels.Folds.Select(f => f.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches("^[a-z0-9-]+$", id));
        var others = ChatPanels.Layout.SelectMany(p => p.Value.Select(x => ChatPanels.Id(p.Key, x.Panel))).Concat(ImagesPanels.Folds.Select(f => f.Id));
        Assert.Empty(ids.Intersect(others));
    }

    [Fact]
    public void The_words_say_where_a_clip_is_and_how_long_it_took()
    {
        Assert.Equal("Starting…", VideoPanels.Doing(null));
        Assert.Equal("Drawing, step 12 of 20", VideoPanels.Doing(new VideoProgress(VideoStages.Drawing, 12, 20)));
        Assert.Equal("Turning it into frames…", VideoPanels.Doing(new VideoProgress(VideoStages.Decoding)));
        Assert.Equal("Waiting for the graphics card…", VideoPanels.Doing(new VideoProgress(VideoStages.Waiting)));
        Assert.Equal("48 s", VideoPanels.Duration(TimeSpan.FromSeconds(48.2)));
        Assert.Equal("2 min 29 s", VideoPanels.Duration(TimeSpan.FromSeconds(149)));
        Assert.Equal("Quick · 960 × 640", VideoPanels.QualitySummary(VideoQuality.Quick, PictureShape.Wide));
        Assert.Equal("Better · 832 × 1216", VideoPanels.QualitySummary(VideoQuality.Better, PictureShape.Tall));
        Assert.Equal("Takes about 2½ minutes", VideoPanels.EstimateLine(VideoEstimate.For(Quick())));
        Assert.Contains("about 2½ minutes", VideoPanels.Blurb(VideoQuality.Quick, PictureShape.Wide));
        Assert.Equal("New each time", VideoPanels.SeedSummary(null));
        Assert.Contains("every few steps", VideoPanels.WhileMaking(VideoDecoder.Fast));
        Assert.Contains("nothing of it shows", VideoPanels.WhileMaking(VideoDecoder.Own));
    }

    [Fact]
    public void A_clip_made_a_moment_ago_with_nothing_large_loaded_since_starts_warm()
    {
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(VideoEstimate.IsWarm(now.AddMinutes(-3), now, somethingLargeInMemory: false));
        Assert.False(VideoEstimate.IsWarm(now.AddMinutes(-3), now, somethingLargeInMemory: true));
        Assert.False(VideoEstimate.IsWarm(now.AddMinutes(-11), now, false));
        Assert.False(VideoEstimate.IsWarm(null, now, false));
        Assert.False(VideoEstimate.IsWarm(now.AddMinutes(1), now, false));
        Assert.True(VideoEstimate.For(Quick(), warm: true).Total < VideoEstimate.For(Quick()).Total);
    }
}
