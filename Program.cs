using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using BybitExecutionTracker.Models;
using BybitExecutionTracker.Services;

namespace BybitExecutionTracker;

class Program
{
    private static CancellationTokenSource? _shutdownCts;
    private static ExecutionProcessor? _processor;
    private static BybitWebSocketService? _webSocketService;

    static async Task Main(string[] args)
    {
        Console.WriteLine("Bybit Execution Tracker - Starting...");
        
        _shutdownCts = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n[Main] Shutdown requested (Ctrl+C)");
            _shutdownCts.Cancel();
        };

        try
        {
            var config = LoadConfiguration();
            
            if (string.IsNullOrEmpty(config.ApiKey) || string.IsNullOrEmpty(config.ApiSecret))
            {
                Console.WriteLine("ERROR: ApiKey and ApiSecret must be set in appsettings.json or environment variables");
                Console.WriteLine("Environment variables: BYBIT__APIKEY and BYBIT__APISECRET");
                return;
            }

            _processor = new ExecutionProcessor();
            _processor.Start();

            _webSocketService = new BybitWebSocketService(config, _processor.Writer);

            var connectTask = _webSocketService.Connect(_shutdownCts.Token);

            await connectTask;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[Main] Application shutdown");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Main] Fatal error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
        finally
        {
            Console.WriteLine("[Main] Shutting down...");
            
            if (_webSocketService != null)
            {
                await _webSocketService.Disconnect();
            }
            
            if (_processor != null)
            {
                await _processor.Stop();
            }
            
            Console.WriteLine("[Main] Shutdown complete");
        }
    }

    private static BybitConfig LoadConfiguration()
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();

        var configuration = builder.Build();
        
        var config = new BybitConfig();
        configuration.GetSection("Bybit").Bind(config);
        
        var apiKey = Environment.GetEnvironmentVariable("BYBIT__APIKEY") 
                     ?? Environment.GetEnvironmentVariable("BYBIT_APIKEY");
        var apiSecret = Environment.GetEnvironmentVariable("BYBIT__APISECRET") 
                        ?? Environment.GetEnvironmentVariable("BYBIT_APISECRET");
        
        if (!string.IsNullOrEmpty(apiKey))
            config.ApiKey = apiKey;
        if (!string.IsNullOrEmpty(apiSecret))
            config.ApiSecret = apiSecret;
        
        return config;
    }
}
