using System.Buffers;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BybitExecutionTracker.Models;

namespace BybitExecutionTracker.Services;

public sealed class BybitWebSocketService
{
    private readonly BybitConfig _config;
    private readonly ChannelWriter<ExecutionEvent> _writer;

    private int _reconnectAttempts;

    public BybitWebSocketService(BybitConfig config, ChannelWriter<ExecutionEvent> writer)
    {
        _config = config;
        _writer = writer;
    }

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ConnectAndProcess(token);
                _reconnectAttempts = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WS] Error: {ex.Message}");
            }

            var delay = CalculateBackoff();
            Console.WriteLine($"[WS] Reconnecting in {delay}ms...");

            await Task.Delay(delay, token);
        }
    }

    private async Task ConnectAndProcess(CancellationToken token)
    {
        using var ws = new ClientWebSocket();

        Console.WriteLine("[WS] Connecting...");
        await ws.ConnectAsync(new Uri(_config.WebSocketUrl), token);
        Console.WriteLine("[WS] Connected");

        await Authenticate(ws, token);
        await Subscribe(ws, token);
        await ReceiveLoop(ws, token);
    }

    private async Task Authenticate(ClientWebSocket ws, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Send(ws, new
        {
            op = "auth",
            args = new[]
            {
                _config.ApiKey,
                GetExpires(),
                Sign(GetExpires())
            }
        }, token);

        _ = WaitForResponse(ws, "auth", tcs, token);

        var success = await tcs.Task.WaitAsync(token);

        if (!success)
            throw new Exception("Authentication failed");

        Console.WriteLine("[WS] Authenticated");
    }

    private static async Task Subscribe(ClientWebSocket ws, CancellationToken token)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Send(ws, new
        {
            op = "subscribe",
            args = new[] { "execution" }
        }, token);

        _ = WaitForResponse(ws, "subscribe", tcs, token);

        var success = await tcs.Task.WaitAsync(token);

        if (!success)
            throw new Exception("Subscription failed");

        Console.WriteLine("[WS] Subscribed");
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);

        try
        {
            while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                using var ms = new MemoryStream();

                WebSocketReceiveResult result;

                do
                {
                    result = await ws.ReceiveAsync(buffer, token);

                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new Exception("Socket closed");

                    ms.Write(buffer, 0, result.Count);

                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(ms.ToArray());
                await HandleMessage(json, ws, token);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleMessage(string json, ClientWebSocket ws, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("op", out var opProp))
        {
            var op = opProp.GetString();

            if (op == "ping")
            {
                var args = root.TryGetProperty("args", out var argsProp) &&
                           argsProp.ValueKind == JsonValueKind.Array
                    ? argsProp.EnumerateArray().Select(a => a.GetString()).Where(s => s != null).ToArray()
                    : [];

                await Send(ws, new { op = "pong", args }, token);
                return;
            }
        }

        if (root.TryGetProperty("topic", out var topicProp) &&
            topicProp.GetString() == "execution" &&
            root.TryGetProperty("data", out var dataProp) &&
            dataProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in dataProp.EnumerateArray())
            {
                var evt = ParseExecution(item);
                if (evt != null)
                    await _writer.WriteAsync(evt.Value, token);
            }
        }
    }


    private static ExecutionEvent? ParseExecution(JsonElement e)
    {
        if (!e.TryGetProperty("execId", out var idProp))
            return null;

        var id = idProp.GetString();
        if (string.IsNullOrEmpty(id))
            return null;

        decimal.TryParse(
            e.GetProperty("execPrice").GetString(),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var price);

        decimal.TryParse(
            e.GetProperty("execQty").GetString(),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var qty);
        
        var execTimeStr = e.GetProperty("execTime").GetString();
        long ts = 0;
        if (!string.IsNullOrEmpty(execTimeStr))
            long.TryParse(execTimeStr, out ts);

        var time = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime;

        return new ExecutionEvent(
            id,
            e.GetProperty("symbol").GetString()!,
            e.GetProperty("side").GetString()!,
            price,
            qty,
            time);
    }


    private static async Task Send(ClientWebSocket ws, object payload, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, token);
    }

    private static string GetExpires()
        => DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds().ToString();

    private string Sign(string expires)
    {
        var message = "GET/realtime" + expires;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_config.ApiSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private int CalculateBackoff()
    {
        _reconnectAttempts++;

        var multiplier = 1 << Math.Min(_reconnectAttempts, 10);
        var delay = _config.ReconnectDelayMs * multiplier;

        return Math.Min(delay, _config.MaxReconnectDelayMs);
    }

    private static async Task WaitForResponse(
        ClientWebSocket ws,
        string expectedOp,
        TaskCompletionSource<bool> tcs,
        CancellationToken token)
    {
        var buffer = new byte[4096];

        var result = await ws.ReceiveAsync(buffer, token);

        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("op", out var opProp) &&
            root.TryGetProperty("success", out var successProp) &&
            opProp.GetString() == expectedOp)
        {
            tcs.TrySetResult(successProp.GetBoolean());
        }
        else
        {
            tcs.TrySetResult(false);
        }
    }
}
