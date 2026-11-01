# Schedule actuals vs v4.1.1 topology C plan (T0 2026-11-01T17:31:24Z)

| Task | Lane | Plan finish (T+min) | Actual merged (UTC) | Actual T+min | PR |
|---|---|---|---|---|---|
| T10 bootstrap → first green CI | integration | 29 | 17:35:57 | 4.5 (shared files moved to T11) | direct |
| T14 interface freeze | domain | 49 | 17:47 | 16 | #1 |
| CR-2 FSharp.Core | integration | — | 17:48 | 17 | #3 |
| T18 provider conformance | persistence | 64 | 17:57 | 26 | #4 |
| T11 stack lock + T10 shared files | integration | 44 | 18:10 | 39 | #5 |
| T12 invariants first | domain | 64 | 18:15 | 44 | #6 |
| T35 review checkpoint 1 | reviewer | 79 | 18:17 | 46 | #7 |
| T24 layout engine discovery | integration | 54 | 18:18 | 47 | #2 |

Started (not yet merged) at 18:18: T13 domain (critical path, started 18:12), T19 persistence (18:07), T16 ui (18:11).
