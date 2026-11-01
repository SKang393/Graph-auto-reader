<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Manual training-axis OCR diagnostic

This local diagnostic runs the existing numeric and official OCR readers on
visually selected axis-label boxes from the authorized manual training catalog.
It uses the application's original-pixel cropper and the pinned reader models;
it does not detect boxes, train a model, change production composition, or
approve a release. Input, labels, source images, crops, and per-case predictions
are private ignored artifacts under the project folder.

Eight distinct build-partition source groups supplied 76 numeric labels. An
assistant visually checked each printed value and proposed box; the labels have
not received independent expert adjudication. They are incomplete, unmarked
regions are not negatives, and overlap with historical or sealed sources has
not been fully established. The private input supplied boxes and image hashes
without transcriptions; scoring joined the separately stored labels afterward.

| Reader | Exact | Wrong | Empty |
| --- | ---: | ---: | ---: |
| Numeric V5 | 30 | 8 | 38 |
| Official PP-OCR V8 composition | 75 | 1 | 0 |

The one official-reader error included a trailing mark. These numbers concern
known boxes on training images and cannot establish automatic detection,
generalization, model approval, or end-to-end product readiness. Two repeated
CPU inference passes produced identical predictions; neither performed training.

The private label manifest SHA-256 is
`868f256c1bbabc635585dbda27d9de01c472e17c30edfe54da2b8b0ffd377e2d`;
the truth-blind input is
`06d77e4621e536ea3328e6baed894d2a8f449bc5d6c2534df0c0f31dba2b8d81`;
the predictions are
`3038724d0940e195eed3ac2cf548ae1af063e7d9081cd359c2abd86a805e81f6`;
and the aggregate score report is
`e43ccc110e7f30265422be97bdd9155b5efcf28b62697ed7b048a626b6e0777c`.
The executed diagnostic assembly SHA-256 is
`34946d79f99c3de07a7a3360472b4f4d4d55fdd7488ae4261ccf4889177beadb`.
The active OCR box-localization investigation retains these private artifacts;
retire them when that investigation no longer needs them.

The Release build and `--self-test` passed. The run used:

```powershell
dotnet build tools/GraphReader.ManualAxisOcrDiagnostic/GraphReader.ManualAxisOcrDiagnostic.csproj -c Release --no-restore
dotnet tools/GraphReader.ManualAxisOcrDiagnostic/bin/Release/net10.0-windows/GraphReader.ManualAxisOcrDiagnostic.dll --self-test
dotnet tools/GraphReader.ManualAxisOcrDiagnostic/bin/Release/net10.0-windows/GraphReader.ManualAxisOcrDiagnostic.dll --expected-input-sha256 06d77e4621e536ea3328e6baed894d2a8f449bc5d6c2534df0c0f31dba2b8d81
```

The output path is create-only. Reproducing the run requires a fresh output
location and the exact private input; the tracked repository does not contain
the manually reviewed labels or images.

The runner also accepts `--input <project-relative-path>`,
`--expected-input-sha256 <hash>`, and `--output <project-relative-path>` for
another bounded input and create-only output inside its diagnostic directory.
The automatic-box candidate using that entry point is documented in
`ml/ocr/stroke_seeded_xtick_v1/recognition_diagnostic/manual_y_axis_candidate_v1/RESULT.md`.
The optional `--dense-tick` crop profile and its small, train-only comparison
are documented in [DENSE_TICK_RESULT.md](../GraphReader.ManualOverlayCapture/DENSE_TICK_RESULT.md).
The comparison with the candidate server recognizer is documented in
[REVIEWED_AXIS_SERVER_RESULT.md](../GraphReader.ManualOverlayCapture/REVIEWED_AXIS_SERVER_RESULT.md).
