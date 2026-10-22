using GamerPlug.Recorder.Core;
using Xunit;

namespace GamerPlug.Recorder.Core.Tests;

public class EncodedRingBufferTests
{
    [Fact]
    public void EvictsPacketsOlderThanRetention()
    {
        // 1 second retention, small chunks so rollover happens quickly.
        var ring = new EncodedRingBuffer(StreamKind.Video, 0, maxDurationUs: 1_000_000, chunkCapacity: 512);
        for (int i = 0; i < 300; i++) // 300 * 16.6ms = ~5s of frames
            ring.Append(PacketFactory.Video(i * PacketFactory.FrameDurUs, keyframe: i % 30 == 0));

        long span = ring.NewestPtsUs - ring.OldestPtsUs;
        // Retention is 1s but whole chunks are evicted, so the span is bounded by
        // retention plus at most one chunk's worth of data.
        Assert.True(span <= 1_000_000 + 60 * PacketFactory.FrameDurUs,
            $"span {span} exceeds retention + one chunk");
        Assert.True(ring.NewestPtsUs >= 299 * PacketFactory.FrameDurUs - 1);
    }

    [Fact]
    public void FindsKeyframeAtOrBeforeTarget()
    {
        var ring = new EncodedRingBuffer(StreamKind.Video, 0, maxDurationUs: 10_000_000, chunkCapacity: 65536);
        for (int i = 0; i < 120; i++)
            ring.Append(PacketFactory.Video(i * PacketFactory.FrameDurUs, keyframe: i % 30 == 0));

        // Target between keyframes at index 30 (pts 30*dur) and 60.
        long target = 45 * PacketFactory.FrameDurUs;
        long? kf = ring.FindKeyframePtsAtOrBefore(target);
        Assert.Equal(30 * PacketFactory.FrameDurUs, kf);
    }

    [Fact]
    public void CollectReturnsPacketsIntersectingRange()
    {
        var ring = new EncodedRingBuffer(StreamKind.Video, 0, maxDurationUs: 10_000_000, chunkCapacity: 65536);
        for (int i = 0; i < 100; i++)
            ring.Append(PacketFactory.Video(i * PacketFactory.FrameDurUs, keyframe: i % 30 == 0));

        long start = 10 * PacketFactory.FrameDurUs;
        long end = 20 * PacketFactory.FrameDurUs;
        var collected = ring.Collect(start, end);

        Assert.All(collected, p => Assert.True(p.EndPtsUs > start && p.PtsUs <= end));
        Assert.Contains(collected, p => p.PtsUs == start);
    }

    [Fact]
    public void RejectsPacketFromWrongStream()
    {
        var ring = new EncodedRingBuffer(StreamKind.Video, 0, maxDurationUs: 1_000_000);
        Assert.Throws<ArgumentException>(() => ring.Append(PacketFactory.Audio(0, 0)));
    }
}
