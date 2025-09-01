using System.Globalization;
using Microsoft.Data.Sqlite;
using MathNet.Numerics.Distributions;
using MathNet.Numerics;

namespace Stroll.Alpha.Dataset.Generation;

public sealed class MultiSymbolDataGenerator
{
    private readonly string _dbPath;
    private readonly Random _random;
    private readonly HashSet<DateTime> _usHolidays;
    private readonly Dictionary<string, SymbolConfig> _symbolConfigs;

    public MultiSymbolDataGenerator(string dbPath)
    {
        _dbPath = dbPath;
        _random = new Random(42); // Deterministic seed
        _usHolidays = InitializeUSHolidays();
        _symbolConfigs = InitializeSymbolConfigs();
    }

    public async Task GenerateAsync(string[] symbols, DateTime startDate, DateTime endDate, int batchSize = 5)
    {
        Console.WriteLine($"Starting multi-symbol data generation for {string.Join(", ", symbols)} from {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        
        int totalBars = 0;
        int totalOptions = 0;
        int daysProcessed = 0;

        var currentDate = startDate;
        while (currentDate >= endDate)
        {
            if (IsTradingDay(currentDate))
            {
                foreach (var symbol in symbols)
                {
                    var (bars, options) = await GenerateSingleDaySymbolAsync(symbol, currentDate);
                    totalBars += bars;
                    totalOptions += options;
                }
                daysProcessed++;

                if (daysProcessed % batchSize == 0)
                {
                    Console.WriteLine($"Progress: {daysProcessed} trading days processed, {totalBars} bars, {totalOptions} options inserted for {string.Join(",", symbols)}");
                }
            }
            
            currentDate = currentDate.AddDays(-1);
        }

        Console.WriteLine($"Generation complete: {daysProcessed} days, {totalBars} bars, {totalOptions} options for {string.Join(",", symbols)}");
    }

    private async Task<(int barsInserted, int optionsInserted)> GenerateSingleDaySymbolAsync(string symbol, DateTime sessionDate)
    {
        if (!_symbolConfigs.TryGetValue(symbol, out var config))
        {
            Console.WriteLine($"Unknown symbol: {symbol}");
            return (0, 0);
        }

        var underlyingBars = GenerateUnderlyingBarsForSymbol(symbol, config, sessionDate);
        var midDayPrice = underlyingBars[underlyingBars.Count / 2].Close;

        List<OptionData> optionsChain = new();
        
        // Only generate options for symbols that have options markets
        if (config.HasOptions)
        {
            optionsChain = GenerateOptionsChainForSymbol(symbol, config, midDayPrice, sessionDate);
        }

        return await ExecuteWithRetry(async () =>
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=true;Cache=Shared;");
            await connection.OpenAsync();
            
            // Enable WAL mode for better concurrency
            using var walCommand = connection.CreateCommand();
            walCommand.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=30000;";
            await walCommand.ExecuteNonQueryAsync();

        int barsInserted = 0;
        int optionsInserted = 0;

        // Insert underlying bars
        using var barsCommand = connection.CreateCommand();
        barsCommand.CommandText = @"
            INSERT OR REPLACE INTO underlying_bars 
            (symbol, ts_utc, open, high, low, close, volume)
            VALUES (@symbol, @ts_utc, @open, @high, @low, @close, @volume)";

        foreach (var bar in underlyingBars)
        {
            barsCommand.Parameters.Clear();
            barsCommand.Parameters.AddWithValue("@symbol", symbol);
            barsCommand.Parameters.AddWithValue("@ts_utc", bar.TsUtc);
            barsCommand.Parameters.AddWithValue("@open", bar.Open);
            barsCommand.Parameters.AddWithValue("@high", bar.High);
            barsCommand.Parameters.AddWithValue("@low", bar.Low);
            barsCommand.Parameters.AddWithValue("@close", bar.Close);
            barsCommand.Parameters.AddWithValue("@volume", bar.Volume);
            
            await barsCommand.ExecuteNonQueryAsync();
            barsInserted++;
        }

        // Insert options chain (if applicable)
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

            return (barsInserted, optionsInserted);
        });
    }

    private async Task<T> ExecuteWithRetry<T>(Func<Task<T>> operation, int maxRetries = 5)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 5 && attempt < maxRetries) // SQLITE_BUSY
            {
                Console.WriteLine($"Database busy, retrying in {attempt * 1000}ms... (attempt {attempt}/{maxRetries})");
                await Task.Delay(attempt * 1000); // Exponential backoff
            }
        }
        
        // Final attempt without retry
        return await operation();
    }

    private Dictionary<string, SymbolConfig> InitializeSymbolConfigs()
    {
        return new Dictionary<string, SymbolConfig>
        {
            ["SPX"] = new SymbolConfig
            {
                BasePrice = 4800m,
                Volatility = 0.20,
                VolumeScale = 12.0,
                HasOptions = true,
                AssetType = AssetType.Index
            },
            ["XSP"] = new SymbolConfig  // Mini SPX
            {
                BasePrice = 480m, // 1/10th of SPX
                Volatility = 0.20,
                VolumeScale = 10.0,
                HasOptions = true,
                AssetType = AssetType.Index
            },
            ["VIX"] = new SymbolConfig
            {
                BasePrice = 20m,
                Volatility = 0.80, // VIX is highly volatile
                VolumeScale = 8.0,
                HasOptions = true,
                AssetType = AssetType.Volatility,
                MeanReversion = 0.05 // VIX tends to revert to ~20
            },
            ["GLD"] = new SymbolConfig  // Gold ETF
            {
                BasePrice = 180m,
                Volatility = 0.25,
                VolumeScale = 11.0,
                HasOptions = true,
                AssetType = AssetType.Commodity
            },
            ["USO"] = new SymbolConfig  // Oil ETF
            {
                BasePrice = 70m,
                Volatility = 0.35,
                VolumeScale = 10.5,
                HasOptions = true,
                AssetType = AssetType.Commodity
            },
            ["QQQ"] = new SymbolConfig  // Nasdaq ETF
            {
                BasePrice = 380m,
                Volatility = 0.25,
                VolumeScale = 12.5,
                HasOptions = true,
                AssetType = AssetType.ETF
            }
        };
    }

    private List<UnderlyingBar> GenerateUnderlyingBarsForSymbol(string symbol, SymbolConfig config, DateTime sessionDate)
    {
        var bars = new List<UnderlyingBar>();
        
        // Market hours: 9:30 AM to 4:00 PM ET (6.5 hours)
        var marketOpen = sessionDate.Date.AddHours(14.5); // 9:30 AM ET in UTC
        var marketClose = sessionDate.Date.AddHours(21);   // 4:00 PM ET in UTC

        // Generate 5-minute bars
        var currentTime = marketOpen;
        var currentPrice = config.BasePrice + (decimal)(_random.NextDouble() - 0.5) * config.BasePrice * 0.1m; // ±10% variation
        
        while (currentTime < marketClose)
        {
            // Intraday price movement with asset-specific characteristics
            double minuteReturn;
            
            if (config.AssetType == AssetType.Volatility) // VIX special handling
            {
                // VIX mean-reverting behavior
                var distanceFromMean = (double)(currentPrice - config.BasePrice) / (double)config.BasePrice;
                minuteReturn = Normal.Sample(_random, -config.MeanReversion * distanceFromMean, config.Volatility / Math.Sqrt(252 * 78));
            }
            else
            {
                minuteReturn = Normal.Sample(_random, 0, config.Volatility / Math.Sqrt(252 * 78));
            }
            
            var newPrice = currentPrice * (decimal)(1 + minuteReturn);
            
            // Generate OHLC
            var high = newPrice * (decimal)(1 + Math.Abs(Normal.Sample(_random, 0, 0.001)));
            var low = newPrice * (decimal)(1 - Math.Abs(Normal.Sample(_random, 0, 0.001)));
            var close = newPrice + (decimal)Normal.Sample(_random, 0, (double)newPrice * 0.0005);
            var volume = Math.Max(1000, (long)LogNormal.Sample(_random, config.VolumeScale, 0.5) * 1000);

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

    private List<OptionData> GenerateOptionsChainForSymbol(string symbol, SymbolConfig config, decimal underlyingPrice, DateTime sessionDate)
    {
        var options = new List<OptionData>();
        var snapshotTime = sessionDate.Date.AddHours(20); // 3:00 PM ET

        // Generate expiration dates (0-45 DTE)
        var expiries = GenerateExpiryDates(sessionDate);
        
        // Generate strikes around current price (±15% moneyness)
        var (atmStrike, strikeIncrement) = GetStrikeParameters(symbol, underlyingPrice);
        var strikeRange = (int)(underlyingPrice * 0.15m / strikeIncrement) * strikeIncrement; // 15% range
        var strikes = new List<decimal>();
        
        for (var strike = atmStrike - strikeRange; strike <= atmStrike + strikeRange; strike += strikeIncrement)
        {
            if (strike > 0) strikes.Add(strike);
        }

        foreach (var expiry in expiries)
        {
            var dte = expiry.DayNumber - DateOnly.FromDateTime(sessionDate).DayNumber;
            var timeToExpiry = dte / 365.0;
            
            // Base volatility with some randomness
            var baseVol = config.Volatility + Normal.Sample(_random, 0, 0.02);
            
            foreach (var strike in strikes)
            {
                var moneyness = strike / underlyingPrice;
                
                // Skip strikes too far OTM/ITM
                if (moneyness < 0.85m || moneyness > 1.15m)
                    continue;

                // Volatility smile/skew (asset-specific)
                double volSkew;
                if (config.AssetType == AssetType.Volatility)
                {
                    // VIX options have different skew characteristics
                    volSkew = 0.05 * Math.Pow((double)(moneyness - 1), 2);
                }
                else
                {
                    // Standard equity index skew
                    volSkew = -0.1 * (double)(moneyness - 1) + 0.05 * Math.Pow((double)(moneyness - 1), 2);
                }
                
                var adjustedVol = Math.Max(0.05, baseVol + volSkew);

                // Generate both calls and puts
                foreach (var right in new[] { "C", "P" })
                {
                    var (price, delta, gamma, theta, vega) = CalculateBlackScholesGreeks(
                        (double)underlyingPrice, (double)strike, timeToExpiry, 0.05, adjustedVol, right);

                    var bid = Math.Max(0.01m, (decimal)price * (1 - 0.02m)); // 2% bid-ask spread
                    var ask = (decimal)price * (1 + 0.02m);
                    var mid = (bid + ask) / 2;

                    options.Add(new OptionData
                    {
                        Symbol = symbol,
                        SessionDate = DateOnly.FromDateTime(sessionDate),
                        TsUtc = snapshotTime,
                        ExpiryDate = expiry,
                        Strike = strike,
                        Right = right,
                        Bid = bid,
                        Ask = ask,
                        Mid = mid,
                        Last = mid + (decimal)Normal.Sample(_random, 0, (double)mid * 0.01),
                        Iv = (decimal)adjustedVol,
                        Delta = Math.Round(delta, 4),
                        Gamma = Math.Round(gamma, 6),
                        Theta = Math.Round(theta, 4),
                        Vega = Math.Round(vega, 4),
                        OpenInterest = Math.Max(0, (long)LogNormal.Sample(_random, 8, 0.8) * 100),
                        Volume = Math.Max(0, (long)LogNormal.Sample(_random, 6, 1.2) * 10),
                        Moneyness = moneyness
                    });
                }
            }
        }

        return options;
    }

    private (decimal atmStrike, decimal strikeIncrement) GetStrikeParameters(string symbol, decimal underlyingPrice)
    {
        return symbol switch
        {
            "SPX" => (Math.Round(underlyingPrice / 5) * 5, 5m),      // $5 increments
            "XSP" => (Math.Round(underlyingPrice / 5) * 5, 5m),      // $5 increments
            "VIX" => (Math.Round(underlyingPrice / 5) * 5, 5m),      // $5 increments
            "GLD" => (Math.Round(underlyingPrice / 1) * 1, 1m),      // $1 increments
            "USO" => (Math.Round(underlyingPrice / 1) * 1, 1m),      // $1 increments
            _ => (Math.Round(underlyingPrice / 5) * 5, 5m)
        };
    }

    private List<DateOnly> GenerateExpiryDates(DateTime sessionDate)
    {
        var expiries = new List<DateOnly>();
        var sessionDateOnly = DateOnly.FromDateTime(sessionDate);
        
        // Weekly expirations (Fridays)
        var currentFriday = sessionDateOnly.AddDays(5 - (int)sessionDateOnly.DayOfWeek);
        for (int weeks = 0; weeks < 7; weeks++)
        {
            var expiry = currentFriday.AddDays(weeks * 7);
            var dte = expiry.DayNumber - sessionDateOnly.DayNumber;
            if (dte >= 0 && dte <= 45)
            {
                expiries.Add(expiry);
            }
        }

        // Monthly expirations (3rd Friday of month)
        var currentMonth = sessionDateOnly.AddDays(-sessionDateOnly.Day + 1);
        for (int months = 0; months < 3; months++)
        {
            var monthStart = currentMonth.AddMonths(months);
            var thirdFriday = GetThirdFriday(monthStart);
            var dte = thirdFriday.DayNumber - sessionDateOnly.DayNumber;
            if (dte >= 0 && dte <= 45 && !expiries.Contains(thirdFriday))
            {
                expiries.Add(thirdFriday);
            }
        }

        return expiries.OrderBy(e => e).ToList();
    }

    private DateOnly GetThirdFriday(DateOnly monthStart)
    {
        var firstFriday = monthStart.AddDays(5 - (int)monthStart.DayOfWeek);
        if (firstFriday.Month != monthStart.Month) firstFriday = firstFriday.AddDays(7);
        return firstFriday.AddDays(14);
    }

    private (double price, double delta, double gamma, double theta, double vega) CalculateBlackScholesGreeks(
        double S, double K, double T, double r, double vol, string optionType)
    {
        if (T <= 0) return optionType == "C" ? (Math.Max(S - K, 0), 0, 0, 0, 0) : (Math.Max(K - S, 0), 0, 0, 0, 0);
        
        var d1 = (Math.Log(S / K) + (r + 0.5 * vol * vol) * T) / (vol * Math.Sqrt(T));
        var d2 = d1 - vol * Math.Sqrt(T);
        
        var nd1 = Normal.CDF(0, 1, d1);
        var nd2 = Normal.CDF(0, 1, d2);
        var pdf_d1 = Math.Exp(-0.5 * d1 * d1) / Math.Sqrt(2 * Math.PI);
        
        double price, delta, gamma, theta, vega;
        
        if (optionType == "C")
        {
            price = S * nd1 - K * Math.Exp(-r * T) * nd2;
            delta = nd1;
        }
        else
        {
            price = K * Math.Exp(-r * T) * Normal.CDF(0, 1, -d2) - S * Normal.CDF(0, 1, -d1);
            delta = nd1 - 1;
        }
        
        gamma = pdf_d1 / (S * vol * Math.Sqrt(T));
        theta = (-S * pdf_d1 * vol / (2 * Math.Sqrt(T)) - r * K * Math.Exp(-r * T) * nd2) / 365.0;
        vega = S * pdf_d1 * Math.Sqrt(T) / 100.0;
        
        return (price, delta, gamma, theta, vega);
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
        
        // Major US trading holidays from 2018-2025
        var years = Enumerable.Range(2018, 8).ToArray();
        
        foreach (var year in years)
        {
            holidays.Add(new DateTime(year, 1, 1));   // New Year's Day
            holidays.Add(new DateTime(year, 7, 4));   // Independence Day
            holidays.Add(new DateTime(year, 12, 25)); // Christmas
            
            // Add other major holidays (simplified)
            holidays.Add(GetThanksgiving(year));
            holidays.Add(GetThanksgiving(year).AddDays(1)); // Black Friday
        }
        
        return holidays;
    }

    private DateTime GetThanksgiving(int year)
    {
        var november = new DateTime(year, 11, 1);
        var firstThursday = november.AddDays(3 - (int)november.DayOfWeek);
        if (firstThursday.Month != 11) firstThursday = firstThursday.AddDays(7);
        return firstThursday.AddDays(21); // 4th Thursday
    }
}

public class SymbolConfig
{
    public required decimal BasePrice { get; init; }
    public required double Volatility { get; init; }
    public required double VolumeScale { get; init; }
    public required bool HasOptions { get; init; }
    public required AssetType AssetType { get; init; }
    public double MeanReversion { get; init; } = 0.0;
}

public enum AssetType
{
    Index,
    Stock,
    ETF,
    Commodity,
    Volatility
}

// Data models are already defined in the existing HistoricalDataGenerator.cs