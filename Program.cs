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
            Console.WriteLine("\n[Main] Shutdown requested");
            shutdownCts.Cancel();
        };

        try
        {
            var config = LoadAndValidateConfiguration();

            var processor = new ExecutionProcessor();
            processor.Start();

            var wsService = new BybitWebSocketService(config, processor.Writer);

            await wsService.RunAsync(shutdownCts.Token);

            await processor.StopAsync();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[Main] Shutdown completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Main] Fatal error: {ex}");
        }
    }

    private static BybitConfig LoadAndValidateConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var config = new BybitConfig();
        configuration.GetSection("Bybit").Bind(config);

        config.ApiKey =
            Environment.GetEnvironmentVariable("BYBIT__APIKEY")
            ?? Environment.GetEnvironmentVariable("BYBIT_APIKEY")
            ?? config.ApiKey;

        config.ApiSecret =
            Environment.GetEnvironmentVariable("BYBIT__APISECRET")
            ?? Environment.GetEnvironmentVariable("BYBIT_APISECRET")
            ?? config.ApiSecret;

        if (string.IsNullOrWhiteSpace(config.ApiKey) ||
            string.IsNullOrWhiteSpace(config.ApiSecret))
        {
            throw new InvalidOperationException(
                "ApiKey and ApiSecret must be provided.");
        }

        return config;
    }
}
