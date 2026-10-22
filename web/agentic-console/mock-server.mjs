// Dependency-free mock of the Agentic orchestrator REST API (port 5080).
// Simulates a time-progressing DAG run with retries, rollback/fallback, approval gates,
// a clarification request, a replan, a hash-chained audit log, lineage, artifacts and a summary.
//   node mock-server.mjs            -> http://localhost:5080
import http from 'node:http';
import crypto from 'node:crypto';

const PORT = Number(process.env.PORT || 5080);
const TICK_MS = 300;

const SCENARIOS = [
  {
    id: 'greenfield-url-shortener',
    title: 'Greenfield: URL shortener service',
    mode: 'greenfield',
    description: 'Build a brand-new URL shortener from scratch: API, storage, tests and a staging deploy.',
    requirementText:
      'Build a URL shortener service.\n- POST /links creates a short code for a long URL.\n- GET /{code} redirects (302) to the original URL.\n- Codes are 7 chars, base62, collision-free.\n- p95 redirect latency < 50 ms.\n- Provide unit + integration tests and deploy to staging.',
  },
  {
    id: 'brownfield-link-expiry',
    title: 'Brownfield: link expiry + click analytics',
    mode: 'brownfield',
    description: 'Extend the existing shortener with expiring links and click analytics without breaking current clients.',
    requirementText:
      'Extend the existing URL shortener:\n- Links may carry an optional expiresAt; expired links return 410.\n- Record click events (timestamp, referrer, country) and expose GET /links/{code}/stats.\n- Existing clients and the current database schema must keep working (backward compatible).\n- Migration must be reversible.',
  },
  {
    id: 'ambiguous-enterprise-ready',
    title: 'Ambiguous: make it enterprise ready',
    mode: 'ambiguous',
    description: 'A vague request that forces the orchestrator to stop and ask the human clarifying questions.',
    requirementText: 'Make the URL shortener enterprise ready, fast.',
  },
];

// ---------------------------------------------------------------- node templates
// dur = simulated work time (ms)
const NODE_DEFS = [
  { id: 'requirements', stage: 'Requirements', agent: 'RequirementsAnalyst', deps: [], dur: 1500 },
  { id: 'impact-analysis', stage: 'Analysis', agent: 'ImpactAnalyzer', deps: ['requirements'], dur: 1800, only: ['brownfield'] },
  { id: 'architecture', stage: 'Design', agent: 'SolutionArchitect', deps: ['requirements'], dur: 2000 },
  { id: 'api-design', stage: 'Design', agent: 'ApiDesigner', deps: ['architecture'], dur: 1500 },
  { id: 'data-model', stage: 'Design', agent: 'DataModeler', deps: ['architecture'], dur: 1600 },
  { id: 'security-review', stage: 'Security', agent: 'SecurityReviewer', deps: ['api-design', 'data-model'], dur: 1700 },
  { id: 'implement-api', stage: 'Build', agent: 'ApiCoder', deps: ['api-design'], dur: 2800 },
  { id: 'implement-storage', stage: 'Build', agent: 'StorageCoder', deps: ['data-model'], dur: 2400 },
  { id: 'static-analysis', stage: 'Quality', agent: 'StaticAnalyzer', deps: ['implement-api', 'implement-storage'], dur: 1400 },
  { id: 'docs', stage: 'Docs', agent: 'DocWriter', deps: ['api-design'], dur: 1500 },
  { id: 'unit-tests', stage: 'Test', agent: 'UnitTester', deps: ['implement-api', 'implement-storage'], dur: 1800, failFirst: 'AssertionError: expected 302 but got 200 in RedirectTests.Returns302' },
  { id: 'integration-tests', stage: 'Test', agent: 'IntegrationTester', deps: ['unit-tests'], dur: 2200, rollbackOnce: 'Container startup timeout; rolled back to last green snapshot and used in-memory fallback' },
  {
    id: 'code-review', stage: 'Review', agent: 'CodeReviewer', deps: ['unit-tests', 'static-analysis'], dur: 1600,
    approval: { when: 'after', risk: 'medium', reason: 'Reviewer findings must be signed off by a human before release.' },
  },
  { id: 'release-notes', stage: 'Docs', agent: 'ReleaseNotesWriter', deps: ['docs', 'code-review'], dur: 1200 },
  {
    id: 'deploy-staging', stage: 'Deploy', agent: 'DeployAgent', deps: ['integration-tests', 'code-review', 'security-review'], dur: 2000,
    approval: { when: 'before', risk: 'high', reason: 'Deploying to a shared environment changes live infrastructure.' },
  },
  { id: 'smoke-tests', stage: 'Verify', agent: 'SmokeTester', deps: ['deploy-staging'], dur: 1500 },
  { id: 'final-report', stage: 'Report', agent: 'SummaryAgent', deps: ['smoke-tests', 'release-notes'], dur: 1000 },
];

const LINEAGE_TEXT = {
  architecture: {
    title: 'Layered minimal-API service over a key-value store',
    rationale: 'A single stateless HTTP service with a pluggable store keeps the redirect hot path to one lookup and scales horizontally.',
    alternatives: ['Serverless functions + managed KV', 'Event-sourced CQRS', 'Monolith with embedded SQLite'],
    tradeoff: 'Gives up cheap scale-to-zero in exchange for predictable p95 latency and simpler local development.',
  },
  'api-design': {
    title: 'REST resource model with 302 redirects',
    rationale: 'POST /links and GET /{code} map directly to the requirement; 302 keeps analytics possible later.',
    alternatives: ['GraphQL mutation', '301 permanent redirects'],
    tradeoff: '302 costs a little extra client latency versus 301 caching but preserves click counting.',
  },
  'data-model': {
    title: 'Single links table keyed by short code',
    rationale: 'Primary-key lookup on code is O(1); uniqueness is enforced by the database, not application logic.',
    alternatives: ['Hash-sharded tables', 'Document store with TTL'],
    tradeoff: 'A single table limits write throughput eventually; sharding can be added behind the repository interface.',
  },
  'security-review': {
    title: 'Allow-list URL schemes and rate limit creation',
    rationale: 'Open redirects and javascript: URLs are the dominant abuse vector for shorteners.',
    alternatives: ['Block-list known bad domains', 'Require authentication for creation'],
    tradeoff: 'Allow-listing may reject exotic but valid schemes; considered acceptable.',
  },
  'implement-api': {
    title: 'Base62 codes from a counter with random salt',
    rationale: 'Counter-derived codes are collision-free; a salt prevents sequential enumeration.',
    alternatives: ['Random 7-char codes with retry on collision', 'Hash of the URL'],
    tradeoff: 'Needs a counter service; avoids birthday-collision retries on the write path.',
  },
  'integration-tests': {
    title: 'Run against an in-memory fallback when containers fail',
    rationale: 'A rollback to the last green snapshot plus fallback keeps the pipeline deterministic.',
    alternatives: ['Fail the run immediately', 'Skip integration tests'],
    tradeoff: 'In-memory store is less faithful than the real database; flagged in the summary.',
  },
  'deploy-staging': {
    title: 'Blue/green deploy to staging behind approval',
    rationale: 'A human gate is required for any shared-environment change; blue/green gives instant rollback.',
    alternatives: ['Rolling update', 'Deploy straight to production'],
    tradeoff: 'Doubles transient capacity for the duration of the deploy.',
  },
};

// ---------------------------------------------------------------- helpers
const runs = new Map();
const nowIso = () => new Date().toISOString();
const sha = (s) => crypto.createHash('sha256').update(s).digest('hex');
const ZERO = '0'.repeat(64);

function audit(run, type, nodeId, actor, message, data = {}) {
  const prev = run.audit.length ? run.audit[run.audit.length - 1].hash : ZERO;
  const seq = run.audit.length + 1;
  const ts = nowIso();
  const hash = sha([prev, seq, ts, type, nodeId ?? '', actor, message, JSON.stringify(data)].join('|'));
  run.audit.push({ seq, ts, type, nodeId, actor, message, data, prevHash: prev, hash });
}

function artifactName(nodeId) {
  return `artifact:${nodeId}`;
}

function putArtifact(run, node, def) {
  const name = artifactName(node.id);
  const prev = run.artifacts.get(name);
  const content = {
    node: node.id,
    stage: node.stage,
    producedBy: node.agent,
    version: (prev?.version ?? 0) + 1,
    summary: `${node.agent} output for ${node.id}`,
    reads: node.reads,
    notes: def.failFirst ? ['Recovered after a failed first attempt'] : [],
    generatedAt: nowIso(),
  };
  const body = JSON.stringify(content);
  run.artifacts.set(name, { name, version: content.version, hash: sha(body), bytes: Buffer.byteLength(body), body });
}

function makeNode(def) {
  return {
    id: def.id, stage: def.stage, agent: def.agent, deps: [...def.deps], status: 'Pending',
    attempts: 0, retries: 0, rollbacks: 0, usedFallback: false,
    startedAt: null, endedAt: null, durationMs: null, error: null,
    reads: def.deps.map(artifactName), expandedBy: null,
    approval: def.approval ? { ...def.approval } : null,
    // internal
    _def: def, _t0: 0, _finishAt: 0, _beforeOk: false, _afterOk: false, _failedAt: null, _staleUntil: 0,
  };
}

function createRun(scenario, autoApprove) {
  const id = crypto.randomUUID();
  const defs = NODE_DEFS.filter((d) => !d.only || d.only.includes(scenario.mode)).map((d) => ({ ...d, deps: [...d.deps] }));
  const have = new Set(defs.map((d) => d.id));
  for (const d of defs) d.deps = d.deps.filter((x) => have.has(x));
  // brownfield: architecture waits for impact-analysis so the replan lands before downstream work starts
  if (scenario.mode === 'brownfield') defs.find((d) => d.id === 'architecture').deps.push('impact-analysis');
  if (scenario.mode === 'ambiguous') {
    defs.find((d) => d.id === 'requirements').clarification = [
      { id: 'q1', question: 'What does "enterprise ready" mean here: SSO/SAML, audit logging, SLAs, or all of them?' },
      { id: 'q2', question: 'What peak redirect throughput (requests/second) must the service sustain?' },
      { id: 'q3', question: 'Which data-residency or compliance regimes apply (GDPR, SOC 2, none)?' },
    ];
  }
  const run = {
    id, scenario, autoApprove: !!autoApprove,
    status: 'Running', stopReason: null, startedAt: nowIso(), endedAt: null,
    nodes: defs.map(makeNode),
    audit: [], lineage: [], artifacts: new Map(), replans: [], pending: [], summary: null,
    policyViolations: 0, approvalsRequested: 0, approvalsGranted: 0, approvalsRejected: 0, recoveries: [],
    replanned: false,
  };
  audit(run, 'run.started', null, 'orchestrator', `Run started for scenario "${scenario.title}"`, { scenarioId: scenario.id, autoApprove: run.autoApprove });
  audit(run, 'plan.created', null, 'planner', `Planned ${run.nodes.length} nodes`, { nodes: run.nodes.map((n) => n.id) });
  runs.set(id, run);
  return run;
}

function requestApproval(run, node, when) {
  const def = node._def;
  const clarification = when === 'before' && def.clarification;
  const ap = {
    id: crypto.randomUUID(), nodeId: node.id, kind: clarification ? 'clarification' : 'approval', when,
    risk: clarification ? 'medium' : node.approval.risk,
    reason: clarification ? 'The requirement is ambiguous; the analyst needs answers before planning can continue.' : node.approval.reason,
    summary: clarification
      ? `Requirement: "${run.scenario.requirementText}"\n\nThe following questions must be answered.`
      : `Node: ${node.id} (${node.agent})\nStage: ${node.stage}\nGate: ${when} execution\nDepends on: ${node.deps.join(', ') || '-'}\n\nProposed action:\n  ${when === 'before' ? 'Run' : 'Accept the output of'} ${node.agent} for ${node.id}.`,
    questions: clarification ? def.clarification : [],
    requestedAt: nowIso(),
  };
  run.approvalsRequested++;
  node.status = 'AwaitingApproval';
  audit(run, 'approval.requested', node.id, 'orchestrator', `${ap.kind} requested ${when} ${node.id}`, { approvalId: ap.id, risk: ap.risk, kind: ap.kind });
  if (run.autoApprove) {
    run.approvalsGranted++;
    audit(run, 'approval.auto_granted', node.id, 'auto-approve', `Auto-approved ${ap.kind} for ${node.id}`, { approvalId: ap.id });
    applyGrant(run, node, when, clarification ? Object.fromEntries(def.clarification.map((q) => [q.id, '(auto) use sensible defaults'])) : null);
    return;
  }
  run.pending.push(ap);
}

function applyGrant(run, node, when, answers) {
  if (answers) {
    const name = 'clarifications:answers';
    const body = JSON.stringify({ answers, answeredAt: nowIso() });
    run.artifacts.set(name, { name, version: 1, hash: sha(body), bytes: Buffer.byteLength(body), body });
    node.reads = [...node.reads, name];
  }
  if (when === 'before') {
    node._beforeOk = true;
    node.status = 'Pending';
  } else {
    node._afterOk = true;
    complete(run, node);
  }
}

function startNode(run, node) {
  node.status = 'Running';
  node.attempts = Math.max(1, node.attempts);
  node._t0 = node._t0 || Date.now();
  node.startedAt = node.startedAt || nowIso();
  node._finishAt = Date.now() + node._def.dur;
  audit(run, 'node.started', node.id, node.agent, `Started ${node.id}`, { attempt: node.attempts });
}

function complete(run, node) {
  node.status = 'Done';
  node.endedAt = nowIso();
  node.durationMs = Date.now() - node._t0;
  node.error = null;
  putArtifact(run, node, node._def);
  audit(run, 'node.completed', node.id, node.agent, `Completed ${node.id} in ${node.durationMs} ms`, { attempts: node.attempts, durationMs: node.durationMs });
  if (node._failedAt) {
    run.recoveries.push(Date.now() - node._failedAt);
    node._failedAt = null;
  }
  const lt = LINEAGE_TEXT[node.id];
  if (lt) {
    run.lineage.push({
      id: crypto.randomUUID(), nodeId: node.id, ...lt,
      basedOn: node.reads.map((r) => ({ artifact: r, hash: run.artifacts.get(r)?.hash ?? ZERO })),
      at: nowIso(),
    });
  }
  if (node.id === 'security-review' && run.scenario.mode === 'brownfield') {
    run.policyViolations++;
    audit(run, 'policy.violation', node.id, 'policy-engine', 'Policy violation: logging of full referrer URL (PII) blocked and rewritten', { policy: 'pii-logging', action: 'rewrite' });
  }
  if (node.id === 'impact-analysis' && !run.replanned) replan(run, node);
}

function replan(run, trigger) {
  run.replanned = true;
  const mk = (def) => {
    const n = makeNode(def);
    n.expandedBy = trigger.id;
    return n;
  };
  const mig = mk({ id: 'migration-plan', stage: 'Design', agent: 'MigrationPlanner', deps: ['impact-analysis'], dur: 1600 });
  const compat = mk({ id: 'compat-tests', stage: 'Test', agent: 'CompatibilityTester', deps: ['migration-plan', 'implement-api'], dur: 1800 });
  run.nodes.push(mig, compat);
  const stale = [];
  for (const id of ['implement-api', 'implement-storage']) {
    const n = run.nodes.find((x) => x.id === id);
    if (n && n.status === 'Pending') {
      if (id === 'implement-api') n.deps.push('migration-plan');
      n.reads = n.deps.map(artifactName);
      n.status = 'Stale';
      n._staleUntil = Date.now() + 2500;
      stale.push(id);
    }
  }
  const deploy = run.nodes.find((x) => x.id === 'deploy-staging');
  if (deploy) {
    deploy.deps.push('compat-tests');
    deploy.reads = deploy.deps.map(artifactName);
  }
  const reason = 'Impact analysis found legacy clients depending on the current schema: added a reversible migration plan and compatibility tests.';
  run.replans.push({ at: nowIso(), reason, staleNodes: stale });
  audit(run, 'run.replanned', trigger.id, 'planner', reason, { added: ['migration-plan', 'compat-tests'], stale });
}

function failNodeOnce(run, node) {
  node.retries++;
  node.attempts++;
  node._failedAt = Date.now();
  node.error = node._def.failFirst;
  audit(run, 'node.failed', node.id, node.agent, `Attempt ${node.attempts - 1} failed: ${node._def.failFirst}`, { attempt: node.attempts - 1 });
  audit(run, 'node.retry', node.id, 'orchestrator', `Retrying ${node.id} (attempt ${node.attempts})`, { attempt: node.attempts });
  node._def = { ...node._def, failFirst: null };
  node._finishAt = Date.now() + node._def.dur;
}

function rollbackNodeOnce(run, node) {
  node.rollbacks++;
  node.attempts++;
  node.usedFallback = true;
  node._failedAt = Date.now();
  node.error = node._def.rollbackOnce;
  audit(run, 'node.failed', node.id, node.agent, `Attempt failed: ${node._def.rollbackOnce}`, { attempt: node.attempts - 1 });
  audit(run, 'node.rollback', node.id, 'orchestrator', `Rolled back ${node.id} to last green snapshot`, {});
  audit(run, 'node.fallback', node.id, 'orchestrator', `Using fallback strategy for ${node.id}`, {});
  node._def = { ...node._def, rollbackOnce: null };
  node._finishAt = Date.now() + node._def.dur;
}

function tick() {
  const t = Date.now();
  for (const run of runs.values()) {
    if (run.status !== 'Running' && run.status !== 'AwaitingHuman') continue;
    const doneIds = new Set(run.nodes.filter((n) => n.status === 'Done').map((n) => n.id));
    for (const node of run.nodes) {
      if (run.status === 'Rejected') break;
      if (node.status === 'Stale' && t >= node._staleUntil) node.status = 'Pending';
      if (node.status === 'Running' && t >= node._finishAt) {
        if (node._def.failFirst) failNodeOnce(run, node);
        else if (node._def.rollbackOnce) rollbackNodeOnce(run, node);
        else if (node.approval?.when === 'after' && !node._afterOk) requestApproval(run, node, 'after');
        else complete(run, node);
      } else if (node.status === 'Pending' && node.deps.every((d) => doneIds.has(d))) {
        const needsBefore = (node.approval?.when === 'before' || node._def.clarification) && !node._beforeOk;
        if (needsBefore) requestApproval(run, node, 'before');
        else startNode(run, node);
      }
    }
    if (run.status === 'Rejected') continue;
    if (run.nodes.every((n) => n.status === 'Done')) finish(run);
    else run.status = run.pending.length ? 'AwaitingHuman' : 'Running';
  }
}

function finish(run) {
  run.status = 'Completed';
  run.endedAt = nowIso();
  audit(run, 'run.completed', null, 'orchestrator', 'All nodes completed', {});
  run.summary = buildSummary(run);
}

function buildSummary(run) {
  const m = computeMetrics(run);
  const rows = run.nodes.map((n) => `| ${n.id} | ${n.stage} | ${n.agent} | ${n.attempts} | ${n.durationMs ?? '-'} ms |`).join('\n');
  return `# Engineering summary: ${run.scenario.title}

**Outcome:** ${run.status} - ${m.nodesDone}/${m.nodesTotal} nodes done in ${(m.endToEndMs / 1000).toFixed(1)} s.

## Requirement

> ${run.scenario.requirementText.replace(/\n/g, ' ')}

## Highlights

- **${m.retries}** retries, **${m.rollbacks}** rollbacks, **${m.fallbacks}** fallbacks
- **${m.replans}** replans, **${m.policyViolations}** policy violations
- Approvals: ${m.approvalsGranted} granted / ${m.approvalsRequested} requested
- Critical path: \`${m.criticalPathMs} ms\`

## Node results

| Node | Stage | Agent | Attempts | Duration |
|------|-------|-------|----------|----------|
${rows}

## Verification

\`\`\`
unit-tests         PASS (after 1 retry)
integration-tests  PASS (in-memory fallback)
smoke-tests        PASS
\`\`\`

Audit chain head: \`${run.audit[run.audit.length - 1].hash}\`
`;
}

function layersOf(run) {
  const byId = new Map(run.nodes.map((n) => [n.id, n]));
  const depth = new Map();
  const d = (id, stack = new Set()) => {
    if (depth.has(id)) return depth.get(id);
    if (stack.has(id)) return 0;
    stack.add(id);
    const n = byId.get(id);
    const v = n.deps.filter((x) => byId.has(x)).length ? 1 + Math.max(...n.deps.filter((x) => byId.has(x)).map((x) => d(x, stack))) : 0;
    depth.set(id, v);
    return v;
  };
  const layers = [];
  for (const n of run.nodes) (layers[d(n.id)] ??= []).push(n.id);
  return layers.filter(Boolean);
}

function computeMetrics(run) {
  const nodes = run.nodes;
  const done = nodes.filter((n) => n.status === 'Done').length;
  const totalAttempts = nodes.reduce((a, n) => a + n.attempts, 0);
  const retries = nodes.reduce((a, n) => a + n.retries, 0);
  const rollbacks = nodes.reduce((a, n) => a + n.rollbacks, 0);
  const perStage = {};
  for (const n of nodes) if (n.durationMs) perStage[n.stage] = (perStage[n.stage] ?? 0) + n.durationMs;
  // critical path over done-node durations
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const memo = new Map();
  const cp = (id) => {
    if (memo.has(id)) return memo.get(id);
    const n = byId.get(id);
    const v = (n.durationMs ?? 0) + Math.max(0, ...n.deps.filter((x) => byId.has(x)).map(cp));
    memo.set(id, v);
    return v;
  };
  const criticalPathMs = Math.max(0, ...nodes.map((n) => cp(n.id)));
  const end = run.endedAt ? Date.parse(run.endedAt) : Date.now();
  const total = nodes.length;
  return {
    successRate: total ? done / total : 0,
    attemptSuccessRate: totalAttempts ? (totalAttempts - retries - rollbacks) / totalAttempts : 1,
    totalAttempts, retries, rollbacks,
    fallbacks: nodes.filter((n) => n.usedFallback).length,
    replans: run.replans.length,
    policyViolations: run.policyViolations,
    approvalsRequested: run.approvalsRequested, approvalsGranted: run.approvalsGranted, approvalsRejected: run.approvalsRejected,
    mttrMs: run.recoveries.length ? Math.round(run.recoveries.reduce((a, b) => a + b, 0) / run.recoveries.length) : null,
    endToEndMs: end - Date.parse(run.startedAt),
    criticalPathMs, nodesDone: done, nodesTotal: total, perStage,
  };
}

function runView(run) {
  return {
    id: run.id, scenarioId: run.scenario.id, title: run.scenario.title, mode: run.scenario.mode,
    status: run.status, stopReason: run.stopReason, startedAt: run.startedAt, endedAt: run.endedAt,
    nodes: run.nodes.map((n) => ({
      id: n.id, stage: n.stage, agent: n.agent, deps: n.deps, status: n.status,
      attempts: n.attempts, retries: n.retries, rollbacks: n.rollbacks, usedFallback: n.usedFallback,
      startedAt: n.startedAt, endedAt: n.endedAt,
      durationMs: n.durationMs ?? (n.status === 'Running' || n.status === 'AwaitingApproval' ? Date.now() - n._t0 : null),
      error: n.error, reads: n.reads, expandedBy: n.expandedBy, approval: n.approval,
    })),
    layers: layersOf(run),
    pendingApprovals: run.pending,
    metrics: computeMetrics(run),
    replans: run.replans,
  };
}

function runSummary(run) {
  return { id: run.id, scenarioId: run.scenario.id, title: run.scenario.title, mode: run.scenario.mode, status: run.status, startedAt: run.startedAt, endedAt: run.endedAt };
}

function stopRun(run) {
  if (run.status !== 'Running' && run.status !== 'AwaitingHuman') return false;
  run.status = 'SafeStopped';
  run.stopReason = 'Operator invoked the safe-stop kill switch';
  run.endedAt = nowIso();
  run.pending = [];
  for (const n of run.nodes) if (['Running', 'AwaitingApproval', 'Pending', 'Stale'].includes(n.status)) n.status = 'Cancelled';
  audit(run, 'run.stopped', null, 'operator', run.stopReason, {});
  return true;
}

function resumeRun(run) {
  if (run.status !== 'SafeStopped') return false;
  run.status = 'Running';
  run.stopReason = null;
  run.endedAt = null;
  for (const n of run.nodes) if (n.status === 'Cancelled') { n.status = 'Pending'; n._t0 = 0; n.startedAt = null; }
  audit(run, 'run.resumed', null, 'operator', 'Run resumed from safe-stop', {});
  return true;
}

function rejectRun(run, node, comment) {
  run.status = 'Rejected';
  run.endedAt = nowIso();
  run.pending = [];
  run.stopReason = `Approval rejected at ${node.id}${comment ? `: ${comment}` : ''}`;
  node.status = 'Rejected';
  node.error = run.stopReason;
  const blocked = new Set([node.id]);
  let grew = true;
  while (grew) {
    grew = false;
    for (const n of run.nodes) if (!blocked.has(n.id) && n.deps.some((d) => blocked.has(d))) { blocked.add(n.id); grew = true; }
  }
  for (const n of run.nodes) {
    if (n.status === 'Done' || n.id === node.id) continue;
    n.status = blocked.has(n.id) ? 'Blocked' : n.status === 'Running' ? 'Cancelled' : n.status === 'Pending' ? 'Skipped' : n.status;
  }
  audit(run, 'run.rejected', node.id, 'orchestrator', run.stopReason, {});
}

// ---------------------------------------------------------------- http
function send(res, status, body, type = 'application/json') {
  const payload = type === 'application/json' && typeof body !== 'string' ? JSON.stringify(body) : body;
  res.writeHead(status, { 'content-type': `${type}; charset=utf-8`, 'cache-control': 'no-store' });
  res.end(payload);
}

function readBody(req) {
  return new Promise((resolve) => {
    let data = '';
    req.on('data', (c) => (data += c));
    req.on('end', () => {
      try { resolve(data ? JSON.parse(data) : {}); } catch { resolve(null); }
    });
  });
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`);
  const path = url.pathname.replace(/\/+$/, '');
  const m = req.method;
  let g;
  try {
    if (m === 'GET' && path === '/api/scenarios') return send(res, 200, SCENARIOS);
    if (m === 'GET' && path === '/api/runs') return send(res, 200, [...runs.values()].map(runSummary));
    if (m === 'POST' && path === '/api/runs') {
      const body = await readBody(req);
      const sc = SCENARIOS.find((s) => s.id === body?.scenarioId);
      if (!sc) return send(res, 400, { error: `Unknown scenarioId "${body?.scenarioId}"` });
      const run = createRun(sc, body.autoApprove);
      return send(res, 202, { runId: run.id });
    }
    if ((g = /^\/api\/runs\/([^/]+)(?:\/(.*))?$/.exec(path))) {
      const run = runs.get(decodeURIComponent(g[1]));
      if (!run) return send(res, 404, { error: 'Run not found' });
      const sub = g[2] ?? '';
      if (m === 'GET' && sub === '') return send(res, 200, runView(run));
      if (m === 'GET' && sub === 'audit') {
        let valid = true;
        run.audit.forEach((e, i) => { if (e.prevHash !== (i ? run.audit[i - 1].hash : ZERO)) valid = false; });
        return send(res, 200, { entries: run.audit, chainValid: valid, headHash: run.audit.length ? run.audit[run.audit.length - 1].hash : ZERO });
      }
      if (m === 'GET' && sub === 'lineage') return send(res, 200, run.lineage);
      if (m === 'GET' && sub === 'artifacts') return send(res, 200, [...run.artifacts.values()].map(({ name, version, hash, bytes }) => ({ name, version, hash, bytes })));
      if (m === 'GET' && sub.startsWith('artifacts/')) {
        const a = run.artifacts.get(decodeURIComponent(sub.slice('artifacts/'.length)));
        return a ? send(res, 200, a.body, 'application/json') : send(res, 404, { error: 'Artifact not found' });
      }
      if (m === 'GET' && sub === 'summary') return run.summary ? send(res, 200, run.summary, 'text/markdown') : send(res, 404, { error: 'Summary not produced yet' });
      if (m === 'POST' && sub === 'stop') return stopRun(run) ? send(res, 202, { ok: true }) : send(res, 409, { error: `Cannot stop a run in status ${run.status}` });
      if (m === 'POST' && sub === 'resume') return resumeRun(run) ? send(res, 202, { ok: true }) : send(res, 409, { error: `Cannot resume a run in status ${run.status}` });
      if (m === 'POST' && (g = /^approvals\/([^/]+)$/.exec(sub))) {
        const body = await readBody(req);
        const idx = run.pending.findIndex((p) => p.id === decodeURIComponent(g[1]));
        if (idx < 0) return send(res, 404, { error: 'Approval not found (already decided?)' });
        const ap = run.pending[idx];
        const node = run.nodes.find((n) => n.id === ap.nodeId);
        if (ap.kind === 'clarification' && body?.approved) {
          const missing = ap.questions.filter((q) => !body.answers?.[q.id]?.trim());
          if (missing.length) return send(res, 400, { error: `Missing answers for: ${missing.map((q) => q.id).join(', ')}` });
        }
        run.pending.splice(idx, 1);
        if (body?.approved) {
          run.approvalsGranted++;
          audit(run, 'approval.granted', node.id, body.approver || 'human', `${ap.kind} granted for ${node.id}${body.comment ? `: ${body.comment}` : ''}`, { approvalId: ap.id, comment: body.comment ?? null, answers: body.answers ?? null });
          applyGrant(run, node, ap.when, ap.kind === 'clarification' ? body.answers : null);
        } else {
          run.approvalsRejected++;
          audit(run, 'approval.rejected', node.id, body?.approver || 'human', `${ap.kind} rejected for ${node.id}${body?.comment ? `: ${body.comment}` : ''}`, { approvalId: ap.id, comment: body?.comment ?? null });
          rejectRun(run, node, body?.comment);
        }
        return send(res, 200, { ok: true });
      }
    }
    return send(res, 404, { error: `No route for ${m} ${path}` });
  } catch (e) {
    console.error(e);
    return send(res, 500, { error: String(e?.message ?? e) });
  }
});

setInterval(tick, TICK_MS);
server.listen(PORT, () => console.log(`Mock Agentic API listening on http://localhost:${PORT}`));
