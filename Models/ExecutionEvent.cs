namespace BybitExecutionTracker.Models;

public readonly struct ExecutionEvent
{
    public string ExecutionId { get; }
    public string Symbol { get; }
    public string Side { get; }
    public decimal Price { get; }
    public decimal Qty { get; }
    public DateTime ExecutionTime { get; }

    public ExecutionEvent(
        string executionId,
        string symbol,
        string side,
        decimal price,
        decimal qty,
        DateTime executionTime)
    {
        ExecutionId = executionId;
        Symbol = symbol;
        Side = side;
        Price = price;
        Qty = qty;
        ExecutionTime = executionTime;
    }
}