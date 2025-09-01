/*
using Stroll.Alpha.Dataset.Models;
using Stroll.Alpha.Dataset.Ingestion;
using Stroll.Alpha.Dataset.Storage;

namespace Stroll.Alpha.Dataset.Tools;

/// <summary>
/// Seeds the SQLite database with sample options and underlying data for testing
/// </summary>
public sealed class DataSeeder : IDisposable
{
    private readonly string _dbPath;
    private readonly DataIngestionService _ingestion;
    private readonly SqliteDataProvider _provider;

    public DataSeeder(string dbPath)
    {
        _dbPath = dbPath;
        _provider = new SqliteDataProvider(dbPath);
        _ingestion = new DataIngestionService(dbPath);
    }

    /// <summary>
    /// Seeds a full year of SPX data with realistic patterns
    /// </summary>
    public async Task SeedSPXDataAsync(int year = 2024, CancellationToken ct = default)
    {
        Console.WriteLine($"Seeding SPX data for {year}...");
        
        var startDate = new DateTime(year, 1, 1);
        var endDate = new DateTime(year, 12, 31);
        var currentDate = startDate;
        
        var random = new Random(42); // Deterministic for testing
        var basePrice = 4500m; // Starting SPX price
        var volatility = 0.20m; // Base volatility

        while (currentDate <= endDate)
        {
            // Skip weekends
            if (currentDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                currentDate = currentDate.AddDays(1);
                continue;
            }

            Console.Write($"\rSeeding {currentDate:yyyy-MM-dd}...");

            // Generate daily price movement
            var dailyReturn = (decimal)(random.NextDouble() - 0.5) * 0.04m; // ±2% daily moves
            basePrice *= (1 + dailyReturn);

            // Generate intraday bars
            await SeedDailyBarsAsync("SPX", currentDate, basePrice, random, ct);

            // Generate options chain every few days
            if (currentDate.Day % 3 == 0) // Every 3rd day to reduce data volume
            {
                await SeedOptionsChainAsync("SPX", currentDate, basePrice, volatility, random, ct);
            }

            currentDate = currentDate.AddDays(1);
        }

        Console.WriteLine($"\nCompleted seeding SPX data for {year}");
    }

    /// <summary>
    /// Seeds a single trading day with minute bars
    /// </summary>
    private async Task SeedDailyBarsAsync(
        string symbol, 
        DateTime date, 
        decimal openPrice,
        Random random,
        CancellationToken ct)
    {
        var bars = new List<UnderlyingBar>();
        var marketOpen = date.Date.AddHours(14.5); // 9:30 AM ET in UTC (approximate)
        var currentPrice = openPrice;
        var high = openPrice;
        var low = openPrice;

        for (int minute = 0; minute < 390; minute++) // 6.5 hour trading day
        {
            var timestamp = marketOpen.AddMinutes(minute);
            
            // Simulate minute price movement
            var change = (decimal)(random.NextDouble() - 0.5) * 0.002m; // Small intraday moves
            currentPrice *= (1 + change);
            
            high = Math.Max(high, currentPrice * 1.001m);
            low = Math.Min(low, currentPrice * 0.999m);
            
            var volume = random.Next(50000, 200000);

            bars.Add(new UnderlyingBar(
                symbol,
                timestamp,
                Math.Round(currentPrice * 0.9995m, 2), // Open slightly different
                Math.Round(high, 2),
                Math.Round(low, 2),
                Math.Round(currentPrice, 2),
                volume
            ));

            // Reset high/low periodically for realism
            if (minute % 60 == 0)
            {
                high = currentPrice;
                low = currentPrice;
            }
        }

        await _ingestion.IngestUnderlyingBarsAsync(bars, ct);
    }

    /// <summary>
    /// Seeds a complete options chain for a given date
    /// </summary>
    private async Task SeedOptionsChainAsync(
        string symbol,
        DateTime date,
        decimal underlyingPrice,
        decimal baseVolatility,
        Random random,
        CancellationToken ct)
    {
        var options = new List<OptionRecord>();
        var sessionDate = DateOnly.FromDateTime(date);
        var timestamp = date.Date.AddHours(15); // 11 AM ET

        // Generate expiries: weeklies for first month, then monthlies
        var expiries = GenerateExpiries(date);

        foreach (var expiry in expiries)
        {
            var dte = (expiry.ToDateTime(TimeOnly.MinValue) - date).Days;
            if (dte < 0 || dte > 45) continue;

            // Calculate time-based volatility adjustment
            var timeAdjustment = Math.Sqrt((double)dte / 30.0); // Vol increases with time
            var volatility = baseVolatility * (decimal)timeAdjustment;
            
            // Generate strike ladder
            var atmStrike = Math.Round(underlyingPrice / 25) * 25; // Round to nearest $25
            
            for (int i = -40; i <= 40; i++) // ±$1000 from ATM
            {
                var strike = atmStrike + (i * 25);
                if (strike <= 0) continue;

                var moneyness = strike / underlyingPrice;
                if (moneyness < 0.85m || moneyness > 1.15m) continue; // ±15% moneyness filter

                // Calculate theoretical prices using simplified Black-Scholes
                var (callPrice, putPrice, delta, gamma, theta, vega) = 
                    CalculateOptionPrices(underlyingPrice, strike, dte, volatility, 0.05m);

                // Add some noise to prices
                var priceNoise = (decimal)(random.NextDouble() - 0.5) * 0.1m;
                callPrice = Math.Max(0.01m, callPrice + priceNoise);
                putPrice = Math.Max(0.01m, putPrice + priceNoise);

                var bid = Math.Max(0.01m, callPrice * 0.98m);
                var ask = callPrice * 1.02m;
                var mid = (bid + ask) / 2;

                // Add volatility smile
                var smileAdjustment = Math.Abs(moneyness - 1) * 0.05m;
                volatility += smileAdjustment;

                // Call option
                options.Add(new OptionRecord(
                    Symbol: symbol,
                    Session: sessionDate,
                    TsUtc: timestamp,
                    Expiry: expiry,
                    Strike: strike,
                    Right: Right.Call,
                    Bid: Math.Round(bid, 2),
                    Ask: Math.Round(ask, 2),
                    Mid: Math.Round(mid, 2),
                    Last: Math.Round(mid * (0.98m + (decimal)random.NextDouble() * 0.04m), 2),
                    Iv: Math.Round(volatility, 4),
                    Delta: Math.Round(delta, 4),
                    Gamma: Math.Round(gamma, 6),
                    Theta: Math.Round(theta, 4),
                    Vega: Math.Round(vega, 4),
                    Oi: random.Next(100, 5000),
                    Volume: random.Next(0, 1000)
                ));

                // Put option
                var putBid = Math.Max(0.01m, putPrice * 0.98m);
                var putAsk = putPrice * 1.02m;
                var putMid = (putBid + putAsk) / 2;

                options.Add(new OptionRecord(
                    Symbol: symbol,
                    Session: sessionDate,
                    TsUtc: timestamp,
                    Expiry: expiry,
                    Strike: strike,
                    Right: Right.Put,
                    Bid: Math.Round(putBid, 2),
                    Ask: Math.Round(putAsk, 2),
                    Mid: Math.Round(putMid, 2),
                    Last: Math.Round(putMid * (0.98m + (decimal)random.NextDouble() * 0.04m), 2),
                    Iv: Math.Round(volatility, 4),
                    Delta: Math.Round(-delta, 4),
                    Gamma: Math.Round(gamma, 6),
                    Theta: Math.Round(theta, 4),
                    Vega: Math.Round(vega, 4),
                    Oi: random.Next(100, 5000),
                    Volume: random.Next(0, 1000)
                ));
            }
        }

        if (options.Any())
        {
            await _ingestion.IngestOptionsChainAsync(options, Guid.NewGuid().ToString(), ct);
        }
    }

    private List<DateOnly> GenerateExpiries(DateTime baseDate)
    {
        var expiries = new List<DateOnly>();
        var current = baseDate;

        // Add weeklies for next 8 weeks (up to ~56 days, but we'll filter to 45)
        for (int week = 0; week < 8; week++)
        {
            // Find next Friday
            var friday = current.AddDays(((int)DayOfWeek.Friday - (int)current.DayOfWeek + 7) % 7);
            if (week > 0) friday = friday.AddDays(7 * (week - 1));
            
            var expiry = DateOnly.FromDateTime(friday);
            if (!expiries.Contains(expiry) && (friday - baseDate).Days <= 45)
            {
                expiries.Add(expiry);
            }
        }

        // Add monthly expiries for good measure
        for (int month = 0; month < 3; month++)
        {
            var monthlyExpiry = baseDate.AddMonths(month + 1);
            // Third Friday of the month
            var firstFriday = new DateTime(monthlyExpiry.Year, monthlyExpiry.Month, 1);
            while (firstFriday.DayOfWeek != DayOfWeek.Friday)
                firstFriday = firstFriday.AddDays(1);
            var thirdFriday = firstFriday.AddDays(14);
            
            var expiry = DateOnly.FromDateTime(thirdFriday);
            if (!expiries.Contains(expiry) && (thirdFriday - baseDate).Days <= 45)
            {
                expiries.Add(expiry);
            }
        }

        return expiries.OrderBy(e => e).ToList();
    }

    /// <summary>
    /// Simplified Black-Scholes calculation for seeding data
    /// </summary>
    private (decimal callPrice, decimal putPrice, double delta, double gamma, double theta, double vega) 
        CalculateOptionPrices(decimal spot, decimal strike, int dte, decimal volatility, decimal rate)
    {
        if (dte == 0) 
        {
            var callIntrinsic = Math.Max(0, spot - strike);
            var putIntrinsic = Math.Max(0, strike - spot);
            return (callIntrinsic, putIntrinsic, callIntrinsic > 0 ? 1.0 : 0.0, 0.0, 0.0, 0.0);
        }

        var S = (double)spot;
        var K = (double)strike;
        var T = dte / 365.0;
        var sigma = (double)volatility;
        var r = (double)rate;

        var d1 = (Math.Log(S / K) + (r + 0.5 * sigma * sigma) * T) / (sigma * Math.Sqrt(T));
        var d2 = d1 - sigma * Math.Sqrt(T);

        var nd1 = NormalCDF(d1);
        var nd2 = NormalCDF(d2);
        var nnd1 = NormalCDF(-d1);
        var nnd2 = NormalCDF(-d2);

        var callPrice = S * nd1 - K * Math.Exp(-r * T) * nd2;
        var putPrice = K * Math.Exp(-r * T) * nnd2 - S * nnd1;

        var delta = nd1;
        var gamma = NormalPDF(d1) / (S * sigma * Math.Sqrt(T));
        var theta = -(S * NormalPDF(d1) * sigma / (2 * Math.Sqrt(T)) + r * K * Math.Exp(-r * T) * nd2) / 365.0;
        var vega = S * NormalPDF(d1) * Math.Sqrt(T) / 100.0; // Divide by 100 for 1% vol move

        return ((decimal)Math.Max(0.01, callPrice), (decimal)Math.Max(0.01, putPrice), delta, gamma, theta, vega);
    }

    private static double NormalCDF(double x) =>
        0.5 * (1 + Erf(x / Math.Sqrt(2)));

    private static double NormalPDF(double x) =>
        Math.Exp(-0.5 * x * x) / Math.Sqrt(2 * Math.PI);

    private static double Erf(double x)
    {
        const double a1 =  0.254829592;
        const double a2 = -0.284496736;
        const double a3 =  1.421413741;
        const double a4 = -1.453152027;
        const double a5 =  1.061405429;
        const double p  =  0.3275911;

        var sign = x < 0 ? -1 : 1;
        x = Math.Abs(x);

        var t = 1.0 / (1.0 + p * x);
        var y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);

        return sign * y;
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _ingestion?.Dispose();
    }
}
*/