# Testing approach, evidence, limitations

```bash
dotnet test AgenticSdlc.sln                              # everything  → 501 tests, 0 failures (~1 min)
dotnet test AgenticSdlc.sln --filter "Category!=E2E"     # fast loop (skips the 4 end-to-end scenario tests)
```

## Test pyramid

| Layer | Project / folder | Tests | What it proves |
|---|---|---|---|
| Product unit + in-process HTTP integration | `tests/UrlShortener.Tests` | **322** (164 methods, parametrised) | Every acceptance criterion `AC-*` of every feature slice; SSRF/URL hardening matrix; code generator uses a CSPRNG (assembly reference check); rate-limit, idempotency, expiry with a fake clock; analytics bounded and PII-free; failure injection (store throws → redirect still works; readiness 503). Each test carries `[Trait("AC","…")]`. |
| Engine unit | `tests/Agentic.Tests/Engine` | **53** | Graph validation (cycles, unknown deps, layers, critical path); hash-chained audit (tamper/deletion detection, concurrency); workspace sandbox (traversal, boundaries, rollback, conflicts); policy rules; **parallel execution and join order; retry+rollback; fallback; safe-stop and resume; critical-policy stop; autonomy breach; blast radius; entry gate; conditional skip; kill-switch; approvals before/after, rejection → rollback; change control; clarification loop; content-addressed re-planning (add/remove features, revert of completed work); metrics**. Uses fake agents so each control is tested in isolation and deterministically. |
| Agent unit | `tests/Agentic.Tests/Agents` | **122** (44 planning, 78 execution) | Ambiguity lexicon and answer handling, determinism (identical JSON on repeat), static analysis of fixture code, impact mapping, architect closure/expansion/approvals/OpenAPI/ADRs/risks, test-plan, implementer create/modify/unchanged semantics and clean-room fallback, traceability scanning, security scan, docs accuracy, release GO/NO-GO logic, summary structure. |
| **End-to-end scenarios** | `tests/Agentic.Tests/Scenarios` | **4** (~45 s) | Real agents + real `dotnet build/test` of the generated workspaces for greenfield, brownfield, ambiguous and chaos/safe-stop(+resume) – asserting statuses, retries/rollbacks, approvals, re-plan behaviour, 322/241 generated tests green, traceability 100 %, release GO, audit-chain integrity and the 8-section engineering summary. |

Manual verification (this session): the same four scenarios through the REST API and the Angular console (`http://localhost:5080`), including the **human approval queue** (clarification answers, approvals, a rejection at the architecture gate) and **safe-stop → resume**.

## Validation built into every run (not just the test suite)
* Exit gates: Roslyn syntax gate per node, `tests-green`, `traceability-complete` (every acceptance criterion has a test), `security-passed`, requirements/design validity.
* Engine-wide policy scan after every node (secrets, process exec, outbound network, weak RNG, raw IP handling).
* Brownfield: green baseline run **before** change, **defect reproduction** (failing tests) before the fix, green full suite after.
* Release checklist REL-01…REL-08 and a human go/no-go.

## Limitations (honest)
* **Agents are deterministic rule-based workers**, not LLMs: reproducible and testable, but they only deliver what the catalog and rules support. Out-of-catalog requirements become ambiguities / NO-GO rather than invented code. The `IAgent` contract is the seam for LLM-backed agents.
* **Requirement NLP is lexicon-based**: it is explicit and auditable, but it is not general language understanding.
* **Single process**: run state lives in the API process (artifacts, audit and state are also written to `runs/<id>/`, but a restart does not reattach to a run); journals/rollback are not distributed.
* **Product scope**: in-memory store, rate limiter and idempotency store (single instance); no authentication on the API (deploy behind an authenticated gateway); analytics retained in memory with a bounded ring buffer.
* **Semantic errors are caught at integration** (one `dotnet test`), per-slice gating is syntactic + policy – parallel `dotnet build` inside one tree is unsafe.
* **Scripted approver** is used in automated runs (labelled in the audit trail); real human decisions go through the console / `--manual`.
* No load/performance testing and no mutation testing; coverage is measured by acceptance-criterion traceability rather than line coverage.
* The E2E tests need the .NET SDK, a populated NuGet cache (first run restores packages) and take about a minute.
