import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { ApiService, describeError } from '../api.service';
import { AuditEntry, AuditView } from '../models';
import { fmtTime, shortHash } from '../util/format';

@Component({
  selector: 'app-audit-tab',
  standalone: true,
  template: `
    @if (error(); as e) { <div class="empty">Could not load the audit trail: {{ e }}</div> }
    @else if (!view()) { <div class="empty">Loading audit trail...</div> }
    @else {
      @let v = view()!;
      <div class="chain row" [class.bad]="!v.chainValid">
        @if (v.chainValid) {
          <span class="chip dot" style="--c:#34d399">&#10004; Hash chain valid</span>
        } @else {
          <span class="chip dot" style="--c:#f87171">&#10006; Hash chain BROKEN</span>
        }
        <span class="muted small">{{ v.entries.length }} entries</span>
        <span class="muted small">Head hash</span>
        <code class="head-hash" [title]="v.headHash">{{ v.headHash || '-' }}</code>
      </div>

      <div class="filters row">
        <label class="row small muted">Type
          <select [value]="typeFilter()" (change)="typeFilter.set($any($event.target).value)">
            <option value="">All</option>
            @for (t of types(); track t) { <option [value]="t">{{ t }}</option> }
          </select>
        </label>
        <label class="row small muted">Node
          <select [value]="nodeFilter()" (change)="nodeFilter.set($any($event.target).value)">
            <option value="">All</option>
            @for (n of nodeIds(); track n) { <option [value]="n">{{ n }}</option> }
          </select>
        </label>
        <label class="row small muted" style="flex:1; min-width:160px">Search
          <input type="text" [value]="text()" (input)="text.set($any($event.target).value)" placeholder="message / actor..." />
        </label>
        <span class="muted small">{{ rows().length }} shown</span>
      </div>

      <div class="table-wrap" style="max-height: 560px; overflow:auto">
        <table>
          <thead>
            <tr><th>#</th><th>Time</th><th>Type</th><th>Node</th><th>Actor</th><th>Message</th><th title="prevHash links to previous entry's hash">Link</th><th>Hash</th><th></th></tr>
          </thead>
          <tbody>
            @for (r of rows(); track r.e.seq) {
              <tr>
                <td class="mono muted">{{ r.e.seq }}</td>
                <td class="mono small">{{ time(r.e.ts) }}</td>
                <td><span class="chip">{{ r.e.type }}</span></td>
                <td class="mono small">{{ r.e.nodeId || '-' }}</td>
                <td class="small">{{ r.e.actor }}</td>
                <td>{{ r.e.message }}</td>
                <td [title]="r.linked ? 'prevHash matches previous entry' : 'prevHash does NOT match previous entry'">
                  <span [style.color]="r.linked ? 'var(--ok)' : 'var(--bad)'">{{ r.linked ? '✔' : '✖' }}</span>
                </td>
                <td class="mono small" [title]="r.e.hash">{{ short(r.e.hash) }}</td>
                <td>
                  <button class="tiny" (click)="toggle(r.e.seq)" [attr.aria-expanded]="expanded().has(r.e.seq)">
                    {{ expanded().has(r.e.seq) ? 'Hide' : 'Data' }}
                  </button>
                </td>
              </tr>
              @if (expanded().has(r.e.seq)) {
                <tr class="data-row">
                  <td colspan="9">
                    <pre class="json">{{ json(r.e) }}</pre>
                    <div class="small muted mono" style="margin-top:6px">prevHash {{ r.e.prevHash }}<br />hash {{ r.e.hash }}</div>
                  </td>
                </tr>
              }
            } @empty {
              <tr><td colspan="9" class="muted" style="text-align:center">No entries match.</td></tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
  styles: [
    `
      .chain { gap: 10px; padding: 10px 12px; border: 1px solid var(--line); border-radius: 10px; margin-bottom: 12px; background: #0f1830; }
      .chain.bad { border-color: var(--bad); }
      .head-hash { font-size: 11.5px; color: var(--accent); word-break: break-all; }
      .filters { margin-bottom: 12px; }
      .filters input { padding: 5px 8px; }
      .filters select { padding: 5px 8px; }
      button.tiny { padding: 1px 8px; font-size: 11.5px; }
      .data-row td { background: #0a1124; }
    `,
  ],
})
export class AuditTabComponent {
  private readonly api = inject(ApiService);

  readonly runId = input.required<string>();
  readonly stamp = input<string>('');

  protected readonly view = signal<AuditView | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly typeFilter = signal('');
  protected readonly nodeFilter = signal('');
  protected readonly text = signal('');
  protected readonly expanded = signal<Set<number>>(new Set());

  protected readonly types = computed(() => [...new Set((this.view()?.entries ?? []).map((e) => e.type))].sort());
  protected readonly nodeIds = computed(() =>
    [...new Set((this.view()?.entries ?? []).map((e) => e.nodeId).filter((x): x is string => !!x))].sort()
  );

  protected readonly rows = computed(() => {
    const entries = this.view()?.entries ?? [];
    const t = this.typeFilter();
    const n = this.nodeFilter();
    const q = this.text().trim().toLowerCase();
    return entries
      .map((e, i) => ({ e, linked: i === 0 || e.prevHash === entries[i - 1].hash }))
      .filter(
        (r) =>
          (!t || r.e.type === t) &&
          (!n || r.e.nodeId === n) &&
          (!q || r.e.message.toLowerCase().includes(q) || r.e.actor.toLowerCase().includes(q))
      );
  });

  constructor() {
    effect(() => {
      const id = this.runId();
      this.stamp();
      untracked(() => this.load(id));
    });
  }

  private load(id: string): void {
    this.api.audit(id).subscribe({
      next: (v) => {
        this.view.set(v);
        this.error.set(null);
      },
      error: (e: unknown) => this.error.set(describeError(e)),
    });
  }

  protected toggle(seq: number): void {
    this.expanded.update((s) => {
      const c = new Set(s);
      if (c.has(seq)) c.delete(seq);
      else c.add(seq);
      return c;
    });
  }

  protected json(e: AuditEntry): string {
    return JSON.stringify(e.data ?? {}, null, 2);
  }
  protected time(ts: string): string {
    return fmtTime(ts);
  }
  protected short(h: string): string {
    return shortHash(h, 12);
  }
}
