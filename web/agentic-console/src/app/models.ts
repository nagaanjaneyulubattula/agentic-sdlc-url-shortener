// Types mirroring the backend REST contract (JSON, camelCase).

export type ScenarioMode = 'greenfield' | 'brownfield' | 'ambiguous';

export type RunStatus =
  | 'Running'
  | 'AwaitingHuman'
  | 'Completed'
  | 'SafeStopped'
  | 'Rejected'
  | 'Failed';

export type NodeStatus =
  | 'Pending'
  | 'Running'
  | 'AwaitingApproval'
  | 'Done'
  | 'Failed'
  | 'Blocked'
  | 'Skipped'
  | 'Stale'
  | 'Cancelled'
  | 'Rejected';

export const TERMINAL_STATUSES: readonly RunStatus[] = ['Completed', 'SafeStopped', 'Rejected', 'Failed'];

export function isTerminal(status: RunStatus): boolean {
  return TERMINAL_STATUSES.includes(status);
}

export interface Scenario {
  id: string;
  title: string;
  mode: ScenarioMode;
  description: string;
  requirementText: string;
}

export interface RunSummary {
  id: string;
  scenarioId: string;
  title: string;
  mode: ScenarioMode;
  status: RunStatus;
  startedAt: string;
  endedAt: string | null;
}

export interface ApprovalSpec {
  when: 'before' | 'after';
  risk: string;
  reason: string;
}

export interface DagNode {
  id: string;
  stage: string;
  agent: string;
  deps: string[];
  status: NodeStatus;
  attempts: number;
  retries: number;
  rollbacks: number;
  usedFallback: boolean;
  startedAt: string | null;
  endedAt: string | null;
  durationMs: number | null;
  error: string | null;
  reads: string[];
  expandedBy: string | null;
  approval: ApprovalSpec | null;
}

export interface ApprovalQuestion {
  id: string;
  question: string;
}

export interface PendingApproval {
  id: string;
  nodeId: string;
  kind: 'approval' | 'clarification';
  when: 'before' | 'after';
  risk: string;
  reason: string;
  summary: string;
  questions: ApprovalQuestion[];
  requestedAt: string;
}

export interface RunMetrics {
  successRate: number;
  attemptSuccessRate: number;
  totalAttempts: number;
  retries: number;
  rollbacks: number;
  fallbacks: number;
  replans: number;
  policyViolations: number;
  approvalsRequested: number;
  approvalsGranted: number;
  approvalsRejected: number;
  mttrMs: number | null;
  endToEndMs: number;
  criticalPathMs: number;
  nodesDone: number;
  nodesTotal: number;
  perStage: Record<string, number>;
}

export interface Replan {
  at: string;
  reason: string;
  staleNodes: string[];
}

export interface RunView {
  id: string;
  scenarioId: string;
  title: string;
  mode: ScenarioMode;
  status: RunStatus;
  stopReason: string | null;
  startedAt: string;
  endedAt: string | null;
  nodes: DagNode[];
  layers: string[][];
  pendingApprovals: PendingApproval[];
  metrics: RunMetrics;
  replans: Replan[];
}

export interface AuditEntry {
  seq: number;
  ts: string;
  type: string;
  nodeId: string | null;
  actor: string;
  message: string;
  data: Record<string, unknown> | null;
  prevHash: string;
  hash: string;
}

export interface AuditView {
  entries: AuditEntry[];
  chainValid: boolean;
  headHash: string;
}

export interface LineageBasis {
  artifact: string;
  hash: string;
}

export interface LineageRecord {
  id: string;
  nodeId: string;
  title: string;
  rationale: string;
  alternatives: string[];
  tradeoff: string;
  basedOn: LineageBasis[];
  at: string;
}

export interface ArtifactInfo {
  name: string;
  version: number | string;
  hash: string;
  bytes: number;
}

export interface DecisionBody {
  approved: boolean;
  comment?: string;
  approver?: string;
  answers?: Record<string, string>;
}
