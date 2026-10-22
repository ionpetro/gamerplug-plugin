using System.Diagnostics;

namespace GamerPlug.Recorder.Core.Tests;

/// <summary>
/// Thin wrapper around ffmpeg/ffprobe for the muxer's validation tests. When the
/// tools are not on PATH (e.g. a bare CI image) the ffprobe-backed tests skip
/// themselves rather than fail.
/// </summary>
internal static class FfTools
{
    public static bool Available => Which("ffmpeg") != null && Which("ffprobe") != null;

    public static string? Which(string tool)
    {
        string exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator);
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p)) continue;
            string candidate = Path.Combine(p, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static (int exitCode, string stdout, string stderr) Run(string tool, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tool,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Parse ffprobe `-show_packets` JSON into packets with file position, size
    /// and keyframe flag, so the raw bytes of each packet can be read back out of
    /// the elementary stream. (Raw elementary streams carry no timestamps, so the
    /// caller assigns pts by frame index.)
    /// </summary>
    public readonly record struct ProbedPacket(long Pos, int Size, bool Key);

    public static List<ProbedPacket> ProbePackets(string ffprobe, string file)
    {
        var (_, stdout, _) = Run(
            ffprobe,
            "-hide_banner", "-loglevel", "error",
            "-select_streams", "0",
            "-show_entries", "packet=flags,size,pos",
            "-of", "json",
            file);
        return ParsePacketsJson(stdout);
    }

    /// <summary>
    /// ffprobe JSON sometimes emits pos/size as strings ("123") and sometimes as
    /// numbers. Accept both so packet slicing stays aligned with the file bytes.
    /// </summary>
    public static List<ProbedPacket> ParsePacketsJson(string stdout)
    {
        var packets = new List<ProbedPacket>();
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        if (!doc.RootElement.TryGetProperty("packets", out var arr)) return packets;
        foreach (var el in arr.EnumerateArray())
        {
            long pos = ReadInt64(el, "pos");
            int size = (int)ReadInt64(el, "size");
            bool key = el.TryGetProperty("flags", out var flagsEl)
                       && flagsEl.ValueKind == System.Text.Json.JsonValueKind.String
                       && (flagsEl.GetString()?.Contains('K') ?? false);
            if (size > 0 && pos >= 0) packets.Add(new ProbedPacket(pos, size, key));
        }
        return packets;
    }

    private static long ReadInt64(System.Text.Json.JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return -1;
        return v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            System.Text.Json.JsonValueKind.String when long.TryParse(v.GetString(), out var s) => s,
            _ => -1,
        };
    }
}
