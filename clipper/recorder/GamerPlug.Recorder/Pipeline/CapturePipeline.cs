using GamerPlug.Recorder.Audio;
using GamerPlug.Recorder.Capture;
using GamerPlug.Recorder.Core;
using GamerPlug.Recorder.Core.Encode;
using GamerPlug.Recorder.Core.Engine;
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
        _frames = CaptureFactory.StartWithFallback(_settings, WriteVideoFrame, out var backend);
        CaptureBackend = backend;

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

        var videoOut = proc.Output;
        _workers.Add(Task.Run(() => EncodedStreamPump.PumpH264(videoOut, _settings.Fps, onVideo, _cts.Token)));

        if (_settings.AudioTrackCount >= 1)
        {
            try
            {
                _systemAudio = new WasapiPcmCapture("default", loopback: true);
                var audioEnc = FfmpegEncoderStarter.StartAudio();
                _audioEnc = audioEnc;
                _systemAudio.Start((buf, len) =>
                {
                    try { audioEnc.Input.Write(buf, 0, len); } catch { /* encoder gone */ }
                });
                _workers.Add(Task.Run(() => EncodedStreamPump.PumpAdts(audioEnc.Output, 0, onAudio, _cts.Token)));
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
        try { _videoEnc?.Input.Close(); } catch { }
        try { _audioEnc?.Input.Close(); } catch { }
        try { Task.WaitAll(_workers.ToArray(), 1500); } catch { }
        _videoEnc?.Dispose();
        _audioEnc?.Dispose();
        _systemAudio?.Dispose();
        _frames?.Dispose();
    }

    public void Dispose() => Stop();

    private void WriteVideoFrame(CapturedFrame frame)
    {
        if (_videoEnc is null) return;
        try { _videoEnc.Input.Write(frame.Bgra, 0, frame.Bgra.Length); }
        catch { /* encoder restart is the host's job */ }
    }
}
