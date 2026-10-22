using System.Diagnostics;

namespace GamerPlug.Recorder.Core.Encode;

/// <summary>Locates an FFmpeg binary. Override with GAMERPLUG_FFMPEG.</summary>
public static class FfmpegLocator
{
    public static string Find()
    {
        string? env = Environment.GetEnvironmentVariable("GAMERPLUG_FFMPEG");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        string winName = "ffmpeg.exe";
        string unixName = "ffmpeg";
        string besideWin = Path.Combine(AppContext.BaseDirectory, winName);
        if (File.Exists(besideWin)) return besideWin;
        string besideUnix = Path.Combine(AppContext.BaseDirectory, unixName);
        if (File.Exists(besideUnix)) return besideUnix;

        string? path = FindOnPath(unixName) ?? FindOnPath(winName);
        if (path != null) return path;

        throw new FileNotFoundException(
            "ffmpeg not found. Place an LGPL ffmpeg next to the recorder, add it to PATH, or set GAMERPLUG_FFMPEG.");
    }

    public static bool TryFind(out string path)
    {
        try
        {
            path = Find();
            return true;
        }
        catch
        {
            path = "";
            return false;
        }
    }

    private static string? FindOnPath(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var p in paths)
        {
            if (string.IsNullOrEmpty(p)) continue;
            string candidate = Path.Combine(p, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>
/// One ffmpeg child process with optional stdin (raw frames/PCM) and stdout
/// (encoded elementary stream). stderr is drained so the pipe never blocks.
/// </summary>
public sealed class FfmpegProcess : IDisposable
{
    private readonly Process _proc;
    public Stream Input => _proc.StandardInput.BaseStream;
    public Stream Output => _proc.StandardOutput.BaseStream;
    public int Id => _proc.Id;

    public FfmpegProcess(string ffmpegPath, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start ffmpeg");
        _ = DrainStderr();
    }

    private async Task DrainStderr()
    {
        try
        {
            while (await _proc.StandardError.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                Debug.WriteLine("[ffmpeg] " + line);
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("[ffmpeg] " + line);
                }
            }
        }
        catch { /* process exited */ }
    }

    public bool HasExited => _proc.HasExited;

    public void Dispose()
    {
        try
        {
            if (!_proc.HasExited)
            {
                try { _proc.StandardInput.Close(); } catch { }
                if (!_proc.WaitForExit(1500))
                {
                    _proc.Kill(entireProcessTree: true);
                    _proc.WaitForExit(1000);
                }
            }
        }
        catch { /* best effort */ }
        _proc.Dispose();
    }
}

/// <summary>
/// Walks <see cref="EncoderRecipe.FallbackChain"/> until ffmpeg starts and stays
/// alive for a short grace period. Returns the attempt that won so the host can
/// fire <c>fallbackEncoder</c> if it wasn't the first choice.
/// </summary>
public static class FfmpegEncoderStarter
{
    public static (FfmpegProcess process, EncoderAttempt attempt) StartVideo(
        EncoderBackend backend,
        VideoCodec codec,
        int width,
        int height,
        int fps,
        int bitrateKbps)
    {
        string path = FfmpegLocator.Find();
        var chain = EncoderRecipe.FallbackChain(backend, codec);
        Exception? last = null;
        foreach (var attempt in chain)
        {
            var args = EncoderRecipe.BuildVideoArgs(attempt, width, height, fps, bitrateKbps);
            FfmpegProcess? proc = null;
            try
            {
                proc = new FfmpegProcess(path, args);
                Thread.Sleep(200);
                if (proc.HasExited) { proc.Dispose(); continue; }
                return (proc, attempt);
            }
            catch (Exception ex)
            {
                last = ex;
                proc?.Dispose();
            }
        }
        throw new InvalidOperationException("no ffmpeg video encoder could start", last);
    }

    public static FfmpegProcess StartAudio()
    {
        string path = FfmpegLocator.Find();
        return new FfmpegProcess(path, EncoderRecipe.BuildAudioArgs());
    }
}
