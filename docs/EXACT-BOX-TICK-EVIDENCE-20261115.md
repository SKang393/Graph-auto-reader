<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Pixel tick evidence for native review geometry

## Defect and bounded change

The [ranked native review trace](NATIVE-RANKED-REVIEW-20261115.md) recovered one
native geometry proposal but supplied no measured ticks. Its additional numeric
associations therefore used explicitly estimated label-center positions. The
existing Rust tick scanners could not simply be called through `measure_axes`:
that function rediscovers endpoints across the image, potentially selecting
different geometry from the reviewed native box.

The new optional `--measure-ticks` Rust mode accepts one exact original-pixel
box. It requires a dark intersection and connected observed axis spans, then
reuses strict inward/outward tick, group-width, spacing, long-stroke and bridge
checks. The supplied box is never moved or expanded into another candidate.
Missing evidence returns empty tick lists or `AXIS_BOX_NOT_OBSERVED`; it never
creates numeric values. The GRT1 transport is additive and separate from the
unchanged normal, diagnostic and review-candidate modes.

The workspace invokes this scan only when reading labels for `opencv-review`
geometry. A separate transient proof binds the result to the original image,
hash, current workspace binding and exact box. The original suggestion object,
provider and warning remain intact. OCR and cached refit use the verified
geometry's immutable tick lists. Stale, cancelled, malformed or mismatched
evidence cannot publish; structured scan failure preserves the original
estimated-label path. Raw native proposals still cannot supply trusted ticks.
Apply still requires fresh explicit review, and Undo remains available.

The development metric is measured-tick availability and complete/partial draft
coverage with all failed stages retained. The safety bar is structural: no
unobserved tick substitution and no silently applied unreviewed calibration.
Acceptance policy and model thresholds are unchanged. One changed-source
first-12 development confirmation was planned; sealed budget was zero.

## Owned validation and failure accounting

- **70/70 Rust tests passed.** New controls cover exact boxes, two-panel
  isolation, inward/outward marks, missing/gray/offset/disconnected geometry,
  sparse and long/grid strokes, adjacent long groups, bounded clipped outline
  bridges, exhausted bridge work, 1/3-channel equivalence, malformed/trailing
  protocol data and incompatible flags. A review found an unnecessary four-pixel
  band cap copied from ambiguous candidate selection. It was removed only from
  the exact-box mode; owned 5-8 pixel bands now retain exact measured positions.
  Original automatic candidate guards remain unchanged.
- **14/14 managed worker tests passed**, zero skips, including actual Rust
  grayscale and RGB child-process requests, exact box echo, immutable lists,
  malformed output, input snapshots, missing worker and pre-cancellation.
- **93/93 application tests passed**, zero skips. They cover original image
  binding, immutable evidence, wrong image/box/ticks, stale/cancelled scans,
  failure fallback, measured refit, persistent native warnings, Apply and Undo.
- Independent read-only review found no material finding in the worker,
  transport, application integration or diagnostic route.

Commands from the active worktree:

```powershell
$env:CARGO_TARGET_DIR = Join-Path (Get-Location) 'artifacts/native-engine'
cargo test --manifest-path native/graphreader-engine/Cargo.toml --locked --offline --jobs 1
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet build tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
$env:GRAPHREADER_RUST_WORKER = Join-Path (Get-Location) 'artifacts/native-engine/release/graphreader-engine.exe'
dotnet vstest tests/GraphReader.App.Tests/bin/Debug/net10.0-windows/win-x64/GraphReader.App.Tests.dll '/TestCaseFilter:FullyQualifiedName~CalibrationOcrWorkspaceTests|FullyQualifiedName~CalibrationTickViewModelTests|FullyQualifiedName~AxisSuggestionTests|FullyQualifiedName~NativeAxisCandidateReviewTests|FullyQualifiedName~NativeAxisWorkerDetectionFailureTests' '/Logger:trx;LogFileName=app-focused-normalhost.trx' '/ResultsDirectory:artifacts/exact-box-ticks-check'
dotnet vstest tests/GraphReader.Axis.Tests/bin/Debug/net10.0/GraphReader.Axis.Tests.dll '/TestCaseFilter:FullyQualifiedName~RustAxisTickMeasurementTests' '/Logger:trx;LogFileName=axis-tick-focused.trx' '/ResultsDirectory:artifacts/exact-box-ticks-check'
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false -p:BuildProjectReferences=false
dotnet tools/GraphReader.CalibrationOcrProbe/bin/Debug/net10.0-windows/win-x64/GraphReader.CalibrationOcrProbe.dll --real-dev-exact-box-tick-trace-self-test
```

Final Rust compile/test time was 3.98/0.20 s, 5.88 s wall. App/Axis builds
passed with zero warnings/errors in 5.08/4.93 s; the probe-only build took
7.69 s. Its owned self-test passed in 0.686 s, checking the actual production
binding helper, mismatch/error fallback and empty-pin refusal without private
reads. Reviewed OpenCV `c96f91b3...f3b31` was restored and verified after builds.

Earlier failures are retained in this record: the first App build failed
CA1859 in `ManualPreviewWorkspaceService.AxisSuggestions.cs` after 68.60 s;
the private helper now accepts its actual array type. Axis test compilation
reported CA1861/CA2263 and was repaired without product changes. One sandboxed
App test attempt timed out after 90 seconds connecting to testhost, completing
zero tests; it was void, and the normal-host retry passed. Its exact orphan
process had already exited when inspected. A local pin-preparation encoding-name
typo failed before changes/private access and was corrected. No failed private
invocation was rerun.

## One development confirmation

`--real-dev-exact-box-tick-trace` pins the source App, Rust worker and twenty
runtime libraries before selector access. It verifies the production binding
helper's signature before reading private input, then uses the same fixed-12
preparation, Rust/native geometry, CPU OCR, association and terminal accounting
as the previous trace. Native geometry alone gets the new scan. The diagnostic
uses the production service and binding helper; it is not native UI acceptance
or a test of workspace currentness. That behavior is covered separately above.

The actual run used `E:/Graph-auto-reader/data/manual data`, the source App,
reviewed 0.4.54 OCR assets, the new release Rust worker and explicit opt-in.
It ran once from `2026-11-15T17:19:49.5337709Z` to
`2026-11-15T17:20:33.0311411Z`, exit zero, empty stderr, **43.4724307 s wall**.
All twelve projects/panels finished: eleven prepared panels and one genuine
no-panel full-image fallback. There were no OCR failures, unsupported panels,
exceptions, timeouts, unattempted or unfinished panels.

Geometry/OCR coverage remained **8/12**, with four geometry failures: three
native pair-budget failures and one rotated best candidate. The one native
proposal reached pixel measurement and returned **`AXIS_BOX_NOT_OBSERVED`**
in **266.5583 ms**. No new measured ticks were bound; original geometry and
estimated evidence remained unchanged. The other seven OCR panels used their
existing Rust geometry and did not invoke the new measurement stage.

All previous aggregate coverage stayed unchanged: **128 OCR regions, 79 literal
numeric rows, 70 associations including estimates, zero complete session
drafts, two printed-X drafts, zero partial-X drafts, three partial-Y drafts,
and two panels with estimated positions**. This run demonstrates the rejection
path on the real sample, not improved automatic calibration. The next diagnosis
is the native box's alignment with observed axis pixels, without repeating OCR
or choosing alternative values/thresholds against private answers.

Stage totals were preparation **2,474.5897 ms**, axes **13,415.6010 ms**, OCR
**26,011.6680 ms**, association **70.3034 ms**, plus the separate tick scan above.
Median/max OCR time was **2,694.6921 / 7,292.0617 ms**. These single-run bounded
CPU timings do not establish a default-device performance change.

Assignment `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`,
inventory `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`,
and selected-source digest
`4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1`
match the unchanged fixed sample. All 51 independent cases stayed excluded;
sealed payload reads, manual truth/geometry input, truth scoring, image writes,
training, Apply and export were zero. No unchanged451 or Mirage rerun occurred.

## Identity, integration and retention

Source base: `1ca57645e2eda111143ce9c09895f8ef661db482`.

| Item | SHA-256 |
| --- | --- |
| App EXE | `843b614c434aafd98d9818ea0e3aed0cd309fb433f5261553300b3d0f9710933` |
| App DLL | `33a0047728c9184273bf7a3ce45ce304f36eae9f4b03e66657194dd561b403bd` |
| Rust worker | `958a14dd74db3cef3c3c726d46581f8a4b35c04b01d71b4349a0dc04c5263e52` |
| Probe DLL | `6ab9be70917e1b5c999bc2bd76dfde76285ad434b8ef4e3ed90ae55a31afad0a` |
| Development report, 65,260 bytes | `cba00fe43ba990ff538dee9a6c7eac832dcef2eef6987ad102e03265bf8f51ff` |
| App TRX, 135,755 bytes | `4652b89c344ce0704badff1896e63c01408648966d316a4a1cacd6685552bc6c` |
| Axis TRX, 20,659 bytes | `be35657c39443763800b01d79bead7c472911a65a5aabfc0027fb1684e29300d` |
| Void testhost TRX, 1,579 bytes | `577f6e439d01af1675e6f6ebfbfa9f831b1845852ee992e5eb6804c08d54ffe7` |

The complete map is in `RealDevExactBoxTickTracePins.cs`. Source remains
Apache-2.0; there is no new dependency, model, license or frozen public schema.
No package/version increment, installer, public release or production approval
was made. The sole delivered 0.4.54 build remains unchanged. Full Windows CI
[43741078318](https://github.com/SKang393/Graph-auto-reader/actions/runs/43741078318)
passed on `9b2b915f4971bbfebd16c58cbeae146575d7c08c`: all 13 .NET suites,
3,021 passed and 30 skipped, including 218 Axis and 1,183 App tests; all 70 Rust
tests; and applicable policy and packaging checks, including 68 packaging
verification tests. The release jobs were skipped. This validates the source
batch, not automatic calibration accuracy or a new packaged build.

`artifacts/exact-box-ticks-check` is temporary integration evidence.
`artifacts/exact-box-tick-dev-trace/report.json` remains temporarily needed by
the next native-box alignment diagnosis for its saved crop/geometry metadata;
this avoids repeating unchanged inference. Retire these outputs after the
source/outcome is pushed and those named consumers finish. Original user data
and independent evaluation inputs are not disposable outputs.

The obsolete unpackaged `artifacts/native-axis-reasons-runtime` copy was retired
at `2026-11-15T17:28:53.3746994Z`: 220 files, 53,786,088 bytes. Its Rust
`cfc8e59a...a1880fd` identity matched the completed historical spatial profile;
the associated outcomes were verified in pushed history at this batch's
`9b2b915f4971bbfebd16c58cbeae146575d7c08c` integration. A scoped consumer audit
found no current path reference or running process. Exact inventory, absence of
models/user data and absence of reparse points were verified before deletion;
the scope is now absent. Historical pins/outcomes, current runtime assets and
the sole verified 0.4.54 package remain.

The subsequent [cached pixel-property diagnosis](CACHED-AXIS-PIXEL-PROPERTIES-20261115.md)
reused this run's crop and geometry without repeating axis detection or OCR.
