# SplitSchema — Frugal Storage Plan (0–45 DTE)

## Goals
- Keep dataset **small** but **authoritative** for backtesting & live reference.
- Avoid storing “everything” — store only what is **material** for 0–45 DTE strategies.

## Partitioning
```
root/
└── alpha/
    └── {symbol}/
        └── {yyyy}/
            └── {mm}/
                ├── bars_{interval}.sqlite        # Underlying OHLCV (1m preferred, else 5m+VWAP)
                ├── chain_{yyyy-mm-dd}.parquet    # One file per session (contract universe)
                ├── snapshots_{yyyy-mm-dd}.parquet# Minute snapshots (best-bid/ask, mid, IV, greeks)
                └── meta.json                     # checksums, build info, session ranges
```

**Symbols**: SPX, XSP, SPY, QQQ, VIX, GOLD, OIL (configurable alias map).

## Options Storage Rules
- **DTE window**: keep only expiries with **0 ≤ DTE ≤ 45** at trade-time.
- **Moneyness window**: keep strikes **K** where `|K / S - 1| ≤ 0.15` at snapshot time.
- **Granularity**:
  - Daily **chain** (contract definitions, expiries, strikes) per session.
  - **Minute snapshots** contain: bid, ask, mid, last, OI, volume, IV, delta, gamma, theta, vega (where available).

## Compression
- **Parquet** with dictionary & snappy for chains/snapshots.
- **SQLite** (WAL) for bars; `bars_1m.sqlite` or fallback to `bars_5m.sqlite` + VWAP-derived interpolation.

## Indexes
- `symbol, ts` index on snapshots
- `symbol, expiry, strike, right` composite
- Covering index for `(ts, dte_bucket, mny_bucket)` where:
  - `dte_bucket = min(45, floor(DTE))`
  - `mny_bucket = floor(100*(K/S - 1))` clipped to [-15,15]

## Integrity
- Per-file `sha256` in `meta.json`
- Row counts & expected-minute math per session
- **CompletenessScore** persisted per day (e.g., 0.0–1.0)

## Why This Works
- Captures the **economically relevant** region for 0–45 DTE.
- Avoids cold storage of deep OTM tails.
- Maintains reliable chain reconstruction + minute snapshots for realistic fills.
