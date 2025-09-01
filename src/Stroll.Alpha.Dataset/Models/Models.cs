namespace Stroll.Alpha.Dataset.Models;

public enum Right { Call, Put }

public sealed record OptionRecord(
    string Symbol,
    DateOnly Session,
    DateTime TsUtc,
    DateOnly Expiry,
    decimal Strike,
    Right Right,
    decimal Bid,
    decimal Ask,
    decimal? Mid,
    decimal? Last,
    decimal? Iv,
    double? Delta,
    double? Gamma,
    double? Theta,
    double? Vega,
    long? Oi,
    long? Volume
);

public sealed record UnderlyingBar(
    string Symbol,
    DateTime TsUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume
);

public sealed record VolRecord(
    string Symbol,
    DateTime TsUtc,
    decimal Vix,
    decimal? Vix9D
);
