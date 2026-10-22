using System.Text.Json;
using GamerPlug.Recorder.Core;

namespace GamerPlug.Recorder.Core.Ipc;

/// <summary>Maps the desktop's JSON settings object onto <see cref="RecorderSettings"/>.</summary>
public static class SettingsMapper
{
    public static RecorderSettings FromJson(JsonElement root)
    {
        var s = new RecorderSettings();
        if (root.TryGetProperty("capture", out var cap))
        {
            s.CaptureMode = EnumParse(cap, "mode", s.CaptureMode);
            s.MonitorIndex = Int(cap, "monitorIndex", s.MonitorIndex);
            s.Fps = Int(cap, "fps", s.Fps);
            s.BitrateKbps = Int(cap, "bitrateKbps", s.BitrateKbps);
            s.Codec = EnumParse(cap, "codec", s.Codec);
            s.Encoder = EnumParse(cap, "encoder", s.Encoder);
            s.Width = Int(cap, "width", s.Width);
            s.Height = Int(cap, "height", s.Height);
            s.ShowCursor = Bool(cap, "showCursor", s.ShowCursor);
            s.InMemoryBuffer = Bool(cap, "inMemoryBuffer", s.InMemoryBuffer);
        }
        if (root.TryGetProperty("replay", out var replay))
        {
            s.BufferSeconds = Int(replay, "bufferSeconds", s.BufferSeconds);
            s.ClipSeconds = Int(replay, "clipSeconds", s.ClipSeconds);
            s.ClipFolder = Str(replay, "clipFolder", s.ClipFolder);
        }
        if (root.TryGetProperty("audio", out var audio))
        {
            string mode = Str(audio, "mode", "single");
            s.AudioTrackCount = mode == "split" ? 2 : 1;
        }
        return s;
    }

    public static List<(string accelerator, string action)> HotkeysFromJson(JsonElement root)
    {
        var list = new List<(string, string)>();
        if (!root.TryGetProperty("hotkeys", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in arr.EnumerateArray())
        {
            string action = Str(el, "action", "");
            string accel = Str(el, "accelerator", "");
            if (action.Length > 0 && accel.Length > 0) list.Add((accel, action));
        }
        return list;
    }

    private static int Int(JsonElement e, string name, int fallback)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    private static bool Bool(JsonElement e, string name, bool fallback)
        => e.TryGetProperty(name, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False) ? v.GetBoolean() : fallback;

    private static string Str(JsonElement e, string name, string fallback)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static TEnum EnumParse<TEnum>(JsonElement e, string name, TEnum fallback) where TEnum : struct, Enum
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return fallback;
        string raw = v.GetString() ?? "";
        // "h264" -> H264, "nvenc" -> Nvenc, "wgc" -> Wgc
        if (Enum.TryParse(raw, ignoreCase: true, out TEnum parsed)) return parsed;
        return fallback;
    }
}
