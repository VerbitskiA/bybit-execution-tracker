using System.Text.Json;
using System.Text.Json.Serialization;

namespace BybitExecutionTracker.Models;

public class BybitWebSocketMessage
{
    [JsonPropertyName("topic")]
    public string? Topic { get; set; }
    
    [JsonPropertyName("type")]
    public string? Type { get; set; }
    
    [JsonPropertyName("ts")]
    public long? Timestamp { get; set; }
    
    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
    
    [JsonPropertyName("ret_msg")]
    public string? RetMsg { get; set; }
    
    [JsonPropertyName("success")]
    public bool? Success { get; set; }
    
    [JsonPropertyName("req_id")]
    public string? ReqId { get; set; }
}

public class ExecutionData
{
    [JsonPropertyName("execId")]
    public string? ExecId { get; set; }
    
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }
    
    [JsonPropertyName("side")]
    public string? Side { get; set; }
    
    [JsonPropertyName("price")]
    public string? Price { get; set; }
    
    [JsonPropertyName("size")]
    public string? Size { get; set; }
    
    [JsonPropertyName("execTime")]
    public string? ExecTime { get; set; }
}
