using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BybitExecutionTracker.Models;

namespace BybitExecutionTracker.Services;

public class BybitWebSocketService
{
    private readonly BybitConfig _config;
    private readonly ChannelWriter<ExecutionEvent> _eventWriter;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _receiveTask;
    private Task? _heartbeatTask;
    private bool _isAuthenticated;
    private bool _isSubscribed;
    private readonly object _lockObject = new object();
    
    private int _reconnectAttempts = 0;
    private const int MaxReconnectAttempts = 10;

    public BybitWebSocketService(BybitConfig config, ChannelWriter<ExecutionEvent> eventWriter)
    {
        _config = config;
        _eventWriter = eventWriter;
    }

    public async Task Connect(CancellationToken cancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        while (!_cancellationTokenSource.Token.IsCancellationRequested)
        {
            try
            {
                await ConnectInternal(_cancellationTokenSource.Token);
                
                _heartbeatTask = Task.Run(() => HeartbeatLoop(_cancellationTokenSource.Token));
                
                if (_receiveTask != null)
                {
                    await _receiveTask;
                }
                
                Console.WriteLine("[WebSocket] Connection lost, attempting reconnect...");
                
                lock (_lockObject)
                {
                    _isAuthenticated = false;
                    _isSubscribed = false;
                }
                
                if (_reconnectAttempts < MaxReconnectAttempts)
                {
                    var delay = CalculateReconnectDelay();
                    Console.WriteLine($"[WebSocket] Waiting {delay}ms before reconnect (attempt {_reconnectAttempts + 1}/{MaxReconnectAttempts})");
                    await Task.Delay(delay, _cancellationTokenSource.Token);
                    _reconnectAttempts++;
                }
                else
                {
                    Console.WriteLine("[WebSocket] Max reconnect attempts reached");
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Error in connection loop: {ex.Message}");
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;
                    
                var delay = CalculateReconnectDelay();
                await Task.Delay(delay, _cancellationTokenSource.Token);
                _reconnectAttempts++;
            }
        }
    }

    private async Task ConnectInternal(CancellationToken cancellationToken)
    {
        _webSocket?.Dispose();
        _webSocket = new ClientWebSocket();
        
        Console.WriteLine($"[WebSocket] Connecting to {_config.WebSocketUrl}...");
        await _webSocket.ConnectAsync(new Uri(_config.WebSocketUrl), cancellationToken);
        Console.WriteLine("[WebSocket] Connected");
        
        _reconnectAttempts = 0;
        
        _receiveTask = Task.Run(() => ReceiveMessages(cancellationToken));
        
        await Task.Delay(500, cancellationToken);
        
        await Authenticate(cancellationToken);
        
        await SubscribeToExecutions(cancellationToken);
    }

    private async Task Authenticate(CancellationToken cancellationToken)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            return;

        var expires = DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds();
        var signature = GenerateSignature(expires.ToString());
        
        var authMessage = new
        {
            op = "auth",
            args = new[] { _config.ApiKey, expires.ToString(), signature }
        };

        var json = JsonSerializer.Serialize(authMessage);
        var bytes = Encoding.UTF8.GetBytes(json);
        
        await _webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken);

        Console.WriteLine("[WebSocket] Authentication request sent");
        
        var timeout = Task.Delay(5000, cancellationToken);
        var authCompleted = Task.Run(async () =>
        {
            while (!_isAuthenticated && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
            }
        }, cancellationToken);
        
        await Task.WhenAny(authCompleted, timeout);
        
        if (!_isAuthenticated)
        {
            throw new Exception("Authentication timeout - server didn't respond");
        }
    }

    private async Task SubscribeToExecutions(CancellationToken cancellationToken)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            return;

        var subscribeMessage = new
        {
            op = "subscribe",
            args = new[] { "execution" }
        };

        var json = JsonSerializer.Serialize(subscribeMessage);
        var bytes = Encoding.UTF8.GetBytes(json);
        
        await _webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            true,
            cancellationToken);

        Console.WriteLine("[WebSocket] Subscription request sent");
        
        var timeout = Task.Delay(3000, cancellationToken);
        var subCompleted = Task.Run(async () =>
        {
            while (!_isSubscribed && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken);
            }
        }, cancellationToken);
        
        await Task.WhenAny(subCompleted, timeout);
    }

    private async Task ReceiveMessages(CancellationToken cancellationToken)
    {
        if (_webSocket == null)
            return;

        var buffer = new byte[8192];
        
        while (_webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("[WebSocket] Received close message");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await ProcessMessage(message);
                }
            }
            catch (WebSocketException ex)
            {
                Console.WriteLine($"[WebSocket] WebSocket error: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Error receiving message: {ex.Message}");
            }
        }
    }

    private async Task ProcessMessage(string messageJson)
    {
        try
        {
            Console.WriteLine($"[WebSocket] Received: {messageJson}");
            
            using var doc = JsonDocument.Parse(messageJson);
            var root = doc.RootElement;

            var op = root.TryGetProperty("op", out var opProp) 
                ? opProp.GetString() 
                : "";

            if (root.TryGetProperty("success", out var success))
            {
                var isSuccess = success.GetBoolean();
                var retMsg = root.TryGetProperty("ret_msg", out var retMsgProp) 
                    ? retMsgProp.GetString() 
                    : "";

                if (op == "auth")
                {
                    lock (_lockObject)
                    {
                        _isAuthenticated = isSuccess;
                    }
                    if (isSuccess)
                        Console.WriteLine("[WebSocket] Authentication successful");
                    else
                        Console.WriteLine($"[WebSocket] Authentication failed: {retMsg}");
                }
                else if (op == "subscribe")
                {
                    lock (_lockObject)
                    {
                        _isSubscribed = isSuccess;
                    }
                    if (isSuccess)
                        Console.WriteLine("[WebSocket] Subscription successful");
                    else
                        Console.WriteLine($"[WebSocket] Subscription failed: {retMsg}");
                }
                return;
            }

            if (op == "ping")
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
                if (root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0)
                {
                    var firstArg = args[0];
                    if (firstArg.ValueKind == JsonValueKind.String)
                    {
                        timestamp = firstArg.GetString() ?? timestamp;
                    }
                }
                await SendPong(timestamp);
                return;
            }

            if (root.TryGetProperty("topic", out var topic) && 
                topic.GetString() == "execution")
            {
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var execution = ParseExecutionEvent(item);
                        if (execution != null)
                        {
                            await _eventWriter.WriteAsync(execution, CancellationToken.None);
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[WebSocket] JSON parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebSocket] Error processing message: {ex.Message}");
        }
    }

    private ExecutionEvent? ParseExecutionEvent(JsonElement element)
    {
        try
        {
            var execId = element.TryGetProperty("execId", out var execIdProp) 
                ? execIdProp.GetString() ?? "" 
                : "";
            var symbol = element.TryGetProperty("symbol", out var symbolProp) 
                ? symbolProp.GetString() ?? "" 
                : "";
            var side = element.TryGetProperty("side", out var sideProp) 
                ? sideProp.GetString() ?? "" 
                : "";
            var priceStr = element.TryGetProperty("execPrice", out var priceProp) 
                ? priceProp.GetString() ?? "0" 
                : "0";
            var qtyStr = element.TryGetProperty("execQty", out var qtyProp) 
                ? qtyProp.GetString() ?? "0" 
                : "0";
            var execTimeStr = element.TryGetProperty("execTime", out var execTimeProp) 
                ? execTimeProp.GetString() ?? "" 
                : "";

            if (string.IsNullOrEmpty(execId))
                return null;

            var price = decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) ? p : 0;
            var qty = decimal.TryParse(qtyStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var q) ? q : 0;
            
            DateTime execTime;
            if (long.TryParse(execTimeStr, out var timestampMs))
            {
                execTime = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).DateTime;
            }
            else
            {
                execTime = DateTime.UtcNow;
            }

            return new ExecutionEvent
            {
                ExecutionId = execId,
                Symbol = symbol,
                Side = side,
                Price = price,
                Qty = qty,
                ExecutionTime = execTime
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebSocket] Error parsing execution: {ex.Message}");
            return null;
        }
    }

    private async Task HeartbeatLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && 
               _webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(_config.HeartbeatIntervalSeconds * 1000, cancellationToken);
                
                if (_webSocket?.State == WebSocketState.Open)
                {
                    var pingMessage = JsonSerializer.Serialize(new { op = "ping" });
                    var bytes = Encoding.UTF8.GetBytes(pingMessage);
                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text,
                        true,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Heartbeat error: {ex.Message}");
            }
        }
    }

    private async Task SendPong(string timestamp)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            return;

        try
        {
            var pongMessage = new
            {
                op = "pong",
                args = new[] { timestamp }
            };
            var json = JsonSerializer.Serialize(pongMessage);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebSocket] Error sending pong: {ex.Message}");
        }
    }

    private string GenerateSignature(string expires)
    {
        var message = "GET/realtime" + expires;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_config.ApiSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return BitConverter.ToString(hash).Replace("-", "").ToLower();
    }

    private int CalculateReconnectDelay()
    {
        var delay = _config.ReconnectDelayMs * (int)Math.Pow(2, _reconnectAttempts);
        return Math.Min(delay, _config.MaxReconnectDelayMs);
    }

    public async Task Disconnect()
    {
        _cancellationTokenSource?.Cancel();
        
        if (_receiveTask != null)
        {
            try
            {
                await _receiveTask;
            }
            catch { }
        }
        
        if (_heartbeatTask != null)
        {
            try
            {
                await _heartbeatTask;
            }
            catch { }
        }

        if (_webSocket != null)
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await _webSocket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Shutting down",
                        CancellationToken.None);
                }
                catch { }
            }
            _webSocket.Dispose();
        }
        
        Console.WriteLine("[WebSocket] Disconnected");
    }
}
