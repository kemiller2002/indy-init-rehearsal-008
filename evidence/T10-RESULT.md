# T10 result

- T0: 2026-11-01T17:31:24Z. Mechanical bootstrap finished 17:31:44Z (20 s; steps in T10-steps.log).
- Bootstrap commit 616ec62 pushed as a fast-forward onto the existing 0fc18ea (DEV-R8-01/02 in T0-START.json).
- First CI on 616ec62: Build and test failed (runner SDK 10.0.403 vs global.json 10.0.402, rollForward disable); Praxis validation failed (bootstrap commit had no work-item attribution).
- Fix 04fbf9b: setup-dotnet reads global.json; package-lock.json committed; bootstrap reconciled post-hoc under WI-R8-T10 (praxis work reconcile, recorded as post-hoc).
- First green CI: 04fbf9b, 2026-11-01T17:35:57Z (Build and test, Praxis validation, Echelon foundations all success; Deploy to GitHub Pages skipped: DEPLOY_TARGET unset, Pages not enabled; the session proxy refuses those settings).
- T10 actual: 4 min 33 s (plan: 29 min).
- Token note: T10 ran in the coordinator's main session; that session is ingested once, into WI-R8-COORD (TOKEN-ECONOMICS: never ingest a session into two executions), so WI-R8-T10 has no own ingest.

## Correction (17:45Z)

T10 is not fully complete at first green CI. Its plan also includes integration's remaining shared files (the 27 estimated minutes). Ten integration-owned planned files were absent after the generator ran: build.fsx, .github/workflows/{ci,live,deploy}.yml, web/{index.html,boundary.js,causa.css}, tests/fixtures/manifest.json, playwright.config.ts and tests/browser/support/fixtures.js. They are carried by WI-R8-T11 (integration lane, in its slot), and their time is reported there. "T10 actual 4 min 33 s" covers only the bootstrap through first green CI.
