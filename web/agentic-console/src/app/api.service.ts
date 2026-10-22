import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, catchError, of, tap, throwError } from 'rxjs';
import {
  ArtifactInfo,
  AuditView,
  DecisionBody,
  LineageRecord,
  RunSummary,
  RunView,
  Scenario,
} from './models';

/** Turn any thrown value into a short human readable message. */
export function describeError(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const body = err.error as unknown;
    if (body && typeof body === 'object') {
      const o = body as Record<string, unknown>;
      const m = o['error'] ?? o['message'] ?? o['title'] ?? o['detail'];
      if (typeof m === 'string' && m) return m;
    }
    if (typeof body === 'string' && body.length > 0 && body.length < 300) return body;
    if (err.status === 0) return 'Cannot reach the orchestrator backend.';
    return `Request failed (HTTP ${err.status}).`;
  }
  return err instanceof Error ? err.message : String(err);
}

/**
 * Thin typed client for the orchestrator REST API (relative /api URLs).
 * Keeps a global `backendError` signal that the shell renders as a banner.
 */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  /** Non-null while the backend looks unreachable / failing. */
  readonly backendError = signal<string | null>(null);

  private track<T>(source: Observable<T>): Observable<T> {
    return source.pipe(
      tap({
        next: () => this.backendError.set(null),
        error: (e: unknown) => {
          // Only transport-level / 5xx failures flag the backend as down; 4xx are business errors.
          if (e instanceof HttpErrorResponse && (e.status === 0 || e.status >= 500)) {
            const detail = describeError(e);
            const generic = detail.startsWith('Request failed') || detail.startsWith('Cannot reach');
            this.backendError.set(
              e.status === 0 || e.status >= 502 || generic
                ? `Cannot reach the orchestrator backend on :5080 (HTTP ${e.status || 'no response'}). Is it running? The console keeps retrying.`
                : `Backend error (HTTP ${e.status}): ${detail}`
            );
          } else if (e instanceof HttpErrorResponse) {
            // The server answered, so it is reachable.
            this.backendError.set(null);
          }
        },
      })
    );
  }

  scenarios(): Observable<Scenario[]> {
    return this.track(this.http.get<Scenario[]>('/api/scenarios'));
  }

  runs(): Observable<RunSummary[]> {
    return this.track(this.http.get<RunSummary[]>('/api/runs'));
  }

  startRun(scenarioId: string, autoApprove: boolean): Observable<{ runId: string }> {
    return this.track(this.http.post<{ runId: string }>('/api/runs', { scenarioId, autoApprove }));
  }

  run(id: string): Observable<RunView> {
    return this.track(this.http.get<RunView>(`/api/runs/${encodeURIComponent(id)}`));
  }

  audit(id: string): Observable<AuditView> {
    return this.track(this.http.get<AuditView>(`/api/runs/${encodeURIComponent(id)}/audit`));
  }

  lineage(id: string): Observable<LineageRecord[]> {
    return this.track(this.http.get<LineageRecord[]>(`/api/runs/${encodeURIComponent(id)}/lineage`));
  }

  artifacts(id: string): Observable<ArtifactInfo[]> {
    return this.track(this.http.get<ArtifactInfo[]>(`/api/runs/${encodeURIComponent(id)}/artifacts`));
  }

  /** Raw JSON of an artifact; returned as text so it can be pretty-printed verbatim. */
  artifact(id: string, name: string): Observable<string> {
    return this.track(
      this.http.get(`/api/runs/${encodeURIComponent(id)}/artifacts/${encodeURIComponent(name)}`, {
        responseType: 'text',
      })
    );
  }

  /** Markdown summary, or null while it has not been produced yet (404). */
  summary(id: string): Observable<string | null> {
    return this.track(
      this.http.get(`/api/runs/${encodeURIComponent(id)}/summary`, { responseType: 'text' })
    ).pipe(
      catchError((e: unknown) =>
        e instanceof HttpErrorResponse && e.status === 404 ? of(null) : throwError(() => e)
      )
    );
  }

  decide(runId: string, approvalId: string, body: DecisionBody): Observable<unknown> {
    return this.track(
      this.http.post(
        `/api/runs/${encodeURIComponent(runId)}/approvals/${encodeURIComponent(approvalId)}`,
        body
      )
    );
  }

  stop(runId: string): Observable<unknown> {
    return this.track(this.http.post(`/api/runs/${encodeURIComponent(runId)}/stop`, {}));
  }

  resume(runId: string): Observable<unknown> {
    return this.track(this.http.post(`/api/runs/${encodeURIComponent(runId)}/resume`, {}));
  }
}
