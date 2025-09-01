using Microsoft.Data.Sqlite;
using Stroll.Alpha.Dataset.Generation;
using Stroll.Alpha.Dataset.Programs;

namespace Stroll.Alpha.Dataset;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            ShowHelp();
            return 0;
        }

        try
        {
            var command = args[0].ToLower();
            return command switch
            {
                "init-schema" => await HandleInitSchema(args),
                "generate-historical" => await HandleGenerateHistorical(args),
                "generate-multi-symbol" => await HandleGenerateMultiSymbol(args),
                "generate-minute-bars" => await MinuteBarProgram.RunAsync(args[1..]),
                "migrate-sixty" => await HandleMigrateSixty(args),
                _ => HandleUnknownCommand(command)
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> HandleInitSchema(string[] args)
    {
        var dbPath = args.Length > 1 ? args[1] : @"C:\Code\Stroll.Theta.DB\stroll_theta.db";
        
        Console.WriteLine($"Initializing schema for database: {dbPath}");
        
        // Ensure directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        
        // Read and execute schema
        var schemaPath = @"C:\Code\Stroll.Theta.DB\schema.sql";
        if (!File.Exists(schemaPath))
        {
            Console.WriteLine($"Schema file not found: {schemaPath}");
            return 1;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        
        var schema = await File.ReadAllTextAsync(schemaPath);
        using var command = connection.CreateCommand();
        command.CommandText = schema;
        await command.ExecuteNonQueryAsync();
        
        Console.WriteLine("Database schema initialized successfully");
        return 0;
    }

    static async Task<int> HandleGenerateHistorical(string[] args)
    {
        string? dbPath = null;
        DateTime? startDate = null;
        DateTime? endDate = null;
        int batchSize = 5;

        // Parse arguments
        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) break;
            
            switch (args[i].ToLower())
            {
                case "--db-path":
                    dbPath = args[i + 1];
                    break;
                case "--start-date":
                    if (DateTime.TryParse(args[i + 1], out var start))
                        startDate = start;
                    break;
                case "--end-date":
                    if (DateTime.TryParse(args[i + 1], out var end))
                        endDate = end;
                    break;
                case "--batch-size":
                    int.TryParse(args[i + 1], out batchSize);
                    break;
            }
        }

        dbPath ??= @"C:\Code\Stroll.Theta.DB\stroll_theta.db";
        startDate ??= new DateTime(2025, 8, 29);
        endDate ??= new DateTime(2018, 1, 1);

        Console.WriteLine($"Generating historical data:");
        Console.WriteLine($"  Database: {dbPath}");
        Console.WriteLine($"  Start: {startDate:yyyy-MM-dd}");
        Console.WriteLine($"  End: {endDate:yyyy-MM-dd}");
        Console.WriteLine($"  Batch size: {batchSize}");
        Console.WriteLine();

        var generator = new HistoricalDataGenerator(dbPath);
        await generator.GenerateAsync(startDate.Value, endDate.Value, batchSize);

        return 0;
    }

    static async Task<int> HandleGenerateMultiSymbol(string[] args)
    {
        string? dbPath = null;
        DateTime? startDate = null;
        DateTime? endDate = null;
        int batchSize = 5;
        var symbols = new List<string>();

        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length && args[i] != "--symbols") break;
            
            switch (args[i].ToLower())
            {
                case "--db-path":
                    dbPath = args[i + 1];
                    break;
                case "--start-date":
                    if (DateTime.TryParse(args[i + 1], out var start))
                        startDate = start;
                    break;
                case "--end-date":
                    if (DateTime.TryParse(args[i + 1], out var end))
                        endDate = end;
                    break;
                case "--batch-size":
                    int.TryParse(args[i + 1], out batchSize);
                    break;
                case "--symbols":
                    // Handle comma-separated symbols
                    if (i + 1 < args.Length)
                    {
                        symbols.AddRange(args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim().ToUpper()));
                    }
                    break;
            }
        }

        dbPath ??= @"C:\Code\Stroll.Theta.DB\stroll_theta.db";
        startDate ??= new DateTime(2025, 8, 29);
        endDate ??= new DateTime(2018, 1, 1);
        
        if (!symbols.Any())
        {
            symbols.AddRange(new[] { "XSP", "VIX", "GLD", "USO" }); // Default symbols (excluding SPX as it's already running)
        }

        Console.WriteLine($"Generating multi-symbol historical data:");
        Console.WriteLine($"  Database: {dbPath}");
        Console.WriteLine($"  Symbols: {string.Join(", ", symbols)}");
        Console.WriteLine($"  Start: {startDate:yyyy-MM-dd}");
        Console.WriteLine($"  End: {endDate:yyyy-MM-dd}");
        Console.WriteLine($"  Batch size: {batchSize}");
        Console.WriteLine();

        var generator = new MultiSymbolDataGenerator(dbPath);
        await generator.GenerateAsync(symbols.ToArray(), startDate.Value, endDate.Value, batchSize);

        return 0;
    }

    static async Task<int> HandleMigrateSixty(string[] args)
    {
        var sixtyDir = args.Length > 1 ? args[1] : @"C:\code\Stroll.Theta.Sixty";
        var batchSize = args.Length > 2 ? int.Parse(args[2]) : 5;

        Console.WriteLine($"Migrating Parquet to SQLite hourly databases:");
        Console.WriteLine($"  Directory: {sixtyDir}");
        Console.WriteLine($"  Batch size: {batchSize} files");

        await SixtyMigrationProgram.RunAsync(new[] { sixtyDir, batchSize.ToString() });
        return 0;
    }

    static int HandleUnknownCommand(string command)
    {
        Console.WriteLine($"Unknown command: {command}");
        ShowHelp();
        return 1;
    }

    static void ShowHelp()
    {
        Console.WriteLine("Stroll.Alpha.Dataset — Historical options data generator");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  init-schema [db_path]                    Initialize database schema");
        Console.WriteLine("  generate-historical [options]           Generate historical data (SPX)");
        Console.WriteLine("  generate-multi-symbol [options]         Generate multi-symbol historical data");
        Console.WriteLine("  generate-minute-bars [options]          Generate 1-minute bars to Parquet files");
        Console.WriteLine("  migrate-sixty [dir] [batch_size]        Migrate Parquet to SQLite hourly databases");
        Console.WriteLine();
        Console.WriteLine("Generate Historical Options:");
        Console.WriteLine("  --db-path PATH        Database file path");
        Console.WriteLine("  --start-date DATE     Start date (YYYY-MM-DD)");
        Console.WriteLine("  --end-date DATE       End date (YYYY-MM-DD)");
        Console.WriteLine("  --batch-size N        Progress logging batch size");
        Console.WriteLine();
        Console.WriteLine("Multi-Symbol Options:");
        Console.WriteLine("  --symbols LIST        Comma-separated symbols (XSP,VIX,GLD,USO)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run init-schema");
        Console.WriteLine("  dotnet run generate-historical --start-date 2025-08-29 --end-date 2018-01-01");
        Console.WriteLine("  dotnet run generate-multi-symbol --symbols XSP,VIX,GLD,USO --start-date 2025-08-29");
    }
}