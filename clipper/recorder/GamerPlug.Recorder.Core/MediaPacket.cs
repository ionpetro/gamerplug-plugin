namespace GamerPlug.Recorder.Core;

/// <summary>
/// Kind of elementary stream a packet belongs to. The replay buffer keeps one
/// ring per stream so video and each audio track can evict independently.
/// </summary>
public enum StreamKind
{
    Video = 0,
    Audio = 1,
}

/// <summary>
/// One encoded media packet (an access unit / audio frame). The replay buffer
/// stores <em>encoded</em> packets, never raw frames, so saving a clip is a
/// remux (no re-encode) — matching Medal's scope.dll <c>TimeDataBuffer</c>
/// (REPORT.md §3/§5 "Replay buffer").
/// </summary>
public readonly struct MediaPacket
{
    /// <summary>Index of the stream within its kind (e.g. audio track 0, 1, 2).</summary>
    public int StreamIndex { get; }

    public StreamKind Kind { get; }

    /// <summary>Presentation timestamp in microseconds on the shared clock.</summary>
    public long PtsUs { get; }

    /// <summary>Decode timestamp in microseconds (== <see cref="PtsUs"/> if unused).</summary>
    public long DtsUs { get; }

    /// <summary>Packet duration in microseconds.</summary>
    public long DurationUs { get; }

    /// <summary>True for an IDR/keyframe. Clip cuts must start at a keyframe.</summary>
    public bool IsKeyframe { get; }

    public byte[] Data { get; }

    public MediaPacket(
        StreamKind kind,
        int streamIndex,
        long ptsUs,
        long dtsUs,
        long durationUs,
        bool isKeyframe,
        byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (data.Length == 0)
        {
            // Mirrors scope.dll's "zero-length objects are a BUG assert" invariant.
            throw new ArgumentException("media packet payload must be non-empty", nameof(data));
        }
        Kind = kind;
        StreamIndex = streamIndex;
        PtsUs = ptsUs;
        DtsUs = dtsUs;
        DurationUs = durationUs;
        IsKeyframe = isKeyframe;
        Data = data;
    }

    public long EndPtsUs => PtsUs + DurationUs;
}
