using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Stroll.Alpha.Dataset.Caching;

/// <summary>
/// High-performance LRU memory cache with TTL support
/// Thread-safe implementation optimized for hot data access patterns
/// </summary>
public sealed class MemoryCache<TKey, TValue> : IDisposable where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, CacheEntry<TValue>> _cache = new();
    private readonly ConcurrentDictionary<TKey, DateTime> _accessTimes = new();
    private readonly Timer _cleanupTimer;
    private readonly int _maxEntries;
    private readonly TimeSpan _defaultTtl;
    private bool _disposed;

    public MemoryCache(int maxEntries = 1000, TimeSpan? defaultTtl = null)
    {
        _maxEntries = maxEntries;
        _defaultTtl = defaultTtl ?? TimeSpan.FromMinutes(30);
        
        // Cleanup expired entries every 5 minutes
        _cleanupTimer = new Timer(CleanupExpired, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Get value from cache or compute if missing/expired
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task<TValue> GetOrAddAsync<TState>(
        TKey key, 
        Func<TKey, TState, Task<TValue>> factory, 
        TState state,
        TimeSpan? ttl = null)
    {
        var now = DateTime.UtcNow;
        
        // Fast path: cache hit with valid entry
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
        {
            _accessTimes[key] = now; // Update access time for LRU
            return entry.Value;
        }

        // Slow path: compute and cache
        var value = await factory(key, state);
        var newEntry = new CacheEntry<TValue>
        {
            Value = value,
            CreatedAt = now,
            ExpiresAt = now + (ttl ?? _defaultTtl)
        };

        _cache[key] = newEntry;
        _accessTimes[key] = now;

        // Enforce size limit with LRU eviction
        if (_cache.Count > _maxEntries)
        {
            EvictLeastRecentlyUsed();
        }

        return value;
    }

    /// <summary>
    /// Get value from cache if present and not expired
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, out TValue? value)
    {
        var now = DateTime.UtcNow;
        
        if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > now)
        {
            _accessTimes[key] = now;
            value = entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Add or update cache entry
    /// </summary>
    public void Set(TKey key, TValue value, TimeSpan? ttl = null)
    {
        var now = DateTime.UtcNow;
        var entry = new CacheEntry<TValue>
        {
            Value = value,
            CreatedAt = now,
            ExpiresAt = now + (ttl ?? _defaultTtl)
        };

        _cache[key] = entry;
        _accessTimes[key] = now;

        if (_cache.Count > _maxEntries)
        {
            EvictLeastRecentlyUsed();
        }
    }

    /// <summary>
    /// Remove entry from cache
    /// </summary>
    public bool Remove(TKey key)
    {
        _accessTimes.TryRemove(key, out _);
        return _cache.TryRemove(key, out _);
    }

    /// <summary>
    /// Clear all cached entries
    /// </summary>
    public void Clear()
    {
        _cache.Clear();
        _accessTimes.Clear();
    }

    /// <summary>
    /// Get cache statistics for monitoring
    /// </summary>
    public CacheStats GetStats()
    {
        var now = DateTime.UtcNow;
        var validEntries = _cache.Values.Count(e => e.ExpiresAt > now);
        
        return new CacheStats
        {
            TotalEntries = _cache.Count,
            ValidEntries = validEntries,
            ExpiredEntries = _cache.Count - validEntries,
            MaxEntries = _maxEntries,
            HitRatio = 0.0 // Would need hit/miss counters for accurate ratio
        };
    }

    private void EvictLeastRecentlyUsed()
    {
        if (!_accessTimes.Any()) return;

        // Find oldest accessed entries to evict (evict ~10% when over limit)
        var evictCount = Math.Max(1, _maxEntries / 10);
        var oldestKeys = _accessTimes
            .OrderBy(kvp => kvp.Value)
            .Take(evictCount)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in oldestKeys)
        {
            _cache.TryRemove(key, out _);
            _accessTimes.TryRemove(key, out _);
        }
    }

    private void CleanupExpired(object? state)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        var expiredKeys = _cache
            .Where(kvp => kvp.Value.ExpiresAt <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _cache.TryRemove(key, out _);
            _accessTimes.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cleanupTimer?.Dispose();
            _cache.Clear();
            _accessTimes.Clear();
            _disposed = true;
        }
    }
}

public sealed record CacheEntry<TValue>
{
    public required TValue Value { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime ExpiresAt { get; init; }
}

public sealed record CacheStats
{
    public int TotalEntries { get; init; }
    public int ValidEntries { get; init; }
    public int ExpiredEntries { get; init; }
    public int MaxEntries { get; init; }
    public double HitRatio { get; init; }
}