using GamerPlug.Recorder.Core.Mux;

namespace GamerPlug.Recorder.Core.Encode;

/// <summary>
/// Turns ffmpeg's Annex-B H.264 / ADTS AAC stdout into <see cref="MediaPacket"/>s
/// for the replay buffer. Shared by the Windows capture pipeline and the
/// cross-platform testsrc dev recorder.
/// </summary>
public static class EncodedStreamPump
{
    public static bool ContainsIdr(byte[] au)
    {
        foreach (var nal in AnnexB.SplitNalUnits(au))
            if (AnnexB.NalType(nal.Span) == AnnexB.NalIdr) return true;
        return false;
    }

    public static void PumpH264(Stream output, int fps, Action<MediaPacket> onVideo, CancellationToken ct)
    {
        var splitter = new AnnexBAccessUnitSplitter();
        var buf = new byte[64 * 1024];
        long pts = 0;
        long dur = 1_000_000 / Math.Max(1, fps);
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = output.Read(buf, 0, buf.Length); }
            catch { break; }
            if (n <= 0) break;
            foreach (var au in splitter.Push(buf.AsSpan(0, n)))
            {
                onVideo(new MediaPacket(StreamKind.Video, 0, pts, pts, dur, ContainsIdr(au), au));
                pts += dur;
            }
        }
        foreach (var au in splitter.Flush())
        {
            onVideo(new MediaPacket(StreamKind.Video, 0, pts, pts, dur, ContainsIdr(au), au));
            pts += dur;
        }
    }

    public static void PumpAdts(Stream output, int track, Action<MediaPacket> onAudio, CancellationToken ct)
    {
        var splitter = new AdtsFrameSplitter();
        var buf = new byte[16 * 1024];
        long pts = 0;
        const long dur = 21_333; // 1024 samples @ 48 kHz
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = output.Read(buf, 0, buf.Length); }
            catch { break; }
            if (n <= 0) break;
            foreach (var frame in splitter.Push(buf.AsSpan(0, n)))
            {
                onAudio(new MediaPacket(StreamKind.Audio, track, pts, pts, dur, false, frame));
                pts += dur;
            }
        }
        foreach (var frame in splitter.Flush())
        {
            onAudio(new MediaPacket(StreamKind.Audio, track, pts, pts, dur, false, frame));
            pts += dur;
        }
    }
}
