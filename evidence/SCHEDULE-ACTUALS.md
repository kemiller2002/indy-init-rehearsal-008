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
| T25 catalog after Connect | persistence | 184 | 19:16 | 105 | #18 |
| T21 checkpoint 2 (live PAT tests) | integration | 189 | 19:33 | 122 | #19 |
| CR-T21-01 live P0 fix (missing path) | persistence | — | 19:40 | 129 | #23 |
| T23 causal graph (+CRs) | ui-2 / integration | 159 | 19:41 | 130 | #22 (#20) |
| Pages deploy gate; first verified deployment b45e969 | integration | — | 19:40 | 129 | #24 |
| T36 review checkpoint 2 | reviewer | 204 | 19:44 | 133 | #26 |
| T36 fixes (compare truncation) | persistence | — | 19:54 | 143 | #27 |
| T22 investigation workspace (+CR-T22-1) | ui / integration | 199 | 20:01 | 150 | #28 (#21) |
| T22B engine Connect/open/save | ui | — | 20:14 | 163 | #29 |
| T30 pre-RC review | reviewer | 224 | 20:16 | 165 | #30 |
| T26 report + Folio (+CRs) | ui-2 / integration | 184 | 20:18 | 167 | #32 (#25) |
| CI output capture / live filter (T30 F-02, F-06) | integration | — | 20:33 | 182 | #31 |
| T28 history, time travel, semantic diff (+CR) | ui-2 / integration | 249 | 20:41 | 190 | #34 (#33) |

| CR-T22B Connect/Create/Save from the page | integration | — | 21:06 | 215 | #36 |
| CAPS Limen schedule/files capabilities | integration | — | 21:26 | 234 | #38 |
| CRFINAL: T29 + UIFIX + #35/#37/#39 with CRs (chromium 95/95) | ui / ui-2 / integration | 204 (T29) | 21:53 | 262 | #40 |
| T31 release candidate (113/613 P0 verified at b0ba767) | integration | 249 | 22:05 | 274 | #42 |
| EXPVER exported expectedVersion fix | persistence | — | 22:06 | 275 | #41 |
| Test sprint: ITESTS / DTESTS / PTESTS | integration / domain / persistence | — | 22:26–22:30 | 295–299 | #43 #44 #45 |
| Test sprint: UTESTS / ITESTS2 | ui / integration | — | 22:38–22:43 | 307–312 | #46 #47 |
| T32P reconcile defects (>250 commits, last read) | persistence | T32 | 22:43 | 312 | #48 |
| T33 fresh pre-verdict review | reviewer | T33 | 22:55 | 324 | #49 |
| CR-T33-F08 WebKit OPFS probe | integration | — | 22:59 | 328 | #50 |
| T32W wired commands carry their body (+T33-F-07) | integration | T32 | 23:03 | 332 | #51 |

| T32L large-file read-back (T33-F-01) | persistence | T32 | 23:05 | 334 | #52 |
| T34 acceptance at c61bfe2 (256/613 verified; INVALID) | integration | T34 | 23:18 | 347 | #53 |
| FINALDET deterministic bound tests (tests only) | integration | — | 23:25 | 354 | #54 |
| FINALUI chromium bound specs (tests only) | ui | — | 23:30 | 359 | #55 |
| T34 acceptance at final candidate 5e27176 (266/613 verified; INVALID) | integration | T34 | 23:50 | 379 | #56 |

Merge freeze 23:50:28Z (T+379). Coordinator ingest 23:50:44Z; WI-R8-COORD complete; token economics and FINAL-REPORT ~23:55Z. Hard stop 00:31:24Z.

(Earlier note) In flight at 20:41 (T+190): T29 ui-2 (plan T+204), CR-T22B connect/save from the page, UIFIX (ui P0 specs, carries #35). Next: T31 release candidate (plan start T+249).
