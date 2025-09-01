using System.Data;
using Microsoft.Data.Sqlite;
using Stroll.Alpha.Dataset.Models;

namespace Stroll.Alpha.Dataset.Ingestion;

public sealed class DataIngestionService : IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public DataIngestionService(string dbPath)
    {
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
        _connection = new SqliteConnection(_connectionString);
        _connection.Open();
    }

    public async Task<IngestionResult> IngestOptionsChainAsync(
        IEnumerable<OptionRecord> records,
        string ingestId,
        CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        var transaction = _connection.BeginTransaction();
        
        try
        {
            var startTime = DateTime.UtcNow;
            int processed = 0;
            int skipped = 0;
            
            // Log ingestion start
            await LogIngestionStartAsync(ingestId, records.First().Symbol, startTime, transaction);

            using var insertCmd = _connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = @"
                INSERT OR REPLACE INTO options_chain (
                    symbol, session_date, ts_utc, expiry_date, strike, right,
                    bid, ask, mid, last, iv, delta, gamma, theta, vega,
                    open_interest, volume, moneyness
                ) VALUES (
                    @symbol, @session_date, @ts_utc, @expiry_date, @strike, @right,
                    @bid, @ask, @mid, @last, @iv, @delta, @gamma, @theta, @vega,
                    @open_interest, @volume, @moneyness
                )";

            // Prepare parameters once
            var parameters = new[]
            {
                insertCmd.Parameters.Add("@symbol", SqliteType.Text),
                insertCmd.Parameters.Add("@session_date", SqliteType.Text),
                insertCmd.Parameters.Add("@ts_utc", SqliteType.Text),
                insertCmd.Parameters.Add("@expiry_date", SqliteType.Text),
                insertCmd.Parameters.Add("@strike", SqliteType.Real),
                insertCmd.Parameters.Add("@right", SqliteType.Text),
                insertCmd.Parameters.Add("@bid", SqliteType.Real),
                insertCmd.Parameters.Add("@ask", SqliteType.Real),
                insertCmd.Parameters.Add("@mid", SqliteType.Real),
                insertCmd.Parameters.Add("@last", SqliteType.Real),
                insertCmd.Parameters.Add("@iv", SqliteType.Real),
                insertCmd.Parameters.Add("@delta", SqliteType.Real),
                insertCmd.Parameters.Add("@gamma", SqliteType.Real),
                insertCmd.Parameters.Add("@theta", SqliteType.Real),
                insertCmd.Parameters.Add("@vega", SqliteType.Real),
                insertCmd.Parameters.Add("@open_interest", SqliteType.Integer),
                insertCmd.Parameters.Add("@volume", SqliteType.Integer),
                insertCmd.Parameters.Add("@moneyness", SqliteType.Real)
            };

            // Get underlying prices for moneyness calculation
            var underlyingPrices = new Dictionary<DateTime, decimal>();

            foreach (var record in records)
            {
                ct.ThrowIfCancellationRequested();
                
                // Calculate DTE
                var dte = (record.Expiry.ToDateTime(TimeOnly.MinValue) - record.Session.ToDateTime(TimeOnly.MinValue)).Days;
                
                // Skip if outside 0-45 DTE range
                if (dte < 0 || dte > 45)
                {
                    skipped++;
                    continue;
                }

                // Get underlying price for moneyness
                if (!underlyingPrices.TryGetValue(record.TsUtc, out var underlyingPrice))
                {
                    underlyingPrice = await GetOrEstimateUnderlyingPriceAsync(record.Symbol, record.TsUtc, transaction);
                    underlyingPrices[record.TsUtc] = underlyingPrice;
                }

                if (underlyingPrice == 0)
                {
                    skipped++;
                    continue;
                }

                var moneyness = record.Strike / underlyingPrice;
                
                // Skip if outside ±15% moneyness window
                if (moneyness < 0.85m || moneyness > 1.15m)
                {
                    skipped++;
                    continue;
                }

                // Set parameter values
                parameters[0].Value = record.Symbol;
                parameters[1].Value = record.Session.ToString("yyyy-MM-dd");
                parameters[2].Value = record.TsUtc;
                parameters[3].Value = record.Expiry.ToString("yyyy-MM-dd");
                parameters[4].Value = (double)record.Strike;
                parameters[5].Value = record.Right == Right.Call ? "C" : "P";
                parameters[6].Value = record.Bid != 0 ? (double)record.Bid : DBNull.Value;
                parameters[7].Value = record.Ask != 0 ? (double)record.Ask : DBNull.Value;
                parameters[8].Value = record.Mid.HasValue ? (double)record.Mid.Value : DBNull.Value;
                parameters[9].Value = record.Last.HasValue ? (double)record.Last.Value : DBNull.Value;
                parameters[10].Value = record.Iv.HasValue ? (double)record.Iv.Value : DBNull.Value;
                parameters[11].Value = record.Delta ?? (object)DBNull.Value;
                parameters[12].Value = record.Gamma ?? (object)DBNull.Value;
                parameters[13].Value = record.Theta ?? (object)DBNull.Value;
                parameters[14].Value = record.Vega ?? (object)DBNull.Value;
                parameters[15].Value = record.Oi ?? (object)DBNull.Value;
                parameters[16].Value = record.Volume ?? (object)DBNull.Value;
                parameters[17].Value = (double)moneyness;

                await insertCmd.ExecuteNonQueryAsync(ct);
                processed++;
            }

            // Update daily greeks summary
            await UpdateDailyGreeksSummaryAsync(records.First().Symbol, records.First().Session, transaction);

            // Complete ingestion log
            await LogIngestionCompleteAsync(ingestId, processed, transaction);

            await transaction.CommitAsync(ct);

            return new IngestionResult
            {
                IngestId = ingestId,
                RecordsProcessed = processed,
                RecordsSkipped = skipped,
                Duration = DateTime.UtcNow - startTime,
                Success = true
            };
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            await LogIngestionErrorAsync(ingestId, ex.Message);
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<IngestionResult> IngestUnderlyingBarsAsync(
        IEnumerable<UnderlyingBar> bars,
        CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        var transaction = _connection.BeginTransaction();
        
        try
        {
            var startTime = DateTime.UtcNow;
            int processed = 0;

            using var insertCmd = _connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = @"
                INSERT OR REPLACE INTO underlying_bars (
                    symbol, ts_utc, open, high, low, close, volume
                ) VALUES (
                    @symbol, @ts_utc, @open, @high, @low, @close, @volume
                )";

            var parameters = new[]
            {
                insertCmd.Parameters.Add("@symbol", SqliteType.Text),
                insertCmd.Parameters.Add("@ts_utc", SqliteType.Text),
                insertCmd.Parameters.Add("@open", SqliteType.Real),
                insertCmd.Parameters.Add("@high", SqliteType.Real),
                insertCmd.Parameters.Add("@low", SqliteType.Real),
                insertCmd.Parameters.Add("@close", SqliteType.Real),
                insertCmd.Parameters.Add("@volume", SqliteType.Integer)
            };

            foreach (var bar in bars)
            {
                ct.ThrowIfCancellationRequested();

                parameters[0].Value = bar.Symbol;
                parameters[1].Value = bar.TsUtc;
                parameters[2].Value = (double)bar.Open;
                parameters[3].Value = (double)bar.High;
                parameters[4].Value = (double)bar.Low;
                parameters[5].Value = (double)bar.Close;
                parameters[6].Value = bar.Volume;

                await insertCmd.ExecuteNonQueryAsync(ct);
                processed++;
            }

            await transaction.CommitAsync(ct);

            return new IngestionResult
            {
                IngestId = Guid.NewGuid().ToString(),
                RecordsProcessed = processed,
                RecordsSkipped = 0,
                Duration = DateTime.UtcNow - startTime,
                Success = true
            };
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<decimal> GetOrEstimateUnderlyingPriceAsync(
        string symbol,
        DateTime tsUtc,
        SqliteTransaction transaction)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            SELECT close
            FROM underlying_bars
            WHERE symbol = @symbol
                AND ts_utc <= @ts_utc
            ORDER BY ts_utc DESC
            LIMIT 1";

        cmd.Parameters.AddWithValue("@symbol", symbol);
        cmd.Parameters.AddWithValue("@ts_utc", tsUtc);

        var result = await cmd.ExecuteScalarAsync();
        if (result != null)
            return Convert.ToDecimal(result);

        // If no exact match, estimate from options mid prices (put-call parity)
        cmd.CommandText = @"
            SELECT AVG(strike + call_mid - put_mid) as estimated_price
            FROM (
                SELECT 
                    c.strike,
                    c.mid as call_mid,
                    p.mid as put_mid
                FROM options_chain c
                JOIN options_chain p ON 
                    c.symbol = p.symbol
                    AND c.expiry_date = p.expiry_date
                    AND c.strike = p.strike
                    AND c.ts_utc = p.ts_utc
                WHERE c.symbol = @symbol
                    AND c.ts_utc = @ts_utc
                    AND c.right = 'C'
                    AND p.right = 'P'
                    AND c.mid IS NOT NULL
                    AND p.mid IS NOT NULL
                LIMIT 5
            )";

        result = await cmd.ExecuteScalarAsync();
        return result != null ? Convert.ToDecimal(result) : 0;
    }

    private async Task UpdateDailyGreeksSummaryAsync(
        string symbol,
        DateOnly sessionDate,
        SqliteTransaction transaction)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT OR REPLACE INTO daily_greeks_summary (
                symbol, session_date, expiry_date,
                total_gamma, total_delta, total_vega, total_theta,
                put_call_ratio, max_pain_strike
            )
            SELECT 
                symbol,
                session_date,
                expiry_date,
                SUM(gamma * open_interest) as total_gamma,
                SUM(delta * open_interest) as total_delta,
                SUM(vega * open_interest) as total_vega,
                SUM(theta * open_interest) as total_theta,
                CAST(SUM(CASE WHEN right = 'P' THEN volume ELSE 0 END) AS REAL) / 
                    NULLIF(SUM(CASE WHEN right = 'C' THEN volume ELSE 0 END), 0) as put_call_ratio,
                (SELECT strike FROM options_chain oc2 
                 WHERE oc2.symbol = oc.symbol 
                   AND oc2.session_date = oc.session_date
                   AND oc2.expiry_date = oc.expiry_date
                 GROUP BY strike
                 ORDER BY SUM(open_interest) DESC
                 LIMIT 1) as max_pain_strike
            FROM options_chain oc
            WHERE symbol = @symbol
                AND session_date = @session_date
            GROUP BY symbol, session_date, expiry_date";

        cmd.Parameters.AddWithValue("@symbol", symbol);
        cmd.Parameters.AddWithValue("@session_date", sessionDate.ToString("yyyy-MM-dd"));

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task LogIngestionStartAsync(
        string ingestId,
        string symbol,
        DateTime startTime,
        SqliteTransaction transaction)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            INSERT INTO ingestion_log (
                ingest_id, symbol, start_date, end_date, dte_range,
                ts_started, status
            ) VALUES (
                @ingest_id, @symbol, @start_date, @end_date, @dte_range,
                @ts_started, 'RUNNING'
            )";

        cmd.Parameters.AddWithValue("@ingest_id", ingestId);
        cmd.Parameters.AddWithValue("@symbol", symbol);
        cmd.Parameters.AddWithValue("@start_date", startTime.Date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@end_date", startTime.Date.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@dte_range", "0-45");
        cmd.Parameters.AddWithValue("@ts_started", startTime);

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task LogIngestionCompleteAsync(
        string ingestId,
        int recordsProcessed,
        SqliteTransaction transaction)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            UPDATE ingestion_log
            SET status = 'COMPLETED',
                ts_completed = @ts_completed,
                records_processed = @records_processed
            WHERE ingest_id = @ingest_id";

        cmd.Parameters.AddWithValue("@ingest_id", ingestId);
        cmd.Parameters.AddWithValue("@ts_completed", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("@records_processed", recordsProcessed);

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task LogIngestionErrorAsync(string ingestId, string errorMessage)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE ingestion_log
                SET status = 'FAILED',
                    error_message = @error_message
                WHERE ingest_id = @ingest_id";

            cmd.Parameters.AddWithValue("@ingest_id", ingestId);
            cmd.Parameters.AddWithValue("@error_message", errorMessage);

            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort logging
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _semaphore?.Dispose();
    }
}

public record IngestionResult
{
    public required string IngestId { get; init; }
    public required int RecordsProcessed { get; init; }
    public required int RecordsSkipped { get; init; }
    public required TimeSpan Duration { get; init; }
    public required bool Success { get; init; }
}