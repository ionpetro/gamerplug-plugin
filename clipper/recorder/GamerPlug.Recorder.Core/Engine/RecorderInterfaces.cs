namespace GamerPlug.Recorder.Core.Engine;

/// <summary>Lifecycle state of the recorder, reported to the desktop app.</summary>
public enum RecorderState
{
    Starting,
    Idle,
    Capturing,
    Saving,
    Error,
    Stopping,
}

public enum ActionSource
{
    Hotkey,
    Ui,
    Api,
}

/// <summary>Why an <c>executeAction</c> was rejected (mirrors Medal 1001-1004).</summary>
public enum ActionRejectReason
{
    None,
    UnknownAction,
    NotRecording,
    ClipsDisabled,
    NoSession,
}

public readonly record struct ActionResult(bool Accepted, ActionRejectReason Reason)
{
    public static ActionResult Ok() => new(true, ActionRejectReason.None);
    public static ActionResult Rejected(ActionRejectReason reason) => new(false, reason);
}

/// <summary>Hints a muxer needs to write a correct MP4 container.</summary>
public sealed record MuxHints(int Width, int Height, int Fps, string VideoCodec, IReadOnlyList<string> AudioCodecs);

/// <summary>A clip file produced by remuxing a <see cref="ClipExtract"/>.</summary>
public sealed record ClipFile(string Path, long DurationUs, int Width, int Height, long SizeBytes);

/// <summary>
/// Remuxes extracted encoded packets into an MP4 (no re-encode). Implemented on
/// Windows with FFmpeg (LGPL); a fake implementation drives the engine tests.
/// </summary>
public interface IClipMuxer
{
    ClipFile WriteClip(ClipExtract extract, string outputPath, MuxHints hints);
}

public sealed record SavedClipInfo(
    string Id,
    string FilePath,
    long DurationUs,
    int Width,
    int Height,
    long SizeBytes,
    string SaveType,
    long SavedAtUnixMs);

/// <summary>
/// Receives engine events. The Windows transport maps these onto the JSON-RPC
/// notifications the desktop app listens for (clipSaved, clipFailed, state).
/// </summary>
public interface IRecorderObserver
{
    void OnStateChanged(RecorderState state);
    void OnClipSaved(SavedClipInfo info);
    void OnClipFailed(ClipSaveStatus status, string message);
    void OnLog(string level, string message);
}

/// <summary>No-op observer for tests/headless runs.</summary>
public sealed class NullRecorderObserver : IRecorderObserver
{
    public static readonly NullRecorderObserver Instance = new();
    public void OnStateChanged(RecorderState state) { }
    public void OnClipSaved(SavedClipInfo info) { }
    public void OnClipFailed(ClipSaveStatus status, string message) { }
    public void OnLog(string level, string message) { }
}

/// <summary>
/// A video/audio capture+encode source. The Windows implementations
/// (WGC/DXGI -> NVENC/AMF/QSV/FFmpeg) push encoded packets to the engine via the
/// supplied callbacks; the engine never touches platform APIs.
/// </summary>
public interface ICaptureSource : IDisposable
{
    void Start(Action<MediaPacket> onVideo, Action<MediaPacket> onAudio);
    void Stop();
}
