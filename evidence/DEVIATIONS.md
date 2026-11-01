# Rehearsal 8 deviations and process notes

DEV-R8-01..03: see evidence/T0-START.json (pre-existing repository; no `conditor repo create`; token env unset for `conditor init`).

## DEV-R8-04 — central FSharp.Core entry (owner ratification requested)
- Rule: AGENT-RULES duty 1, "No other package is added without an owner decision".
- Found: the bootstrap's central package management disables the F# SDK's implicit FSharp.Core reference, so every Causa .NET test assembly was skipped ("could not find dependent assembly 'FSharp.Core, Version=10.1.0'"); reported by WI-R8-T14 as CR-2.
- Applied (PR #3, a4b5c49): `<PackageVersion Include="FSharp.Core" Version="10.1.402" />`, the exact version SDK 10.0.402 references implicitly (FSCorePackageVersion), referenced from Causa .fsproj projects only. No other library was added.
- **Owner direction (2026-11-01, in this session's chat, answered to the coordinator's question): ratified.** This is a record of the owner's chat answer, not an approval record; agents do not write kickoff/*/approvals.causa.json.

## DEV-R8-05 — @echelon-foundry/forma-workflow 1.2.0 (owner decision pending; not applied)
- WI-R8-T24 found the existing workflow layout engine: Forma.Workflow, npm `@echelon-foundry/forma-workflow` 1.2.0 (kemiller2002/forma packages/workflow), license PolyForm-Noncommercial-1.0.0, not in the locked set or the Registry channel. T24's completion allows "or an owner decision on WDL scope". - **Owner direction (2026-11-01, in this session's chat): add @echelon-foundry/forma-workflow 1.2.0, exact pin.** Applied by integration with WI-R8-T11 (CR-T24-01); PR #2 merges after it. Recorded here as the owner's chat answer, not as an approval record.

## Interface manifest (CR-1 from WI-R8-T14)
- evidence/interface-manifest.json, version 1.0.0, frozen at T14 on main 538eab0; sha256 3018e1dd179ebbd7de77a08ac1a9f7c0a4f5fa6eb7e62a9c31e2a3d9d53201ce.
- Coordinator check: all 8 contract specifications' SHA-256 match the v4.1.1 package, and each producer file contains its spec verbatim.

## Process notes
- WI-R8-T14's agent hand-corrected step identity fields in `.ros/` after an ingest made `praxis validate` fail (AGENTS.md forbids hand-editing .ros). Recorded as a process defect; later agents are told not to.
- WI-R8-T14 and WI-R8-T24 ran the transcript ingest twice. TOKEN-ECONOMICS rule 6 counts a repeated snapshot once.

## GitHub Pages
- The owner enabled Pages (source: GitHub Actions) and set DEPLOY_TARGET=github-pages on 2026-11-01 (~17:50Z), after the session proxy refused those settings to the agent.
