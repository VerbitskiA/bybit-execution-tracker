using System.Threading.Channels;
using BybitExecutionTracker.Models;

namespace BybitExecutionTracker.Services;

public sealed class ExecutionProcessor
{
    private readonly Channel<ExecutionEvent> _channel;

    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _seenQueue = new();

    private const int MaxSeen = 10_000;

    private Task? _processingTask;

    public ExecutionProcessor()
    {
        _channel = Channel.CreateUnbounded<ExecutionEvent>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
    }

    public ChannelWriter<ExecutionEvent> Writer => _channel.Writer;

    public void Start()
    {
        _processingTask ??= ProcessAsync();
        Console.WriteLine("[Processor] Started");
    }

    private async Task ProcessAsync()
    {
        await foreach (var execution in _channel.Reader.ReadAllAsync())
        {
            if (!TryDeduplicate(execution.ExecutionId))
                continue;

            Print(execution);
        }
    }

    private bool TryDeduplicate(string id)
    {
        if (!_seen.Add(id))
            return false;

        _seenQueue.Enqueue(id);

        if (_seenQueue.Count > MaxSeen)
        {
            var oldest = _seenQueue.Dequeue();
            _seen.Remove(oldest);
        }

        return true;
    }

    private static void Print(in ExecutionEvent e)
    {
        Console.WriteLine(
            $"[{e.ExecutionTime:yyyy-MM-dd HH:mm:ss}] " +
            $"Execution ID: {e.ExecutionId}, " +
            $"Symbol: {e.Symbol}, Side: {e.Side}, " +
            $"Price: {e.Price:F2}, Qty: {e.Qty}, " +
            $"Time: {e.ExecutionTime:O}");
    }

    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();

        if (_processingTask != null)
        {
            try
            {
                await _processingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        Console.WriteLine("[Processor] Stopped");
    }
}
