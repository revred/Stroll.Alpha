using Microsoft.Data.Sqlite;
using Stroll.Alpha.Dataset.Models;
using Stroll.Alpha.Dataset.Writers;
using Stroll.Alpha.Dataset.Integrity;
using System.Text.Json;

namespace Stroll.Alpha.Dataset.Storage;

public sealed class DatasetStore : IDisposable
{
    private readonly string _rootPath;
    private readonly string _connectionString;
    private bool _disposed;

    public DatasetStore(string rootPath)
    {
        _rootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        _connectionString = $"Data Source={Path.Combine(_rootPath, "bars.sqlite")};Mode=ReadWriteCreate;Pooling=true;Cache=Shared";
        
        Directory.CreateDirectory(_rootPath);
        InitializeBarsDatabase();
    }

    /// <summary>
    /// Save options chain data to Parquet files with metadata and checksums
    /// </summary>
    public async Task<WriteResult> SaveChainAsync(
        string symbol,
        DateOnly sessionDate,
        IEnumerable<OptionRecord> records,
        CancellationToken ct = default)
    {
        using var writer = new ParquetChainWriter(_rootPath);
        return await writer.WriteAsync(symbol, sessionDate, records, ct);
    }

    /// <summary>
    /// Save snapshot data to Parquet files with batching for memory efficiency
    /// </summary>
    public async Task<WriteResult> SaveSnapshotsAsync(
        string symbol,
        DateOnly sessionDate,
        IAsyncEnumerable<OptionRecord> records,
        CancellationToken ct = default)
    {
        using var writer = new ParquetSnapshotWriter(_rootPath);
        return await writer.WriteAsync(symbol, sessionDate, records, ct);
    }

    /// <summary>
    /// Save minute bars to SQLite for fast time-series queries
    /// </summary>
    public async Task<int> SaveBarsAsync(
        string symbol,
        IEnumerable<UnderlyingBar> bars,
        CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        var inserted = 0;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        
        try
        {
            const string sql = """
                INSERT OR REPLACE INTO bars_1m (symbol, ts_utc, open, high, low, close, volume)
                VALUES (@symbol, @ts_utc, @open, @high, @low, @close, @volume)
                """;

            foreach (var bar in bars)
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                
                cmd.Parameters.AddWithValue("@symbol", bar.Symbol);
                cmd.Parameters.AddWithValue("@ts_utc", bar.TsUtc);
                cmd.Parameters.AddWithValue("@open", bar.Open);
                cmd.Parameters.AddWithValue("@high", bar.High);
                cmd.Parameters.AddWithValue("@low", bar.Low);
                cmd.Parameters.AddWithValue("@close", bar.Close);
                cmd.Parameters.AddWithValue("@volume", bar.Volume);

                var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
                inserted += rowsAffected;
            }

            await transaction.CommitAsync(ct);
            return inserted;
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Load file metadata for integrity verification
    /// </summary>
    public async Task<Dictionary<string, FileMetadata>?> LoadMetadataAsync(
        string symbol, 
        DateOnly sessionDate, 
        CancellationToken ct = default)
    {
        var partitionPath = GetPartitionPath(symbol, sessionDate);
        var metaPath = Path.Combine(partitionPath, "meta.json");
        
        if (!File.Exists(metaPath))
            return null;
        
        var json = await File.ReadAllTextAsync(metaPath, ct);
        return JsonSerializer.Deserialize<Dictionary<string, FileMetadata>>(json);
    }

    /// <summary>
    /// Verify file integrity using stored checksums
    /// </summary>
    public async Task<IntegrityReport> VerifyIntegrityAsync(
        string symbol,
        DateOnly sessionDate,
        CancellationToken ct = default)
    {
        var report = new IntegrityReport { Symbol = symbol, SessionDate = sessionDate };
        var metadata = await LoadMetadataAsync(symbol, sessionDate, ct);
        
        if (metadata == null)
        {
            report.Status = IntegrityStatus.MetadataMissing;
            return report;
        }

        var partitionPath = GetPartitionPath(symbol, sessionDate);
        var verifiedFiles = 0;
        var totalFiles = metadata.Count;

        foreach (var (fileName, meta) in metadata)
        {
            var filePath = Path.Combine(partitionPath, fileName);
            
            if (!File.Exists(filePath))
            {
                report.MissingFiles.Add(fileName);
                continue;
            }

            var actualSha256 = await CalculateFileSha256Async(filePath, ct);
            
            if (actualSha256.Equals(meta.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                verifiedFiles++;
            }
            else
            {
                report.CorruptedFiles.Add(new CorruptedFile 
                { 
                    FileName = fileName, 
                    ExpectedSha256 = meta.Sha256, 
                    ActualSha256 = actualSha256 
                });
            }
        }

        report.Status = report.MissingFiles.Count == 0 && report.CorruptedFiles.Count == 0 
            ? IntegrityStatus.Valid 
            : IntegrityStatus.Corrupted;
        
        report.VerifiedFiles = verifiedFiles;
        report.TotalFiles = totalFiles;
        
        return report;
    }

    /// <summary>
    /// Comprehensive integrity verification with expected minute math
    /// </summary>
    public async Task<SessionIntegrityReport> ValidateSessionIntegrityAsync(
        string symbol,
        DateOnly sessionDate,
        CancellationToken ct = default)
    {
        var report = new SessionIntegrityReport 
        { 
            Symbol = symbol, 
            SessionDate = sessionDate,
            CheckedUtc = DateTime.UtcNow
        };

        // Check file integrity first
        var fileReport = await VerifyIntegrityAsync(symbol, sessionDate, ct);
        report.FilesIntegrity = fileReport.Status;
        report.VerifiedFiles = fileReport.VerifiedFiles;
        report.TotalFiles = fileReport.TotalFiles;

        // Validate minute bar completeness
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT ts_utc 
            FROM bars_1m 
            WHERE symbol = @symbol 
                AND DATE(ts_utc) = @session_date
            ORDER BY ts_utc";

        cmd.Parameters.AddWithValue("@symbol", symbol);
        cmd.Parameters.AddWithValue("@session_date", sessionDate.ToString("yyyy-MM-dd"));

        var timestamps = new List<DateTime>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            timestamps.Add(reader.GetDateTime(0));
        }

        var (expected, actual, ratio) = IntegrityCalculator.ValidateSessionWindow(sessionDate, timestamps);
        report.ExpectedMinuteBars = expected;
        report.ActualMinuteBars = actual;
        report.CompletenessRatio = ratio;

        // Overall assessment
        report.Status = DetermineIntegrityStatus(fileReport.Status, ratio);
        
        if (ratio < 0.95)
        {
            report.Issues.Add($"Missing minute bars: {expected - actual} of {expected} expected");
        }

        return report;
    }

    private IntegrityStatus DetermineIntegrityStatus(IntegrityStatus fileStatus, double completenessRatio)
    {
        if (fileStatus == IntegrityStatus.Corrupted)
            return IntegrityStatus.Corrupted;

        if (completenessRatio < 0.8)
            return IntegrityStatus.Corrupted;

        if (completenessRatio < 0.95 || fileStatus == IntegrityStatus.MetadataMissing)
            return IntegrityStatus.MetadataMissing; // Reusing as "incomplete"

        return IntegrityStatus.Valid;
    }

    private string GetPartitionPath(string symbol, DateOnly sessionDate)
    {
        return Path.Combine(_rootPath, "alpha", symbol.ToUpperInvariant(), 
            sessionDate.Year.ToString(), sessionDate.Month.ToString("D2"));
    }

    private async Task<string> CalculateFileSha256Async(string filePath, CancellationToken ct)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        await using var fileStream = File.OpenRead(filePath);
        var hashBytes = await sha256.ComputeHashAsync(fileStream, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private void InitializeBarsDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        const string createTableSql = """
            CREATE TABLE IF NOT EXISTS bars_1m (
                symbol TEXT NOT NULL,
                ts_utc DATETIME NOT NULL,
                open DECIMAL(10,4) NOT NULL,
                high DECIMAL(10,4) NOT NULL,
                low DECIMAL(10,4) NOT NULL,
                close DECIMAL(10,4) NOT NULL,
                volume INTEGER NOT NULL,
                PRIMARY KEY (symbol, ts_utc)
            );
            
            CREATE INDEX IF NOT EXISTS idx_bars_symbol_ts ON bars_1m(symbol, ts_utc);
            """;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = createTableSql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Check chain completeness for data quality scoring
    /// </summary>
    public async Task<CompletenessReport> CheckChainCompletenessAsync(
        string symbol, 
        DateTime tsUtc, 
        decimal mny = 0.15m, 
        int dteMin = 0, 
        int dteMax = 45, 
        CancellationToken ct = default)
    {
        // Simple completeness check without full probe dependency
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_rootPath, "..", "Stroll.Theta.DB", "stroll_theta.db")};Mode=ReadOnly;Cache=Shared");
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                right,
                COUNT(*) as strike_count
            FROM options_chain
            WHERE symbol = @symbol
                AND session_date = DATE(@ts_utc)
                AND dte >= @dte_min AND dte <= @dte_max
            GROUP BY right";

        cmd.Parameters.AddWithValue("@symbol", symbol);
        cmd.Parameters.AddWithValue("@ts_utc", tsUtc);
        cmd.Parameters.AddWithValue("@dte_min", dteMin);
        cmd.Parameters.AddWithValue("@dte_max", dteMax);

        var putStrikes = 0;
        var callStrikes = 0;
        var warnings = new List<string>();

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var right = reader.GetString(0);
            var count = reader.GetInt32(1);
            
            if (right == "P")
                putStrikes = count;
            else if (right == "C")
                callStrikes = count;
        }

        var total = putStrikes + callStrikes;
        var score = Math.Min(1.0, total / 12.0); // Expect ~6 strikes per side

        if (putStrikes < 3) warnings.Add("insufficient_put_strikes");
        if (callStrikes < 3) warnings.Add("insufficient_call_strikes");

        return new CompletenessReport(
            symbol,
            DateOnly.FromDateTime(tsUtc),
            putStrikes,
            callStrikes,
            score,
            warnings.ToArray()
        );
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            SqliteConnection.ClearAllPools();
            _disposed = true;
        }
    }
}

public sealed record IntegrityReport
{
    public required string Symbol { get; init; }
    public required DateOnly SessionDate { get; init; }
    public IntegrityStatus Status { get; set; }
    public int VerifiedFiles { get; set; }
    public int TotalFiles { get; set; }
    public List<string> MissingFiles { get; init; } = [];
    public List<CorruptedFile> CorruptedFiles { get; init; } = [];
}

public sealed record CorruptedFile
{
    public required string FileName { get; init; }
    public required string ExpectedSha256 { get; init; }
    public required string ActualSha256 { get; init; }
}

public sealed record SessionIntegrityReport
{
    public required string Symbol { get; init; }
    public required DateOnly SessionDate { get; init; }
    public required DateTime CheckedUtc { get; init; }
    public IntegrityStatus Status { get; set; }
    public IntegrityStatus FilesIntegrity { get; set; }
    public int VerifiedFiles { get; set; }
    public int TotalFiles { get; set; }
    public int ExpectedMinuteBars { get; set; }
    public int ActualMinuteBars { get; set; }
    public double CompletenessRatio { get; set; }
    public List<string> Issues { get; init; } = [];
}

public enum IntegrityStatus
{
    Valid,
    Corrupted,
    MetadataMissing
}