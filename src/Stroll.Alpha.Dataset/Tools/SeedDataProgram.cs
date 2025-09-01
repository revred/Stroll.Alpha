using Stroll.Alpha.Dataset.Tools;

namespace Stroll.Alpha.Dataset.Tools;

public static class SeedDataProgram
{
    public static async Task<int> Main(string[] args)
    {
        var dbPath = args.Length > 0 
            ? args[0] 
            : Path.Combine("C:", "Code", "Stroll.Theta.DB", "stroll_theta.db");

        var year = args.Length > 1 && int.TryParse(args[1], out var y) 
            ? y 
            : 2024;

        Console.WriteLine($"Seeding database: {dbPath}");
        Console.WriteLine($"Target year: {year}");
        
        // Ensure directory exists
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            Console.WriteLine($"Created directory: {directory}");
        }

        try
        {
            using var seeder = new DataSeeder(dbPath);
            
            Console.WriteLine("Starting data seeding process...");
            var startTime = DateTime.Now;
            
            await seeder.SeedSPXDataAsync(year);
            
            var elapsed = DateTime.Now - startTime;
            Console.WriteLine($"Data seeding completed in {elapsed.TotalSeconds:F1} seconds");
            
            // Basic validation
            Console.WriteLine("Validating seeded data...");
            // TODO: Add validation logic here
            
            Console.WriteLine("✓ Data seeding successful!");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error during data seeding: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}