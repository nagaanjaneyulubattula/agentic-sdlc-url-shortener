# The three scenarios (+ one chaos demo)

Each scenario is a JSON file in `scenarios/`. Run it from the console (http://localhost:5080) or headless:

```bash
dotnet run --project src/Agentic.Api -- run greenfield        # or: brownfield | ambiguous | chaos-safe-stop
```
Numbers below were observed in real local runs (they are asserted in `tests/Agentic.Tests/Scenarios/ScenarioE2ETests.cs`). Every run produces `runs/<id>/` with the generated workspace, `audit.jsonl`, `metrics.json`, artifacts and `ENGINEERING_SUMMARY.md`.

---

## 1. Greenfield – "Build a URL shortener …"
**Input** – a well-defined requirement (aliases, expiry, click tracking without raw IPs, rate limiting, idempotency, health/metrics, inspect/delete) and an **empty** repository.

**Decomposition** (`requirements` → `design`)
* Requirements agent extracts 9 catalog features (+ always-on hardening), 1 low-risk ambiguity becomes an explicit assumption.
* Architect closes dependencies (e.g. `analytics` ⇒ `shorten`,`redirect`), writes the OpenAPI document, 7 ADRs, a risk register, and **expands the graph into 9 `implement:<feature>` nodes** whose edges follow the feature dependencies.

**Orchestration**
```
requirements ─► design ─┬─► scaffold ─┬─► implement:shorten ─► implement:redirect ─► implement:{expiry, analytics, manage}   (parallel)
 (approval)   (approval)│             ├─► implement:{alias, ratelimit, idempotency, observability}                          (parallel)
                        └─► test-plan │                                  ▼ join
                                                    integrate ─┬─► verify-full ─┬─► release-readiness (human go/no-go) ─► summarize
                                                               ├─► verify-traceability ┤
                                                               ├─► security-scan       ┤
                                                               └─► document ───────────┘
```
* Sensitive slices (`analytics` = visitor data, `ratelimit` = security control) are gated by a **human approval before** anything is written.
* **Injected faults prove the resilience controls**: `implement:analytics` crashes on attempt 1 → *rollback + retry*; `implement:expiry` leaves a syntactically broken file → Roslyn *syntax gate fails → rollback → retry*.

**Validation (observed)** – 20 nodes Done (3 brownfield-only nodes Skipped) · generated workspace **322/322 tests pass** (real `dotnet test`) · **34/34 acceptance criteria traced to tests** · security/compliance scan: 52 files, no findings · release checklist all `pass` → **GO** · retries 2, rollbacks 1, attempt success 91%, MTTR ≈ 0.1 s, end-to-end ≈ 9 s · audit chain valid.

---

## 2. Brownfield – "Fix security defects and add expiry + analytics to the v1 service"
**Input** – an **existing v1 codebase** (seeded from `library/baseline`) with two defects the agents are *not told about*: DEF-1 (any URL scheme/host accepted → open-redirect/XSS/SSRF vector) and DEF-2 (`Random` for short codes → enumerable). Requirement text mentions the symptoms only.

**Codebase reasoning**
* `analyze-codebase` (parallel with requirements): static analysis of the workspace → routes, hook implementations, type-dependency graph, data flows, installed features (`shorten, redirect, manage`), **findings** (`FIND-URL-SCHEME` CWE-601, `FIND-SEC-004` CWE-338) and the **baseline test run (89/89 green = regression safety net)**.
* `reproduce`: copies the workspace to a temp dir, adds the hardening tests and runs them → **77 of 186 fail on the unpatched code – the defects are proven before anything is changed**.
* `impact-assessment`: maps requested features onto existing modules: `UrlValidator.cs` & `CodeGenerator.cs` (**modify, high risk**), hook invocation sites in `ShortenEndpoint.cs`, `RedirectFeature.cs`, `Hooks.cs` (**extend, medium – additive only**), `FeatureRegistry.cs` (regenerated, low); API changes (400s for unsafe URLs, `ttlSeconds`, 410 on expiry, new `/stats`).

**Orchestration / change control**
* `scaffold` is *skipped* (condition `workspace:baseline`); the plan is a **patch** (`implement:hardening`) + two additive slices.
* The patch modifies existing, security-sensitive files → **human approval before it is applied** (with the file list). `analytics` is gated too.
* Fault: the first patch attempt leaves broken code → syntax gate fails → **rollback restores the original (weak) files byte-for-byte** → retry succeeds.

**Validation (observed)** – 241/241 tests after the change (the 77 reproduced failures now pass) · 21/21 acceptance criteria traced · release item **REL-06 "defect reproduced before the fix and suite green after" = pass** · blast radius 16 files (13 created, 3 modified) · `CHANGELOG.md` lists modified files, new files, API changes and findings fixed · GO.

---

## 3. Ambiguous – "Make it better for our marketing team. Fast and secure, scale well, some reporting."
**Input** – a vague business request on an empty repository.

**Requirement understanding**
* The requirements agent detects **six ambiguities** with evidence: `AMB-PERFORMANCE` (fast – no target), `AMB-SECURITY`, `AMB-REPORTING` (**high**), `AMB-SCALE`, `AMB-QUALITY`, `AMB-ACTOR` (medium). Medium/low ones become documented **assumptions with defaults**; high ones **block**.
* Exit gate `requirements-clear` → *needs human* → the engine asks 3 questions. Answers ("p95 under 50 ms", "rate limit creation and block dangerous URLs", "daily clicks per link with top referrers, JSON API") are merged and the agent **re-runs** (asking a human is not counted as a failure/retry). Unanswered → safe-stop, never a guess.
* Answers turn into NFR targets and features (`analytics`, `ratelimit` marked `clarified`).

**Dynamic re-planning** – after `implement:redirect` completes, a **change request** arrives: *"campaign links must expire; rate limiting will be handled by the API gateway"* (+`expiry`, −`ratelimit`).
* The requirements artifact gets a new hash → `design` and `test-plan` (the only nodes that read it) are invalidated; **2 re-plans** recorded.
* `design` re-runs (and is re-approved by the human), the expansion diff **adds `implement:expiry`** and **cancels `implement:ratelimit`, reverting its files**. Untouched nodes (`implement:redirect`, `shorten`, `analytics`) are **not redone** (generation stays 1).

**Validation (observed)** – final workspace tests green, change reflected in features/OpenAPI/docs, lineage links the amendment to the artifact hashes; audit chain valid (154 entries with human actors `human:<approver>` and `human:change-request`).

---

## Extra – chaos / safe-stop
A fault makes the implementer leak an AWS-style key. Policy `SEC-001` is **critical** → no retry; the node is rolled back (3 file operations undone), dependents are `Blocked`, the run is **SafeStopped** with state and audit intact (82 entries, chain valid). After the operator removes the cause, **Resume** continues: finished nodes are not redone and the run completes.

## Human-in-the-loop path
`autoApprove=false` (default in the console) puts every checkpoint into the approval queue: approve/reject with a comment, or answer clarification questions. Verified through the REST queue: clarification answered → gates approved (including the second architecture approval after the re-plan) → Completed; and rejecting the architecture gate ends the run as **Rejected**. Demos/tests use a *scripted* approver that is labelled `scripted:<scenario>` in the audit trail.
