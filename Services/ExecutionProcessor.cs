using System.Collections.Generic;
using System.Threading.Channels;
using BybitExecutionTracker.Models;

namespace BybitExecutionTracker.Services;

public class ExecutionProcessor
{
    private readonly Channel<ExecutionEvent> _channel;
    private readonly HashSet<string> _seenIds;
    private readonly SortedSet<ExecutionEvent> _buffer;
    private readonly IComparer<ExecutionEvent> _comparer;
    private readonly CancellationTokenSource _cts;
    private Task? _processingTask;
    
    private DateTime _lastFlushTime = DateTime.UtcNow;
    private const int FlushIntervalMs = 100;

    public ExecutionProcessor()
    {
        var options = new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        };
        _channel = Channel.CreateUnbounded<ExecutionEvent>(options);
        
        _seenIds = new HashSet<string>();
        _comparer = Comparer<ExecutionEvent>.Create((x, y) => 
        {
            var timeCompare = x.SortKey.CompareTo(y.SortKey);
            if (timeCompare != 0) return timeCompare;
            return string.Compare(x.ExecutionId, y.ExecutionId, StringComparison.Ordinal);
        });
        _buffer = new SortedSet<ExecutionEvent>(_comparer);
        _cts = new CancellationTokenSource();
    }

    public ChannelWriter<ExecutionEvent> Writer => _channel.Writer;

    public void Start()
    {
        if (_processingTask != null)
            return;
            
        _processingTask = Task.Run(ProcessEvents, _cts.Token);
        Console.WriteLine("[Processor] Started processing execution events");
    }

    private async Task ProcessEvents()
    {
        try
        {
            await foreach (var execution in _channel.Reader.ReadAllAsync(_cts.Token))
            {
                if (_seenIds.Contains(execution.ExecutionId))
                {
                    continue;
                }

                _seenIds.Add(execution.ExecutionId);
                _buffer.Add(execution);

                var now = DateTime.UtcNow;
                if ((now - _lastFlushTime).TotalMilliseconds >= FlushIntervalMs)
                {
                    FlushBuffer();
                    _lastFlushTime = now;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Processor] Error in processing loop: {ex.Message}");
        }
        finally
        {
            FlushBuffer();
        }
    }

    private void FlushBuffer()
    {
        while (_buffer.Count > 0)
        {
            var execution = _buffer.Min;
            if (execution != null)
            {
                _buffer.Remove(execution);
                PrintExecution(execution);
            }
            else
            {
                break;
            }
        }
    }

    private void PrintExecution(ExecutionEvent execution)
    {
        var timeStr = execution.ExecutionTime.ToString("yyyy-MM-dd HH:mm:ss");
        Console.WriteLine(
            $"[{timeStr}] Execution ID: {execution.ExecutionId}, " +
            $"Symbol: {execution.Symbol}, " +
            $"Side: {execution.Side}, " +
            $"Price: {execution.Price:F2}, " +
            $"Qty: {execution.Qty}, " +
            $"Time: {execution.ExecutionTime:yyyy-MM-ddTHH:mm:ss.fffZ}");
    }

    public async Task Stop()
    {
        _channel.Writer.Complete();
        _cts.Cancel();
        
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
        
        FlushBuffer();
        Console.WriteLine("[Processor] Stopped");
    }
}
