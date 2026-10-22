# Agentic SDLC Orchestrator – URL Shortener

An agentic software-engineering system built on **.NET 8 + Angular 18**. It turns a requirement into a reviewable engineering outcome (design, code, tests, docs, release-readiness) by orchestrating specialised agents over an **explicit dependency graph** with gates, human approval checkpoints, bounded retries / fallback / rollback / safe-stop, policy guardrails, an audit-grade hash-chained trail, reliability metrics and **dynamic re-planning**. The system under construction is a **URL shortener** (core APIs, analytics, reliability features).

> **Principle** – agents execute under defined autonomy boundaries; humans own oversight, approvals and final quality.

| Assessment ask | Where it lives |
|---|---|
| Requirement understanding (ambiguity detection, normalisation) | `RequirementsAgent` – clarification gate; ambiguous scenario |
| Task decomposition with dependencies | `ArchitectAgent` → `TaskSpec`s → dynamic graph expansion; design artifact |
| Codebase reasoning (brownfield) | `CodebaseAnalystAgent` (static analysis: routes, hooks, dependency graph, findings, impact) + defect reproduction |
| **Workflow orchestration** (critical differentiator) | `src/Agentic.Orchestrator/Engine` – see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Production-quality output (code, API schema, tests, docs) | `src/UrlShortener.Api` (322 tests), generated OpenAPI + docs per run |
| Validation & risk control | policy engine, exit gates, traceability matrix, risk register, release checklist |
| Controlled autonomy | autonomy boundaries enforced by a sandboxed workspace; approvals before/after; safe-stop |
| Final engineering summary | generated per run: `runs/<id>/ENGINEERING_SUMMARY.md` |
| Three scenarios (greenfield / brownfield / ambiguous) | `scenarios/*.json`, [docs/SCENARIOS.md](docs/SCENARIOS.md) |

## Quick start

Prerequisites: **.NET SDK 8+** (developed on SDK 10, targets `net8.0`). Node.js is only needed to rebuild the Angular UI – the built UI is committed under `src/Agentic.Api/wwwroot`.

```bash
# 1. everything (product tests + engine/agent tests + end-to-end scenario tests that build and test generated workspaces)
dotnet test AgenticSdlc.sln

# fast loop (skips the ~minutes-long end-to-end scenarios)
dotnet test AgenticSdlc.sln --filter "Category!=E2E"
```

```bash
# 2. open the console (live DAG, approvals, metrics, audit, lineage, summary) at http://localhost:5080
dotnet run --project src/Agentic.Api
```
Pick a scenario, leave **Auto-approve** off to act as the human: approve/reject the checkpoints, answer clarification questions, press **Stop** (safe-stop) / **Resume**.

```bash
# 3. headless run in the terminal (scripted approver by default, --manual to be asked on the console)
dotnet run --project src/Agentic.Api -- run greenfield
dotnet run --project src/Agentic.Api -- run brownfield
dotnet run --project src/Agentic.Api -- run ambiguous
dotnet run --project src/Agentic.Api -- run chaos-safe-stop      # extra: critical policy violation → safe-stop
```
Each run writes to `runs/<runId>/`: `workspace/` (the generated or modified .NET solution – `dotnet test workspace/tests/UrlShortener.Tests` works on it), `audit.jsonl`, `state.json`, `metrics.json`, `artifacts/`, `ENGINEERING_SUMMARY.md`.

```bash
# 4. the product itself (the full URL shortener)
dotnet run --project src/UrlShortener.Api --urls http://localhost:5000
curl -s -X POST localhost:5000/api/links -H 'Content-Type: application/json' \
     -d '{"url":"https://example.com/docs","alias":"docs","ttlSeconds":3600}'
curl -i localhost:5000/docs
curl -s localhost:5000/api/links/docs/stats
```

Rebuild the UI (optional): `cd web/agentic-console && npm install && npm run build` (outputs into `src/Agentic.Api/wwwroot`); dev mode with proxy: `npm start` (+ `npm run mock` for a stand-alone mock backend).

## How it works in one page

1. **Plan** – `PlanBuilder` defines the SDLC graph; `requirements`, brownfield analysis, `design`, `scaffold`/`test-plan`, per-feature `implement:*`, `integrate`, `verify-full ∥ verify-traceability ∥ security-scan ∥ document`, `release-readiness`, `summarize`.
2. **Decompose dynamically** – the architect turns requirements into feature tasks and *expands the graph*; independent slices run in parallel and join at `integrate`.
3. **Execute with control** – each node: entry gates → agent in a sandbox (autonomy boundary, journaled writes) → engine-wide policy scan → exit gates → approval (if required) → atomic commit. Failures: rollback → bounded retry (backoff) → fallback agent → safe-stop.
4. **Govern** – high-impact actions (sensitive slices, modifying existing code, architecture sign-off, release) need a human; critical policy violations stop the run immediately.
5. **Re-plan** – every node records the hash of what it read; a mid-run requirement change invalidates and re-queues only the affected nodes.
6. **Prove** – hash-chained audit log, decision lineage, traceability matrix, metrics (success rate, retries, rollbacks, MTTR, end-to-end latency), and a final engineering summary.

## Repository layout
```
src/UrlShortener.Api        product (feature slices) – also the component library
src/Agentic.Orchestrator    engine (Engine/), agents (Agents/Planning, Agents/Execution), catalog, policy
src/Agentic.Api             REST control plane + headless CLI + static hosting of the console
web/agentic-console         Angular 18 console (+ mock backend for UI development)
library/catalog.json        feature catalog: requires, routes, hooks, acceptance criteria, risks
library/baseline            weak v1 files used to seed the brownfield scenario (known defects)
scenarios/                  greenfield, brownfield, ambiguous, chaos-safe-stop
tests/                      UrlShortener.Tests, Agentic.Tests (unit + E2E)
docs/                       ARCHITECTURE.md, SCENARIOS.md, TESTING.md
```

## Testing approach, limitations, trade-offs
See [docs/TESTING.md](docs/TESTING.md) (approach, evidence, limitations) and the trade-off table in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#3-key-design-decisions-and-trade-offs).

**Honest limitations**: agents are deterministic rule-based workers behind an `IAgent` contract (reproducible and testable offline; an LLM-backed agent can be dropped in without touching the engine) · the link store, rate limiter and idempotency store are in-memory/single-instance · the product has no authentication (deploy behind an authenticated gateway) · demos use a *scripted* approver (clearly labelled in the audit trail) – the console and `--manual` provide the real human path · run state is held in memory by the API process (artifacts, audit and state are also persisted to disk, but a process restart does not reattach to a run).
