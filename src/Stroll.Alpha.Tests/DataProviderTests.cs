using Xunit;
using FluentAssertions;
using Stroll.Alpha.Dataset.Models;
using Stroll.Alpha.Dataset.Storage;
using Stroll.Alpha.Dataset.Ingestion;
using System.IO;

namespace Stroll.Alpha.Tests;

public sealed class DataProviderTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly SqliteDataProvider _provider;
    private readonly DataIngestionService _ingestion;

    public DataProviderTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
        _provider = new SqliteDataProvider(_testDbPath);
        _ingestion = new DataIngestionService(_testDbPath);
    }

    [Fact]
    public async Task GetSnapshotAsync_Should_Return_Options_Within_DTE_Range()
    {
        // Arrange
        var testData = GenerateTestOptionsChain("SPX", DateTime.Parse("2024-01-15T15:00:00Z"));
        await _ingestion.IngestOptionsChainAsync(testData, Guid.NewGuid().ToString());

        // Act
        var snapshot = await _provider.GetSnapshotAsync(
            "SPX",
            DateTime.Parse("2024-01-15T15:00:00Z"),
            0.15m, // ±15% moneyness
            0,     // Min DTE
            45,    // Max DTE
            CancellationToken.None
        ).ToListAsync();

        // Assert
        snapshot.Should().NotBeEmpty();
        snapshot.All(o => o.Symbol == "SPX").Should().BeTrue();
        
        // Verify DTE constraints
        foreach (var option in snapshot)
        {
            var dte = (option.Expiry.ToDateTime(TimeOnly.MinValue) - option.Session.ToDateTime(TimeOnly.MinValue)).Days;
            dte.Should().BeInRange(0, 45);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_Should_Filter_By_Moneyness()
    {
        // Arrange
        var tsUtc = DateTime.Parse("2024-01-15T15:00:00Z");
        var underlyingPrice = 4500m;
        
        // First, ingest underlying bar data
        var bars = new[]
        {
            new UnderlyingBar("SPX", tsUtc, underlyingPrice, underlyingPrice + 5, underlyingPrice - 5, underlyingPrice, 1000000)
        };
        await _ingestion.IngestUnderlyingBarsAsync(bars);

        // Create options at various moneyness levels
        var options = new List<OptionRecord>();
        for (decimal moneyness = 0.8m; moneyness <= 1.2m; moneyness += 0.05m)
        {
            var strike = underlyingPrice * moneyness;
            options.Add(CreateTestOption("SPX", tsUtc, strike, Right.Call));
            options.Add(CreateTestOption("SPX", tsUtc, strike, Right.Put));
        }
        
        await _ingestion.IngestOptionsChainAsync(options, Guid.NewGuid().ToString());

        // Act
        var snapshot = await _provider.GetSnapshotAsync(
            "SPX",
            tsUtc,
            0.10m, // ±10% moneyness window
            0,
            45,
            CancellationToken.None
        ).ToListAsync();

        // Assert
        snapshot.Should().NotBeEmpty();
        
        // All returned options should be within ±10% moneyness
        foreach (var option in snapshot)
        {
            var moneyness = option.Strike / underlyingPrice;
            moneyness.Should().BeInRange(0.9m, 1.1m);
        }
    }

    [Fact]
    public async Task GetExpiriesAsync_Should_Return_Available_Expiries()
    {
        // Arrange
        var baseDate = DateTime.Parse("2024-01-15T15:00:00Z");
        var options = new List<OptionRecord>();
        
        for (int dte = 0; dte <= 45; dte += 7) // Weekly expiries
        {
            var expiry = DateOnly.FromDateTime(baseDate.AddDays(dte));
            options.Add(CreateTestOption("SPX", baseDate, 4500, Right.Call, expiry));
        }
        
        await _ingestion.IngestOptionsChainAsync(options, Guid.NewGuid().ToString());

        // Act
        var expiries = await _provider.GetExpiriesAsync("SPX", baseDate, 45, CancellationToken.None);

        // Assert
        expiries.Should().NotBeEmpty();
        expiries.Should().HaveCountGreaterOrEqualTo(7); // At least weekly expiries
        expiries.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task GetBarsAsync_Should_Return_Bars_In_Time_Range()
    {
        // Arrange
        var startTime = DateTime.Parse("2024-01-15T14:30:00Z");
        var bars = new List<UnderlyingBar>();
        
        for (int i = 0; i < 60; i++) // 60 minutes of data
        {
            var ts = startTime.AddMinutes(i);
            bars.Add(new UnderlyingBar("SPX", ts, 4500 + i, 4505 + i, 4495 + i, 4502 + i, 100000 + i * 1000));
        }
        
        await _ingestion.IngestUnderlyingBarsAsync(bars);

        // Act
        var result = await _provider.GetBarsAsync(
            "SPX",
            startTime,
            startTime.AddMinutes(30),
            "1m",
            CancellationToken.None
        ).ToListAsync();

        // Assert
        result.Should().HaveCount(31); // 0-30 minutes inclusive
        result.First().TsUtc.Should().Be(startTime);
        result.Last().TsUtc.Should().Be(startTime.AddMinutes(30));
    }

    [Fact]
    public async Task GetBarsAsync_Should_Support_Different_Intervals()
    {
        // Arrange
        var startTime = DateTime.Parse("2024-01-15T14:30:00Z");
        var bars = new List<UnderlyingBar>();
        
        for (int i = 0; i < 60; i++)
        {
            var ts = startTime.AddMinutes(i);
            bars.Add(new UnderlyingBar("SPX", ts, 4500, 4510, 4490, 4505, 100000));
        }
        
        await _ingestion.IngestUnderlyingBarsAsync(bars);

        // Act - 5 minute bars
        var fiveMinBars = await _provider.GetBarsAsync(
            "SPX",
            startTime,
            startTime.AddMinutes(59),
            "5m",
            CancellationToken.None
        ).ToListAsync();

        // Assert
        fiveMinBars.Should().HaveCount(12); // 60 minutes / 5 = 12 bars
        fiveMinBars.All(b => b.Volume == 500000).Should().BeTrue(); // 5 bars aggregated
    }

    [Fact]
    public async Task ProbeDataCompletenessAsync_Should_Calculate_Correct_Score()
    {
        // Arrange
        var tsUtc = DateTime.Parse("2024-01-15T15:00:00Z");
        
        // Add underlying bars
        var bars = new List<UnderlyingBar>();
        for (int i = 0; i < 390; i++) // Full trading day
        {
            bars.Add(new UnderlyingBar("SPX", tsUtc.Date.AddHours(14.5).AddMinutes(i), 4500, 4510, 4490, 4505, 100000));
        }
        await _ingestion.IngestUnderlyingBarsAsync(bars);

        // Add complete options chain
        var options = GenerateCompleteOptionsChain("SPX", tsUtc);
        await _ingestion.IngestOptionsChainAsync(options, Guid.NewGuid().ToString());

        // Act
        var result = await _provider.ProbeDataCompletenessAsync("SPX", tsUtc);

        // Assert
        result.Should().NotBeNull();
        result.Score.Should().BeGreaterThan(80m); // Good completeness score
        result.Symbol.Should().Be("SPX");
        result.StrikesLeft.Should().BeGreaterThan(10);
        result.StrikesRight.Should().BeGreaterThan(10);
    }

    [Fact]
    public async Task IngestOptionsChainAsync_Should_Skip_Invalid_DTE()
    {
        // Arrange
        var options = new List<OptionRecord>
        {
            CreateTestOption("SPX", DateTime.UtcNow, 4500, Right.Call, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1))), // Expired
            CreateTestOption("SPX", DateTime.UtcNow, 4500, Right.Call, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(50))), // > 45 DTE
            CreateTestOption("SPX", DateTime.UtcNow, 4500, Right.Call, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10))), // Valid
        };

        // Act
        var result = await _ingestion.IngestOptionsChainAsync(options, Guid.NewGuid().ToString());

        // Assert
        result.RecordsProcessed.Should().Be(1); // Only the valid one
        result.RecordsSkipped.Should().Be(2);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task IngestOptionsChainAsync_Should_Skip_Out_Of_Moneyness_Range()
    {
        // Arrange
        var tsUtc = DateTime.UtcNow;
        var underlyingPrice = 4500m;
        
        // Add underlying bar
        await _ingestion.IngestUnderlyingBarsAsync(new[]
        {
            new UnderlyingBar("SPX", tsUtc, underlyingPrice, underlyingPrice, underlyingPrice, underlyingPrice, 100000)
        });

        var options = new List<OptionRecord>
        {
            CreateTestOption("SPX", tsUtc, underlyingPrice * 0.75m, Right.Put),  // Too far OTM
            CreateTestOption("SPX", tsUtc, underlyingPrice * 1.25m, Right.Call), // Too far OTM
            CreateTestOption("SPX", tsUtc, underlyingPrice * 0.95m, Right.Put),  // Valid
            CreateTestOption("SPX", tsUtc, underlyingPrice * 1.05m, Right.Call), // Valid
        };

        // Act
        var result = await _ingestion.IngestOptionsChainAsync(options, Guid.NewGuid().ToString());

        // Assert
        result.RecordsProcessed.Should().Be(2); // Only the ones within ±15% moneyness
        result.RecordsSkipped.Should().Be(2);
    }

    private OptionRecord CreateTestOption(
        string symbol,
        DateTime tsUtc,
        decimal strike,
        Right right,
        DateOnly? expiry = null)
    {
        expiry ??= DateOnly.FromDateTime(tsUtc.AddDays(7));
        
        return new OptionRecord(
            Symbol: symbol,
            Session: DateOnly.FromDateTime(tsUtc),
            TsUtc: tsUtc,
            Expiry: expiry.Value,
            Strike: strike,
            Right: right,
            Bid: 10m,
            Ask: 11m,
            Mid: 10.5m,
            Last: 10.5m,
            Iv: 0.25m,
            Delta: right == Right.Call ? 0.5 : -0.5,
            Gamma: 0.01,
            Theta: -0.05,
            Vega: 0.1,
            Oi: 1000,
            Volume: 500
        );
    }

    private List<OptionRecord> GenerateTestOptionsChain(string symbol, DateTime tsUtc)
    {
        var options = new List<OptionRecord>();
        var baseStrike = 4500m;
        
        // Generate options for multiple expiries
        for (int dte = 0; dte <= 45; dte += 7)
        {
            var expiry = DateOnly.FromDateTime(tsUtc.AddDays(dte));
            
            // Generate strikes around ATM
            for (decimal strike = baseStrike * 0.9m; strike <= baseStrike * 1.1m; strike += 50)
            {
                options.Add(CreateTestOption(symbol, tsUtc, strike, Right.Call, expiry));
                options.Add(CreateTestOption(symbol, tsUtc, strike, Right.Put, expiry));
            }
        }
        
        return options;
    }

    private List<OptionRecord> GenerateCompleteOptionsChain(string symbol, DateTime tsUtc)
    {
        var options = new List<OptionRecord>();
        var baseStrike = 4500m;
        
        // Generate comprehensive chain for testing completeness
        for (int dte = 0; dte <= 45; dte++)
        {
            if (dte <= 7 || dte % 7 == 0) // Daily for first week, then weekly
            {
                var expiry = DateOnly.FromDateTime(tsUtc.AddDays(dte));
                
                // Generate 30 strikes each side
                for (int i = -30; i <= 30; i++)
                {
                    var strike = baseStrike + (i * 25);
                    options.Add(CreateTestOption(symbol, tsUtc, strike, Right.Call, expiry));
                    options.Add(CreateTestOption(symbol, tsUtc, strike, Right.Put, expiry));
                }
            }
        }
        
        return options;
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _ingestion?.Dispose();
        
        // Clean up test database
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }
}