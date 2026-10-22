using GamerPlug.Recorder.Core;
using Xunit;

namespace GamerPlug.Recorder.Core.Tests;

public class ReplayBufferTests
{
    [Fact]
    public void SaveLastSecondsSnapsStartToKeyframe()
    {
        var buf = new ReplayBuffer(retentionUs: 120_000_000, audioTracks: 1,
            videoChunkBytes: 1 << 20, audioChunkBytes: 1 << 16);
        PacketFactory.FillVideo(buf, frameCount: 600, gop: 60); // 10s @ 60fps

        // Ask for the last 3 seconds; start should land on a keyframe boundary.
        var clip = buf.SaveLastSeconds(3.0);
        Assert.True(clip.Success);
        Assert.Equal(0, clip.Video[0].PtsUs % (60 * PacketFactory.FrameDurUs));
        Assert.True(clip.Video[0].IsKeyframe);
        Assert.True(clip.DurationUs >= 3_000_000);
        // End is the end-of-presentation of the last frame (pts + duration).
        Assert.Equal(600 * PacketFactory.FrameDurUs, clip.EndPtsUs);
    }

    [Fact]
    public void EmptyBufferReportsNoData()
    {
        var buf = new ReplayBuffer(retentionUs: 10_000_000);
        var clip = buf.SaveLastSeconds(5);
        Assert.False(clip.Success);
        Assert.Equal(ClipSaveStatus.NoDataInBuffer, clip.Status);
    }

    [Fact]
    public void NonPositiveSecondsReportsNegativeBuffer()
    {
        var buf = new ReplayBuffer(retentionUs: 10_000_000);
        PacketFactory.FillVideo(buf, 120);
        Assert.Equal(ClipSaveStatus.NegativeBuffer, buf.SaveLastSeconds(0).Status);
        Assert.Equal(ClipSaveStatus.NegativeBuffer, buf.SaveLastSeconds(-3).Status);
    }

    [Fact]
    public void RequestLongerThanBufferClampsToAvailable()
    {
        var buf = new ReplayBuffer(retentionUs: 120_000_000,
            videoChunkBytes: 1 << 20, audioChunkBytes: 1 << 16);
        PacketFactory.FillVideo(buf, frameCount: 120, gop: 60); // 2s available

        var clip = buf.SaveLastSeconds(60); // ask for 60s
        Assert.True(clip.Success);
        Assert.Equal(0, clip.StartPtsUs); // clamped to first keyframe
    }

    [Fact]
    public void CollectsAllAudioTracks()
    {
        var buf = new ReplayBuffer(retentionUs: 120_000_000, audioTracks: 3,
            videoChunkBytes: 1 << 20, audioChunkBytes: 1 << 16);
        PacketFactory.FillVideo(buf, frameCount: 300, gop: 60);
        for (int track = 0; track < 3; track++)
            for (int i = 0; i < 200; i++)
                buf.AddAudio(PacketFactory.Audio(track, i * PacketFactory.AudioDurUs));

        var clip = buf.SaveLastSeconds(2.0);
        Assert.True(clip.Success);
        Assert.Equal(3, clip.AudioTracks.Count);
        Assert.All(clip.AudioTracks, t => Assert.NotEmpty(t));
    }

    [Fact]
    public void AudioTrackIndexOutOfRangeThrows()
    {
        var buf = new ReplayBuffer(retentionUs: 10_000_000, audioTracks: 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => buf.AddAudio(PacketFactory.Audio(2, 0)));
    }
}
