using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GamerPlug.Recorder.Ipc;

/// <summary>
/// JSON-RPC 2.0 client over a localhost WebSocket. Intentionally does not send
/// an Origin header (the desktop server rejects any connection that has one).
/// </summary>
public sealed class JsonRpcClient : IDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly Dictionary<string, Func<JsonNode?, Task<JsonNode?>>> _requests = new();
    private readonly Dictionary<string, Func<JsonNode?, Task>> _notifications = new();
    private readonly object _gate = new();
    private int _nextId = 1;
    private CancellationTokenSource? _cts;
    private Task? _recv;

    public bool IsConnected => _ws.State == WebSocketState.Open;

    public void OnRequest(string method, Func<JsonNode?, Task<JsonNode?>> handler) => _requests[method] = handler;
    public void OnNotification(string method, Func<JsonNode?, Task> handler) => _notifications[method] = handler;

    public async Task ConnectAsync(int port, CancellationToken ct = default)
    {
        // Do not set Origin. ClientWebSocket on .NET does not add one by default.
        await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), ct);
        _cts = new CancellationTokenSource();
        _recv = Task.Run(() => RecvLoop(_cts.Token));
    }

    public async Task NotifyAsync(string method, object? @params)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (@params != null) msg["params"] = JsonSerializer.SerializeToNode(@params);
        await SendAsync(msg);
    }

    public async Task<JsonNode?> RequestAsync(string method, object? @params, int timeoutMs = 10_000)
    {
        int id;
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            id = _nextId++;
            _pending[id] = tcs;
        }
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params != null) msg["params"] = JsonSerializer.SerializeToNode(@params);
        await SendAsync(msg);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (completed != tcs.Task)
        {
            lock (_gate) { _pending.Remove(id); }
            throw new TimeoutException($"request '{method}' timed out");
        }
        return tcs.Task.Result;
    }

    private async Task RecvLoop(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var sb = new MemoryStream();
        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            sb.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                sb.Write(buf, 0, result.Count);
            } while (!result.EndOfMessage);

            JsonNode? node;
            try { node = JsonNode.Parse(sb.ToArray()); }
            catch { continue; }
            if (node is not JsonObject obj) continue;
            _ = Dispatch(obj);
        }
    }

    private async Task Dispatch(JsonObject obj)
    {
        if (obj.ContainsKey("method"))
        {
            string method = obj["method"]!.GetValue<string>();
            var @params = obj["params"];
            if (obj.ContainsKey("id"))
            {
                JsonNode? result = null;
                JsonObject? error = null;
                try
                {
                    if (_requests.TryGetValue(method, out var h))
                        result = await h(@params);
                    else
                        error = new JsonObject { ["code"] = -32601, ["message"] = $"method not found: {method}" };
                }
                catch (Exception ex)
                {
                    error = new JsonObject { ["code"] = -32603, ["message"] = ex.Message };
                }
                var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = obj["id"]!.DeepClone() };
                if (error != null) response["error"] = error;
                else response["result"] = result ?? JsonValue.Create((string?)null);
                await SendAsync(response);
            }
            else if (_notifications.TryGetValue(method, out var n))
            {
                try { await n(@params); } catch { /* log-less */ }
            }
            return;
        }

        if (obj.ContainsKey("id") && obj["id"] is JsonValue idVal)
        {
            int id = idVal.GetValue<int>();
            TaskCompletionSource<JsonNode?>? tcs;
            lock (_gate) { _pending.TryGetValue(id, out tcs); _pending.Remove(id); }
            if (tcs is null) return;
            if (obj.ContainsKey("error"))
                tcs.TrySetException(new InvalidOperationException(obj["error"]!.ToJsonString()));
            else
                tcs.TrySetResult(obj["result"]);
        }
    }

    private async Task SendAsync(JsonObject msg)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _ws.Dispose(); } catch { }
    }
}
