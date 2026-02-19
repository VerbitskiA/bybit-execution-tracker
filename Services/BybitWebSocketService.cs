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

    private ClientWebSocket? _webSocket;
    private int _reconnectAttempts;
    private TaskCompletionSource<bool>? _authTcs;
    private TaskCompletionSource<bool>? _subscribeTcs;

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
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();

        Console.WriteLine("[WS] Connecting...");
        await _webSocket.ConnectAsync(new Uri(_config.WebSocketUrl), token);
        Console.WriteLine("[WS] Connected");

        var receiveTask = ReceiveLoop(token);

        await Authenticate(token);
        await Subscribe(token);

        await receiveTask;
    }

    private async Task Authenticate(CancellationToken token)
    {
        if (_webSocket is not { State: WebSocketState.Open })
            throw new InvalidOperationException("WebSocket is not connected");

        _authTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Send(new
        {
            op = "auth",
            args = new[]
            {
                _config.ApiKey,
                GetExpires(),
                Sign(GetExpires())
            }
        }, token);

        var success = await _authTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), token);

        if (!success)
            throw new Exception("Authentication failed");

        Console.WriteLine("[WS] Authenticated");
    }

    private async Task Subscribe(CancellationToken token)
    {
        if (_webSocket is not { State: WebSocketState.Open })
            throw new InvalidOperationException("WebSocket is not connected");

        _subscribeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Send(new
        {
            op = "subscribe",
            args = new[] { "execution" }
        }, token);

        var success = await _subscribeTcs.Task.WaitAsync(TimeSpan.FromSeconds(3), token);

        if (!success)
            throw new Exception("Subscription failed");

        Console.WriteLine("[WS] Subscribed");
    }

    private async Task ReceiveLoop(CancellationToken token)
    {
        if (_webSocket == null)
            return;

        var buffer = ArrayPool<byte>.Shared.Rent(8192);

        try
        {
            while (_webSocket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                using var ms = new MemoryStream();

                WebSocketReceiveResult result;

                do
                {
                    result = await _webSocket.ReceiveAsync(buffer, token);

                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new Exception("Socket closed");

                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var json = Encoding.UTF8.GetString(ms.ToArray());
                await HandleMessage(json, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WS] Receive error: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleMessage(string json, CancellationToken token)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("op", out var opProp) &&
                root.TryGetProperty("success", out var successProp))
            {
                var op = opProp.GetString();
                var success = successProp.GetBoolean();

                if (op == "auth" && _authTcs != null)
                {
                    _authTcs.TrySetResult(success);
                    return;
                }

                if (op == "subscribe" && _subscribeTcs != null)
                {
                    _subscribeTcs.TrySetResult(success);
                    return;
                }
            }

            if (root.TryGetProperty("op", out var pingOpProp))
            {
                var op = pingOpProp.GetString();

                if (op == "ping")
                {
                    var args = root.TryGetProperty("args", out var argsProp) &&
                               argsProp.ValueKind == JsonValueKind.Array
                        ? argsProp.EnumerateArray().Select(a => a.GetString()).Where(s => s != null).ToArray()
                        : [];

                    await Send(new { op = "pong", args }, token);
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
        catch (JsonException ex)
        {
            Console.WriteLine($"[WS] JSON parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WS] Message handling error: {ex.Message}");
        }
    }

    private static ExecutionEvent? ParseExecution(JsonElement e)
    {
        try
        {
            if (!e.TryGetProperty("execId", out var idProp) || string.IsNullOrEmpty(idProp.GetString()))
            {
                Console.WriteLine("[ParseExecution] Missing or empty execId");
                return null;
            }

            var id = idProp.GetString()!;

            if (!e.TryGetProperty("symbol", out var symbolProp) || string.IsNullOrEmpty(symbolProp.GetString()))
            {
                Console.WriteLine($"[ParseExecution] Missing symbol for execId={id}");
                return null;
            }

            var symbol = symbolProp.GetString()!;

            if (!e.TryGetProperty("side", out var sideProp) || string.IsNullOrEmpty(sideProp.GetString()))
            {
                Console.WriteLine($"[ParseExecution] Missing side for execId={id}");
                return null;
            }

            var side = sideProp.GetString()!;

            if (!e.TryGetProperty("execPrice", out var priceProp) || !decimal.TryParse(priceProp.GetString(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
            {
                Console.WriteLine($"[ParseExecution] Invalid execPrice for execId={id}");
                return null;
            }

            if (!e.TryGetProperty("execQty", out var qtyProp) || !decimal.TryParse(qtyProp.GetString(),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var qty))
            {
                Console.WriteLine($"[ParseExecution] Invalid execQty for execId={id}");
                return null;
            }

            if (!e.TryGetProperty("execTime", out var timeProp) || !long.TryParse(timeProp.GetString(), out var ts))
            {
                Console.WriteLine($"[ParseExecution] Invalid execTime for execId={id}");
                return null;
            }

            var time = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime;

            return new ExecutionEvent(id, symbol, side, price, qty, time);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ParseExecution] Unexpected error: {ex.Message}");
            return null;
        }
    }


    private async Task Send(object payload, CancellationToken token)
    {
        if (_webSocket is not { State: WebSocketState.Open })
            return;

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
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
}