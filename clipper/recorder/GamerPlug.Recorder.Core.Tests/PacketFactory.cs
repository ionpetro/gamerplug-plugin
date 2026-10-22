using GamerPlug.Recorder.Core;

namespace GamerPlug.Recorder.Core.Tests;

internal static class PacketFactory
{
    public const long FrameDurUs = 16_666; // ~60 fps
    public const long AudioDurUs = 21_333; // ~1024 samples @ 48 kHz

    public static MediaPacket Video(long ptsUs, bool keyframe, int dataLen = 64)
    {
        var data = new byte[dataLen];
        for (int i = 0; i < dataLen; i++) data[i] = (byte)(ptsUs + i);
        return new MediaPacket(StreamKind.Video, 0, ptsUs, ptsUs, FrameDurUs, keyframe, data);
    }

    public static MediaPacket Audio(int track, long ptsUs, int dataLen = 48)
    {
        var data = new byte[dataLen];
        for (int i = 0; i < dataLen; i++) data[i] = (byte)(ptsUs + track + i);
        return new MediaPacket(StreamKind.Audio, track, ptsUs, ptsUs, AudioDurUs, false, data);
    }

    /// <summary>Fill a ring with a GOP pattern: keyframe every <paramref name="gop"/> frames.</summary>
    public static void FillVideo(ReplayBuffer buf, int frameCount, int gop = 60, long startPts = 0)
    {
        for (int i = 0; i < frameCount; i++)
        {
            long pts = startPts + i * FrameDurUs;
            buf.AddVideo(Video(pts, keyframe: i % gop == 0));
        }
    }
}
