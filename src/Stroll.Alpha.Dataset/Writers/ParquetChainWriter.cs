using System.Text.Json;
using Parquet.Serialization;
using Stroll.Alpha.Dataset.Models;
using System.Security.Cryptography;

namespace Stroll.Alpha.Dataset.Writers;

public sealed class ParquetChainWriter : IDisposable
{
    private readonly string _rootPath;
    private bool _disposed;

    public ParquetChainWriter(string rootPath)
    {
        _rootPath = rootPath ?? throw new ArgumentNullException(nameof(rootPath));
    }

    public async Task<WriteResult> WriteAsync(
        string symbol, 
        DateOnly sessionDate, 
        IEnumerable<OptionRecord> records, 
        CancellationToken ct = default)
    {
        var partitionPath = GetPartitionPath(symbol, sessionDate);
        Directory.CreateDirectory(partitionPath);
        
        var fileName = $"chain_{sessionDate:yyyy-MM-dd}.parquet";
        var filePath = Path.Combine(partitionPath, fileName);
        
        var recordsList = records.ToList();
        
        // Convert to Parquet-compatible records
        var parquetRecords = recordsList.Select(r => new ChainParquetRecord
        {
            Symbol = r.Symbol,
            SessionDate = r.Session,
            TsUtc = r.TsUtc,
            ExpiryDate = r.Expiry,
            Strike = r.Strike,
            Right = r.Right.ToString(),
            Bid = r.Bid,
            Ask = r.Ask,
            Mid = r.Mid,
            Last = r.Last,
            Iv = r.Iv,
            Delta = r.Delta,
            Gamma = r.Gamma,
            Theta = r.Theta,
            Vega = r.Vega,
            OpenInterest = r.Oi,
            Volume = r.Volume
        }).ToList();
        
        // Write Parquet file using serialization
        await using var fileStream = File.Create(filePath);
        await ParquetSerializer.SerializeAsync(parquetRecords, fileStream, cancellationToken: ct);
        
        await fileStream.FlushAsync(ct);
        fileStream.Close();
        
        // Calculate checksum and metadata
        var sha256 = await CalculateSha256Async(filePath, ct);
        var metadata = new FileMetadata
        {
            FileName = fileName,
            RecordCount = recordsList.Count,
            Sha256 = sha256,
            Symbol = symbol,
            SessionDate = sessionDate,
            CreatedUtc = DateTime.UtcNow,
            BuildVersion = GetBuildVersion()
        };
        
        // Write metadata
        var metaPath = Path.Combine(partitionPath, "meta.json");
        await UpdateMetadataAsync(metaPath, fileName, metadata, ct);
        
        return new WriteResult(filePath, recordsList.Count, sha256);
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
        var version = typeof(ParquetChainWriter).Assembly.GetName().Version;
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

public sealed record WriteResult(string FilePath, int RecordCount, string Sha256);

public sealed record FileMetadata
{
    public required string FileName { get; init; }
    public required int RecordCount { get; init; }
    public required string Sha256 { get; init; }
    public required string Symbol { get; init; }
    public required DateOnly SessionDate { get; init; }
    public required DateTime CreatedUtc { get; init; }
    public required string BuildVersion { get; init; }
}

// Parquet-serializable record
public sealed class ChainParquetRecord
{
    public string Symbol { get; set; } = "";
    public DateOnly SessionDate { get; set; }
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
}