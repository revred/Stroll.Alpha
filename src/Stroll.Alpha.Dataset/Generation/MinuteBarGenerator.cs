using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using Stroll.Alpha.Dataset.Models;

namespace Stroll.Alpha.Dataset.Generation;

public class MinuteBarGenerator
{
    private readonly string _sqliteDbPath;
    private readonly string _parquetOutputPath;
    private readonly Random _random = new();

    public MinuteBarGenerator(string sqliteDbPath, string parquetOutputPath)
    {
        _sqliteDbPath = sqliteDbPath ?? throw new ArgumentNullException(nameof(sqliteDbPath));
        _parquetOutputPath = parquetOutputPath ?? throw new ArgumentNullException(nameof(parquetOutputPath));
        
        Directory.CreateDirectory(_parquetOutputPath);
    }

    public async Task GenerateMinuteBarsForDateAsync(DateOnly date, string symbol)
    {
        Console.WriteLine($"🕐 Generating 1-minute bars for {symbol} on {date:yyyy-MM-dd}");

        // Skip weekends and holidays
        if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
        {
            Console.WriteLine($"  📅 Skipping weekend: {date:yyyy-MM-dd}");
            return;
        }

        var marketHours = GetMarketHours();
        var basePrice = GetBasePriceFromSqlite(date, symbol);
        
        foreach (var hour in marketHours)
        {
            await GenerateHourlyBarsAsync(date, symbol, hour, basePrice);
        }
    }

    private async Task GenerateHourlyBarsAsync(DateOnly date, string symbol, int hour, decimal basePrice)
    {
        var outputDir = Path.Combine(_parquetOutputPath, "bars", 
                                    date.ToString("yyyy"), 
                                    date.ToString("MM"), 
                                    date.ToString("dd"));
        
        Directory.CreateDirectory(outputDir);
        
        var fileName = $"{symbol}_{hour:D2}.parquet";
        var filePath = Path.Combine(outputDir, fileName);

        if (File.Exists(filePath))
        {
            Console.WriteLine($"  ⚡ Hour {hour:D2} already exists for {symbol}");
            return;
        }

        var minuteBars = GenerateMinuteBarsForHour(date, symbol, hour, basePrice);
        
        await WriteParquetFileAsync(filePath, minuteBars);
        
        Console.WriteLine($"  ✅ Generated {fileName} ({minuteBars.Count} bars)");
    }

    private List<MinuteBar> GenerateMinuteBarsForHour(DateOnly date, string symbol, int hour, decimal basePrice)
    {
        var bars = new List<MinuteBar>();
        var currentPrice = basePrice;
        
        // Generate volatility for this hour
        var hourlyVolatility = GetHourlyVolatility(hour, symbol);
        
        for (int minute = 0; minute < 60; minute++)
        {
            var timestamp = date.ToDateTime(new TimeOnly(hour, minute, 0));
            var timestampUtc = TimeZoneInfo.ConvertTimeToUtc(timestamp, 
                TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));

            // Price movement for this minute
            var priceChange = (decimal)((_random.NextDouble() - 0.5) * 2 * (double)hourlyVolatility * (double)currentPrice * 0.01);
            var newPrice = Math.Max(0.01m, currentPrice + priceChange);
            
            // Generate OHLC for the minute
            var high = Math.Max(currentPrice, newPrice) * (1 + (decimal)(_random.NextDouble() * 0.002));
            var low = Math.Min(currentPrice, newPrice) * (1 - (decimal)(_random.NextDouble() * 0.002));
            var open = currentPrice;
            var close = newPrice;
            
            // Generate volume based on hour and symbol
            var volume = GenerateVolumeForMinute(hour, symbol, minute);
            var vwap = (open + high + low + close) / 4m;

            bars.Add(new MinuteBar
            {
                TimestampUtc = timestampUtc,
                TimestampEt = timestamp,
                Symbol = symbol,
                Open = Math.Round(open, 2),
                High = Math.Round(high, 2),
                Low = Math.Round(low, 2),
                Close = Math.Round(close, 2),
                Volume = volume,
                Vwap = Math.Round(vwap, 4)
            });
            
            currentPrice = newPrice;
        }
        
        return bars;
    }

    private decimal GetHourlyVolatility(int hour, string symbol)
    {
        // Higher volatility at market open/close
        var baseVolatility = symbol switch
        {
            "SPX" => 0.15m,
            "XSP" => 0.15m,
            "VIX" => 0.80m,
            "QQQ" => 0.20m,
            "GLD" => 0.10m,
            "USO" => 0.25m,
            _ => 0.18m
        };

        // Time-of-day multiplier
        var timeMultiplier = hour switch
        {
            9 or 10 => 1.5m,  // Market open volatility
            15 => 1.3m,        // Closing hour
            11 or 14 => 0.7m,  // Lunch lull
            _ => 1.0m
        };

        return baseVolatility * timeMultiplier;
    }

    private long GenerateVolumeForMinute(int hour, string symbol, int minute)
    {
        var baseVolume = symbol switch
        {
            "SPX" => 2000L,
            "XSP" => 1800L,
            "VIX" => 1500L,
            "QQQ" => 2500L,
            "GLD" => 1200L,
            "USO" => 1000L,
            _ => 1500L
        };

        // Time-of-day multiplier for volume
        var timeMultiplier = hour switch
        {
            9 or 10 => 2.0,    // High volume at open
            15 => 1.8,         // High volume at close
            12 or 13 => 0.6,   // Low volume at lunch
            _ => 1.0
        };

        var randomFactor = 0.7 + (_random.NextDouble() * 0.6); // 0.7 to 1.3
        
        return (long)(baseVolume * timeMultiplier * randomFactor);
    }

    private decimal GetBasePriceFromSqlite(DateOnly date, string symbol)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={_sqliteDbPath}");
            connection.Open();
            
            var query = @"
                SELECT close 
                FROM underlying_bars 
                WHERE DATE(ts_utc) = @date AND symbol = @symbol 
                ORDER BY ts_utc DESC 
                LIMIT 1";
            
            using var command = new SqliteCommand(query, connection);
            command.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("@symbol", symbol);
            
            var result = command.ExecuteScalar();
            if (result != null && decimal.TryParse(result.ToString(), out var price))
            {
                return price;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  Error getting base price from SQLite: {ex.Message}");
        }

        // Fallback to default prices
        return symbol switch
        {
            "SPX" => 4800m,
            "XSP" => 480m,
            "VIX" => 20m,
            "QQQ" => 380m,
            "GLD" => 185m,
            "USO" => 75m,
            _ => 100m
        };
    }

    private static int[] GetMarketHours()
    {
        // 9:30 AM to 4:00 PM ET = hours 9, 10, 11, 12, 13, 14, 15
        // Note: 9 represents 9:30-10:30, 15 represents 3:00-4:00
        return [9, 10, 11, 12, 13, 14, 15];
    }

    private async Task WriteParquetFileAsync(string filePath, List<MinuteBar> bars)
    {
        // Create schema
        var schema = new ParquetSchema(
            new DataField<DateTime>("ts_utc"),
            new DataField<DateTime>("ts_et"),
            new DataField<string>("symbol"),
            new DataField<double>("open"),  // Changed to double for better Parquet compatibility
            new DataField<double>("high"),
            new DataField<double>("low"),
            new DataField<double>("close"),
            new DataField<long>("volume"),
            new DataField<double>("vwap")
        );

        // Convert data to arrays
        var timestampUtc = bars.Select(b => b.TimestampUtc).ToArray();
        var timestampEt = bars.Select(b => b.TimestampEt).ToArray();
        var symbol = bars.Select(b => b.Symbol).ToArray();
        var open = bars.Select(b => (double)b.Open).ToArray();
        var high = bars.Select(b => (double)b.High).ToArray();
        var low = bars.Select(b => (double)b.Low).ToArray();
        var close = bars.Select(b => (double)b.Close).ToArray();
        var volume = bars.Select(b => b.Volume).ToArray();
        var vwap = bars.Select(b => (double)b.Vwap).ToArray();

        // Write to Parquet file using column-based approach
        using var fileStream = File.Create(filePath);
        using var parquetWriter = await ParquetWriter.CreateAsync(schema, fileStream);
        using var groupWriter = parquetWriter.CreateRowGroup();

        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[0], timestampUtc));
        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[1], timestampEt));
        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[2], symbol));
        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[3], open));
        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[4], high));
        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[5], low));
        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[6], close));
        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[7], volume));
        await groupWriter.WriteColumnAsync(new DataColumn(schema.DataFields[8], vwap));
    }
}

public class MinuteBar
{
    public DateTime TimestampUtc { get; set; }
    public DateTime TimestampEt { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public decimal Vwap { get; set; }
}