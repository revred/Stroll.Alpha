using System.Text.Json;
using Stroll.Alpha.Dataset.Config;
using Stroll.Alpha.Dataset.Sources;
using Stroll.Alpha.Dataset.Storage;
using Stroll.Alpha.Dataset.Models;
using Stroll.Alpha.Dataset.Extensions;
using Stroll.Alpha.Market.Contracts;

namespace Stroll.Alpha.Market.Services;

/// <summary>
/// Market data query service for backtesting.
/// Provides historical options chains and underlying data from 0-45 DTE.
/// </summary>
public sealed class MarketQueryService : IDisposable
{
    private readonly ThetaDbSource _dataSource;
    private readonly DatasetStore _store;

    public MarketQueryService(DatasetConfig? cfg = null)
    {
        cfg ??= DatasetConfig.Default;
        _dataSource = new ThetaDbSource();
        _store = new DatasetStore(_dataSource, _dataSource, cfg);
    }

    /// <summary>
    /// Get options chain summary for backtesting
    /// </summary>
    public async Task<ChainSummary> GetChainAsync(ChainRequest req, CancellationToken ct)
    {
        var completeness = await _store.CheckChainCompletenessAsync(
            req.Symbol, 
            req.AtUtc, 
            req.Moneyness, 
            req.DteMin, 
            req.DteMax, 
            ct);

        return new ChainSummary(
            req.Symbol, 
            req.AtUtc, 
            completeness.StrikesLeft + completeness.StrikesRight, 
            completeness.Score, 
            Array.Empty<object>());
    }

    /// <summary>
    /// Get full options chain data for backtesting
    /// </summary>
    public async Task<IReadOnlyList<OptionRecord>> GetOptionsSnapshotAsync(
        string symbol,
        DateTime atUtc,
        decimal moneynessWindow = 0.15m,
        int dteMin = 0,
        int dteMax = 45,
        CancellationToken ct = default)
    {
        return await _dataSource.GetSnapshotAsync(symbol, atUtc, moneynessWindow, dteMin, dteMax, ct)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Get underlying price bars for backtesting
    /// </summary>
    public async Task<IReadOnlyList<UnderlyingBar>> GetUnderlyingBarsAsync(
        string symbol,
        DateTime fromUtc,
        DateTime toUtc,
        string interval = "1m",
        CancellationToken ct = default)
    {
        return await _dataSource.GetBarsAsync(symbol, fromUtc, toUtc, interval, ct)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Get available expiration dates
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> GetAvailableExpiriesAsync(
        string symbol,
        DateTime asOfUtc,
        int maxDte = 45,
        CancellationToken ct = default)
    {
        return await _dataSource.GetExpiriesAsync(symbol, asOfUtc, maxDte, ct);
    }

    /// <summary>
    /// Get underlying price at a specific timestamp
    /// </summary>
    public async Task<decimal?> GetUnderlyingPriceAsync(
        string symbol,
        DateTime atUtc,
        CancellationToken ct = default)
    {
        var bars = await GetUnderlyingBarsAsync(symbol, atUtc.AddMinutes(-5), atUtc, "1m", ct);
        return bars.LastOrDefault()?.Close;
    }

    /// <summary>
    /// Check data quality and completeness
    /// </summary>
    public async Task<CompletenessReport> CheckDataQualityAsync(
        string symbol,
        DateTime atUtc,
        decimal moneynessWindow = 0.15m,
        int dteMin = 0,
        int dteMax = 45,
        CancellationToken ct = default)
    {
        return await _store.CheckChainCompletenessAsync(symbol, atUtc, moneynessWindow, dteMin, dteMax, ct);
    }

    public void Dispose()
    {
        _dataSource?.Dispose();
    }
}
