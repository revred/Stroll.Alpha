# Stroll.Alpha Architecture Overview

## Multi-Repository Data Platform

Stroll.Alpha employs a **distributed repository architecture** to efficiently manage large-scale historical options and market data while respecting GitHub's storage limitations and optimizing for different data access patterns.

### Repository Structure

```
┌─────────────────────────────────────────────────────────────┐
│                  STROLL.ALPHA ECOSYSTEM                    │
└─────────────────────────────────────────────────────────────┘
             │
             ├── github.com/revred/Stroll.Alpha
             │   ├── Application Code & Business Logic
             │   ├── Market Data Provider APIs
             │   ├── Black-Scholes Pricing Engine
             │   ├── Data Generation & Validation Tools
             │   └── MCP Server for Market Queries
             │
             ├── github.com/revred/Stroll.Theta.DB
             │   ├── SQLite Database (Options + Underlying)
             │   ├── Historical Data (2018-01-01 → 2025-08-29)
             │   ├── 2M+ Options Contracts (0-45 DTE)
             │   ├── Automated 2-Minute Commit System
             │   └── Zero-Disruption Export Strategy
             │
             └── github.com/revred/Stroll.Theta.Sixty
                 ├── 1-Minute OHLCV Bars (Parquet Format)
                 ├── Hourly Partitioned Structure
                 ├── Ultra-Compact Storage (4.5KB/hour)
                 └── Columnar Data for Analytics
```

## Data Distribution Strategy

### 1. Core Application Code
**Repository**: `github.com/revred/Stroll.Alpha`
**Purpose**: Business logic and data generation tools
**Size**: ~50MB (lightweight codebase)

```
src/
├── Stroll.Alpha.Market/          # MCP market data server
├── Stroll.Alpha.Dataset/         # Data generation & validation
├── Stroll.Alpha.Providers/       # SQLite data providers
├── Stroll.Alpha.Probes/         # Data completeness validation
└── Stroll.Alpha.Tests/          # Comprehensive test suite
```

### 2. Historical Options Database
**Repository**: `github.com/revred/Stroll.Theta.DB`
**Purpose**: Complete options chain historical data
**Size**: ~2GB (approaching GitHub limits)

```
Database Schema:
├── options_chain          # 2M+ contracts (SPX, XSP, VIX, QQQ, GLD, USO)
├── underlying_bars        # 5-minute resolution underlying prices
├── greeks_daily          # Daily Greek snapshots
├── probe_results         # Data quality validation
├── market_calendar       # Trading holidays & sessions
└── volatility_surface    # IV surfaces by symbol/expiry
```

**Key Features**:
- **DTE Filtering**: Only 0-45 DTE contracts stored
- **Moneyness Filter**: ±15% strike range for liquid options
- **Computed Columns**: SQLite julianday() for fast DTE queries
- **WAL Mode**: Concurrent read/write access
- **Auto-Export**: 2-minute commit cycles with zero disruption

### 3. High-Frequency Bar Data
**Repository**: `github.com/revred/Stroll.Theta.Sixty`
**Purpose**: 1-minute OHLCV bars in Parquet format
**Size**: Scalable with excellent compression

```
Partition Structure:
bars/
├── 2025/08/29/
│   ├── SPX_09.parquet    # 9:30-10:30 AM ET (60 bars)
│   ├── SPX_10.parquet    # 10:30-11:30 AM ET
│   ├── ...
│   └── SPX_15.parquet    # 3:00-4:00 PM ET
└── 2025/08/28/
    └── [Same structure]
```

**Parquet Schema**:
```
ts_utc     : DATETIME     # UTC timestamp
ts_et      : DATETIME     # Eastern Time timestamp  
symbol     : STRING       # SPX, XSP, VIX, QQQ, GLD, USO
open       : DOUBLE       # Opening price
high       : DOUBLE       # High price
low        : DOUBLE       # Low price
close      : DOUBLE       # Closing price
volume     : LONG         # Volume
vwap       : DOUBLE       # Volume-weighted average price
```

## Data Generation Pipeline

### Parallel Processing Architecture
```
┌─────────────────────────────────────────────────────────────┐
│                 LIVE GENERATION STATUS                      │
├─────────────────────────────────────────────────────────────┤
│ 🔄 Theta Console    │ Real-time monitoring dashboard       │
│ 🚀 Multi-Symbol     │ XSP,VIX,GLD,USO,QQQ generation      │
│ ⚡ SPX Generator    │ High-priority SPX data               │
│ 💾 Auto-Commit      │ 2-minute GitHub commits              │
│ 📊 Minute Bars      │ Parquet file generation              │
└─────────────────────────────────────────────────────────────┘
```

### Current Progress (Live)
- **Options Contracts**: 1.85M+ generated
- **Trading Days**: 631+ processed (34% complete)
- **Date Range**: 2023-03-17 → 2025-08-29
- **Database Size**: 623MB+ and growing
- **Data Quality**: 95%+ completeness

## Storage Optimization Strategies

### 1. SQLite Database Efficiency
- **Computed DTE Column**: No redundant DTE calculations
- **Indexed Queries**: Sub-second chain retrieval
- **WAL Mode**: Concurrent access without locks
- **VACUUM**: Regular database optimization

### 2. Parquet Compression
- **Columnar Storage**: 90%+ compression ratio
- **Schema Evolution**: Forward-compatible data format
- **Partition Pruning**: Query only relevant files
- **Delta Encoding**: Efficient time-series compression

### 3. Git LFS Considerations
- **Size Monitoring**: Proactive GitHub limit management
- **Smart Commits**: Export only stable data
- **Incremental Updates**: Minimal diff strategies
- **Branch Strategies**: Separate development from data

## Query Performance Architecture

### Fast Market Data Access
```csharp
// Get options chain for specific date/time
var chain = await marketService.GetOptionsChainAsync(
    symbol: "SPX",
    timestamp: DateTime.Parse("2023-10-31T18:45:00Z"),
    maxDTE: 45,
    moneyness: 0.15
);

// Get 1-minute bars for analysis
var bars = await barService.GetMinuteBarsAsync(
    symbol: "SPX", 
    start: DateTime.Parse("2023-10-31T14:30:00Z"),
    end: DateTime.Parse("2023-10-31T16:00:00Z")
);
```

### MCP Server Integration
```bash
# JSON-over-stdio market queries
dotnet run --project src/Stroll.Alpha.Market -- chain \
  --symbol SPX --at 2023-10-31T18:45:00Z --json
```

## Data Quality & Validation

### Probe-Driven Quality Assurance
```bash
# Validate chain completeness
dotnet run --project src/Stroll.Alpha.Probes -- \
  --symbol SPX --date 2023-10-31 --depth 8
```

### Multi-Tier Validation
1. **Real-time Monitoring**: Live completeness tracking
2. **Batch Validation**: Daily quality reports  
3. **Cross-Symbol Consistency**: Inter-market validation
4. **Historical Integrity**: Time-series consistency checks

## Deployment & Scaling

### Local Development
```bash
# Full stack startup
./scripts/theta-console.sh        # Monitoring
./scripts/generate-multi-symbol-data.sh  # Data gen
./scripts/auto-commit-2min.sh     # Auto-commits
```

### Production Considerations
- **Database Sharding**: Symbol-based partitioning for >10GB
- **CDN Distribution**: Global Parquet file access
- **Caching Layers**: Redis for hot data
- **Monitoring**: Grafana dashboards for data pipeline health

## Future Enhancements

### Planned Improvements
1. **Real-time Streaming**: Live market data ingestion
2. **ML Feature Store**: Pre-computed features for backtesting
3. **Multi-Asset Support**: Equity options beyond indices
4. **Delta Hedging**: Portfolio risk analytics integration
5. **Cloud Native**: Kubernetes deployment strategies

---

This architecture enables **petabyte-scale** historical options analysis while maintaining **sub-second query performance** and **GitHub-friendly** repository management.