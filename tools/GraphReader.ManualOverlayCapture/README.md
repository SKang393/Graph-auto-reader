<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Manual overlay marker-input diagnostic

This diagnostic evaluates the existing unapproved candidate composition on the
authorized, train-only controlled overlays. It does not change the application,
approve a model, read sealed data, or run an optimizer. Its input preparer reads
the fixed overlay receipt, checks all 200 image hashes, and creates two private
raster manifests without label or coordinate fields. The 20-image smoke input
selects one overlay from each source group using the sorted filename and group
index; the full input contains all 200. The capture itself reads only the
raster manifest, image bytes, frozen candidate model bindings, and runtime
description. File names describe the injected shape, so the input is not a
blinded human review.

The `original` input also selects the same 20 source groups' unmodified
images from a checksum-verified train-only receipt. It contains no point labels
or coordinates. `original_score.py` reads the recorded points only after the
original capture is frozen, and compares the same eligible points with the
overlay score.

For each imported panel the capture uses the candidate workflow's original
raster decoder, axis detector, OCR adapter, artifact-mask composer, and V27
marker-center candidate. The OCR, artifact, and marker components are not
approved for production. ONNX inference uses the CPU provider with two
intra-operation threads; this is inference, not training. The reviewed OpenCV
binary and the running executable files are hash-bound in the runtime
description. Model manifests, payloads, licenses, and notices are checked by
the shared frozen-candidate model validator. This is not operating-system
attestation, a complete workflow run, or release evidence.

Each private gzip frame starts with little-endian `GRF1`, width, and height,
then three float32 planes: luminance, OCR mask, and merged artifact mask. The
runtime proposal adapter converts luminance to ink by subtracting it from one.
The stage-enabled rerun also saves a `GRM1` gzip containing the seed and
residual artifact-mask float32 planes and residual category bits. A separate
compressed stage record contains the plot polygon, emitted grid locations,
above-threshold decoded points, pre-suppression candidates, and stage counts.
Panel-to-source transforms, predicted centers, hashes, and failures are kept
in the private capture report. The scorer reads the controlled labels only
after that report is frozen. It scores centers in the modified patches and
never labels other image pixels as negative.

`native_score.py` separately compares predictions with recorded digitized
points outside each modified patch and a five-pixel margin. It uses one-to-one
matching within five pixels, excludes exact duplicate recorded coordinates,
and reports missing or ambiguous panel assignments. It cannot measure
full-image precision because other unrecorded marks may be legitimate.
`patches.py` extracts 33-pixel model-input proposals near the injected shapes,
with the four-pixel lattice and the runtime's ink and mask channels. Its build
and internal holdout files are separate by source group. Plot-domain membership
is not captured, and the extracted patches are diagnostic only, not admitted
for training. On one synthetic skewed-polygon fixture, the Python extractor and
the actual C# runtime produce identical bytes for 118 proposal patches,
including padded boundary locations. This does not establish parity for every
real input or verify that each captured real-image lattice point was emitted.
The rebuilt C# test and capture used the same `GraphReader.App.dll` SHA-256,
`763cd5717d5bc2759024a50163440198471b23e2cdd9c033c74c6839305a8789`.

## Smoke result

The 20-image smoke capture completed with no reported image failures. All 20
saved frames had nonzero OCR and artifact mask pixels. Seven of ten injected
markers had a detected center within five pixels; two of ten injected
distractors had a center within five pixels. This sample is small and the
modified white patches and simplified shapes are not representative of whole
real graphs.

For recorded points outside the modified patches, the marker-center candidate
matched 98/208 points in 15 build images and 52/69 in five internal holdout
images. The source data are imperfect and these are modified images, so the
result is a diagnostic rather than independent acceptance. Of 127 unmatched
points, 58 had artifact-mask values at or above 0.35 in a five-by-five window
around the recorded point; 50 of 150 matched points also had such overlap.
No unmatched point had equivalent OCR-mask overlap. Overlap is an observation,
not proof that a mask caused a miss. These measurements concern the center
candidate only, not the later classifier and recovery stages.

An additional local-confidence probe excluded points whose 33-pixel proposal
patch could touch the modified area. Among the remaining points without a
nearby final center, 62 had only local proposal confidences below the existing
0.1 operating threshold, while 59 had at least one local proposal at or above
it. This indicates both low local confidence and later filtering or geometry
need investigation. The probe did not check plot-domain membership, searched
only grid positions within three pixels, and did not one-to-one match final
centers. The patch extractor matches the C# runtime on the synthetic fixture
described above; these counts remain preliminary diagnostics, not causal
attribution.

As a counterfactual, the same raw model was evaluated with only the artifact
channel zeroed. Across 267 eligible points, 14 crossed upward and two crossed
downward at the fixed 0.1 threshold. Eight of the upward crossings had no
nearby final center and had artifact-mask content in their input patches. This
does not test postprocessing or false positives and does not justify removing
the artifact mask from the workflow.

The 20 images yielded 29 build patches and 14 holdout patches near the injected
shapes. They are not independent real-marker observations, and the smoke
holdout is especially small. No candidate was selected or trained from them.

## Full controlled-overlay result

All 200 images completed with no reported capture failures; all 200 saved
frames had nonzero OCR and artifact masks. The candidate detected a center
within five pixels of 58/100 injected markers and 5/100 injected distractors.
The build split was 45/75 markers and 3/75 distractors; the five-group internal
holdout was 13/25 and 2/25. These are controlled, modified patches, not
full-image precision or independent release acceptance. The 100 marker examples
repeat five shapes across only 20 source groups. Seven groups detected none of
their five marker overlays, and nine detected all five, suggesting that
graph-level geometry or proposal filtering merits further investigation.

All 20 images repeated from the smoke run reproduced the same panel hash,
compressed frame hash, and marker coordinates, radii and confidences. The
runtime description also remained byte-identical after the full capture. The
diagnostic patch extraction produced 290 build patches from 15 groups and 140
holdout patches from five groups. Those patches remain unapproved for training:
plot-domain membership and a bounded GPU training/evaluation protocol are not
yet established.

## Stage trace

The stage-enabled 20-image rerun reproduced the earlier panel bytes, merged
frame bytes, and marker geometry/confidences on all 20 images. Its recorded
seed and residual planes reproduce every merged artifact mask exactly. Among
the 127 recorded points without a nearby final center, 71 were more than 16
pixels outside the returned plot polygon; six of seven groups with zero of five
controlled-marker hits also had their smoke anchor outside that boundary.
Several white overlay patches visibly covered portions of an axis, so the
modified-image result cannot establish the same failure on the original
graphs. The separate unmodified-image check below replaces that assumption.

The local stage proximity patterns comprised 71 misses without a nearby
emitted grid proposal, 49 with a nearby grid proposal but no nearby
above-threshold decoded point, one with a decoded point but no nearby
pre-suppression candidate, and six with a pre-suppression candidate but no
nearby final center. These proximity checks are not linked proposal traces and
can associate different nearby proposals. Among missed points, 21 had seed-mask
overlap and 37 had residual-mask overlap in the local five-by-five window;
categories can overlap. The residual legend category occurred near 36 misses.
These observations are not proof of causation and do not measure false
positives or the later classifier/recovery workflow.

## Unmodified-image comparison

All 20 unmodified train images completed capture with no image failures. Before
the wider connected-ink bridge, the center candidate matched 163/253 recorded
points in 15 build images and 60/79 in five internal holdout images. On the
same 277 points outside the modified patches it matched 180 on the originals,
compared with 150 on the overlays. Two of the seven groups with no controlled
overlay hits also placed the tested anchor outside the original plot boundary.

After the bridge adjustment, those original-image matches increased to 181/253
and 61/79. On the same 277 points the original count increased to 198. One of
the seven zero-hit groups still had its anchor outside the original plot.
Across all original images the number of returned center candidates increased
from 1,707 to 1,913, with 197 of the 206 additional candidates in the two
images whose plot extents changed substantially. The recorded points are
imperfect and do not label all image content, so these additional candidates
cannot be classified as correct or false positives from this score. It does
not execute downstream marker classification or recovery and is not release
acceptance. See `../GraphReader.ManualPanelDiagnostic/SQUARE_BRIDGE_RESULT.md`
for the first axis adjustment.

The subsequent faint-ink adjustment recovered eight recorded points on one
internal holdout image. Matches rose from 242/332 to 250/332 overall, with
build unchanged at 181/253 and holdout rising from 61/79 to 69/79. Returned
center candidates increased from 1,913 to 1,933. Inspection of two train images
showed proposals on text, arrows, and connecting lines. These are center-stage
observations; the capture does not determine which proposals the later
classifier and exclusions would remove. The visible-axis repair and review
status are documented in `../GraphReader.ManualPanelDiagnostic/FAINT_INK_RESULT.md`.

| Original-image evidence | SHA-256 |
| --- | --- |
| Raster manifest | `b9044372d5d6b37257ef0e1823935eaed840b99f9dc5a66a2c5359a1b7beeba6` |
| Before capture | `436e45e9765698a2a17bb0d86a42981223399e29638539375a61e617d378381c` |
| Before point score | `56b5d400089cc8713b898a12ef972b6da1aa35aff4544dc4c7a17468581fdffd` |
| After runtime description | `f9a40e9bea7b3d11b4037e5aac8fc62b4e867eee757312e0e0e6e73872d6746d` |
| After capture | `d96efe9f3eb414f4163e927da7e61943fc73875b2f22d230e186cb9363cd5f9e` |
| After point score | `1687239ae043b205c1f7fec2f2eafb9eb7a673c9b7fbbd50ce54c6aa6f19fc90` |
| Faint-ink runtime description | `c7339cadbc26bd45c05b512513358029312100b1214c541b7d9be7b5cce681ba` |
| Faint-ink capture | `27d85db95b09e2c79f021de5d9cb4a65ac69c3fc82763d7b4656bdcaca28bfa2` |
| Faint-ink point score | `40ae0a929c0a63e8bae1b0894c908c0f14c83fab210a60711ed356e25eb79e26` |

| Private input or result | SHA-256 |
| --- | --- |
| Smoke raster manifest | `28f6480b72126ad8b1598cab758a6484a4ac2071daa12fc8986e47ef6ae6510f` |
| Full raster manifest | `f19a0c38020cfaaf0c3e557193815f1710a07dc64bef9aca450178002a46d608` |
| Runtime description | `3d84392767a66318bf03d897565f33e38cacd7d7dfe6a53fef02d94c05a026c3` |
| Smoke capture report | `7676aa3b8dc3e8c171d8db3fb19841c12d769cd68a4b00212d7ce16e9e53896e` |
| Controlled-overlay score | `be153a477f1a71fff2c38febbfff508368159bf1722839434a233dc9b02c34a9` |
| Native recorded-point score | `7773a19d4a345b58fddcd45e9be040112648d6e4d272b7b0641021d9fa24240a` |
| Local-confidence and mask-ablation diagnostic | `ba3aa2765ac6ded025c6c871d354544bad2a285270c4c2088481d495346d989d` |
| Smoke patch report | `b205b49df0bf512b97109e936453103d993980ebd14ae7b3268efbf8bcfab194` |
| Full capture report | `860ae730a4e5409335b28b9236952b9ca0ed166313d3ea9e5c73538864f46068` |
| Full controlled-overlay score | `ac02af58fb6fe1d79e07693e7c6fbe29eb750ccdd4307adc11cbafbb605643f4` |
| Full patch report | `9b91b7fd1fe791c0584bbd91559670e3adeaa8e068c7844a7d272bb568420ef2` |
| Stage runtime description | `f2479eb7c7f5496e7d4962e1436665781162330de9dd071a0460c6fc03d0a26e` |
| Stage capture report | `e6163654e958000182cc710cb1b54c882532282f53799147aa5291f3587e2ed4` |
| Stage aggregate | `69031b8aeadc193c43a6a7a7891ffefe35add5daa977ffdeb10ebffdf326fc1e` |

The frames, input manifests, scores, and temporary review crops remain
project-local while the marker-stage diagnosis and candidate follow-up consume
them; they are never packaging inputs.

## Checks

```powershell
dotnet build tools/GraphReader.ManualOverlayCapture/GraphReader.ManualOverlayCapture.csproj -c Release --no-restore -m:1 -p:NuGetAudit=false -p:UseSharedCompilation=false
python -B -m unittest test_score test_patches test_native_score test_raw_confidence_score test_stage_score test_original_score -v
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false --filter FullyQualifiedName~PlotDomainCandidateMatchesPythonSkewedCoordinatesAndPatchBytes
```

Run the Python command from this directory with the existing Python 3.13 and
NumPy environment. Captures additionally require the original authorized local
corpus and the checksum-reviewed candidate/runtime files. Results use fresh,
create-only output paths. All training, if later admitted, must use the
project-local RX 6600 XT launcher with no CPU fallback.
