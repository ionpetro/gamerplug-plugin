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
            try
            {
                IFrameSource src = name == "wgc"
                    ? new WgcFrameSource(settings.MonitorIndex, settings.ShowCursor)
                    : new DxgiFrameSource(settings.MonitorIndex);
                actualBackend = name;
                return src;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new CaptureStartException("no capture backend could start", last ?? new InvalidOperationException());
    }
}
