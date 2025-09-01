using System.Text.Json;
using Parquet.Serialization;
using Stroll.Alpha.Dataset.Models;
using System.Security.Cryptography;

namespace Stroll.Alpha.Dataset.Writers;

public sealed class ParquetSnapshotWriter : IDisposable
{
    private readonly string _rootPath;
    private readonly int _batchSize;
    private bool _disposed;

    public ParquetSnapshotWriter(string rootPath, int batchSize = 1000)
    {
        _rootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
        _batchSize = batchSize;
    }

    public async Task<WriteResult> WriteAsync(
        string symbol,
        DateOnly sessionDate,
        IAsyncEnumerable<OptionRecord> records,
        CancellationToken ct = default)
    {
        var partitionPath = GetPartitionPath(symbol, sessionDate);
        Directory.CreateDirectory(partitionPath);
        
        var fileName = $"snapshots_{sessionDate:yyyy-MM-dd}.parquet";
        var filePath = Path.Combine(partitionPath, fileName);
        
        var allRecords = new List<SnapshotParquetRecord>();
        
        // Collect all records (for simplicity, we'll write all at once)
        await foreach (var record in records.WithCancellation(ct))
        {
            var dte = (record.Expiry.ToDateTime(TimeOnly.MinValue) - record.Session.ToDateTime(TimeOnly.MinValue)).Days;
            
            allRecords.Add(new SnapshotParquetRecord
            {
                Symbol = record.Symbol,
                TsUtc = record.TsUtc,
                ExpiryDate = record.Expiry,
                Strike = record.Strike,
                Right = record.Right.ToString(),
                Bid = record.Bid,
                Ask = record.Ask,
                Mid = record.Mid,
                Last = record.Last,
                Iv = record.Iv,
                Delta = record.Delta,
                Gamma = record.Gamma,
                Theta = record.Theta,
                Vega = record.Vega,
                OpenInterest = record.Oi,
                Volume = record.Volume,
                Dte = dte,
                Moneyness = null // Will need underlying price to calculate
            });
        }
        
        // Write Parquet file using serialization
        await using var fileStream = File.Create(filePath);
        await ParquetSerializer.SerializeAsync(allRecords, fileStream, cancellationToken: ct);
        
        await fileStream.FlushAsync(ct);
        fileStream.Close();
        
        // Calculate checksum and metadata
        var sha256 = await CalculateSha256Async(filePath, ct);
        var metadata = new FileMetadata
        {
            FileName = fileName,
            RecordCount = allRecords.Count,
            Sha256 = sha256,
            Symbol = symbol,
            SessionDate = sessionDate,
            CreatedUtc = DateTime.UtcNow,
            BuildVersion = GetBuildVersion()
        };
        
        // Write metadata
        var metaPath = Path.Combine(partitionPath, "meta.json");
        await UpdateMetadataAsync(metaPath, fileName, metadata, ct);
        
        return new WriteResult(filePath, allRecords.Count, sha256);
    }

    private string GetPartitionPath(string symbol, DateOnly sessionDate)
    {
        return Path.Combine(_rootPath, "alpha", symbol.ToUpperInvariant(), 
            sessionDate.Year.ToString(), sessionDate.Month.ToString("D2"));
    }

    private async Task<string> CalculateSha256Async(string filePath, CancellationToken ct)
    {
        using var sha256 = SHA256.Create();
        await using var fileStream = File.OpenRead(filePath);
        var hashBytes = await sha256.ComputeHashAsync(fileStream, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private async Task UpdateMetadataAsync(
        string metaPath, 
        string fileName, 
        FileMetadata metadata, 
        CancellationToken ct)
    {
        var metaDict = new Dictionary<string, FileMetadata>();
        
        // Load existing metadata if present
        if (File.Exists(metaPath))
        {
            var existingJson = await File.ReadAllTextAsync(metaPath, ct);
            var existing = JsonSerializer.Deserialize<Dictionary<string, FileMetadata>>(existingJson);
            if (existing != null)
                metaDict = existing;
        }
        
        metaDict[fileName] = metadata;
        
        var json = JsonSerializer.Serialize(metaDict, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(metaPath, json, ct);
    }

    private static string GetBuildVersion()
    {
        var version = typeof(ParquetSnapshotWriter).Assembly.GetName().Version;
        return version?.ToString() ?? "unknown";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

// Parquet-serializable snapshot record
public sealed class SnapshotParquetRecord
{
    public string Symbol { get; set; } = "";
    public DateTime TsUtc { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public decimal Strike { get; set; }
    public string Right { get; set; } = "";
    public decimal? Bid { get; set; }
    public decimal? Ask { get; set; }
    public decimal? Mid { get; set; }
    public decimal? Last { get; set; }
    public decimal? Iv { get; set; }
    public double? Delta { get; set; }
    public double? Gamma { get; set; }
    public double? Theta { get; set; }
    public double? Vega { get; set; }
    public long? OpenInterest { get; set; }
    public long? Volume { get; set; }
    public int Dte { get; set; }
    public decimal? Moneyness { get; set; }
}