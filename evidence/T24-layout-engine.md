# T24 — Workflow layout engine discovery (WDL-001, WDL-002; DEC-030)

Work item: WI-R8-T24 (integration lane). Recorded 2026-11-01.
Capability source: Echelon registry channel `echelon-current/linux-x64`
(`kemiller2002/echelon-registry`, `channels/echelon-current/linux-x64.json`) and the
owning repository `kemiller2002/forma` (cloned read-only at `2fcd107`, `main`, 2026-10-30).

## Finding

The existing Echelon workflow layout engine is **Forma.Workflow**, the F# engine in the
Forma repository, distributed to browser hosts as the npm package
**`@echelon-foundry/forma-workflow`**. It is not Forma Studio, and it is not part of the
zero-runtime design-system package (Forma ADR-0006 keeps that package runtime-free).
Causa must compose it; Causa must not write its own layout (WDL-002, DEC-030).

## Engine record

| Field | Value |
|---|---|
| Owner repository | `kemiller2002/forma` (directory `packages/workflow`; engine source `Forma.Workflow`) |
| Package | `@echelon-foundry/forma-workflow` (npm, public, provenance-signed) |
| Version | `1.2.0` (npm `latest`, published 2026-10-29T17:44:27Z, gitHead `8f34a329791e4acad9376b5f95acfca831b8a2aa`) |
| Engine inside | Forma 0.12.0 engine (Forma CHANGELOG, "@echelon-foundry/forma-workflow 1.2.0"); runs on .NET WebAssembly from `dist/engine/_framework/` (`Forma.Workflow.wasm`, `Forma.Workflow.Wasm.wasm`, `FSharp.Core.wasm`) |
| Tarball | `https://registry.npmjs.org/@echelon-foundry/forma-workflow/-/forma-workflow-1.2.0.tgz` |
| Tarball SHA-256 | `211f4f41af9b696e57e4cede2f6439449819c507e6fcf2105042236713440bd1` |
| npm integrity | `sha512-6xopcnEOFN/3n9ItU6V+YLFdWzekDGlKjDxFdyGe4mJz15ZId3G505zNWY2gv+MwAuixZZRUHVQW13mvSAUERw==` |
| License | `PolyForm-Noncommercial-1.0.0` (same as Forma) |
| Peer dependency | `@echelon-foundry/design-system >=0.4.0` — satisfied by the locked `0.12.0` |
| Input format | Forma portable workflow format 1.0.0 (`forma-workflow.schema.json`, `workflow-capabilities.json` contract 1.0.0; capability `workflow.layout`: "authored geometry, waypoints and the deterministic default layout") |
| Public API | Custom element `<forma-workflow>`, host contract `forma-workflow-host/1` (Forma `docs/workflow/EMBEDDING.md`) |
| .NET distribution | `EchelonFoundry.Forma.Workflow` 1.2.0 is named in the Forma CHANGELOG but is **not on nuget.org** (flat-container 404, 2026-11-01). Causa uses the npm package in the browser. |

### API (forma-workflow-host/1)

- Attributes: `mode` (`view`, `inspect`, `edit`, `pick`, `runtime`), `instance`, `engine` (engine directory URL).
- Methods (promises): `load(workflow)`, `getWorkflow()`, `execute(command)` (including
  `{ name: "autoLayout" }`), `undo()`, `redo()`, `select(objects)`, `focusObject(object)`,
  `setRuntimeState(states)`, `validate(workflow)`.
- Events: `forma-workflow-ready`, `-load`, `-change`, `-selection`, `-focus`, `-intent`, `-pick`, `-refused`, `-error`.
- Inline source: `<script type="application/json" class="forma-workflow-source">` inside the element; the
  static figure inside the element is the no-script fallback.
- Isolated mode: `frame.html` over `postMessage` with the same contract.

### Constraints

- Output is deterministic: the same workflow and options give byte-identical HTML (Forma `docs/workflow/RENDERING.md`) — supports WDL-009.
- Validation is strict since 1.2.0: `A11Y-NONTEXT-CONTRAST` (WCAG 1.4.11, 3:1) rejects literal colours below 3:1 and `render` refuses invalid documents. Causa should use `token:status-*` / `token:border-functional` colours, not literals.
- The engine ships .NET WebAssembly (about 94 files); pages must serve `dist/engine/` with the package.
- Text is escaped by the engine's typed HTML tree; intents are reported, never executed (no `eval`).
- License is noncommercial (PolyForm-Noncommercial-1.0.0), as for Forma itself.

## Lock status (T11)

The engine is **not in the locked set**. `package.json` on `main` locks
`@echelon-foundry/design-system 0.12.0`, `@echelon-foundry/limen 0.9.1` and
`@echelon-foundry/print-components 0.4.1`; `forma-workflow` is absent, and the Echelon
registry channel has no `forma-workflow` component (it lists `forma` 0.12.0 as the
design-system web package). T11's agent is editing `package.json`, so this task does not.
Needed addition: **CR-T24-01** below.

### CR-T24-01 (integration applies)

- File: `package.json` (and the regenerated `package-lock.json`).
- Change: add to `"dependencies"`: `"@echelon-foundry/forma-workflow": "1.2.0"` (exact pin; npm tarball integrity above).
- Serves: T24 (WDL-001, WDL-002) and the diagram tasks (T22/T23) that render the workflow view.
- Until it is applied, `tests/browser/acceptance/WDL-001.spec.js` and the browser half of
  `WDL-002.spec.js` fail in CI (the package is not installed), which is the true state.

### Upstream note (no change required for the example)

No upstream change was needed for the working example. Gap to record in Forma's Praxis
input if Causa later needs a .NET host link: `EchelonFoundry.Forma.Workflow` is not published to nuget.org.
A registry channel entry for `forma-workflow` would also let the Echelon lock resolve it.

## Working example (WDL-001-O2)

- Fixture page: `tests/browser/support/wdl-001-fixture.html` — a five-node root-cause
  investigation workflow (start, two tasks, a decision with a "No" loop back, end) given
  to `<forma-workflow mode="view">` as inline Forma workflow JSON with **no authored
  coordinates**. Served from the repository root by the existing `playwright.config.js`.
- Observed locally (Chromium, `@echelon-foundry/forma-workflow@1.2.0` installed with
  `npm install --no-save`): the engine started from `dist/engine/_framework/*.wasm`,
  rendered all five nodes as `.ef-diagram-node` with engine-computed
  `--ef-diagram-x/y/w/h`, drew five SVG connectors in `.ef-diagram__wires`
  (including the routed loop-back edge), and announced "Opened Root cause investigation."
- Tests: `tests/browser/acceptance/WDL-001.spec.js` (O1: this record matches the installed
  package; O2: the engine lays out the example, validly and deterministically) and
  `tests/browser/acceptance/WDL-002.spec.js` (O1: no second layout engine or authored
  diagram coordinates in Causa source, and page geometry comes from the engine; O2: no
  unapproved layout fallback).
