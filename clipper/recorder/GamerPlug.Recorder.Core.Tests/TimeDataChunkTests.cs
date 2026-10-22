using GamerPlug.Recorder.Core;
using Xunit;

namespace GamerPlug.Recorder.Core.Tests;

public class TimeDataChunkTests
{
    [Fact]
    public void AppendsAndEnumeratesPacketsInOrder()
    {
        var chunk = new TimeDataChunk(1, TimeDataChunk.ChunkTypeVideo, 4096);
        Assert.True(chunk.TryAppend(PacketFactory.Video(0, true)));
        Assert.True(chunk.TryAppend(PacketFactory.Video(16_666, false)));

        var packets = chunk.Packets().ToList();
        Assert.Equal(2, packets.Count);
        Assert.Equal(0, packets[0].PtsUs);
        Assert.True(packets[0].IsKeyframe);
        Assert.Equal(16_666, packets[1].PtsUs);
        Assert.False(packets[1].IsKeyframe);
        Assert.Equal(2, chunk.RecordCount);
    }

    [Fact]
    public void ReturnsFalseWhenPacketDoesNotFit()
    {
        var chunk = new TimeDataChunk(1, TimeDataChunk.ChunkTypeVideo, 128);
        Assert.True(chunk.TryAppend(PacketFactory.Video(0, true, dataLen: 16)));
        // Second large packet should not fit in the tiny chunk.
        Assert.False(chunk.TryAppend(PacketFactory.Video(16_666, false, dataLen: 256)));
        Assert.Equal(1, chunk.RecordCount);
    }

    [Fact]
    public void SerializeDeserializeRoundTrips()
    {
        var chunk = new TimeDataChunk(42, TimeDataChunk.ChunkTypeAudio, 8192);
        chunk.TryAppend(PacketFactory.Audio(0, 0));
        chunk.TryAppend(PacketFactory.Audio(0, 21_333));

        byte[] bytes = chunk.Serialize();
        var restored = TimeDataChunk.TryDeserialize(bytes);

        Assert.NotNull(restored);
        Assert.Equal(42u, restored!.Id);
        Assert.Equal(TimeDataChunk.ChunkTypeAudio, restored.ChunkType);
        Assert.Equal(2, restored.RecordCount);
        var packets = restored.Packets().ToList();
        Assert.Equal(0, packets[0].PtsUs);
        Assert.Equal(21_333, packets[1].PtsUs);
    }

    [Fact]
    public void DetectsCorruptHeader()
    {
        var chunk = new TimeDataChunk(1, TimeDataChunk.ChunkTypeVideo, 4096);
        chunk.TryAppend(PacketFactory.Video(0, true));
        byte[] bytes = chunk.Serialize();

        bytes[10] ^= 0xFF; // flip a byte inside the header (chunkType)
        Assert.Null(TimeDataChunk.TryDeserialize(bytes));
    }

    [Fact]
    public void DetectsCorruptPayload()
    {
        var chunk = new TimeDataChunk(1, TimeDataChunk.ChunkTypeVideo, 4096);
        chunk.TryAppend(PacketFactory.Video(0, true));
        byte[] bytes = chunk.Serialize();

        bytes[TimeDataChunk.HeaderBytes + 4] ^= 0xFF; // corrupt a payload byte
        Assert.Null(TimeDataChunk.TryDeserialize(bytes));
    }

    [Fact]
    public void DetectsTruncatedChunk()
    {
        var chunk = new TimeDataChunk(1, TimeDataChunk.ChunkTypeVideo, 4096);
        chunk.TryAppend(PacketFactory.Video(0, true));
        byte[] bytes = chunk.Serialize();

        var truncated = bytes.AsSpan(0, bytes.Length - 5).ToArray();
        Assert.Null(TimeDataChunk.TryDeserialize(truncated));
    }

    [Fact]
    public void RejectsBogusMagic()
    {
        var bytes = new byte[TimeDataChunk.HeaderBytes + 10];
        Assert.Null(TimeDataChunk.TryDeserialize(bytes));
    }
}
