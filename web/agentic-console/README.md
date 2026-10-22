# Agentic Console

Angular 18 (standalone components + signals) UI for the agentic SDLC orchestrator. No UI libraries.

- **Install:** `npm install`
- **Dev:** `npm start` serves http://localhost:4200 and proxies `/api` to `http://localhost:5080` (`proxy.conf.json`).
- **Mock backend:** `npm run mock` (or `node mock-server.mjs`) starts a dependency-free simulated API on :5080 with a time-progressing run, approval gates, a clarification, a replan, retries/rollback/fallback, a hash-chained audit trail, lineage, artifacts and a summary.
- **Build:** `npm run build` writes the production bundle straight into `../../src/Agentic.Api/wwwroot` (cleaned on each build), so the .NET host serves the UI at http://localhost:5080 (it needs SPA fallback to `index.html`).
- **Layout:** `src/app/{api.service.ts, models.ts, pages/{launcher,run}, components/, util/}`.
- Run page polls `GET /api/runs/{id}` every second until the run reaches a terminal status.
