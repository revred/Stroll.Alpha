using System.Text.Json;
using System.Text.Json.Serialization;
using Stroll.Alpha.Dataset.Models;
using Stroll.Alpha.Market.Services;

namespace Stroll.Alpha.Market.MCP;

/// <summary>
/// MCP (Model Context Protocol) JSON-over-stdio server
/// Provides standardized interface for external backtesting tools
/// </summary>
public sealed class McpServer : IDisposable
{
    private readonly MarketQueryService _marketService;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public McpServer(MarketQueryService marketService, TextReader? input = null, TextWriter? output = null)
    {
        _marketService = marketService ?? throw new ArgumentNullException(nameof(marketService));
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Start MCP server message loop
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            await _output.WriteLineAsync("MCP Stroll.Alpha Market Server v1.0");
            await _output.FlushAsync();

            string? line;
            while (!ct.IsCancellationRequested && (line = await _input.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var request = JsonSerializer.Deserialize<McpRequest>(line, _jsonOptions);
                    if (request == null) continue;

                    var response = await HandleRequestAsync(request, ct);
                    var responseJson = JsonSerializer.Serialize(response, _jsonOptions);
                    
                    await _output.WriteLineAsync(responseJson);
                    await _output.FlushAsync();
                }
                catch (JsonException ex)
                {
                    var errorResponse = new McpResponse
                    {
                        Id = null,
                        Error = new McpError
                        {
                            Code = -32700, // Parse error
                            Message = $"Invalid JSON: {ex.Message}"
                        }
                    };
                    
                    var errorJson = JsonSerializer.Serialize(errorResponse, _jsonOptions);
                    await _output.WriteLineAsync(errorJson);
                    await _output.FlushAsync();
                }
                catch (Exception ex)
                {
                    var errorResponse = new McpResponse
                    {
                        Id = null,
                        Error = new McpError
                        {
                            Code = -32603, // Internal error
                            Message = $"Server error: {ex.Message}"
                        }
                    };
                    
                    var errorJson = JsonSerializer.Serialize(errorResponse, _jsonOptions);
                    await _output.WriteLineAsync(errorJson);
                    await _output.FlushAsync();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown
        }
    }

    private async Task<McpResponse> HandleRequestAsync(McpRequest request, CancellationToken ct)
    {
        try
        {
            var result = request.Method switch
            {
                "get_health" => await HandleGetHealthAsync(request.Params, ct),
                "get_chain" => await HandleGetChainAsync(request.Params, ct),
                "get_snapshot" => await HandleGetSnapshotAsync(request.Params, ct),
                "get_underlying_price" => await HandleGetUnderlyingPriceAsync(request.Params, ct),
                "get_expiries" => await HandleGetExpiriesAsync(request.Params, ct),
                _ => throw new ArgumentException($"Unknown method: {request.Method}")
            };

            return new McpResponse
            {
                Id = request.Id,
                Result = result
            };
        }
        catch (Exception ex)
        {
            return new McpResponse
            {
                Id = request.Id,
                Error = new McpError
                {
                    Code = -32602, // Invalid params
                    Message = ex.Message
                }
            };
        }
    }

    private async Task<object> HandleGetHealthAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        // Simple health check
        return new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            version = "1.0.0"
        };
    }

    private async Task<object> HandleGetChainAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        if (!paramsElement.HasValue)
            throw new ArgumentException("Missing required parameters");

        var @params = paramsElement.Value;
        
        var symbol = GetRequiredString(@params, "symbol");
        var atUtcStr = GetRequiredString(@params, "at");
        
        if (!DateTime.TryParse(atUtcStr, out var atUtc))
            throw new ArgumentException("Invalid 'at' timestamp format");

        var dteMin = GetOptionalInt(@params, "dte_min", 0);
        var dteMax = GetOptionalInt(@params, "dte_max", 45);
        var moneyness = GetOptionalDecimal(@params, "moneyness", 0.15m);

        var options = await _marketService.GetOptionsSnapshotAsync(
            symbol, atUtc, moneyness, dteMin, dteMax, ct);

        return new
        {
            symbol,
            atUtc = atUtc.ToString("O"),
            count = options.Count,
            options = options.Select(o => new
            {
                symbol = o.Symbol,
                session = o.Session.ToString("O"),
                tsUtc = o.TsUtc.ToString("O"),
                expiry = o.Expiry.ToString("O"),
                strike = o.Strike,
                right = o.Right.ToString(),
                bid = o.Bid,
                ask = o.Ask,
                mid = o.Mid,
                last = o.Last,
                iv = o.Iv,
                delta = o.Delta,
                gamma = o.Gamma,
                theta = o.Theta,
                vega = o.Vega,
                oi = o.Oi,
                volume = o.Volume
            }).ToArray()
        };
    }

    private async Task<object> HandleGetSnapshotAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        // Minute-level snapshot - similar to chain but focused on specific timestamp
        return await HandleGetChainAsync(paramsElement, ct);
    }

    private async Task<object> HandleGetUnderlyingPriceAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        if (!paramsElement.HasValue)
            throw new ArgumentException("Missing required parameters");

        var @params = paramsElement.Value;
        
        var symbol = GetRequiredString(@params, "symbol");
        var atUtcStr = GetRequiredString(@params, "at");
        
        if (!DateTime.TryParse(atUtcStr, out var atUtc))
            throw new ArgumentException("Invalid 'at' timestamp format");

        var price = await _marketService.GetUnderlyingPriceAsync(symbol, atUtc, ct);

        return new
        {
            symbol,
            atUtc = atUtc.ToString("O"),
            price
        };
    }

    private async Task<object> HandleGetExpiriesAsync(JsonElement? paramsElement, CancellationToken ct)
    {
        if (!paramsElement.HasValue)
            throw new ArgumentException("Missing required parameters");

        var @params = paramsElement.Value;
        
        var symbol = GetRequiredString(@params, "symbol");
        var asOfUtcStr = GetRequiredString(@params, "as_of");
        
        if (!DateTime.TryParse(asOfUtcStr, out var asOfUtc))
            throw new ArgumentException("Invalid 'as_of' timestamp format");

        var maxDte = GetOptionalInt(@params, "max_dte", 45);

        var expiries = await _marketService.GetAvailableExpiriesAsync(symbol, asOfUtc, maxDte, ct);

        return new
        {
            symbol,
            asOfUtc = asOfUtc.ToString("O"),
            maxDte,
            expiries = expiries.Select(e => e.ToString("O")).ToArray()
        };
    }

    // Helper methods for parameter extraction
    private static string GetRequiredString(JsonElement @params, string name)
    {
        if (!@params.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Missing or invalid required parameter: {name}");
        return element.GetString()!;
    }

    private static int GetOptionalInt(JsonElement @params, string name, int defaultValue)
    {
        return @params.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetInt32() 
            : defaultValue;
    }

    private static decimal GetOptionalDecimal(JsonElement @params, string name, decimal defaultValue)
    {
        return @params.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDecimal()
            : defaultValue;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _marketService?.Dispose();
            _disposed = true;
        }
    }
}

// MCP Protocol types
public sealed record McpRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
    
    [JsonPropertyName("method")]
    public required string Method { get; init; }
    
    [JsonPropertyName("params")]
    public JsonElement? Params { get; init; }
}

public sealed record McpResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
    
    [JsonPropertyName("result")]
    public object? Result { get; init; }
    
    [JsonPropertyName("error")]
    public McpError? Error { get; init; }
}

public sealed record McpError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }
    
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}