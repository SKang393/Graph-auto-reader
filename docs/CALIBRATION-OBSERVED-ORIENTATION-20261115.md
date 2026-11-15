<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Observed calibration text orientation

## Failure and bounded repair

`ConnectedComponentTextRegionDetector.ToRegion` assigned a quarter turn whenever
a component's height exceeded 1.4 times its width. Calibration passed that angle
directly to `OcrCropBatcher`. An ordinary upright, asymmetric 5-by-11-pixel owned
digit 7 was therefore rotated and resampled. Its first expected black sample
`0` became `0.27272728`. A separate genuinely clockwise-rotated three-glyph row
correctly returned its exact original upright pixels.

The unchanged-source reproduction ran two tests: one expected failure, one pass
in 2.7653921 seconds. This establishes a preprocessing defect, not a measured
OCR reading error or an explanation of private aggregate failures.

The smallest opt-in repair adds
`UseObservedVerticalGroupingOrientation`, default false. When enabled, a quarter
turn requires an actual merge in the existing vertical-glyph grouping stage.
A single tall glyph remains upright. Existing grouping thresholds, components,
pixels, region IDs and original-coordinate polygons are unchanged. The default
aspect-based behavior and its configuration fingerprint remain unchanged.
The opt-in fingerprint appends `:observed-vertical-orientation-v1`.

Only `CalibrationOcrSession` enables this option. Existing approved/default
detector compositions are not switched. The historical
`PreferWideHorizontalModelOrientation` experiment in
`GraphStructureConsensusTextRegionDetector` concerns model-polygon evidence and
remains restricted; its two exposed cases were not reused here.

## Owned files and checks

- `src/GraphReader.Ocr/ConnectedComponentTextRegionDetector.cs`: opt-in option,
  internal observed-group flag and configuration identity.
- `src/GraphReader.App/Integration/CalibrationOcr/CalibrationOcrSession.cs`:
  calibration-only activation.
- `tests/GraphReader.Ocr.Tests/OwnedDigitOrientationReproductionTests.cs`:
  upright pixel sampling, genuine rotation with both option values, and default
  orientation/fingerprint/coordinate isolation.

The initial sandbox test command compiled successfully but executed no tests:
VSTest failed to connect to its host after 90 seconds, total 106.1031581 seconds.
The reported PID 100624 no longer existed when checked explicitly. Normal-host
execution of the same compiled tests produced the failure/pass evidence above.
No host settings or timeout threshold was raised.

Final command, from the active worktree with existing E-local CLI home/cache and
normal-host test IPC:

```text
dotnet test tests/GraphReader.Ocr.Tests/GraphReader.Ocr.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false -p:OutDir=E:/Graph-auto-reader/artifacts/goal22-worktrees/resume-20261111-phase2/artifacts/owned-digit-orientation-check/bin/ --filter "FullyQualifiedName~OwnedDigitOrientationReproductionTests|FullyQualifiedName~GraphReader.Ocr.Tests.CropAndMaskTests|FullyQualifiedName~GraphReader.Ocr.Tests.TextRegionDetectorTests|FullyQualifiedName~AxisLabelGroupingReproductionTests|FullyQualifiedName~FramedLegendTextRecoveryTests" --logger "trx;LogFileName=owned-orientation-final.trx" --results-directory artifacts/owned-digit-orientation-check/test-results
```

**85 passed, zero failed/skipped**, 17.9489092 seconds wall; test duration about
one second. `git diff --check` passed. SHA-256:

- Before TRX: `4c261d9f66295e743efa05ce8402cf79a517b3668f82064e2d01cb59bd27b8d4`
- Final TRX: `6a57798154c6a8b7c9e92e3b4fc19ca4dbd921bb04b31f9df918857bcab1fc26`
- Test OCR DLL: `972fe10cdaecfe3237a9ddb1cea17fd8c29e123bc3fc4ab91bc2bb33b2d5e635`

## Actual owned reading check

New `tools/GraphReader.CalibrationOcrProbe/CalibrationOrientationReadingCheck.cs`
and one Program route reuse the reviewed manifest parser, production crop options
and existing CPU runtime. Probe-only build passed in 8.5754901 seconds, zero
warnings/errors, with `BuildProjectReferences=false` and
`OutDir=<active>/artifacts/owned-digit-orientation-check/probe/`. Verified 451
dependencies were staged beside it, retaining only the changed source OCR DLL;
the model was not copied. Actual command:

```text
GraphReader.CalibrationOcrProbe.exe --owned-orientation-reading-check E:/Graph-auto-reader/artifacts/dev-portable/builds/0.4.51-20261114T235320548Z-e5491791/review-assistance/ocr
```

One fixed Segoe UI 16 digit row and clockwise-rotated `777` row were rendered in
memory. Default/opt-in used identical pixels, IDs and boxes in one recognizer
session. Exit 0 in 5.767083 seconds, verified 0.4992 logical core/BelowNormal,
one thread/session, initialization 1793.0928 ms. Both paths read exactly
`1,2,3,4,5,6,7,8,9`, all numeric and without failures: **no literal-reading gain
was demonstrated**. For digits 1/3/5, unsupported -90 angles became 0; confidence
changed respectively 0.938682/0.996586/0.997242 to
0.999997/0.999980/0.999973. Other digit readings/confidences were unchanged.

The genuinely rotated control kept angle -90 and identical crop hash
`2e8cb133752ae573821258ee1f43a14659740b2c4d62b4d9dae54ddf9ec17b4c`.
Both returned raw `7 7 7`, confidence 0.934588, no recognition failure; the
existing whitespace guard correctly returned numeric false/value null.
Recognition default/opt-in took 1158.2233/1121.1798 ms for the digit row and
54.0892/56.1894 ms for the rotated row. One sequential pass is not a controlled
speed comparison or steady-state measurement. Stderr contained 13 existing ORT
unused-initializer warnings, 3087 bytes, not inference errors.

SHA-256: probe DLL
`a92f4be4de8e250ab205a652494442c902e4e9d621566a818326048619cd6263`;
reading report
`00fadf2fdc02c97f583ebf2574c0d5eb2f0eb784330a6906e769503d0c37442a`.
The report binds exact runtime/model/manifest and both raw-pixel source hashes.

## App integration

The session-level regression sends an asymmetric upright glyph through
`CalibrationOcrSession` to an inspecting reader, preserving original bounds
and upright crop ink. The coordinated command compiled the current App and
passed 200 calibration/marker tests, zero failed/skipped, in 75.1885191 seconds
(about four seconds in tests):

```text
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter "FullyQualifiedName~Calibration|FullyQualifiedName~MarkerExemplarBatchViewModelTests" --logger "trx;LogFileName=app-calibration-integration.trx" --results-directory artifacts/owned-digit-orientation-check/test-results --verbosity minimal
```

TRX SHA-256:
`ee0bd3355d72b2e145372a2d162898c501d6daeeec0c68316c77e2c49c80172d`.
Independent source review found no actionable issue; it did not rerun tests.
This establishes source integration, not updated-package native acceptance.

## Limits and retention

A rotated single glyph has no demonstrated direction evidence and is not
claimed solved. This check
does not establish representative OCR accuracy, private coverage or promotion.
No private read, tuning, package, dependency, schema or license change occurred.
Source remains Apache-2.0, with no font binary copied. Retain
`artifacts/owned-digit-orientation-check` (231 files / 145,252,904 bytes,
compiler dependencies and reports, zero image files) only until the pushed
outcome and integrated consumers finish, then retire it. Preserve the current
package, original corpus and other tasks' work.
