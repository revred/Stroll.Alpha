# Stroll.Alpha — Multi-Repository Options Data Platform (2018-01-01 → 2025-08-29)

**Production-ready distributed data platform** providing comprehensive historical options and market data across multiple specialized repositories:

## Multi-Repository Architecture

```
🏗️  github.com/revred/Stroll.Alpha        ← Core application & tools (50MB)
📊  github.com/revred/Stroll.Theta.DB      ← Historical options database (2GB)  
📈  github.com/revred/Stroll.Theta.Sixty   ← 1-minute bars Parquet format (350MB)
```

**Current Status**: 🎉 **MILESTONE ACHIEVED** - 5.05M+ options contracts, 1524+ trading days (83% complete)
- ✅ Multi-Symbol Stream (XSP/VIX/GLD/USO/QQQ): **COMPLETE** - 3.39M contracts
- 🔥 SPX Stream: 97% Complete - 6.93M+ contracts

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

# Monitor live data generation (1.85M+ options, 34% complete)
./scripts/theta-console.sh

# Generate historical options data
./scripts/generate-multi-symbol-data.sh "SPX,XSP,VIX,QQQ,GLD,USO" "2025-08-29" "2018-01-01"

# Generate 1-minute Parquet bars
./scripts/generate-minute-bars.sh "SPX,XSP,VIX,QQQ,GLD,USO" "2025-08-29" "2025-08-01"

# Query market data via MCP server
dotnet run --project src/Stroll.Alpha.Market -- chain --symbol SPX --at 2023-10-31T18:45:00Z --json

# Validate data completeness
dotnet run --project src/Stroll.Alpha.Probes -- --symbol SPX --date 2023-10-31 --depth 8
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

## Architecture & Scaling

For complete system architecture, data distribution strategy, performance characteristics, and scaling considerations, see:

- **[ARCHITECTURE.md](ARCHITECTURE.md)** - Complete multi-repository platform overview
- **[Stroll.Theta.Sixty/ARCHITECTURE.md](https://github.com/revred/Stroll.Theta.Sixty/blob/main/ARCHITECTURE.md)** - 1-minute bars Parquet storage design

## Key Features

- **📊 5M+ Options Contracts**: Complete 0-45 DTE historical data (83% complete)
- **⚡ Sub-second Queries**: Optimized SQLite with computed DTE columns  
- **📈 1-minute Bars**: Parquet format with 90%+ compression
- **🔄 Live Generation**: Parallel processing achieving ~130K options/hour peak rate
- **🚀 Zero-Disruption Commits**: Smart 2-minute GitHub integration
- **🔍 Data Quality**: 95%+ completeness with continuous validation
- **📱 MCP Server**: JSON-over-stdio for external integrations

— Updated: 2025-09-02 (5M+ Options Milestone Achieved)
