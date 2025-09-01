/*
using Microsoft.Data.Sqlite;
using Stroll.Alpha.Dataset.Models;
using Stroll.Alpha.Dataset.Storage;

namespace Stroll.Alpha.Dataset.Probes;

public sealed class ChainCompletenessProbe : IDisposable
{
    private readonly SqliteDataProvider _provider;
    private readonly SqliteConnection _connection;

    public ChainCompletenessProbe(string dbPath)
    {
        _provider = new SqliteDataProvider(dbPath);
        _connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared");
        _connection.Open();
    }

    public async Task<ChainProbeReport> ProbeAsync(
        string symbol,
        DateTime tsUtc,
        ProbeDepth depth = ProbeDepth.Standard,
        CancellationToken ct = default)
    {
        var report = new ChainProbeReport
        {
            Symbol = symbol,
            Timestamp = tsUtc,
            SessionDate = DateOnly.FromDateTime(tsUtc.Date)
        };

        // Check underlying data
        await ProbeUnderlyingDataAsync(report, ct);

        // Check options chain coverage
        await ProbeOptionsChainAsync(report, depth, ct);

        // Check greeks completeness
        await ProbeGreeksAsync(report, ct);

        // Check data freshness
        await ProbeDataFreshnessAsync(report, ct);

        // Calculate overall score
        report.OverallScore = CalculateOverallScore(report);

        // Log probe results
        await LogProbeResultAsync(report, ct);

        return report;
    }

    private async Task ProbeUnderlyingDataAsync(ChainProbeReport report, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                COUNT(*) as bar_count,
                MIN(ts_utc) as first_bar,
                MAX(ts_utc) as last_bar,
                COUNT(DISTINCT DATE(ts_utc)) as trading_days
            FROM underlying_bars
            WHERE symbol = @symbol
                AND DATE(ts_utc) = DATE(@ts_utc)";

        cmd.Parameters.AddWithValue("@symbol", report.Symbol);
        cmd.Parameters.AddWithValue("@ts_utc", report.Timestamp);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var barCount = reader.GetInt32(0);
            report.UnderlyingBarsFound = barCount;

            if (barCount > 0)
            {
                var expectedBars = 390; // 6.5 hours * 60 minutes for regular trading hours
                report.UnderlyingCompleteness = Math.Min(1m, barCount / (decimal)expectedBars);
                
                if (barCount < expectedBars * 0.9m)
                {
                    report.AddWarning($"Incomplete underlying data: {barCount}/{expectedBars} bars");
                }
            }
            else
            {
                report.UnderlyingCompleteness = 0;
                report.AddError("No underlying bar data found");
            }
        }
    }

    private async Task ProbeOptionsChainAsync(ChainProbeReport report, ProbeDepth depth, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            WITH chain_stats AS (
                SELECT 
                    expiry_date,
                    dte,
                    COUNT(DISTINCT CASE WHEN right = 'P' THEN strike END) as put_strikes,
                    COUNT(DISTINCT CASE WHEN right = 'C' THEN strike END) as call_strikes,
                    MIN(CASE WHEN right = 'P' THEN moneyness END) as min_put_moneyness,
                    MAX(CASE WHEN right = 'P' THEN moneyness END) as max_put_moneyness,
                    MIN(CASE WHEN right = 'C' THEN moneyness END) as min_call_moneyness,
                    MAX(CASE WHEN right = 'C' THEN moneyness END) as max_call_moneyness,
                    SUM(CASE WHEN bid IS NULL OR ask IS NULL THEN 1 ELSE 0 END) as missing_quotes,
                    SUM(CASE WHEN iv IS NULL THEN 1 ELSE 0 END) as missing_iv,
                    COUNT(*) as total_contracts
                FROM options_chain
                WHERE symbol = @symbol
                    AND session_date = DATE(@ts_utc)
                    AND dte >= 0
                    AND dte <= 45
                GROUP BY expiry_date, dte
            )
            SELECT * FROM chain_stats ORDER BY dte";

        cmd.Parameters.AddWithValue("@symbol", report.Symbol);
        cmd.Parameters.AddWithValue("@ts_utc", report.Timestamp);

        var expiryReports = new List<ExpiryReport>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        
        while (await reader.ReadAsync(ct))
        {
            var expiry = DateOnly.Parse(reader.GetString(0));
            var dte = reader.GetInt32(1);
            var putStrikes = reader.GetInt32(2);
            var callStrikes = reader.GetInt32(3);
            var totalContracts = reader.GetInt32(10);

            var expiryReport = new ExpiryReport
            {
                Expiry = expiry,
                DTE = dte,
                PutStrikes = putStrikes,
                CallStrikes = callStrikes,
                TotalContracts = totalContracts
            };

            // Check strike coverage based on depth
            var requiredStrikes = depth switch
            {
                ProbeDepth.Minimal => 10,
                ProbeDepth.Standard => 20,
                ProbeDepth.Deep => 30,
                ProbeDepth.Complete => 50,
                _ => 20
            };

            if (putStrikes < requiredStrikes)
            {
                expiryReport.Issues.Add($"Insufficient put strikes: {putStrikes}/{requiredStrikes}");
            }

            if (callStrikes < requiredStrikes)
            {
                expiryReport.Issues.Add($"Insufficient call strikes: {callStrikes}/{requiredStrikes}");
            }

            // Check moneyness coverage
            if (!reader.IsDBNull(4))
            {
                var minPutMoneyness = reader.GetDecimal(4);
                var maxPutMoneyness = reader.GetDecimal(5);
                var minCallMoneyness = reader.GetDecimal(6);
                var maxCallMoneyness = reader.GetDecimal(7);

                if (maxPutMoneyness < 0.85m || minCallMoneyness > 1.15m)
                {
                    expiryReport.Issues.Add("Incomplete moneyness coverage");
                }
            }

            // Check data quality
            var missingQuotes = reader.GetInt32(8);
            var missingIV = reader.GetInt32(9);

            if (missingQuotes > totalContracts * 0.1)
            {
                expiryReport.Issues.Add($"Missing quotes: {missingQuotes}/{totalContracts}");
            }

            if (missingIV > totalContracts * 0.2)
            {
                expiryReport.Issues.Add($"Missing IV: {missingIV}/{totalContracts}");
            }

            expiryReport.Score = CalculateExpiryScore(expiryReport, requiredStrikes);
            expiryReports.Add(expiryReport);
        }

        report.ExpiryReports = expiryReports;
        report.TotalExpiries = expiryReports.Count;
        
        // Check DTE coverage
        var dteGaps = FindDTEGaps(expiryReports);
        if (dteGaps.Any())
        {
            report.AddWarning($"Missing DTEs: {string.Join(", ", dteGaps)}");
        }

        report.ChainCompleteness = expiryReports.Any() 
            ? expiryReports.Average(e => e.Score) 
            : 0;
    }

    private async Task ProbeGreeksAsync(ChainProbeReport report, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                COUNT(*) as total_contracts,
                SUM(CASE WHEN delta IS NULL THEN 1 ELSE 0 END) as missing_delta,
                SUM(CASE WHEN gamma IS NULL THEN 1 ELSE 0 END) as missing_gamma,
                SUM(CASE WHEN theta IS NULL THEN 1 ELSE 0 END) as missing_theta,
                SUM(CASE WHEN vega IS NULL THEN 1 ELSE 0 END) as missing_vega,
                SUM(CASE WHEN iv IS NULL THEN 1 ELSE 0 END) as missing_iv
            FROM options_chain
            WHERE symbol = @symbol
                AND session_date = DATE(@ts_utc)
                AND dte <= 45";

        cmd.Parameters.AddWithValue("@symbol", report.Symbol);
        cmd.Parameters.AddWithValue("@ts_utc", report.Timestamp);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var total = reader.GetInt32(0);
            if (total > 0)
            {
                var missingDelta = reader.GetInt32(1);
                var missingGamma = reader.GetInt32(2);
                var missingTheta = reader.GetInt32(3);
                var missingVega = reader.GetInt32(4);
                var missingIV = reader.GetInt32(5);

                report.GreeksCompleteness = 1m - (missingDelta + missingGamma + missingTheta + missingVega) / (4m * total);

                if (missingDelta > total * 0.1) report.AddWarning($"Missing delta: {missingDelta}/{total}");
                if (missingGamma > total * 0.1) report.AddWarning($"Missing gamma: {missingGamma}/{total}");
                if (missingTheta > total * 0.1) report.AddWarning($"Missing theta: {missingTheta}/{total}");
                if (missingVega > total * 0.1) report.AddWarning($"Missing vega: {missingVega}/{total}");
                if (missingIV > total * 0.1) report.AddWarning($"Missing IV: {missingIV}/{total}");
            }
        }
    }

    private async Task ProbeDataFreshnessAsync(ChainProbeReport report, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                MAX(o.ts_utc) as latest_option_ts,
                MAX(b.ts_utc) as latest_bar_ts
            FROM options_chain o
            CROSS JOIN underlying_bars b
            WHERE o.symbol = @symbol
                AND b.symbol = @symbol
                AND DATE(o.ts_utc) = DATE(@ts_utc)
                AND DATE(b.ts_utc) = DATE(@ts_utc)";

        cmd.Parameters.AddWithValue("@symbol", report.Symbol);
        cmd.Parameters.AddWithValue("@ts_utc", report.Timestamp);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct) && !reader.IsDBNull(0))
        {
            var latestOption = reader.GetDateTime(0);
            var latestBar = reader.GetDateTime(1);

            var optionLag = report.Timestamp - latestOption;
            var barLag = report.Timestamp - latestBar;

            if (optionLag.TotalMinutes > 15)
            {
                report.AddWarning($"Stale options data: {optionLag.TotalMinutes:F0} minutes old");
            }

            if (barLag.TotalMinutes > 5)
            {
                report.AddWarning($"Stale bar data: {barLag.TotalMinutes:F0} minutes old");
            }

            report.DataFreshness = Math.Max(0, 1m - (decimal)optionLag.TotalMinutes / 60);
        }
    }

    private List<int> FindDTEGaps(List<ExpiryReport> expiries)
    {
        var gaps = new List<int>();
        var dtes = expiries.Select(e => e.DTE).OrderBy(d => d).ToList();
        
        if (!dtes.Any()) return Enumerable.Range(0, 8).ToList(); // First week critical

        for (int expectedDte = 0; expectedDte <= Math.Min(7, 45); expectedDte++)
        {
            if (!dtes.Contains(expectedDte))
            {
                gaps.Add(expectedDte);
            }
        }

        return gaps;
    }

    private decimal CalculateExpiryScore(ExpiryReport expiry, int requiredStrikes)
    {
        decimal strikeScore = Math.Min(1m, (expiry.PutStrikes + expiry.CallStrikes) / (2m * requiredStrikes));
        decimal balanceScore = 1m - Math.Abs(expiry.PutStrikes - expiry.CallStrikes) / 
            (decimal)Math.Max(expiry.PutStrikes, expiry.CallStrikes);
        decimal qualityScore = expiry.Issues.Count == 0 ? 1m : Math.Max(0, 1m - expiry.Issues.Count * 0.2m);

        return (strikeScore * 0.5m + balanceScore * 0.25m + qualityScore * 0.25m);
    }

    private decimal CalculateOverallScore(ChainProbeReport report)
    {
        return report.UnderlyingCompleteness * 0.2m +
               report.ChainCompleteness * 0.4m +
               report.GreeksCompleteness * 0.2m +
               report.DataFreshness * 0.2m;
    }

    private async Task LogProbeResultAsync(ChainProbeReport report, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO probe_results (
                probe_id, symbol, probe_type, probe_date, ts_utc,
                score, status, missing_strikes, total_expected, warnings, metadata
            ) VALUES (
                @probe_id, @symbol, 'CHAIN_COMPLETENESS', @probe_date, @ts_utc,
                @score, @status, @missing_strikes, @total_expected, @warnings, @metadata
            )";

        var status = report.OverallScore switch
        {
            >= 0.9m => "PASS",
            >= 0.7m => "WARN",
            _ => "FAIL"
        };

        cmd.Parameters.AddWithValue("@probe_id", Guid.NewGuid().ToString());
        cmd.Parameters.AddWithValue("@symbol", report.Symbol);
        cmd.Parameters.AddWithValue("@probe_date", report.SessionDate.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@ts_utc", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("@score", report.OverallScore * 100);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@missing_strikes", 0); // Calculate if needed
        cmd.Parameters.AddWithValue("@total_expected", report.TotalExpiries * 40); // Approximate
        cmd.Parameters.AddWithValue("@warnings", string.Join("; ", report.Warnings));
        cmd.Parameters.AddWithValue("@metadata", System.Text.Json.JsonSerializer.Serialize(report));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _connection?.Dispose();
    }
}

public enum ProbeDepth
{
    Minimal,   // Quick check, minimal validation
    Standard,  // Default depth
    Deep,      // Thorough validation
    Complete   // Full audit
}

public sealed class ChainProbeReport
{
    public required string Symbol { get; init; }
    public required DateTime Timestamp { get; init; }
    public required DateOnly SessionDate { get; init; }
    
    public decimal OverallScore { get; set; }
    public decimal UnderlyingCompleteness { get; set; }
    public decimal ChainCompleteness { get; set; }
    public decimal GreeksCompleteness { get; set; }
    public decimal DataFreshness { get; set; }
    
    public int UnderlyingBarsFound { get; set; }
    public int TotalExpiries { get; set; }
    public List<ExpiryReport> ExpiryReports { get; set; } = new();
    
    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();
    
    public void AddWarning(string warning) => Warnings.Add(warning);
    public void AddError(string error) => Errors.Add(error);
}

public sealed class ExpiryReport
{
    public required DateOnly Expiry { get; init; }
    public required int DTE { get; init; }
    public required int PutStrikes { get; init; }
    public required int CallStrikes { get; init; }
    public required int TotalContracts { get; init; }
    public decimal Score { get; set; }
    public List<string> Issues { get; } = new();
}
*/