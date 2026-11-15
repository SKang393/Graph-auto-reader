<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Completed native geometry as an explicit review proposal

## Defect and scope

The [fixed endpoint development trace](ENDPOINT-LABEL-LANE-20261115.md)
returned geometry on 7/12 panels. Three native searches exceeded their exact
pair budget; two completed searches were discarded solely because they had
multiple ranked candidates. The existing axis detector already ranks a best
hypothesis and reports uncertainty. Its preview adapter imposed a separate
single-candidate rule before checking whether that best geometry was usable.

This change permits that completed detector's rank-zero geometry as an explicit
review proposal. It changes preview review policy, not detector scoring,
thresholds, candidate order, model weights or the 128-pair work budget. It never
selects another hypothesis when rank zero is invalid or rotated. Incomplete
enumeration, cancellation and invalid geometry still return no proposal.

The implementation metric is completed review coverage with full failure
accounting. The safety requirement is unchanged: no silently applied or exported
unreviewed calibration. Owned tests check exact rank-zero fidelity and the
128/129-pair boundary. This is not an automatic-accuracy gate or production
approval; the acceptance bars remain in `ml/policy/acceptance-bars.json`.
One fixed first-12 development confirmation was authorized, with zero sealed
budget, no truth scoring, and the same 120-development/51-reserved assignment.

## Implementation and owned checks

`NativeAxisWorker` now allows a valid best result after complete enumeration,
with `native_multiple_ranked_candidates` when more than one hypothesis exists.
The provider remains `opencv-review`; all measured tick arrays remain empty.
A specific localized warning asks the user to inspect the anchors or review
other axis choices. A partial-axis warning is retained alongside it when needed.
OCR and cached refit preserve that warning and require a fresh explicit review
before Apply. Apply and Undo are exercised through the real workspace/VM.

The first owned 128-candidate control failed against the old adapter with no
geometry, reproducing the single-candidate rejection. An intermediate focused
run passed 33/34 tests: the remaining divider test still expected the old null
normal result. Its expectation now checks the same best geometry as candidate
zero, empty measured ticks and the ambiguity reason; candidate-review coverage
is preserved. Final focused validation passed **84/84**, zero skips/failures.
Independent read-only review found no remaining material finding.

An additional Rust control distinguishes inward-tick primary success from
ambiguous two-panel recovery, which intentionally requires outward ticks.
The existing guard is retained. All **64 Rust tests** passed; no Rust production
behavior changed.

Commands from the active worktree:

```powershell
$env:CARGO_TARGET_DIR = Join-Path (Get-Location) 'artifacts/native-engine'
cargo test --manifest-path native/graphreader-engine/Cargo.toml --locked --offline --jobs 1
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-build --no-restore --filter 'FullyQualifiedName~NativeAxisWorkerDetectionFailureTests|FullyQualifiedName~CalibrationTickViewModelTests|FullyQualifiedName~AxisSuggestionTests|FullyQualifiedName~CalibrationLocalizationTests|FullyQualifiedName~NativeAxisCandidateReviewTests' --logger 'trx;LogFileName=final-focused.trx' --results-directory artifacts/native-ranked-proposal-check
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Debug --no-restore -m:1 -nr:false -p:BuildProjectReferences=false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet tools/GraphReader.CalibrationOcrProbe/bin/Debug/net10.0-windows/win-x64/GraphReader.CalibrationOcrProbe.dll --real-dev-ranked-review-trace-self-test
```

Rust compile/test time was 4.37/0.19 s, 6.55 s wall. The final App test build
passed with zero warnings/errors in 40.85 s; the focused tests took 4 s test
time, 8.19 s wall. Probe-only build took 8.63 s with zero warnings/errors.
The owned profile self-test exited zero and confirmed incompatible-mode and
missing-pin refusal, a ready identity profile and zero private reads.
The reviewed OpenCV DLL was restored after compiler copy-local replaced it;
App, test and probe outputs use the reviewed `c96f91b3...f3b31` identity.

## One changed-source development confirmation

`--real-dev-ranked-review-trace` has a separate schema and twenty-library
identity profile. All candidate files are pinned before selector access.
It reuses endpoint-v2's first-12 selection, panel preparation, actual native
child process, bounded CPU OCR, literal numeric association, printed-X and
terminal accounting. Historical modes and pins are unchanged.

The actual invocation used the source App, the reviewed 0.4.54 OCR assets and
`artifacts/native-engine/release/graphreader-engine.exe`, with explicit opt-in
and `E:/Graph-auto-reader/data/manual data` as the corpus root. The release Rust
worker matches the earlier trace exactly. A different debug executable was
noticed during preflight and was not used. No unchanged private run occurred.

The trace ran once, from `2026-11-15T16:55:39.1428646Z` to
`2026-11-15T16:56:17.6927119Z`: exit zero, empty stderr, **38.5206088 s wall**.
All twelve projects and panels finished, with eleven prepared panels and one
genuine no-panel full-image fallback. There were no execution exceptions,
timeouts, unsupported panels, OCR failures, unfinished or unattempted panels.

| Aggregate | Prior endpoint trace | Current ranked review trace |
| --- | ---: | ---: |
| Geometry/OCR panels | 7/12 | 8/12 |
| No geometry | 5 | 4 |
| OCR regions / literal numeric rows | 117 / 72 | 128 / 79 |
| Associated rows, including estimated | 63 | 70 |
| Complete session drafts | 0 | 0 |
| Printed-X review drafts | 2 | 2 |
| Partial X / partial Y drafts | 0 / 2 | 0 / 3 |
| Panels using estimated positions | 1 | 2 |

All five native attempts are accounted for: one returned an explicitly warned
review proposal, three exceeded the unchanged pair budget, and one completed
search had rotated rank-zero geometry and was rejected. The adapter did not
substitute a lower-ranked axis-aligned candidate. The recovered proposal's
additional associations are estimated label-center positions, not measured
ticks or validated scientific values. No complete-calibration improvement is
claimed. Existing geometry and printed-X review still need human confirmation.

Overlapping proposal-reason panel counts were: duplicate association 1,
inconsistent spacing 2, insufficient pairs 5, invalid numeric label 2, invalid
range 2, nonmonotonic 2, observed zero required 2, and unpaired label 3.
Stage totals were preparation **1,890.4526 ms**, axes **11,657.4785 ms**, OCR
**23,820.2095 ms**, and association **68.5142 ms**. Median OCR time was
**2,368.2245 ms**, maximum **7,055.7589 ms**. These are single-run CPU timings
with changed coverage, not a default-device speed comparison.

Assignment `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`,
inventory `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`,
and selected-source digest
`4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1`
match the previous fixed sample. All 51 independent cases stayed excluded;
sealed payload reads, supplied geometry/values, truth scoring, image writes,
training, Apply and export were zero. No unchanged451 or Mirage rerun occurred.

## Identity, integration and retention

This source batch is based on verified checkpoint
`2ca493364947243deb4fcff84eaa605efcd2c223`. Its runtime identities are:

| Item | SHA-256 |
| --- | --- |
| App EXE | `8e6df066fb568b598b75fd6ffe029570226c5d5196184ea3189e9caeab890024` |
| App DLL | `b40dc90263c7e143337fb10d7092dd37b34557020923f5e168b6f7f5fce646b4` |
| Probe DLL | `80857d07bf82af3a755d9f89d2198672513bba2c475cc46537a12ffb66db44a3` |
| Rust worker | `9c5ac19abc3b08632d82b5590a9e59f2794d94f92f345fbc70741e181808c0d9` |
| OCR model | `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` |
| OCR manifest | `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a` |
| Development report, 64,484 bytes | `f199665178c7762a6b5ab34738126b479d5076730a8d690274eafdc8938aa0ee` |
| Final focused TRX, 122,621 bytes | `a6113b398d69b6eaca30da5d3db6b08e6c7d12576d35fd513bdc2eae06d81ec2` |
| Intermediate TRX, 52,527 bytes | `b16e776caed5c252002fb1dae39841fa6f32f0da32a22a50720bc83bbdbaaa7f` |

The full runtime map is in `RealDevRankedReviewTracePins.cs`. No dependency,
weights, license, frozen schema, version increment or package was added. Source
remains Apache-2.0. The sole delivered 0.4.54 preview is unchanged and does not
contain this unpackaged change. Full Windows CI
[43738490205](https://github.com/SKang393/Graph-auto-reader/actions/runs/43738490205)
passed on integrated source `1ca57645e2eda111143ce9c09895f8ef661db482`:
all 13 .NET suites, **2,999 passed, 30 skipped, zero failed**, including
1,175 App tests and 204 Axis tests; **64/64 Rust tests** and all applicable
localization, packaging and policy checks passed. Release jobs were skipped.

After the source/outcome reached `origin/main` and integration/diagnosis
consumers finished, `artifacts/native-ranked-proposal-check` and
`artifacts/native-ranked-review-trace` were retired at
`2026-11-15T17:13:46.1369065Z`: five files, 240,011 bytes. The recorded report
and TRX hashes were rechecked before deletion; both scopes are absent. Original
data, reviewed runtime assets and independent evaluation inputs remain intact.
The next bounded work is original-pixel tick evidence for an exact native
review box; it must not silently move geometry or infer absent tick marks.
