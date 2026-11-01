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

The 20 images yielded 29 build patches and 14 holdout patches near the injected
shapes. They are not independent real-marker observations, and the smoke
holdout is especially small. No candidate was selected or trained from them.

| Private input or result | SHA-256 |
| --- | --- |
| Smoke raster manifest | `28f6480b72126ad8b1598cab758a6484a4ac2071daa12fc8986e47ef6ae6510f` |
| Full raster manifest | `f19a0c38020cfaaf0c3e557193815f1710a07dc64bef9aca450178002a46d608` |
| Runtime description | `3d84392767a66318bf03d897565f33e38cacd7d7dfe6a53fef02d94c05a026c3` |
| Smoke capture report | `7676aa3b8dc3e8c171d8db3fb19841c12d769cd68a4b00212d7ce16e9e53896e` |
| Controlled-overlay score | `be153a477f1a71fff2c38febbfff508368159bf1722839434a233dc9b02c34a9` |
| Native recorded-point score | `7773a19d4a345b58fddcd45e9be040112648d6e4d272b7b0641021d9fa24240a` |
| Local-confidence diagnostic | `d635bd642967408fe122ba694e5ed72d914ee983111d3149a3704a142aa71b92` |
| Smoke patch report | `b205b49df0bf512b97109e936453103d993980ebd14ae7b3268efbf8bcfab194` |

The full 200-image capture is an active downstream consumer of the current
runtime and private inputs. The frames, input manifests, scores, and temporary
review crops remain project-local until the marker-input comparison and its
candidate follow-up finish; they are never packaging inputs.

## Checks

```powershell
dotnet build tools/GraphReader.ManualOverlayCapture/GraphReader.ManualOverlayCapture.csproj -c Release --no-restore -m:1 -p:NuGetAudit=false -p:UseSharedCompilation=false
python -B -m unittest test_score test_patches test_native_score test_raw_confidence_score -v
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false --filter FullyQualifiedName~PlotDomainCandidateMatchesPythonSkewedCoordinatesAndPatchBytes
```

Run the Python command from this directory with the existing Python 3.13 and
NumPy environment. Captures additionally require the original authorized local
corpus and the checksum-reviewed candidate/runtime files. Results use fresh,
create-only output paths. All training, if later admitted, must use the
project-local RX 6600 XT launcher with no CPU fallback.
