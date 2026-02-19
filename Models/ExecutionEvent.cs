namespace BybitExecutionTracker.Models;

public class ExecutionEvent
{
    public string ExecutionId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal Qty { get; set; }
    public DateTime ExecutionTime { get; set; }
    
    public long SortKey => ExecutionTime.Ticks;
}
