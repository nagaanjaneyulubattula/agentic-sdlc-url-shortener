import { Component, computed, input, output } from '@angular/core';
import { DagNode } from '../models';
import { fmtDateTime, fmtMs } from '../util/format';

@Component({
  selector: 'app-node-panel',
  standalone: true,
  template: `
    @if (node(); as n) {
      <div class="panel-head">
        <div>
          <div class="small muted upper">{{ n.stage }}</div>
          <h3>{{ n.agent }}</h3>
          <div class="mono small muted">{{ n.id }}</div>
        </div>
        <button class="x" (click)="close.emit()" aria-label="Close details">&times;</button>
      </div>
      <div class="row" style="margin:8px 0 12px">
        <span class="chip dot s-{{ n.status }}" [class.pulse]="n.status === 'Running'">{{ n.status }}</span>
        @if (n.usedFallback) { <span class="chip" style="--c:#c084fc">fallback used</span> }
        @if (n.expandedBy) { <span class="chip" style="--c:#67e8f9">added by replan</span> }
      </div>

      @if (n.error) {
        <div class="err-box"><strong>Error</strong><pre class="pre">{{ n.error }}</pre></div>
      }

      @if (n.approval; as a) {
        <div class="gate-box">
          <div class="row" style="gap:8px">
            <strong>Approval gate</strong>
            <span class="chip">{{ a.when }}</span>
            <span class="chip risk-{{ a.risk.toLowerCase() }}">{{ a.risk }} risk</span>
          </div>
          <div class="small" style="margin-top:6px">{{ a.reason }}</div>
        </div>
      }

      <dl class="kv" style="margin-top:12px">
        <dt>Attempts</dt><dd>{{ n.attempts }}</dd>
        <dt>Retries</dt><dd>{{ n.retries }}</dd>
        <dt>Rollbacks</dt><dd>{{ n.rollbacks }}</dd>
        <dt>Duration</dt><dd>{{ ms(n.durationMs) }}</dd>
        <dt>Started</dt><dd>{{ dt(n.startedAt) }}</dd>
        <dt>Ended</dt><dd>{{ dt(n.endedAt) }}</dd>
        @if (n.expandedBy) { <dt>Expanded by</dt><dd class="mono">{{ n.expandedBy }}</dd> }
      </dl>

      <h4 class="sec">Depends on</h4>
      @if (n.deps.length === 0) { <div class="muted small">No dependencies (root node)</div> }
      <div class="chips">
        @for (d of n.deps; track d) {
          <button class="link-chip mono" (click)="goto.emit(d)">{{ d }}</button>
        }
      </div>

      <h4 class="sec">Required by</h4>
      @if (dependents().length === 0) { <div class="muted small">Nothing downstream</div> }
      <div class="chips">
        @for (d of dependents(); track d) {
          <button class="link-chip mono" (click)="goto.emit(d)">{{ d }}</button>
        }
      </div>

      <h4 class="sec">Reads (artifacts)</h4>
      @if (n.reads.length === 0) { <div class="muted small">None</div> }
      <ul class="reads">
        @for (r of n.reads; track r) { <li class="mono small">{{ r }}</li> }
      </ul>
    } @else {
      <div class="empty">Click a node in the graph to inspect it.</div>
    }
  `,
  styles: [
    `
      :host { display: block; }
      .panel-head { display: flex; justify-content: space-between; gap: 8px; }
      .panel-head h3 { margin: 2px 0; font-size: 1.1rem; }
      .upper { text-transform: uppercase; letter-spacing: .07em; }
      .x { padding: 0 9px; font-size: 20px; line-height: 26px; height: 30px; }
      .sec { margin: 16px 0 6px; font-size: 11.5px; text-transform: uppercase; letter-spacing: .06em; color: var(--muted); }
      .chips { display: flex; flex-wrap: wrap; gap: 6px; }
      .link-chip { padding: 2px 8px; font-size: 11.5px; border-radius: 6px; }
      .reads { margin: 0; padding-left: 16px; }
      .err-box { border: 1px solid #7a2a38; background: #2d1219; border-radius: 10px; padding: 8px 10px; margin-bottom: 8px; color: #ffc9c9; }
      .err-box pre { margin-top: 4px; font-size: 12px; }
      .gate-box { border: 1px solid #7a5a14; background: #2a2008; border-radius: 10px; padding: 8px 10px; }
    `,
  ],
})
export class NodePanelComponent {
  readonly node = input<DagNode | null>(null);
  readonly allNodes = input<DagNode[]>([]);
  readonly close = output<void>();
  readonly goto = output<string>();

  protected readonly dependents = computed(() => {
    const n = this.node();
    return n ? this.allNodes().filter((x) => x.deps.includes(n.id)).map((x) => x.id) : [];
  });

  protected ms(v: number | null): string {
    return fmtMs(v);
  }
  protected dt(v: string | null): string {
    return fmtDateTime(v);
  }
}
