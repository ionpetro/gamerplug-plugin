namespace GamerPlug.Recorder.Core;

/// <summary>
/// Durability backend for sealed chunks. Mirrors scope.dll's three
/// <c>TimeDataBuffer</c> variants (REPORT.md §5): memory (no persistence), file,
/// and memory-mapped file. A store lets the ring survive a crash: sealed chunks
/// are persisted as they roll over, and <see cref="Recover"/> rebuilds the ring
/// from intact chunks after an unclean shutdown (the ClipRecovery path).
/// </summary>
public interface IChunkStore
{
    /// <summary>Persist a sealed chunk. Memory backends may no-op.</summary>
    void Persist(TimeDataChunk chunk);

    /// <summary>Drop a chunk that has aged out of the ring.</summary>
    void Remove(ulong chunkId);

    /// <summary>
    /// Recover previously persisted, intact chunks (ascending by id). Corrupt or
    /// partially written chunks are skipped, not thrown.
    /// </summary>
    IReadOnlyList<TimeDataChunk> Recover();

    /// <summary>Remove all persisted chunks.</summary>
    void Clear();
}

/// <summary>In-RAM mode: chunks live only in the ring, nothing is persisted.</summary>
public sealed class NullChunkStore : IChunkStore
{
    public static readonly NullChunkStore Instance = new();

    public void Persist(TimeDataChunk chunk) { }
    public void Remove(ulong chunkId) { }
    public IReadOnlyList<TimeDataChunk> Recover() => Array.Empty<TimeDataChunk>();
    public void Clear() { }
}
