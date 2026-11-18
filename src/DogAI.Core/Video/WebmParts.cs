namespace DogAI.Core.Video;

/// <summary>One of the segment's own parts (the index, padding, the facts, the track, a frame's cluster, the cue list).</summary>
/// <param name="Id">The part's id as Matroska names it.</param>
/// <param name="At">Where the part begins in the file (its id).</param>
/// <param name="Body">Where its content begins.</param>
/// <param name="End">The first byte after it.</param>
public readonly record struct WebmPart(uint Id, int At, int Body, int End);

/// <summary>One cluster of frames. The video engine writes one frame a cluster.</summary>
/// <param name="Time">The cluster's time, in the file's time unit.</param>
/// <param name="BlockAt">Where its first frame's block begins (the block's id), or -1 without one.</param>
/// <param name="Plain">
/// True when it is written as the video engine writes one: it says its size and holds its time followed by one
/// simple block, and nothing else. Only such clusters can be joined (<see cref="WebmJoin"/>).
/// </param>
public readonly record struct WebmCluster(int At, int Body, int End, long Time, int BlockAt, bool Plain);

/// <summary>One frame as the file holds it. Nothing of it is decoded.</summary>
/// <param name="Time">When it shows, in the file's time unit: its cluster's time and the block's own.</param>
/// <param name="At">Where the frame's own bytes begin (after the block's four leading bytes).</param>
/// <param name="Length">How many bytes the frame has.</param>
/// <param name="Key">The block says the frame stands by itself (a key frame).</param>
/// <param name="Cluster">Which of <see cref="WebmParts.Clusters"/> holds it.</param>
public readonly record struct WebmFrame(long Time, int At, int Length, bool Key, int Cluster);

/// <summary>One entry of the cue list: where a player jumps to for a time.</summary>
/// <param name="Position">Where the cluster begins, counted from the start of the segment's content.</param>
public readonly record struct WebmCue(long Time, long Track, long Position);

/// <summary>
/// Where the parts of a WebM are, as <see cref="WebmInfo.Parts"/> noted them on its one walk. Only a file that
/// <see cref="WebmInfo"/> lets through has one. <see cref="WebmJoin"/> writes a joined file from two of these, and
/// the check on a joined file compares its frames with its parts' through them.
/// </summary>
public sealed class WebmParts
{
    private WebmParts(WebmInfo info) => Info = info;

    public WebmInfo Info { get; }

    /// <summary>How many bytes the file has.</summary>
    public int Bytes { get; private init; }

    /// <summary>Where the segment begins: everything before it is the file header.</summary>
    public int SegmentAt { get; private init; }

    /// <summary>Where the segment's content begins. Positions in the index and the cue list count from here.</summary>
    public int SegmentBody { get; private init; }

    /// <summary>The segment's own parts, in the file's order.</summary>
    public IReadOnlyList<WebmPart> Top { get; private init; } = [];

    public IReadOnlyList<WebmCluster> Clusters { get; private init; } = [];

    /// <summary>Every frame, in the file's order.</summary>
    public IReadOnlyList<WebmFrame> Frames { get; private init; } = [];

    /// <summary>The index at the file's start: which part begins where (the part's id, its position).</summary>
    public IReadOnlyList<(uint Id, long Position)> Index { get; private init; } = [];

    public IReadOnlyList<WebmCue> Cues { get; private init; } = [];

    /// <summary>The time unit, in nanoseconds (1,000,000: milliseconds).</summary>
    public long TimeUnit { get; private init; }

    public long TrackNumber { get; private init; }

    /// <summary>How long the file says it plays, in its time unit; 0 when it does not say.</summary>
    public double Length { get; private init; }

    /// <summary>Where the file says its length (the number's own bytes), or null when it does not.</summary>
    public (int At, int Count)? LengthAt { get; private init; }

    /// <summary>True when a part carries a checksum of its content.</summary>
    public bool Checksums { get; private init; }

    /// <summary>The first of the segment's parts with this id, or null.</summary>
    public WebmPart? First(uint id)
    {
        foreach (var part in Top)
            if (part.Id == id) return part;
        return null;
    }

    /// <summary>
    /// What is wrong with the file's index and cue list, in a few words, or null: every index entry must point at
    /// the first part of its kind, and there must be one cue a frame, in order, each pointing at the cluster that
    /// holds that frame, with its time. (The engine's files are so; a joined file is written so and checked.)
    /// </summary>
    public string? IndexProblem()
    {
        foreach (var (id, position) in Index)
            if (First(id) is not { } part || part.At - SegmentBody != position) return "its index does not point at its parts";
        if (Cues.Count != Frames.Count) return "its cue list does not have one entry a frame";
        for (var i = 0; i < Cues.Count; i++)
        {
            var (cue, frame) = (Cues[i], Frames[i]);
            var cluster = Clusters[frame.Cluster];
            if (cue.Position != cluster.At - SegmentBody || cue.Time != frame.Time || cue.Track != TrackNumber)
                return "its cue list does not point at its frames";
        }
        return null;
    }

    /// <summary>What the walk notes as it goes. Filled by <see cref="WebmInfo"/> only; nothing here decides anything.</summary>
    internal sealed class Notes
    {
        private sealed class OpenCluster
        {
            public int At, Body, Children, Blocks, BlockAt = -1;
            public long Time;
            public bool Sized, TimeFirst, Other;
        }

        private int _segmentAt, _segmentBody;
        private readonly List<WebmPart> _top = [];
        private readonly List<WebmCluster> _clusters = [];
        private readonly List<(int Cluster, int Relative, int At, int Length, bool Key)> _blocks = [];
        private readonly List<(uint, long)> _index = [];
        private readonly List<WebmCue> _cues = [];
        private (int At, int Count)? _lengthAt;
        private bool _checksums;
        private OpenCluster? _cluster;
        private (long Id, long Position) _seek;
        private (long Time, long Track, long Position) _cue;

        public void Segment(int at, int body) => (_segmentAt, _segmentBody) = (at, body);

        /// <summary>Padding or a checksum inside <paramref name="parent"/>.</summary>
        public void Extra(uint parent, uint id, int at, int body, int end)
        {
            if (id == WebmInfo.Checksum) _checksums = true;
            if (parent == WebmInfo.Segment) _top.Add(new WebmPart(id, at, body, end));
            else if (parent is WebmInfo.Cluster or WebmInfo.BlockGroup && _cluster is not null) _cluster.Other = true;
        }

        /// <summary>A part that holds parts begins.</summary>
        public void Open(uint parent, uint id, int at, int body, bool sized)
        {
            if (id == WebmInfo.Cluster) _cluster = new OpenCluster { At = at, Body = body, Sized = sized };
            else if (id == WebmInfo.Seek) _seek = (-1, -1);
            else if (id == WebmInfo.CuePoint) _cue = (-1, -1, -1);
            else if (id == WebmInfo.BlockGroup && _cluster is not null) _cluster.Other = true;
        }

        /// <summary>A part that holds parts has ended at <paramref name="end"/>.</summary>
        public void Close(uint parent, uint id, int at, int body, int end)
        {
            if (parent == WebmInfo.Segment) _top.Add(new WebmPart(id, at, body, end));
            if (id == WebmInfo.Cluster && _cluster is { } c)
            {
                var plain = c.Sized && c.TimeFirst && c.Children == 2 && c.Blocks == 1 && !c.Other;
                _clusters.Add(new WebmCluster(c.At, c.Body, end, c.Time, c.BlockAt, plain));
                _cluster = null;
            }
            else if (id == WebmInfo.Seek && _seek is { Id: >= 0 and <= uint.MaxValue, Position: >= 0 })
                _index.Add(((uint)_seek.Id, _seek.Position));
            else if (id == WebmInfo.CuePoint)
                _cues.Add(new WebmCue(_cue.Time, _cue.Track, _cue.Position));
        }

        /// <summary>A part that holds a value, already let through by the walk.</summary>
        public void Leaf(uint parent, uint id, int at, int body, ReadOnlySpan<byte> value)
        {
            switch (parent, id)
            {
                case (WebmInfo.Info, WebmInfo.Length):
                    _lengthAt = (body, value.Length);
                    break;
                case (WebmInfo.Seek, WebmInfo.SeekId):
                    _seek.Id = Number(value);
                    break;
                case (WebmInfo.Seek, WebmInfo.SeekPosition):
                    _seek.Position = Number(value);
                    break;
                case (WebmInfo.CuePoint, WebmInfo.CueTime):
                    _cue.Time = Number(value);
                    break;
                case (WebmInfo.CueTrackPositions, WebmInfo.CueTrack):
                    _cue.Track = Number(value);
                    break;
                case (WebmInfo.CueTrackPositions, WebmInfo.CueClusterPosition):
                    _cue.Position = Number(value);
                    break;
                case (WebmInfo.Cluster, _) when _cluster is { } c:
                    if (id == WebmInfo.ClusterTime)
                    {
                        c.Time = Number(value);
                        c.TimeFirst = c.Children == 0;
                    }
                    else if (id == WebmInfo.SimpleBlock) Block(c, at, body, value, simple: true);
                    else c.Other = true;
                    c.Children++;
                    break;
                case (WebmInfo.BlockGroup, WebmInfo.GroupBlock) when _cluster is { } c:
                    Block(c, at, body, value, simple: false);
                    break;
            }
        }

        private void Block(OpenCluster c, int at, int body, ReadOnlySpan<byte> value, bool simple)
        {
            // The walk has checked the shape: one byte of track, two of time (signed), one of switches, then the frame.
            var relative = (short)(value[1] << 8 | value[2]);
            if (c.Blocks++ == 0) c.BlockAt = at;
            // Only a simple block says by itself that its frame is a key frame.
            _blocks.Add((_clusters.Count, relative, body + 4, value.Length - 4, simple && (value[3] & 0x80) != 0));
        }

        private static long Number(ReadOnlySpan<byte> value)
        {
            long number = 0;
            foreach (var b in value) number = number << 8 | b;
            return number;
        }

        public WebmParts Build(WebmInfo info, long timeUnit, long trackNumber, double length, int bytes) => new(info)
        {
            Bytes = bytes,
            SegmentAt = _segmentAt,
            SegmentBody = _segmentBody,
            Top = _top,
            Clusters = _clusters,
            Frames = [.. _blocks.Select(b => new WebmFrame(_clusters[b.Cluster].Time + b.Relative, b.At, b.Length, b.Key, b.Cluster))],
            Index = _index,
            Cues = _cues,
            TimeUnit = timeUnit,
            TrackNumber = trackNumber,
            Length = length,
            LengthAt = _lengthAt,
            Checksums = _checksums,
        };
    }
}
