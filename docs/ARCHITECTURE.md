# Architecture overview

> **Principle** – agents execute under defined autonomy boundaries; humans own oversight, approvals and final quality.

## 1. What was built

| Layer | Project | Role |
|---|---|---|
| **Product** (the thing being engineered) | `src/UrlShortener.Api` | ASP.NET Core 8 minimal API: create/redirect/manage links, aliases, expiry, click analytics, rate limiting, idempotency, health/metrics. Organised as **vertical feature slices** behind small hook interfaces. It is also the *component library* the agents assemble from. |
| **Orchestration engine** (critical differentiator) | `src/Agentic.Orchestrator` | Explicit dependency graph, stateful scheduler, gates, retries/fallback/rollback/safe-stop, approvals, policy guardrails, audit chain, metrics, dynamic re-planning, 11 deterministic agents. |
| **Control plane** | `src/Agentic.Api` | REST API + headless CLI (`-- run <scenario>`) over the engine; serves the Angular console. |
| **Console** | `web/agentic-console` (Angular 18) | Live DAG, human approval queue (approve / reject / answer clarifications), metrics, audit trail with chain verification, decision lineage, artifacts, engineering summary. |
| **Tests** | `tests/UrlShortener.Tests`, `tests/Agentic.Tests` | Product unit+integration tests; engine/agent unit tests; end-to-end scenario tests that run the real agents and a real `dotnet test` on the generated workspaces. |

```
                       ┌──────────────────────── Angular console ────────────────────────┐
                       │ DAG · approvals · metrics · audit (hash chain) · lineage · docs │
                       └───────────────▲──────────────────────────────┬──────────────────┘
                                       │ REST (poll)                   │ approve / reject / answer
                       ┌───────────────┴──────────────────────────────▼──────────────────┐
                       │                    Agentic.Api  (RunManager)                     │
                       └───────────────▲──────────────────────────────┬──────────────────┘
                                       │ views                         │ IApprovalProvider
┌──────────────────────────────────────┴───────────────────────────────▼───────────────────────────────┐
│                                       Agentic.Orchestrator                                            │
│  PlanBuilder ─► DependencyGraph ─► Engine (scheduler) ─┬─► Gates (entry/exit)                         │
│   (static DAG)    (validate, layers,   │               ├─► PolicyEngine + ChangeControl (guardrails)  │
│                    critical path)      │               ├─► Approvals (before/after, clarification)    │
│        ▲                               │               ├─► WorkspaceManager (sandbox + journal)       │
│        │ Expansion                     │               ├─► ArtifactStore (versioned, content-hashed)  │
│        │ (architect adds nodes)        │               ├─► AuditLog (hash-chained) + Metrics          │
│        │                               ▼               └─► Reconcile (content-addressed re-planning)  │
│   ┌────┴────────────── agents (IAgent: name, autonomy boundary, RunAsync) ───────────────┐             │
│   │ requirements · codebase-analyst · architect · test-planner · implementer(+clean-room)│             │
│   │ tester · security-reviewer · documenter · release-manager · summarizer               │             │
│   └──────────────────────────────────────────────────────────────────────────────────────┘             │
└───────────────────────────────┬───────────────────────────────────────────────────────────────────────┘
                                │ reads vetted slices           │ writes (sandboxed, journaled)
                    ┌───────────▼───────────┐          ┌────────▼─────────────────────────┐
                    │ library/catalog.json  │          │ runs/<id>/workspace  (generated  │
                    │ + src/UrlShortener.Api│          │ .NET solution, built & tested by │
                    │   Features/<Slice>    │          │ `dotnet test`)                   │
                    └───────────────────────┘          └──────────────────────────────────┘
```

## 2. Orchestration model

### 2.1 Explicit dependency graph with gates
`PlanBuilder` defines the SDLC as a DAG (`NodeSpec`: agent, `Deps`, `Reads`, entry gates, exit gates, retry policy, fallback, approval spec, condition). `DependencyGraph.Validate()` rejects duplicates, unknown deps and cycles; `Layers()` yields the parallel groups; `CriticalPath()` feeds the metrics.

```
requirements ──┐                                   ┌─ implement:<feature> … (parallel, one per slice)
analyze-codebase ─► reproduce ─┐                   │          │ join (sync barrier)
        └─► impact-assessment ─┴─► design ─┬─► scaffold ─────────┴─► integrate ─┬─► verify-full ──────┐
                                           └─► test-plan ───────────────────────┼─► verify-traceability ├─► release-readiness ─► summarize
                                                                                 ├─► security-scan ──────┤   (human go/no-go)
                                                                                 └─► document ──────────┘
```
* **Non-linear**: brownfield analysis runs in parallel with requirements; after `design` the architect *expands the graph* (`Expansion`) with one `implement:<feature>` node per slice and the engine joins them into `integrate`; verification, security scan and documentation run in parallel after integration.
* **Entry gates** (preconditions, e.g. `workspace-ready`) and **exit gates** (postconditions: `requirements-valid`, `requirements-clear`, `design-valid`, `files-written`, `syntax-valid` (Roslyn parse of changed C#), `tests-green`, `traceability-complete`, `security-passed`) are named, serialisable and audited.
* **Conditional nodes** (`workspace:baseline` / `workspace:empty`) are *skipped* (not failed) so greenfield and brownfield share one graph.

### 2.2 Node lifecycle
`Pending → Running → (AwaitingApproval) → Done`, or `Failed / Blocked / Rejected / Cancelled / Skipped / Stale`.

```
attempt ─► agent runs in sandbox ─► policy scan (engine-wide) ─► exit gates
   │ fail (non-critical)                                            │ pass
   ▼                                                                ▼
rollback journal ─► bounded retry (exp. backoff) ─► fallback agent ─► approval-after / change control ─► atomic commit
                                                    │ exhausted                                          (artifacts, lineage, changes)
                                                    ▼
                                    node Failed ─► dependents Blocked ─► SAFE-STOP (state, workspace, audit preserved; resumable)
critical policy violation / autonomy breach / blast-radius breach  ──► immediate rollback + SAFE-STOP (no retry)
human rejection ──► rollback + run Rejected
```
* **Bounded retries**: per-node `RetryPolicy(MaxRetries, BackoffMs)` + a global `MaxTotalRetries` runaway budget.
* **Fallback**: after retries a different agent strategy is tried once (e.g. `implementer-clean-room` wipes the slice and reinstalls).
* **Rollback**: every write is journaled per node (`WorkspaceManager`); a failed attempt, rejected approval, superseded node or removed feature restores the exact previous bytes (and deletes created files). Parallel nodes cannot write the same file concurrently (conflict detection).
* **Safe-stop**: halts scheduling, leaves finished work intact, marks the rest `Blocked`, persists `state.json`/`audit.jsonl`. `Resume` re-queues failed/blocked nodes without redoing completed ones. A kill-switch (`RequestStop`) aborts in-flight work (rolled back).

### 2.3 Context, decision lineage, least privilege
Agents read only the artifacts a node declares in `Reads` (others → denied + audited `AUT-002`). Artifacts are versioned and **content-hashed**. Every agent returns `Decision`s (title, rationale, alternatives, trade-off); the engine stamps each with the node and the hashes of the artifacts it was based on → `DecisionRecord` lineage (viewable in the console and in the final summary).

### 2.4 Human approval checkpoints (controlled autonomy)
* **Declared** on nodes: requirements baseline (medium, after), architecture/change plan (high, after), sensitive slices (analytics, rate-limit) and any patch to existing security code (high, **before** writing), release go/no-go (high, after).
* **Policy-driven** (CHG-001): any node that *modifies/deletes existing* non-generated files, without an explicit approval spec, is gated automatically with the file list and diff summary.
* **Clarification loop**: the requirements agent will not guess high-severity ambiguities; the exit gate `requirements-clear` returns *needs-human*, the engine asks the questions, merges the answers and re-runs the agent (bounded rounds). Unanswered → safe-stop, not a guess.
* Approver is an interface: Angular console (queue), terminal (`--manual`), or a *scripted approver* for demos/tests (always labelled `scripted:<scenario>` in the audit log).

### 2.5 Policy guardrails (security, compliance, change control)
| Rule | Category | Severity | Effect |
|---|---|---|---|
| SEC-001 secrets / private keys in code | security | critical | immediate rollback + safe-stop |
| SEC-002 process exec / dynamic loading / BinaryFormatter | security | block | attempt fails (retry/rollback) |
| SEC-003 outbound HTTP/sockets from the service (SSRF surface) | security | block | " |
| SEC-004 `System.Random` in the service (CSPRNG only) | security | block | " |
| COMP-001 raw client IP read outside `Core/ClientKey.cs` | compliance (privacy) | block | " |
| COMP-002 raw IP persisted by a feature | compliance | block | " |
| CHG-001 modification of existing code needs human approval | change control | approval | gate |
| CHG-002 blast radius > 120 files per run | change control | critical | safe-stop |
| AUT-001 write outside the agent's autonomy boundary / sandbox escape | autonomy | critical | safe-stop |
| AUT-002 read of an undeclared artifact | autonomy | warn | denied, audited |

Autonomy boundaries are enforced by the workspace capability, not by agent good behaviour: e.g. the documenter can only write `README.md`, `docs/**`, `CHANGELOG.md`; the tester and release manager can write nothing.

### 2.6 Audit-grade observability and traceability
* **Hash-chained audit log** (`audit.jsonl`): every transition, gate verdict, policy violation, approval, re-plan and artifact commit is appended with `prevHash`/`hash` (SHA-256). `Verify()` (and the console) recompute the chain; any edit or deletion is detected at the first broken sequence number.
* **Traceability matrix**: requirement → feature → acceptance criterion (`AC-…`) → xUnit test (`[Trait("AC", …)]`); the `traceability-complete` gate fails if any criterion has no test.
* **Decision lineage** links each design/approval decision to the exact artifact hashes it was based on.

### 2.7 Reliability metrics
`MetricsCalculator`: node **success rate**, **attempt success rate**, **retries**, **rollbacks**, **fallbacks**, **re-plans**, **policy violations**, approvals requested/granted/rejected, **MTTR** (mean time from a node's first failure to its next success), **end-to-end latency** (with human wait time reported separately and excluded from node durations), **critical path** and per-stage latency. Exposed live in the console, written to `metrics.json`, embedded in the engineering summary.

### 2.8 Dynamic re-planning (content-addressed)
Each node records, at start, the hash of every artifact in its `Reads` (**input ledger**). `Reconcile()` runs atomically with every commit and every mid-run change request:
1. any `Done` node whose consumed hash changed → reverted (journal) and re-queued (`Stale`);
2. any `Running` node whose input changed → *superseded*: its result is discarded when it finishes;
3. when an expander (`design`) re-runs, `ApplyExpansion` diffs the node set: new nodes added and joined, removed nodes `Cancelled` and their committed files reverted;
4. nodes whose inputs did **not** change keep their results (minimal re-plan; governance gates run again for re-executed nodes, e.g. the architecture approval).

## 3. Key design decisions and trade-offs

| Decision | Why | Trade-off / mitigation |
|---|---|---|
| **Deterministic, rule-based agents** behind an `IAgent` contract (no LLM call) | The prototype must be reproducible, offline, testable and fast to review; outputs are asserted in tests. | Less open-ended than an LLM. The contract (typed artifacts, autonomy boundary, `Decision`s) is exactly what an LLM-backed agent would implement; swapping one in does not touch the engine. |
| **Assemble from a vetted component library** (feature slices) instead of free-form code generation | Production-quality, reviewed code with tests per slice; the same repo is the product and the library (no duplication); safe change management. | Agents cannot invent new features outside the catalog – out-of-catalog requirements surface as ambiguity/NO-GO, not as silent guesses. |
| **Vertical slices + hook interfaces** (`IRequestGate`, `IShortenHook`, `IRedirectHook`) | Slices are independently installable → parallel implementation, minimal blast radius, impact analysis is tractable. | A small amount of indirection in the product core. |
| **Content-addressed invalidation** rather than "re-run everything downstream" | Re-plans touch only what changed; makes the re-plan explainable and auditable. | Needs accurate `Reads` declarations (enforced: undeclared reads are denied). |
| **Journal-per-node rollback** (not git stash) | Parallel nodes need independent, surgical undo; also powers re-plan reverts. | Single-process; not distributed. |
| **302 not 301** redirects | Keeps analytics/expiry/deletion accurate (no browser caching). | More origin traffic. |
| **In-memory store behind `ILinkStore`** | Prototype scope; swap for Redis/SQL without touching features. | No durability across restarts (documented limitation). |
| **HMAC-hashed client key** instead of raw IP | Privacy by design: unique-visitor counting without storing personal data (policy COMP-001/002). | Salt rotation resets unique counts. |
| **Roslyn syntax gate** per node + one real `dotnet test` after integration | Fast parallel safety net; full compile/test only once (parallel `dotnet build` in one tree is unsafe). | Semantic errors are caught at integration, not per slice. |

## 4. Failure scenarios considered

| Failure | Control | Evidence |
|---|---|---|
| Agent crashes / transient error | rollback + bounded retry + backoff | `Failed_attempt_is_rolled_back_then_retried…`, greenfield scenario (`implement:analytics`) |
| Agent leaves broken code | syntax gate → rollback → retry | greenfield (`implement:expiry`), brownfield (`implement:hardening`) |
| Retries exhausted | fallback agent, then safe-stop | `Exhausted_retries_engage_the_fallback_agent`, `Total_failure_safe_stops…` |
| Agent leaks a secret | critical policy → no retry, rollback, safe-stop | chaos scenario |
| Agent writes outside its remit / path traversal | sandbox + autonomy boundary | `WorkspaceManagerTests`, `Autonomy_boundary_violation_is_critical` |
| Runaway change set | blast-radius cap | `Blast_radius_limit_halts_the_run` |
| Ambiguous requirement | clarification gate; never guessed | ambiguous scenario |
| Requirement changes mid-flight | content-addressed re-plan | `EngineReplanTests`, ambiguous scenario |
| Human disagrees | rejection → rollback → run `Rejected` | `EngineGovernanceTests` |
| Operator pulls the plug | kill-switch aborts + rolls back | `Kill_switch_aborts_inflight_work_and_safe_stops` |
| Tampered audit trail | hash chain verification | `AuditLogTests` |
