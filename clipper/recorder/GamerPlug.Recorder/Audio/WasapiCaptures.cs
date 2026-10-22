using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GamerPlug.Recorder.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, string Kind, bool IsDefault);

/// <summary>WASAPI render-loopback (system mix) or capture (microphone) as PCM s16le.</summary>
public sealed class WasapiPcmCapture : IDisposable
{
    private readonly WasapiCapture _capture;
    private Action<byte[], int>? _onPcm;
    private readonly WaveFormat _target = new(48000, 16, 2);

    public WasapiPcmCapture(string deviceId, bool loopback)
    {
        MMDevice device = Resolve(deviceId, loopback);
        _capture = loopback
            ? new WasapiLoopbackCapture(device)
            : new WasapiCapture(device);
        _capture.DataAvailable += OnData;
    }

    public void Start(Action<byte[], int> onPcm)
    {
        _onPcm = onPcm;
        _capture.StartRecording();
    }

    public void Stop()
    {
        try { _capture.StopRecording(); } catch { /* already stopped */ }
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _onPcm is null) return;
        var source = _capture.WaveFormat;
        if (source.Equals(_target))
        {
            _onPcm(e.Buffer, e.BytesRecorded);
            return;
        }
        // Convert whatever WASAPI gives us (usually IEEE float 32-bit) to s16le stereo 48k.
        using var raw = new RawSourceWaveStream(e.Buffer, 0, e.BytesRecorded, source);
        using var resampled = new MediaFoundationResampler(raw, _target) { ResamplerQuality = 60 };
        var buf = new byte[e.BytesRecorded * 2];
        int read = resampled.Read(buf, 0, buf.Length);
        if (read > 0) _onPcm(buf, read);
    }

    public static List<AudioDeviceInfo> ListDevices()
    {
        var list = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? defaultRender = null;
        MMDevice? defaultCapture = null;
        try { defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); } catch { }
        try { defaultCapture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia); } catch { }

        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, "render", defaultRender != null && d.ID == defaultRender.ID));
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            list.Add(new AudioDeviceInfo(d.ID, d.FriendlyName, "capture", defaultCapture != null && d.ID == defaultCapture.ID));
        return list;
    }

    private static MMDevice Resolve(string deviceId, bool loopback)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (string.IsNullOrEmpty(deviceId) || deviceId == "default")
        {
            return enumerator.GetDefaultAudioEndpoint(
                loopback ? DataFlow.Render : DataFlow.Capture, Role.Multimedia);
        }
        return enumerator.GetDevice(deviceId);
    }
}
