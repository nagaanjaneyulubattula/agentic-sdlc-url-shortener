import { Component, computed, input, output } from '@angular/core';
import { DagNode } from '../models';
import { fmtMs } from '../util/format';

interface Placed {
  node: DagNode;
  x: number;
  y: number;
}
interface Edge {
  key: string;
  from: string;
  to: string;
  d: string;
  cls: string;
}

const W = 212;
const H = 84;
const GX = 70;
const GY = 18;
const PAD_X = 20;
const PAD_Y = 40;

/** Layered DAG: columns are the topological layers, edges are drawn from `deps`. */
@Component({
  selector: 'app-dag-view',
  standalone: true,
  template: `
    @if (layout().placed.length === 0) {
      <div class="empty">Waiting for the plan...</div>
    } @else {
      <div class="legend small muted">
        @for (s of legendStatuses; track s) {
          <span class="chip dot s-{{ s }}">{{ s }}</span>
        }
      </div>
      <div class="scroller">
        <div class="canvas" [style.width.px]="layout().width" [style.height.px]="layout().height">
          <svg class="edges" [attr.width]="layout().width" [attr.height]="layout().height" aria-hidden="true">
            <defs>
              <marker id="arrow" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto">
                <path d="M0,0 L8,4 L0,8 z" fill="#5b6b94" />
              </marker>
              <marker id="arrow-hi" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto">
                <path d="M0,0 L8,4 L0,8 z" fill="#6ea8ff" />
              </marker>
            </defs>
            @for (c of layout().columns; track c.i) {
              <text class="col-title" [attr.x]="c.x + W / 2" y="22" text-anchor="middle">
                Layer {{ c.i + 1 }} - {{ c.count }} {{ c.count === 1 ? 'node' : 'nodes' }}{{ c.count > 1 ? ' (parallel)' : '' }}
              </text>
            }
            @for (e of layout().edges; track e.key) {
              <path
                [attr.d]="e.d"
                class="edge {{ e.cls }}"
                [class.hi]="isEdgeHi(e)"
                [attr.marker-end]="isEdgeHi(e) ? 'url(#arrow-hi)' : 'url(#arrow)'"
              />
            }
          </svg>
          @for (p of layout().placed; track p.node.id) {
            <div
              class="node s-{{ p.node.status }}"
              [class.selected]="selectedId() === p.node.id"
              [class.running]="p.node.status === 'Running'"
              [class.waiting]="p.node.status === 'AwaitingApproval'"
              [style.left.px]="p.x"
              [style.top.px]="p.y"
              [style.width.px]="W"
              [style.height.px]="H"
              tabindex="0"
              role="button"
              [attr.aria-label]="p.node.agent + ' ' + p.node.status"
              [attr.data-node]="p.node.id"
              (click)="select.emit(p.node.id)"
              (keydown.enter)="select.emit(p.node.id)"
              (keydown.space)="select.emit(p.node.id); $event.preventDefault()"
            >
              <div class="n-top">
                <span class="n-stage">{{ p.node.stage }}</span>
                <span class="chip dot sm s-{{ p.node.status }}" [class.pulse]="p.node.status === 'Running'">{{ label(p.node.status) }}</span>
              </div>
              <div class="n-agent" [title]="p.node.agent">{{ p.node.agent }}</div>
              <div class="n-id mono" [title]="p.node.id">{{ p.node.id }}</div>
              <div class="n-badges">
                @if (p.node.retries > 0) { <span class="badge b-retry" title="Retries">&#8635; {{ p.node.retries }}</span> }
                @if (p.node.rollbacks > 0) { <span class="badge b-rb" title="Rollbacks">&#8617; {{ p.node.rollbacks }}</span> }
                @if (p.node.usedFallback) { <span class="badge b-fb" title="Used fallback agent/strategy">FALLBACK</span> }
                @if (p.node.approval) {
                  <span class="badge b-gate" [title]="'Approval required ' + p.node.approval.when + ' (' + p.node.approval.risk + ' risk)'">GATE</span>
                }
                @if (p.node.expandedBy) { <span class="badge b-exp" [title]="'Added by replan after ' + p.node.expandedBy">+REPLAN</span> }
                @if (p.node.durationMs !== null) { <span class="n-dur">{{ ms(p.node.durationMs) }}</span> }
              </div>
            </div>
          }
        </div>
      </div>
    }
  `,
  styles: [
    `
      :host { display: block; min-width: 0; }
      .legend { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 10px; }
      .scroller { overflow: auto; border: 1px solid var(--line); border-radius: 10px; background:
        linear-gradient(var(--bg-2), var(--bg-2)); max-height: 78vh; }
      .canvas { position: relative; }
      svg.edges { position: absolute; left: 0; top: 0; pointer-events: none; }
      .col-title { fill: #7585ad; font-size: 11px; letter-spacing: .05em; text-transform: uppercase; }
      .edge { fill: none; stroke: #3a4a78; stroke-width: 1.5; }
      .edge.live { stroke: #4c9bff; stroke-dasharray: 5 4; animation: dash 0.8s linear infinite; }
      .edge.done { stroke: #2f7a62; }
      .edge.hi { stroke: #6ea8ff; stroke-width: 2.4; }
      @keyframes dash { to { stroke-dashoffset: -9; } }

      .node {
        position: absolute; padding: 8px 10px; border-radius: 10px; cursor: pointer; overflow: hidden;
        background: linear-gradient(180deg, color-mix(in srgb, var(--c) 12%, var(--card)), var(--card));
        border: 1.5px solid color-mix(in srgb, var(--c) 70%, #0b1220);
        display: flex; flex-direction: column; gap: 2px;
        transition: box-shadow .15s, transform .1s;
      }
      .node:hover { transform: translateY(-1px); box-shadow: 0 6px 18px rgba(0,0,0,.4); }
      .node.selected { box-shadow: 0 0 0 2px var(--accent), 0 8px 24px rgba(76,155,255,.25); }
      .node.s-Pending, .node.s-Skipped, .node.s-Cancelled { border-style: dashed; opacity: .85; }
      .node.s-Stale { border-style: dashed; }
      .node.running { animation: glow 1.4s ease-in-out infinite; }
      .node.waiting { animation: glow-warn 1.4s ease-in-out infinite; }
      @keyframes glow { 0%,100% { box-shadow: 0 0 0 0 rgba(76,155,255,.55); } 50% { box-shadow: 0 0 0 6px rgba(76,155,255,0); } }
      @keyframes glow-warn { 0%,100% { box-shadow: 0 0 0 0 rgba(251,191,36,.6); } 50% { box-shadow: 0 0 0 6px rgba(251,191,36,0); } }
      .n-top { display: flex; align-items: center; justify-content: space-between; gap: 6px; }
      .n-stage { font-size: 10.5px; text-transform: uppercase; letter-spacing: .07em; color: var(--muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
      .chip.sm { font-size: 10px; padding: 0 7px; }
      .n-agent { font-weight: 650; font-size: 13.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .n-id { font-size: 10.5px; color: var(--muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .n-badges { display: flex; gap: 4px; align-items: center; margin-top: auto; flex-wrap: nowrap; }
      .badge { font-size: 9.5px; font-weight: 700; padding: 0 5px; border-radius: 5px; line-height: 15px; white-space: nowrap; border: 1px solid; }
      .b-retry { color: #fbbf24; border-color: #7a5a14; background: #3a2c0a; }
      .b-rb { color: #fb923c; border-color: #7a4214; background: #3a2208; }
      .b-fb { color: #c084fc; border-color: #5b3a85; background: #2a1744; }
      .b-gate { color: #fbbf24; border-color: #fbbf24; background: transparent; }
      .b-exp { color: #67e8f9; border-color: #1f6a78; background: #0b2e36; }
      .n-dur { margin-left: auto; font-size: 10.5px; color: var(--muted); font-family: var(--mono); }
    `,
  ],
})
export class DagViewComponent {
  readonly nodes = input.required<DagNode[]>();
  readonly layers = input.required<string[][]>();
  readonly selectedId = input<string | null>(null);
  readonly select = output<string>();

  protected readonly W = W;
  protected readonly H = H;
  protected readonly legendStatuses = ['Pending', 'Running', 'AwaitingApproval', 'Done', 'Failed', 'Blocked', 'Stale', 'Cancelled', 'Rejected'];

  protected readonly layout = computed(() => {
    const nodes = this.nodes();
    const byId = new Map(nodes.map((n) => [n.id, n]));
    // Use provided layers; append any node missing from them as an extra layer (defensive).
    const layers: string[][] = this.layers()
      .map((l) => l.filter((id) => byId.has(id)))
      .filter((l) => l.length > 0);
    const seen = new Set(layers.flat());
    const missing = nodes.filter((n) => !seen.has(n.id)).map((n) => n.id);
    if (missing.length) layers.push(missing);

    const pos = new Map<string, Placed>();
    const placed: Placed[] = [];
    let maxRows = 0;
    layers.forEach((layer, li) => {
      maxRows = Math.max(maxRows, layer.length);
      layer.forEach((id, ri) => {
        const node = byId.get(id)!;
        const p: Placed = { node, x: PAD_X + li * (W + GX), y: PAD_Y + ri * (H + GY) };
        pos.set(id, p);
        placed.push(p);
      });
    });

    const edges: Edge[] = [];
    for (const p of placed) {
      for (const dep of p.node.deps) {
        const from = pos.get(dep);
        if (!from) continue;
        const x1 = from.x + W;
        const y1 = from.y + H / 2;
        const x2 = p.x - 2;
        const y2 = p.y + H / 2;
        const dx = Math.max(24, (x2 - x1) * 0.5);
        const target = p.node.status;
        const cls =
          target === 'Running' || target === 'AwaitingApproval'
            ? 'live'
            : from.node.status === 'Done' && target === 'Done'
              ? 'done'
              : '';
        edges.push({
          key: `${dep}->${p.node.id}`,
          from: dep,
          to: p.node.id,
          d: `M${x1},${y1} C${x1 + dx},${y1} ${x2 - dx},${y2} ${x2},${y2}`,
          cls,
        });
      }
    }
    const columns = layers.map((l, i) => ({ i, count: l.length, x: PAD_X + i * (W + GX) }));
    return {
      placed,
      edges,
      columns,
      width: PAD_X * 2 + layers.length * W + Math.max(0, layers.length - 1) * GX,
      height: PAD_Y + maxRows * (H + GY) + 8,
    };
  });

  protected isEdgeHi(e: Edge): boolean {
    const s = this.selectedId();
    return !!s && (e.from === s || e.to === s);
  }

  protected label(status: string): string {
    return status === 'AwaitingApproval' ? 'Awaiting' : status;
  }

  protected ms(v: number | null): string {
    return fmtMs(v);
  }
}
