import { Component, input, output } from '@angular/core';
import { Replan } from '../models';
import { fmtDateTime } from '../util/format';

@Component({
  selector: 'app-replans-tab',
  standalone: true,
  template: `
    @if (replans().length === 0) {
      <div class="empty">No replans: the original plan has held so far.</div>
    } @else {
      <ol class="timeline">
        @for (r of replans(); track $index) {
          <li>
            <span class="dot" aria-hidden="true"></span>
            <div class="card">
              <div class="row" style="justify-content:space-between">
                <strong>Replan #{{ $index + 1 }}</strong>
                <span class="small muted">{{ dt(r.at) }}</span>
              </div>
              <p>{{ r.reason }}</p>
              @if (r.staleNodes.length) {
                <div class="small muted">Invalidated (stale) nodes</div>
                <div class="chips">
                  @for (n of r.staleNodes; track n) {
                    <button class="chip mono s-Stale" (click)="select.emit(n)">{{ n }}</button>
                  }
                </div>
              }
            </div>
          </li>
        }
      </ol>
    }
  `,
  styles: [
    `
      .timeline { list-style: none; margin: 0; padding: 0 0 0 22px; border-left: 2px solid var(--line-2); display: flex; flex-direction: column; gap: 14px; }
      .timeline li { position: relative; }
      .dot { position: absolute; left: -30px; top: 16px; width: 12px; height: 12px; border-radius: 50%; background: var(--accent-2); border: 2px solid var(--bg); }
      .card { background: var(--card-2); }
      p { margin: 6px 0 10px; }
      .chips { display: flex; gap: 6px; flex-wrap: wrap; margin-top: 4px; }
      button.chip { cursor: pointer; padding: 2px 9px; }
    `,
  ],
})
export class ReplansTabComponent {
  readonly replans = input.required<Replan[]>();
  readonly select = output<string>();
  protected dt(iso: string): string {
    return fmtDateTime(iso);
  }
}
