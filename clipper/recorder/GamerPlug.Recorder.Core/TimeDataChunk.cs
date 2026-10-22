using System.Buffers.Binary;

namespace GamerPlug.Recorder.Core;

/// <summary>
/// A fixed-size chunk of encoded packet records, modeled on scope.dll's
/// <c>TimeDataChunk&lt;N&gt;</c> (REPORT.md §5): 16 MiB for video, 1 MiB for
/// audio. Records are typed (a 1-byte tag) and appended contiguously. A chunk
/// serializes to a 64-byte header plus payload, with CRC-32 over both so a
/// partially written chunk is detected and skipped during crash recovery.
/// This is an independent implementation; no Medal code or layout is reused.
/// </summary>
public sealed class TimeDataChunk
{
    /// <summary>Default video chunk size (16 MiB), per REPORT.md §5.</summary>
    public const int VideoChunkBytes = 16 * 1024 * 1024;

    /// <summary>Default audio chunk size (1 MiB), per REPORT.md §5.</summary>
    public const int AudioChunkBytes = 1 * 1024 * 1024;

    public const int HeaderBytes = 64;

    /// <summary>Our own magic (not Medal's); marks a valid chunk header.</summary>
    public const ulong Magic = 0x47504C5547434B31; // "GPLUGCK1"

    // ASCII "VIDEOCHK" / "AUDIOCHK" as 64-bit tags.
    public const ulong ChunkTypeVideo = 0x56494445_4F43484B;
    public const ulong ChunkTypeAudio = 0x41554449_4F43484B;

    private const byte RecordTypePacket = 0x01;

    private readonly byte[] _buffer;
    private int _writePos;

    public ulong Id { get; }
    public ulong ChunkType { get; }
    public int Capacity => _buffer.Length;
    public int UsedBytes => _writePos;
    public int RecordCount { get; private set; }
    public long FirstPtsUs { get; private set; } = long.MaxValue;
    public long LastPtsUs { get; private set; } = long.MinValue;

    public TimeDataChunk(ulong id, ulong chunkType, int capacity)
    {
        if (capacity <= HeaderBytes)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        Id = id;
        ChunkType = chunkType;
        _buffer = new byte[capacity];
    }

    private TimeDataChunk(ulong id, ulong chunkType, byte[] payload, int usedBytes, int recordCount, long firstPts, long lastPts)
    {
        Id = id;
        ChunkType = chunkType;
        _buffer = payload;
        _writePos = usedBytes;
        RecordCount = recordCount;
        FirstPtsUs = firstPts;
        LastPtsUs = lastPts;
    }

    public bool IsEmpty => RecordCount == 0;

    /// <summary>Bytes a packet with the given payload length occupies as a record.</summary>
    public static int RecordSize(int dataLength) => 1 + 1 + 4 + 8 + 8 + 8 + 1 + 4 + dataLength;

    /// <summary>
    /// Appends a packet. Returns false (without mutating) if it does not fit, so
    /// the caller can roll to a fresh chunk.
    /// </summary>
    public bool TryAppend(in MediaPacket packet)
    {
        int needed = RecordSize(packet.Data.Length);
        if (_writePos + needed > _buffer.Length)
            return false;

        var span = _buffer.AsSpan(_writePos);
        int o = 0;
        span[o++] = RecordTypePacket;
        span[o++] = (byte)packet.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o), packet.StreamIndex); o += 4;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(o), packet.PtsUs); o += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(o), packet.DtsUs); o += 8;
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(o), packet.DurationUs); o += 8;
        span[o++] = packet.IsKeyframe ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(o), packet.Data.Length); o += 4;
        packet.Data.CopyTo(span.Slice(o));
        o += packet.Data.Length;

        _writePos += o;
        RecordCount++;
        if (packet.PtsUs < FirstPtsUs) FirstPtsUs = packet.PtsUs;
        if (packet.EndPtsUs > LastPtsUs) LastPtsUs = packet.EndPtsUs;
        return true;
    }

    /// <summary>Enumerates the packets stored in this chunk in append order.</summary>
    public IEnumerable<MediaPacket> Packets()
    {
        int o = 0;
        for (int i = 0; i < RecordCount; i++)
        {
            var span = _buffer.AsSpan(o);
            byte recordType = span[0];
            if (recordType != RecordTypePacket)
                throw new InvalidDataException($"unknown record type {recordType}");
            int p = 1;
            var kind = (StreamKind)span[p++];
            int streamIndex = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(p)); p += 4;
            long pts = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(p)); p += 8;
            long dts = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(p)); p += 8;
            long dur = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(p)); p += 8;
            bool key = span[p++] != 0;
            int len = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(p)); p += 4;
            var data = span.Slice(p, len).ToArray(); p += len;
            o += p;
            yield return new MediaPacket(kind, streamIndex, pts, dts, dur, key, data);
        }
    }

    /// <summary>Serializes the chunk (header + payload) for a file/mmap backend.</summary>
    public byte[] Serialize()
    {
        var outBuf = new byte[HeaderBytes + _writePos];
        var h = outBuf.AsSpan(0, HeaderBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(0), Magic);
        BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(8), ChunkType);
        BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(16), Id);
        BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(24), (ulong)_writePos);
        BinaryPrimitives.WriteInt64LittleEndian(h.Slice(32), RecordCount == 0 ? 0 : FirstPtsUs);
        BinaryPrimitives.WriteInt64LittleEndian(h.Slice(40), RecordCount == 0 ? 0 : LastPtsUs);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(48), (uint)RecordCount);
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(52), 0); // reserved
        uint headerCrc = Crc32.Compute(h.Slice(0, 56));
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(56), headerCrc);

        _buffer.AsSpan(0, _writePos).CopyTo(outBuf.AsSpan(HeaderBytes));
        uint payloadCrc = Crc32.Compute(outBuf.AsSpan(HeaderBytes, _writePos));
        BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(60), payloadCrc);
        return outBuf;
    }

    /// <summary>
    /// Parses a serialized chunk, validating magic and both checksums. Returns
    /// null when the data is incomplete or corrupt (the crash-recovery case) so
    /// the loader can stop at the last intact chunk.
    /// </summary>
    public static TimeDataChunk? TryDeserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderBytes) return null;
        var h = data.Slice(0, HeaderBytes);
        if (BinaryPrimitives.ReadUInt64LittleEndian(h.Slice(0)) != Magic) return null;

        uint storedHeaderCrc = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(56));
        if (Crc32.Compute(h.Slice(0, 56)) != storedHeaderCrc) return null;

        ulong chunkType = BinaryPrimitives.ReadUInt64LittleEndian(h.Slice(8));
        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(h.Slice(16));
        ulong payloadSize = BinaryPrimitives.ReadUInt64LittleEndian(h.Slice(24));
        long firstPts = BinaryPrimitives.ReadInt64LittleEndian(h.Slice(32));
        long lastPts = BinaryPrimitives.ReadInt64LittleEndian(h.Slice(40));
        uint recordCount = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(48));
        uint storedPayloadCrc = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(60));

        if (payloadSize > int.MaxValue) return null;
        int size = (int)payloadSize;
        if (data.Length < HeaderBytes + size) return null;

        var payload = data.Slice(HeaderBytes, size);
        if (Crc32.Compute(payload) != storedPayloadCrc) return null;

        var owned = new byte[Math.Max(size, HeaderBytes + 1)];
        payload.CopyTo(owned);
        return new TimeDataChunk(id, chunkType, owned, size, (int)recordCount,
            recordCount == 0 ? long.MaxValue : firstPts,
            recordCount == 0 ? long.MinValue : lastPts);
    }

    /// <summary>Total serialized size (header + payload).</summary>
    public int SerializedSize => HeaderBytes + _writePos;
}
