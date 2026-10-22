import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { ApiService, describeError } from '../api.service';
import { ArtifactInfo } from '../models';
import { fmtBytes, prettyJson, shortHash } from '../util/format';

@Component({
  selector: 'app-artifacts-tab',
  standalone: true,
  template: `
    @if (error(); as e) { <div class="empty">Could not load artifacts: {{ e }}</div> }
    @else if (list() === null) { <div class="empty">Loading artifacts...</div> }
    @else if (list()!.length === 0) { <div class="empty">No artifacts produced yet.</div> }
    @else {
      <div class="split">
        <div class="table-wrap list">
          <table>
            <thead><tr><th>Name</th><th>Ver</th><th>Size</th><th>Hash</th></tr></thead>
            <tbody>
              @for (a of list()!; track a.name + a.version) {
                <tr class="clickable" [class.sel]="selected() === a.name" tabindex="0"
                    (click)="open(a)" (keydown.enter)="open(a)">
                  <td class="mono small">{{ a.name }}</td>
                  <td>v{{ a.version }}</td>
                  <td class="small">{{ bytes(a.bytes) }}</td>
                  <td class="mono small" [title]="a.hash">{{ short(a.hash) }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <div class="viewer">
          @if (!selected()) {
            <div class="empty">Select an artifact to view its JSON.</div>
          } @else {
            <div class="row" style="margin-bottom:8px">
              <strong class="mono">{{ selected() }}</strong>
              <span class="spacer"></span>
              @if (loadingBody()) { <span class="muted small">Loading...</span> }
            </div>
            @if (bodyError(); as be) { <div class="empty">{{ be }}</div> }
            @else { <pre class="json body">{{ body() }}</pre> }
          }
        </div>
      </div>
    }
  `,
  styles: [
    `
      .split { display: grid; grid-template-columns: minmax(360px, 1fr) minmax(300px, 1.3fr); gap: 14px; align-items: start; }
      @media (max-width: 900px) { .split { grid-template-columns: 1fr; } }
      .list { max-height: 560px; overflow: auto; }
      .clickable { cursor: pointer; }
      td.nowrap, .list td:nth-child(2), .list td:nth-child(3) { white-space: nowrap; }
      .sel td { background: rgba(110, 168, 255, .12) !important; }
      .body { max-height: 560px; }
    `,
  ],
})
export class ArtifactsTabComponent {
  private readonly api = inject(ApiService);
  readonly runId = input.required<string>();
  readonly stamp = input<string>('');

  protected readonly list = signal<ArtifactInfo[] | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly selected = signal<string | null>(null);
  protected readonly body = signal('');
  protected readonly bodyError = signal<string | null>(null);
  protected readonly loadingBody = signal(false);

  constructor() {
    effect(() => {
      const id = this.runId();
      this.stamp();
      untracked(() => this.load(id));
    });
  }

  private load(id: string): void {
    this.api.artifacts(id).subscribe({
      next: (l) => {
        this.list.set(l);
        this.error.set(null);
      },
      error: (e: unknown) => this.error.set(describeError(e)),
    });
  }

  protected open(a: ArtifactInfo): void {
    this.selected.set(a.name);
    this.body.set('');
    this.bodyError.set(null);
    this.loadingBody.set(true);
    this.api.artifact(this.runId(), a.name).subscribe({
      next: (raw) => {
        this.body.set(prettyJson(raw));
        this.loadingBody.set(false);
      },
      error: (e: unknown) => {
        this.bodyError.set(describeError(e));
        this.loadingBody.set(false);
      },
    });
  }

  protected bytes(b: number): string {
    return fmtBytes(b);
  }
  protected short(h: string): string {
    return shortHash(h, 10);
  }
}
