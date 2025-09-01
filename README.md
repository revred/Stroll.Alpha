# Stroll.Alpha — 0–45 DTE Options Data + Market Engine (2018-01-01 → 2025-08-29)

**Goal:** Replace `Stroll.Theta` with a lean, test-first engine that:
- **Authoritatively** serves options + index data for the last **7 years**
- **Focuses** on **0–45 DTE** instruments
- **Keeps storage frugal** (split schema with moneyness + DTE windows)
- **Exposes data fast** via a small **CLI + MCP** server for other agents
- **Enforces risk-first execution** (Reverse Fibonacci loss caps)
- **Validates completeness** continuously via **Probes**

> Primary source integration: `github.com/revred/Stroll.Theta.DB` (ingestion)
>
> This repo: **`github.com/revred/Stroll.Alpha`** (dataset, MCP/CLI, probes, tests)

## Projects

```
Stroll.Alpha/
├─ README.md
├─ CLAUDE.md
├─ ProductSpecification.md
├─ SplitSchema.md
├─ Stroll.Alpha.sln
└─ src/
   ├─ Stroll.Alpha.Dataset/   # Data layout, ingestion, partitioning, caching
   ├─ Stroll.Alpha.Market/    # CLI + MCP server; Loss cap policy; Query services
   ├─ Stroll.Alpha.Probes/    # On-the-fly channel validation (no full download needed)
   └─ Stroll.Alpha.Tests/     # TDD suite to guarantee surface area & data guarantees
```

## Quick Start

```bash
git clone https://github.com/revred/Stroll.Alpha.git
cd Stroll.Alpha

# Build all
dotnet build

# Run Probes (sanity for a specific day/instrument)
dotnet run --project src/Stroll.Alpha.Probes -- --symbol SPX --date 2024-06-12 --depth 6

# Start Market MCP/CLI
dotnet run --project src/Stroll.Alpha.Market -- --help

# Example: dump 0–45 DTE chain snapshot (SPX on 2024‑06‑12 @ 14:00 ET)
dotnet run --project src/Stroll.Alpha.Market -- chain --symbol SPX --at 2024-06-12T14:00:00Z --dte-min 0 --dte-max 45 --moneyness -0.15:0.15
```

## Data Sources

- **Primary historical/options**: `Stroll.Theta.DB` (local DB/files) — add this repository alongside and configure `DATA_ROOT`.
- **Volatility indices**: VIX (and short-term VIX if available).
- **Underlyings**: SPX, XSP, SPY, QQQ, **Gold Index** (XAUUSD proxy or ETF like GLD), **Oil Index** (WTI/Brent proxy or ETF like USO) — you can map to your preferred identifiers in `DatasetConfig`.

> This repo ships with **interfaces** and **local stubs** so it compiles cleanly.
> Wire real providers by implementing `IOptionsSource` and `IBarSource` (see `ThetaDbSource`).

## Guardrails

- **0–45 DTE** coverage required for listed instruments from **2018‑01‑01** to **2025‑08‑29**.
- **Chain completeness tests**: ±15% moneyness window with a **minimum strikes-per-side** threshold.
- **Loss cap policy**: Reverse Fibonacci ($500 → $300 → $200 → $100) — **hard stop**; trades refused when breach risked.
- **Probe-first workflow**: Don’t download everything. Probe first, then selectively ingest missing shards.

## Why this replaces Stroll.Theta

`Stroll.Theta` grew feature-heavy and inconsistent. **Stroll.Alpha** is:
- **Smaller**: a single purpose engine
- **Predictable**: strict contracts + tests
- **Efficient**: split schema that stores **only what’s needed** for reliable backtests

— Updated: 2025-09-01
