using System.Buffers.Binary;
using System.Numerics;

namespace DogAI.Core.Video;

/// <summary>
/// Makes one WebM of two, so that a clip and the clip that continues it play as one (M22). Nothing is decoded and
/// nothing encoded: the frames are the two files' own bytes, moved. It runs in DOG.ai's own memory and sends nothing
/// to any engine.
/// <para>
/// The joined file is the first file's header and track as they are, its facts with the new length, its frames, then
/// the second file's frames <b>from its second on</b> (a continuation's first frame is the clip before's last frame
/// over again), each with its time raised by the first file's last frame's time, then a cue list written anew (one
/// entry a frame) and the index and every size written anew. So two clips of 121 frames give 241, and three 361.
/// </para>
/// <para>
/// Only files written as the video engine writes them are joined (<see cref="ShapeProblem"/>): everything else is
/// refused in words. A joined file is itself such a file, so it can be the first of the next join. Every file made
/// here is read back and compared with its two parts before it is handed on (<see cref="Problem"/>).
/// </para>
/// </summary>
public static class WebmJoin
{
    /// <summary>
    /// The two files as one, or null with <paramref name="problem"/> saying in words why not: a file
    /// <see cref="WebmInfo"/> refuses, one not written as the video engine writes them, two of different size, time
    /// unit or track number, or a result over <see cref="VideoClips.MaxBytes"/>.
    /// </summary>
    public static byte[]? Join(byte[]? first, byte[]? second, out string? problem)
    {
        if (first is null || first.Length == 0 || second is null || second.Length == 0)
        {
            problem = "One of the two videos is empty.";
            return null;
        }
        if (WebmInfo.Parts(first, out var why) is not { } a)
        {
            problem = "The first video is not a plain WebM file: " + why + ".";
            return null;
        }
        if (WebmInfo.Parts(second, out why) is not { } b)
        {
            problem = "The second video is not a plain WebM file: " + why + ".";
            return null;
        }
        if (ShapeProblem(a) is { } shapeA)
        {
            problem = "The first video cannot be joined: " + shapeA + ".";
            return null;
        }
        if (ShapeProblem(b) is { } shapeB)
        {
            problem = "The second video cannot be joined: " + shapeB + ".";
            return null;
        }
        if (a.Info.Width != b.Info.Width || a.Info.Height != b.Info.Height)
        {
            problem = $"The two videos are not the same size ({a.Info.Width} × {a.Info.Height} and {b.Info.Width} × {b.Info.Height}).";
            return null;
        }
        if (a.TimeUnit != b.TimeUnit)
        {
            problem = "The two videos do not count time in the same unit.";
            return null;
        }
        if (a.TrackNumber != b.TrackNumber)
        {
            problem = "The two videos do not number their track the same.";
            return null;
        }
        if (b.Frames.Count < 2)
        {
            problem = "The second video has no frame after its first.";
            return null;
        }
        // The second file's first frame is left out; its second lands one step after the first file's last.
        var raise = a.Frames[^1].Time - b.Frames[0].Time;
        if (raise < 0 || (long)a.Bytes + b.Bytes > VideoClips.MaxBytes)
        {
            problem = raise < 0 ? "The second video’s times cannot follow the first’s." : "The joined video would be too large a file.";
            return null;
        }

        var joined = Write(first, a, second, b, raise);
        if (joined is null)
        {
            problem = "The joined video’s index could not be written.";
            return null;
        }
        if (joined.Length > VideoClips.MaxBytes)
        {
            problem = "The joined video would be too large a file.";
            return null;
        }
        // Read back and compared, frame for frame, before anybody gets it.
        if (Problem(joined, first, second) is { } wrong)
        {
            problem = "The joined video did not come out right (" + wrong + "), so it was not made.";
            return null;
        }
        problem = null;
        return joined;
    }

    /// <summary>
    /// Why a file that <see cref="WebmInfo"/> lets through still cannot be joined, in a few words, or null. It must be
    /// written as the video engine writes its clips: the index, the facts and the track before the frames, the cue
    /// list after them; every frame a key frame in a cluster of its own that says its size; times that rise; its
    /// length said; and no checksums (they would be wrong once anything moves).
    /// </summary>
    public static string? ShapeProblem(WebmParts file)
    {
        if (file.Checksums) return "it carries checksums";
        if (file.First(WebmInfo.Info) is null || file.First(WebmInfo.Tracks) is null) return "it does not have its facts and its track";
        var seen = new HashSet<uint>();
        var (clusters, afterFrames) = (false, false);
        foreach (var part in file.Top)
        {
            if (part.Id == WebmInfo.Cluster)
            {
                if (afterFrames) return "its parts are not in the order the video engine writes them";
                clusters = true;
                continue;
            }
            if (part.Id != WebmInfo.Padding && !seen.Add(part.Id)) return "it has a part twice";
            // Before the frames: anything but the cue list. After them: the cue list alone.
            if (clusters) afterFrames = true;
            if ((part.Id == WebmInfo.Cues) != clusters) return "its parts are not in the order the video engine writes them";
        }
        if (file.Clusters.Any(c => !c.Plain) || file.Clusters.Count != file.Frames.Count) return "its frames are not written one to a cluster";
        if (file.Frames.Any(f => !f.Key)) return "not every frame of it stands by itself";
        for (var i = 0; i < file.Frames.Count; i++)
        {
            if (file.Frames[i].Time < 0 || file.Clusters[i].Time < 0 || (i > 0 && file.Frames[i].Time <= file.Frames[i - 1].Time))
                return "its frames’ times do not rise";
        }
        if (file.TimeUnit <= 0) return "it does not say its time unit";
        if (file.LengthAt is not { Count: 4 or 8 } || !(file.Length > 0) || double.IsInfinity(file.Length)) return "it does not say how long it is";
        return null;
    }

    /// <summary>
    /// What stops <paramref name="joined"/> from being <paramref name="first"/> and <paramref name="second"/> in one
    /// file, in a few words, or null: a clip by <see cref="WebmInfo"/>'s rules, written as the engine writes them,
    /// with every frame of the first and every frame of the second but its first, <b>byte for byte and in order</b>,
    /// each at its time; the first file's header and track untouched; an index and a cue list that point at what
    /// they say.
    /// </summary>
    public static string? Problem(byte[] joined, byte[] first, byte[] second)
    {
        if (WebmInfo.Parts(joined, out var why) is not { } j) return why;
        if (WebmInfo.Parts(first, out _) is not { } a || WebmInfo.Parts(second, out _) is not { } b) return "its parts cannot be read";
        if (ShapeProblem(j) is { } shape) return shape;
        if (j.Info.Width != a.Info.Width || j.Info.Height != a.Info.Height) return "it is not the size of its parts";
        if (j.Frames.Count != a.Frames.Count + b.Frames.Count - 1) return "it does not have its parts’ frames";
        var raise = a.Frames[^1].Time - b.Frames[0].Time;
        for (var i = 0; i < j.Frames.Count; i++)
        {
            var (from, file, add) = i < a.Frames.Count ? (a.Frames[i], first, 0L) : (b.Frames[i - a.Frames.Count + 1], second, raise);
            var frame = j.Frames[i];
            if (frame.Time != from.Time + add) return "a frame is not at its time";
            if (!joined.AsSpan(frame.At, frame.Length).SequenceEqual(file.AsSpan(from.At, from.Length))) return "a frame is not its part’s frame";
        }
        if (!joined.AsSpan(0, j.SegmentAt).SequenceEqual(first.AsSpan(0, a.SegmentAt))) return "its header is not the first part’s";
        if (!Same(joined, j.First(WebmInfo.Tracks), first, a.First(WebmInfo.Tracks))) return "its track is not the first part’s";
        if (j.TimeUnit != a.TimeUnit || j.Info.Writer != a.Info.Writer) return "its facts are not the first part’s";
        // A length kept in four bytes is right to about seven digits.
        var length = raise + b.Length;
        if (Math.Abs(j.Length - length) > Math.Max(0.01, Math.Abs(length) * 1e-6)) return "it does not say its length right";
        return j.IndexProblem();
    }

    private static bool Same(byte[] one, WebmPart? at, byte[] other, WebmPart? there) =>
        at is { } x && there is { } y && one.AsSpan(x.At, x.End - x.At).SequenceEqual(other.AsSpan(y.At, y.End - y.At));

    // ---- Writing ----

    private static byte[]? Write(byte[] first, WebmParts a, byte[] second, WebmParts b, long raise)
    {
        var firstCluster = a.Clusters[0].At;
        var framesEnd = a.Clusters[^1].End;

        // The second file's clusters from its second frame on, each with its time written anew. A time that grows by
        // a byte makes the cluster a byte longer, so its size is written anew too.
        var added = new MemoryStream(b.Bytes);
        var addedAt = new List<(int At, long Time)>(b.Clusters.Count);
        for (var i = 1; i < b.Clusters.Count; i++)
        {
            var c = b.Clusters[i];
            var time = c.Time + raise;
            var number = Number(time);
            var size = 2 + number.Length + (c.End - c.BlockAt);
            addedAt.Add(((int)added.Length, b.Frames[i].Time + raise));
            added.Write(Id(WebmInfo.Cluster));
            // The width the cluster's size had (the engine writes eight bytes), or more when the size needs it.
            added.Write(Size(size, Math.Max(c.Body - c.At - 4, Width(size))));
            added.WriteByte((byte)WebmInfo.ClusterTime);
            added.Write(Size(number.Length, 1));
            added.Write(number);
            added.Write(second, c.BlockAt, c.End - c.BlockAt);
        }

        // The facts: the same bytes, with the new length written over the old (a number of the same width).
        var facts = a.First(WebmInfo.Info)!.Value;
        var info = first.AsSpan(facts.At, facts.End - facts.At).ToArray();
        var (lengthAt, lengthCount) = a.LengthAt!.Value;
        var length = raise + b.Length;
        if (lengthCount == 4) BinaryPrimitives.WriteSingleBigEndian(info.AsSpan(lengthAt - facts.At), (float)length);
        else BinaryPrimitives.WriteDoubleBigEndian(info.AsSpan(lengthAt - facts.At), length);

        // What comes before the frames. The index says where the parts are, and its own size depends on those
        // numbers; the engine leaves padding after it, which takes up the difference. So: write this stretch with the
        // places the parts had, see where they landed, and write it again until both agree.
        var before = a.Top.Where(p => p.At < firstCluster).ToList();
        var framesLength = framesEnd - firstCluster + (int)added.Length;
        var places = new Dictionary<uint, long>();
        foreach (var part in before) places.TryAdd(part.Id, part.At - a.SegmentBody);
        places[WebmInfo.Cluster] = firstCluster - a.SegmentBody;
        places[WebmInfo.Cues] = firstCluster - a.SegmentBody + framesLength;
        for (var round = 0; round < 8; round++)
        {
            var head = new MemoryStream(firstCluster - a.SegmentBody + 16);
            var landed = new Dictionary<uint, long>();
            for (var i = 0; i < before.Count; i++)
            {
                var part = before[i];
                landed.TryAdd(part.Id, head.Length);
                if (part.Id == WebmInfo.SeekHead)
                {
                    var index = Index(a, places);
                    head.Write(index);
                    // The padding right after the index gives way, so that everything behind it stays where it is.
                    if (i + 1 < before.Count && before[i + 1].Id == WebmInfo.Padding)
                    {
                        var room = part.End - part.At + (before[i + 1].End - before[i + 1].At) - index.Length;
                        if (room >= 0 && Empty(room) is { } padding)
                        {
                            landed.TryAdd(WebmInfo.Padding, head.Length);
                            head.Write(padding);
                            i++;
                        }
                    }
                }
                else if (part.Id == WebmInfo.Info) head.Write(info);
                else head.Write(first, part.At, part.End - part.At);
            }
            var guess = (int)head.Length;
            landed[WebmInfo.Cluster] = guess;
            landed[WebmInfo.Cues] = guess + framesLength;
            if (a.Index.Any(entry => landed.TryGetValue(entry.Id, out var now) && (!places.TryGetValue(entry.Id, out var said) || said != now)))
            {
                places = landed;
                continue;
            }

            // The cue list: one entry a frame, each pointing at its cluster.
            var points = new MemoryStream(a.Frames.Count * 16 + addedAt.Count * 16);
            void Point(long time, long position)
            {
                var (t, track, p) = (Number(time), Number(a.TrackNumber), Number(position));
                var where = 2 + track.Length + 2 + p.Length;
                points.WriteByte((byte)WebmInfo.CuePoint);
                points.Write(Size(2 + t.Length + 1 + Width(where) + where, 0));
                points.WriteByte((byte)WebmInfo.CueTime);
                points.Write(Size(t.Length, 1));
                points.Write(t);
                points.WriteByte((byte)WebmInfo.CueTrackPositions);
                points.Write(Size(where, 0));
                points.WriteByte((byte)WebmInfo.CueTrack);
                points.Write(Size(track.Length, 1));
                points.Write(track);
                points.WriteByte((byte)WebmInfo.CueClusterPosition);
                points.Write(Size(p.Length, 1));
                points.Write(p);
            }
            for (var i = 0; i < a.Frames.Count; i++) Point(a.Frames[i].Time, guess + (a.Clusters[i].At - firstCluster));
            foreach (var (at, time) in addedAt) Point(time, guess + (framesEnd - firstCluster) + at);

            var body = head.Length + framesLength + 4 + Width(points.Length) + points.Length;
            var file = new MemoryStream((int)(a.SegmentAt + 12 + body));
            file.Write(first, 0, a.SegmentAt);
            file.Write(Id(WebmInfo.Segment));
            file.Write(Size(body, 8));
            head.WriteTo(file);
            file.Write(first, firstCluster, framesEnd - firstCluster);
            added.WriteTo(file);
            file.Write(Id(WebmInfo.Cues));
            file.Write(Size(points.Length, 0));
            points.WriteTo(file);
            return file.ToArray();
        }
        return null;
    }

    /// <summary>The index written anew: the first file's entries, in its order, each with where its part is now.</summary>
    private static byte[] Index(WebmParts a, Dictionary<uint, long> positions)
    {
        var entries = new MemoryStream();
        foreach (var (id, _) in a.Index)
        {
            if (!positions.TryGetValue(id, out var position)) continue;
            var (name, where) = (Id(id), Number(position));
            entries.Write(Id(WebmInfo.Seek));
            entries.Write(Size(3 + name.Length + 3 + where.Length, 0));
            entries.Write(Id(WebmInfo.SeekId));
            entries.Write(Size(name.Length, 1));
            entries.Write(name);
            entries.Write(Id(WebmInfo.SeekPosition));
            entries.Write(Size(where.Length, 1));
            entries.Write(where);
        }
        var index = new MemoryStream();
        index.Write(Id(WebmInfo.SeekHead));
        index.Write(Size(entries.Length, 0));
        entries.WriteTo(index);
        return index.ToArray();
    }

    /// <summary>Padding of exactly <paramref name="total"/> bytes, all empty; nothing for 0; null when no padding has that size.</summary>
    private static byte[]? Empty(int total)
    {
        if (total == 0) return [];
        if (total < 2) return null;
        // One byte of id, then the size in one byte (up to 126 bytes of padding) or two.
        var sizeWidth = total - 2 <= 126 ? 1 : 2;
        var room = total - 1 - sizeWidth;
        if (room > 16382) return null;
        var padding = new byte[total];
        padding[0] = (byte)WebmInfo.Padding;
        Size(room, sizeWidth).CopyTo(padding, 1);
        return padding;
    }

    private static byte[] Id(uint id) =>
        id > 0xFFFFFF ? [(byte)(id >> 24), (byte)(id >> 16), (byte)(id >> 8), (byte)id]
        : id > 0xFFFF ? [(byte)(id >> 16), (byte)(id >> 8), (byte)id]
        : id > 0xFF ? [(byte)(id >> 8), (byte)id]
        : [(byte)id];

    /// <summary>A whole number in as few bytes as it needs (at least one), highest first.</summary>
    private static byte[] Number(long value)
    {
        var count = Math.Max(1, (64 - BitOperations.LeadingZeroCount((ulong)value) + 7) / 8);
        var bytes = new byte[count];
        for (var i = count - 1; i >= 0; i--, value >>= 8) bytes[i] = (byte)value;
        return bytes;
    }

    /// <summary>How many bytes a size needs at least. A size whose bits are all set means "not said", so it takes one more.</summary>
    private static int Width(long size)
    {
        for (var width = 1; width < 8; width++)
            if (size < (1L << (7 * width)) - 1) return width;
        return 8;
    }

    /// <summary>A part's size in <paramref name="width"/> bytes (0: as few as it needs).</summary>
    private static byte[] Size(long size, int width)
    {
        width = Math.Clamp(Math.Max(width, Width(size)), 1, 8);
        var bytes = new byte[width];
        for (var i = width - 1; i >= 0; i--, size >>= 8) bytes[i] = (byte)size;
        bytes[0] |= (byte)(0x80 >> (width - 1));
        return bytes;
    }
}
