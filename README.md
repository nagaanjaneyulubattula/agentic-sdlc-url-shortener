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

## Run it and test it fully (step by step)

### 0. Prerequisites
| Need | Version | Notes |
|---|---|---|
| **.NET SDK** | 8.0 or newer | `dotnet --version`. Targets `net8.0` (developed on SDK 10). |
| Internet (first run only) | – | NuGet restore of xUnit / test packages. |
| Node.js | 18.19+ / 20 / 22 | **Optional** – only to rebuild or hot-reload the Angular UI. The built UI is already committed in `src/Agentic.Api/wwwroot`, so you do **not** need `npm install` to run the console. |

Works on Windows, macOS and Linux. Ports used: **5080** (console + control API) and **5000** (URL shortener) – change with `--urls`.

### 1. Get the code (never share `node_modules`)
```bash
git clone <repo-url> && cd agentic-sdlc-url-shortener
```
`node_modules/`, `bin/`, `obj/` and `runs/` are git-ignored and are not in the repository. If you share a zip instead of a git link, create it from git so those folders are left out: `git archive --format=zip -o agentic-sdlc.zip HEAD`.

### 2. Run the complete test suite (≈ 1–2 min, 501 tests)
```bash
dotnet test AgenticSdlc.sln
```
Expected: `UrlShortener.Tests` **322 passed**, `Agentic.Tests` **179 passed**, 0 failed. This includes **4 end-to-end tests** that run the real agents on the greenfield / brownfield / ambiguous / chaos scenarios and execute a real `dotnet build` + `dotnet test` on the generated code (first run is slower while packages restore).

Handy subsets:
```bash
dotnet test AgenticSdlc.sln --filter "Category!=E2E"                 # fast loop (~10 s), skips the 4 end-to-end tests
dotnet test tests/UrlShortener.Tests                                 # product only (322)
dotnet test tests/Agentic.Tests --filter "Category=E2E"              # only the 4 end-to-end scenario tests
```

### 3. Run the console and try the scenarios (human-in-the-loop)
```bash
dotnet run --project src/Agentic.Api
```
Open **http://localhost:5080**. Pick a scenario, leave **Auto-approve** *off* to act as the human – answer the clarification questions, approve / reject each gate, press **Stop** (safe-stop) and **Resume** – and watch the dependency graph, metrics, audit trail (with hash-chain check), decision lineage and the final engineering summary. Turn **Auto-approve** on to let a scripted approver run it unattended.

### 4. Or run a scenario headless in the terminal
```bash
dotnet run --project src/Agentic.Api -- run greenfield         # clear requirement, empty repo (injected faults → retry + rollback)
dotnet run --project src/Agentic.Api -- run brownfield         # existing v1 code with hidden defects → reproduce, impact, change-controlled fix
dotnet run --project src/Agentic.Api -- run ambiguous          # vague request → clarification + mid-run change → minimal re-plan
dotnet run --project src/Agentic.Api -- run chaos-safe-stop    # extra: secret leak → critical policy → safe-stop
dotnet run --project src/Agentic.Api -- run ambiguous --manual # be asked for every approval on the console
```
Each run prints the live audit stream and a result line (status, release decision, metrics) and writes `runs/<runId>/`: the generated `workspace/` (a real .NET solution – `dotnet test runs/<runId>/workspace/tests/UrlShortener.Tests` works), `audit.jsonl`, `state.json`, `metrics.json`, `artifacts/` and `ENGINEERING_SUMMARY.md`. Exit code is `0` when the run completes.

### 5. Run the URL shortener itself
```bash
dotnet run --project src/UrlShortener.Api --urls http://localhost:5000
curl -s -X POST localhost:5000/api/links -H "Content-Type: application/json" \
     -d '{"url":"https://example.com/docs","alias":"docs","ttlSeconds":3600}'
curl -i localhost:5000/docs                 # 302 → https://example.com/docs
curl -s localhost:5000/api/links/docs/stats # clicks, unique visitors, per-day, referrers
curl -s localhost:5000/healthz ; curl -s localhost:5000/metrics
```
Unsafe URLs (`javascript:`, `file:`, internal/metadata hosts) are rejected with `400`.

### 6. Optional – Angular UI development (the only step that needs Node)
```bash
cd web/agentic-console
npm install            # creates node_modules/ locally (git-ignored)
npm start              # dev server on http://localhost:4200, proxies /api → :5080 (start Agentic.Api first)
npm run mock           # alternative: stand-alone mock backend on :5080 for UI work
npm run build          # rebuilds the production UI into src/Agentic.Api/wwwroot
```

### Troubleshooting
| Symptom | Fix |
|---|---|
| `address already in use` | another process holds 5080/5000 – stop it or pass `--urls http://localhost:<port>` (the Angular dev proxy expects 5080). |
| First `dotnet test` is slow / restore errors | needs internet once for NuGet; re-run. |
| `dotnet run` says file is locked after tests | stop running servers before `dotnet test` / rebuild (they lock the built DLLs). |
| E2E test timeout on a slow machine | run it alone: `dotnet test tests/Agentic.Tests --filter "Category=E2E"`. |
| Blank console page | rebuild the UI (`npm run build`) or use the dev server (step 6). |

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
