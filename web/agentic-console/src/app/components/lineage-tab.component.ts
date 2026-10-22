import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { ApiService, describeError } from '../api.service';
import { LineageRecord } from '../models';
import { fmtTime, shortHash } from '../util/format';

@Component({
  selector: 'app-lineage-tab',
  standalone: true,
  template: `
    @if (error(); as e) { <div class="empty">Could not load decision lineage: {{ e }}</div> }
    @else if (records() === null) { <div class="empty">Loading decision lineage...</div> }
    @else if (records()!.length === 0) { <div class="empty">No decisions recorded yet.</div> }
    @else {
      <div class="grid cards">
        @for (r of records()!; track r.id) {
          <article class="card dec">
            <div class="row" style="justify-content:space-between">
              <span class="chip mono">{{ r.nodeId }}</span>
              <span class="small muted">{{ time(r.at) }}</span>
            </div>
            <h3>{{ r.title }}</h3>
            <div class="lbl">Rationale</div>
            <p>{{ r.rationale }}</p>
            @if (r.alternatives.length) {
              <div class="lbl">Alternatives considered</div>
              <ul>@for (a of r.alternatives; track a) { <li>{{ a }}</li> }</ul>
            }
            <div class="lbl">Trade-off</div>
            <p>{{ r.tradeoff }}</p>
            @if (r.basedOn.length) {
              <div class="lbl">Based on</div>
              <div class="basis">
                @for (b of r.basedOn; track b.artifact + b.hash) {
                  <span class="chip mono" [title]="b.hash">{{ b.artifact }} <span class="muted">&#64;{{ short(b.hash) }}</span></span>
                }
              </div>
            }
          </article>
        }
      </div>
    }
  `,
  styles: [
    `
      .cards { grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); }
      .dec { background: var(--card-2); }
      .dec h3 { margin: 8px 0 6px; }
      .dec p { margin: 2px 0 8px; }
      .dec ul { margin: 2px 0 8px; padding-left: 18px; }
      .lbl { font-size: 11px; text-transform: uppercase; letter-spacing: .07em; color: var(--muted); margin-top: 6px; }
      .basis { display: flex; gap: 6px; flex-wrap: wrap; }
    `,
  ],
})
export class LineageTabComponent {
  private readonly api = inject(ApiService);
  readonly runId = input.required<string>();
  readonly stamp = input<string>('');

  protected readonly records = signal<LineageRecord[] | null>(null);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const id = this.runId();
      this.stamp();
      untracked(() => this.load(id));
    });
  }

  private load(id: string): void {
    this.api.lineage(id).subscribe({
      next: (r) => {
        this.records.set(r);
        this.error.set(null);
      },
      error: (e: unknown) => this.error.set(describeError(e)),
    });
  }

  protected time(iso: string): string {
    return fmtTime(iso);
  }
  protected short(h: string): string {
    return shortHash(h, 8);
  }
}
