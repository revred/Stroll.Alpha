using System.Text.Json.Serialization;

namespace Stroll.Alpha.Dataset.Config;

public sealed class DatasetConfig
{
    public string DataRoot { get; init; } = Path.Combine("C:", "Code", "Stroll.Theta.DB");
    public string BarsInterval { get; init; } = "1m";
    public decimal DefaultMoneynessWindow { get; init; } = 0.15m; // ±15%
    public int DteMax { get; init; } = 45;
    public string[] Symbols { get; init; } = new[] { "SPX","XSP","SPY","QQQ","VIX","GOLD","OIL" };

    public static DatasetConfig Default => new();
}
