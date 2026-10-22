import { Component, OnInit, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiService, describeError } from '../../api.service';
import { RunSummary, Scenario } from '../../models';
import { fmtDateTime, fmtMs } from '../../util/format';

@Component({
  selector: 'app-launcher-page',
  standalone: true,
  imports: [RouterLink],
  template: `
    <section class="hero">
      <h1>Launch an agentic SDLC run</h1>
      <p class="muted">
        Pick a scenario. The orchestrator plans a DAG of specialised agents, executes it with retries,
        rollbacks and human approval gates, and records a tamper-evident audit trail.
      </p>
    </section>

    @if (loading()) {
      <div class="empty">Loading scenarios...</div>
    } @else if (scenarios().length === 0) {
      <div class="empty">
        No scenarios available. Check that the backend is running on <code>:5080</code>.
        <div style="margin-top:10px"><button (click)="load()">Retry</button></div>
      </div>
    } @else {
      <div class="grid grid-3" role="radiogroup" aria-label="Scenario">
        @for (s of scenarios(); track s.id) {
          <div
            class="card scenario focusable"
            [class.selected]="selectedId() === s.id"
            role="radio"
            tabindex="0"
            [attr.aria-checked]="selectedId() === s.id"
            (click)="selectedId.set(s.id)"
            (keydown.enter)="selectedId.set(s.id)"
            (keydown.space)="selectedId.set(s.id); $event.preventDefault()"
          >
            <div class="row" style="justify-content:space-between">
              <span class="chip dot mode-{{ s.mode }}">{{ s.mode }}</span>
              <span class="radio" aria-hidden="true"></span>
            </div>
            <h2>{{ s.title }}</h2>
            <p class="muted desc">{{ s.description }}</p>
            <div class="small muted req-label">Requirement</div>
            <pre class="pre req" [class.clamp]="selectedId() !== s.id">{{ s.requirementText }}</pre>
          </div>
        }
      </div>

      <div class="card launch-bar">
        <label class="row toggle">
          <input type="checkbox" [checked]="autoApprove()" (change)="onToggle($event)" />
          <span>
            <strong>Auto-approve gates (demo)</strong>
            <span class="muted small"> - skip human approvals. Leave off to act on gates in the UI.</span>
          </span>
        </label>
        <span class="spacer"></span>
        @if (error(); as e) { <span class="err">{{ e }}</span> }
        <button class="btn-primary run-btn" [disabled]="!selectedId() || starting()" (click)="run()">
          {{ starting() ? 'Starting...' : 'Run' }}
        </button>
      </div>
    }

    <section class="card" style="margin-top:20px">
      <div class="card-head">
        <h2>Previous runs</h2>
        <span class="spacer"></span>
        <button (click)="loadRuns()">Refresh</button>
      </div>
      @if (runs().length === 0) {
        <div class="empty">No runs yet.</div>
      } @else {
        <div class="table-wrap">
          <table>
            <thead><tr><th>Run</th><th>Scenario</th><th>Mode</th><th>Status</th><th>Started</th><th>Duration</th><th></th></tr></thead>
            <tbody>
              @for (r of runs(); track r.id) {
                <tr>
                  <td class="mono"><a [routerLink]="['/runs', r.id]">{{ r.id.slice(0, 8) }}</a></td>
                  <td>{{ r.title }}</td>
                  <td><span class="chip mode-{{ r.mode }}">{{ r.mode }}</span></td>
                  <td><span class="chip dot s-{{ r.status }}" [class.pulse]="r.status === 'Running'">{{ r.status }}</span></td>
                  <td class="muted">{{ dt(r.startedAt) }}</td>
                  <td class="muted">{{ dur(r) }}</td>
                  <td><a [routerLink]="['/runs', r.id]">Open</a></td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>
  `,
  styles: [
    `
      .hero { margin-bottom: 18px; max-width: 760px; }
      .hero h1 { font-size: 1.7rem; }
      .scenario { cursor: pointer; display: flex; flex-direction: column; gap: 4px; transition: border-color .12s, box-shadow .12s; }
      .scenario:hover { border-color: var(--line-2); }
      .scenario.selected { border-color: var(--accent); box-shadow: 0 0 0 1px var(--accent), 0 8px 30px rgba(76, 155, 255, .12); }
      .scenario h2 { margin: 8px 0 2px; }
      .desc { margin: 0 0 6px; }
      .req-label { text-transform: uppercase; letter-spacing: .06em; }
      .req { font-size: 12px; max-height: 260px; }
      .req.clamp { max-height: 4.6em; overflow: hidden; -webkit-mask-image: linear-gradient(#000 55%, transparent); mask-image: linear-gradient(#000 55%, transparent); }
      .radio { width: 16px; height: 16px; border-radius: 50%; border: 2px solid var(--line-2); }
      .selected .radio { border-color: var(--accent); background: radial-gradient(var(--accent) 45%, transparent 50%); }
      .launch-bar { display: flex; align-items: center; gap: 16px; margin-top: 16px; flex-wrap: wrap; }
      .toggle { gap: 10px; }
      .toggle input { width: 18px; height: 18px; accent-color: var(--accent); }
      .run-btn { padding: 10px 34px; font-size: 15px; }
      .err { color: var(--bad); }
    `,
  ],
})
export class LauncherPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  readonly scenarios = signal<Scenario[]>([]);
  readonly runs = signal<RunSummary[]>([]);
  readonly selectedId = signal<string | null>(null);
  readonly autoApprove = signal(false);
  readonly loading = signal(true);
  readonly starting = signal(false);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.load();
    this.loadRuns();
  }

  load(): void {
    this.loading.set(true);
    this.api.scenarios().subscribe({
      next: (list) => {
        this.scenarios.set(list);
        if (!this.selectedId() && list.length > 0) this.selectedId.set(list[0].id);
        this.loading.set(false);
      },
      error: () => {
        this.scenarios.set([]);
        this.loading.set(false);
      },
    });
  }

  loadRuns(): void {
    this.api.runs().subscribe({
      next: (r) => this.runs.set([...r].sort((a, b) => (b.startedAt ?? '').localeCompare(a.startedAt ?? ''))),
      error: () => this.runs.set([]),
    });
  }

  onToggle(ev: Event): void {
    this.autoApprove.set((ev.target as HTMLInputElement).checked);
  }

  run(): void {
    const id = this.selectedId();
    if (!id) return;
    this.starting.set(true);
    this.error.set(null);
    this.api.startRun(id, this.autoApprove()).subscribe({
      next: (res) => {
        this.starting.set(false);
        void this.router.navigate(['/runs', res.runId]);
      },
      error: (e: unknown) => {
        this.starting.set(false);
        this.error.set(describeError(e));
      },
    });
  }

  dt(iso: string): string {
    return fmtDateTime(iso);
  }

  dur(r: RunSummary): string {
    if (!r.endedAt) return r.status === 'Running' || r.status === 'AwaitingHuman' ? 'in progress' : '-';
    return fmtMs(new Date(r.endedAt).getTime() - new Date(r.startedAt).getTime());
  }
}
