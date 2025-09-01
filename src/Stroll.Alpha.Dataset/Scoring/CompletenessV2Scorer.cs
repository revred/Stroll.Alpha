using Microsoft.Data.Sqlite;
using Stroll.Alpha.Dataset.Models;

namespace Stroll.Alpha.Dataset.Scoring;

/// <summary>
/// Enhanced completeness scoring v2 per Steering.md requirements
/// </summary>
public sealed class CompletenessV2Scorer
{
    /// <summary>
    /// Calculate v2 completeness score with enhanced criteria
    /// Base score per DTE bucket (0-1):
    /// +0.4 if ≥3 strikes/side within ±5% moneyness
    /// +0.2 if bid+ask present for ≥80% included strikes  
    /// +0.2 if ATM spread ≤ X bp percentile for that symbol/day
    /// +0.2 if OI or Vol present for ≥70% of strikes
    /// Final score = mean across active buckets in view
    /// Below 0.9 → emit actionable hints
    /// </summary>
    public static async Task<CompletenessV2Report> ScoreAsync(
        string symbol,
        DateTime tsUtc,
        decimal moneynessWindow = 0.15m,
        int dteMin = 0,
        int dteMax = 45,
        string dbPath = "",
        CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
        await connection.OpenAsync(ct);

        var report = new CompletenessV2Report
        {
            Symbol = symbol,
            Timestamp = tsUtc,
            SessionDate = DateOnly.FromDateTime(tsUtc),
            MoneynessWindow = moneynessWindow,
            DteMin = dteMin,
            DteMax = dteMax
        };

        // Get underlying price for moneyness calculations
        var underlyingPrice = await GetUnderlyingPriceAsync(connection, symbol, tsUtc, ct);
        if (underlyingPrice == 0)
        {
            report.Hints.Add("Missing underlying price data - cannot calculate moneyness");
            return report;
        }

        // Get active DTE buckets
        var dteBuckets = await GetActiveDteBucketsAsync(connection, symbol, tsUtc, dteMin, dteMax, ct);
        if (!dteBuckets.Any())
        {
            report.Hints.Add($"No active DTE buckets found for {symbol} on {tsUtc:yyyy-MM-dd}");
            return report;
        }

        report.ActiveDteBuckets = dteBuckets.Count;

        // Score each DTE bucket
        var bucketScores = new List<double>();
        
        foreach (var dte in dteBuckets)
        {
            var bucketScore = await ScoreDteBucketAsync(
                connection, symbol, tsUtc, dte, underlyingPrice, moneynessWindow, ct);
            bucketScores.Add(bucketScore.TotalScore);
            report.DteBucketScores[dte] = bucketScore;
        }

        // Calculate final score
        report.FinalScore = bucketScores.Average();
        
        // Generate actionable hints for scores below 0.9
        if (report.FinalScore < 0.9)
        {
            GenerateActionableHints(report);
        }

        return report;
    }

    private static async Task<decimal> GetUnderlyingPriceAsync(
        SqliteConnection connection, 
        string symbol, 
        DateTime tsUtc, 
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT close 
            FROM underlying_bars 
            WHERE symbol = @symbol 
                AND ts_utc <= @ts_utc 
            ORDER BY ts_utc DESC 
            LIMIT 1";

        cmd.Parameters.AddWithValue("@symbol", symbol);
        cmd.Parameters.AddWithValue("@ts_utc", tsUtc);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is decimal price ? price : 0m;
    }

    private static async Task<List<int>> GetActiveDteBucketsAsync(
        SqliteConnection connection,
        string symbol,
        DateTime tsUtc,
        int dteMin,
        int dteMax,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT dte 
            FROM options_chain 
            WHERE symbol = @symbol 
                AND session_date = DATE(@ts_utc)
                AND dte >= @dte_min AND dte <= @dte_max
            ORDER BY dte";

        cmd.Parameters.AddWithValue("@symbol", symbol);
        cmd.Parameters.AddWithValue("@ts_utc", tsUtc);
        cmd.Parameters.AddWithValue("@dte_min", dteMin);
        cmd.Parameters.AddWithValue("@dte_max", dteMax);

        var buckets = new List<int>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            buckets.Add(reader.GetInt32(0));
        }

        return buckets;
    }

    private static async Task<DteBucketScore> ScoreDteBucketAsync(
        SqliteConnection connection,
        string symbol,
        DateTime tsUtc,
        int dte,
        decimal underlyingPrice,
        decimal moneynessWindow,
        CancellationToken ct)
    {
        var score = new DteBucketScore { DTE = dte };

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                right,
                strike,
                bid,
                ask,
                open_interest,
                volume,
                ABS(strike / @underlying_price - 1.0) as moneyness_abs
            FROM options_chain 
            WHERE symbol = @symbol 
                AND session_date = DATE(@ts_utc)
                AND dte = @dte
                AND ABS(strike / @underlying_price - 1.0) <= @moneyness_window
            ORDER BY moneyness_abs";

        cmd.Parameters.AddWithValue("@symbol", symbol);
        cmd.Parameters.AddWithValue("@ts_utc", tsUtc);
        cmd.Parameters.AddWithValue("@dte", dte);
        cmd.Parameters.AddWithValue("@underlying_price", underlyingPrice);
        cmd.Parameters.AddWithValue("@moneyness_window", moneynessWindow);

        var strikes = new List<StrikeInfo>();
        var atmStrikes = new List<StrikeInfo>(); // Within ±5% for ATM density check

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var strikeInfo = new StrikeInfo
            {
                Right = reader.GetString(0),  // right
                Strike = reader.GetDecimal(1), // strike  
                Bid = reader.IsDBNull(2) ? null : reader.GetDecimal(2), // bid
                Ask = reader.IsDBNull(3) ? null : reader.GetDecimal(3), // ask
                OpenInterest = reader.IsDBNull(4) ? null : reader.GetInt64(4), // open_interest
                Volume = reader.IsDBNull(5) ? null : reader.GetInt64(5), // volume
                MoneynessAbs = reader.GetDouble(6) // moneyness_abs
            };

            strikes.Add(strikeInfo);

            // ATM strikes within ±5%
            if (strikeInfo.MoneynessAbs <= 0.05)
                atmStrikes.Add(strikeInfo);
        }

        // Score component 1: ≥3 strikes/side within ±5% moneyness (+0.4)
        var atmPuts = atmStrikes.Count(s => s.Right == "P");
        var atmCalls = atmStrikes.Count(s => s.Right == "C");
        if (atmPuts >= 3 && atmCalls >= 3)
        {
            score.StrikeDensityScore = 0.4;
        }

        // Score component 2: bid+ask present for ≥80% included strikes (+0.2)  
        var quotedStrikes = strikes.Count(s => s.Bid.HasValue && s.Ask.HasValue);
        var quoteCoverage = strikes.Count > 0 ? (double)quotedStrikes / strikes.Count : 0;
        if (quoteCoverage >= 0.8)
        {
            score.QuoteCoverageScore = 0.2;
        }
        score.QuoteCoverage = quoteCoverage;

        // Score component 3: ATM spread ≤ percentile (+0.2)
        // Simplified: average spread of ATM strikes should be reasonable
        var atmSpreads = atmStrikes
            .Where(s => s.Bid.HasValue && s.Ask.HasValue)
            .Select(s => (double)(s.Ask!.Value - s.Bid!.Value))
            .ToList();
        
        if (atmSpreads.Any())
        {
            var avgSpread = atmSpreads.Average();
            var avgPrice = atmStrikes
                .Where(s => s.Bid.HasValue && s.Ask.HasValue)
                .Average(s => (double)((s.Bid!.Value + s.Ask!.Value) / 2));
            
            var spreadBps = avgPrice > 0 ? (avgSpread / avgPrice) * 10000 : 0;
            
            // Reasonable spread threshold: < 100 bps for ATM options
            if (spreadBps < 100)
            {
                score.SpreadScore = 0.2;
            }
            score.AverageSpreadBps = spreadBps;
        }

        // Score component 4: OI or Vol present for ≥70% of strikes (+0.2)
        var liquidStrikes = strikes.Count(s => 
            (s.OpenInterest.HasValue && s.OpenInterest > 0) || 
            (s.Volume.HasValue && s.Volume > 0));
        var liquidityCoverage = strikes.Count > 0 ? (double)liquidStrikes / strikes.Count : 0;
        if (liquidityCoverage >= 0.7)
        {
            score.LiquidityScore = 0.2;
        }
        score.LiquidityCoverage = liquidityCoverage;

        score.TotalStrikes = strikes.Count;
        score.TotalScore = score.StrikeDensityScore + score.QuoteCoverageScore + 
                          score.SpreadScore + score.LiquidityScore;

        return score;
    }

    private static void GenerateActionableHints(CompletenessV2Report report)
    {
        foreach (var (dte, bucketScore) in report.DteBucketScores)
        {
            if (bucketScore.StrikeDensityScore < 0.4)
            {
                report.Hints.Add($"DTE {dte}: Need ≥3 put and call strikes within ±5% ATM");
            }
            
            if (bucketScore.QuoteCoverageScore < 0.2)
            {
                report.Hints.Add($"DTE {dte}: Need bid/ask quotes for ≥80% of strikes (current: {bucketScore.QuoteCoverage:P1})");
            }
            
            if (bucketScore.SpreadScore < 0.2)
            {
                report.Hints.Add($"DTE {dte}: ATM spreads too wide ({bucketScore.AverageSpreadBps:F0} bps, target <100 bps)");
            }
            
            if (bucketScore.LiquidityScore < 0.2)
            {
                report.Hints.Add($"DTE {dte}: Need OI/volume for ≥70% of strikes (current: {bucketScore.LiquidityCoverage:P1})");
            }
        }

        if (report.ActiveDteBuckets < 3)
        {
            report.Hints.Add($"Increase DTE range - only {report.ActiveDteBuckets} active buckets found");
        }
    }
}

public sealed record CompletenessV2Report
{
    public required string Symbol { get; init; }
    public required DateTime Timestamp { get; init; }
    public required DateOnly SessionDate { get; init; }
    public decimal MoneynessWindow { get; init; }
    public int DteMin { get; init; }
    public int DteMax { get; init; }
    public int ActiveDteBuckets { get; set; }
    public double FinalScore { get; set; }
    public Dictionary<int, DteBucketScore> DteBucketScores { get; init; } = new();
    public List<string> Hints { get; init; } = new();
}

public sealed record DteBucketScore
{
    public int DTE { get; init; }
    public double StrikeDensityScore { get; set; }    // 0.0 or 0.4
    public double QuoteCoverageScore { get; set; }    // 0.0 or 0.2
    public double SpreadScore { get; set; }           // 0.0 or 0.2  
    public double LiquidityScore { get; set; }        // 0.0 or 0.2
    public double TotalScore { get; set; }            // Sum of above (0.0-1.0)
    public int TotalStrikes { get; set; }
    public double QuoteCoverage { get; set; }
    public double LiquidityCoverage { get; set; }
    public double AverageSpreadBps { get; set; }
}

public sealed record StrikeInfo
{
    public required string Right { get; init; }
    public decimal Strike { get; init; }
    public decimal? Bid { get; init; }
    public decimal? Ask { get; init; }
    public long? OpenInterest { get; init; }
    public long? Volume { get; init; }
    public double MoneynessAbs { get; init; }
}