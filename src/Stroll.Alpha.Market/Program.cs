using System.Text.Json;
using Stroll.Alpha.Dataset.Config;
using Stroll.Alpha.Market.Contracts;
using Stroll.Alpha.Market.Services;
using Stroll.Alpha.Market.MCP;

namespace Stroll.Alpha.Market;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            ShowHelp();
            return 0;
        }

        try
        {
            using var svc = new MarketQueryService();

            var command = args[0].ToLower();
            switch (command)
            {
                case "chain":
                    return await HandleChainCommand(svc, args);
                case "data":
                    return await HandleDataCommand(svc, args);
                case "bars":
                    return await HandleBarsCommand(svc, args);
                case "quality":
                    return await HandleQualityCommand(svc, args);
                case "mcp":
                    return await HandleMcpCommand(svc);
                default:
                    Console.WriteLine($"Unknown command: {command}");
                    ShowHelp();
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> HandleChainCommand(MarketQueryService svc, string[] args)
    {
        string? symbol = null;
        DateTime? at = null;
        int dteMin = 0, dteMax = 45;
        decimal moneyness = 0.15m;
        bool json = false;

        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) break;
            
            switch (args[i].ToLower())
            {
                case "--symbol":
                    symbol = args[i + 1];
                    break;
                case "--at":
                    if (DateTime.TryParse(args[i + 1], out var parsed))
                        at = parsed;
                    break;
                case "--dte-min":
                    int.TryParse(args[i + 1], out dteMin);
                    break;
                case "--dte-max":
                    int.TryParse(args[i + 1], out dteMax);
                    break;
                case "--moneyness":
                    decimal.TryParse(args[i + 1], out moneyness);
                    break;
                case "--json":
                    bool.TryParse(args[i + 1], out json);
                    break;
            }
        }

        if (symbol == null || at == null)
        {
            Console.WriteLine("Missing required arguments: --symbol and --at");
            return 1;
        }

        var req = new ChainRequest(symbol, at.Value, dteMin, dteMax, moneyness, json);
        var res = await svc.GetChainAsync(req, CancellationToken.None);
        
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true }));
        else
            Console.WriteLine($"{res.Symbol} @ {res.AtUtc:yyyy-MM-dd HH:mm:ss}Z | strikes={res.Count} | completeness={res.Completeness:P1}");
        
        return 0;
    }

    static async Task<int> HandleDataCommand(MarketQueryService svc, string[] args)
    {
        Console.WriteLine("Data command not fully implemented in simplified version");
        return 0;
    }

    static async Task<int> HandleBarsCommand(MarketQueryService svc, string[] args)
    {
        Console.WriteLine("Bars command not fully implemented in simplified version");
        return 0;
    }

    static async Task<int> HandleQualityCommand(MarketQueryService svc, string[] args)
    {
        Console.WriteLine("Quality command not fully implemented in simplified version");
        return 0;
    }

    static async Task<int> HandleMcpCommand(MarketQueryService svc)
    {
        using var mcpServer = new McpServer(svc);
        await mcpServer.RunAsync(CancellationToken.None);
        return 0;
    }

    static void ShowHelp()
    {
        Console.WriteLine("Stroll.Alpha.Market — Historical options data provider for backtesting (0-45 DTE)");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  chain    Get options chain summary");
        Console.WriteLine("  data     Get full options data");  
        Console.WriteLine("  bars     Get underlying price bars");
        Console.WriteLine("  quality  Check data quality");
        Console.WriteLine("  mcp      Run MCP server");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine("  dotnet run chain --symbol SPX --at \"2024-01-15T15:00:00Z\"");
    }
}