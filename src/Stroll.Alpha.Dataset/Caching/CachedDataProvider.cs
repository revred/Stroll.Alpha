using Stroll.Alpha.Dataset.Models;
using Stroll.Alpha.Dataset.Sources;
using System.Text;

namespace Stroll.Alpha.Dataset.Caching;

/// <summary>
/// High-performance caching wrapper for data providers
/// Implements hot cache pattern for chain snapshots and bars
/// </summary>
public sealed class CachedDataProvider : IOptionsSource, IBarSource, IDisposable
{
    private readonly IOptionsSource _optionsSource;
    private readonly IBarSource _barSource;
    private readonly MemoryCache<string, List<OptionRecord>> _chainCache;
    private readonly MemoryCache<string, List<UnderlyingBar>> _barsCache;
    private readonly MemoryCache<string, decimal> _priceCache;

    public CachedDataProvider(IOptionsSource optionsSource, IBarSource barSource)
    {
        _optionsSource = optionsSource ?? throw new ArgumentNullException(nameof(optionsSource));
        _barSource = barSource ?? throw new ArgumentNullException(nameof(barSource));
        
        // Hot cache configuration per Steering.md: ≤150ms hot / ≤1.5s cold
        _chainCache = new MemoryCache<string, List<OptionRecord>>(
            maxEntries: 500, 
            defaultTtl: TimeSpan.FromMinutes(15)); // Chain data changes less frequently
        
        _barsCache = new MemoryCache<string, List<UnderlyingBar>>(
            maxEntries: 200, 
            defaultTtl: TimeSpan.FromMinutes(5)); // Price data more volatile
        
        _priceCache = new MemoryCache<string, decimal>(
            maxEntries: 100,
            defaultTtl: TimeSpan.FromMinutes(1)); // Current prices cache briefly
    }

    /// <summary>
    /// Cached options chain snapshot with LRU eviction
    /// </summary>
    public async IAsyncEnumerable<OptionRecord> GetSnapshotAsync(
        string symbol, 
        DateTime tsUtc, 
        decimal moneynessWindow, 
        int dteMin, 
        int dteMax,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var cacheKey = BuildChainCacheKey(symbol, tsUtc, moneynessWindow, dteMin, dteMax);
        
        var cachedRecords = await _chainCache.GetOrAddAsync(
            cacheKey,
            async (key, state) =>
            {
                var records = new List<OptionRecord>();
                await foreach (var record in _optionsSource.GetSnapshotAsync(symbol, tsUtc, moneynessWindow, dteMin, dteMax, ct))
                {
                    records.Add(record);
                }
                return records;
            },
            state: default(object),
            ttl: TimeSpan.FromMinutes(15)
        );

        foreach (var record in cachedRecords)
        {
            yield return record;
        }
    }

    /// <summary>
    /// Cached underlying bars with short TTL for price volatility
    /// </summary>
    public async IAsyncEnumerable<UnderlyingBar> GetBarsAsync(
        string symbol, 
        DateTime fromUtc, 
        DateTime toUtc, 
        string interval,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var cacheKey = BuildBarsCacheKey(symbol, fromUtc, toUtc, interval);
        
        var cachedBars = await _barsCache.GetOrAddAsync(
            cacheKey,
            async (key, state) =>
            {
                var bars = new List<UnderlyingBar>();
                await foreach (var bar in _barSource.GetBarsAsync(symbol, fromUtc, toUtc, interval, ct))
                {
                    bars.Add(bar);
                }
                return bars;
            },
            state: default(object),
            ttl: TimeSpan.FromMinutes(5)
        );

        foreach (var bar in cachedBars)
        {
            yield return bar;
        }
    }

    /// <summary>
    /// Cached expiry dates lookup  
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> GetExpiriesAsync(
        string symbol, 
        DateTime asOfUtc, 
        int maxDte, 
        CancellationToken ct = default)
    {
        // Delegate to underlying source (expiries don't change frequently within day)
        return await _optionsSource.GetExpiriesAsync(symbol, asOfUtc, maxDte, ct);
    }

    /// <summary>
    /// Cached underlying price with very short TTL
    /// </summary>
    public async Task<decimal> GetUnderlyingPriceAsync(
        string symbol, 
        DateTime tsUtc, 
        CancellationToken ct = default)
    {
        var cacheKey = BuildPriceCacheKey(symbol, tsUtc);
        
        return await _priceCache.GetOrAddAsync(
            cacheKey,
            async (key, source) =>
            {
                // Get most recent bar before timestamp
                var bars = new List<UnderlyingBar>();
                await foreach (var bar in _barSource.GetBarsAsync(symbol, tsUtc.AddMinutes(-5), tsUtc, "1m", ct))
                {
                    bars.Add(bar);
                }
                return bars.LastOrDefault()?.Close ?? 0m;
            },
            state: _barSource,
            ttl: TimeSpan.FromMinutes(1)
        );
    }

    /// <summary>
    /// Get cache performance statistics
    /// </summary>
    public CachePerformanceStats GetCacheStats()
    {
        return new CachePerformanceStats
        {
            ChainCacheStats = _chainCache.GetStats(),
            BarsCacheStats = _barsCache.GetStats(), 
            PriceCacheStats = _priceCache.GetStats()
        };
    }

    /// <summary>
    /// Warm cache with frequently accessed data
    /// </summary>
    public async Task WarmCacheAsync(
        string symbol, 
        DateTime[] timestamps, 
        CancellationToken ct = default)
    {
        // Pre-populate cache with common queries to achieve ≤150ms hot performance
        var tasks = new List<Task>();
        
        foreach (var ts in timestamps)
        {
            tasks.Add(Task.Run(async () =>
            {
                // Warm chain cache
                await GetSnapshotAsync(symbol, ts, 0.15m, 0, 45, ct).ToListAsync(ct);
                
                // Warm bars cache  
                await GetBarsAsync(symbol, ts.AddMinutes(-60), ts, "1m", ct).ToListAsync(ct);
                
                // Warm price cache
                await GetUnderlyingPriceAsync(symbol, ts, ct);
            }, ct));
        }

        await Task.WhenAll(tasks);
    }

    private static string BuildChainCacheKey(string symbol, DateTime tsUtc, decimal moneynessWindow, int dteMin, int dteMax)
    {
        var sb = new StringBuilder(64);
        sb.Append(symbol).Append('|')
          .Append(tsUtc.ToString("yyyy-MM-ddTHH:mm")).Append('|')
          .Append(moneynessWindow.ToString("F2")).Append('|')
          .Append(dteMin).Append('-').Append(dteMax);
        return sb.ToString();
    }

    private static string BuildBarsCacheKey(string symbol, DateTime fromUtc, DateTime toUtc, string interval)
    {
        var sb = new StringBuilder(64);
        sb.Append(symbol).Append('|')
          .Append(fromUtc.ToString("yyyy-MM-ddTHH:mm")).Append('|')
          .Append(toUtc.ToString("yyyy-MM-ddTHH:mm")).Append('|')
          .Append(interval);
        return sb.ToString();
    }

    private static string BuildPriceCacheKey(string symbol, DateTime tsUtc)
    {
        // Round to minute for better cache hit ratio
        var rounded = new DateTime(tsUtc.Year, tsUtc.Month, tsUtc.Day, tsUtc.Hour, tsUtc.Minute, 0, DateTimeKind.Utc);
        return $"{symbol}|{rounded:yyyy-MM-ddTHH:mm}";
    }

    public void Dispose()
    {
        _chainCache?.Dispose();
        _barsCache?.Dispose();
        _priceCache?.Dispose();
        
        if (_optionsSource is IDisposable optionsDisposable)
            optionsDisposable.Dispose();
        
        if (_barSource is IDisposable barDisposable)
            barDisposable.Dispose();
    }
}

public sealed record CachePerformanceStats
{
    public required CacheStats ChainCacheStats { get; init; }
    public required CacheStats BarsCacheStats { get; init; }
    public required CacheStats PriceCacheStats { get; init; }
}

public static class AsyncEnumerableExtensions
{
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source, CancellationToken ct = default)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(ct))
        {
            list.Add(item);
        }
        return list;
    }
}