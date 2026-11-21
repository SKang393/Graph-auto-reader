# Scanned session-axis calibration

`scan-sessions.png` is a newly drawn, single-series synthetic figure with irregular
tick spacing, a grainy background, mild Gaussian blur and a JPEG round trip. Its
stored pixels use Gray8. The RGB control encodes the same gray samples in three
channels. Both runs score one graph.

`ScanSessionCalibrationTests` passes only the encoded panel to the OpenCV axis
worker. The pinned CPU OCR reader receives the detected plot bounds and the
image. Connected components and recognition produce the label polygons and
values. The fitter uses glyph centers when measured tick strokes are unavailable.
A test-only distance transform selects one exemplar inside the detected plot.
The existing exemplar matcher uses the application's default 17-pixel patch.
No supplied bounds, tick positions, label values or point coordinates enter
these steps.

After the proposals are frozen, the test replays explicit review actions. It
records missing point placements separately from acceptances, exports once
under the suggested coordinates, reviews the endpoint columns, then saves,
reopens and exports again. The scorer opens `scan-sessions.reference.json` after
both exports. It checks signed plot-edge errors, raw numeric labels, anchor
distances, scientific CSV values, phases and the review evidence for every row.
Both encodings must recover the five non-seed disks with no extra proposal or
manual placement. The separate marker encoding tests exercise a hollow-square
series with filled-square and cross distractors, transparency and a color annotation.

## Native reproduction

Use the repository's development setup. Stage the four reviewed OCR files
described in `models/review-assistance/ocr/README.md` in a local `reader` directory.

```powershell
$env:GRAPHREADER_CALIBRATION_OCR_ASSET_ROOT = (Resolve-Path .\reader).Path
dotnet test tests/GraphReader.Integration.Tests -c Release --filter "FullyQualifiedName~ScanSessionCalibrationTests|FullyQualifiedName~MarkerExemplarEncodingTests" --logger "console;verbosity=detailed"
```

A missing asset directory makes this opt-in OCR case inconclusive. The reader
validates model, manifest, notice and license hashes before inference.
The Windows CI manual dispatch option `review_assistance` recreates and verifies
the reader and runs the image export and child-process checks. The OCR
child-process probe supplies a known synthetic crop and plot to test reader
reuse, cancellation and restart. The image-to-export test derives its own plot.

On native Windows, import the PNG into manual preview, run axis and label
suggestion with the actual worker processes, and inspect both the measured-tick
and glyph-center paths. Check raw OCR, the rightmost X label, explicit printed-X
selection, review-before-Apply and placement provenance after save/reopen.
Repeat with the RGB control to compare marker proposals. Measure operator
actions separately from the scripted review transcript.
