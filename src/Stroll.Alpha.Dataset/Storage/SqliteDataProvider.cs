using System.Data;
using Microsoft.Data.Sqlite;
using Stroll.Alpha.Dataset.Models;
using Stroll.Alpha.Dataset.Sources;

namespace Stroll.Alpha.Dataset.Storage;

public sealed class SqliteDataProvider : IOptionsSource, IBarSource, IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public SqliteDataProvider(string dbPath)
    {
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
        _connection = new SqliteConnection(_connectionString);
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        var schemaPath = Path.Combine(Path.GetDirectoryName(_connectionString.Replace("Data Source=", "").Split(';')[0])!, "..", "schema.sql");
        if (File.Exists(schemaPath))
        {
            var schema = File.ReadAllText(schemaPath);
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = schema;
            cmd.ExecuteNonQuery();
        }
    }

    public async IAsyncEnumerable<OptionRecord> GetSnapshotAsync(
        string symbol, 
        DateTime tsUtc, 
        decimal moneynessWindow, 
        int dteMin, 
        int dteMax, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            // Get underlying price at timestamp
            decimal underlyingPrice = await GetUnderlyingPriceAsync(symbol, tsUtc, ct);
            if (underlyingPrice == 0) yield break;

            decimal minMoneyness = 1 - moneynessWindow;
            decimal maxMoneyness = 1 + moneynessWindow;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT 
                    symbol, session_date, ts_utc, expiry_date, strike, right,
                    bid, ask, mid, last, iv, delta, gamma, theta, vega, 
                    open_interest, volume
                FROM options_chain
                WHERE symbol = @symbol
                    AND ts_utc <= @ts_utc
                    AND dte >= @dte_min
                    AND dte <= @dte_max
                    AND moneyness >= @min_moneyness
                    AND moneyness <= @max_moneyness
                    AND ts_utc = (
                        SELECT MAX(ts_utc) 
                        FROM options_chain oc2
                        WHERE oc2.symbol = options_chain.symbol
                            AND oc2.expiry_date = options_chain.expiry_date
                            AND oc2.strike = options_chain.strike
                            AND oc2.right = options_chain.right
                            AND oc2.ts_utc <= @ts_utc
                    )
                ORDER BY expiry_date, strike, right";

            cmd.Parameters.AddWithValue("@symbol", symbol);
            cmd.Parameters.AddWithValue("@ts_utc", tsUtc);
            cmd.Parameters.AddWithValue("@dte_min", dteMin);
            cmd.Parameters.AddWithValue("@dte_max", dteMax);
            cmd.Parameters.AddWithValue("@min_moneyness", minMoneyness);
            cmd.Parameters.AddWithValue("@max_moneyness", maxMoneyness);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                yield return new OptionRecord(
                    Symbol: reader.GetString(0),
                    Session: DateOnly.Parse(reader.GetString(1)),
                    TsUtc: reader.GetDateTime(2),
                    Expiry: DateOnly.Parse(reader.GetString(3)),
                    Strike: reader.GetDecimal(4),
                    Right: reader.GetString(5) == "C" ? Right.Call : Right.Put,
                    Bid: reader.IsDBNull(6) ? 0 : reader.GetDecimal(6),
                    Ask: reader.IsDBNull(7) ? 0 : reader.GetDecimal(7),
                    Mid: reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    Last: reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                    Iv: reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                    Delta: reader.IsDBNull(11) ? null : reader.GetDouble(11),
                    Gamma: reader.IsDBNull(12) ? null : reader.GetDouble(12),
                    Theta: reader.IsDBNull(13) ? null : reader.GetDouble(13),
                    Vega: reader.IsDBNull(14) ? null : reader.GetDouble(14),
                    Oi: reader.IsDBNull(15) ? null : reader.GetInt64(15),
                    Volume: reader.IsDBNull(16) ? null : reader.GetInt64(16)
                );
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<DateOnly>> GetExpiriesAsync(
        string symbol, 
        DateTime asOfUtc, 
        int dteMax, 
        CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT DISTINCT expiry_date
                FROM options_chain
                WHERE symbol = @symbol
                    AND session_date = date(@as_of)
                    AND dte <= @dte_max
                ORDER BY expiry_date";

            cmd.Parameters.AddWithValue("@symbol", symbol);
            cmd.Parameters.AddWithValue("@as_of", asOfUtc.Date);
            cmd.Parameters.AddWithValue("@dte_max", dteMax);

            var expiries = new List<DateOnly>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                expiries.Add(DateOnly.Parse(reader.GetString(0)));
            }
            return expiries;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async IAsyncEnumerable<UnderlyingBar> GetBarsAsync(
        string symbol, 
        DateTime fromUtc, 
        DateTime toUtc, 
        string interval, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            
            // Support different intervals (1m, 5m, 15m, 1h, 1d)
            var groupBy = interval switch
            {
                "1m" => "",
                "5m" => "GROUP BY symbol, datetime(ts_utc, '-'||((strftime('%M', ts_utc) % 5))||' minutes')",
                "15m" => "GROUP BY symbol, datetime(ts_utc, '-'||((strftime('%M', ts_utc) % 15))||' minutes')",
                "1h" => "GROUP BY symbol, strftime('%Y-%m-%d %H:00:00', ts_utc)",
                "1d" => "GROUP BY symbol, date(ts_utc)",
                _ => ""
            };

            if (string.IsNullOrEmpty(groupBy))
            {
                cmd.CommandText = @"
                    SELECT symbol, ts_utc, open, high, low, close, volume
                    FROM underlying_bars
                    WHERE symbol = @symbol
                        AND ts_utc >= @from_utc
                        AND ts_utc <= @to_utc
                    ORDER BY ts_utc";
            }
            else
            {
                cmd.CommandText = $@"
                    SELECT 
                        symbol,
                        MIN(ts_utc) as ts_utc,
                        (SELECT open FROM underlying_bars b2 WHERE b2.symbol = b.symbol AND b2.ts_utc = MIN(b.ts_utc)) as open,
                        MAX(high) as high,
                        MIN(low) as low,
                        (SELECT close FROM underlying_bars b3 WHERE b3.symbol = b.symbol AND b3.ts_utc = MAX(b.ts_utc)) as close,
                        SUM(volume) as volume
                    FROM underlying_bars b
                    WHERE symbol = @symbol
                        AND ts_utc >= @from_utc
                        AND ts_utc <= @to_utc
                    {groupBy}
                    ORDER BY ts_utc";
            }

            cmd.Parameters.AddWithValue("@symbol", symbol);
            cmd.Parameters.AddWithValue("@from_utc", fromUtc);
            cmd.Parameters.AddWithValue("@to_utc", toUtc);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                yield return new UnderlyingBar(
                    Symbol: reader.GetString(0),
                    TsUtc: reader.GetDateTime(1),
                    Open: reader.GetDecimal(2),
                    High: reader.GetDecimal(3),
                    Low: reader.GetDecimal(4),
                    Close: reader.GetDecimal(5),
                    Volume: reader.GetInt64(6)
                );
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<decimal> GetUnderlyingPriceAsync(string symbol, DateTime tsUtc, CancellationToken ct)
    {
        using var cmd = _connection.CreateCommand();
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
        return result != null ? Convert.ToDecimal(result) : 0;
    }

    public async Task<ProbeResult> ProbeDataCompletenessAsync(
        string symbol,
        DateTime tsUtc,
        CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var sessionDate = DateOnly.FromDateTime(tsUtc.Date);
            
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT 
                    COUNT(DISTINCT CASE WHEN right = 'P' THEN strike END) as put_strikes,
                    COUNT(DISTINCT CASE WHEN right = 'C' THEN strike END) as call_strikes,
                    COUNT(*) as total_records,
                    MIN(dte) as min_dte,
                    MAX(dte) as max_dte
                FROM options_chain
                WHERE symbol = @symbol
                    AND session_date = @session_date
                    AND dte <= 45";

            cmd.Parameters.AddWithValue("@symbol", symbol);
            cmd.Parameters.AddWithValue("@session_date", sessionDate.ToString("yyyy-MM-dd"));

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                var putStrikes = reader.GetInt32(0);
                var callStrikes = reader.GetInt32(1);
                var totalRecords = reader.GetInt32(2);
                var minDte = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                var maxDte = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);

                var warnings = new List<string>();
                
                // Check for minimum strike coverage
                if (putStrikes < 20) warnings.Add($"Low put strike coverage: {putStrikes}");
                if (callStrikes < 20) warnings.Add($"Low call strike coverage: {callStrikes}");
                
                // Check DTE coverage
                if (maxDte < 45) warnings.Add($"Missing long-dated options: max DTE = {maxDte}");
                
                var score = CalculateCompletenessScore(putStrikes, callStrikes, minDte, maxDte);
                
                return new ProbeResult
                {
                    Symbol = symbol,
                    SessionDate = sessionDate,
                    Score = score,
                    StrikesLeft = putStrikes,
                    StrikesRight = callStrikes,
                    Warnings = warnings.ToArray()
                };
            }

            return new ProbeResult
            {
                Symbol = symbol,
                SessionDate = sessionDate,
                Score = 0,
                StrikesLeft = 0,
                StrikesRight = 0,
                Warnings = new[] { "No data found" }
            };
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private decimal CalculateCompletenessScore(int putStrikes, int callStrikes, int minDte, int maxDte)
    {
        decimal strikeScore = Math.Min(1m, (putStrikes + callStrikes) / 40m);
        decimal dteScore = Math.Min(1m, (maxDte - minDte + 1) / 46m);
        decimal balanceScore = 1m - Math.Abs(putStrikes - callStrikes) / (decimal)Math.Max(putStrikes, callStrikes);
        
        return (strikeScore * 0.4m + dteScore * 0.3m + balanceScore * 0.3m) * 100;
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _semaphore?.Dispose();
    }
}

public record ProbeResult
{
    public required string Symbol { get; init; }
    public required DateOnly SessionDate { get; init; }
    public required decimal Score { get; init; }
    public required int StrikesLeft { get; init; }
    public required int StrikesRight { get; init; }
    public required string[] Warnings { get; init; }
}