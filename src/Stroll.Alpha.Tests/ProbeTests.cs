using Xunit;
using FluentAssertions;
using Stroll.Alpha.Dataset.Probes;
using Stroll.Alpha.Dataset.Storage;
using Stroll.Alpha.Dataset.Ingestion;
using Stroll.Alpha.Dataset.Models;
using System.IO;

namespace Stroll.Alpha.Tests;

public sealed class ProbeTests : IDisposable
{
    private readonly string _testDbPath;
    private readonly ChainCompletenessProbe _probe;
    private readonly DataIngestionService _ingestion;

    public ProbeTests()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"probe_test_{Guid.NewGuid()}.db");
        var provider = new SqliteDataProvider(_testDbPath);
        provider.Dispose(); // Just to initialize schema
        
        _probe = new ChainCompletenessProbe(_testDbPath);
        _ingestion = new DataIngestionService(_testDbPath);
    }

    [Fact]
    public async Task ProbeAsync_Should_Detect_Missing_Data()
    {
        // Arrange - empty database
        var tsUtc = DateTime.Parse("2024-01-15T15:00:00Z");

        // Act
        var report = await _probe.ProbeAsync("SPX", tsUtc, ProbeDepth.Standard);

        // Assert
        report.Should().NotBeNull();
        report.OverallScore.Should().Be(0);
        report.Errors.Should().Contain(e => e.Contains("No underlying bar data"));
        report.UnderlyingBarsFound.Should().Be(0);
        report.TotalExpiries.Should().Be(0);
    }

    [Fact]
    public async Task ProbeAsync_Should_Calculate_High_Score_For_Complete_Data()
    {
        // Arrange
        var tsUtc = DateTime.Parse("2024-01-15T15:00:00Z");
        await SeedCompleteDataAsync("SPX", tsUtc);

        // Act
        var report = await _probe.ProbeAsync("SPX", tsUtc, ProbeDepth.Standard);

        // Assert
        report.Should().NotBeNull();
        report.OverallScore.Should().BeGreaterThan(0.8m);
        report.UnderlyingCompleteness.Should().BeGreaterThan(0.9m);
        report.ChainCompleteness.Should().BeGreaterThan(0.8m);
        report.GreeksCompleteness.Should().BeGreaterThan(0.9m);
        report.Warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProbeDepth.Minimal, 10)]
    [InlineData(ProbeDepth.Standard, 20)]
    [InlineData(ProbeDepth.Deep, 30)]
    [InlineData(ProbeDepth.Complete, 50)]
    public async Task ProbeAsync_Should_Enforce_Different_Depth_Requirements(ProbeDepth depth, int requiredStrikes)
    {
        // Arrange
        var tsUtc = DateTime.Parse("2024-01-15T15:00:00Z");
        await SeedPartialOptionsChain("SPX", tsUtc, requiredStrikes - 5); // Just under requirement

        // Act
        var report = await _probe.ProbeAsync("SPX", tsUtc, depth);

        // Assert
        report.Should().NotBeNull();
        report.ExpiryReports.Should().NotBeEmpty();
        
        foreach (var expiry in report.ExpiryReports)
        {
            expiry.Issues.Should().Contain(issue => 
                issue.Contains("Insufficient put strikes") || 
                issue.Contains("Insufficient call strikes"));
        }
    }

    [Fact]
    public async Task ProbeAsync_Should_Detect_Missing_DTEs()
    {
        // Arrange
        var tsUtc = DateTime.Parse("2024-01-15T15:00:00Z");
        
        // Add data with gaps in DTE coverage (missing 0-3 DTE)
        var options = new List<OptionRecord>();
        for (int dte = 4; dte <= 10; dte++)
        {
            options.AddRange(GenerateOptionsForExpiry("SPX", tsUtc, dte));
        }
        
        await _ingestion.IngestOptionsChainAsync(options, Guid.NewGuid().ToString());

        // Act
        var report = await _probe.ProbeAsync("SPX", tsUtc, ProbeDepth.Standard);

        // Assert
        report.Warnings.Should().Contain(w => w.Contains("Missing DTEs"));
    }

    [Fact]
    public async Task ProbeAsync_Should_Detect_Stale_Data()
    {
        // Arrange
        var oldDataTime = DateTime.Parse("2024-01-15T14:00:00Z");
        var probeTime = DateTime.Parse("2024-01-15T15:30:00Z");
        
        // Seed old data
        await SeedCompleteDataAsync("SPX", oldDataTime);

        // Act - probe at later time
        var report = await _probe.ProbeAsync("SPX", probeTime, ProbeDepth.Standard);

        // Assert
        report.DataFreshness.Should().BeLessThan(0.8m);
        report.Warnings.Should().Contain(w => w.Contains("Stale"));
    }

    [Fact]
    public async Task ProbeAsync_Should_Check_Greeks_Completeness()
    {
        // Arrange
        var tsUtc = DateTime.Parse("2024-01-15T15:00:00Z");
        
        // Add options with missing greeks
        var options = GenerateOptionsForExpiry("SPX", tsUtc, 7);
        foreach (var option in options.Take(options.Count / 2))
        {
            // Remove greeks from half the options
            var modified = option with 
            { 
                Delta = null, 
                Gamma = null, 
                Theta = null, 
                Vega = null 
            };
            options[options.IndexOf(option)] = modified;
        }
        
        await _ingestion.IngestOptionsChainAsync(options, Guid.NewGuid().ToString());

        // Act
        var report = await _probe.ProbeAsync("SPX", tsUtc, ProbeDepth.Standard);

        // Assert
        report.GreeksCompleteness.Should().BeLessThan(0.6m);
        report.Warnings.Should().Contain(w => 
            w.Contains("Missing delta") || 
            w.Contains("Missing gamma") || 
            w.Contains("Missing theta") || 
            w.Contains("Missing vega"));
    }

    private async Task SeedCompleteDataAsync(string symbol, DateTime tsUtc)
    {
        // Add complete underlying bars for the day
        var bars = new List<UnderlyingBar>();
        var startTime = tsUtc.Date.AddHours(14.5); // Market open
        
        for (int i = 0; i < 390; i++) // Full trading day
        {
            bars.Add(new UnderlyingBar(
                symbol, 
                startTime.AddMinutes(i), 
                4500, 4510, 4490, 4505, 
                100000 + i * 100));
        }
        
        await _ingestion.IngestUnderlyingBarsAsync(bars);

        // Add complete options chain
        var options = new List<OptionRecord>();
        for (int dte = 0; dte <= 45; dte++)
        {
            if (dte <= 7 || dte % 7 == 0) // Daily for first week, then weekly
            {
                options.AddRange(GenerateOptionsForExpiry(symbol, tsUtc, dte, 25));
            }
        }
        
        await _ingestion.IngestOptionsChainAsync(options, Guid.NewGuid().ToString());
    }

    private async Task SeedPartialOptionsChain(string symbol, DateTime tsUtc, int strikeCount)
    {
        // Add minimal underlying data
        await _ingestion.IngestUnderlyingBarsAsync(new[]
        {
            new UnderlyingBar(symbol, tsUtc, 4500, 4510, 4490, 4505, 100000)
        });

        // Add limited options
        var options = GenerateOptionsForExpiry(symbol, tsUtc, 7, strikeCount / 2);
        await _ingestion.IngestOptionsChainAsync(options, Guid.NewGuid().ToString());
    }

    private List<OptionRecord> GenerateOptionsForExpiry(
        string symbol, 
        DateTime tsUtc, 
        int dte,
        int strikesPerSide = 20)
    {
        var options = new List<OptionRecord>();
        var baseStrike = 4500m;
        var expiry = DateOnly.FromDateTime(tsUtc.AddDays(dte));
        
        for (int i = -strikesPerSide; i <= strikesPerSide; i++)
        {
            var strike = baseStrike + (i * 25);
            
            // Call option
            options.Add(new OptionRecord(
                Symbol: symbol,
                Session: DateOnly.FromDateTime(tsUtc),
                TsUtc: tsUtc,
                Expiry: expiry,
                Strike: strike,
                Right: Right.Call,
                Bid: Math.Max(0, 10m - Math.Abs(i) * 0.5m),
                Ask: Math.Max(0, 11m - Math.Abs(i) * 0.5m),
                Mid: Math.Max(0, 10.5m - Math.Abs(i) * 0.5m),
                Last: Math.Max(0, 10.5m - Math.Abs(i) * 0.5m),
                Iv: 0.20m + Math.Abs(i) * 0.001m,
                Delta: i > 0 ? 0.5 - i * 0.02 : 0.5 + Math.Abs(i) * 0.02,
                Gamma: 0.01,
                Theta: -0.05,
                Vega: 0.1,
                Oi: 1000 - Math.Abs(i) * 10,
                Volume: 500 - Math.Abs(i) * 5
            ));
            
            // Put option
            options.Add(new OptionRecord(
                Symbol: symbol,
                Session: DateOnly.FromDateTime(tsUtc),
                TsUtc: tsUtc,
                Expiry: expiry,
                Strike: strike,
                Right: Right.Put,
                Bid: Math.Max(0, 10m - Math.Abs(i) * 0.5m),
                Ask: Math.Max(0, 11m - Math.Abs(i) * 0.5m),
                Mid: Math.Max(0, 10.5m - Math.Abs(i) * 0.5m),
                Last: Math.Max(0, 10.5m - Math.Abs(i) * 0.5m),
                Iv: 0.20m + Math.Abs(i) * 0.001m,
                Delta: i < 0 ? -0.5 - Math.Abs(i) * 0.02 : -0.5 + i * 0.02,
                Gamma: 0.01,
                Theta: -0.05,
                Vega: 0.1,
                Oi: 1000 - Math.Abs(i) * 10,
                Volume: 500 - Math.Abs(i) * 5
            ));
        }
        
        return options;
    }

    public void Dispose()
    {
        _probe?.Dispose();
        _ingestion?.Dispose();
        
        // Clean up test database
        if (File.Exists(_testDbPath))
        {
            try { File.Delete(_testDbPath); } catch { }
        }
    }
}