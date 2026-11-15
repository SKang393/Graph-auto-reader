<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Review complete observed Rust axis choices with their measured ticks

Source baseline: `e1da376b497757add3329d0aabd14c3121edf9a4`.

## Problem and scope

The [native-work diagnostic](AXIS-NATIVE-WORK-20261115.md) found 381 admitted
pairs among 384 attempted pairs in three exhausted searches. Its two complete
ambiguous lists contained 58 and 122 candidates. More cheap contact pruning
would not address that measured bottleneck. Rust already has stricter observed
span/intersection/outward-tick qualification, but normal Suggest stops when it
finds a second plot, so that result cannot supply a complete review list.

This addition exposes a separate explicit Rust review path. A routing defect
found during native checking also required a shared normal/review projection
selection repair, described below. Neither path selects an ambiguous plot
automatically, infers scientific values or weakens calibration fits. The changes
are justified and tested with source-owned controls.
No private input, independent evaluation, sealed payload, training or threshold
selection is part of the implementation checks. Sealed-run budget and use: zero.

## Rust enumeration and wire

`--review-candidates` reads the existing bounded GRA1 frame. It reuses the same
projection routes, strong-band rules, strict observed span/tick measurement and
existing 128-pair and bridge-work limits. It evaluates the entire eligible
row/column product before returning anything. Pair/band/bridge failure discards
the whole list, including any earlier valid choices. Normal selection retains
its unique-plot requirement and ordinary wire.

Success is `REVIEW1 N`, then exactly N LF-terminated
`BOX left bottom right top xTickCsv yTickCsv` rows. Both arrays contain 3 to 128
strictly increasing measured pixel positions within the corresponding bounds.
Rows follow ascending row-band bottom, then column-band left, not a confidence
ranking. Output is buffered and rejected before writing if it exceeds 64 KiB.
Failure is one allowlisted ERR line with exit code 1. Mixed diagnostics/review
flags are rejected. No numeric labels or confidence estimates are transmitted.

The initial owned proof used geometry-only rows. Before integration, the wire
was extended to preserve the already observed ticks, allowing downstream OCR to
associate actual measurements instead of discarding that evidence. Both test
stages are recorded below; the initial protocol was never shipped.

## Managed integration

The separate managed reader snapshots input, launches hidden off-thread, drains
bounded stdout/stderr concurrently, and requires complete EOF plus the expected
exit status. It rejects malformed, duplicate, truncated, extra, oversized or
out-of-bounds rows and invalid tick lists as a whole. Cancellation and timeout
kill the child and wait boundedly. The existing ordinary one-line client is
unchanged.

Explicit review prefers a complete Rust list. Only NO_AXIS and explicit
platform/start unavailability permit native fallback; ambiguity, resource
refusal, malformed output, I/O failure, timeout and cancellation do not.
AMBIGUOUS_AXIS also covers Rust resource exhaustion, so it cannot authorize
fallback by itself.

The workspace binds provider and a defensive immutable candidate/tick snapshot
to the exact image/tab/calibration. Rust first/last X positions come from the
observed list endpoints. Native choices retain empty tick lists and cannot
self-label through JSON. Both providers have unknown confidence and a persistent
geometry-review warning. Selection previews anchors; explicit Use leaves numeric
fields blank and Apply disabled. Explicit Read forwards the selected measured
pixels to the existing literal OCR/refit pipeline. Existing fit checks and
reviewed Apply remain required. No frozen project/model schema changes.

## Rust owned verification

```text
cargo test --manifest-path native/graphreader-engine/Cargo.toml --locked --offline --jobs 1
cargo build --manifest-path native/graphreader-engine/Cargo.toml --locked --offline --jobs 1
```

Both used checkout-local `CARGO_TARGET_DIR=artifacts/native-engine`. Neither was
a packaged build. The historical diagnostic worker keeper was not overwritten.

| Stage | Result | Timing |
| --- | --- | --- |
| Geometry-only intermediate | 61/61 tests, 7/7 actual process checks | Tests 0.17 s; compile 3.79 s; test command 4.631 s |
| Final measured-tick wire | 61/61 tests | Tests 0.17 s; compile 2.93 s; test command 3.642 s |
| Final Debug worker build | Zero errors | Compile 1.83 s; command 2.155 s |
| Final actual process checks | 7/7 expected responses/exits; empty stderr throughout | Suite 596.262 ms; shell 2.979 s |

Unique review returned one choice (47 bytes) in 54.7533 ms. The diagonal
two-plot control returned exactly two choices (91 bytes) in 43.5408 ms, with
original-coordinate bounds `(20,100,115,10)` and `(148,228,243,138)` in wire order.
Ordinary Suggest still returned its expected unique response or AMBIGUOUS_AXIS
for the two-plot control. Blank, resource-exhausted and mixed-flag requests
returned only their expected ERR responses. These timings are small owned
controls, not a controlled speed comparison or real-input latency claim.

Other owned guards cover gray 127 versus 128, RGB equivalence, sparse/inward
ticks, thick bands, pair/band limits, a bridge failure after an earlier valid
choice, immutable pixels and response-size rejection. No unexpected test or
process failures occurred. All fourteen intermediate/final process invocations
exited and were disposed. Six final payload calls showed job membership and
BelowNormal priority; mixed-flag refusal occurs before reading input. The
0.4992-logical-core limit on this 12-logical-processor host is derived from the
existing fail-closed CPU-rate setup, not a new external utilization measurement.

| Identity | SHA-256 |
| --- | --- |
| Measured-wire source before routing repair | `6f353edb936300ee8b023f24eb0dd16d7cb0d35a146aef9a8a4265100332d370` |
| Final Debug worker | `88c6ade019be248527dd3a8fc8866894b930f23139b84dbebbdbbb6f6e2242e1` |
| Intermediate process report | `a1fb6d3be3df05c5a270ccc3007f00cea096a5e5d31b585af8d7981022a0c7c4` |
| Final process report | `882834c97c4dc3b30c838b8c062ba779e21c163ce55479a72cc5b8f3f65fa8ae` |
| Final process helper | `849ba50464df8d069bfaea7d1aded72c364f7dd88c7027063f05f79c9d935805` |

Independent read-only review found no actionable transport or protocol issue.

## Managed verification

Normal-host VSTest used the existing restored dependencies, Debug outputs,
`--no-build --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
-p:NuGetAudit=false` and `VSTEST_CONNECTION_TIMEOUT=30`. No private inputs were
read. Serial App and Axis builds completed with zero warnings/errors in 11.969
and 5.365 seconds after adding the workspace-to-OCR boundary regression. The
reviewed native OpenCV binary was restaged after compilation.

| Check | Result | Test / command duration | TRX SHA-256 |
| --- | --- | --- | --- |
| Focused App review, workspace, provider, localization and shell contracts | 66 passed | 2 / 6.127 s | `cb7baf2d7d2a0c6528754d4e69335df245c673ff3ba6c8bb737ebe9e807b8177` |
| Final focused Axis transport | 19 passed, 2 optional skips | 0.124 / 2.564 s | `614502ce0b0c84171a1f58ff693be72ef30c089dee292db6809f1d7bb028c91f` |
| Full App suite | 1,151 passed, no skips or failures | 43 / 45.668 s | `3b60309e3be9470f7391a45d3acaf4ac356c10fdda0247909471f9023dbb1f31` |
| Full Axis suite | 202 passed, 2 optional skips, no failures | 2 / 4.208 s | `86aba11828f877fe3fb8e21ee3ee0036c870a5fb025ba93fc879543691fa4ffa` |

The boundary test checks that a Rust choice retains its measured tick positions
and forwards them only on an explicit label-read request, with blank numeric
drafts and no calibration application. The earlier focused transport run passed
18 tests with the same two optional skips before that final coverage addition.
These are component checks, not native UI or real automatic-calibration proof.

## Native interaction and projection-routing repair

The source-built native app opened a 1,280 by 960 owned image made from two
unscaled copies of the existing GridLines fixture. Review returned six native
fallback choices, with no default selection and Use disabled. Escape dismissed
them. No label reading, calibration application, save or export occurred during
that first check. An actual release-worker call on the same App-normalized WPF
Gray8 pixels returned `ERR NO_AXIS`, exit 1, empty stderr in 80.3481 ms. This
result is recorded as fallback coverage, not successful Rust review coverage.

The paired ordinary diagnostic returned `ERR AMBIGUOUS_AXIS` and
`TRACE1 GRID NO_VALID_PAIR 6 0 0 0 0 0 0 0`, exit 1, empty stderr in 66.4032 ms.
No pair reached tick qualification. Gray pixels had SHA-256
`a6ce98ced973cec84f6bbe89634a124e24e72d43f2faf0aaca59732af16a072a`;
the source PNG was unchanged. Row/column maxima were 489/319 against primary
minima 512/384. Content extents 1,155 by 816 produced minima 462/326: horizontal
selection was ambiguous while vertical selection independently lacked an axis.
The shared `select(rows).and_then(select(cols))` pattern short-circuited before
that vertical failure and entered grid recovery with six row bands and zero
column bands. Existing connected-run maxima were 486/306, with minima 194/122.
This is a routing defect before label reading, not evidence for a recognition
threshold change. The shared `select_pair` helper now checks both projections
independently and reaches existing recovery when either lacks the existing
minimum. When both qualify, it preserves ambiguity. Both normal and review
routes use it. Strict tick/span qualification, pixel thresholds and budgets
are unchanged.

The first diagnostic script emitted an extra empty object from PowerShell's
asynchronous write helper. That prevented its conditional ordinary-diagnostic
call from running. The helper output was suppressed, and only that missing
ordinary call was then executed; the review call was not repeated.

The owned regression failed before the repair: one failed, 61 filtered, command
4.0083 seconds. The final complete Rust suite passed 63/63, compile 3.27 seconds,
tests 0.18 seconds, command 4.1643 seconds. A separate Debug build took 2.2588
seconds. On the exact same rendered PNG and WPF Gray8 bytes, the old Debug
worker returned `ERR NO_AXIS` in 78.0863 ms and the repaired Debug worker returned
the complete two-choice response in 195.327 ms. Both exited with empty stderr;
the former exited 1 and the latter 0. This is a single functional comparison,
not a controlled latency benchmark.

```text
REVIEW1 2
BOX 100 380 580 80 140,340,540 80,230,380
BOX 740 860 1220 560 780,980,1180 560,710,860
```

| Routing identity | SHA-256 |
| --- | --- |
| Final Rust source | `473c46a57ab2b15023c0a0655ce237dd2619dddb4917c276d97e470cc3d7a894` |
| Repaired Debug worker | `c5c8eb71f36976ec7b131a97804456f8d16c914045bfdb5545b1f086aa633ad8` |
| Before process result | `d69149183fe4d6d5cc5188083e6643af03f6189024d10e417c966ce99289b8be` |
| After process result | `7ff661ec99f768ffd246b5a8d554a3822e633f6d904f1b195f7edcb84a0082ce` |
| Projection measurement | `0f56ba591cfd32a787111b96ffad758be4970598c48fc23fa94563bc5331b3ef` |

One incremental App test-project build staged the repaired release Rust worker:
zero warnings/errors, MSBuild 6.12 seconds, command 7.095 seconds. The unchanged
managed suites above were not repeated; final exact-commit CI is required.
The reviewed OpenCV runtime was verified/restaged after this build.

Native source-app interaction then passed the following sequence on the same
owned PNG, with no supplied geometry, numeric edits or replacement fixture:

1. Explicit Review returned exactly the two Rust choices above, with no default
   selection and Use disabled. Selecting the first plot previewed it without
   creating an Undo entry.
2. Explicit Use placed anchors at `(140,380)`, `(140,80)` and `(540,380)`, using
   measured X tick endpoints. Y maximum, first and final session were blank;
   the geometry warning remained visible and Apply was disabled.
3. One explicit label read returned six text regions. The UI reported actual
   GPU execution on AMD Radeon RX 6600 XT. No value was applied automatically.
4. Refit paired all six labels to measured ticks: X values 1/2/3 at pixels
   140/340/540; Y values 10/5/0 at pixels 80/230/380. It filled Y maximum 10,
   first session 1, final session 3. Apply remained disabled until the review
   checkbox was checked.
5. After confirming the owned printed values, Apply created a Calibrate axes
   Undo entry. Undo restored the prior uncalibrated state and manual defaults;
   Redo became available. No project save, CSV export or marker edit was made.
   The app was closed normally and its process/window absence was verified.

The source PNG SHA-256 remained
`b67913c31dbbe4f6a9a651cc003b07c800f968e9f5e61461768c538bbd8b8642`.
Source App EXE/DLL hashes were
`539f56b547644666d9b18eeafb36aa495c9817a2fac1dbefbac1d56d9bef7689` /
`99e27730db36135bf6ddc914042e7c2a866e87b8a0a726b9875cbfdeb49d7b99`.
Loaded release Rust hash was
`9c5ac19abc3b08632d82b5590a9e59f2794d94f92f345fbc70741e181808c0d9`.
The window initially needed activation after launch before Computer Use could
capture its monitor. An off-screen Use click had no effect until the inspector
was scrolled; both were observation/input-tool details, not additional model
attempts. Review widgets required inspector scrolling on this window size.

This proves an owned assisted calibration path with explicit plot selection,
label read, refit and review. It does not prove fully automatic calibration,
real-graph accuracy or end-to-end marker digitization. Exact-head
[Windows CI 43719235200](https://github.com/SKang393/Graph-auto-reader/actions/runs/43719235200)
passed on `9e694ca5c0418b7ffcb9c90fe0de741d3c38d67c`, completed at
`2026-11-15T13:37:31Z`: all thirteen .NET suites, 2,955 passed, 30 skipped,
zero failed, including 1,151 App and 204 Axis tests. Rust passed 63/63;
version policy 62/62, ledger 6/6, portable retention 12/12, packaging verification
68/68 and the remaining evidence checks passed. Public-release jobs were
skipped. This result covers that checkpoint, not later eligibility changes.

## Fixed development review-availability diagnostic

The new separately pinned `RealDevReviewRoutingTracePins.cs` profile reuses the
existing first-twelve selector and panel preparation. It invokes the actual
Rust-first review service once per effective panel and never chooses a
candidate, invokes normal Suggest, or calls OCR. Runtime verification precedes
selector loading. Historical profiles and the unchanged451/Mirage comparison
are unchanged.

```text
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj --configuration Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false -p:BuildProjectReferences=false
GraphReader.CalibrationOcrProbe.exe --real-dev-review-routing-geometry-self-test
GraphReader.CalibrationOcrProbe.exe --real-dev-review-routing-geometry-trace <authorized-corpus> <source-App.exe> <reviewed-assets> <source-Rust.exe> --explicit-opt-in
```

Probe build passed with zero warnings/errors in 8.115 seconds. All twenty pinned
runtime libraries matched in App and probe output. The owned-only self-test
passed in 0.617 seconds, exit zero and empty stderr: empty-input refusal,
complete-list accounting, declared NO_AXIS fallback and no candidate selection.
The initial real-run shell preflight used a path without the `win-x64` segment
and stopped before launching the probe or opening data. Correcting that path
was the only launch repair; no private evaluation was repeated.

The one actual fixed-twelve run finished at `2026-11-15T13:24:32.5749003Z`, exit
zero, empty stderr, 13.2522 seconds wall time. All twelve inputs and panels were
attempted and finished: eleven prepared panels and one full-image fallback.
There were no unsupported dimensions, panel exceptions, project timeouts,
unfinished or unattempted panels.

| Stage | Count and result |
| --- | --- |
| Rust review | 12 calls: 4 complete one-choice lists, 7 NO_AXIS, 1 AMBIGUOUS_AXIS |
| Native fallback | 7 calls, all OPENCV_AXIS_AMBIGUOUS |
| Final review availability | 4/12 complete lists; 8/12 unavailable |
| Candidate selection / OCR / Apply / export | All zero |

Review work totaled 9,758.749 ms, median 759.2781 ms per panel; preparation
totaled 2,043.882 ms. These are single-run diagnostic timings. The preceding
ordinary geometry run returned 7/12 single proposals; that is a different
operation and eligibility rule, so 4/12 review-list availability is not a
measured accuracy improvement or decline. Normal Suggest was not rerun here.
No returned candidate was scored against private truth. The next useful
investigation is why completed native searches cannot supply eligible review
choices, rather than increasing contact budgets or repeating this run unchanged.

Assignment and selected-inventory SHA-256 values remain
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a` and
`03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`.
The unchanged selected-source digest is
`4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1`.
All 51 reserved inputs remained excluded. Sealed payload reads, OCR calls,
truth scoring, image writes, optimizer calls and paid services were zero.

| Diagnostic artifact | SHA-256 |
| --- | --- |
| Probe DLL | `9569ec11dc91947c23a7b70766a1d27f53947dba8d5c13b2ffa0501103150eb2` |
| Profile source | `711778d67ba1db319778244873a2d3c47819b8537676632ff08e1b6450d061c7` |
| Owned self-test | `94adfaa152d303d52342f2594ae4efd4c63dd5de709ea49fd8574b6c1867ccdd` |
| Fixed-twelve report, 8,360 bytes | `dbdb219c8b90ab21c98dd01d8877bc4ad6f0d76c76ffbf0d971d64976fffe9b8` |

## License and retention

All additions are Apache-2.0 project source. No dependency, model, font binary,
external source or paid service was added. The sole retained package remains
0.4.52 until a verified, uploaded and ledger-recorded replacement is activated.
The Rust/App changes are consolidated into the pending 0.4.53 preview rather
than generating another adjacent package.

After this source/outcome reached main in `9e694ca5`, the 32 initial Rust review
scratch files totaling 25,946 bytes were retired as part of the
[recorded cleanup](NATIVE-AXIS-REVIEW-ELIGIBILITY-20261115.md#retention).
Completed managed TRXs and the remaining owned UI/probe outputs were retired
after their consumers finished. The two-panel PNG formerly in
`artifacts/rust-review-ocr-owned-check` completed the
[453 packaged native checks](INTERNAL-PREVIEW-453-VERIFICATION-20261115.md)
and was retired with its generated worker records. It is generated project-owned
material, not a private figure. Originals, sealed
inputs, current runtime assets, user Data and the sole verified build remain
protected. The fixed-twelve report formerly in `artifacts/rust-review-routing-check`
was retired after the [failure-accounting comparison](NATIVE-REVIEW-INTEGER-REPRESENTATION-20261115.md)
reached main. Real automatic-calibration acceptance is still incomplete.
