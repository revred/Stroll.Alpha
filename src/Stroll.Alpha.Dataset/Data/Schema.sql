-- Underlying bars (1m preferred). If 5m-only, add vwap to enable interpolation.
CREATE TABLE IF NOT EXISTS bars_1m (
  symbol TEXT NOT NULL,
  ts_utc TEXT NOT NULL,
  open REAL NOT NULL,
  high REAL NOT NULL,
  low REAL NOT NULL,
  close REAL NOT NULL,
  volume INTEGER NOT NULL,
  PRIMARY KEY(symbol, ts_utc)
);
CREATE INDEX IF NOT EXISTS ix_bars_symbol_ts ON bars_1m(symbol, ts_utc);

-- Options snapshots are stored in parquet files (see SplitSchema.md). This file exists for reference only.
