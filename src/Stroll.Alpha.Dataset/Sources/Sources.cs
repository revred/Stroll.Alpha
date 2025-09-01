using Stroll.Alpha.Dataset.Models;
using Stroll.Alpha.Dataset.Storage;

namespace Stroll.Alpha.Dataset.Sources;

public interface IOptionsSource
{
    IAsyncEnumerable<OptionRecord> GetSnapshotAsync(string symbol, DateTime tsUtc, decimal moneynessWindow, int dteMin, int dteMax, CancellationToken ct);
    Task<IReadOnlyList<DateOnly>> GetExpiriesAsync(string symbol, DateTime asOfUtc, int dteMax, CancellationToken ct);
}

public interface IBarSource
{
    IAsyncEnumerable<UnderlyingBar> GetBarsAsync(string symbol, DateTime fromUtc, DateTime toUtc, string interval, CancellationToken ct);
}

/*
/// <summary>
/// Production implementation connected to Stroll.Theta.DB SQLite database.
/// </summary>
public sealed class ThetaDbSource : IOptionsSource, IBarSource, IDisposable
{
    private readonly SqliteDataProvider _provider;
    private readonly bool _useFallback;

    public ThetaDbSource(string? dbPath = null)
    {
        dbPath ??= Path.Combine("C:", "Code", "Stroll.Theta.DB", "stroll_theta.db");
        
        if (File.Exists(dbPath) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STROLL_THETA_DB")))
        {
            _provider = new SqliteDataProvider(dbPath);
            _useFallback = false;
        }
        else
        {
            // Fallback to in-memory database for testing
            _provider = new SqliteDataProvider(":memory:");
            _useFallback = true;
        }
    }

    public IAsyncEnumerable<OptionRecord> GetSnapshotAsync(string symbol, DateTime tsUtc, decimal mny, int dteMin, int dteMax, CancellationToken ct)
    {
        if (_useFallback)
        {
            return GetFallbackSnapshot(symbol, tsUtc, mny, dteMin, dteMax, ct);
        }
        
        return _provider.GetSnapshotAsync(symbol, tsUtc, mny, dteMin, dteMax, ct);
    }

    public Task<IReadOnlyList<DateOnly>> GetExpiriesAsync(string symbol, DateTime asOfUtc, int dteMax, CancellationToken ct)
    {
        if (_useFallback)
        {
            var list = Enumerable.Range(0, Math.Min(dteMax, 45))
                .Select(d => DateOnly.FromDateTime(asOfUtc.Date.AddDays(d)))
                .ToList();
            return Task.FromResult<IReadOnlyList<DateOnly>>(list);
        }

        return _provider.GetExpiriesAsync(symbol, asOfUtc, dteMax, ct);
    }

    public IAsyncEnumerable<UnderlyingBar> GetBarsAsync(string symbol, DateTime fromUtc, DateTime toUtc, string interval, CancellationToken ct)
    {
        if (_useFallback)
        {
            return GetFallbackBars(symbol, fromUtc, toUtc, interval, ct);
        }

        return _provider.GetBarsAsync(symbol, fromUtc, toUtc, interval, ct);
    }

    private async IAsyncEnumerable<OptionRecord> GetFallbackSnapshot(
        string symbol, 
        DateTime tsUtc, 
        decimal mny, 
        int dteMin, 
        int dteMax,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Stub data for testing
        var mid = 10m;
        yield return new OptionRecord(symbol, DateOnly.FromDateTime(tsUtc), tsUtc, 
            DateOnly.FromDateTime(tsUtc.Date.AddDays(7)), 95m, Right.Put, 
            9m, 11m, mid, null, 0.25m, -0.15, 0.02, -0.10, 0.12, 1000, 50);
        yield return new OptionRecord(symbol, DateOnly.FromDateTime(tsUtc), tsUtc, 
            DateOnly.FromDateTime(tsUtc.Date.AddDays(7)), 105m, Right.Call, 
            9m, 11m, mid, null, 0.24m, 0.15, 0.02, -0.10, 0.12, 900, 60);
        await Task.CompletedTask;
    }

    private async IAsyncEnumerable<UnderlyingBar> GetFallbackBars(
        string symbol, 
        DateTime fromUtc, 
        DateTime toUtc, 
        string interval,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var ts = fromUtc;
        while (ts <= toUtc)
        {
            yield return new UnderlyingBar(symbol, ts, 100, 101, 99, 100, 1000);
            ts = interval switch
            {
                "5m" => ts.AddMinutes(5),
                "15m" => ts.AddMinutes(15),
                "1h" => ts.AddHours(1),
                "1d" => ts.AddDays(1),
                _ => ts.AddMinutes(1)
            };
        }
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        _provider?.Dispose();
    }
}
*/
