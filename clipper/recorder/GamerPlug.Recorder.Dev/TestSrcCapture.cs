using GamerPlug.Recorder.Core;
using GamerPlug.Recorder.Core.Encode;
using GamerPlug.Recorder.Core.Engine;

namespace GamerPlug.Recorder.Dev;

/// <summary>
/// Cross-platform stand-in for WGC/DXGI capture. Spawns ffmpeg lavfi
/// <c>testsrc2</c> + <c>sine</c>, encodes to H.264/AAC, and pushes packets
/// into the same <see cref="RecorderEngine"/> ring the Windows host uses.
/// </summary>
public sealed class TestSrcCapture : ICaptureSource
{
    private readonly RecorderSettings _settings;
    private readonly Action<string, string, string>? _onFallback;
    private FfmpegProcess? _video;
    private FfmpegProcess? _audio;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _workers = new();

    public string CaptureBackend { get; private set; } = "testsrc";
    public string EncoderName { get; private set; } = "none";
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Fps { get; private set; }

    public TestSrcCapture(RecorderSettings settings, Action<string, string, string>? onFallback = null)
    {
        _settings = settings;
        _onFallback = onFallback;
        (Width, Height, Fps) = ResolveGeometry(settings);
    }

    public void Start(Action<MediaPacket> onVideo, Action<MediaPacket> onAudio)
    {
        string ffmpeg = FfmpegLocator.Find();
        _cts = new CancellationTokenSource();

        var (proc, codecName) = StartVideoEncoder(ffmpeg, Width, Height, Fps, DevBitrateKbps(_settings));
        _video = proc;
        EncoderName = codecName;
        if (!codecName.Equals("libx264", StringComparison.OrdinalIgnoreCase))
            _onFallback?.Invoke("libx264", codecName, "preferred software encoder unavailable");

        var videoOut = proc.Output;
        _workers.Add(Task.Run(() => EncodedStreamPump.PumpH264(videoOut, Fps, onVideo, _cts.Token)));

        try
        {
            _audio = new FfmpegProcess(ffmpeg, AudioArgs());
            Thread.Sleep(150);
            if (_audio.HasExited)
            {
                _audio.Dispose();
                _audio = null;
            }
            else
            {
                var audioProc = _audio;
                _workers.Add(Task.Run(() => EncodedStreamPump.PumpAdts(audioProc.Output, 0, onAudio, _cts.Token)));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("dev recorder: audio encoder unavailable, continuing video-only: " + ex.Message);
            _audio = null;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _video?.Input.Close(); } catch { }
        try { _audio?.Input.Close(); } catch { }
        try { Task.WaitAll(_workers.ToArray(), 1500); } catch { }
        _video?.Dispose();
        _audio?.Dispose();
        _video = null;
        _audio = null;
        _workers.Clear();
    }

    public void Dispose() => Stop();

    internal static (int width, int height, int fps) ResolveGeometry(RecorderSettings _)
    {
        // Synthetic source stays small enough for CI/laptops. Override with env.
        int width = EnvInt("GAMERPLUG_DEV_WIDTH", 640);
        int height = EnvInt("GAMERPLUG_DEV_HEIGHT", 360);
        int fps = EnvInt("GAMERPLUG_DEV_FPS", 30);
        return (width, height, fps);
    }

    private static int DevBitrateKbps(RecorderSettings settings)
        => EnvInt("GAMERPLUG_DEV_BITRATE", Math.Clamp(settings.BitrateKbps <= 0 ? 2000 : Math.Min(settings.BitrateKbps, 4000), 400, 8000));

    private static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) && v > 0 ? v : fallback;

    private static (FfmpegProcess process, string codec) StartVideoEncoder(
        string ffmpeg, int width, int height, int fps, int bitrateKbps)
    {
        var attempts = new (string codec, string[] extra)[]
        {
            ("libx264", new[] { "-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p" }),
            ("libopenh264", new[] { "-pix_fmt", "yuv420p" }),
        };
        Exception? last = null;
        foreach (var (codec, extra) in attempts)
        {
            FfmpegProcess? proc = null;
            try
            {
                proc = new FfmpegProcess(ffmpeg, VideoArgs(width, height, fps, bitrateKbps, codec, extra));
                Thread.Sleep(250);
                if (proc.HasExited)
                {
                    proc.Dispose();
                    continue;
                }
                return (proc, codec);
            }
            catch (Exception ex)
            {
                last = ex;
                proc?.Dispose();
            }
        }
        throw new InvalidOperationException(
            "dev recorder: no H.264 encoder could start (tried libx264, libopenh264)", last);
    }

    private static List<string> VideoArgs(
        int width, int height, int fps, int bitrateKbps, string codec, IReadOnlyList<string> extra)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-re",
            "-f", "lavfi",
            "-i", $"testsrc2=size={width}x{height}:rate={fps}",
            "-an",
            "-c:v", codec,
            "-g", (fps * 2).ToString(),
            "-bf", "0",
            "-b:v", $"{bitrateKbps}k",
        };
        args.AddRange(extra);
        args.AddRange(new[] { "-bsf:v", "dump_extra=freq=keyframe", "-f", "h264", "pipe:1" });
        return args;
    }

    private static string[] AudioArgs() =>
    [
        "-hide_banner", "-loglevel", "error",
        "-re",
        "-f", "lavfi",
        "-i", "sine=frequency=440:sample_rate=48000",
        "-vn",
        "-c:a", "aac",
        "-b:a", "96k",
        "-f", "adts",
        "pipe:1",
    ];
}
