using Microsoft.Extensions.Configuration;
using BybitExecutionTracker.Models;
using BybitExecutionTracker.Services;

namespace BybitExecutionTracker;

internal static class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Bybit Execution Tracker - Starting...");
        
        using var shutdownCts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n[Main] Shutdown requested (Ctrl+C)");
            shutdownCts.Cancel();
        };

        ExecutionProcessor? processor = null;

        try
        {
            var config = LoadConfiguration();
            Console.WriteLine("[Main] Configuration loaded");

            processor = new ExecutionProcessor();
            _ = processor.StartAsync(shutdownCts.Token);

            var wsService = new BybitWebSocketService(config, processor.Writer);
            var wsTask = wsService.RunAsync(shutdownCts.Token);

            Console.WriteLine("[Main] Services started. Waiting for events...");

            await wsTask;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[Main] Operation cancelled.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Main] Fatal error: {ex}");
            await shutdownCts.CancelAsync();
        }
        finally
        {
            if (processor != null)
            {
                Console.WriteLine("[Main] Stopping processor. Waiting for final messages to be processed...");
                await processor.StopAsync();
                Console.WriteLine("[Main] Processor stopped. Shutdown completed.");
            }
            else
            {
                Console.WriteLine("[Main] No processor to stop.");
            }
        }
    }

    private static BybitConfig LoadConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "BYBIT_")
            .Build();

        var config = new BybitConfig();
        configuration.GetSection("Bybit").Bind(config);

        if (string.IsNullOrWhiteSpace(config.ApiKey) ||
            string.IsNullOrWhiteSpace(config.ApiSecret))
        {
            throw new InvalidOperationException(
                "ApiKey and ApiSecret must be provided via environment variables " +
                "BYBIT_APIKEY/BYBIT_APISECRET or in appsettings.json under Bybit:ApiKey / Bybit:ApiSecret");
        }

        return config;
    }
}