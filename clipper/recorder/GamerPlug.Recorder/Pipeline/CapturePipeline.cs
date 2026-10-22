using GamerPlug.Recorder.Audio;
using GamerPlug.Recorder.Capture;
using GamerPlug.Recorder.Core;
using GamerPlug.Recorder.Core.Encode;
using GamerPlug.Recorder.Core.Engine;
using GamerPlug.Recorder.Core.Mux;
using GamerPlug.Recorder.Encode;

namespace GamerPlug.Recorder.Pipeline;

/// <summary>
/// Wires a frame source + WASAPI into ffmpeg encoders and pushes encoded packets
/// into <see cref="RecorderEngine"/>. This is the Windows-only half of
/// <see cref="ICaptureSource"/>.
/// </summary>
public sealed class CapturePipeline : ICaptureSource
{
    private readonly RecorderSettings _settings;
    private readonly Action<string, string, string>? _onFallbackEncoder; // from, to, reason
    private IFrameSource? _frames;
    private WasapiPcmCapture? _systemAudio;
    private WasapiPcmCapture? _mic;
    private FfmpegProcess? _videoEnc;
    private FfmpegProcess? _audioEnc;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _workers = new();
    private EncoderAttempt? _videoAttempt;

    public string CaptureBackend { get; private set; } = "none";
    public string EncoderName => _videoAttempt?.FfmpegVideoCodec ?? "none";

    public CapturePipeline(RecorderSettings settings, Action<string, string, string>? onFallbackEncoder = null)
    {
        _settings = settings;
        _onFallbackEncoder = onFallbackEncoder;
    }

    public void Start(Action<MediaPacket> onVideo, Action<MediaPacket> onAudio)
    {
        _cts = new CancellationTokenSource();
        _frames = CaptureFactory.Create(_settings, out var backend);
        CaptureBackend = backend;

        // Probe a first frame so we know the actual size (WGC item size can
        // differ from the settings width/height).
        _frames.Start(frame => WriteVideoFrame(frame));

        int w = _frames.Width > 0 ? _frames.Width : _settings.Width;
        int h = _frames.Height > 0 ? _frames.Height : _settings.Height;
        var (proc, attempt) = FfmpegEncoderStarter.StartVideo(
            _settings.Encoder, _settings.Codec, w, h, _settings.Fps, _settings.BitrateKbps);
        _videoEnc = proc;
        _videoAttempt = attempt;
        if (attempt.Backend != _settings.Encoder && _settings.Encoder != EncoderBackend.Auto)
            _onFallbackEncoder?.Invoke(_settings.Encoder.ToString(), attempt.FfmpegVideoCodec, "preferred encoder failed to start");
        else if (_settings.Encoder == EncoderBackend.Auto && attempt.Backend != EncoderBackend.Nvenc)
            _onFallbackEncoder?.Invoke("auto", attempt.FfmpegVideoCodec, "earlier hardware encoder unavailable");

        _workers.Add(Task.Run(() => ReadVideo(onVideo, _cts.Token)));

        if (_settings.AudioTrackCount >= 1)
        {
            try
            {
                _systemAudio = new WasapiPcmCapture("default", loopback: true);
                _audioEnc = FfmpegEncoderStarter.StartAudio();
                _systemAudio.Start((buf, len) =>
                {
                    try { _audioEnc.Input.Write(buf, 0, len); } catch { /* encoder gone */ }
                });
                _workers.Add(Task.Run(() => ReadAudio(onAudio, 0, _cts.Token)));
            }
            catch
            {
                // Audio is optional; video-only capture still works.
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _frames?.Stop();
        _systemAudio?.Stop();
        _mic?.Stop();
        try { _videoEnc?.Input.Close(); } catch { }
        try { _audioEnc?.Input.Close(); } catch { }
        try { Task.WaitAll(_workers.ToArray(), 1500); } catch { }
        _videoEnc?.Dispose();
        _audioEnc?.Dispose();
        _systemAudio?.Dispose();
        _mic?.Dispose();
        _frames?.Dispose();
    }

    public void Dispose() => Stop();

    private void WriteVideoFrame(CapturedFrame frame)
    {
        if (_videoEnc is null) return;
        try { _videoEnc.Input.Write(frame.Bgra, 0, frame.Bgra.Length); }
        catch { /* encoder restart is the host's job */ }
    }

    private void ReadVideo(Action<MediaPacket> onVideo, CancellationToken ct)
    {
        var splitter = new AnnexBAccessUnitSplitter();
        var buf = new byte[64 * 1024];
        long pts = 0;
        long dur = 1_000_000 / Math.Max(1, _settings.Fps);
        while (!ct.IsCancellationRequested && _videoEnc is not null)
        {
            int n;
            try { n = _videoEnc.Output.Read(buf, 0, buf.Length); }
            catch { break; }
            if (n <= 0) break;
            foreach (var au in splitter.Push(buf.AsSpan(0, n)))
            {
                bool key = ContainsIdr(au);
                onVideo(new MediaPacket(StreamKind.Video, 0, pts, pts, dur, key, au));
                pts += dur;
            }
        }
        foreach (var au in splitter.Flush())
        {
            onVideo(new MediaPacket(StreamKind.Video, 0, pts, pts, dur, ContainsIdr(au), au));
            pts += dur;
        }
    }

    private void ReadAudio(Action<MediaPacket> onAudio, int track, CancellationToken ct)
    {
        var splitter = new AdtsFrameSplitter();
        var buf = new byte[16 * 1024];
        long pts = 0;
        const long dur = 21_333;
        while (!ct.IsCancellationRequested && _audioEnc is not null)
        {
            int n;
            try { n = _audioEnc.Output.Read(buf, 0, buf.Length); }
            catch { break; }
            if (n <= 0) break;
            foreach (var frame in splitter.Push(buf.AsSpan(0, n)))
            {
                onAudio(new MediaPacket(StreamKind.Audio, track, pts, pts, dur, false, frame));
                pts += dur;
            }
        }
    }

    private static bool ContainsIdr(byte[] au)
    {
        foreach (var nal in AnnexB.SplitNalUnits(au))
            if (AnnexB.NalType(nal.Span) == AnnexB.NalIdr) return true;
        return false;
    }
}
