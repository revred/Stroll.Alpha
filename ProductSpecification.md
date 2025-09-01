# ProductSpecification — Stroll.Alpha

## Objective
Authoritative **0–45 DTE** options + index data service for **2018‑01‑01 → 2025‑08‑29**, exposing:
- **Fast CLI + MCP** for strategy engines and notebooks
- **Frugal storage** with deterministic partitions
- **Continuous validation** via probes
- **Risk-first execution guardrails**

## Instruments (initial)
- Indices/ETFs: **SPX, XSP, SPY, QQQ, VIX, Gold Index, Oil Index**
- Identifier mapping via `DatasetConfig`.

## Functional Requirements
1. **Chain Snapshot** at arbitrary UTC timestamp (minute resolution recommended) within historical bounds.
2. **Greeks/IV** retrieval if available; else approximations (B&S with historical IV proxy).
3. **0–45 DTE filter** with **moneyness window** (default ±15%).
4. **Completeness score** for each snapshot (strikes-per-side, spread sanity, bid/ask presence).
5. **MCP Endpoints** (JSON over stdio or HTTP):  
   - `get_chain(symbol, at, dte_min, dte_max, moneyness_window)`  
   - `get_snapshot(symbol, at)`  
   - `get_underlying(symbol, from, to, interval)`  
   - `get_health()`  
   - `validate_channel(symbol, date)`  
   - `loss_cap_state()`
6. **CLI Commands** mirroring MCP methods for human use.
7. **Probes**: Chain completeness, Greeks availability, bar continuity, VIX term sanity.
8. **Risk Policy**: Reverse Fibonacci hard stop for order intents (`/intent/estimate_loss`).

## Non-Functional
- **Latency targets**: chain snapshot ≤ 150ms (hot cache), ≤ 1.5s (cold partition).
- **Storage target**: ≤ 120 GB for 7-year scope (options + minimal bars) given split schema.
- **Determinism**: same query → same result (idempotent historical reads).
- **Auditability**: partition keys & checksums, reproducible rebuilds.

## Extensibility
- Add new symbols by appending partitions; no schema change.
- Plug different providers via `IOptionsSource`/`IBarSource`.
- Schema supports vendor-agnostic identifiers.

## Failure Modes & Safeguards
- **Missing shard** → Probe suggests exact shard (`symbol/year/month` & session) to ingest.
- **Loss cap breach** → Engine returns refusal with current cap and next reset rule.
- **Partial chain** → Mark `CompletenessScore < threshold` and provide remediation hints.

## Deliverables
- 4 C# projects, solution file, and docs (this file + README + CLAUDE + SplitSchema).
- TDD suite covering contracts, partition math, DTE/moneyness filters, loss caps, probes.
