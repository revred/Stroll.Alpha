namespace Stroll.Alpha.Market.Contracts;

public sealed record ChainRequest(string Symbol, DateTime AtUtc, int DteMin = 0, int DteMax = 45, decimal Moneyness = 0.15m, bool Json = false);
public sealed record ChainSummary(string Symbol, DateTime AtUtc, int Count, double Completeness, object[] Options);
public sealed record Health(string Status, string Version, DateTime NowUtc);

public sealed record LossCapState(int ConsecutiveLossDays, decimal DailyLossCap, string Rule);
