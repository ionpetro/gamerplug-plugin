using System.Text.Json;
using System.Text.Json.Nodes;
using GamerPlug.Recorder.Core;
using GamerPlug.Recorder.Core.Engine;
using GamerPlug.Recorder.Core.Ipc;
using GamerPlug.Recorder.Core.Mux;

namespace GamerPlug.Recorder.Dev;

/// <summary>
/// JSON-RPC host identical in contract to the Windows <c>RecorderHost</c>, but
/// capture is <see cref="TestSrcCapture"/> feeding the real Core ring buffer.
/// OS-level hotkeys are owned by the Electron shell on macOS/Linux.
/// </summary>
public sealed class DevRecorderHost : IRecorderObserver, IDisposable
{
    public const int ProtocolVersion = 1;
    public const string RecorderVersion = "0.1.0-dev";

    private readonly JsonRpcClient _rpc;
    private readonly object _gate = new();
    private RecorderEngine _engine;
    private TestSrcCapture? _pipeline;
    private RecorderSettings _settings = new();

    public DevRecorderHost(JsonRpcClient rpc)
    {
        _rpc = rpc;
        _engine = new RecorderEngine(_settings, new Mp4ClipMuxer(), this);
        BindRpc();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await _rpc.RequestAsync("handshake", new
        {
            recorderVersion = RecorderVersion,
            protocolVersion = ProtocolVersion,
            pid = Environment.ProcessId,
        });
        await _rpc.NotifyAsync("ready", new { state = "idle" });
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
    }

    private void BindRpc()
    {
        _rpc.OnRequest("ping", _ => Task.FromResult<JsonNode?>(JsonValue.Create("pong")));
        _rpc.OnRequest("applySettings", p =>
        {
            ApplySettings(p);
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        });
        _rpc.OnRequest("startCapture", _ =>
        {
            StartCapture();
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        });
        _rpc.OnRequest("stopCapture", _ =>
        {
            StopCapture();
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        });
        _rpc.OnRequest("executeAction", p =>
        {
            string action = p?["action"]?.GetValue<string>() ?? "";
            string source = p?["source"]?.GetValue<string>() ?? "ui";
            var parsed = Enum.TryParse<ActionSource>(source, true, out var src) ? src : ActionSource.Ui;
            var result = _engine.ExecuteAction(action, parsed);
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["accepted"] = result.Accepted,
                ["reason"] = result.Accepted ? null : ToReason(result.Reason),
            });
        });
        _rpc.OnRequest("startHotkeys", _p =>
        {
            // Global hotkeys on macOS/Linux are registered by the Electron shell.
            _ = _rpc.NotifyAsync("log", new { level = "info", message = "dev recorder: hotkeys owned by Electron shell" });
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        });
        _rpc.OnRequest("stopHotkeys", _ =>
            Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true }));
        _rpc.OnRequest("listDisplays", _ =>
        {
            var (w, h, fps) = TestSrcCapture.ResolveGeometry(_settings);
            var arr = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["name"] = "testsrc2",
                    ["width"] = w,
                    ["height"] = h,
                    ["refreshHz"] = fps,
                    ["isPrimary"] = true,
                },
            };
            return Task.FromResult<JsonNode?>(arr);
        });
        _rpc.OnRequest("listAudioDevices", _ =>
        {
            var arr = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "sine",
                    ["name"] = "ffmpeg sine (dev)",
                    ["kind"] = "render",
                    ["isDefault"] = true,
                },
            };
            return Task.FromResult<JsonNode?>(arr);
        });
        _rpc.OnRequest("shutdown", _p =>
        {
            StopCapture();
            _ = Task.Run(() => Environment.Exit(0));
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        });
    }

    private void ApplySettings(JsonNode? node)
    {
        using var doc = JsonDocument.Parse(node?.ToJsonString() ?? "{}");
        var root = doc.RootElement;
        var obj = root.TryGetProperty("settings", out var inner) ? inner : root;
        lock (_gate)
        {
            _settings = SettingsMapper.FromJson(obj);
            _engine.ApplySettings(_settings);
        }
    }

    private void StartCapture()
    {
        lock (_gate)
        {
            StopCaptureLocked();
            var (w, h, fps) = TestSrcCapture.ResolveGeometry(_settings);
            _settings.Width = w;
            _settings.Height = h;
            _settings.Fps = fps;
            _pipeline = new TestSrcCapture(_settings, (from, to, reason) =>
            {
                _ = _rpc.NotifyAsync("fallbackEncoder", new { from, to, reason });
            });
            _pipeline.Start(p => _engine.OnVideoPacket(p), p => _engine.OnAudioPacket(p));
            _engine.Start();
            _ = _rpc.NotifyAsync("captureStarted", new
            {
                backend = _pipeline.CaptureBackend,
                encoder = _pipeline.EncoderName,
            });
            _ = _rpc.NotifyAsync("log", new
            {
                level = "info",
                message = $"dev recorder capturing testsrc2 {_pipeline.Width}x{_pipeline.Height}@{_pipeline.Fps} via {_pipeline.EncoderName}",
            });
        }
    }

    private void StopCapture()
    {
        lock (_gate) StopCaptureLocked();
        _ = _rpc.NotifyAsync("captureStopped", new { });
    }

    private void StopCaptureLocked()
    {
        _pipeline?.Stop();
        _pipeline?.Dispose();
        _pipeline = null;
        _engine.Stop();
    }

    public void OnStateChanged(RecorderState state)
        => _ = _rpc.NotifyAsync("state", new { state = state.ToString().ToLowerInvariant() });

    public void OnClipSaved(SavedClipInfo info)
        => _ = _rpc.NotifyAsync("clipSaved", new
        {
            id = info.Id,
            filePath = info.FilePath,
            durationSec = info.DurationUs / 1_000_000.0,
            width = info.Width,
            height = info.Height,
            fileSizeBytes = info.SizeBytes,
            fileType = "mp4",
            savedAt = info.SavedAtUnixMs,
            saveType = info.SaveType,
            gameTitle = "testsrc2",
        });

    public void OnClipFailed(ClipSaveStatus status, string message)
        => _ = _rpc.NotifyAsync("clipFailed", new { reason = status.ToString(), message });

    public void OnLog(string level, string message)
        => _ = _rpc.NotifyAsync("log", new { level, message });

    public void Dispose()
    {
        StopCapture();
        _rpc.Dispose();
    }

    private static string ToReason(ActionRejectReason r) => r switch
    {
        ActionRejectReason.UnknownAction => "unknownAction",
        ActionRejectReason.NotRecording => "notRecording",
        ActionRejectReason.ClipsDisabled => "clipsDisabled",
        ActionRejectReason.NoSession => "noSession",
        _ => "unknownAction",
    };
}
