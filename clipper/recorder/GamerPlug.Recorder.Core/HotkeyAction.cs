using System.Globalization;

namespace GamerPlug.Recorder.Core;

public enum HotkeyActionKind
{
    Clip,
    SegmentToggle,
    Bookmark,
    Screenshot,
}

/// <summary>
/// Parsed hotkey action. The wire grammar matches the desktop side
/// (packages/shared/hotkeys.ts) and Medal's action strings (REPORT.md §1):
/// <c>clip;&lt;seconds&gt;</c>, <c>segment_toggle</c>, <c>bookmark</c>,
/// <c>screenshot</c>.
/// </summary>
public readonly record struct HotkeyAction(HotkeyActionKind Kind, double Seconds)
{
    public static bool TryParse(string raw, out HotkeyAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var parts = raw.Trim().Split(';');
        switch (parts[0])
        {
            case "clip":
                if (parts.Length < 2) return false;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)
                    || secs <= 0 || double.IsNaN(secs) || double.IsInfinity(secs))
                    return false;
                action = new HotkeyAction(HotkeyActionKind.Clip, secs);
                return true;
            case "segment_toggle":
                action = new HotkeyAction(HotkeyActionKind.SegmentToggle, 0);
                return true;
            case "bookmark":
                action = new HotkeyAction(HotkeyActionKind.Bookmark, 0);
                return true;
            case "screenshot":
                action = new HotkeyAction(HotkeyActionKind.Screenshot, 0);
                return true;
            default:
                return false;
        }
    }

    public override string ToString() => Kind switch
    {
        HotkeyActionKind.Clip => $"clip;{Seconds.ToString(CultureInfo.InvariantCulture)}",
        HotkeyActionKind.SegmentToggle => "segment_toggle",
        HotkeyActionKind.Bookmark => "bookmark",
        HotkeyActionKind.Screenshot => "screenshot",
        _ => "unknown",
    };
}
