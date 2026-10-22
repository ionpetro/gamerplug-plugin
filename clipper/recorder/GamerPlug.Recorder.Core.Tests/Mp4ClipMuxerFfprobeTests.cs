using GamerPlug.Recorder.Core;
using GamerPlug.Recorder.Core.Engine;
using GamerPlug.Recorder.Core.Mux;
using Xunit;

namespace GamerPlug.Recorder.Core.Tests;

/// <summary>
/// End-to-end muxer validation: generate real H.264 (Annex B) and AAC (ADTS)
/// elementary streams with ffmpeg, feed them through the replay-buffer extract +
/// <see cref="Mp4ClipMuxer"/>, and confirm ffprobe reads the resulting MP4 as a
/// valid h264+aac file. Skips when ffmpeg/ffprobe are not installed.
/// </summary>
public class Mp4ClipMuxerFfprobeTests : IDisposable
{
    private readonly string _dir;

    public Mp4ClipMuxerFfprobeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gpmux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static List<MediaPacket> PacketsFromElementaryStream(
        string ffprobe, string file, StreamKind kind, int streamIndex)
    {
        byte[] bytes = File.ReadAllBytes(file);
        var probed = FfTools.ProbePackets(ffprobe, file);
        var packets = new List<MediaPacket>();
        long dur = kind == StreamKind.Video ? 33_333 : 21_333;
        int index = 0;
        foreach (var p in probed)
        {
            if (p.Size <= 0 || p.Pos < 0 || p.Pos + p.Size > bytes.Length) continue;
            var data = new byte[p.Size];
            Array.Copy(bytes, p.Pos, data, 0, p.Size);
            long pts = index * dur; // raw streams have no timestamps; assign by order
            packets.Add(new MediaPacket(kind, streamIndex, pts, pts, dur, p.Key, data));
            index++;
        }
        return packets;
    }

    [SkippableFact]
    public void MuxesValidMp4FromRealStreams()
    {
        Skip.IfNot(FfTools.Available, "ffmpeg/ffprobe not installed");
        string ffmpeg = FfTools.Which("ffmpeg")!;
        string ffprobe = FfTools.Which("ffprobe")!;

        string h264 = Path.Combine(_dir, "v.h264");
        string aac = Path.Combine(_dir, "a.aac");
        string outMp4 = Path.Combine(_dir, "clip.mp4");

        var (vcode, _, verr) = FfTools.Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=duration=1:size=320x240:rate=30",
            "-c:v", "libx264", "-g", "15", "-bf", "0", "-pix_fmt", "yuv420p",
            "-f", "h264", h264);
        Assert.True(vcode == 0, $"ffmpeg video gen failed: {verr}");

        var (acode, _, aerr) = FfTools.Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=1:sample_rate=48000",
            "-c:a", "aac", "-f", "adts", aac);
        Assert.True(acode == 0, $"ffmpeg audio gen failed: {aerr}");

        var videoPackets = PacketsFromElementaryStream(ffprobe, h264, StreamKind.Video, 0);
        var audioPackets = PacketsFromElementaryStream(ffprobe, aac, StreamKind.Audio, 0);
        Assert.NotEmpty(videoPackets);
        Assert.NotEmpty(audioPackets);

        long start = Math.Min(videoPackets[0].PtsUs, audioPackets[0].PtsUs);
        long end = Math.Max(videoPackets[^1].EndPtsUs, audioPackets[^1].EndPtsUs);
        var extract = new ClipExtract
        {
            Status = ClipSaveStatus.Ok,
            StartPtsUs = start,
            EndPtsUs = end,
            Video = videoPackets,
            AudioTracks = new List<IReadOnlyList<MediaPacket>> { audioPackets },
        };

        var muxer = new Mp4ClipMuxer();
        var hints = new MuxHints(320, 240, 30, "h264", new[] { "aac" });
        var clip = muxer.WriteClip(extract, outMp4, hints);
        Assert.True(File.Exists(outMp4));
        Assert.True(clip.SizeBytes > 0);

        // ffprobe must parse the file and see both streams.
        var (pcode, pout, perr) = FfTools.Run(ffprobe, "-hide_banner", "-loglevel", "error",
            "-show_entries", "stream=codec_name,codec_type,width,height",
            "-of", "csv=p=0", outMp4);
        Assert.True(pcode == 0, $"ffprobe failed: {perr}");
        Assert.Contains("h264", pout);
        Assert.Contains("aac", pout);
        Assert.Contains("320,240", pout.Replace(" ", ""));

        // A full decode pass should not error on the remuxed file.
        var (dcode, _, _) = FfTools.Run(ffprobe, "-hide_banner", "-loglevel", "error",
            "-count_frames", "-select_streams", "v:0",
            "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", outMp4);
        Assert.True(dcode == 0, "ffprobe frame count failed on remuxed file");
    }
}
