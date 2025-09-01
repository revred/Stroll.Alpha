using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

namespace Stroll.Alpha.Dataset.Storage;

/// <summary>
/// High-performance SQLite connection pool with automatic cleanup
/// Optimized for read-heavy workloads with connection reuse
/// </summary>
public sealed class ConnectionPool : IDisposable
{
    private readonly string _connectionString;
    private readonly ConcurrentQueue<PooledConnection> _availableConnections = new();
    private readonly ConcurrentDictionary<int, PooledConnection> _activeConnections = new();
    private readonly SemaphoreSlim _semaphore;
    private readonly Timer _cleanupTimer;
    private readonly int _maxPoolSize;
    private readonly TimeSpan _connectionTimeout;
    private int _currentPoolSize;
    private bool _disposed;

    public ConnectionPool(string connectionString, int maxPoolSize = 20, TimeSpan? connectionTimeout = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _maxPoolSize = maxPoolSize;
        _connectionTimeout = connectionTimeout ?? TimeSpan.FromMinutes(30);
        _semaphore = new SemaphoreSlim(maxPoolSize, maxPoolSize);
        
        // Cleanup idle connections every 10 minutes
        _cleanupTimer = new Timer(CleanupIdleConnections, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    /// <summary>
    /// Rent a connection from the pool (or create new if needed)
    /// </summary>
    public async Task<IPooledConnection> RentConnectionAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);

        try
        {
            // Try to get existing connection from pool
            if (_availableConnections.TryDequeue(out var pooledConnection))
            {
                // Verify connection is still alive
                if (IsConnectionValid(pooledConnection))
                {
                    pooledConnection.LastUsed = DateTime.UtcNow;
                    _activeConnections[pooledConnection.Id] = pooledConnection;
                    return pooledConnection;
                }
                else
                {
                    // Connection is stale, dispose it
                    pooledConnection.Connection.Dispose();
                    Interlocked.Decrement(ref _currentPoolSize);
                }
            }

            // Create new connection
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);
            
            var newPooledConnection = new PooledConnection(this)
            {
                Id = Interlocked.Increment(ref _currentPoolSize),
                Connection = connection,
                CreatedAt = DateTime.UtcNow,
                LastUsed = DateTime.UtcNow
            };

            _activeConnections[newPooledConnection.Id] = newPooledConnection;
            return newPooledConnection;
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    /// <summary>
    /// Return connection to pool for reuse
    /// </summary>
    internal void ReturnConnection(PooledConnection pooledConnection)
    {
        _activeConnections.TryRemove(pooledConnection.Id, out _);
        
        if (!_disposed && IsConnectionValid(pooledConnection))
        {
            _availableConnections.Enqueue(pooledConnection);
        }
        else
        {
            pooledConnection.Connection.Dispose();
            Interlocked.Decrement(ref _currentPoolSize);
        }
        
        _semaphore.Release();
    }

    /// <summary>
    /// Get pool statistics for monitoring
    /// </summary>
    public PoolStats GetStats()
    {
        return new PoolStats
        {
            MaxPoolSize = _maxPoolSize,
            CurrentPoolSize = _currentPoolSize,
            AvailableConnections = _availableConnections.Count,
            ActiveConnections = _activeConnections.Count,
            TotalConnections = _currentPoolSize
        };
    }

    private bool IsConnectionValid(PooledConnection pooledConnection)
    {
        if (pooledConnection.Connection.State != System.Data.ConnectionState.Open)
            return false;

        // Check if connection has been idle too long
        if (DateTime.UtcNow - pooledConnection.LastUsed > _connectionTimeout)
            return false;

        return true;
    }

    private void CleanupIdleConnections(object? state)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        var connectionsToClose = new List<PooledConnection>();

        // Collect idle connections from available pool
        while (_availableConnections.TryDequeue(out var connection))
        {
            if (now - connection.LastUsed > _connectionTimeout)
            {
                connectionsToClose.Add(connection);
            }
            else
            {
                // Put back connections that are still fresh
                _availableConnections.Enqueue(connection);
                break; // Assume queue is roughly ordered by usage time
            }
        }

        // Close idle connections
        foreach (var connection in connectionsToClose)
        {
            connection.Connection.Dispose();
            Interlocked.Decrement(ref _currentPoolSize);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer?.Dispose();
        _semaphore?.Dispose();

        // Close all connections
        while (_availableConnections.TryDequeue(out var connection))
        {
            connection.Connection.Dispose();
        }

        foreach (var activeConnection in _activeConnections.Values)
        {
            activeConnection.Connection.Dispose();
        }

        _activeConnections.Clear();
        SqliteConnection.ClearAllPools();
    }
}

/// <summary>
/// Pooled connection wrapper that automatically returns to pool when disposed
/// </summary>
public sealed class PooledConnection : IPooledConnection
{
    private readonly ConnectionPool _pool;
    private bool _disposed;

    internal PooledConnection(ConnectionPool pool)
    {
        _pool = pool;
    }

    public int Id { get; init; }
    public required SqliteConnection Connection { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastUsed { get; set; }

    public void Dispose()
    {
        if (!_disposed)
        {
            _pool.ReturnConnection(this);
            _disposed = true;
        }
    }
}

public interface IPooledConnection : IDisposable
{
    SqliteConnection Connection { get; }
}

public sealed record PoolStats
{
    public int MaxPoolSize { get; init; }
    public int CurrentPoolSize { get; init; }
    public int AvailableConnections { get; init; }
    public int ActiveConnections { get; init; }
    public int TotalConnections { get; init; }
}