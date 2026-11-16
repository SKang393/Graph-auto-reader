<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Review marker matches inside a confirmed tilted plot

Baseline: `2316841baf1e4be6444962f9584133cabcb472d8`.

## Observed limitation and isolated change

Reviewed affine calibration already maps original-pixel points into data values,
but marker assistance requires the scalar session lattice. Its service and UI
also construct a rectangle by taking independent X/Y anchor coordinates. Merely
removing the gate would search the wrong plot area. The existing matcher itself
uses only source pixels, an exemplar and a search region; it does not consume
scientific calibration. Matching examples from the same tilted image does not
require resampling those examples.

Add an optional original-pixel affine plot frame to the private marker request.
Use the enclosing rectangle only to bound the existing correlation work. Check
the seed and retained peaks against the actual parallelogram using its inverse
basis, before candidate capacity accounting. Keep exact request/response binding.
Preview must add no points. Explicit acceptance uses the existing original-pixel
point mapping, duplicate guard, Undo and persistence paths.

Apply the same marker-only confirmation proof to reviewed printed-X calibration
in aligned and tilted plots. The previous scalar-lattice restriction also blocks
this already supported calibration mode. Its marker acceptance must preserve
estimated-X export semantics and the separate ordinal observation index; printed
coordinates must not be reinterpreted as exact session or observation numbers.
Do not accidentally enable printed-X in only the affine branch.

Only the marker service, private contracts/geometry, validation, matcher, VM
eligibility and focused App tests change. The shared scalar-calibration gate,
phase workflow, full Auto Detect, PNG/template pixels, matching arithmetic,
score thresholds, worker limits, candidate cap and cancellation remain unchanged.
No model, dependency or frozen project schema change is proposed.

## Owned verification and boundary

Use fixed positive/negative two-degree and shear controls with reviewed anchors,
fractional seed positions, same-shape interior matches, and identical distractors
inside the enclosing rectangle but outside the actual plot. Cover boundary and
invalid/degenerate frames. Verify unchanged source/template bytes, preview-only
search, explicit individual/batch acceptance, correct data values, Undo/Redo,
one crop-to-source translation, save/reopen/export, and stale source/calibration/
seed/series/tab/cancellation replies. Include a printed-X acceptance/persistence/
export control. Retain aligned controls and existing
uninformative/overflow/watchdog behavior.

These are deterministic correctness checks, not model selection or real-marker
accuracy. Private and sealed evaluation use is zero. Run focused marker and
affine guards after integrated source review, then the required broader solution
check. One in-memory owned affine request will also exercise the actual child
worker and optional-frame wire protocol, separately from the in-process tests.
It uses supplied review geometry and fixed marker centers, with no OCR or
automatic-calibration claim. Native review remains subject to REV-011. Do not
package this intermediate source change or repeat private calibration comparisons.

## Implementation and local validation

The private request now carries an optional typed `AffinePlot`, omitted from
JSON when absent. Its actual origin/X/Y anchors and derived fourth corner stay
inside the original image. The matcher caches an inverse basis, filters centers
before the existing peak/candidate limits, and keeps the same correlation and
suppression arithmetic. The shared scalar gate remains unchanged. Marker-only
confirmation checks the persisted reviewed anchors in source coordinates,
avoiding an unnecessary inverse crop round trip. Aligned and affine printed-X
calibrations use this same proof.

Source review caught binary64 rounding at a declared fractional fourth corner:
an inverse coordinate could be `1.0000000000000002`. Exact declared vertices
and a documented arithmetic-roundoff bound on edge interpolation now retain
those boundary points. No pixel-sized plot growth or coordinate snapping is
introduced. Owned containment tests also reject actual outside points and
malformed frames.

The first full-solution Debug build passed with **zero warnings/errors** in
**182.7286645 s**. The focused run passed **241/241**, zero failures/skips, in
**20.786607 s**: App 239 and integration two. The one unchanged-output full
solution run passed **3,164**, with **32 expected optional-input/worker skips**
and zero failures across thirteen projects, in **173.2886653 s**. App passed
**1,296/1,296**. There were no failed build/test attempts. Commands were:

```text
dotnet build GraphAutoReader.slnx -c Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false
dotnet vstest <App.Tests.dll> <Integration.Tests.dll> /TestCaseFilter:<marker/printed-X/affine filter> /Logger:trx
dotnet test GraphAutoReader.slnx -c Debug --no-build --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --logger trx
```

The actual in-process matcher returned both interior centers for positive tilt,
negative tilt and shear. The unmasked enclosing rectangle admitted the third
outside distractor; affine filtering rejected it while keeping the two interior
scores identical. Raw images/templates and fractional centers stayed unchanged.
Optional-frame wire round trips, legacy null-field omission, preview-only state,
single/batch acceptance, duplicate marking, Undo/Redo, persisted confirmation
binding and aligned/affine printed-X crop/save/reopen/export checks passed.
Printed-X values remained estimated and observation order remained separate.
The in-process test's runtime receipt is fixture metadata, not evidence of a
real child-process CPU budget.

Focused TRX SHA-256:
`f97b9cd98fe7274872d3246459267e8a1bcf456150cfa44b98f9970570fd3734`;
full summary:
`8e5402303536e9dfec6a0a205ce5ee91a921363bd7d5de8c0f1d4d3a2eed28f8`.
Frozen twenty-library manifest:
`794d23093876bcaaa276c5fae44a42d90e4b4de94f7484e4df930b65e9487cbb`;
App DLL:
`296fad6626eedc5326aff159a1ecf3fbdf26c5c4acccd0f2597182d0359f766e`;
App EXE:
`e53a203473d186c50d9655fddb3cec354e0c21239156451a9d7dea6808a423c5`.
All twenty source/test runtime libraries matched. Reviewed OpenCV remains `c96`
and the Rust worker remains `2cc6f263` from the linked calibration record.
Private/model opt-ins, production approval and package version were unchanged.

## Actual child-worker check

The probe-only build passed first attempt with zero warnings/errors in
**12.51 s**. It verified all twenty App/probe runtime copies against the frozen
manifest and checked four loaded assemblies before calling the worker. The one
`--owned-affine-marker-worker-check <App-exe>` run started **2026-11-15
23:34:30.2596513 UTC**, ended at **23:34:31.5230484 UTC**, exited zero with empty
stderr, and took **1.2529057 s** process wall time.

The actual worker call took **698.6899 ms**: reported decode **34.1672 ms**,
matching **50.9374 ms**, total worker **85.1083 ms**. It returned exactly the
two expected original-pixel centers, excluded the explicitly checked outside
distractor, and returned the exact request echo. The 776-byte in-memory input
retained SHA-256
`3b1e368ba56674bdd9efdfc6fae49dbd302c6e0924b916baf689c7bc63088a5c`.
The real worker verified BelowNormal priority, one OpenCV thread, and a job CPU
rate of **416/10000 on 12 logical processors**, or **0.4992 logical cores**.
This is an interval-based aggregate cap, not a promise of zero short CPU spikes.
There was one worker call and no OCR, model, private-input, image-file, Apply or
export operation. The supplied geometry makes this a marker/protocol control,
not automatic geometry or real-graph accuracy evidence.

Probe DLL SHA-256:
`34112004e17fc43a5e51d1ec58d910dcff16b4d3a91a1f0e82421976458797c3`;
worker report:
`5674ead09e61d738b6dead1e39cdde9ea3bdf9778265248e00cdb42085613212`;
run receipt:
`07cbe71f31cd61e7cc18c7014f568cb8e32d912438aa628ac8b3da37acc7bf12`.
The report/run/empty stderr total **1,822 bytes**. Existing source Data's four
files retained their recorded hashes after the source validation work.

No dependency, model, license or frozen schema changed. New source uses the
project Apache-2.0 notices. Global full-Auto-Detect and phase restrictions remain.

Source/outcome commit `5920ebc2ddbf23dad73951c8e6e534cce9eeda30` is integrated
and pushed to origin/main. Its exact-head
[Windows CI run 43775835498](https://github.com/SKang393/Graph-auto-reader/actions/runs/43775835498)
completed successfully, **2026-11-15 23:37:40 to 23:47:38 UTC**. The main job
passed **3,166 .NET tests**, with **30 expected skips** and zero failures across
thirteen projects, plus **72 Rust tests** and **68 packaging verification tests**.
Localization, OCR-host, evidence, handoff, upload and packaging audits passed.
The two release jobs were skipped; no package or public release was produced.

Status: source integration, local validation, the actual worker check and full
Windows CI pass. Native QA is authorized but the tool still returns its
Escape-stop state; REV-011 records that execution blocker.

## Completed prior-output cleanup

After source/outcome commit `5920ebc2ddbf23dad73951c8e6e534cce9eeda30` reached
origin/main, these completed diagnostic outputs were retired under the active
worktree's `artifacts` directory. Their source and concise outcomes are recorded
above or in linked calibration documents; the old comparison consumer finished.
Resolved path containment, reparse exclusion, unchanged inventories and the
pushed record were verified before removal.

| Scope | Files | Bytes |
| --- | ---: | ---: |
| `affine-calibration-rectification-check` | 31 | 5,053,237 |
| `affine-calibration-dev-trace` | 3 | 3,837 |
| `affine-tick-observation-check` | 3 | 11,314 |
| `native-affine-dev-trace` | 3 | 14,962 |
| `interval-cluster-dev-trace` | 7 | 81,224 |
| `affine-calibration-owned-check` result/run/stderr only | 3 | 40,475 |
| Total retired | 50 | 5,205,049 |

Absence was verified at **2026-11-15 23:40:22.5191401 UTC**. Eight protected
files retained their hashes: the current App EXE, current runtime manifest,
marker-worker report, existing autosave and four owned PNGs. The four images,
**24,926 bytes**, remain inputs for waiting native review. Existing Data/settings,
originals, current runtime dependencies and the sole 0.4.55 package remain.
Temporary artifact hashes above are now historical identities.

After the exact-head CI result was recorded in pushed commit
`bb40265adc0a37b5906ed38f86a9e18ffae25d47` and all marker-output consumers
finished, `artifacts/affine-marker-assistance-check` (30 files / 5,113,833 bytes)
and `artifacts/affine-marker-worker-check` (three files / 1,822 bytes) were also
retired. The **33 files / 5,115,655 bytes** were checked for exact inventories,
resolved containment and reparse points before removal; absence was verified
at **2026-11-16 00:20:35.8960162 UTC**. Seven current runtime/receipt/owned-image
files and all four existing App Data files retained their hashes. Current manual
validation inputs, original data and the sole delivered 0.4.55 package remain.
