using GamerPlug.Recorder.Core;

namespace GamerPlug.Recorder.Capture;

/// <summary>
/// Picks a frame source. Auto tries WGC first, then DXGI Desktop Duplication.
/// Never uses game-hook injection (the GPL / Detours path is intentionally
/// omitted — see README "Known gaps").
/// </summary>
public static class CaptureFactory
{
    public static IFrameSource Create(RecorderSettings settings, out string actualBackend)
        => StartWithFallback(settings, _ => { }, out actualBackend, start: false);

    /// <summary>
    /// Construct and start a frame source. Auto tries WGC first and falls back
    /// to DXGI Desktop Duplication if <see cref="IFrameSource.Start"/> throws.
    /// Constructors never fail, so fallback has to happen at Start.
    /// </summary>
    public static IFrameSource StartWithFallback(
        RecorderSettings settings,
        Action<CapturedFrame> onFrame,
        out string actualBackend)
        => StartWithFallback(settings, onFrame, out actualBackend, start: true);

    private static IFrameSource StartWithFallback(
        RecorderSettings settings,
        Action<CapturedFrame> onFrame,
        out string actualBackend,
        bool start)
    {
        var order = settings.CaptureMode switch
        {
            CaptureMode.Wgc => new[] { "wgc" },
            CaptureMode.Dxgi => new[] { "dxgi" },
            _ => new[] { "wgc", "dxgi" },
        };

        Exception? last = null;
        foreach (var name in order)
        {
            IFrameSource? src = null;
            try
            {
                src = name == "wgc"
                    ? new WgcFrameSource(settings.MonitorIndex, settings.ShowCursor)
                    : new DxgiFrameSource(settings.MonitorIndex);
                if (start) src.Start(onFrame);
                actualBackend = name;
                return src;
            }
            catch (Exception ex)
            {
                last = ex;
                try { src?.Dispose(); } catch { /* ignore */ }
            }
        }
        throw new CaptureStartException("no capture backend could start", last ?? new InvalidOperationException());
    }
}
