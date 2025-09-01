# CLAUDE.md — Collaboration Playbook

## Mission
Act as a laser-focused engineer on **data correctness, frugal storage, and fast access** for 0–45 DTE options from 2018‑01‑01 to 2025‑08‑29.

## Constraints
- Capital preservation first — **Reverse Fibonacci** loss caps hard-enforced in `LossCapPolicy`.
- **Probe-before-ingest** — run `Stroll.Alpha.Probes` to validate gaps on demand.
- **Split schema** — store only DTE∈[0,45], strikes in ±15% moneyness, top-of-book minute bars, and daily greeks summaries.

## High-Level Tasks
1. Implement real providers for `IOptionsSource` / `IBarSource` that bind to `Stroll.Theta.DB`.
2. Harden chain completeness rules and add more probes (economic calendar, dividend, holidays).
3. Expand MCP methods in `Stroll.Alpha.Market` as needed, maintain backward compatibility.
4. Keep tests green; **add tests before code** (TDD).

## Fast Commands
```bash
# Probe-only workflow (no heavy downloads)
dotnet run --project src/Stroll.Alpha.Probes -- --symbol SPX --date 2023-10-31 --depth 8

# Market query (JSON)
dotnet run --project src/Stroll.Alpha.Market -- chain --symbol SPX --at 2023-10-31T18:45:00Z --json

# Validate risk caps
dotnet test src/Stroll.Alpha.Tests
```

## Definition of Done
- Queries for every supported instrument and any timestamp in range return **either**
  - a correct chain snapshot within SLA, **or**
  - a clear diagnostic and an ingest hint (which shard to fetch).
- Risk policy blocks trades that could violate loss caps.
- Dataset is auditable with deterministic partition rules.
