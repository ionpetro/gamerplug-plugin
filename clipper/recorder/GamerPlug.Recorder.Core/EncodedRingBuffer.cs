namespace GamerPlug.Recorder.Core;

/// <summary>
/// Time-bounded ring of encoded packets for a single stream, built from
/// <see cref="TimeDataChunk"/>s. Packets are appended to the current chunk; when
/// it fills, the chunk is sealed (and persisted to the <see cref="IChunkStore"/>
/// for crash recovery) and a new one starts. Chunks whose data falls entirely
/// outside the retention window are evicted from both memory and the store.
/// </summary>
public sealed class EncodedRingBuffer
{
    private readonly ulong _chunkType;
    private readonly int _chunkCapacity;
    private readonly IChunkStore _store;
    private readonly LinkedList<TimeDataChunk> _chunks = new();
    private ulong _nextChunkId;

    public long MaxDurationUs { get; }
    public StreamKind Kind { get; }
    public int StreamIndex { get; }

    public EncodedRingBuffer(
        StreamKind kind,
        int streamIndex,
        long maxDurationUs,
        int? chunkCapacity = null,
        IChunkStore? store = null)
    {
        if (maxDurationUs <= 0) throw new ArgumentOutOfRangeException(nameof(maxDurationUs));
        Kind = kind;
        StreamIndex = streamIndex;
        MaxDurationUs = maxDurationUs;
        _chunkType = kind == StreamKind.Video ? TimeDataChunk.ChunkTypeVideo : TimeDataChunk.ChunkTypeAudio;
        _chunkCapacity = chunkCapacity
            ?? (kind == StreamKind.Video ? TimeDataChunk.VideoChunkBytes : TimeDataChunk.AudioChunkBytes);
        _store = store ?? NullChunkStore.Instance;
    }

    public bool IsEmpty => _chunks.Count == 0 || _chunks.All(c => c.IsEmpty);

    public long OldestPtsUs => _chunks.FirstOrDefault(c => !c.IsEmpty)?.FirstPtsUs ?? long.MaxValue;

    public long NewestPtsUs
    {
        get
        {
            long newest = long.MinValue;
            foreach (var c in _chunks)
                if (!c.IsEmpty && c.LastPtsUs > newest) newest = c.LastPtsUs;
            return newest;
        }
    }

    public int ChunkCount => _chunks.Count;

    public void Append(in MediaPacket packet)
    {
        if (packet.Kind != Kind || packet.StreamIndex != StreamIndex)
            throw new ArgumentException("packet does not belong to this stream", nameof(packet));

        var current = _chunks.Last?.Value;
        if (current is null || !current.TryAppend(packet))
        {
            // Seal the full chunk (persist for recovery) and roll to a new one.
            if (current is not null) _store.Persist(current);
            var fresh = new TimeDataChunk(_nextChunkId++, _chunkType, EnsureFits(packet));
            if (!fresh.TryAppend(packet))
                throw new InvalidOperationException("packet larger than a single chunk");
            _chunks.AddLast(fresh);
        }
        Evict();
    }

    private int EnsureFits(in MediaPacket packet)
    {
        int needed = TimeDataChunk.HeaderBytes + TimeDataChunk.RecordSize(packet.Data.Length) + 1;
        return Math.Max(_chunkCapacity, needed);
    }

    private void Evict()
    {
        long newest = NewestPtsUs;
        if (newest == long.MinValue) return;
        long cutoff = newest - MaxDurationUs;
        // Drop whole chunks whose newest packet is older than the cutoff, but
        // always keep the chunk currently being written to.
        while (_chunks.Count > 1)
        {
            var first = _chunks.First!.Value;
            if (first.LastPtsUs < cutoff)
            {
                _chunks.RemoveFirst();
                _store.Remove(first.Id);
            }
            else break;
        }
    }

    /// <summary>All packets currently retained, in append (ascending pts) order.</summary>
    public IEnumerable<MediaPacket> Packets()
    {
        foreach (var chunk in _chunks)
            foreach (var p in chunk.Packets())
                yield return p;
    }

    /// <summary>
    /// Greatest keyframe pts at or before <paramref name="targetUs"/>, or null if
    /// no keyframe precedes it. Clip cuts must begin here so the decoder has an
    /// IDR to start from.
    /// </summary>
    public long? FindKeyframePtsAtOrBefore(long targetUs)
    {
        long? best = null;
        foreach (var p in Packets())
        {
            if (p.IsKeyframe && p.PtsUs <= targetUs)
                best = p.PtsUs;
            else if (p.PtsUs > targetUs)
                break;
        }
        return best;
    }

    /// <summary>
    /// Packets whose presentation interval intersects [startUs, endUs]. For audio
    /// this naturally includes frames straddling the boundary.
    /// </summary>
    public List<MediaPacket> Collect(long startUs, long endUs)
    {
        var result = new List<MediaPacket>();
        foreach (var p in Packets())
        {
            if (p.EndPtsUs <= startUs) continue;
            if (p.PtsUs > endUs) break;
            result.Add(p);
        }
        return result;
    }

    /// <summary>Rebuild the in-memory ring from recovered chunks (ascending id).</summary>
    public void LoadRecovered(IEnumerable<TimeDataChunk> chunks)
    {
        foreach (var c in chunks)
        {
            _chunks.AddLast(c);
            if (c.Id >= _nextChunkId) _nextChunkId = c.Id + 1;
        }
        Evict();
    }

    public void Clear()
    {
        _chunks.Clear();
        _store.Clear();
    }
}
