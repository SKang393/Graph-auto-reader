<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve the spatial budget under descending input order

Baseline: `7d9e5f31fc2dd22af7038089b3f70519b6c5081d`.

## Defect and repair

Insertion sorting 182 descending envelopes needs 16,471 comparisons, exceeding
the shipped B=128 cheap-work budget of 16,384 before any query. Family order
may follow observation order, so this is a reachable algorithmic defect, not
a requirement to increase resource limits. An owned fixture with one plot,
182 detached vertical lines and 90 detached horizontal lines reproduced the
failure at B=128. Reversed input passed; the unrestricted oracle returned one
candidate in both orders.

Both index construction and result-order restoration now use stable bottom-up
merge sorting. Every comparison spends the same shared B-squared budget.
Tail copies and output copies check cancellation, and each array copy is bounded
by the existing index-size limit. Equal spatial keys retain source order;
numeric index order restores the original candidate traversal order.
Geometry tolerances, scoring, exact B=128 contact limit and fail-closed behavior
are unchanged. No dependency or threshold was added. Independent source review
found no actionable issue.

## Owned verification

Normal-host Debug VSTest, serial compiler, existing dependency cache:

```powershell
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj --configuration Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter FullyQualifiedName~AxisPairEnvelopePruningTests --logger 'trx;LogFileName=merge-focused.trx' --results-directory artifacts/axis-merge-sort-check
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj --configuration Debug --no-build --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --logger 'trx;LogFileName=merge-full-axis.trx' --results-directory artifacts/axis-merge-sort-check
```

| Run | Passed / failed / skipped | Test / wall time |
| --- | --- | --- |
| New shipped-limit control before repair | 1 / 1 / 0 | 273 ms / 11.081 s |
| Final focused parity/budget/cancellation controls | 29 / 0 / 0 | 976 ms / 16.300 s |
| Full Axis suite | 164 / 0 / 2 | About 2 s / 6.896 s |

The two full-suite skips are existing opt-in Rust worker cases. The reviewed
OpenCV library was staged into test output; no model-dependent/sealed test was
enabled. Raw TRX identities, in table order:
`0f16c273266be160245f8e91977b44cad5cf52eafa667f173492655f4d28555c`,
`aa303a8de99e9b46fa4b7ce87e6dbfa5d9da0565de17695a55c562336128977e`,
`5860e0532a5a74ab07eedee9bf3f9748db236bc3aa078f4e01e7c80b3ef43086`.

Full [Windows CI 43706342357](https://github.com/SKang393/Graph-auto-reader/actions/runs/43706342357)
completed successfully on exact head
`d279d384fff0c29ee0d897de255447535e3ff7f9`, including build, tests and packaging
validation. Release candidate and tagged publication jobs were correctly skipped.

## Geometry-only development confirmation

The new isolated probe route inherits the prior spatial runtime pins and
changes only `GraphReader.Axis.dll` to
`cdf244fd92b3e82fc033a4c636d229f74e07ee0abb4e5d12cfdd23bc40ab8a52`.
All other runtime, App, model and Rust identities are unchanged and checked
before payload reads. Existing trace routes/pins remain intact. Probe build:
13.00 s, zero warnings/errors. Owned self-test: exit 0, 1.33 s, zero private
reads. Final probe DLL:
`062bd1180e0c804bf230e25322754bb401198d60f3cd87c761850821359ad676`.

```text
GraphReader.CalibrationOcrProbe.exe --real-dev-sorted-spatial-geometry-self-test
GraphReader.CalibrationOcrProbe.exe --real-dev-sorted-spatial-geometry-trace <authorized-corpus> <pinned-App.exe> <reviewed-assets> <unchanged-Rust.exe> --explicit-opt-in
```

One fixed first-12 development run completed in **33.0001 s**, exit 0, empty
stderr. All twelve projects/panels finished; eleven prepared panels and one
full-image fallback. Seven returned geometry and five did not. There were no
other panel failures, unsupported dimensions, timeouts or unattempted panels.
All seven prior axis rectangles and measured X/Y tick sets are unchanged after
normalizing the two diagnostic output schemas. No OCR call, numeric fitting,
Apply, export, image write or truth scoring ran. All 51 reserved inputs remained
excluded and sealed payload reads are zero.

The three envelope-budget failures are gone. Four native attempts now reach
the exact-pair budget and one rejects multiple candidates; none returns new
geometry. Thus automatic coverage remains **7/12**, not an improvement in
coverage. The preceding OCR totals/draft counts are not newly measured here.
Axis time totaled 27,093.4964 ms versus 11,499.2733 ms in the preceding trace;
preparation took 3,021.2339 ms. More pairs now reach exact processing instead
of aborting early, and Rust-only timings also varied. This is not a speed gain
or a controlled causal latency comparison.

The 14,514-byte report hash is
`151937750b8b5434f7578cb43e50aea1dfc975cdf83f3ea76118cc77ef282b1e`.
Assignment, inventory and twelve-source digest match the
[preceding trace](SPATIAL-AXIS-LABEL-ROWS-20261115.md). No per-case private
predictions or pixels are committed.

## Next work and retention

The next exact pruning hypothesis is a zero forward axis extent: existing
scoring can never admit a pair whose maximum right/up projection is nonpositive.
Verify that necessary condition against owned unrestricted candidates before
moving it ahead of contact accounting. Keep all existing bounds and ambiguity
checks. This is a source-level hypothesis, not a claim about private answers.

Separate inspection of the saved prior OCR metadata did not establish a new
coordinate defect. Nine unpaired literal rows exceeded both association bounds;
eight were at most four pixels tall. Tiny duplicate rows and one nonliteral
axis row remain visible correction/exclusion/refit cases. Do not add an
unjustified association threshold or repeat unchanged OCR.

Project code remains Apache-2.0; dependencies/licenses and packaged 0.4.52 are
unchanged. Production automatic acceptance remains incomplete. After this
record is pushed, retire completed `axis-merge-sort-check` outputs and the
previous `spatial-axis-ocr-check` report after the next geometry consumer has
finished. Keep only the specific current report needed for that comparison.

Prior cleanup completed at `2026-11-15T10:38:26.4835832Z`: 22 generated files,
1,853,279 bytes, from completed localization/spatial/OCR run directories. The
new sorting red TRX had a verified identical keeper in its new active folder.
The active development report, original inputs, independent data, model assets,
shared user Data and sole package were preserved.
