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
| T13 semantic kernel | domain | 109 | 18:27 | 56 | #8 |
| CR-T16 (+T16 shell) | integration/ui | 79 | 18:38 | 67 | #11 (#9) |
| T19 save-state machine | persistence | 124 | 18:41 | 70 | #10 |
| T15 frozen fixture replay + T35 fixes | domain | 129 | 18:49 | 78 | #12 |
| T15B frozen v1 replay (5/5 golden, 40/40 negatives) | domain | (T15) | 18:56 | 85 | #13 |
| T20 connect + provider | persistence | 164 | 18:57 | 86 | #14 |
| T17 integration checkpoint 1 (chromium + webkit) | integration | 129 | 19:02 | 91 | #15 |
| CI live fix (live suites never ran: MSB1008) | integration | — | 19:05 | 94 | #16 |
| T27 AI proposal boundaries | domain | 159 | 19:08 | 97 | #17 |

In flight at 19:10 (T+99): T22 ui (critical path; plan finish T+199), T23 ui-2 (plan T+159), T21 integration (plan T+189), T25 persistence PR #18 in CI (plan T+184).
