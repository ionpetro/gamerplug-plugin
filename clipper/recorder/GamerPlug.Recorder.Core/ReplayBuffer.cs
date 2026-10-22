namespace GamerPlug.Recorder.Core;

/// <summary>
/// Coordinates one video ring and N audio-track rings on a shared microsecond
/// clock, and cuts clips out of them. This is the heart of the recorder: it
/// keeps the last <see cref="RetentionUs"/> of encoded packets so a hotkey can
/// export the last N seconds without re-encoding (REPORT.md §3/§5).
/// </summary>
public sealed class ReplayBuffer
{
    /// <summary>Safety margin kept when clamping a clip to the buffer (REPORT.md §5).</summary>
    public const long SafetyMarginUs = 5_000_000;

    private readonly EncodedRingBuffer _video;
    private readonly List<EncodedRingBuffer> _audio = new();
    private readonly long _retentionUs;
    private readonly Func<StreamKind, int, IChunkStore>? _storeFactory;
    private readonly int? _videoChunkBytes;
    private readonly int? _audioChunkBytes;

    public long RetentionUs => _retentionUs;
    public int AudioTrackCount => _audio.Count;

    public ReplayBuffer(
        long retentionUs,
        int audioTracks = 1,
        Func<StreamKind, int, IChunkStore>? storeFactory = null,
        int? videoChunkBytes = null,
        int? audioChunkBytes = null)
    {
        if (retentionUs <= 0) throw new ArgumentOutOfRangeException(nameof(retentionUs));
        _retentionUs = retentionUs;
        _storeFactory = storeFactory;
        _videoChunkBytes = videoChunkBytes;
        _audioChunkBytes = audioChunkBytes;

        _video = new EncodedRingBuffer(
            StreamKind.Video, 0, retentionUs, videoChunkBytes,
            storeFactory?.Invoke(StreamKind.Video, 0));

        for (int i = 0; i < audioTracks; i++)
        {
            _audio.Add(new EncodedRingBuffer(
                StreamKind.Audio, i, retentionUs, audioChunkBytes,
                storeFactory?.Invoke(StreamKind.Audio, i)));
        }
    }

    public void AddVideo(in MediaPacket packet) => _video.Append(packet);

    public void AddAudio(in MediaPacket packet)
    {
        if (packet.StreamIndex < 0 || packet.StreamIndex >= _audio.Count)
            throw new ArgumentOutOfRangeException(nameof(packet), "audio track index out of range");
        _audio[packet.StreamIndex].Append(packet);
    }

    /// <summary>Newest presentation time across all streams, or null if empty.</summary>
    public long? NewestPtsUs()
    {
        long newest = _video.IsEmpty ? long.MinValue : _video.NewestPtsUs;
        foreach (var a in _audio)
            if (!a.IsEmpty && a.NewestPtsUs > newest) newest = a.NewestPtsUs;
        return newest == long.MinValue ? null : newest;
    }

    /// <summary>
    /// Cut a clip of the last <paramref name="seconds"/> seconds. The start is
    /// snapped back to the nearest video keyframe so the clip is decodable, and
    /// clamped to what the buffer actually holds.
    /// </summary>
    public ClipExtract SaveLastSeconds(double seconds, long? nowUs = null)
    {
        if (seconds <= 0) return ClipExtract.Failed(ClipSaveStatus.NegativeBuffer);
        if (_video.IsEmpty) return ClipExtract.Failed(ClipSaveStatus.NoDataInBuffer);

        long end = nowUs ?? _video.NewestPtsUs;
        long requestedStart = end - (long)(seconds * 1_000_000);

        // Clamp to the oldest retained data. A requested window longer than the
        // buffer simply yields the whole buffer rather than failing.
        long oldest = _video.OldestPtsUs;
        long clampedStart = Math.Max(requestedStart, oldest);
        if (clampedStart >= end) return ClipExtract.Failed(ClipSaveStatus.NegativeBuffer);

        long? keyframeStart = _video.FindKeyframePtsAtOrBefore(clampedStart)
            ?? _video.FindKeyframePtsAtOrBefore(end); // fall back to any keyframe in-range
        if (keyframeStart is null) return ClipExtract.Failed(ClipSaveStatus.NoKeyframe);

        long start = keyframeStart.Value;
        var video = _video.Collect(start, end);
        if (video.Count == 0) return ClipExtract.Failed(ClipSaveStatus.NoDataInBuffer);

        var audioTracks = new List<IReadOnlyList<MediaPacket>>();
        foreach (var a in _audio)
            audioTracks.Add(a.Collect(start, end));

        return new ClipExtract
        {
            Status = ClipSaveStatus.Ok,
            StartPtsUs = start,
            EndPtsUs = end,
            Video = video,
            AudioTracks = audioTracks,
        };
    }

    /// <summary>
    /// Rebuild buffers from a store after a crash (ClipRecovery). Call before the
    /// first <see cref="AddVideo"/>.
    /// </summary>
    public void Recover()
    {
        if (_storeFactory is null) return;
        _video.LoadRecovered(_storeFactory(StreamKind.Video, 0).Recover());
        for (int i = 0; i < _audio.Count; i++)
            _audio[i].LoadRecovered(_storeFactory(StreamKind.Audio, i).Recover());
    }

    public void Clear()
    {
        _video.Clear();
        foreach (var a in _audio) a.Clear();
    }
}
