import { Component, computed, input } from '@angular/core';
import { RunMetrics } from '../models';
import { fmtMs, fmtPct } from '../util/format';

interface Tile {
  label: string;
  value: string;
  sub?: string;
  tone?: 'ok' | 'warn' | 'bad' | '';
}

@Component({
  selector: 'app-metrics',
  standalone: true,
  template: `
    <div class="tiles">
      @for (t of tiles(); track t.label) {
        <div class="tile {{ t.tone }}">
          <div class="label">{{ t.label }}</div>
          <div class="value">{{ t.value }}</div>
          @if (t.sub) { <div class="sub">{{ t.sub }}</div> }
        </div>
      }
    </div>

    <div class="card chart">
      <div class="card-head"><h3>Latency per stage</h3><span class="muted small">sum of node durations by stage</span></div>
      @if (stages().length === 0) {
        <div class="muted small">No stage timings yet.</div>
      } @else {
        <div class="bars">
          @for (s of stages(); track s.stage) {
            <div class="bar-row">
              <div class="bar-label" [title]="s.stage">{{ s.stage }}</div>
              <div class="bar-track"><div class="bar-fill" [style.width.%]="s.pct"></div></div>
              <div class="bar-val mono">{{ s.text }}</div>
            </div>
          }
        </div>
      }
    </div>
  `,
  styles: [
    `
      :host { display: block; }
      .tiles { display: grid; grid-template-columns: repeat(auto-fill, minmax(128px, 1fr)); gap: 10px; margin-bottom: 14px; }
      .tile { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 8px 12px; position: relative; overflow: hidden; }
      .tile::before { content: ''; position: absolute; left: 0; top: 0; bottom: 0; width: 3px; background: var(--line-2); }
      .tile.ok::before { background: var(--ok); } .tile.warn::before { background: var(--warn); } .tile.bad::before { background: var(--bad); }
      .label { color: var(--muted); font-size: 11.5px; text-transform: uppercase; letter-spacing: .06em; }
      .value { font-size: 1.3rem; font-weight: 700; line-height: 1.25; font-variant-numeric: tabular-nums; }
      .sub { color: var(--muted); font-size: 11.5px; }
      .bars { display: flex; flex-direction: column; gap: 7px; }
      .bar-row { display: grid; grid-template-columns: minmax(80px, 150px) 1fr 80px; gap: 10px; align-items: center; }
      .bar-label { font-size: 12.5px; text-align: right; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--muted); }
      .bar-track { background: #0c1428; border-radius: 6px; height: 14px; overflow: hidden; border: 1px solid var(--line); }
      .bar-fill { height: 100%; background: linear-gradient(90deg, #4c9bff, #8b7bff); border-radius: 5px; transition: width .4s ease; min-width: 2px; }
      .bar-val { font-size: 12px; }
    `,
  ],
})
export class MetricsComponent {
  readonly metrics = input.required<RunMetrics>();

  protected readonly tiles = computed<Tile[]>(() => {
    const m = this.metrics();
    const rateTone = (v: number): Tile['tone'] => {
      const p = v <= 1 ? v * 100 : v;
      return p >= 90 ? 'ok' : p >= 60 ? 'warn' : 'bad';
    };
    return [
      { label: 'Nodes done', value: `${m.nodesDone}/${m.nodesTotal}`, tone: m.nodesTotal > 0 && m.nodesDone === m.nodesTotal ? 'ok' : '' },
      { label: 'Success rate', value: fmtPct(m.successRate), sub: 'nodes done / total', tone: (m.successRate <= 1 ? m.successRate * 100 : m.successRate) >= 90 ? 'ok' : '' },
      { label: 'Attempt success', value: fmtPct(m.attemptSuccessRate), sub: `${m.totalAttempts} attempts`, tone: rateTone(m.attemptSuccessRate) },
      { label: 'Retries', value: String(m.retries), tone: m.retries > 0 ? 'warn' : '' },
      { label: 'Rollbacks', value: String(m.rollbacks), tone: m.rollbacks > 0 ? 'warn' : '' },
      { label: 'Fallbacks', value: String(m.fallbacks), tone: m.fallbacks > 0 ? 'warn' : '' },
      { label: 'Replans', value: String(m.replans), tone: '' },
      { label: 'Policy violations', value: String(m.policyViolations), tone: m.policyViolations > 0 ? 'bad' : 'ok' },
      {
        label: 'Approvals',
        value: `${m.approvalsGranted}/${m.approvalsRequested}`,
        sub: m.approvalsRejected > 0 ? `${m.approvalsRejected} rejected` : 'granted / requested',
        tone: m.approvalsRejected > 0 ? 'bad' : '',
      },
      { label: 'MTTR', value: fmtMs(m.mttrMs), sub: 'mean time to recover', tone: '' },
      { label: 'End-to-end', value: fmtMs(m.endToEndMs), tone: '' },
      { label: 'Critical path', value: fmtMs(m.criticalPathMs), tone: '' },
    ];
  });

  protected readonly stages = computed(() => {
    const entries = Object.entries(this.metrics().perStage ?? {});
    const max = Math.max(1, ...entries.map(([, v]) => v));
    return entries
      .sort((a, b) => b[1] - a[1])
      .map(([stage, v]) => ({ stage, pct: Math.max(1, (v / max) * 100), text: fmtMs(v) }));
  });
}
