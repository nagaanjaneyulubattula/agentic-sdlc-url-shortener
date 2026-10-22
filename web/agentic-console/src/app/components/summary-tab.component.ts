import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { ApiService, describeError } from '../api.service';
import { renderMarkdown } from '../util/markdown';

@Component({
  selector: 'app-summary-tab',
  standalone: true,
  template: `
    @if (error(); as e) { <div class="empty">Could not load the summary: {{ e }}</div> }
    @else if (!loaded()) { <div class="empty">Loading summary...</div> }
    @else if (text() === null) {
      <div class="empty">The engineering summary has not been produced yet. It appears when the run finishes.</div>
    } @else {
      <div class="row" style="margin-bottom:10px">
        <label class="row small muted"><input type="checkbox" [checked]="raw()" (change)="raw.set($any($event.target).checked)" /> Show raw markdown</label>
      </div>
      @if (raw()) {
        <pre class="pre">{{ text() }}</pre>
      } @else {
        <!-- markdown is HTML-escaped by renderMarkdown, then sanitised again by Angular -->
        <article class="md card" [innerHTML]="html()"></article>
      }
    }
  `,
})
export class SummaryTabComponent {
  private readonly api = inject(ApiService);
  readonly runId = input.required<string>();
  readonly stamp = input<string>('');

  protected readonly text = signal<string | null>(null);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly raw = signal(false);
  protected readonly html = computed(() => (this.text() === null ? '' : renderMarkdown(this.text()!)));

  constructor() {
    effect(() => {
      const id = this.runId();
      this.stamp();
      untracked(() => this.load(id));
    });
  }

  private load(id: string): void {
    this.api.summary(id).subscribe({
      next: (t) => {
        this.text.set(t);
        this.loaded.set(true);
        this.error.set(null);
      },
      error: (e: unknown) => this.error.set(describeError(e)),
    });
  }
}
