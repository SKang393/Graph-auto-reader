<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Bounded spatial enumeration and jitter-aware label rows

Source baseline: `d61e223c27a38ac4798043e2daf5a677104a8acc`.
This batch changes deterministic geometry enumeration and measured-tick OCR
grouping. It introduces no model, dependency, fitted threshold or training.
The sealed-run budget and actual sealed payload reads are zero.

## Defects and implementation

The preceding fixed development trace retained five geometry failures,
including two cheap-envelope budget failures. An owned 17-by-17 family fixture
with only one genuine overlap exhausted B squared at B=16 before reaching its
single plot. Both original and reversed inputs failed; unrestricted enumeration
returned the complete candidate set. The bounded path now builds an interval
index only when the Cartesian product exceeds B squared. Inclusive envelope
overlaps preserve possible contacts, and original vertical-family order is
restored before existing intersection/contact/scoring logic runs.

Construction comparisons, tree population, query visits and result-order
comparisons share the unchanged B-squared budget and check cancellation.
Exact contact remains bounded by B, which is 128 in the worker. Exhaustion
publishes no partial geometry. Unrestricted and previously passing Cartesian
paths retain their prior behavior. Independent review caught initially
uncharged tree population/order sorting; those operations are now charged.

A separate owned OCR control has three 3-by-8 glyph components with tops
75/76/75 and measured ticks at 31/51/71, also tested with intervening minor
ticks. The old row-support check required every glyph to span the full union
height, merging all three into one 43-by-9 crop. The fix requires the shared
row and the existing 0.35 vertical-overlap fraction over the union. This retains
the three original component rectangles while preventing short punctuation
from establishing a tick lane. No-context behavior is unchanged. Calibration
OCR bypasses persistent stage caching, so no unrelated cache key was changed.

## Component validation

All executed tests used normal-host VSTest IPC, Debug, serial compilation,
`--no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false`.

| Check | Result | Test / whole-command time |
| --- | --- | --- |
| Spatial owned red, original/reversed | 0 passed, 2 failed, 0 skipped | 120 ms / 8.437 s |
| Initial indexed suite | 24 passed, 0 failed | Recorded before boundary/accounting additions |
| Inclusive-boundary suite | 26 passed, 0 failed, 0 skipped | 750 ms / 7.686 s |
| Final accounted-index suite | 27 passed, 0 failed, 0 skipped | 669 ms / 6.771 s |
| OCR row-jitter red | 0 passed, 2 failed, 0 skipped | 117 ms / 3.7187 s |
| OCR grouping/rotation/coordinate guards | 38 passed, 0 failed, 0 skipped | 577 ms / 19.8057 s |
| Full Axis suite | 162 passed, 0 failed, 2 opt-in worker skips | About 2 s |
| Full OCR suite | 899 passed, 0 failed, 16 opt-in artifact skips | About 8 s |

The two full component commands took 37.9375 s together. Optional artifact and
sealed checks were not enabled. The later actual-worker control and development
trace are separate evidence, not replacements for the skipped test cases.

Void attempts: the first spatial fixture invocation and the first OCR red
invocation ran inside the sandbox and could not establish VSTest IPC; no tests
executed. OCR's void command took 49.5901 s. Axis compile-only attempts stopped
on CA1859 (`FindOverlaps` return type, 7.512 s), CA1859 (`overlaps` local type,
7.080 s), and CA1861 (test constant array, 4.354 s). Concrete types and a static
readonly array repaired those analyzer errors before the passing runs.

```powershell
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj --configuration Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --logger 'trx;LogFileName=full-axis.trx' --results-directory artifacts/spatial-axis-ocr-check
dotnet test tests/GraphReader.Ocr.Tests/GraphReader.Ocr.Tests.csproj --configuration Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --logger 'trx;LogFileName=full-ocr.trx' --results-directory artifacts/spatial-axis-ocr-check
```

## Actual owned worker comparison

The generator's opt-in `-DenseXTicks -JitteredXLabels` renders X labels 1/2/3
at horizontal positions 140/160/180 and row positions 403/404/403. The 640-by-480
PNG is 4,042 bytes. No font binary is copied. The request supplies only the
image and full-image crop; expected plot/tick geometry is not supplied to the
worker. Rust detects the axes and ticks before literal CPU OCR and association.

Only the OCR DLL changed between runs. All other runtime EXE/DLL hashes,
image bytes, model and request were verified unchanged. Geometry and the
three original Y-label records are identical between the results.

| Result | Before | After |
| --- | ---: | ---: |
| Automatic X / Y ticks | 3 / 3 | 3 / 3 |
| OCR regions / numeric rows | 4 / 3 | 6 / 6 |
| Associated labels | 3, all Y | 6, both axes |
| Complete automatic review draft | No | Yes |
| Estimated positions / user-entered session / applied | No / No / No | No / No / No |
| Invocation wall time | 2.6915 s | 2.9943 s |

The before result reports invalid numeric evidence and insufficient pairs;
the after result has no reasons. All six after values match the owned printed
strings. This is a functional improvement, not a speed claim. CPU policy remains
one thread, one worker, BelowNormal priority, verified 0.4992-logical-core budget
on this 12-logical-processor host. No persistent cache was used.

One preliminary request used camelCase instead of the probe's required
snake_case keys. It exited before image processing with `JsonException` in
0.2411 s. Correcting the request schema did not change the image or experiment.

## Fixed development confirmation

The new `--real-dev-spatial-trace` has separate runtime pins and preserves all
historical routes and hashes. Its self-test and the existing printed-X self-test
passed without private reads. The first probe build took 7.69 s; review then
aligned its new sanitized failure schema with the success schema. The corrected
build took 7.70 s with zero warnings/errors, and its self-test took 1.33 s.

One current-runtime run completed all 12 fixed development projects/panels in
**37.8658 s**, exit 0, empty stderr. Eleven panels were prepared and one used
the genuine full-image fallback. Seven returned OCR and five failed geometry;
there were zero OCR failures, unsupported panels, other panel failures,
unfinished panels or unattempted projects/panels. The 51 reserved projects
were excluded. No images were written and no truth scoring, Apply or export ran.

Coverage did **not** improve: two measured printed-X review drafts, zero complete
session drafts, zero partial-X and two partial-Y drafts, one panel using
estimated positions elsewhere, 117 regions, 72 numeric rows and 63 pairs.
These aggregate counts match the preceding printed-X trace. The previous raw
result was already retired; no new byte-for-byte per-panel equality claim is made.
The five native attempts returned no geometry: three envelope-budget failures,
one exact-pair-budget failure and one multiple-candidate rejection. Previously
the first two categories were two and two. Failure categories shifted without
recovering a panel. Proposal-reason counts remained unchanged.

Stage totals: preparation 2,147.3199 ms, axes 11,499.2733 ms, OCR 22,966.3652 ms,
association 56.0248 ms. Median OCR panel time was 2,405.9524 ms, maximum
6,793.1432 ms. These single-run timings do not establish a controlled speedup.

| Identity | SHA-256 |
| --- | --- |
| Owned PNG | `2a2ab05bc2b9c8b46f11f127e55513b5ee388be5073e28f231200c164b3e6251` |
| Owned request | `b6eee9c6e9a19eae64198239c4884531968110ac2b3296799e3e01c789147a25` |
| Owned before / after reports | `5f656dd5aa86882833d6f958ad1d07cc682fe5962499464213d2ca5ed1c016b4` / `b341a3a09525adb29f663c1409b0969433a99dc3f3393b8d0ab161472d3f94bf` |
| Before / after OCR DLL | `d7700f81450b933c73bc8d7a5a6105e324111c87112589c105dc9015822b5983` / `43a692c26a0c557903cbd0a59d880e61b72e00968d83b82292844664a6851e0d` |
| Spatial red / final focused TRX | `3c8459c9cc7194afa102c2981d94f7765c385033cbb48bfc037469d7c8270a83` / `ab412d6e0c8a529543033add4ace1fe445381a57f1defddae6610fc02dd6fb52` |
| OCR red / focused TRX | `000c2ba3ab9adacef789e81bb4c12bc32e8599c50fff49d2f97cc531c1ed16fc` / `ecc162d017aee951b1107fccf4f88f6abc3c5e5895c660f6bbf331e16cc6679b` |
| Full Axis / OCR TRX | `d689775aeaaec5e034a97f403a403186ca85e90ba6cc6f7184421f8e97af5bd4` / `1d2545b75bff5b8b2a25ca553331b1cf2a5be09708bfece083d90fa022bbe33e` |
| Final probe DLL | `0f1eb3de00fe904e4fd0dbc431f79775266dcc7f645f959b2151aa6a151b2344` |
| Development report, 55,352 bytes | `f94584d2c99053088e1419269dfddf5dbe32e9bd29caaf1dd247829a95556c05` |
| Assignment / inventory | `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a` / `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d` |
| Twelve-source digest | `4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1` |

The complete 20-file runtime identity is in `RealDevSpatialTracePins.cs`,
including the reviewed native OpenCV library, unchanged Rust worker and OCR
model/manifest identities. No private pixels, truth rows or predictions belong
in this record or Git.

## Acceptance and next work

Owned invariants and component checks pass; real automatic calibration remains
incomplete and no model/stage production approval follows. The next bounded
geometry defect is insertion ordering: 182 descending envelopes require 16,471
comparisons, exceeding the shipped 16,384 budget before any query. This is a
source-level worst case, not a diagnosis of the private cases. Verify a stable
budgeted merge sort with an owned shipped-limit reproduction, preserving all
geometry, exact limits and rejection behavior. Do not rerun unchanged OCR.

Project additions are Apache-2.0; existing dependency/model licenses are
unchanged. No new package is created; the sole retained package is 0.4.52.
Full Windows CI [43704849910](https://github.com/SKang393/Graph-auto-reader/actions/runs/43704849910)
completed successfully on `7d9e5f31fc2dd22af7038089b3f70519b6c5081d`.
This verifies the integrated batch, not later source changes.
After this source/outcome batch is pushed, completed
`axis-spatial-query-check` and `x-tick-row-jitter-check` outputs are disposable.
Keep `spatial-axis-ocr-check/real-dev.json` only for the active geometry diagnosis;
retire its remaining completed owned/test outputs after documentation is pushed.
Keep the current headless runtime for that consumer. Preserve originals,
independent data, current model assets, user Data and the sole packaged build.
