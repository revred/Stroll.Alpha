using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stroll.Alpha.Dataset.Generation;

namespace Stroll.Alpha.Dataset.Programs;

public class MinuteBarProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: generate-minute-bars --db-path <path> --output-path <path> [--symbols <symbol1,symbol2>] [--start-date <yyyy-MM-dd>] [--end-date <yyyy-MM-dd>]");
            return 1;
        }

        var dbPath = "";
        var outputPath = "";
        var symbols = new List<string> { "SPX", "XSP", "VIX", "QQQ", "GLD", "USO" };
        var startDate = DateOnly.FromDateTime(DateTime.Now.AddDays(-30)); // Default: last 30 days
        var endDate = DateOnly.FromDateTime(DateTime.Now);

        // Parse arguments
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db-path":
                    dbPath = args[++i];
                    break;
                case "--output-path":
                    outputPath = args[++i];
                    break;
                case "--symbols":
                    symbols = args[++i].Split(',').Select(s => s.Trim().ToUpper()).ToList();
                    break;
                case "--start-date":
                    startDate = DateOnly.Parse(args[++i]);
                    break;
                case "--end-date":
                    endDate = DateOnly.Parse(args[++i]);
                    break;
            }
        }

        if (string.IsNullOrEmpty(dbPath) || string.IsNullOrEmpty(outputPath))
        {
            Console.WriteLine("❌ --db-path and --output-path are required");
            return 1;
        }

        Console.WriteLine("🕐 STROLL.ALPHA MINUTE BAR GENERATOR");
        Console.WriteLine("═══════════════════════════════════════");
        Console.WriteLine($"📊 Database: {dbPath}");
        Console.WriteLine($"📁 Output: {outputPath}");
        Console.WriteLine($"📅 Date Range: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        Console.WriteLine($"🎯 Symbols: {string.Join(", ", symbols)}");
        Console.WriteLine();

        var generator = new MinuteBarGenerator(dbPath, outputPath);
        var totalDays = endDate.DayNumber - startDate.DayNumber + 1;
        var processedDays = 0;
        var generatedFiles = 0;

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            processedDays++;
            
            Console.WriteLine($"📅 Processing {date:yyyy-MM-dd} ({processedDays}/{totalDays} days)");

            foreach (var symbol in symbols)
            {
                try
                {
                    await generator.GenerateMinuteBarsForDateAsync(date, symbol);
                    generatedFiles += 7; // 7 hours per symbol per day
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ❌ Error generating {symbol}: {ex.Message}");
                }
            }

            // Progress update every 10 days
            if (processedDays % 10 == 0)
            {
                Console.WriteLine($"🚀 Progress: {processedDays} days completed, ~{generatedFiles} Parquet files generated");
            }
        }

        Console.WriteLine();
        Console.WriteLine("📊 GENERATION COMPLETE!");
        Console.WriteLine($"✅ Processed: {processedDays} trading days");
        Console.WriteLine($"📁 Generated: ~{generatedFiles} Parquet files");
        Console.WriteLine($"💾 Output: {outputPath}");

        return 0;
    }
}