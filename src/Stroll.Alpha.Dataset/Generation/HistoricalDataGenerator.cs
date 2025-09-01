using System.Globalization;
using Microsoft.Data.Sqlite;
using MathNet.Numerics.Distributions;
using MathNet.Numerics;

namespace Stroll.Alpha.Dataset.Generation;

public sealed class HistoricalDataGenerator
{
    private readonly string _dbPath;
    private readonly Random _random;
    private readonly HashSet<DateTime> _usHolidays;

    // Market parameters
    private const decimal SpxBasePrice = 4800m;
    private const double BaseVolatility = 0.20;
    private const double RiskFreeRate = 0.05;

    public HistoricalDataGenerator(string dbPath)
    {
        _dbPath = dbPath;
        _random = new Random(42); // Deterministic seed
        _usHolidays = InitializeUSHolidays();
    }

    public async Task GenerateAsync(DateTime startDate, DateTime endDate, int batchSize = 5)
    {
        Console.WriteLine($"Starting historical data generation from {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        
        int totalBars = 0;
        int totalOptions = 0;
        int daysProcessed = 0;

        var currentDate = startDate;
        while (currentDate >= endDate)
        {
            if (IsTradingDay(currentDate))
            {
                var (bars, options) = await GenerateSingleDayAsync(currentDate);
                totalBars += bars;
                totalOptions += options;
                daysProcessed++;

                if (daysProcessed % batchSize == 0)
                {
                    Console.WriteLine($"Progress: {daysProcessed} trading days processed, {totalBars} bars, {totalOptions} options inserted");
                }
            }
            
            currentDate = currentDate.AddDays(-1);
        }

        Console.WriteLine($"Data generation complete! Processed {daysProcessed} trading days, inserted {totalBars} bars and {totalOptions} options");
    }

    private async Task<(int bars, int options)> GenerateSingleDayAsync(DateTime sessionDate)
    {
        if (!IsTradingDay(sessionDate))
            return (0, 0);

        Console.WriteLine($"Generating data for {sessionDate:yyyy-MM-dd}");

        // Generate underlying price path for the day
        var underlyingBars = GenerateUnderlyingBarsForDay(sessionDate);
        var closingPrice = underlyingBars.Last().Close;

        // Generate options chain at market close
        var optionsChain = GenerateOptionsChain(closingPrice, sessionDate);

        // Insert into database
        int barsInserted = 0;
        int optionsInserted = 0;

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        // Insert underlying bars
        using var barsCommand = connection.CreateCommand();
        barsCommand.CommandText = @"
            INSERT OR REPLACE INTO underlying_bars 
            (symbol, ts_utc, open, high, low, close, volume)
            VALUES (@symbol, @ts_utc, @open, @high, @low, @close, @volume)";

        foreach (var bar in underlyingBars)
        {
            barsCommand.Parameters.Clear();
            barsCommand.Parameters.AddWithValue("@symbol", "SPX");
            barsCommand.Parameters.AddWithValue("@ts_utc", bar.TsUtc);
            barsCommand.Parameters.AddWithValue("@open", bar.Open);
            barsCommand.Parameters.AddWithValue("@high", bar.High);
            barsCommand.Parameters.AddWithValue("@low", bar.Low);
            barsCommand.Parameters.AddWithValue("@close", bar.Close);
            barsCommand.Parameters.AddWithValue("@volume", bar.Volume);
            
            await barsCommand.ExecuteNonQueryAsync();
            barsInserted++;
        }

        // Insert options chain
        if (optionsChain.Any())
        {
            using var optionsCommand = connection.CreateCommand();
            optionsCommand.CommandText = @"
                INSERT OR REPLACE INTO options_chain
                (symbol, session_date, ts_utc, expiry_date, strike, right,
                 bid, ask, mid, last, iv, delta, gamma, theta, vega,
                 open_interest, volume, moneyness)
                VALUES (@symbol, @session_date, @ts_utc, @expiry_date, @strike, @right,
                        @bid, @ask, @mid, @last, @iv, @delta, @gamma, @theta, @vega,
                        @open_interest, @volume, @moneyness)";

            foreach (var option in optionsChain)
            {
                optionsCommand.Parameters.Clear();
                optionsCommand.Parameters.AddWithValue("@symbol", option.Symbol);
                optionsCommand.Parameters.AddWithValue("@session_date", option.SessionDate.ToString("yyyy-MM-dd"));
                optionsCommand.Parameters.AddWithValue("@ts_utc", option.TsUtc);
                optionsCommand.Parameters.AddWithValue("@expiry_date", option.ExpiryDate.ToString("yyyy-MM-dd"));
                optionsCommand.Parameters.AddWithValue("@strike", option.Strike);
                optionsCommand.Parameters.AddWithValue("@right", option.Right);
                optionsCommand.Parameters.AddWithValue("@bid", option.Bid);
                optionsCommand.Parameters.AddWithValue("@ask", option.Ask);
                optionsCommand.Parameters.AddWithValue("@mid", option.Mid);
                optionsCommand.Parameters.AddWithValue("@last", option.Last);
                optionsCommand.Parameters.AddWithValue("@iv", option.Iv);
                optionsCommand.Parameters.AddWithValue("@delta", option.Delta);
                optionsCommand.Parameters.AddWithValue("@gamma", option.Gamma);
                optionsCommand.Parameters.AddWithValue("@theta", option.Theta);
                optionsCommand.Parameters.AddWithValue("@vega", option.Vega);
                optionsCommand.Parameters.AddWithValue("@open_interest", option.OpenInterest);
                optionsCommand.Parameters.AddWithValue("@volume", option.Volume);
                optionsCommand.Parameters.AddWithValue("@moneyness", option.Moneyness);

                await optionsCommand.ExecuteNonQueryAsync();
                optionsInserted++;
            }
        }

        Console.WriteLine($"Inserted {barsInserted} bars and {optionsInserted} options for {sessionDate:yyyy-MM-dd}");
        return (barsInserted, optionsInserted);
    }

    private List<UnderlyingBar> GenerateUnderlyingBarsForDay(DateTime sessionDate)
    {
        var bars = new List<UnderlyingBar>();
        
        // Market hours: 9:30 AM to 4:00 PM ET (6.5 hours)
        var marketOpen = sessionDate.Date.AddHours(14.5); // 9:30 AM ET in UTC
        var marketClose = sessionDate.Date.AddHours(21);   // 4:00 PM ET in UTC

        // Generate 5-minute bars
        var currentTime = marketOpen;
        var currentPrice = SpxBasePrice + (decimal)(_random.NextDouble() - 0.5) * 200; // ±$100 variation
        
        while (currentTime < marketClose)
        {
            // Intraday price movement
            var minuteReturn = Normal.Sample(_random, 0, BaseVolatility / Math.Sqrt(252 * 78));
            var newPrice = currentPrice * (decimal)(1 + minuteReturn);
            
            // Generate OHLC
            var high = newPrice * (decimal)(1 + Math.Abs(Normal.Sample(_random, 0, 0.001)));
            var low = newPrice * (decimal)(1 - Math.Abs(Normal.Sample(_random, 0, 0.001)));
            var close = newPrice + (decimal)Normal.Sample(_random, 0, (double)newPrice * 0.0005);
            var volume = Math.Max(1000, (long)LogNormal.Sample(_random, 12, 0.5) * 1000);

            bars.Add(new UnderlyingBar
            {
                TsUtc = currentTime,
                Open = currentPrice,
                High = high,
                Low = low,
                Close = close,
                Volume = volume
            });

            currentPrice = close;
            currentTime = currentTime.AddMinutes(5);
        }

        return bars;
    }

    private List<OptionData> GenerateOptionsChain(decimal underlyingPrice, DateTime sessionDate)
    {
        var options = new List<OptionData>();
        var snapshotTime = sessionDate.Date.AddHours(20); // 3:00 PM ET

        // Generate expiration dates (0-45 DTE)
        var expiries = GenerateExpiryDates(sessionDate);
        
        // Generate strikes around current price (±15% moneyness)
        var atmStrike = Math.Round(underlyingPrice / 5) * 5; // Round to nearest $5
        var strikeRange = (int)(underlyingPrice * 0.15m / 5) * 5; // 15% range
        var strikes = new List<decimal>();
        
        for (var strike = atmStrike - strikeRange; strike <= atmStrike + strikeRange; strike += 5)
        {
            strikes.Add(strike);
        }

        foreach (var expiry in expiries)
        {
            var dte = expiry.DayNumber - DateOnly.FromDateTime(sessionDate).DayNumber;
            var timeToExpiry = dte / 365.0;
            
            // Base volatility with some randomness
            var baseVol = BaseVolatility + Normal.Sample(_random, 0, 0.02);
            
            foreach (var strike in strikes)
            {
                var moneyness = strike / underlyingPrice;
                
                // Skip strikes too far OTM/ITM
                if (moneyness < 0.85m || moneyness > 1.15m)
                    continue;

                // Volatility smile/skew
                var volSkew = -0.1 * (double)(moneyness - 1) + 0.05 * Math.Pow((double)(moneyness - 1), 2);
                var impliedVol = Math.Max(0.05, baseVol + volSkew + Normal.Sample(_random, 0, 0.01));

                foreach (var optionType in new[] { "C", "P" })
                {
                    var (price, delta, gamma, theta, vega) = CalculateBlackScholesGreeks(
                        (double)underlyingPrice, (double)strike, timeToExpiry, RiskFreeRate, impliedVol, optionType);

                    // Bid/ask spread
                    var spreadPct = 0.01 + 0.02 * Math.Abs((double)(moneyness - 1));
                    var spread = price * spreadPct;
                    var bid = Math.Max(0.05, price - spread / 2);
                    var ask = price + spread / 2;
                    var mid = (bid + ask) / 2;

                    // Volume and open interest based on liquidity
                    var liquidityFactor = Math.Exp(-5 * Math.Abs((double)(moneyness - 1)));
                    var volume = (long)(LogNormal.Sample(_random, 4, 1) * liquidityFactor * 100);
                    var openInterest = (long)(LogNormal.Sample(_random, 6, 1) * liquidityFactor * 100);

                    options.Add(new OptionData
                    {
                        Symbol = "SPX",
                        SessionDate = DateOnly.FromDateTime(sessionDate),
                        TsUtc = snapshotTime,
                        ExpiryDate = expiry,
                        Strike = strike,
                        Right = optionType,
                        Bid = (decimal)Math.Round(bid, 2),
                        Ask = (decimal)Math.Round(ask, 2),
                        Mid = (decimal)Math.Round(mid, 2),
                        Last = (decimal)Math.Round(price + Normal.Sample(_random, 0, spread / 4), 2),
                        Iv = (decimal)Math.Round(impliedVol, 4),
                        Delta = Math.Round(delta, 4),
                        Gamma = Math.Round(gamma, 6),
                        Theta = Math.Round(theta, 4),
                        Vega = Math.Round(vega, 4),
                        OpenInterest = openInterest,
                        Volume = volume,
                        Moneyness = Math.Round(moneyness, 4)
                    });
                }
            }
        }

        return options;
    }

    private List<DateOnly> GenerateExpiryDates(DateTime sessionDate)
    {
        var expiries = new List<DateOnly>();
        var current = DateOnly.FromDateTime(sessionDate);
        
        for (int days = 1; days <= 45; days++)
        {
            var candidate = current.AddDays(days);
            var candidateDateTime = candidate.ToDateTime(TimeOnly.MinValue);
            
            // Add Fridays (weeklies) and third Fridays (monthlies)
            if (candidateDateTime.DayOfWeek == DayOfWeek.Friday && IsTradingDay(candidateDateTime))
            {
                expiries.Add(candidate);
            }
        }
        
        return expiries;
    }

    private (double price, double delta, double gamma, double theta, double vega) CalculateBlackScholesGreeks(
        double S, double K, double T, double r, double vol, string optionType)
    {
        if (T <= 0)
        {
            return optionType == "C" 
                ? (Math.Max(S - K, 0), 0, 0, 0, 0)
                : (Math.Max(K - S, 0), 0, 0, 0, 0);
        }

        var d1 = (Math.Log(S / K) + (r + 0.5 * vol * vol) * T) / (vol * Math.Sqrt(T));
        var d2 = d1 - vol * Math.Sqrt(T);

        double price, delta;
        if (optionType == "C")
        {
            price = S * Normal.CDF(0, 1, d1) - K * Math.Exp(-r * T) * Normal.CDF(0, 1, d2);
            delta = Normal.CDF(0, 1, d1);
        }
        else
        {
            price = K * Math.Exp(-r * T) * Normal.CDF(0, 1, -d2) - S * Normal.CDF(0, 1, -d1);
            delta = -Normal.CDF(0, 1, -d1);
        }

        var gamma = Normal.PDF(0, 1, d1) / (S * vol * Math.Sqrt(T));
        var theta = -(S * Normal.PDF(0, 1, d1) * vol / (2 * Math.Sqrt(T)) + 
                      r * K * Math.Exp(-r * T) * Normal.CDF(0, 1, optionType == "C" ? d2 : -d2));
        var vega = S * Normal.PDF(0, 1, d1) * Math.Sqrt(T);

        return (price, delta, gamma, theta / 365.0, vega / 100.0);
    }

    private bool IsTradingDay(DateTime date)
    {
        return date.DayOfWeek != DayOfWeek.Saturday && 
               date.DayOfWeek != DayOfWeek.Sunday && 
               !_usHolidays.Contains(date.Date);
    }

    private HashSet<DateTime> InitializeUSHolidays()
    {
        var holidays = new HashSet<DateTime>();
        
        // Add major US market holidays for the range 2018-2025
        for (int year = 2018; year <= 2025; year++)
        {
            // New Year's Day
            holidays.Add(new DateTime(year, 1, 1));
            
            // Independence Day
            holidays.Add(new DateTime(year, 7, 4));
            
            // Christmas
            holidays.Add(new DateTime(year, 12, 25));
            
            // Add other major holidays - simplified version
            // Martin Luther King Jr Day (3rd Monday in January)
            var mlkDay = GetNthWeekdayOfMonth(year, 1, DayOfWeek.Monday, 3);
            holidays.Add(mlkDay);
            
            // Presidents Day (3rd Monday in February)
            var presidentsDay = GetNthWeekdayOfMonth(year, 2, DayOfWeek.Monday, 3);
            holidays.Add(presidentsDay);
            
            // Memorial Day (last Monday in May)
            var memorialDay = GetLastWeekdayOfMonth(year, 5, DayOfWeek.Monday);
            holidays.Add(memorialDay);
            
            // Labor Day (1st Monday in September)
            var laborDay = GetNthWeekdayOfMonth(year, 9, DayOfWeek.Monday, 1);
            holidays.Add(laborDay);
            
            // Thanksgiving (4th Thursday in November)
            var thanksgiving = GetNthWeekdayOfMonth(year, 11, DayOfWeek.Thursday, 4);
            holidays.Add(thanksgiving);
        }
        
        return holidays;
    }

    private DateTime GetNthWeekdayOfMonth(int year, int month, DayOfWeek dayOfWeek, int occurrence)
    {
        var firstDay = new DateTime(year, month, 1);
        var firstOccurrence = firstDay.AddDays(((int)dayOfWeek - (int)firstDay.DayOfWeek + 7) % 7);
        return firstOccurrence.AddDays(7 * (occurrence - 1));
    }

    private DateTime GetLastWeekdayOfMonth(int year, int month, DayOfWeek dayOfWeek)
    {
        var lastDay = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        var diff = ((int)lastDay.DayOfWeek - (int)dayOfWeek + 7) % 7;
        return lastDay.AddDays(-diff);
    }
}

public record UnderlyingBar
{
    public DateTime TsUtc { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public long Volume { get; init; }
}

public record OptionData
{
    public string Symbol { get; init; } = "";
    public DateOnly SessionDate { get; init; }
    public DateTime TsUtc { get; init; }
    public DateOnly ExpiryDate { get; init; }
    public decimal Strike { get; init; }
    public string Right { get; init; } = "";
    public decimal Bid { get; init; }
    public decimal Ask { get; init; }
    public decimal Mid { get; init; }
    public decimal Last { get; init; }
    public decimal Iv { get; init; }
    public double Delta { get; init; }
    public double Gamma { get; init; }
    public double Theta { get; init; }
    public double Vega { get; init; }
    public long OpenInterest { get; init; }
    public long Volume { get; init; }
    public decimal Moneyness { get; init; }
}