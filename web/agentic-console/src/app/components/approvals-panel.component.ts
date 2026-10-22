import { Component, computed, inject, input, output, signal } from '@angular/core';
import { ApiService, describeError } from '../api.service';
import { DagNode, PendingApproval } from '../models';
import { fmtTime } from '../util/format';

@Component({
  selector: 'app-approvals-panel',
  standalone: true,
  template: `
    <section class="approvals" aria-live="polite">
      <div class="head">
        <div class="pulse-dot" aria-hidden="true"></div>
        <h2>Human action required ({{ approvals().length }})</h2>
        <span class="spacer"></span>
        <label class="row small muted approver">
          Approver
          <input type="text" [value]="approver()" (input)="approver.set($any($event.target).value)" />
        </label>
      </div>

      @if (error(); as e) { <div class="err">{{ e }}</div> }

      <div class="items">
        @for (a of approvals(); track a.id) {
          <article class="item" [attr.data-approval]="a.id">
            <div class="row" style="gap:8px">
              <span class="chip" style="--c:#fbbf24">{{ a.kind === 'clarification' ? 'Clarification needed' : 'Approval gate' }}</span>
              <span class="chip">{{ a.when }}</span>
              @if (a.kind === 'approval') { <span class="chip dot risk-{{ a.risk.toLowerCase() }}">{{ a.risk }} risk</span> }
              <strong class="mono">{{ nodeLabel(a.nodeId) }}</strong>
              <span class="spacer"></span>
              <span class="small muted">requested {{ time(a.requestedAt) }}</span>
            </div>
            <p class="reason">{{ a.reason }}</p>

            @if (a.summary) {
              <div class="small muted">Summary</div>
              <pre class="pre summary">{{ a.summary }}</pre>
            }

            @if (a.kind === 'clarification') {
              <div class="questions">
                @for (q of a.questions; track q.id) {
                  <label class="q">
                    <span>{{ q.question }}</span>
                    <textarea
                      [attr.data-question]="q.id"
                      [value]="answer(a.id, q.id)"
                      (input)="setAnswer(a.id, q.id, $any($event.target).value)"
                      placeholder="Your answer..."
                    ></textarea>
                  </label>
                }
              </div>
              <div class="row" style="margin-top:10px">
                <button class="btn-ok" [disabled]="busy() || !complete(a)" (click)="submitAnswers(a)">
                  {{ busy() ? 'Submitting...' : 'Submit answers' }}
                </button>
                @if (!complete(a)) { <span class="small muted">Answer every question to continue.</span> }
              </div>
            } @else {
              <label class="q">
                <span class="small muted">Comment (optional)</span>
                <textarea [value]="comment(a.id)" (input)="setComment(a.id, $any($event.target).value)" placeholder="Reason for your decision..."></textarea>
              </label>
              <div class="row" style="margin-top:10px">
                <button class="btn-ok" [disabled]="busy()" (click)="decide(a, true)">Approve</button>
                <button class="btn-bad" [disabled]="busy()" (click)="decide(a, false)">Reject</button>
              </div>
            }
          </article>
        }
      </div>
    </section>
  `,
  styles: [
    `
      .approvals {
        border: 1.5px solid var(--warn); border-radius: var(--radius); padding: 14px 16px;
        background: linear-gradient(180deg, rgba(251, 191, 36, .1), rgba(251, 191, 36, .03));
        box-shadow: 0 0 0 4px rgba(251, 191, 36, .08);
      }
      .head { display: flex; align-items: center; gap: 10px; margin-bottom: 10px; flex-wrap: wrap; }
      .head h2 { margin: 0; color: #ffe29a; }
      .pulse-dot { width: 10px; height: 10px; border-radius: 50%; background: var(--warn); animation: p 1.2s infinite; }
      @keyframes p { 0% { box-shadow: 0 0 0 0 rgba(251,191,36,.7); } 100% { box-shadow: 0 0 0 10px rgba(251,191,36,0); } }
      .approver input { width: 150px; padding: 4px 8px; }
      .items { display: flex; flex-direction: column; gap: 12px; }
      .item { background: var(--card); border: 1px solid var(--line-2); border-radius: 10px; padding: 12px 14px; }
      .reason { margin: 8px 0; }
      .summary { max-height: 220px; font-size: 12.5px; margin-bottom: 8px; }
      .q { display: flex; flex-direction: column; gap: 4px; margin-top: 8px; }
      .err { color: var(--bad); margin-bottom: 8px; }
    `,
  ],
})
export class ApprovalsPanelComponent {
  private readonly api = inject(ApiService);

  readonly runId = input.required<string>();
  readonly approvals = input.required<PendingApproval[]>();
  readonly nodes = input<DagNode[]>([]);
  readonly changed = output<void>();

  protected readonly approver = signal('console-user');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly comments = signal<Record<string, string>>({});
  private readonly answers = signal<Record<string, string>>({});

  protected readonly nodeNames = computed(() => new Map(this.nodes().map((n) => [n.id, n])));

  protected nodeLabel(id: string): string {
    const n = this.nodeNames().get(id);
    return n ? `${n.agent} (${id})` : id;
  }
  protected time(iso: string): string {
    return fmtTime(iso);
  }
  protected comment(id: string): string {
    return this.comments()[id] ?? '';
  }
  protected setComment(id: string, v: string): void {
    this.comments.update((m) => ({ ...m, [id]: v }));
  }
  protected answer(aid: string, qid: string): string {
    return this.answers()[`${aid}:${qid}`] ?? '';
  }
  protected setAnswer(aid: string, qid: string, v: string): void {
    this.answers.update((m) => ({ ...m, [`${aid}:${qid}`]: v }));
  }
  protected complete(a: PendingApproval): boolean {
    return a.questions.every((q) => this.answer(a.id, q.id).trim().length > 0);
  }

  protected decide(a: PendingApproval, approved: boolean): void {
    this.send(a, { approved, comment: this.comment(a.id).trim() || undefined, approver: this.approver() });
  }

  protected submitAnswers(a: PendingApproval): void {
    const answers: Record<string, string> = {};
    for (const q of a.questions) answers[q.id] = this.answer(a.id, q.id).trim();
    this.send(a, { approved: true, answers, approver: this.approver() });
  }

  private send(a: PendingApproval, body: { approved: boolean; comment?: string; approver?: string; answers?: Record<string, string> }): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.decide(this.runId(), a.id, body).subscribe({
      next: () => {
        this.busy.set(false);
        this.changed.emit();
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(describeError(e));
        this.changed.emit();
      },
    });
  }
}
