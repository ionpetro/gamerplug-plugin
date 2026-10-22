using System.Text.Json;
using System.Text.Json.Nodes;
using GamerPlug.Recorder.Audio;
using GamerPlug.Recorder.Capture;
using GamerPlug.Recorder.Core;
using GamerPlug.Recorder.Core.Engine;
using GamerPlug.Recorder.Core.Mux;
using GamerPlug.Recorder.Input;
using GamerPlug.Recorder.Ipc;
using GamerPlug.Recorder.Pipeline;

namespace GamerPlug.Recorder;

/// <summary>
/// Process host: JSON-RPC methods, capture pipeline lifecycle, hotkeys, and
/// mapping engine events onto UI notifications.
/// </summary>
public sealed class RecorderHost : IRecorderObserver, IDisposable
{
    public const int ProtocolVersion = 1;
    public const string RecorderVersion = "0.1.0";

    private readonly JsonRpcClient _rpc;
    private readonly HotkeyHook _hotkeys = new();
    private readonly object _gate = new();
    private RecorderEngine _engine;
    private CapturePipeline? _pipeline;
    private RecorderSettings _settings = new();
    private bool _hotkeysOn;

    public RecorderHost(JsonRpcClient rpc)
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
        _rpc.OnRequest("startHotkeys", _ =>
        {
            _hotkeysOn = true;
            _hotkeys.Start(action => _engine.ExecuteAction(action, ActionSource.Hotkey));
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        });
        _rpc.OnRequest("stopHotkeys", _ =>
        {
            _hotkeysOn = false;
            _hotkeys.Stop();
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        });
        _rpc.OnRequest("listDisplays", _ =>
        {
            var arr = new JsonArray();
            foreach (var m in MonitorEnumeration.Enumerate())
            {
                arr.Add(new JsonObject
                {
                    ["index"] = m.Index,
                    ["name"] = m.Name,
                    ["width"] = m.Width,
                    ["height"] = m.Height,
                    ["refreshHz"] = m.RefreshHz,
                    ["isPrimary"] = m.IsPrimary,
                });
            }
            return Task.FromResult<JsonNode?>(arr);
        });
        _rpc.OnRequest("listAudioDevices", _ =>
        {
            var arr = new JsonArray();
            foreach (var d in WasapiPcmCapture.ListDevices())
            {
                arr.Add(new JsonObject
                {
                    ["id"] = d.Id,
                    ["name"] = d.Name,
                    ["kind"] = d.Kind,
                    ["isDefault"] = d.IsDefault,
                });
            }
            return Task.FromResult<JsonNode?>(arr);
        });
        _rpc.OnRequest("shutdown", _ =>
        {
            StopCapture();
            _hotkeys.Stop();
            Environment.Exit(0);
            return Task.FromResult<JsonNode?>(new JsonObject { ["ok"] = true });
        });
    }

    private void ApplySettings(JsonNode? node)
    {
        using var doc = JsonDocument.Parse(node?.ToJsonString() ?? "{}");
        var root = doc.RootElement;
        // Settings may be wrapped `{ settings: { ... } }` or be the object itself.
        var obj = root.TryGetProperty("settings", out var inner) ? inner : root;
        lock (_gate)
        {
            _settings = SettingsMapper.FromJson(obj);
            _engine.ApplySettings(_settings);
            _hotkeys.SetBindings(SettingsMapper.HotkeysFromJson(obj));
        }
    }

    private void StartCapture()
    {
        lock (_gate)
        {
            StopCaptureLocked();
            _pipeline = new CapturePipeline(_settings, (from, to, reason) =>
            {
                _ = _rpc.NotifyAsync("fallbackEncoder", new { from, to, reason });
            });
            _pipeline.Start(_engine.OnVideoPacket, _engine.OnAudioPacket);
            _engine.Start();
            _ = _rpc.NotifyAsync("captureStarted", new
            {
                backend = _pipeline.CaptureBackend,
                encoder = _pipeline.EncoderName,
            });
            if (_hotkeysOn)
                _hotkeys.Start(action => _engine.ExecuteAction(action, ActionSource.Hotkey));
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
        });

    public void OnClipFailed(ClipSaveStatus status, string message)
        => _ = _rpc.NotifyAsync("clipFailed", new { reason = status.ToString(), message });

    public void OnLog(string level, string message)
        => _ = _rpc.NotifyAsync("log", new { level, message });

    public void Dispose()
    {
        StopCapture();
        _hotkeys.Dispose();
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
