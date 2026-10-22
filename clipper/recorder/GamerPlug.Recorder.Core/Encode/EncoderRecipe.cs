namespace GamerPlug.Recorder.Core.Encode;

/// <summary>
/// One ffmpeg invocation in the encoder fallback chain. The Windows recorder
/// tries each attempt in order until ffmpeg starts successfully, then notifies
/// the UI via <c>fallbackEncoder</c> if it had to skip the preferred backend.
/// </summary>
public sealed record EncoderAttempt(
    EncoderBackend Backend,
    VideoCodec Codec,
    string FfmpegVideoCodec,
    IReadOnlyList<string> ExtraArgs);

/// <summary>
/// Builds ffmpeg argument lists for NVENC / AMF / QuickSync / software, matching
/// the backend list in REPORT.md §3/§5. The recipe is platform-independent so
/// the fallback order is unit-tested; the Windows host just spawns ffmpeg.
/// Software uses <c>libx264</c> first and <c>libopenh264</c> as a last resort
/// (LGPL-friendly). No GPL components are referenced.
/// </summary>
public static class EncoderRecipe
{
    /// <summary>
    /// Ordered attempts for the requested backend/codec. <see cref="EncoderBackend.Auto"/>
    /// tries NVENC, then AMF, then QSV, then software.
    /// </summary>
    public static IReadOnlyList<EncoderAttempt> FallbackChain(EncoderBackend backend, VideoCodec codec)
    {
        var preferred = backend switch
        {
            EncoderBackend.Nvenc => new[] { EncoderBackend.Nvenc },
            EncoderBackend.Amf => new[] { EncoderBackend.Amf },
            EncoderBackend.Qsv => new[] { EncoderBackend.Qsv },
            EncoderBackend.Software => new[] { EncoderBackend.Software },
            _ => new[] { EncoderBackend.Nvenc, EncoderBackend.Amf, EncoderBackend.Qsv, EncoderBackend.Software },
        };

        var attempts = new List<EncoderAttempt>();
        foreach (var b in preferred)
            attempts.AddRange(AttemptsFor(b, codec));
        return attempts;
    }

    public static IReadOnlyList<string> BuildVideoArgs(
        EncoderAttempt attempt,
        int width,
        int height,
        int fps,
        int bitrateKbps)
    {
        var args = new List<string>
        {
            "-f", "rawvideo",
            "-pix_fmt", "bgra",
            "-s", $"{width}x{height}",
            "-r", fps.ToString(),
            "-i", "pipe:0",
            "-an",
            "-c:v", attempt.FfmpegVideoCodec,
            "-b:v", $"{bitrateKbps}k",
            "-maxrate", $"{bitrateKbps}k",
            "-bufsize", $"{bitrateKbps * 2}k",
            "-g", (fps * 2).ToString(), // 2-second GOP so clip cuts land on a nearby keyframe
            "-bf", "0",                 // no B-frames: every non-key is independently decodable from the last IDR
        };
        args.AddRange(attempt.ExtraArgs);
        // dump_extra puts SPS/PPS on every keyframe so the muxer can start mid-stream.
        args.AddRange(new[] { "-bsf:v", "dump_extra=freq=keyframe", "-f", "h264", "pipe:1" });
        return args;
    }

    public static IReadOnlyList<string> BuildAudioArgs(int sampleRate = 48000, int channels = 2, int bitrateKbps = 160)
    {
        return new[]
        {
            "-f", "s16le",
            "-ar", sampleRate.ToString(),
            "-ac", channels.ToString(),
            "-i", "pipe:0",
            "-vn",
            "-c:a", "aac",
            "-b:a", $"{bitrateKbps}k",
            "-f", "adts",
            "pipe:1",
        };
    }

    private static IEnumerable<EncoderAttempt> AttemptsFor(EncoderBackend backend, VideoCodec codec)
    {
        switch (backend)
        {
            case EncoderBackend.Nvenc:
                yield return new EncoderAttempt(backend, codec, NvencName(codec),
                    new[] { "-preset", "p4", "-tune", "ll", "-rc", "cbr" });
                break;
            case EncoderBackend.Amf:
                yield return new EncoderAttempt(backend, codec, AmfName(codec),
                    new[] { "-quality", "speed", "-rc", "cbr" });
                break;
            case EncoderBackend.Qsv:
                yield return new EncoderAttempt(backend, codec, QsvName(codec),
                    new[] { "-preset", "veryfast", "-look_ahead", "0" });
                break;
            default:
                // libx264 is GPL-or-LGPL depending on the ffmpeg build; libopenh264 is BSD.
                // We try x264 first (most ffmpeg LGPL builds still ship it as a wrapper
                // around OpenH264 or omit it) and fall back to libopenh264.
                if (codec == VideoCodec.H264)
                {
                    yield return new EncoderAttempt(EncoderBackend.Software, codec, "libx264",
                        new[] { "-preset", "veryfast", "-tune", "zerolatency" });
                    yield return new EncoderAttempt(EncoderBackend.Software, codec, "libopenh264",
                        Array.Empty<string>());
                }
                else
                {
                    // Software HEVC/AV1 are optional; if the build lacks them the host skips.
                    yield return new EncoderAttempt(EncoderBackend.Software, codec,
                        codec == VideoCodec.Hevc ? "libx265" : "libsvtav1",
                        Array.Empty<string>());
                }
                break;
        }
    }

    private static string NvencName(VideoCodec codec) => codec switch
    {
        VideoCodec.Hevc => "hevc_nvenc",
        VideoCodec.Av1 => "av1_nvenc",
        _ => "h264_nvenc",
    };

    private static string AmfName(VideoCodec codec) => codec switch
    {
        VideoCodec.Hevc => "hevc_amf",
        VideoCodec.Av1 => "av1_amf",
        _ => "h264_amf",
    };

    private static string QsvName(VideoCodec codec) => codec switch
    {
        VideoCodec.Hevc => "hevc_qsv",
        VideoCodec.Av1 => "av1_qsv",
        _ => "h264_qsv",
    };
}
