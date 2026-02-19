namespace BybitExecutionTracker.Models;

public class BybitConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string WebSocketUrl { get; set; } = "wss://stream.bybit.com/v5/private";
    public int ReconnectDelayMs { get; set; } = 1000;
    public int MaxReconnectDelayMs { get; set; } = 30000;
}
