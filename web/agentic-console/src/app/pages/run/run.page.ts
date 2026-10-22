import { Component, OnDestroy, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { EMPTY, Subscription, catchError, exhaustMap, takeWhile, timer } from 'rxjs';
import { ApiService, describeError } from '../../api.service';
import { RunView, isTerminal } from '../../models';
import { fmtMs } from '../../util/format';
import { DagViewComponent } from '../../components/dag-view.component';
import { NodePanelComponent } from '../../components/node-panel.component';
import { ApprovalsPanelComponent } from '../../components/approvals-panel.component';
import { MetricsComponent } from '../../components/metrics.component';
import { AuditTabComponent } from '../../components/audit-tab.component';
import { LineageTabComponent } from '../../components/lineage-tab.component';
import { ArtifactsTabComponent } from '../../components/artifacts-tab.component';
import { SummaryTabComponent } from '../../components/summary-tab.component';
import { ReplansTabComponent } from '../../components/replans-tab.component';

type TabId = 'audit' | 'lineage' | 'artifacts' | 'summary' | 'replans';

@Component({
  selector: 'app-run-page',
  standalone: true,
  imports: [
    RouterLink,
    DagViewComponent,
    NodePanelComponent,
    ApprovalsPanelComponent,
    MetricsComponent,
    AuditTabComponent,
    LineageTabComponent,
    ArtifactsTabComponent,
    SummaryTabComponent,
    ReplansTabComponent,
  ],
  template: `
    @if (notFound()) {
      <div class="empty">
        Run <code>{{ id() }}</code> was not found. <a routerLink="/">Back to launcher</a>
      </div>
    } @else if (!run()) {
      <div class="empty">Loading run {{ id().slice(0, 8) }}...</div>
    } @else {
      @let r = run()!;
      <section class="status-banner s-{{ r.status }}" role="status" aria-live="polite">
        <div>
          <div class="row" style="gap:10px">
            <span class="chip dot big s-{{ r.status }}" [class.pulse]="r.status === 'Running'">{{ statusLabel(r.status) }}</span>
            <span class="chip mode-{{ r.mode }}">{{ r.mode }}</span>
            <span class="muted small mono">run {{ r.id.slice(0, 8) }}</span>
          </div>
          <h1 style="margin-top:8px">{{ r.title }}</h1>
          <div class="muted small">
            Elapsed {{ elapsed() }}
            @if (!terminal()) { - live, refreshing every 1s }
          </div>
          @if (r.stopReason) { <div class="reason"><strong>Reason:</strong> {{ r.stopReason }}</div> }
        </div>
        <span class="spacer"></span>
        <div class="row">
          @if (r.status === 'Running' || r.status === 'AwaitingHuman') {
            <button class="btn-warn" [disabled]="busy()" (click)="stop()" title="Kill switch: safely stops the run, leaving a resumable state">Stop (safe-stop)</button>
          }
          @if (r.status === 'SafeStopped') {
            <button class="btn-primary" [disabled]="busy()" (click)="resume()">Resume</button>
          }
          <a routerLink="/"><button>New run</button></a>
        </div>
      </section>

      @if (actionError(); as e) { <div class="banner banner-error" style="border-radius:10px;margin-bottom:12px">{{ e }}</div> }

      <div class="stack">
        @if (r.pendingApprovals.length > 0) {
          <app-approvals-panel
            [runId]="r.id"
            [approvals]="r.pendingApprovals"
            [nodes]="r.nodes"
            (changed)="refreshNow()"
          />
        }

        <section class="card">
          <div class="card-head">
            <h2>Execution graph</h2>
            <span class="muted small">{{ r.nodes.length }} nodes in {{ r.layers.length }} layers - click a node for details</span>
          </div>
          <div class="dag-layout" [class.with-panel]="!!selectedNode()">
            <app-dag-view [nodes]="r.nodes" [layers]="r.layers" [selectedId]="selectedNodeId()" (select)="selectedNodeId.set($event)" />
            @if (selectedNode()) {
              <aside class="side card">
                <app-node-panel
                  [node]="selectedNode()"
                  [allNodes]="r.nodes"
                  (close)="selectedNodeId.set(null)"
                  (goto)="selectedNodeId.set($event)"
                />
              </aside>
            }
          </div>
        </section>

        <app-metrics [metrics]="r.metrics" />

        <section class="card">
          <div class="tabs" role="tablist">
            @for (t of tabs; track t.id) {
              <button class="tab" role="tab" [class.active]="tab() === t.id" [attr.aria-selected]="tab() === t.id" (click)="tab.set(t.id)">
                {{ t.label }}@if (t.id === 'replans' && r.replans.length) { <span class="count">{{ r.replans.length }}</span> }
              </button>
            }
          </div>
          @switch (tab()) {
            @case ('audit') { <app-audit-tab [runId]="r.id" [stamp]="stamp()" /> }
            @case ('lineage') { <app-lineage-tab [runId]="r.id" [stamp]="stamp()" /> }
            @case ('artifacts') { <app-artifacts-tab [runId]="r.id" [stamp]="stamp()" /> }
            @case ('summary') { <app-summary-tab [runId]="r.id" [stamp]="stamp()" /> }
            @case ('replans') { <app-replans-tab [replans]="r.replans" (select)="selectedNodeId.set($event)" /> }
          }
        </section>
      </div>
    }
  `,
  styles: [
    `
      .chip.big { font-size: 13px; padding: 3px 12px; }
      .reason { margin-top: 8px; padding: 6px 10px; border-radius: 8px; background: rgba(0,0,0,.25); border: 1px solid var(--line-2); }
      .dag-layout { display: grid; grid-template-columns: minmax(0, 1fr); gap: 14px; align-items: start; }
      .dag-layout.with-panel { grid-template-columns: minmax(0, 1fr) 340px; }
      .side { background: var(--card-2); position: sticky; top: 64px; max-height: 80vh; overflow: auto; }
      @media (max-width: 1000px) { .dag-layout.with-panel { grid-template-columns: minmax(0, 1fr); } .side { position: static; } }
      .count { margin-left: 6px; background: var(--accent-2); color: #fff; border-radius: 999px; font-size: 11px; padding: 0 7px; }
    `,
  ],
})
export class RunPage implements OnDestroy {
  /** Bound from the route parameter (withComponentInputBinding). */
  readonly id = input.required<string>();

  private readonly api = inject(ApiService);

  protected readonly run = signal<RunView | null>(null);
  protected readonly notFound = signal(false);
  protected readonly selectedNodeId = signal<string | null>(null);
  protected readonly tab = signal<TabId>('audit');
  protected readonly busy = signal(false);
  protected readonly actionError = signal<string | null>(null);
  private readonly now = signal(Date.now());
  private sub?: Subscription;

  protected readonly tabs: { id: TabId; label: string }[] = [
    { id: 'audit', label: 'Audit trail' },
    { id: 'lineage', label: 'Decision lineage' },
    { id: 'artifacts', label: 'Artifacts' },
    { id: 'summary', label: 'Engineering summary' },
    { id: 'replans', label: 'Replans' },
  ];

  protected readonly terminal = computed(() => {
    const r = this.run();
    return r ? isTerminal(r.status) : false;
  });

  protected readonly selectedNode = computed(() => {
    const id = this.selectedNodeId();
    return id ? (this.run()?.nodes.find((n) => n.id === id) ?? null) : null;
  });

  /** Changes whenever something observable about the run changes; tabs reload on it. */
  protected readonly stamp = computed(() => {
    const r = this.run();
    if (!r) return '';
    const m = r.metrics;
    return [
      r.status,
      m.nodesDone,
      m.totalAttempts,
      m.retries,
      m.rollbacks,
      m.replans,
      m.approvalsRequested,
      m.approvalsGranted,
      m.approvalsRejected,
      r.pendingApprovals.length,
      r.nodes.map((n) => n.status.charAt(0) + n.attempts).join(''),
    ].join('|');
  });

  protected readonly elapsed = computed(() => {
    const r = this.run();
    if (!r) return '-';
    const start = new Date(r.startedAt).getTime();
    const end = r.endedAt ? new Date(r.endedAt).getTime() : this.now();
    return fmtMs(Math.max(0, end - start));
  });

  constructor() {
    effect(() => {
      const id = this.id();
      untracked(() => {
        this.run.set(null);
        this.notFound.set(false);
        this.selectedNodeId.set(null);
        this.actionError.set(null);
        this.startPolling(id);
      });
    });
  }

  ngOnDestroy(): void {
    this.sub?.unsubscribe();
  }

  /** Poll once a second; stops after the first terminal snapshot. */
  private startPolling(id: string): void {
    this.sub?.unsubscribe();
    this.sub = timer(0, 1000)
      .pipe(
        exhaustMap(() =>
          this.api.run(id).pipe(
            catchError((e: unknown) => {
              if (e instanceof HttpErrorResponse && e.status === 404) this.notFound.set(true);
              return EMPTY; // keep polling; the shell shows the connectivity banner
            })
          )
        ),
        takeWhile((r) => !isTerminal(r.status), true)
      )
      .subscribe({
        next: (r) => {
          this.run.set(r);
          this.now.set(Date.now());
        },
      });
  }

  protected refreshNow(): void {
    this.startPolling(this.id());
  }

  protected statusLabel(s: string): string {
    return s === 'AwaitingHuman' ? 'Awaiting human' : s === 'SafeStopped' ? 'Safe-stopped' : s;
  }

  protected stop(): void {
    this.act(this.api.stop(this.id()));
  }

  protected resume(): void {
    this.act(this.api.resume(this.id()));
  }

  private act(call: ReturnType<ApiService['stop']>): void {
    this.busy.set(true);
    this.actionError.set(null);
    call.subscribe({
      next: () => {
        this.busy.set(false);
        this.refreshNow();
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.actionError.set(describeError(e));
        this.refreshNow();
      },
    });
  }
}
