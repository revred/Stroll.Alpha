using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Stroll.Alpha.Dataset.Config;
using Stroll.Alpha.Dataset.Models;
using Stroll.Alpha.Dataset.Sources;

namespace Stroll.Alpha.Dataset.Storage;

public sealed class Partitioning
{
    private readonly DatasetConfig _cfg;
    public Partitioning(DatasetConfig cfg) => _cfg = cfg;

    public string SymbolRoot(string symbol) => Path.Combine(_cfg.DataRoot, "alpha", symbol.ToUpperInvariant());
    public string MonthRoot(string symbol, DateOnly d) => Path.Combine(SymbolRoot(symbol), d.Year.ToString("0000"), d.Month.ToString("00"));

    public string BarsPath(string symbol, DateOnly d) => Path.Combine(MonthRoot(symbol, d), "bars_1m.sqlite");
    public string ChainPath(string symbol, DateOnly d) => Path.Combine(MonthRoot(symbol, d), $"chain_{d:yyyy-MM-dd}.parquet");
    public string SnapshotsPath(string symbol, DateOnly d) => Path.Combine(MonthRoot(symbol, d), $"snapshots_{d:yyyy-MM-dd}.parquet");
    public string MetaPath(string symbol, DateOnly d) => Path.Combine(MonthRoot(symbol, d), "meta.json");

    public static int Dte(DateOnly expiry, DateTime asOfUtc) => (expiry.DayNumber - DateOnly.FromDateTime(asOfUtc).DayNumber);
    public static decimal Moneyness(decimal strike, decimal spot) => strike/spot - 1m;
}

public sealed record CompletenessReport(
    string Symbol,
    DateOnly Session,
    int StrikesLeft,
    int StrikesRight,
    double Score,
    string[] Warnings
);

public sealed class DatasetStore
{
    private readonly IOptionsSource _options;
    private readonly IBarSource _bars;
    private readonly DatasetConfig _cfg;
    private readonly Partitioning _p;

    public DatasetStore(IOptionsSource options, IBarSource bars, DatasetConfig cfg)
    {
        _options = options; _bars = bars; _cfg = cfg; _p = new Partitioning(cfg);
    }

    public async Task<CompletenessReport> CheckChainCompletenessAsync(string symbol, DateTime tsUtc, decimal mny = 0.15m, int dteMin = 0, int dteMax = 45, CancellationToken ct = default)
    {
        var opts = new List<OptionRecord>();
        await foreach (var opt in _options.GetSnapshotAsync(symbol, tsUtc, mny, dteMin, dteMax, ct))
        {
            opts.Add(opt);
        }
        var left = opts.Count(o => o.Right == Right.Put);
        var right = opts.Count(o => o.Right == Right.Call);
        var total = Math.Max(1, left + right);
        var score = total >= 6 ? 1.0 : total / 6.0; // simplistic: at least 3 strikes each side
        var warns = new List<string>();
        if (left < 3) warns.Add("insufficient_put_strikes");
        if (right < 3) warns.Add("insufficient_call_strikes");
        return new CompletenessReport(symbol, DateOnly.FromDateTime(tsUtc), left, right, score, warns.ToArray());
    }
}
