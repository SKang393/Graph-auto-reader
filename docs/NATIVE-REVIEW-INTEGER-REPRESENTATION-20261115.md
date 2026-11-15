<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Preserve distinct, representable native review choices

## Failure and implementation

The [unchanged observer diagnostic](NATIVE-REVIEW-REJECTION-TRACE-20261115.md)
completed two native searches but rejected both entire lists at an integer
representation failure. Five other native searches exhausted their existing
128-pair budget. This revision addresses representation only, after complete
enumeration; it does not change detector thresholds, budgets, ordinary automatic
suggestion, models, selection, calibration or export.

The adapter still rejects malformed/nonfinite/out-of-bounds geometry and excludes
unsupported rotation. It now excludes an individually unrepresentable rounded
box without clamping or expanding it. Distinct subpixel hypotheses can also map
to identical integer rectangles. Keeping all such rectangles violates the
existing client uniqueness check, so the adapter retains the first ranked
occurrence of each exact `(Left, Bottom, Right, Top)` tuple and assigns contiguous
review indices. It does not merge nearby boxes or rank by private answers.
Two observation counters expose rounded and duplicate exclusions. Incomplete
enumeration still publishes no prefix.

The acceptance property is preservation of all distinct representable choices,
in ranked order, with unchanged malformed-geometry and incomplete-search guards.
Owned controls establish that property. The fixed development sample confirms
the representation repair only, without accuracy grading. Sealed-run budget is
zero; unchanged451, Mirage and all earlier profile identities remain frozen.

## Owned regression results

| Check | Before repair | After repair |
| --- | --- | --- |
| Rounding collapse at first/last rank, both input orders | Four expected failures | 46/46 focused tests passed |
| Six representable hypotheses mapping to two exact boxes, both input orders | Two expected client-validation failures | 48/48 focused tests passed |

All focused runs had zero skips. The duplicate control uses one long baseline,
two one-pixel support segments strictly inside the existing four-pixel merge
envelope, and two vertical families. The actual detector emits six snapshots;
the unfiltered result fails `OpenCvAxisProposalService.ValidCandidates`. The
repair returns two valid boxes, preserving first-ranked geometry and contiguous
indices. Assertions include observer-on/off serialized parity and the exact
four duplicate exclusions. Rounding controls retain two valid choices while
excluding one collapsed box at either rank. No production threshold changed.

Rounding red test: 204 ms / 2.4949125 s wall, build 19.7553175 s. Its green
suite: 721 ms / 3.4632831 s wall, build 98.9822442 s. Duplicate red test:
178 ms / 2.1626909 s wall, test-only build 9.3287104 s. Final green suite:
821 ms / 2.8054586 s wall, production-reference build 46.0497182 s.
Builds completed without warnings/errors. The probe-only build took 8.44 s;
`--real-dev-rounded-review-observation-self-test` passed in 0.570 s with zero
private reads. All twenty probe runtime libraries matched their literal pins.

Commands used the existing normal-host .NET/cache environment, one compiler,
no restore/shared compilation, and reviewed OpenCV restored to App/test roots:

```powershell
dotnet build tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Debug --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false
dotnet vstest tests/GraphReader.App.Tests/bin/Debug/net10.0-windows/win-x64/GraphReader.App.Tests.dll '/TestCaseFilter:FullyQualifiedName~NativeAxisCandidateReviewTests|FullyQualifiedName~NativeAxisWorkerDetectionFailureTests|FullyQualifiedName~OpenCvAxisProposalTests|FullyQualifiedName~RustFirstAxisProposalServiceTests' '/Logger:trx;LogFileName=focused.trx' '/ResultsDirectory:artifacts/native-duplicate-eligibility-check'
```

Red builds additionally used `-p:BuildProjectReferences=false` and the individual
new regression filter. Probe compilation also used that property to avoid
rebuilding the frozen runtime. A no-inline helper separates new observation
properties from historical profile paths; compatibility with historical runtime
bytes was not rerun and is not claimed.

## Fixed development confirmation and full failure accounting

Each invocation used the same first twelve sorted anonymized cases from the
120-case development assignment, eleven prepared panels and one full-image
fallback. The 51 independent cases were excluded. All three invocations finished
12/12 projects and panels; zero were unattempted, unfinished, unsupported or timed
out. No OCR, truth scoring, image writing, candidate selection, Apply or export
occurred; sealed payload reads were zero.

| Aggregate | Observer baseline | Rounded-only intermediate | Rounded plus exact dedup |
| --- | ---: | ---: | ---: |
| Review choices available | 4 | 4 | 6 |
| Review unavailable | 8 | 6 | 6 |
| Panel execution/validation failures | 0 | 2 | 0 |
| Native attempts / observed | 7 / 7 | 7 / 7 | 7 / 7 |
| Complete / incomplete native enumerations | 2 / 5 | 2 / 5 | 2 / 5 |
| Complete-list snapshots checked | 126 of 180 | 180 of 180 | 180 of 180 |
| Eligible returned native choices | 0 | 0 after client validation | 79 |
| Rounded exclusions | Not yet supported | 9 | 9 |
| Exact duplicate exclusions | Not yet supported | Not yet supported | 18 |
| Wall seconds | 16.7229137 | 18.0919959 | 11.500166 |

The rounded-only invocation exposed the existing duplicate-list rejection and
is a failed intermediate result, not an availability success. Both affected
panels reported `PANEL_FAILED / INVALID_DATA`. That defect was then reproduced
with the owned six-hypothesis control above before the exact dedup repair.
Its observer counted 97 eligible boxes, but none from those two lists survived
client validation. The final invocation checked all 180 complete snapshots:
74 rotated exclusions, nine rounded exclusions, eighteen exact duplicates and
79 eligible choices. Five unchanged pair-budget failures and one Rust ambiguity
remain. Rust success still supplies the same four one-choice lists.

The final invocation ran `2026-11-15T14:33:37.4328034Z` through
`2026-11-15T14:33:48.9989188Z`, exit 0, empty stderr. Preparation totaled
3,111.6292 ms and review calls 7,120.1774 ms. These single functional timings
do not establish a speed improvement. Native calls were in-process under the
probe CPU budget, with one OpenCV thread and the existing linked deadline;
they are not timing-comparable to historical child-worker profiles. The
intermediate panel timing rows omit failed calls, so they are not a total.

Assignment SHA-256:
`decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`.
Selected inventory:
`03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`.
Selected-source digest:
`4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1`.
All match the unchanged observer baseline. Both new invocations used the same
in-progress revision mode `--real-dev-rounded-review-observation-trace`, with
explicit opt-in and literal runtime hashes checked before selector access.
The final profile records the intermediate profile identity rather than silently
rebinding an earlier completed experiment.

## Source and result identities

This is an unpackaged source repair over `057ef8c6bb877db359459cd632916c99d211e532`.
The delivered 0.4.53 remains bound to `9adee655` and does not contain this repair.

| Evidence | SHA-256 |
| --- | --- |
| Final adapter source | `e92cf089dd87fa8ce095f72e023fa908e80b1fcb71f76b128d487c072661df8e` |
| Final adapter tests | `ddab8f60c6253fd05701fb8de097f379fd657936958275d083064932c2a15920` |
| Final App EXE | `f2b147da1520b78ef53a2d30d7d81c3601354b6a74b5799e38826a63fff2b16e` |
| Final App DLL | `8e25f20587f4390587159d4c9e09103a2277108af75eb1527bb068a8392f7c60` |
| Final Axis DLL | `f9152a45c9ee00d951973d5318db32c0ecd0335356e48c96b8abf8db49a4cb2d` |
| Rust worker | `9c5ac19abc3b08632d82b5590a9e59f2794d94f92f345fbc70741e181808c0d9` |
| OpenCV native | `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31` |
| Final probe DLL | `00436b9e16a5744d30bd78c5caf1c1dcbb96acad9f33faa125778aaa89a48b94` |
| Final probe profile | `193f44fb4f1f0cbf2f68368923d4d4835a34f1fb812b65857f0e4ab8c22b466a` |
| Rounded-only App DLL | `f82762ea8ff57f366ddf660e3ef1264e19057c2525ee44471f0ec6e3aa4a51b0` |
| Rounded-only probe DLL | `a3743d88a18c1cb2e094a3e5425f9e9bc0188cfa74b39d041993d5b4c91b2cde` |
| Rounded-only profile | `0f3ecac21f312a7fb0661820bd42981cda3ba8922f384ae1b0d19edd8dbd09a5` |
| Rounding red TRX | `da392c2766e165c43abb2b8867e9dfdf9986eba07ccd931f23d333c7032d8a25` |
| Rounding green TRX | `495efd711c1d5c4ab496012088de9f78ef346cfbb3fb40932296899e2c436075` |
| Duplicate red TRX | `8593d7610ece61b7432d6580563aed94bc9e87e21ba75a535a470660e9c7db63` |
| Final focused TRX | `63dd72554287cfb4c43fc1cd73e68fe18a662a87cd4c2214051c65afeb2ac557` |
| Baseline report, 14,857 bytes | `43bf79c2a5143d7be36ee0864143e35660a9857aaec189eeb6159c2a250b9aec` |
| Rounded-only report, 12,759 bytes | `6c03ee051a2dd80d0fa5dee2e21e67944530b4b454c6a1a492dfd8ec3c965c2a` |
| Final report, 15,267 bytes | `35fd32951de46208b6f9929144fa97027edadd35e3b674c595500161f1f558e1` |

## Acceptance, limits and retention

Owned representation invariants and the final bounded confirmation passed.
Full Windows CI [43725696430](https://github.com/SKang393/Graph-auto-reader/actions/runs/43725696430)
passed exact commit `c0f7a2f97f3fd74f82f0c6b92ed4cfa165f40b1f`: all thirteen
.NET suites, 2,969 passed / 30 skipped / zero failed, including 1,165 App tests,
plus 63 Rust tests. Localization, version/ledger/retention, evidence and packaging
checks passed; public release jobs were skipped. Review availability
is not correct-axis selection or automatic numeric calibration; 79 native
choices across two panels may still be costly to review. The remaining five
budget failures are not addressed by relaxing bounds. Existing pruning already
rejects impossible intersections and extents before charging the exact budget;
distinct source hypotheses cannot be discarded solely because their later
integer boxes coincide. No further detector change is justified by this run.

No dependency, model, license or public contract changed. Original source remains
Apache-2.0. No production approval or release was performed.

After this outcome and source reached origin/main in `c0f7a2f9`, the completed
raw outputs were retired from
`artifacts/native-rounded-eligibility-check` (six files, 83,781 bytes),
`artifacts/native-duplicate-eligibility-check` (six files, 80,792 bytes), and
the three `native-review-observation-dev-check`,
`rounded-review-observation-dev-check`, `distinct-review-observation-dev-check`
directories. Their only consumer was this comparison. The preceding observer
test output and completed Rust review integration/routing and native eligibility
checks were also retired. The ten exact directories contained 53 files totaling
4,195,891 bytes; cleanup completed `2026-11-15T14:45:05.7886117Z` after remote
scope, exact inventory, path/reparse and consumer checks. The other retired
scopes were `artifacts/native-candidate-observation-check`,
`artifacts/rust-review-integration-check`, `artifacts/rust-review-routing-check`,
`artifacts/native-eligibility-dev-check` and
`artifacts/native-candidate-eligibility-check`. Regenerate owned tests from source; do not rerun
unchanged real diagnostics simply to recreate disposable evidence. Preserve
originals, independent evaluation, active runtime/model assets and user Data.
