using System.Text;
using DogAI.Core.Images;
using DogAI.Core.Video;

namespace DogAI.Tests;

/// <summary>What the joined-clip tests share: reading a file's frames back by the mark each begins with.</summary>
internal static class JoinedFiles
{
    public static WebmParts Parts(byte[] file)
    {
        var parts = WebmInfo.Parts(file, out var problem);
        Assert.True(parts is not null, problem);
        return parts!;
    }

    /// <summary>The mark each frame begins with ("A:000"), in the file's order.</summary>
    public static List<string> Marks(byte[] file) =>
        [.. Parts(file).Frames.Select(f =>
        {
            var text = Encoding.ASCII.GetString(file, f.At, Math.Min(f.Length, 12));
            return text[1..text.IndexOf(']')];
        })];

    public static IEnumerable<string> MarksOf(string mark, int from, int frames) => Enumerable.Range(from, frames - from).Select(i => $"{mark}:{i:000}");

    public static int Count(byte[] data, byte[] piece)
    {
        var count = 0;
        for (var at = data.AsSpan().IndexOf(piece); at >= 0;)
        {
            count++;
            var next = data.AsSpan(at + 1).IndexOf(piece);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return count;
    }

    public static byte[] Join(byte[] first, byte[] second)
    {
        var joined = WebmJoin.Join(first, second, out var problem);
        Assert.True(joined is not null, problem);
        return joined!;
    }

    public static string Refused(byte[]? first, byte[]? second)
    {
        Assert.Null(WebmJoin.Join(first, second, out var problem));
        return problem!;
    }
}

/// <summary>M22 step 1: <see cref="WebmInfo.Parts"/> hands out where a file's parts are, from the walk that checks the file.</summary>
public class WebmPartsTests
{
    private const uint SeekHead = 0x114D9B74, Padding = 0xEC, Info = 0x1549A966, Tracks = 0x1654AE6B, Cluster = 0x1F43B675, Cues = 0x1C53BB6B;

    [Fact]
    public void The_walk_hands_out_every_part_and_every_frame_of_a_file_as_the_engine_writes_one()
    {
        var file = TestWebm.Engine("A", 960, 640, frames: 5);

        var parts = JoinedFiles.Parts(file);

        // The same facts as the plain read gives.
        Assert.Equal(WebmInfo.Read(file), parts.Info);
        Assert.Equal((960, 640, 5, "stable-diffusion.cpp"), (parts.Info.Width, parts.Info.Height, parts.Info.Frames, parts.Info.Writer));
        Assert.Equal((file.Length, 36, 48), (parts.Bytes, parts.SegmentAt, parts.SegmentBody));
        // The segment's own parts, in the file's order, back to back to the end.
        Assert.Equal([SeekHead, Padding, Info, Tracks, Cluster, Cluster, Cluster, Cluster, Cluster, Cues], parts.Top.Select(p => p.Id));
        Assert.Equal(parts.SegmentBody, parts.Top[0].At);
        for (var i = 1; i < parts.Top.Count; i++) Assert.Equal(parts.Top[i - 1].End, parts.Top[i].At);
        Assert.Equal(file.Length, parts.Top[^1].End);
        Assert.Equal(110, parts.First(Info)!.Value.At - parts.SegmentBody);
        Assert.Null(parts.First(0x1254C367));

        // Every frame: its time, its own bytes (after the block's four leading bytes), a key frame in a cluster of its own.
        Assert.Equal(5, parts.Frames.Count);
        Assert.Equal(5, parts.Clusters.Count);
        for (var i = 0; i < 5; i++)
        {
            var (frame, cluster) = (parts.Frames[i], parts.Clusters[i]);
            Assert.Equal((i * 1000L / 24, 60, true, i), (frame.Time, frame.Length, frame.Key, frame.Cluster));
            Assert.Equal(TestWebm.FrameMark("A", i), file.AsSpan(frame.At, 7).ToArray());
            Assert.Equal((frame.Time, true), (cluster.Time, cluster.Plain));
            Assert.Equal(parts.Top[4 + i], new WebmPart(Cluster, cluster.At, cluster.Body, cluster.End));
            Assert.Equal(cluster.End, frame.At + frame.Length);
            Assert.Equal(0xA3, file[cluster.BlockAt]);
        }
        Assert.Equal(["A:000", "A:001", "A:002", "A:003", "A:004"], JoinedFiles.Marks(file));

        // The facts the writer needs: the time unit, the track's number, the length and where it is written.
        Assert.Equal((1_000_000L, 1L), (parts.TimeUnit, parts.TrackNumber));
        Assert.Equal(4 * 1000f / 24, parts.Length);
        Assert.Equal(4, parts.LengthAt!.Value.Count);
        Assert.Equal(parts.Length, System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(file.AsSpan(parts.LengthAt.Value.At, 4)));
        Assert.False(parts.Checksums);

        // The index and the cue list, as the file has them; in an engine file both point right.
        Assert.Equal([Info, Tracks, Cluster, Cues], parts.Index.Select(e => e.Id));
        Assert.Equal(parts.Clusters[0].At - parts.SegmentBody, parts.Index[2].Position);
        Assert.Equal(5, parts.Cues.Count);
        Assert.Equal(new WebmCue(83, 1, parts.Clusters[2].At - parts.SegmentBody), parts.Cues[2]);
        Assert.Null(parts.IndexProblem());
        Assert.Null(WebmJoin.ShapeProblem(parts));
    }

    [Fact]
    public void Parts_are_handed_out_for_exactly_the_files_the_plain_read_lets_through()
    {
        byte[][] files =
        [
            TestWebm.Make(), TestWebm.Make(832, 1216, 33), TestWebm.Make(frames: 9, open: true), TestWebm.Engine("A", frames: 3),
            TestWebm.Make(inSegment: TestWebm.TagsWith("a red fox")), TestWebm.Make(inInfo: TestWebm.Text(0x7BA9, "a red fox")),
            TestWebm.Make(codec: "V_VP9"), TestWebm.Make(blockTrack: 2), TestWebm.Make(after: [1, 2, 3]), TestWebm.Make(frames: 0),
            TestWebm.Make(padding: Encoding.ASCII.GetBytes("a red fox")), KeptPictureTests.Webp(960, 640), [],
        ];
        foreach (var file in files)
        {
            var info = WebmInfo.Read(file, out var why);
            var parts = WebmInfo.Parts(file, out var said);
            Assert.Equal(why, said);
            Assert.Equal(info, parts?.Info);
            if (parts is not null) Assert.Equal(info!.Value.Frames, parts.Frames.Count);
        }

        // Cut short or damaged: never an exception, and never parts for a file the read refuses.
        var whole = TestWebm.Engine("A", frames: 12);
        for (var length = 0; length < whole.Length; length += 5) Assert.Null(WebmInfo.Parts(whole.AsSpan(0, length), out _));
        var random = new Random(11);
        for (var i = 0; i < 3000; i++)
        {
            var damaged = (byte[])whole.Clone();
            for (var n = random.Next(1, 4); n > 0; n--) damaged[random.Next(i % 2 == 0 ? 420 : damaged.Length)] = (byte)random.Next(256);
            var parts = WebmInfo.Parts(damaged, out _);
            Assert.Equal(WebmInfo.Read(damaged) is null, parts is null);
            if (parts is null) continue;
            // Whatever it hands out lies inside the file.
            Assert.All(parts.Frames, f => Assert.True(f.At >= 0 && f.Length >= 0 && f.At + f.Length <= damaged.Length));
            _ = parts.IndexProblem();
            _ = WebmJoin.ShapeProblem(parts);
        }
    }

    [Fact]
    public void A_file_that_is_a_clip_but_not_written_as_the_engine_writes_one_says_so()
    {
        // The made-up files of the older tests: an index and a cue list that point at nothing.
        var made = JoinedFiles.Parts(TestWebm.Make(frames: 4));
        Assert.Equal([(Info, 110L)], made.Index);
        Assert.Equal([new WebmCue(0, 1, 233)], made.Cues);
        Assert.Equal("its index does not point at its parts", made.IndexProblem());
        Assert.Equal([0L, 42, 84, 126], made.Frames.Select(f => f.Time));
        Assert.Null(WebmJoin.ShapeProblem(made));

        // Frames that do not say their size (written to a stream): a clip, but not one that can be moved about.
        var open = JoinedFiles.Parts(TestWebm.Make(frames: 4, open: true));
        Assert.All(open.Clusters, c => Assert.False(c.Plain));
        Assert.Equal(4, open.Frames.Count);
        Assert.Equal("its frames are not written one to a cluster", WebmJoin.ShapeProblem(open));

        Assert.Equal("not every frame of it stands by itself", WebmJoin.ShapeProblem(JoinedFiles.Parts(TestWebm.Make(frames: 4, blockFlags: 0x00))));
        Assert.Equal("it carries checksums", WebmJoin.ShapeProblem(JoinedFiles.Parts(TestWebm.Make(frames: 4, inInfo: TestWebm.El(0xBF, new byte[4])))));
        Assert.Equal("it does not say how long it is", WebmJoin.ShapeProblem(JoinedFiles.Parts(TestWebm.Make(frames: 4, length: 0))));
        Assert.Equal("its parts are not in the order the video engine writes them",
            WebmJoin.ShapeProblem(JoinedFiles.Parts(TestWebm.Make(frames: 4, inSegment: TestWebm.El(0xEC, new byte[3])))));
    }
}

/// <summary>M22 step 1: two clips in one file, by structure. Every made-up file has a mark at the start of every frame.</summary>
public class WebmJoinTests
{
    private const uint Padding = 0xEC, Info = 0x1549A966, Tracks = 0x1654AE6B, Cluster = 0x1F43B675, Cues = 0x1C53BB6B;

    /// <summary>Sets a frame's cluster time (a made-up file's times under 256 are one byte).</summary>
    private static byte[] Retimed(byte[] file, int frame, byte time)
    {
        var changed = (byte[])file.Clone();
        var cluster = JoinedFiles.Parts(file).Clusters[frame];
        Assert.Equal((0xE7, 0x81), (changed[cluster.Body], changed[cluster.Body + 1]));
        changed[cluster.Body + 2] = time;
        return changed;
    }

    [Fact]
    public void A_clip_and_its_continuation_become_one_file_of_241_frames_and_ten_seconds()
    {
        var (a, b) = (TestWebm.Engine("A", seed: 1), TestWebm.Engine("B", seed: 2));
        var (aBefore, bBefore) = ((byte[])a.Clone(), (byte[])b.Clone());

        var joined = JoinedFiles.Join(a, b);

        // Nothing was changed in place: the two clips are as they were.
        Assert.Equal(aBefore, a);
        Assert.Equal(bBefore, b);
        // A clip by the reader's own rules, of the same size, with the writer's name and nothing else in words.
        var info = WebmInfo.Read(joined, out var problem);
        Assert.Null(problem);
        Assert.Equal((960, 640, 241, 10.0, "stable-diffusion.cpp"), (info!.Value.Width, info.Value.Height, info.Value.Frames, info.Value.Seconds, info.Value.Writer));

        // Order: every frame of the first, then the second's from its second on. The second's first frame, which
        // repeats the first's last, is nowhere in the file.
        Assert.Equal([.. JoinedFiles.MarksOf("A", 0, 121), .. JoinedFiles.MarksOf("B", 1, 121)], JoinedFiles.Marks(joined));
        Assert.Equal(0, JoinedFiles.Count(joined, TestWebm.FrameMark("B", 0)));
        Assert.Equal(1, JoinedFiles.Count(joined, TestWebm.FrameMark("A", 120)));
        Assert.Equal(1, JoinedFiles.Count(joined, TestWebm.FrameMark("B", 1)));

        // Times: each of the second's raised by the first's last frame's time, so they go on as one clip's would.
        var (parts, first, second) = (JoinedFiles.Parts(joined), JoinedFiles.Parts(a), JoinedFiles.Parts(b));
        Assert.Equal(5000, first.Frames[^1].Time);
        Assert.Equal(Enumerable.Range(0, 241).Select(k => k * 1000L / 24), parts.Frames.Select(f => f.Time));
        Assert.Equal(10_000, parts.Frames[^1].Time);
        // The length: the first's last frame's time and the second's length. Written where the first had its own.
        Assert.Equal(10_000.0, parts.Length);
        Assert.Equal(first.LengthAt, parts.LengthAt);

        // Byte for byte: every frame is its part's frame.
        for (var k = 0; k < 241; k++)
        {
            var (from, file) = k < 121 ? (first.Frames[k], a) : (second.Frames[k - 120], b);
            Assert.Equal(file.AsSpan(from.At, from.Length).ToArray(), joined.AsSpan(parts.Frames[k].At, parts.Frames[k].Length).ToArray());
            Assert.True(parts.Frames[k].Key);
        }

        // The first file's header and track as they are; its facts but for the length.
        Assert.Equal(a.AsSpan(0, first.SegmentAt).ToArray(), joined.AsSpan(0, parts.SegmentAt).ToArray());
        var (track, was) = (parts.First(Tracks)!.Value, first.First(Tracks)!.Value);
        Assert.Equal(a.AsSpan(was.At, was.End - was.At).ToArray(), joined.AsSpan(track.At, track.End - track.At).ToArray());
        var (facts, factsWas) = (parts.First(Info)!.Value, first.First(Info)!.Value);
        var same = joined.AsSpan(facts.At, facts.End - facts.At).ToArray();
        a.AsSpan(first.LengthAt!.Value.At, 4).CopyTo(same.AsSpan(parts.LengthAt!.Value.At - facts.At));
        Assert.Equal(a.AsSpan(factsWas.At, factsWas.End - factsWas.At).ToArray(), same);
        // The first file's frames were not touched at all: the whole stretch is the same bytes.
        Assert.Equal(a.AsSpan(first.Clusters[0].At, first.Clusters[^1].End - first.Clusters[0].At).ToArray(),
            joined.AsSpan(parts.Clusters[0].At, parts.Clusters[120].End - parts.Clusters[0].At).ToArray());

        // The same parts in the engine's order, every size written anew: they lie back to back to the file's end.
        Assert.Equal(first.Top.Take(4).Select(p => p.Id), parts.Top.Take(4).Select(p => p.Id));
        Assert.Equal(4 + 241 + 1, parts.Top.Count);
        Assert.Equal(Cues, parts.Top[^1].Id);
        for (var i = 1; i < parts.Top.Count; i++) Assert.Equal(parts.Top[i - 1].End, parts.Top[i].At);
        Assert.Equal(joined.Length, parts.Top[^1].End);
        Assert.All(parts.Clusters, c => Assert.True(c.Plain));

        // The cue list: one entry a frame, each at its frame's time and pointing at its cluster.
        Assert.Equal(241, parts.Cues.Count);
        for (var k = 0; k < 241; k++)
        {
            Assert.Equal(new WebmCue(parts.Frames[k].Time, 1, parts.Clusters[k].At - parts.SegmentBody), parts.Cues[k]);
            Assert.Equal((byte[])[0x1F, 0x43, 0xB6, 0x75], joined.AsSpan(parts.SegmentBody + (int)parts.Cues[k].Position, 4).ToArray());
        }
        // The index: the first file's four entries, each pointing at where its part is now.
        Assert.Equal(first.Index.Select(e => e.Id), parts.Index.Select(e => e.Id));
        foreach (var (id, position) in parts.Index) Assert.Equal(parts.First(id)!.Value.At - parts.SegmentBody, position);
        Assert.Equal(first.Index.Take(3), parts.Index.Take(3));
        Assert.NotEqual(first.Index[3], parts.Index[3]);
        Assert.Null(parts.IndexProblem());
        Assert.Null(WebmJoin.ShapeProblem(parts));

        // And the check a joined file gets before anybody sees it, and the one a kept clip gets.
        Assert.Null(WebmJoin.Problem(joined, a, b));
        Assert.Null(VideoClips.Problem(joined, 960, 640, 121, parts: 2));
    }

    [Fact]
    public void Three_clips_join_in_two_steps_either_way_round_into_the_same_file_of_361_frames()
    {
        var (a, b, c) = (TestWebm.Engine("A", seed: 1), TestWebm.Engine("B", seed: 2), TestWebm.Engine("C", seed: 3));

        // A joined file is the first of the next join…
        var abThenC = JoinedFiles.Join(JoinedFiles.Join(a, b), c);
        // …or the second, when the later two were joined first.
        var aThenBc = JoinedFiles.Join(a, JoinedFiles.Join(b, c));

        Assert.Equal(abThenC, aThenBc);
        var info = WebmInfo.Read(abThenC)!.Value;
        Assert.Equal((361, 15.0), (info.Frames, info.Seconds));
        Assert.Equal([.. JoinedFiles.MarksOf("A", 0, 121), .. JoinedFiles.MarksOf("B", 1, 121), .. JoinedFiles.MarksOf("C", 1, 121)], JoinedFiles.Marks(abThenC));
        var parts = JoinedFiles.Parts(abThenC);
        Assert.Equal(Enumerable.Range(0, 361).Select(k => k * 1000L / 24), parts.Frames.Select(f => f.Time));
        Assert.Equal(15_000.0, parts.Length);
        Assert.Null(parts.IndexProblem());
        Assert.Null(VideoClips.Problem(abThenC, 960, 640, 121, parts: 3));
        Assert.Null(WebmJoin.Problem(abThenC, JoinedFiles.Join(a, b), c));
        Assert.Null(WebmJoin.Problem(abThenC, a, JoinedFiles.Join(b, c)));

        // The made-up files of the older tests (a short index, a cue list of one entry, sizes in other widths) join
        // too, and come out with an index and a cue list that are right.
        var (x, y) = (TestWebm.Make(768, 768, 33, seed: 4, filler: 80, mark: "X"), TestWebm.Make(768, 768, 33, seed: 5, filler: 80, mark: "Y"));
        var xy = JoinedFiles.Join(x, y);
        Assert.Equal([.. JoinedFiles.MarksOf("X", 0, 33), .. JoinedFiles.MarksOf("Y", 1, 33)], JoinedFiles.Marks(xy));
        var made = JoinedFiles.Parts(xy);
        Assert.Equal(Enumerable.Range(0, 65).Select(k => k * 42L), made.Frames.Select(f => f.Time));
        Assert.Equal(32 * 42 + 33 * 1000f / 24, made.Length, 3);
        Assert.Null(made.IndexProblem());
        Assert.Equal(65, made.Cues.Count);
        Assert.Null(VideoClips.Problem(xy, 768, 768, 33, parts: 2));
    }

    [Fact]
    public void The_padding_after_the_index_gives_way_when_a_position_needs_another_byte()
    {
        // 12 MB each: the cue list of one lies under 16 MB (three bytes say where), that of the two over it (four).
        var (a, b) = (TestWebm.Engine("A", seed: 1, filler: 100_000), TestWebm.Engine("B", seed: 2, filler: 100_000));
        var first = JoinedFiles.Parts(a);
        Assert.InRange(first.Index[3].Position, 1 << 16, (1 << 24) - 1);

        var joined = JoinedFiles.Join(a, b);

        var parts = JoinedFiles.Parts(joined);
        Assert.True(parts.Index[3].Position >= 1 << 24);
        // The index grew by a byte, the padding shrank by one, and everything behind them stayed where it was.
        Assert.Equal((first.Top[0].End - first.Top[0].At) + 1, parts.Top[0].End - parts.Top[0].At);
        Assert.Equal((Padding, first.Top[1].End - first.Top[1].At - 1), (parts.Top[1].Id, parts.Top[1].End - parts.Top[1].At));
        Assert.Equal(first.Index.Take(3), parts.Index.Take(3));
        Assert.Equal(first.Clusters[0].At, parts.Clusters[0].At);
        Assert.Null(parts.IndexProblem());
        Assert.Null(WebmJoin.Problem(joined, a, b));
        Assert.Equal(241, parts.Frames.Count);
    }

    [Fact]
    public void Files_that_cannot_be_joined_are_refused_in_words()
    {
        var a = TestWebm.Engine("A", frames: 9);
        var b = TestWebm.Engine("B", frames: 9, seed: 2);

        Assert.Equal("One of the two videos is empty.", JoinedFiles.Refused(null, b));
        Assert.Equal("One of the two videos is empty.", JoinedFiles.Refused(a, []));

        // Anything the reader refuses, in the reader's words, and which of the two it is.
        Assert.Equal("The first video is not a plain WebM file: it carries tags.", JoinedFiles.Refused(TestWebm.Make(inSegment: TestWebm.TagsWith("a red fox")), b));
        Assert.Equal("The second video is not a plain WebM file: it does not start as a WebM.", JoinedFiles.Refused(a, TestJpegs.Make(960, 640)));
        Assert.Equal("The second video is not a plain WebM file: it carries a title.", JoinedFiles.Refused(a, TestWebm.Make(inInfo: TestWebm.Text(0x7BA9, "a red fox"))));
        Assert.Equal("The first video is not a plain WebM file: it has more than one track.",
            JoinedFiles.Refused(TestWebm.Make(moreTracks: TestWebm.El(TestWebm.TrackEntry, TestWebm.Num(0xD7, 2), TestWebm.Num(0x83, 2), TestWebm.Text(0x86, "A_OPUS"))), b));
        Assert.Equal("The second video is not a plain WebM file: it has no segment.", JoinedFiles.Refused(a, b[..^3]));

        // Another size, another time unit, another track number.
        Assert.Equal("The two videos are not the same size (960 × 640 and 640 × 960).", JoinedFiles.Refused(a, TestWebm.Engine("B", 640, 960, 9)));
        Assert.Equal("The two videos are not the same size (960 × 640 and 1216 × 832).", JoinedFiles.Refused(a, TestWebm.Engine("B", 1216, 832, 9)));
        Assert.Equal("The two videos do not count time in the same unit.", JoinedFiles.Refused(TestWebm.Make(frames: 5), TestWebm.Make(frames: 5, scale: 1000)));
        Assert.Equal("The two videos do not number their track the same.", JoinedFiles.Refused(TestWebm.Make(frames: 5), TestWebm.Make(frames: 5, track: 2, blockTrack: 2)));

        // Nothing to add: the second's only frame is the one left out.
        Assert.Equal("The second video has no frame after its first.", JoinedFiles.Refused(a, TestWebm.Make(frames: 1)));

        // A clip, but not written as the video engine writes one: each way, and which of the two.
        Assert.Equal("The first video cannot be joined: its frames are not written one to a cluster.", JoinedFiles.Refused(TestWebm.Make(frames: 5, open: true), b));
        Assert.Equal("The second video cannot be joined: its frames are not written one to a cluster.", JoinedFiles.Refused(a, TestWebm.Make(frames: 5, open: true)));
        Assert.Equal("The second video cannot be joined: not every frame of it stands by itself.", JoinedFiles.Refused(a, TestWebm.Make(frames: 5, blockFlags: 0x00)));
        Assert.Equal("The first video cannot be joined: it carries checksums.", JoinedFiles.Refused(TestWebm.Make(frames: 5, inInfo: TestWebm.El(0xBF, new byte[4])), b));
        Assert.Equal("The first video cannot be joined: it does not say how long it is.", JoinedFiles.Refused(TestWebm.Make(frames: 5, length: 0), b));
        Assert.Equal("The second video cannot be joined: it does not say how long it is.", JoinedFiles.Refused(a, TestWebm.Make(frames: 5, length: 0)));
        Assert.Equal("The first video cannot be joined: its parts are not in the order the video engine writes them.",
            JoinedFiles.Refused(TestWebm.Make(frames: 5, inSegment: TestWebm.El(0xEC, new byte[3])), b));
        Assert.Equal("The second video cannot be joined: its frames’ times do not rise.", JoinedFiles.Refused(a, Retimed(b, 2, 41)));
        // The second begins later than the first ends: its frames cannot be put behind the first's.
        Assert.Equal("The second video’s times cannot follow the first’s.", JoinedFiles.Refused(TestWebm.Engine("A", frames: 2), Retimed(Retimed(b, 1, 70), 0, 60)));

        // Two that together are over the most a clip may be.
        var large = TestWebm.Engine("L", frames: 5, filler: VideoClips.MaxBytes / 10 + 1000);
        Assert.Equal("The joined video would be too large a file.", JoinedFiles.Refused(large, large));
    }

    [Fact]
    public void A_joined_file_that_is_not_its_two_parts_in_order_is_caught()
    {
        var (a, b, c) = (TestWebm.Engine("A", frames: 9), TestWebm.Engine("B", frames: 9, seed: 2), TestWebm.Engine("C", frames: 9, seed: 3));
        var joined = JoinedFiles.Join(a, b);
        var parts = JoinedFiles.Parts(joined);
        Assert.Null(WebmJoin.Problem(joined, a, b));

        byte[] With(Action<byte[]> change)
        {
            var changed = (byte[])joined.Clone();
            change(changed);
            return changed;
        }

        // One byte of one frame, in either half.
        Assert.Equal("a frame is not its part’s frame", WebmJoin.Problem(With(f => f[parts.Frames[3].At + 30] ^= 1), a, b));
        Assert.Equal("a frame is not its part’s frame", WebmJoin.Problem(With(f => f[parts.Frames[12].At + 30] ^= 1), a, b));
        // Other parts than it was made from, or the same two the other way round.
        Assert.Equal("a frame is not its part’s frame", WebmJoin.Problem(joined, a, c));
        Assert.Equal("a frame is not its part’s frame", WebmJoin.Problem(joined, b, a));
        // A frame missing (a part of four frames in place of nine), a frame at another time.
        Assert.Equal("it does not have its parts’ frames", WebmJoin.Problem(JoinedFiles.Join(a, TestWebm.Engine("B", frames: 4, seed: 2)), a, b));
        Assert.Equal("a frame is not at its time", WebmJoin.Problem(With(f => f[parts.Clusters[10].Body + 3]++), a, b));
        // The track or the header changed.
        Assert.Equal("its track is not the first part’s", WebmJoin.Problem(With(f => f[parts.First(Tracks)!.Value.Body + 8] ^= 1), a, b));
        Assert.Equal("its header is not the first part’s", WebmJoin.Problem(With(f => f[8] = 2), a, b));
        // The length.
        Assert.Equal("it does not say its length right", WebmJoin.Problem(With(f => f[parts.LengthAt!.Value.At + 1] ^= 0x10), a, b));
        // The cue list or the index pointing somewhere else (the last byte of the file is the last cue's position).
        Assert.Equal("its cue list does not point at its frames", WebmJoin.Problem(With(f => f[^1]++), a, b));
        var entry = joined.AsSpan(parts.Top[0].At, parts.Top[0].End - parts.Top[0].At).IndexOf((byte[])[0x53, 0xAC, 0x81, 110]);
        Assert.True(entry > 0);
        Assert.Equal("its index does not point at its parts", WebmJoin.Problem(With(f => f[parts.Top[0].At + entry + 3]++), a, b));
        // Not a clip at all any more.
        Assert.Equal("it has no segment", WebmJoin.Problem(joined[..^2], a, b));

        // The check a kept joined clip gets has no parts to compare with; it still knows a file with a wrong index.
        Assert.Equal("The joined video is not written as DOG.ai writes one: its cue list does not point at its frames.",
            VideoClips.Problem(With(f => f[^1]++), 960, 640, 9, parts: 2));
    }

    [Fact]
    public void Damaged_files_never_throw_and_never_give_a_file_that_is_not_checked()
    {
        var (a, b) = (TestWebm.Engine("A", frames: 7), TestWebm.Engine("B", frames: 7, seed: 2));
        var random = new Random(23);
        var made = 0;
        for (var i = 0; i < 2000; i++)
        {
            var (x, y) = ((byte[])a.Clone(), (byte[])b.Clone());
            var damaged = i % 2 == 0 ? x : y;
            for (var n = random.Next(1, 4); n > 0; n--) damaged[random.Next(i % 4 < 2 ? 420 : damaged.Length)] = (byte)random.Next(256);
            var joined = WebmJoin.Join(x, y, out var problem);
            Assert.Equal(joined is null, problem is not null);
            if (joined is null) continue;
            made++;
            Assert.Null(WebmJoin.Problem(joined, x, y));
            Assert.NotNull(WebmInfo.Read(joined));
        }
        // Most damage lands inside a frame, which is still a frame: those are joined, as they should be.
        Assert.InRange(made, 100, 1990);
    }
}

/// <summary>M22 step 1: what a kept clip must be once it may hold two or three clips.</summary>
public class JoinedClipCheckTests
{
    private static VideoRequest Request(long seed = 7, byte[]? frame = null) =>
        VideoRequest.For("The fox lowers its head.", VideoQuality.Quick, PictureShape.Wide, seed, frame);

    [Fact]
    public void A_clip_of_parts_has_the_frames_of_its_parts_less_each_repeated_one()
    {
        Assert.Equal(3, VideoClips.MaxParts);
        Assert.Equal((121, 241, 361), (VideoClips.FramesOf(121, 1), VideoClips.FramesOf(121, 2), VideoClips.FramesOf(121, 3)));
        Assert.Equal((33, 65, 97), (VideoClips.FramesOf(33, 1), VideoClips.FramesOf(33, 2), VideoClips.FramesOf(33, 3)));
        // Counted as a single clip's length is: "5 s", "10 s", "15 s" in a clip's facts.
        Assert.Equal(["5 s", "10 s", "15 s"], new[] { 1, 2, 3 }.Select(p => VideoPanels.Length(VideoClips.SecondsOf(121, 24, p))));
        // Where the last frame is: what the page falls back on when a file does not say its length.
        Assert.Equal((5.0, 10.0, 15.0), (VideoClips.LastFrameSeconds(121, 24), VideoClips.LastFrameSeconds(121, 24, 2), VideoClips.LastFrameSeconds(121, 24, 3)));
        Assert.Equal(0, VideoClips.LastFrameSeconds(121, 0, 2));
    }

    [Fact]
    public void The_engines_own_clips_are_still_held_to_exactly_the_frames_asked_for()
    {
        var (a, b) = (TestWebm.Engine("A"), TestWebm.Engine("B", seed: 2));
        var joined = JoinedFiles.Join(a, b);
        var request = new VideoRequest("a fox", 960, 640);

        Assert.Null(VideoClips.Problem(a, request));
        Assert.Null(VideoClips.Problem(a, 960, 640, 121));
        Assert.Equal("The video has 241 frames, not the 121 asked for.", VideoClips.Problem(joined, request));
        Assert.Equal("The video has 241 frames, not the 121 asked for.", VideoClips.Problem(joined, 960, 640, 121, parts: 1));

        // A joined clip: exactly the frames of its parts, the same size, the same rules for what a clip may hold.
        Assert.Null(VideoClips.Problem(joined, 960, 640, 121, parts: 2));
        Assert.Equal("The video has 241 frames, not the 361 that 3 joined clips of 121 frames have.", VideoClips.Problem(joined, 960, 640, 121, parts: 3));
        Assert.Equal("The video has 121 frames, not the 241 that 2 joined clips of 121 frames have.", VideoClips.Problem(a, 960, 640, 121, parts: 2));
        Assert.Equal("The video is 960 × 640, not the 640 × 960 asked for.", VideoClips.Problem(joined, 640, 960, 121, parts: 2));
        Assert.Equal("The video is empty.", VideoClips.Problem(null, 960, 640, 121, parts: 2));
        // At most three.
        Assert.Equal("A video holds between 1 and 3 clips.", VideoClips.Problem(joined, 960, 640, 121, parts: 4));
        Assert.Equal("A video holds between 1 and 3 clips.", VideoClips.Problem(joined, 960, 640, 121, parts: 0));
        // A file of the right number of frames that is not written as a joined one is (frames that do not say their size).
        Assert.Equal("The joined video is not written as DOG.ai writes one: its frames are not written one to a cluster.",
            VideoClips.Problem(TestWebm.Make(frames: 241, filler: 20, open: true), 960, 640, 121, parts: 2));
        Assert.Equal("The joined video is not written as DOG.ai writes one: its index does not point at its parts.",
            VideoClips.Problem(TestWebm.Make(frames: 241, filler: 20), 960, 640, 121, parts: 2));
    }

    [Fact]
    public void A_joined_clip_is_kept_as_its_last_parts_request_with_how_many_clips_its_video_holds()
    {
        var frame = TestJpegs.Make(960, 640);
        var request = Request(7, frame);
        var joined = JoinedFiles.Join(TestWebm.Engine("A"), TestWebm.Engine("B", seed: 2));

        var clip = Clip.Of(ClipOwners.Video, ClipOwners.VideoOwner, VideoFiles.Wan22, request, new VideoResult(joined, 960, 640, 241, 24, 7), parts: 2);

        Assert.Empty(clip.Problems());
        Assert.Equal((2, true, 121, 241), (clip.Parts, clip.Joined, clip.Frames, clip.TotalFrames));
        Assert.InRange(clip.Seconds, 10.0, 10.1);
        // What makes it again is the last part's request, unchanged by the joining: 121 frames, its seed, its first frame.
        Assert.Equal(request, clip.ToRequest());
        Assert.Empty(clip.ToRequest().Problems());

        // The number of parts is checked against the video.
        clip.Parts = 1;
        Assert.Equal(["The video has 241 frames, not the 121 asked for."], clip.Problems());
        clip.Parts = 3;
        Assert.Contains("not the 361 that 3 joined clips", Assert.Single(clip.Problems()));
        clip.Parts = 4;
        Assert.Equal(["A video holds between 1 and 3 clips."], clip.Problems());
        // A clip as it was made says one part and nothing changes for it.
        var plain = Clip.Of(ClipOwners.Video, ClipOwners.VideoOwner, VideoFiles.Wan22, Request(), new VideoResult(TestWebm.Make(), 960, 640, 121, 24, 7));
        Assert.Equal((1, false, 121), (plain.Parts, plain.Joined, plain.TotalFrames));
        Assert.Empty(plain.Problems());
    }

    [Fact]
    public void A_joined_kept_clips_facts_say_its_whole_length_and_how_many_clips_it_holds()
    {
        var joined = JoinedFiles.Join(TestWebm.Engine("A"), TestWebm.Engine("B", seed: 2));
        var clip = Clip.Of(ClipOwners.Video, ClipOwners.VideoOwner, VideoFiles.Wan22, Request(4321, TestJpegs.Make(960, 640)) with { Faster = true },
            new VideoResult(joined, 960, 640, 241, 24, 4321), parts: 2);

        var meta = VideoPanels.KeptMeta(clip, new DateTime(2026, 11, 18, 14, 3, 0));

        Assert.StartsWith("960 × 640 · 10 s · 2 clips joined · Quick · Faster · seed 4321 · ", meta);
        Assert.EndsWith(" MB · kept 18 Nov 2026, 14:03", meta);
        // Its first frame is the end of the clip before, not a picture it began with.
        Assert.DoesNotContain("from a picture", meta);
        clip.Parts = 3;
        Assert.Contains(" · 15 s · 3 clips joined · ", VideoPanels.KeptMeta(clip, DateTime.Now));
        Assert.Equal("This video is 3 clips joined. The sentence, the seed and the first frame shown are its last clip’s; the earlier clips’ are not kept.",
            VideoPanels.JoinedAbout(3));
    }

    [Fact]
    public void Make_one_like_this_on_a_joined_clip_puts_back_its_last_part_and_says_so()
    {
        var frame = TestJpegs.Make(960, 640);
        var request = Request(4321, frame) with { Faster = true };
        var joined = JoinedFiles.Join(TestWebm.Engine("A"), TestWebm.Engine("B", seed: 2));
        var clip = Clip.Of(ClipOwners.Video, ClipOwners.VideoOwner, VideoFiles.Wan22, request, new VideoResult(joined, 960, 640, 241, 24, 4321), parts: 2);

        var (like, problem) = VideoLike.Of(clip);

        Assert.Null(problem);
        Assert.Equal(2, like!.Parts);
        // What Draw then asks for is the last part's request: its sentence, settings, seed and first frame.
        var again = VideoRequest.For(like.Sentence, like.Controls.Quality, like.Controls.Shape, like.Controls.Seed!.Value, like.Frame!.Bytes, like.Controls.Faster);
        Assert.Equal(request, again);
        Assert.Equal(121, again.Frames);
        // The frame put back is an ordinary first frame: a clip made from it is not known to continue anything.
        Assert.Equal((false, null), (like.Frame.Continues, like.Frame.Ended));

        Assert.Equal("The kept video is 2 clips joined, and only its last clip can be made again. That clip’s sentence, first frame, quality, shape and seed (4321) " +
            "are back, with Faster: Draw makes the last five seconds again, not the whole video. Press New seed under Seed for new ones.",
            VideoPanels.PutBack(like, betterOffered: true));
        // At Better on a card that cannot make Better.
        var better = VideoRequest.For("The waves roll.", VideoQuality.Better, PictureShape.Wide, 9, TestJpegs.Make(1216, 832));
        var large = Clip.Of(ClipOwners.Video, 0, VideoFiles.Wan22, better,
            new VideoResult(JoinedFiles.Join(JoinedFiles.Join(TestWebm.Engine("A", 1216, 832), TestWebm.Engine("B", 1216, 832, seed: 2)), TestWebm.Engine("C", 1216, 832, seed: 3)), 1216, 832, 361, 24, 9), parts: 3);
        Assert.Empty(large.Problems());
        var said = VideoPanels.PutBack(VideoLike.Of(large).Like!, betterOffered: false);
        Assert.StartsWith("The kept video is 3 clips joined, and only its last clip can be made again.", said);
        Assert.Contains("it was made at Better, which this card cannot make, so Draw makes the last five seconds again at Quick, not the whole video.", said);
        // A single clip's note is as it was.
        var single = Clip.Of(ClipOwners.Video, 0, VideoFiles.Wan22, Request(5), new VideoResult(TestWebm.Make(), 960, 640, 121, 24, 5));
        Assert.Equal(1, VideoLike.Of(single).Like!.Parts);
        Assert.Equal("The kept video’s sentence, quality, shape and seed (5) are back: Draw makes the same clip. Press New seed under Seed for new ones.",
            VideoPanels.PutBack(VideoLike.Of(single).Like!, betterOffered: true));
    }

    [Fact]
    public void Continue_the_clip_on_a_joined_clip_takes_its_last_parts_sentence_and_knows_which_clip_the_frame_ended()
    {
        var request = Request(4321, TestJpegs.Make(960, 640));
        var joined = JoinedFiles.Join(TestWebm.Engine("A"), TestWebm.Engine("B", seed: 2));
        var clip = Clip.Of(ClipOwners.Video, ClipOwners.VideoOwner, VideoFiles.Wan22, request, new VideoResult(joined, 960, 640, 241, 24, 4321), parts: 2);
        clip.Id = 12;
        var last = TestJpegs.Make(960, 640, seed: 9);

        var (next, problem) = VideoContinue.Of(clip, last);

        Assert.Null(problem);
        Assert.Equal(("The fox lowers its head.", 2), (next!.Sentence, next.Parts));
        Assert.Equal(new VideoControls(VideoQuality.Quick, PictureShape.Wide, Seed: null, Faster: false), next.Controls);
        // The frame says which clip it ended, so a clip made from it can be joined with that one.
        Assert.True(next.Frame.Continues);
        Assert.Equal(new VideoBefore(Kept: 12), next.Frame.Ended);
        Assert.Equal(VideoPanels.FromKeptVideoEnd, next.Frame.From);
        Assert.DoesNotContain("the most one video holds", VideoPanels.Continued(next, betterOffered: true));

        // A video of three clips can still be continued; the note says the next clip stays by itself.
        clip.Parts = 3;
        var full = VideoContinue.Of(clip, last).Next!;
        Assert.Equal(3, full.Parts);
        Assert.EndsWith(" It holds 3 clips, which is the most one video holds: the next clip will stay a clip of its own.", VideoPanels.Continued(full, betterOffered: true));
        // A clip that is not kept yet has no id to be found by.
        clip.Id = 0;
        Assert.Null(VideoContinue.Of(clip, last).Next!.Frame.Ended);
        // A frame that is not a clip's last frame never says it ended one.
        Assert.Null(VideoFrame.Of(last, VideoPanels.FromFile, continues: false, new VideoBefore(Kept: 12)).Frame!.Ended);
    }

    [Fact]
    public void The_page_reads_a_kept_clips_last_frame_at_the_time_all_of_its_parts_give()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "DogAI.sln"))) root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("DogAI.sln not found");
        var page = File.ReadAllText(Path.Combine(root, "src", "DogAI.UI", "Pages", "VideoMake.razor"));

        // A kept row's Frames is one part's; the time the player is set to when the file gives no length counts them all.
        Assert.Contains("LastFrameAsync(webm, clip.Width, clip.Height, clip.TotalFrames, clip.Fps)", page);
        Assert.DoesNotContain("clip.Frames, clip.Fps", page);
        // Joining sends nothing anywhere: the writer and the session name no engine and no address.
        var core = Path.Combine(root, "src", "DogAI.Core", "Video");
        foreach (var file in new[] { "WebmJoin.cs", "WebmParts.cs" })
        {
            var source = File.ReadAllText(Path.Combine(core, file));
            foreach (var word in new[] { "IVideoEngine", "HttpClient", "http", "File.", "Path.", "Process" })
                Assert.DoesNotContain(word, source, StringComparison.Ordinal);
        }
    }
}

/// <summary>M22 step 1: the Make tab's session knows what a clip continues, and joining adds a new try beside.</summary>
public class VideoSessionJoinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dogai-videojoin-" + Guid.NewGuid().ToString("N"));
    private readonly VideoModel _model;
    private readonly FakeVideoEngine _engine = new();
    private readonly VideoSession _session = new();

    public VideoSessionJoinTests()
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

    /// <summary>A clip from the sentence alone (any frame held is let go first, as Remove does on the page).</summary>
    private async Task<VideoTry> Make(long seed, string sentence = "A fox stands in the snow.")
    {
        _session.Hold(null);
        var request = VideoRequest.For(sentence, VideoQuality.Quick, PictureShape.Wide, seed);
        return await _session.MakeAsync(_engine, _model, request, VideoEstimate.For(request));
    }

    /// <summary>Continue the clip, as the page does it: the last frame read, held, and a clip made from the frame held.</summary>
    private async Task<VideoTry> Continue(VideoTry before, long seed, VideoQuality quality = VideoQuality.Quick, string sentence = "The fox lowers its head.")
    {
        var (next, problem) = VideoContinue.Of(before, TestJpegs.Make(before.Result.Width, before.Result.Height, seed: (int)seed));
        Assert.Null(problem);
        return await FromHeld(next!, seed, quality, sentence);
    }

    private async Task<VideoTry> FromHeld(VideoContinue next, long seed, VideoQuality quality = VideoQuality.Quick, string sentence = "The fox lowers its head.")
    {
        _session.Hold(next.Frame);
        var request = VideoRequest.For(sentence, quality, _session.Frame!.Shape, seed, _session.Frame.Bytes, next.Controls.Faster);
        return await _session.MakeAsync(_engine, _model, request, VideoEstimate.For(request));
    }

    private static string Refused(Action join) => Assert.Throws<InvalidOperationException>(join).Message;

    [Fact]
    public async Task A_clip_made_from_the_last_frame_of_another_knows_what_it_continues()
    {
        var a = await Make(1);
        Assert.Null(a.Continues);
        Assert.Equal((1, false), (a.Parts, a.Joined));

        // Continue the clip: the frame held says which clip it ended.
        var next = VideoContinue.Of(a, TestJpegs.Make(960, 640)).Next!;
        Assert.Equal(new VideoBefore(Try: a.Number), next.Frame.Ended);
        Assert.Equal(1, next.Parts);
        var b = await FromHeld(next, 2);
        Assert.Equal(new VideoBefore(Try: a.Number), b.Continues);
        // Drawn again from the same frame held: that clip continues it too.
        var b2 = await FromHeld(next, 3);
        Assert.Equal(new VideoBefore(Try: a.Number), b2.Continues);

        // The frame let go, or another picture held: the next clip continues nothing.
        Assert.Null((await Make(4)).Continues);
        var (other, _) = VideoFrame.Of(TestJpegs.Make(960, 640, seed: 5), VideoPanels.FromFile);
        _session.Hold(other);
        var request = VideoRequest.For("A fox.", VideoQuality.Quick, PictureShape.Wide, 5, other!.Bytes);
        Assert.Null((await _session.MakeAsync(_engine, _model, request, VideoEstimate.For(request))).Continues);
        // The last frame held, but the clip made from another picture than the one held.
        _session.Hold(next.Frame);
        var apart = VideoRequest.For("A fox.", VideoQuality.Quick, PictureShape.Wide, 6, TestJpegs.Make(960, 640, seed: 77));
        Assert.Null((await _session.MakeAsync(_engine, _model, apart, VideoEstimate.For(apart))).Continues);

        // Keeping does not write what a clip continues anywhere: a row has no place for it.
        Assert.DoesNotContain(typeof(Clip).GetProperties(), p => p.Name.Contains("Continue") || p.Name.Contains("Before"));
    }

    [Fact]
    public async Task Joining_adds_a_new_clip_beside_the_two_and_changes_neither()
    {
        var a = await Make(1);
        var b = await Continue(a, 2);
        var (aBytes, bBytes) = ((byte[])a.Result.Webm.Clone(), (byte[])b.Result.Webm.Clone());
        var changes = 0;
        _session.Changed += () => changes++;
        Assert.Null(_session.JoinProblem(b));
        Assert.Equal(VideoPanels.JoinNothing, _session.JoinProblem(a));
        Assert.Equal(VideoPanels.JoinNothing, Refused(() => _session.Join(a)));

        var joined = _session.Join(b);

        // A new try, shown, beside the two, which are the very same records with the very same bytes.
        Assert.Equal(3, joined.Number);
        Assert.Equal(3, _session.Tries.Count);
        Assert.Same(a, _session.Tries[0]);
        Assert.Same(b, _session.Tries[1]);
        Assert.Same(joined, _session.Tries[2]);
        Assert.Same(joined, _session.Selected);
        Assert.Same(joined, _session.Shown);
        Assert.True(changes > 0);
        Assert.Equal(aBytes, a.Result.Webm);
        Assert.Equal(bBytes, b.Result.Webm);
        Assert.False(_session.Dropped);

        // It holds two clips: every frame of the first, then the second's but its first; ten seconds.
        Assert.Equal((2, true), (joined.Parts, joined.Joined));
        Assert.Equal((960, 640, 241, 24), (joined.Result.Width, joined.Result.Height, joined.Result.Frames, joined.Result.Fps));
        Assert.Null(WebmJoin.Problem(joined.Result.Webm, a.Result.Webm, b.Result.Webm));
        Assert.InRange(joined.Seconds, 10.0, 10.1);
        // Its facts are its last part's: the request, the seed, the first frame. It is not kept, and continues nothing
        // (the first clip continued nothing).
        Assert.Equal(b.Request, joined.Request);
        Assert.Equal(b.Result.Seed, joined.Result.Seed);
        Assert.Null(joined.KeptId);
        Assert.Null(joined.Continues);
        Assert.Equal(VideoPanels.JoinNothing, _session.JoinProblem(joined));
        Assert.Equal("Try 3 · 960 × 640 · 10 s · 2 clips joined · Quick · seed 2", VideoPanels.Meta(joined));
        Assert.Equal("Joined with the clip before: 2 clips joined in one file, 10 s, as a new clip beside them. " +
            "At each join the picture softens in one step and jumps a little; the clips’ own frames are kept as they are.", VideoPanels.JoinedNote(joined));

        // As kept: the last part's request, two parts, a video the store takes.
        var row = joined.ToKeep();
        Assert.Empty(row.Problems());
        Assert.Equal((2, 121, 241), (row.Parts, row.Frames, row.TotalFrames));
        Assert.Equal(b.Request, row.ToRequest());
        Assert.Same(joined.Result.Webm, row.Video);
        // The two it was made from are kept as before: single clips of 121 frames.
        Assert.Equal((1, 1), (a.ToKeep().Parts, b.ToKeep().Parts));
        Assert.Empty(b.ToKeep().Problems());

        // Joined once more with the same press, it is simply made again beside.
        var again = _session.Join(b);
        Assert.Equal(4, again.Number);
        Assert.Equal(joined.Result.Webm, again.Result.Webm);
    }

    [Fact]
    public async Task The_first_parts_still_becomes_the_joined_clips_still()
    {
        var a = await Make(1);
        a = _session.WithStill(a, KeptPictureTests.Webp(384, 256, 2000, 1));
        var b = await Continue(a, 2);
        b = _session.WithStill(b, KeptPictureTests.Webp(384, 256, 2000, 2));

        var joined = _session.Join(b);

        // The joined clip begins with the first clip's first frame.
        Assert.Same(a.Still, joined.Still);
        Assert.Same(a.Still, joined.ToKeep().Still);
        Assert.Empty(joined.ToKeep().Problems());
    }

    [Fact]
    public async Task A_joined_clip_continued_and_joined_again_holds_three_and_a_fourth_is_refused()
    {
        var a = await Make(1);
        var b = await Continue(a, 2);
        var ab = _session.Join(b);

        // The joined clip is continued like any other: from its end, with its last part's sentence.
        var next = VideoContinue.Of(ab, TestJpegs.Make(960, 640, seed: 3)).Next!;
        Assert.Equal((b.Request.Prompt, 2, new VideoBefore(Try: ab.Number)), (next.Sentence, next.Parts, next.Frame.Ended));
        var c = await FromHeld(next, 3, sentence: "The fox walks on.");
        Assert.Equal(new VideoBefore(Try: ab.Number), c.Continues);
        Assert.Equal([a.Number, b.Number, ab.Number, c.Number], _session.Tries.Select(t => t.Number));

        var abc = _session.Join(c);

        Assert.Equal((3, 361), (abc.Parts, abc.Result.Frames));
        Assert.InRange(abc.Seconds, 15.0, 15.1);
        Assert.Equal(c.Request, abc.Request);
        Assert.Null(WebmJoin.Problem(abc.Result.Webm, ab.Result.Webm, c.Result.Webm));
        Assert.Equal("Try 5 · 960 × 640 · 15 s · 3 clips joined · Quick · seed 3", VideoPanels.Meta(abc));
        Assert.Empty(abc.ToKeep().Problems());
        // The strip holds four: the oldest was let go, never the one that was shown or the new one.
        Assert.Equal([b.Number, ab.Number, c.Number, abc.Number], _session.Tries.Select(t => t.Number));
        Assert.True(_session.Dropped);

        // A clip of three parts can still be continued; joining that one is refused in words, and nothing changes.
        var full = VideoContinue.Of(abc, TestJpegs.Make(960, 640, seed: 4)).Next!;
        Assert.Contains("which is the most one video holds", VideoPanels.Continued(full, betterOffered: true));
        var d = await FromHeld(full, 4);
        var before = _session.Tries;
        const string Four = "A video holds at most 3 clips (15 seconds), and these together would be 4. The clip can still be kept and continued by itself.";
        Assert.Equal(Four, _session.JoinProblem(d));
        Assert.Equal(Four, Refused(() => _session.Join(d)));
        Assert.Same(before, _session.Tries);
        Assert.Same(d, _session.Selected);
    }

    [Fact]
    public async Task Three_clips_made_one_after_another_join_in_two_presses()
    {
        var a = await Make(1);
        var b = await Continue(a, 2);
        var c = await Continue(b, 3, sentence: "The fox walks on.");

        // First press, on the last: it and the clip before. The joined clip remembers what its first part continued.
        var bc = _session.Join(c);
        Assert.Equal((2, new VideoBefore(Try: a.Number)), (bc.Parts, bc.Continues));
        Assert.Null(_session.JoinProblem(bc));
        // Second press, on the joined clip: the first clip in front of it.
        var abc = _session.Join(bc);

        Assert.Equal((3, 361), (abc.Parts, abc.Result.Frames));
        Assert.Null(abc.Continues);
        // The same frames at the same times as joining them in the other order.
        Assert.Null(WebmJoin.Problem(abc.Result.Webm, JoinedFiles.Join(a.Result.Webm, b.Result.Webm), c.Result.Webm));
        // Its request is the last clip's.
        Assert.Equal(c.Request, abc.Request);
        Assert.Equal("The fox walks on.", abc.ToKeep().Prompt);
        Assert.Empty(abc.ToKeep().Problems());
    }

    [Fact]
    public async Task Joining_is_refused_in_words_when_the_clip_before_is_gone_or_another_size_or_a_clip_is_being_made()
    {
        var a = await Make(1);
        var b = await Continue(a, 2);

        // Another size: a Quick clip continued at Better (the frame held passes for both).
        var better = await Continue(a, 3, VideoQuality.Better);
        Assert.Equal((1216, 832, new VideoBefore(Try: a.Number)), (better.Result.Width, better.Result.Height, better.Continues));
        const string Size = "The clip before is 960 × 640 and this one 1216 × 832. Only clips of the same size can be joined.";
        Assert.Equal(Size, _session.JoinProblem(better));
        Assert.Equal(Size, Refused(() => _session.Join(better)));

        // Another length (a part of 33 frames against one of 121).
        Assert.Equal(VideoPanels.JoinOtherLength, _session.JoinProblem(b with { Request = b.Request with { Frames = 33 } }));

        // While a clip is being made: nothing is joined, whatever the clip.
        _engine.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = VideoRequest.For("A lake.", VideoQuality.Quick, PictureShape.Wide, 9);
        _session.Hold(null);
        var making = Task.Run(() => _session.MakeAsync(_engine, _model, request, VideoEstimate.For(request)));
        await _engine.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(_session.IsMaking);
        Assert.Equal("A clip is being made. Join when it is done.", _session.JoinProblem(b));
        Assert.Equal("A clip is being made. Join when it is done.", Refused(() => _session.Join(b)));
        _engine.Hold.SetResult();
        _engine.Hold = null;
        await making;
        Assert.Null(_session.JoinProblem(b));

        // The clip before let go from the strip (it holds four; a fifth clip took the oldest's place).
        Assert.Equal(4, _session.Tries.Count);
        await Make(10);
        Assert.DoesNotContain(_session.Tries, t => t.Number == a.Number);
        const string Gone = "The clip before is not held any more (only the last 4 clips are), so there is nothing to join this one with.";
        Assert.Equal(Gone, _session.JoinProblem(b));
        Assert.Equal(Gone, Refused(() => _session.Join(b)));
        // Nothing was added by any refusal.
        Assert.DoesNotContain(_session.Tries, t => t.Joined);
    }

    [Fact]
    public async Task A_continuation_of_a_kept_clip_is_joined_with_that_clips_video_read_by_id()
    {
        // A clip kept earlier (its row has an id), opened in Kept videos and continued from there.
        var first = await Make(1);
        var kept = _session.WithStill(first, KeptPictureTests.Webp(384, 256, 2000, 1)).ToKeep();
        kept.Id = 7;
        var keptVideo = (byte[])kept.Video!.Clone();
        _session.Clear();
        var next = VideoContinue.Of(kept, TestJpegs.Make(960, 640)).Next!;
        Assert.Equal(new VideoBefore(Kept: 7), next.Frame.Ended);
        var b = await FromHeld(next, 2);
        Assert.Equal(new VideoBefore(Kept: 7), b.Continues);

        // The page reads the row by id. Removed meanwhile, or another row: said in words.
        Assert.Equal(VideoPanels.JoinKeptGone, _session.JoinProblem(b));
        Assert.Equal(VideoPanels.JoinKeptGone, Refused(() => _session.Join(b)));
        var another = first.ToKeep();
        another.Id = 8;
        Assert.Equal(VideoPanels.JoinKeptGone, _session.JoinProblem(b, another));
        // Its facts are enough to say whether it can be joined; joining needs its video.
        var facts = first.ToKeep();
        (facts.Id, facts.Video) = (7, null);
        Assert.Null(_session.JoinProblem(b, facts));
        Assert.Equal(VideoPanels.JoinKeptGone, Refused(() => _session.Join(b, facts)));

        var joined = _session.Join(b, kept);

        Assert.Equal((2, 241), (joined.Parts, joined.Result.Frames));
        Assert.Null(WebmJoin.Problem(joined.Result.Webm, kept.Video!, b.Result.Webm));
        // The kept clip is as it was, and the joined one is a try like any other: not kept until Keep is pressed.
        Assert.Equal(keptVideo, kept.Video);
        Assert.Null(joined.KeptId);
        Assert.Same(kept.Still, joined.Still);
        // A kept row does not remember what it continued.
        Assert.Null(joined.Continues);
        Assert.Equal([b.Number, joined.Number], _session.Tries.Select(t => t.Number));

        // A kept clip that is itself two clips joined, continued and joined: three. One of three: refused.
        var two = joined.ToKeep();
        two.Id = 9;
        var c = await FromHeld(VideoContinue.Of(two, TestJpegs.Make(960, 640, seed: 3)).Next!, 3);
        var three = _session.Join(c, two);
        Assert.Equal((3, 361), (three.Parts, three.Result.Frames));
        var full = three.ToKeep();
        full.Id = 10;
        var d = await FromHeld(VideoContinue.Of(full, TestJpegs.Make(960, 640, seed: 4)).Next!, 4);
        Assert.Equal(VideoPanels.JoinTooMany(4), _session.JoinProblem(d, full));
        // A kept clip of another size.
        var large = Clip.Of(ClipOwners.Video, 0, VideoFiles.Wan22, VideoRequest.For("A lake.", VideoQuality.Better, PictureShape.Wide, 5),
            new VideoResult(TestWebm.Make(1216, 832), 1216, 832, 121, 24, 5));
        large.Id = 11;
        var e = await FromHeld(VideoContinue.Of(large, TestJpegs.Make(1216, 832)).Next!, 6, VideoQuality.Quick);
        Assert.Equal(VideoPanels.JoinOtherSize(1216, 832, 960, 640), _session.JoinProblem(e, large));
    }

    [Fact]
    public async Task A_removed_kept_clip_and_a_cleared_strip_leave_joined_tries_as_they_are()
    {
        var a = await Make(1);
        var b = await Continue(a, 2);
        var joined = _session.Join(b);
        joined = _session.Kept(joined, 31);
        Assert.Equal("Try 3 · 960 × 640 · 10 s · 2 clips joined · Quick · seed 2 · kept", VideoPanels.Meta(joined));
        // Kept videos › Remove: the try offers Keep again, still two clips.
        Assert.True(_session.Unkept(31));
        Assert.Equal((null, 2), (_session.Tries[2].KeptId, _session.Tries[2].Parts));
        // The session is still a thing with nothing to save into (a joined clip that is not kept is in memory only).
        Assert.Empty(typeof(VideoSession).GetConstructors().Single().GetParameters());
    }
}

/// <summary>M22 step 2: what the Make tab and Kept videos show and do with a joined clip.</summary>
public class JoinedClipScreenTests
{
    private static string Root()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "DogAI.sln"))) root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("DogAI.sln not found");
        return root;
    }

    private static string Ui(params string[] parts) => File.ReadAllText(Path.Combine([Root(), "src", "DogAI.UI", .. parts]));

    [Fact]
    public void A_joined_clip_is_saved_under_a_name_of_its_own_and_its_tile_says_its_length()
    {
        Assert.Equal("video-123.webm", VideoPanels.FileName(123, 1));
        Assert.Equal("video-123-joined.webm", VideoPanels.FileName(123, 2));
        Assert.Equal("video-4294967295-joined.webm", VideoPanels.FileName(4294967295, 3));

        // A clip of one part is always five seconds: its tile says nothing; a joined one says how long it plays.
        Assert.Null(VideoPanels.TileLength(1, 5.0));
        Assert.Equal("10 s", VideoPanels.TileLength(2, 241 / 24.0));
        Assert.Equal("15 s", VideoPanels.TileLength(3, 361 / 24.0));

        Assert.Equal("First frame", VideoPanels.KeptFrameLabel(1));
        Assert.Equal("First frame of its last clip", VideoPanels.KeptFrameLabel(2));
        Assert.Contains("Draw makes the same clip", VideoPanels.LikeTitle(1));
        Assert.Contains("2 clips joined", VideoPanels.LikeTitle(2));
        Assert.Contains("Draw makes the last five seconds again", VideoPanels.LikeTitle(2));
        Assert.DoesNotContain("the same clip", VideoPanels.LikeTitle(3));
    }

    [Fact]
    public void Join_is_on_the_Make_tab_only_and_both_tabs_use_the_shared_words()
    {
        var make = Ui("Pages", "VideoMake.razor");
        var kept = Ui("Components", "Video", "KeptVideos.razor");

        // Make: the button on a clip that continues another, the line when it cannot, the line about the join.
        foreach (var id in new[] { "id=\"video-join\"", "id=\"video-join-why\"", "id=\"video-join-line\"" })
            Assert.Contains(id, make);
        Assert.Contains("@VideoPanels.JoinName", make);
        Assert.Contains("shown.Continues is not null", make);
        Assert.Contains("Session.JoinProblem(shown", make);
        Assert.Contains("Session.Join(shown, kept)", make);
        // A continuation of a kept clip reads that clip by id on the press (its video), its facts for the line.
        Assert.Contains("Clips.GetAsync(id)", make);
        Assert.Contains("Clips.FactsAsync(id)", make);
        Assert.Contains("VideoPanels.JoinedNote(joined)", make);
        Assert.Contains("VideoPanels.FileName(shown.Result.Seed, shown.Parts)", make);

        // Kept videos: no Join (only the Make tab knows which clips belong together), the length, what a joined clip holds.
        Assert.DoesNotContain("JoinName", kept);
        Assert.DoesNotContain("Session.Join", kept);
        Assert.Contains("VideoPanels.TileLength(item.Parts, item.Seconds)", kept);
        Assert.Contains("id=\"kept-clip-joined\"", kept);
        Assert.Contains("VideoPanels.JoinedAbout(viewed.Parts)", kept);
        Assert.Contains("VideoPanels.KeptFrameLabel(viewed.Parts)", kept);
        Assert.Contains("VideoPanels.LikeTitle(viewed.Parts)", kept);
        Assert.Contains("VideoPanels.FileName(clip.Seed, clip.Parts)", kept);

        // No screen writes a video's file name by hand.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src", "DogAI.UI"), "*.razor", SearchOption.AllDirectories))
            Assert.DoesNotMatch(@"\$""video-\{", File.ReadAllText(file));
    }
}
