using DogAI.Core.Images;
using DogAI.Core.Video;
using DogAI.Engine.Images;
using DogAI.Engine.Video;

namespace DogAI.Tests;

/// <summary>
/// Video, second round (M21 step 2), Core and engine without a screen: the choice "Faster" on the request, in the
/// request body and in the program's start switches (a plain clip's are held byte for byte as M20 sent them), the
/// estimate's numbers for it, the remembered choice, and Continue the clip's Core.
/// </summary>
public class VideoSecondRoundTests
{
    private const string Negative =
        "overexposed, static, motionless, blurred details, subtitles, text, watermark, worst quality, low quality, " +
        "jpeg artifacts, ugly, deformed, disfigured, extra fingers, fused fingers, poorly drawn hands, poorly drawn face, " +
        "malformed limbs, extra limbs, cluttered background, walking backwards";

    /// <summary>The body of a Quick Wide clip as M20 sent it, up to its last field. Written out here, not built from the code's constants.</summary>
    private static string M20Body(string preview, string size = "\"width\":960,\"height\":640") =>
        "{\"prompt\":\"A fox runs through the snow.\",\"negative_prompt\":\"" + Negative + "\"," + size + ",\"seed\":4000000123," +
        "\"video_frames\":121,\"fps\":24,\"sample_params\":{\"sample_method\":\"euler\",\"sample_steps\":20,\"guidance\":{\"txt_cfg\":5},\"flow_shift\":5}," +
        "\"output_format\":\"webm\",\"output_compression\":70,\"preview\":\"" + preview + "\",\"preview_interval\":4";

    private const string FasterFields = ",\"cache_mode\":\"dbcache\",\"cache_option\":\"Fn=8,Bn=0,threshold=0.08,warmup=8\"";

    private static VideoRequest Quick(bool faster = false, byte[]? frame = null) =>
        VideoRequest.For("A fox runs through the snow.", VideoQuality.Quick, PictureShape.Wide, 4_000_000_123, frame, faster);

    // ── Faster: the request ──

    [Fact]
    public void Faster_is_off_unless_chosen_and_is_part_of_the_request()
    {
        Assert.False(new VideoRequest("a fox").Faster);
        Assert.False(VideoRequest.For("a fox", VideoQuality.Quick, PictureShape.Wide).Faster);
        var faster = VideoRequest.For("a fox", VideoQuality.Better, PictureShape.Tall, 5, faster: true);
        Assert.True(faster.Faster);
        Assert.Empty(faster.Problems());
        // Nothing else about the clip changes with it: size, decoder, steps and the rest are the plain clip's.
        Assert.Equal(VideoRequest.For("a fox", VideoQuality.Better, PictureShape.Tall, 5), faster with { Faster = false });
        // Two clips that differ only in it are two clips (it changes the frames).
        Assert.NotEqual(Quick(), Quick(faster: true));
        // The engine's own settings, written out.
        Assert.Equal("dbcache", VideoRequest.FasterCacheMode);
        Assert.Equal("Fn=8,Bn=0,threshold=0.08,warmup=8", VideoRequest.FasterCacheOption);
    }

    [Fact]
    public void A_plain_clips_request_body_is_byte_for_byte_m20s_and_a_faster_one_adds_two_fields_at_its_end()
    {
        // From a sentence, without and with previews (as the test engine and the app ask).
        Assert.Equal(M20Body("none") + "}", SdApi.VideoBody(Quick(), 4_000_000_123).ToJsonString());
        Assert.Equal(M20Body("tae") + "}", SdApi.VideoBody(Quick() with { Preview = true }, 4_000_000_123).ToJsonString());
        // From a first frame: the bytes as base64, last.
        var frame = KeptPictureTests.Webp(960, 640);
        // (The JSON writer spells a base64 "+" as +, on the wire too; the engine reads it back as "+".)
        var framed = ",\"init_image\":\"" + Convert.ToBase64String(frame).Replace("+", "\\u002B") + "\"";
        Assert.Equal(M20Body("tae") + framed + "}", SdApi.VideoBody(Quick(frame: frame) with { Preview = true }, 4_000_000_123).ToJsonString());
        // Better: only the size differs in the body (its decoder is a start switch).
        var better = VideoRequest.For("A fox runs through the snow.", VideoQuality.Better, PictureShape.Tall, 4_000_000_123);
        Assert.Equal(M20Body("none", "\"width\":832,\"height\":1216") + "}", SdApi.VideoBody(better, 4_000_000_123).ToJsonString());

        // Faster: the same body with the cache's two fields after everything else.
        Assert.Equal(M20Body("none") + FasterFields + "}", SdApi.VideoBody(Quick(faster: true), 4_000_000_123).ToJsonString());
        Assert.Equal(M20Body("tae") + framed + FasterFields + "}", SdApi.VideoBody(Quick(true, frame) with { Preview = true }, 4_000_000_123).ToJsonString());

        // No end frame, ever (the M21 trial: the installed model ends such a clip on washed-out frames), and none of
        // the caches that were faster and softer, or the other fields the engine takes for a cache.
        foreach (var request in new[] { Quick(), Quick(true), Quick(true, frame), better with { Faster = true } })
        {
            var body = SdApi.VideoBody(request, 1);
            foreach (var key in new[] { "end_image", "end_img", "scm_mask", "scm_policy_dynamic", "sage_attn", "max_vram" })
                Assert.False(body.ContainsKey(key), key);
            var text = body.ToJsonString();
            foreach (var word in new[] { "easycache", "spectrum", "ucache", "taylorseer", "cache-dit", "end_im" })
                Assert.DoesNotContain(word, text);
            Assert.Equal(request.Faster, body.ContainsKey("cache_mode"));
            Assert.Equal(request.Faster, body.ContainsKey("cache_option"));
        }
        Assert.DoesNotContain(typeof(VideoRequest).GetProperties(), p => p.Name.Contains("End", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Loop", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(Clip).GetProperties(), p => p.Name.Contains("End", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Loop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_plain_clips_start_switches_are_m20s_and_a_faster_one_adds_sage_attention()
    {
        var model = VideoFolders.ModelFor(@"D:\m\video", VideoFiles.Wan22);
        string[] files =
        [
            "--listen-ip", "127.0.0.1", "--listen-port", "41235",
            "--diffusion-model", @"D:\m\video\diffusion_models\wan2.2_ti2v_5B_fp16.safetensors",
            "--vae", @"D:\m\video\vae\wan2.2_vae.safetensors",
            "--t5xxl", @"D:\m\video\text_encoders\umt5-xxl-encoder-Q8_0.gguf",
            "--taesd", @"D:\m\video\vae\taew2_2.safetensors",
        ];
        string[] quiet = ["--serve-html-path", @"D:\e\dogai-blank.html", "--completed-ttl-seconds", "60", "--disable-image-metadata"];
        const string Page = @"D:\e\dogai-blank.html";

        // Plain: M20's lists, whole, whether "faster" is left out or said to be off.
        Assert.Equal([.. files, .. quiet, "--max-vram", "-3"], VideoServer.Arguments(model, VideoDecoder.Fast, 41235, Page));
        Assert.Equal([.. files, .. quiet, "--max-vram", "-3"], VideoServer.Arguments(model, VideoDecoder.Fast, 41235, Page, faster: false));
        Assert.Equal([.. files, "--taesd-preview-only", .. quiet, "--max-vram", "-3"], VideoServer.Arguments(model, VideoDecoder.Own, 41235, Page));
        Assert.Equal([.. files, .. quiet, "--max-vram", "10.0"], VideoServer.Arguments(model, VideoDecoder.Fast, 41235, Page, "10.0"));

        // Faster: one switch more, before the plan (which stays last), with either decoder and with a plan.
        Assert.Equal([.. files, .. quiet, "--sage-attn", "--max-vram", "-3"], VideoServer.Arguments(model, VideoDecoder.Fast, 41235, Page, faster: true));
        Assert.Equal([.. files, "--taesd-preview-only", .. quiet, "--sage-attn", "--max-vram", "-3"], VideoServer.Arguments(model, VideoDecoder.Own, 41235, Page, faster: true));
        Assert.Equal([.. files, .. quiet, "--sage-attn", "--max-vram", "10.0"], VideoServer.Arguments(model, VideoDecoder.Fast, 41235, Page, "10.0", faster: true));
        Assert.Equal([.. files, "--taesd-preview-only", .. quiet, "--sage-attn", "--max-vram", "13.0"], VideoServer.Arguments(model, VideoDecoder.Own, 41235, Page, "13.0", faster: true));

        foreach (var decoder in Enum.GetValues<VideoDecoder>())
        {
            var args = VideoServer.Arguments(model, decoder, 1, "b.html", faster: true);
            Assert.Contains("--disable-image-metadata", args);
            // The cache is asked for in the request, never at start (a start default would apply to every clip), and
            // nothing that puts the model in RAM or names an end picture.
            Assert.DoesNotContain(args, a => a is "--cache-mode" or "--cache-option" or "--end-img" or "--offload-to-cpu" or "--mmap" or "-m" or "--model"
                or "--lora-model-dir" or "--hires-upscalers-dir" or "-v" or "--verbose" or "-o" or "--output");
        }
    }

    [Fact]
    public async Task A_faster_clip_stays_faster_through_the_session_and_into_the_row_that_is_kept()
    {
        var engine = new FakeVideoEngine();
        var session = new VideoSession();
        var model = VideoFolders.ModelFor(@"D:\m\video", VideoFiles.Wan22);
        var request = Quick(faster: true);
        var made = await session.MakeAsync(engine, model, request, VideoEstimate.For(request));
        Assert.True(engine.Made.Single().Request.Faster);
        // The try keeps it, so Keep writes it and the facts say it.
        Assert.True(made.Request.Faster);
        Assert.True(made.ToKeep().Faster);
        Assert.Contains(" · Quick · Faster · seed ", VideoPanels.Meta(made));
        var plain = await session.MakeAsync(engine, model, Quick(), VideoEstimate.For(Quick()));
        Assert.False(plain.ToKeep().Faster);
        Assert.DoesNotContain("Faster", VideoPanels.Meta(plain));
    }

    // ── Faster: the estimate ──

    [Fact]
    public void With_faster_only_the_drawing_is_shorter_by_the_trials_share()
    {
        static VideoEstimate Of(VideoQuality quality, PictureShape shape, bool faster, bool warm = true) =>
            VideoEstimate.For(VideoRequest.For("a fox", quality, shape, faster: faster), warm);

        foreach (var quality in Enum.GetValues<VideoQuality>())
        foreach (var shape in Enum.GetValues<PictureShape>())
        {
            var (plain, faster) = (Of(quality, shape, false), Of(quality, shape, true));
            // Start, decoding and encoding are untouched (at Better decoding is 45% of the clip and stays).
            Assert.Equal((plain.Start, plain.Decoding, plain.Encoding), (faster.Start, faster.Decoding, faster.Encoding));
            Assert.True(faster.Drawing < plain.Drawing);
        }

        // The trial's shares: 0.62 at Quick's size, 0.53 at Better's.
        var (quick, quickFaster) = (Of(VideoQuality.Quick, PictureShape.Wide, false), Of(VideoQuality.Quick, PictureShape.Wide, true));
        Assert.Equal(0.62, quickFaster.Drawing / quick.Drawing, 3);
        Assert.Equal(quickFaster.Drawing, Of(VideoQuality.Quick, PictureShape.Tall, true).Drawing, 3);
        var (better, betterFaster) = (Of(VideoQuality.Better, PictureShape.Wide, false), Of(VideoQuality.Better, PictureShape.Wide, true));
        Assert.Equal(0.53, betterFaster.Drawing / better.Drawing, 3);
        // The squares (0.59 and 1.05 megapixels) lie beside them; sizes nobody tried are taken a little worse.
        Assert.InRange(VideoEstimate.FasterShare(0.59), 0.62, 0.63);
        Assert.Equal(0.53, VideoEstimate.FasterShare(1.05), 3);
        Assert.Equal(0.53, VideoEstimate.FasterShare(1.6), 3);
        Assert.Equal(0.65, VideoEstimate.FasterShare(0.40), 3);
        Assert.Equal(0.65, VideoEstimate.FasterShare(0.20), 3);
        Assert.InRange(VideoEstimate.FasterShare(0.80), 0.53, 0.62);

        // Whole clips, the program just started and the files in Windows' cache. The trial: Quick 72.6 to 80.2 s
        // against 118 plain; Better 300 to 307 s against 401 to 404.
        Assert.InRange(quickFaster.Total, 70, 84);
        Assert.InRange(quick.Total, 110, 122);
        Assert.InRange(betterFaster.Total, 295, 325);
        Assert.InRange(better.Total, 395, 420);
        // A third faster at Quick, a quarter at Better, as the owner was told.
        Assert.InRange(1 - quickFaster.Total / quick.Total, 0.30, 0.40);
        Assert.InRange(1 - betterFaster.Total / better.Total, 0.21, 0.28);

        // In words, beside Draw.
        Assert.Equal("about 1½ minutes", quickFaster.Words());
        Assert.Equal("about 2 minutes", quick.Words());
        Assert.Equal("about 2 minutes", Of(VideoQuality.Quick, PictureShape.Wide, true, warm: false).Words());
        Assert.Equal("about 5½ minutes", betterFaster.Words());
        Assert.Equal("about 7 minutes", better.Words());

        // The engine still reports every step, so the bar counts them as before, over the shorter drawing.
        var half = VideoBar.Share(quickFaster, VideoPart.Drawing, 10, 20, 0);
        Assert.Equal((quickFaster.Start + quickFaster.Drawing / 2) / quickFaster.Total, half, 6);
    }

    // ── Faster: remembered ──

    [Fact]
    public async Task Faster_is_remembered_with_the_other_controls_and_an_older_document_reads_off()
    {
        // What is remembered: four controls, never the sentence, the frame or a clip.
        Assert.Equal(["Faster", "Quality", "Seed", "Shape"], typeof(VideoChoices.VideoDocument).GetProperties().Select(p => p.Name).Order());
        Assert.Equal(["Faster", "Quality", "Seed", "Shape"], typeof(VideoControls).GetProperties().Select(p => p.Name).Order());
        Assert.False(new VideoControls().Faster);

        var docs = new MemoryDocumentStore();
        var choices = new VideoChoices(docs);
        await choices.LoadAsync();
        Assert.False(choices.Controls.Faster);

        await choices.SaveAsync(choices.Controls with { Faster = true });
        var again = new VideoChoices(docs);
        await again.LoadAsync();
        Assert.Equal(new VideoControls(VideoQuality.Quick, PictureShape.Wide, null, true), again.Controls);
        await again.SaveAsync(again.Controls with { Faster = false, Seed = 9 });
        await choices.LoadAsync();
        Assert.Equal(new VideoControls(VideoQuality.Quick, PictureShape.Wide, 9, false), choices.Controls);

        // A document written by M20's build: three controls.
        await docs.SaveAsync(VideoChoices.Key, new { Quality = 1, Shape = 0, Seed = 77L });
        await again.LoadAsync();
        Assert.Equal(new VideoControls(VideoQuality.Better, PictureShape.Tall, 77, false), again.Controls);
    }

    // ── Make one like this, with Faster ──

    private static Clip Kept(VideoRequest request, long seed = 4321)
    {
        var clip = Clip.Of(ClipOwners.Video, ClipOwners.VideoOwner, VideoFiles.Wan22, request,
            new VideoResult(TestWebm.Make(request.Width, request.Height, request.Frames, seed: 7), request.Width, request.Height, request.Frames, request.Fps, seed));
        clip.Id = 7;
        clip.CreatedAt = new DateTime(2026, 11, 17, 13, 3, 0, DateTimeKind.Utc);
        return clip;
    }

    [Theory]
    [InlineData(VideoQuality.Quick, PictureShape.Wide, false)]
    [InlineData(VideoQuality.Quick, PictureShape.Square, true)]
    [InlineData(VideoQuality.Better, PictureShape.Tall, true)]
    [InlineData(VideoQuality.Better, PictureShape.Wide, false)]
    public void Make_one_like_this_on_a_faster_clip_makes_the_same_faster_clip(VideoQuality quality, PictureShape shape, bool withFrame)
    {
        var (w, h) = VideoRequest.SizeOf(quality, shape);
        var frame = withFrame ? KeptPictureTests.Webp(w, h) : null;
        var clip = Kept(VideoRequest.For("A wolf turns his head to the fire.", quality, shape, 4321, frame, faster: true));
        Assert.True(clip.Faster);
        Assert.True(clip.ToRequest().Faster);
        Assert.Empty(clip.Problems());

        var (like, problem) = VideoLike.Of(clip);
        Assert.Null(problem);
        Assert.Equal(new VideoControls(quality, shape, 4321, Faster: true), like!.Controls);

        // What the Make tab builds from it is the request the clip was made with, and so is what is sent.
        var again = VideoRequest.For(like.Sentence, like.Controls.Quality, like.Controls.Shape, like.Controls.Seed!.Value, like.Frame?.Bytes, like.Controls.Faster);
        Assert.Equal(clip.ToRequest() with { StartFrame = null }, again with { StartFrame = null });
        Assert.Equal(SdApi.VideoBody(clip.ToRequest(), clip.Seed).ToJsonString(), SdApi.VideoBody(again, 4321).ToJsonString());
        Assert.Contains("\"cache_mode\":\"dbcache\"", SdApi.VideoBody(again, 4321).ToJsonString());

        // The facts of the kept clip say it; a plain one's do not.
        Assert.Contains($" · {quality} · Faster · seed 4321 · ", VideoPanels.KeptMeta(clip, new DateTime(2026, 11, 17, 14, 3, 0)));
        var plain = Kept(VideoRequest.For("A wolf turns his head to the fire.", quality, shape, 4321, frame));
        Assert.DoesNotContain("Faster", VideoPanels.KeptMeta(plain, DateTime.Now));
        Assert.False(VideoLike.Of(plain).Like!.Controls.Faster);
    }

    // ── Continue the clip ──

    [Theory]
    [InlineData(VideoQuality.Quick, PictureShape.Wide, false, false)]
    [InlineData(VideoQuality.Quick, PictureShape.Tall, true, false)]
    [InlineData(VideoQuality.Quick, PictureShape.Square, false, true)]
    [InlineData(VideoQuality.Better, PictureShape.Wide, true, true)]
    [InlineData(VideoQuality.Better, PictureShape.Tall, false, true)]
    [InlineData(VideoQuality.Better, PictureShape.Square, true, false)]
    public void Continue_the_clip_hands_make_the_last_frame_and_what_the_clip_was_made_with(VideoQuality quality, PictureShape shape, bool faster, bool fromFrame)
    {
        var (w, h) = VideoRequest.SizeOf(quality, shape);
        var first = fromFrame ? KeptPictureTests.Webp(w, h) : null;
        var made = VideoRequest.For("A wolf turns his head to the fire.", quality, shape, 4321, first, faster);
        var result = new VideoResult(TestWebm.Make(w, h, made.Frames, 3), w, h, made.Frames, made.Fps, 4321);
        var shown = new VideoTry(2, made, result, TimeSpan.FromSeconds(118), 116);
        // What the page makes of the clip's last frame: a JPEG at the clip's own size.
        var last = TestJpegs.Make(w, h, seed: 9);

        var (next, problem) = VideoContinue.Of(shown, last);

        Assert.Null(problem);
        // The clip's quality, shape and Faster, its sentence (the owner changes it), and a new seed.
        Assert.Equal(new VideoControls(quality, shape, Seed: null, faster), next!.Controls);
        Assert.Equal("A wolf turns his head to the fire.", next.Sentence);
        Assert.Equal(last, next.Frame.Bytes);
        Assert.Equal((w, h, shape, true, VideoPanels.FromTryEnd), (next.Frame.Width, next.Frame.Height, next.Frame.Shape, next.Frame.Continues, next.Frame.From));

        // What the Make tab builds from it: the clip's own request with the last frame as the first and a new seed.
        var again = VideoRequest.For(next.Sentence, next.Controls.Quality, next.Controls.Shape, next.Controls.Seed ?? -1, next.Frame.Bytes, next.Controls.Faster);
        Assert.Empty(again.Problems());
        Assert.Equal(made with { Seed = -1, StartFrame = null }, again with { StartFrame = null });
        Assert.Equal(last, again.StartFrame);
        // As any clip from a first frame is sent: the frame's bytes as base64, and never an end picture.
        var body = SdApi.VideoBody(again, 5);
        Assert.Equal(Convert.ToBase64String(last), (string?)body["init_image"]);
        Assert.False(body.ContainsKey("end_image"));

        // From a kept clip the same, whether it was read whole or from a list (neither the video nor the first frame is needed).
        var kept = shown.ToKeep();
        var (fromKept, keptProblem) = VideoContinue.Of(kept, last);
        Assert.Null(keptProblem);
        Assert.Equal(next.Controls, fromKept!.Controls);
        Assert.Equal((next.Sentence, VideoPanels.FromKeptVideoEnd, true), (fromKept.Sentence, fromKept.Frame.From, fromKept.Frame.Continues));
        kept.Video = null;
        kept.StartFrame = null;
        Assert.Equal(next.Controls, VideoContinue.Of(kept, last).Next!.Controls);

        // A continuation can be continued in turn.
        var second = new VideoTry(3, again with { Seed = 88 }, result with { Seed = 88 }, TimeSpan.FromSeconds(118), 116);
        Assert.Equal(next.Controls, VideoContinue.Of(second, TestJpegs.Make(w, h, seed: 10)).Next!.Controls);

        // Held as any first frame is, shown large, and the tab says what it is.
        var session = new VideoSession();
        session.Type(next.Sentence);
        session.Hold(next.Frame);
        Assert.True(session.FrameShown);
        Assert.Same(next.Frame, session.Frame);
    }

    [Fact]
    public void A_last_frame_that_is_not_the_clips_own_frame_is_refused_in_words()
    {
        var made = VideoRequest.For("A wolf turns his head.", VideoQuality.Quick, PictureShape.Wide, 5);
        var shown = new VideoTry(1, made, new VideoResult(TestWebm.Make(960, 640), 960, 640, 121, 24, 5), TimeSpan.FromSeconds(100), 100);

        Assert.Equal("The clip’s last frame could not be read.", VideoContinue.Of(shown, null).Problem);
        Assert.Equal("The clip’s last frame could not be read.", VideoContinue.Of(shown, []).Problem);
        // The page draws the frame at the clip's own size: any other size is not that clip's frame.
        Assert.Equal("The last frame read is 1216 × 832, not the clip’s 960 × 640.", VideoContinue.Of(shown, TestJpegs.Make(1216, 832)).Problem);
        Assert.Equal("The last frame read is 640 × 960, not the clip’s 960 × 640.", VideoContinue.Of(shown, TestJpegs.Make(640, 960)).Problem);
        // Checked as every first frame is: a picture of the two kinds with nothing in it but the picture.
        Assert.StartsWith("The clip’s last frame cannot start a clip: It is not a picture DOG.ai can open", VideoContinue.Of(shown, [0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0]).Problem);
        Assert.Equal("The clip’s last frame cannot start a clip: The first frame carries extra information besides the picture.",
            VideoContinue.Of(shown, TestJpegs.Make(960, 640, extra: TestJpegs.Exif())).Problem);
        Assert.StartsWith("The clip’s last frame cannot start a clip:", VideoContinue.Of(shown, TestWebm.Make(960, 640, 5)).Problem);
        // The colour profile the browser's encoder adds is taken out, as for a chat picture; the frame is then held plain.
        var withProfile = TestJpegs.Make(960, 640, extra: TestJpegs.Profile());
        var (next, problem) = VideoContinue.Of(shown, withProfile);
        Assert.Null(problem);
        Assert.Null(VideoStartFrame.Problem(next!.Frame.Bytes, 960, 640));
        Assert.True(next.Frame.Bytes.Length < withProfile.Length);

        // A WebP of the clip's size passes too (the first-frame check takes the two kinds); nothing else does.
        Assert.Null(VideoContinue.Of(shown, KeptPictureTests.Webp(960, 640)).Problem);
    }

    [Fact]
    public void The_make_tab_says_where_a_continuation_starts_and_what_it_costs()
    {
        var made = VideoRequest.For("A wolf turns his head.", VideoQuality.Quick, PictureShape.Wide, 5);
        var shown = new VideoTry(1, made, new VideoResult(TestWebm.Make(960, 640), 960, 640, 121, 24, 5), TimeSpan.FromSeconds(100), 100);
        var frame = VideoContinue.Of(shown, TestJpegs.Make(960, 640)).Next!.Frame;

        Assert.Equal("The next clip will start from the end of the clip before. Change the sentence below to say what happens next, then press Draw.", VideoPanels.Arrived(frame));
        Assert.Equal("The next clip will start from the end of the kept video. Change the sentence below to say what happens next, then press Draw.",
            VideoPanels.Arrived(VideoContinue.Of(shown.ToKeep(), TestJpegs.Make(960, 640)).Next!.Frame));
        // One line: softer, drifts, after two it shows, change the sentence.
        Assert.Equal(
            "Each continued clip is a little softer than the one before and drifts further from the first picture: after two it shows. " +
            "Change the sentence, or the clip repeats the same motion.", VideoPanels.Continuation);
        Assert.Equal(VideoPanels.Continuation, VideoPanels.AboutFrame(frame));

        // Any other first frame is said as before.
        var (file, _) = VideoFrame.Of(TestJpegs.Make(1216, 832), VideoPanels.FromFile);
        Assert.False(file!.Continues);
        Assert.Equal("The clip will start from a file. Say below what moves, then press Draw.", VideoPanels.Arrived(file));
        Assert.Equal(VideoPanels.FollowsLoosely, VideoPanels.AboutFrame(file));
        // A kept clip's own first frame, put back by Make one like this, is not a continuation.
        var kept = Kept(VideoRequest.For("A wolf.", VideoQuality.Quick, PictureShape.Wide, 5, frame.Bytes));
        Assert.False(VideoLike.Of(kept).Like!.Frame!.Continues);
    }
}
