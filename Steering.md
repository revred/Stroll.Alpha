# Steering.md — Stroll.Alpha (Project Lead Playbook)

**Scope:** Turn `Stroll.Alpha` into the **authoritative 0–45 DTE** data layer (2018‑01‑01 → 2025‑08‑29) with a **fast CLI + MCP** access plane and **probe‑first** validation. This doc tells Claude.Code *exactly* what to build, in what order, with acceptance criteria and reference snippets.

---

## 0) North‑Star Outcomes (what “done” looks like)
1. **Authoritative dataset** for SPX/XSP/SPY/QQQ/VIX/GOLD/OIL with deterministic partitions and checksums.
2. **Chain snapshot** for any timestamp in range returns within **≤150ms (hot)** / **≤1.5s (cold)** with a **CompletenessScore ≥ 0.9** or a precise remediation hint.
3. **Probe‑first** workflow catches missing shards before ingestion; operators know *exactly* which shard to fetch.
4. **Loss caps enforced** on all order intents: Reverse‑Fibonacci (500 → 300 → 200 → 100) with hard refusals.
5. **TDD green**: contracts, DTE/moneyness math, chain completeness, risk policy, integrity math.

---

## 1) Architecture (responsibilities & strict boundaries)
- **Stroll.Alpha.Dataset**  
  Data contracts, providers (`IOptionsSource`, `IBarSource`), partitioning rules, writers/readers, integrity checks, completeness scoring.
- **Stroll.Alpha.Market**  
  CLI + minimal MCP server. Query services. Risk policy. No storage concerns here; only orchestrates `Dataset`.
- **Stroll.Alpha.Probes**  
  Fast channel checks (minute bars continuity, chain coverage, greeks availability) *without full downloads*.
- **Stroll.Alpha.Tests**  
  TDD that locks API contracts and correctness (math, partitions, integrity counts).

> Golden rule: **No provider‑specific SQL in Market/Probes**. All I/O behind `Dataset` interfaces.

---

## 2) Execution Plan (MVP → v1.0)
### Phase A — MVP (2–3 days)
- **A1. Wire the real adapter**: implement `ThetaDbSource` (both `IOptionsSource`, `IBarSource`) against `Stroll.Theta.DB` (SQLite).  
  - Config: `DATA_ROOT` path to DB shards or monolith.  
  - Minimum queries implemented: `GetSnapshotAsync`, `GetExpiriesAsync`, `GetBarsAsync`.
- **A2. Partition writer/reader**: create `ParquetChainWriter` and `ParquetSnapshotWriter`; `SqliteBarsStore` for 1‑minute bars.  
- **A3. Probe: ChainCompleteness** for one symbol/day/timestamp; outputs score + missing strike sides.
- **A4. CLI**: `chain`, `losscap`, `mcp get_chain` minimal JSON.

**Exit Criteria (MVP):**
- `dotnet build` + `dotnet test` all green.
- `chain` returns JSON with `Completeness ≥ 0.9` for a known‑good day; otherwise remediation hint.
- Probe prints actionable warnings (e.g., `insufficient_call_strikes`).

### Phase B — Data Authority (3–5 days)
- **B1. Full split schema**: write chain/snapshot Parquet files and 1‑min bars SQLite with checksums.  
- **B2. Integrity math**: expected minute rows per session (ex‑holidays), per‑file sha256 in `meta.json`.  
- **B3. Completeness v2**: incorporate bid/ask presence, OI/vol sanity, ATM density, min strikes per DTE bucket.  
- **B4. Caching**: in‑memory hot cache (LRU by `symbol|day|bucket`), file‑handle pool.

**Exit Criteria (Authority):**
- Timeboxed cold load ≤1.5s (P95) for chain summaries.
- Datasets reconstruct reproducibly from raw DB.

### Phase C — Access & Guardrails (2–3 days)
- **C1. MCP JSON‑over‑stdio**: methods `get_health`, `get_chain`, `get_snapshot` (minute).  
- **C2. Risk**: loss‑cap check endpoint `/intent/estimate_loss`.  
- **C3. Observability**: structured logs + counters (probe scores, load times, refusals).

**Exit Criteria (v1.0):**
- Third‑party backtest tool can depend only on `Stroll.Alpha.Market` to retrieve authoritative slices.  
- All order intents blocked if they violate the current cap.

---

## 3) Hard Requirements (acceptance tests to automate)
1. **DTE filter**: return only expiries **0–45** (inclusive).  
2. **Moneyness window**: default ±15% around spot; configurable.  
3. **Minute alignment**: snapshots are minute‑aligned UTC; trading sessions/holidays respected.  
4. **CompletenessScore**:  
   - ≥ 3 strikes per side ATM±5% for each active DTE bucket in view  
   - ≥ 1 quote with both bid/ask per strike included  
   - OI/Vol non‑decreasing anomaly flags  
5. **Performance**: hot cache ≤150ms; cold ≤1.5s.  
6. **Determinism**: identical inputs yield identical outputs (checksum stable).  
7. **Risk**: refusal messages include `allowed_cap` and `rule`.  

---

## 4) Data Contracts (do not change without tests)
```csharp
// Dataset.Models
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
```

---

## 5) Adapter Blueprint — `ThetaDbSource` (real, not stub)
**Assumptions (SQLite tables):**
- `options_chains(symbol, session, expiry, strike, right)`  
- `options_quotes(symbol, ts_utc, expiry, strike, right, bid, ask, last, iv, delta, gamma, theta, vega, oi, vol)`  
- `bars_1m(symbol, ts_utc, open, high, low, close, volume)`

> If actual names differ, map in SQL views or adjust the queries here.

**Snapshot query (nearest minute ≤ tsUtc):**
```sql
WITH m AS (
  SELECT datetime(strftime('%Y-%m-%dT%H:%M:00Z', ?1)) AS ts_floor
),
spot AS (
  SELECT close AS s FROM bars_1m WHERE symbol = ?2 AND ts_utc = (SELECT ts_floor FROM m)
),
universe AS (
  SELECT c.symbol, c.expiry, c.strike, c.right
  FROM options_chains c, spot
  WHERE c.symbol = ?2
    AND julianday(c.expiry) - julianday(date(?1)) BETWEEN ?3 AND ?4
    AND ABS(c.strike / spot.s - 1.0) <= ?5  -- moneyness
),
q AS (
  SELECT q.* FROM options_quotes q, m
  WHERE q.symbol = ?2 AND q.ts_utc = (SELECT ts_floor FROM m)
)
SELECT u.symbol, date(?1) AS session, q.ts_utc, u.expiry, u.strike, u.right,
       q.bid, q.ask, (q.bid+q.ask)/2.0 AS mid, q.last, q.iv,
       q.delta, q.gamma, q.theta, q.vega, q.oi, q.vol
FROM universe u
LEFT JOIN q ON (q.symbol=u.symbol AND q.expiry=u.expiry AND q.strike=u.strike AND q.right=u.right);
```

**C# (simplified):**
```csharp
public async IAsyncEnumerable<OptionRecord> GetSnapshotAsync(
    string symbol, DateTime tsUtc, decimal mny, int dteMin, int dteMax, [EnumeratorCancellation] CancellationToken ct)
{
    await using var cn = new SqliteConnection(_connStr);
    await cn.OpenAsync(ct);

    var cmd = cn.CreateCommand();
    cmd.CommandText = SqlTexts.SnapshotQuery;
    cmd.Parameters.AddWithValue("?1", tsUtc);
    cmd.Parameters.AddWithValue("?2", symbol);
    cmd.Parameters.AddWithValue("?3", dteMin);
    cmd.Parameters.AddWithValue("?4", dteMax);
    cmd.Parameters.AddWithValue("?5", mny);

    await using var rd = await cmd.ExecuteReaderAsync(ct);
    while (await rd.ReadAsync(ct))
    {
        yield return new OptionRecord(
            symbol,
            DateOnly.FromDateTime(tsUtc),
            tsUtc,
            DateOnly.Parse(rd.GetString(3)),
            rd.GetDecimal(4),
            rd.GetString(5) == "C" ? Right.Call : Right.Put,
            rd.GetDecimal(6),
            rd.GetDecimal(7),
            rd.IsDBNull(8) ? null : rd.GetDecimal(8),
            rd.IsDBNull(9) ? null : rd.GetDecimal(9),
            rd.IsDBNull(10) ? null : rd.GetDecimal(10),
            rd.IsDBNull(11) ? null : rd.GetDouble(11),
            rd.IsDBNull(12) ? null : rd.GetDouble(12),
            rd.IsDBNull(13) ? null : rd.GetDouble(13),
            rd.IsDBNull(14) ? null : rd.GetDouble(14),
            rd.IsDBNull(15) ? null : rd.GetInt64(15),
            rd.IsDBNull(16) ? null : rd.GetInt64(16)
        );
    }
}
```

**Bars query:**
```sql
SELECT symbol, ts_utc, open, high, low, close, volume
FROM bars_1m
WHERE symbol = ?1 AND ts_utc BETWEEN ?2 AND ?3
ORDER BY ts_utc;
```

---

## 6) Partitioning & Writers
**Layout:**
```
root/alpha/{{SYMBOL}}/{{YYYY}}/{{MM}}/
  bars_1m.sqlite
  chain_YYYY-MM-DD.parquet
  snapshots_YYYY-MM-DD.parquet
  meta.json
```

**Writers (packages):**
- Add NuGet: `Parquet.Net` for Parquet writers; `Microsoft.Data.Sqlite` for bars.  
- Implement `ParquetChainWriter.WriteAsync(IEnumerable<ChainRow>)`  
- Implement `ParquetSnapshotWriter.WriteAsync(IAsyncEnumerable<OptionRecord>)` (batched).  
- `meta.json` includes `sha256`, record counts, build version, session window.

---

## 7) Completeness Scoring (v2 formula)
- Base score per DTE bucket (0–1):  
  - +0.4 if ≥3 strikes/side within ±5% moneyness  
  - +0.2 if bid+ask present for ≥80% included strikes  
  - +0.2 if ATM spread ≤ X bp percentile for that symbol/day  
  - +0.2 if OI or Vol present for ≥70% of strikes  
- Final score = mean across active buckets in view.  
- Below 0.9 → emit actionable hints: “increase moneyness window”, “ingest session 2024‑06‑12”, etc.

---

## 8) Market CLI & MCP (contract)
**CLI:**
```
chain --symbol SPX --at 2024-06-12T14:00:00Z --dte-min 0 --dte-max 45 --moneyness 0.15 --json
losscap --loss-days 2 --estimate 350
mcp    # JSON over stdio: {{ "method":"get_chain", "params":{{...}} }}
```

**MCP payloads:**
```jsonc
// get_chain request
{ "method":"get_chain", "params": { "symbol":"SPX", "at":"2024-06-12T14:00:00Z", "dte_min":0, "dte_max":45, "moneyness":0.15 } }

// response
{
  "symbol":"SPX", "atUtc":"2024-06-12T14:00:00Z",
  "count": 128, "completeness": 0.93,
  "options": [] // stream separately if needed
}
```

---

## 9) Risk & Capital Preservation
- **Reverse Fibonacci** loss caps (500 → 300 → 200 → 100).  
- Endpoint returns refusal if `estimated_worst_loss > cap`.  
- Reset to 500 after a profitable day. Persist state per calendar day (SQLite `risk_state` table).

**Snippet:**
```csharp
var refused = svc.RefuseIfLossCapBreached(est, lossDays, out var state);
if (refused) return Results.Problem($"REFUSED: {{est}} > {{state.DailyLossCap}} (rule={{state.Rule}})");
```

---

## 10) Observability & Ops
- **Logs**: JSON lines `logs/market.log` (query latencies, refusals, probe scores).  
- **Counters**: P50/P95 latencies; cache hit ratio; completeness average by symbol/day.  
- **Integrity job**: nightly compute checksums + expected minute math; raise alerts on drift.  

---

## 11) CI Gates (DoD)
- `dotnet test` must include:  
  - DTE/moneyness math invariants  
  - Completeness scoring fixtures  
  - Risk policy steps  
  - Partition path math (symbol → yyyy/mm)  
- **Static checks**: nullable enabled; no analyzer warnings.  
- **Artifacts**: packaged `Steering.md`, `ProductSpecification.md`, `SplitSchema.md`.  

---

## 12) Developer Checklist (Claude.Code)
- [ ] Implement `ThetaDbSource` real adapter (queries above).  
- [ ] Add `Parquet.Net` + writers; wire `DatasetStore` save/load.  
- [ ] Implement completeness v2 and thresholds.  
- [ ] Extend Market CLI: `snapshot` (minute), `underlying` (bars).  
- [ ] Implement MCP `get_snapshot`.  
- [ ] Add logging + counters.  
- [ ] Harden tests.  
- [ ] Document `DATA_ROOT` & examples.

---

## 13) Footguns to avoid
- Don’t widen DTE/moneyness silently to “make numbers look good”. Emit hints instead.  
- Don’t compute Greeks if vendor already provides; prefer vendor values.  
- Don’t cache indefinitely; respect session boundaries and config TTLs.  
- Don’t put provider SQL in Market/Probes.

---

## 14) Handoff Notes
- If `Stroll.Theta.DB` schema differs, create SQL **views** to match the assumed contract quickly.  
- For GOLD/OIL, start with ETF proxies (GLD/USO) and alias to `GOLD`/`OIL` in `DatasetConfig`.  
- Keep all envelopes in UTC; translate only at presentation layers.

— Updated: 2025-09-01 18:37 (Asia/Kolkata)
