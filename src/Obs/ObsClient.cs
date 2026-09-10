using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClipWatch;

public readonly record struct ObsResponse(bool Ok, int Code, string? Comment, JsonElement Data)
{
    public bool TryGetBool(string property, out bool value)
    {
        value = false;
        if (!Ok || Data.ValueKind != JsonValueKind.Object) return false;
        if (!Data.TryGetProperty(property, out var el)) return false;
        if (el.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = el.GetBoolean();
        return true;
    }
}

public sealed class ObsClient : IDisposable
{
    private const int OpHello = 0;
    private const int OpIdentify = 1;
    private const int OpIdentified = 2;
    private const int OpEvent = 5;
    private const int OpRequest = 6;
    private const int OpRequestResponse = 7;

    private const int EventSubscriptions = 1 | 64;

    private readonly Config _config;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ObsResponse>> _pending = new();

    private ClientWebSocket? _socket;
    private CancellationTokenSource? _loopCts;
    private volatile bool _identified;

    public bool IsConnected => _identified;

    public event Action<bool>? ConnectionChanged;

    public event Action<string, JsonElement>? ObsEvent;

    public event Action<string>? Log;

    public ObsClient(Config config) => _config = config;

    public void Start()
    {
        if (_loopCts != null) return;
        _loopCts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(_loopCts.Token));
    }

    public async Task StopAsync()
    {
        _loopCts?.Cancel();
        var socket = _socket;
        if (socket is { State: WebSocketState.Open })
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutdown", cts.Token);
            }
            catch { }
        }
        SetIdentified(false);
    }

    private async Task RunAsync(CancellationToken token)
    {
        var backoffSeconds = 2;

        while (!token.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                _socket = socket;

                var uri = new Uri($"ws://{_config.ObsHost}:{_config.ObsPort}");
                await socket.ConnectAsync(uri, token);
                Log?.Invoke("Connected to OBS, identifying...");

                await ReceiveLoopAsync(socket, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"OBS connection lost: {ex.Message}");
            }
            finally
            {
                _socket = null;
                SetIdentified(false);
                FailAllPending("connection closed");
            }

            if (token.IsCancellationRequested) break;

            try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), token); }
            catch (OperationCanceledException) { break; }

            backoffSeconds = Math.Min(backoffSeconds + 2, 15);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];

        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log?.Invoke("OBS closed the connection.");
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var text = Encoding.UTF8.GetString(ms.ToArray());
            if (text.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(text);
                await HandleMessageAsync(doc.RootElement.Clone(), socket, token);
            }
            catch (JsonException)
            {
            }
        }
    }

    private async Task HandleMessageAsync(JsonElement root, ClientWebSocket socket, CancellationToken token)
    {
        if (!root.TryGetProperty("op", out var opEl) || !opEl.TryGetInt32(out var op)) return;
        var d = root.TryGetProperty("d", out var dEl) ? dEl : default;

        switch (op)
        {
            case OpHello:
                {
                    var identify = new JsonObject
                    {
                        ["op"] = OpIdentify,
                        ["d"] = new JsonObject
                        {
                            ["rpcVersion"] = 1,
                            ["eventSubscriptions"] = EventSubscriptions
                        }
                    };

                    if (d.ValueKind == JsonValueKind.Object &&
                        d.TryGetProperty("authentication", out var auth) &&
                        auth.ValueKind == JsonValueKind.Object)
                    {
                        var challenge = auth.GetProperty("challenge").GetString() ?? "";
                        var salt = auth.GetProperty("salt").GetString() ?? "";
                        var token2 = BuildAuthToken(_config.ObsPassword, salt, challenge);
                        ((JsonObject)identify["d"]!)["authentication"] = token2;
                    }

                    await SendAsync(socket, identify.ToJsonString(), token);
                    break;
                }

            case OpIdentified:
                SetIdentified(true);
                Log?.Invoke("Identified with OBS.");
                break;

            case OpEvent:
                {
                    if (d.ValueKind != JsonValueKind.Object) break;
                    var type = d.TryGetProperty("eventType", out var t) ? t.GetString() : null;
                    if (type == null) break;
                    var data = d.TryGetProperty("eventData", out var ed) ? ed : default;
                    ObsEvent?.Invoke(type, data);
                    break;
                }

            case OpRequestResponse:
                {
                    if (d.ValueKind != JsonValueKind.Object) break;
                    var id = d.TryGetProperty("requestId", out var idEl) ? idEl.GetString() : null;
                    if (id == null || !_pending.TryRemove(id, out var tcs)) break;

                    var ok = false;
                    var code = 0;
                    string? comment = null;

                    if (d.TryGetProperty("requestStatus", out var status) && status.ValueKind == JsonValueKind.Object)
                    {
                        if (status.TryGetProperty("result", out var r) &&
                            r.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            ok = r.GetBoolean();
                        if (status.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci))
                            code = ci;
                        if (status.TryGetProperty("comment", out var cm) && cm.ValueKind == JsonValueKind.String)
                            comment = cm.GetString();
                    }

                    var payload = d.TryGetProperty("responseData", out var rd) ? rd : default;
                    tcs.TrySetResult(new ObsResponse(ok, code, comment, payload));
                    break;
                }
        }
    }

    public async Task<ObsResponse> RequestAsync(
        string requestType,
        JsonObject? requestData = null,
        int timeoutMs = 5000)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open } || !_identified)
            return new ObsResponse(false, -1, "not connected", default);

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ObsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = new JsonObject
        {
            ["op"] = OpRequest,
            ["d"] = new JsonObject
            {
                ["requestType"] = requestType,
                ["requestId"] = id
            }
        };
        if (requestData != null)
            ((JsonObject)payload["d"]!)["requestData"] = requestData;

        try
        {
            await SendAsync(socket, payload.ToJsonString(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            return new ObsResponse(false, -1, ex.Message, default);
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        if (completed != tcs.Task)
        {
            _pending.TryRemove(id, out _);
            return new ObsResponse(false, -1, "timed out", default);
        }

        return await tcs.Task;
    }

    private async Task SendAsync(ClientWebSocket socket, string json, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(token);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void SetIdentified(bool value)
    {
        if (_identified == value) return;
        _identified = value;
        ConnectionChanged?.Invoke(value);
    }

    private void FailAllPending(string reason)
    {
        foreach (var key in _pending.Keys)
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetResult(new ObsResponse(false, -1, reason, default));
    }

    private static string BuildAuthToken(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    public void Dispose()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _sendLock.Dispose();
    }
}
