namespace GamerPlug.Recorder.Core;

/// <summary>
/// The recorder-relevant settings the desktop app pushes over IPC
/// (<c>applySettings</c>). A subset of the desktop's full Settings object
/// (packages/shared/settings.ts).
/// </summary>
public sealed class RecorderSettings
{
    public CaptureMode CaptureMode { get; set; } = CaptureMode.Auto;
    public int MonitorIndex { get; set; }
    public int Fps { get; set; } = 60;
    public int BitrateKbps { get; set; } = 30_000;
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public EncoderBackend Encoder { get; set; } = EncoderBackend.Auto;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public bool ShowCursor { get; set; } = true;

    /// <summary>In-RAM ring vs disk-backed (crash-recoverable) ring.</summary>
    public bool InMemoryBuffer { get; set; } = true;

    /// <summary>Rolling retention in seconds; the max any single save can produce.</summary>
    public int BufferSeconds { get; set; } = 120;

    /// <summary>Default clip length produced by the plain <c>clip</c> hotkey.</summary>
    public int ClipSeconds { get; set; } = 30;

    public string ClipFolder { get; set; } = string.Empty;

    /// <summary>Number of audio tracks (1 = mixed, &gt;1 = split by source).</summary>
    public int AudioTrackCount { get; set; } = 1;

    public long BufferRetentionUs => (long)BufferSeconds * 1_000_000;
}

public enum CaptureMode { Auto, Wgc, Dxgi }

public enum VideoCodec { H264, Hevc, Av1 }

public enum EncoderBackend { Auto, Nvenc, Amf, Qsv, Software }
