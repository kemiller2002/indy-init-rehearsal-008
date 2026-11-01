# Indy Init Rehearsal 008

This repository is a greenfield pilot running Praxis 3.24.0, Echelon Foundry's
repository operating system. An older installation may also have `./ros`, a
compatibility alias of `./praxis`.

## Start here

1. Read [`AGENTS.md`](AGENTS.md) and [`BOOTSTRAP.md`](BOOTSTRAP.md).
2. Complete [`PROJECT-CHARTER.md`](PROJECT-CHARTER.md).
3. Establish the baseline in [`context/CURRENT-STATE.md`](context/CURRENT-STATE.md).
4. Select the first bounded mission and its observable acceptance criteria.
5. Record durable evidence, decisions, and handoffs as the work proceeds.

## Local operating commands

```bash
./praxis work begin --id TASK-001 --occurred-at TIMESTAMP --type task
./praxis work context TASK-001
./praxis status
./praxis registry check
./praxis registry build
./praxis validate
```

`work context` reports legal actions and completion evidence. Validation errors include repair instructions; use `./praxis validate --json` for machine-readable output. Complete work with explicit evidence paths as described in `docs/work-protocol.md`.

The installed snapshot is self-contained. It does not read from the source Praxis
repository. `.ros/installation.json` records the package version and checksums
of installed files.

## Pilot rule

The operating system is itself under evaluation. Do not infer that
Indy Init Rehearsal 008 is a validated discipline, method, or product merely because
the repository follows a rigorous process. Measure whether the process improves
decisions, traceability, handoffs, and rework relative to the declared baseline.

## Causa: build, run and verify

Causa's F# engine (`src/Causa.App`, `Causa.Domain`, `Causa.Persistence`) runs as a .NET WebAssembly engine behind
the Limen browser kernel. `src/Causa.Host` is the browser-wasm host (no AOT, no trimming) with one C# `[JSExport]`,
`HostShim.Dispatch`: a Limen message as JSON in, an answer as JSON out. `web/boundary.js` loads the runtime, wraps that
export in a Limen `EngineTransport` and starts `BrowserKernel`; `web/index.html` is the loading shell and the bound
application root; `web/causa.css` reads only published Forma `--ef-*` properties.

```bash
npm ci                                   # exactly package-lock.json
dotnet build Causa.slnx                  # warnings are errors
dotnet fsi build.fsx verify-lock         # stack-lock gate (VER-008): installed == run lock, else exit 1
dotnet fsi build.fsx site                # dist/: web/ + _framework/ + locked Limen, Forma, Folio, Forma workflow assets
dotnet fsi build.fsx test                # dotnet test Causa.slnx; empty or skipped runs fail
npx playwright test                      # assembles dist/ first, then drives /dist/index.html in Chromium
```

Workflows: `ci.yml` (lock gate, Causa.slnx build and non-live tests), `build-and-test.yml` (App.slnx and every
browser suite; required by branch protection), `live.yml` (live provider suites with the CAUSA_* secrets, push to
main and manual), `deploy.yml` (GitHub Pages at the pushed SHA, then a deployed check). `deploy-pages.yml` is retired
to manual use and shares the `pages` concurrency group, so only one publish runs at a time.
