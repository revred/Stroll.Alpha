using Stroll.Alpha.Dataset.Probes;
using System.Text.Json;

// Parse command line arguments
var symbol = GetArg("--symbol", "SPX");
var date = DateOnly.Parse(GetArg("--date", DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd")));
var depthArg = GetArg("--depth", "standard");
var dbPath = GetArg("--db", Path.Combine("C:", "Code", "Stroll.Theta.DB", "stroll_theta.db"));
var outputFormat = GetArg("--output", "summary"); // summary, detailed, json

// Parse depth
var depth = depthArg.ToLower() switch
{
    "minimal" or "1" => ProbeDepth.Minimal,
    "standard" or "2" => ProbeDepth.Standard,
    "deep" or "3" => ProbeDepth.Deep,
    "complete" or "4" => ProbeDepth.Complete,
    _ => ProbeDepth.Standard
};

// Calculate timestamp (US market open in UTC)
var tsUtc = date.ToDateTime(TimeOnly.Parse("14:30:00"));

// Run probe
using var probe = new ChainCompletenessProbe(dbPath);
var report = await probe.ProbeAsync(symbol, tsUtc, depth);

// Output results based on format
switch (outputFormat.ToLower())
{
    case "json":
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions 
        { 
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        break;
        
    case "detailed":
        PrintDetailedReport(report);
        break;
        
    default: // summary
        PrintSummaryReport(report);
        break;
}

// Return appropriate exit code
Environment.Exit(report.OverallScore >= 0.7m ? 0 : 1);

void PrintSummaryReport(ChainProbeReport report)
{
    Console.WriteLine($"=== Probe Report: {report.Symbol} @ {report.SessionDate} ===");
    Console.WriteLine($"Overall Score: {report.OverallScore * 100:F1}% [{GetScoreGrade(report.OverallScore)}]");
    Console.WriteLine();
    
    Console.WriteLine("Component Scores:");
    Console.WriteLine($"  Underlying Data: {report.UnderlyingCompleteness * 100:F1}%");
    Console.WriteLine($"  Options Chain:   {report.ChainCompleteness * 100:F1}%");
    Console.WriteLine($"  Greeks Data:     {report.GreeksCompleteness * 100:F1}%");
    Console.WriteLine($"  Data Freshness:  {report.DataFreshness * 100:F1}%");
    Console.WriteLine();
    
    Console.WriteLine($"Coverage: {report.TotalExpiries} expiries, {report.UnderlyingBarsFound} bars");
    
    if (report.Warnings.Any())
    {
        Console.WriteLine();
        Console.WriteLine($"⚠ Warnings ({report.Warnings.Count}):");
        foreach (var warning in report.Warnings.Take(5))
        {
            Console.WriteLine($"  • {warning}");
        }
        if (report.Warnings.Count > 5)
        {
            Console.WriteLine($"  ... and {report.Warnings.Count - 5} more");
        }
    }
    
    if (report.Errors.Any())
    {
        Console.WriteLine();
        Console.WriteLine($"❌ Errors ({report.Errors.Count}):");
        foreach (var error in report.Errors)
        {
            Console.WriteLine($"  • {error}");
        }
    }
}

void PrintDetailedReport(ChainProbeReport report)
{
    PrintSummaryReport(report);
    
    if (report.ExpiryReports.Any())
    {
        Console.WriteLine();
        Console.WriteLine("=== Expiry Details ===");
        Console.WriteLine("DTE  | Expiry     | Puts | Calls | Score | Issues");
        Console.WriteLine("-----|------------|------|-------|-------|--------");
        
        foreach (var expiry in report.ExpiryReports.OrderBy(e => e.DTE).Take(10))
        {
            Console.WriteLine($"{expiry.DTE,3}  | {expiry.Expiry,-10} | {expiry.PutStrikes,4} | {expiry.CallStrikes,5} | {expiry.Score * 100,5:F1}% | {string.Join(", ", expiry.Issues.Take(2))}");
        }
        
        if (report.ExpiryReports.Count > 10)
        {
            Console.WriteLine($"... and {report.ExpiryReports.Count - 10} more expiries");
        }
    }
    
    // Provide actionable recommendations
    Console.WriteLine();
    Console.WriteLine("=== Recommendations ===");
    
    if (report.OverallScore < 0.9m)
    {
        if (report.UnderlyingCompleteness < 0.9m)
            Console.WriteLine("• Ingest missing underlying bar data");
            
        if (report.ChainCompleteness < 0.9m)
            Console.WriteLine("• Fill gaps in options chain (check strike coverage)");
            
        if (report.GreeksCompleteness < 0.9m)
            Console.WriteLine("• Calculate missing greeks values");
            
        if (report.DataFreshness < 0.9m)
            Console.WriteLine("• Update stale data to current timestamp");
    }
    else
    {
        Console.WriteLine("✓ Data quality meets requirements");
    }
}

string GetScoreGrade(decimal score) => score switch
{
    >= 0.95m => "EXCELLENT",
    >= 0.9m => "GOOD",
    >= 0.8m => "ACCEPTABLE",
    >= 0.7m => "MARGINAL",
    >= 0.6m => "POOR",
    _ => "CRITICAL"
};

string GetArg(string name, string def)
{
    var i = Array.FindIndex(args, a => a == name);
    if (i >= 0 && i + 1 < args.Length) return args[i + 1];
    return def;
}
