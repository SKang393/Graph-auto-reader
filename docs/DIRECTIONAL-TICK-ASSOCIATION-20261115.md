<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Associate offset labels within their actual tick cells

## Defect and repair

The [saved development trace](CALIBRATION-SOURCE-TRACE-20261115.md) reported
unpaired labels on three OCR panels. Source inspection found a concrete
interaction between offset glyphs and unlabeled minor ticks; that mechanism
is not asserted to explain those particular private failures.

After selecting a unique nearest measured tick, association previously used
half the smaller gap to either neighbor as a symmetric distance limit. A minor
tick behind the chosen tick could reject a glyph offset in the other direction,
even though that glyph remained within its nearest-tick cell and the existing
font-size bound. The repair uses the neighboring gap toward the glyph offset.
Unique-nearest selection, tie rejection, font bounds, literal numeric checks,
duplicate rejection, all-pair fits, monotonicity, observed zero, valid ranges
and explicit review/Apply behavior are unchanged. No alternative tick is chosen
to make a sequence fit and no value is assigned to an unlabeled tick.

The acceptance property is identical complete calibration and literal pairs
with or without an opposite-side unlabeled tick, plus unchanged safety guards
and immutable OCR evidence. This is an owned runtime invariant, not a relaxed
accuracy threshold. Model, preprocessing and fitted-value thresholds are fixed;
the sealed-run budget is zero.

## Owned regression and actual worker comparison

Against unchanged `04f6d6a7`, the 24-case red group produced eight expected
failures and sixteen guard passes, zero skips, in 65.4655584 seconds including
compilation (239 ms in tests). The eight positives cover both axes, both offset
directions and Create/CreateReviewDraft. Guards cover midpoint ties, exact and
just-outside font bounds, and retaining the actual nearest minor tick.

The repaired App test build passed with zero warnings/errors in 23.6938985
seconds. **122 focused tests passed, zero failed/skipped**, in 4.0749576 seconds
wall time (about one second in tests), covering proposal, view-model, label
review and axis-suggestion behavior. Reviewed OpenCV was staged after compilation
and unchanged after testing. Independent source review found no material issue.

One new owned 640 x 480 raster then supplies a separate actual-worker comparison:
printed X values 1/2/3, printed Y values 10/5/0, unlabeled minor ticks at X=130
and Y=220, and two labels drawn seven pixels away from their intended ticks.
The request contains no supplied plot or tick geometry; the full image crop is
passed to Rust and its detected axes/ticks are passed to the unchanged CPU OCR
worker. Expected positions are post-run assertions only.

| Actual result | Before | After, only App DLL replaced |
| --- | ---: | ---: |
| Automatically detected X / Y ticks | 4 / 4 | 4 / 4 |
| OCR regions / numeric rows | 6 / 6 | 6 / 6 |
| Associated literal labels | 4 | 6 |
| Complete automatic review draft | No | Yes |
| User-entered session / estimated positions / applied | No / No / No | No / No / No |
| Whole invocation wall time | 3.387191 s | 2.8981566 s |

The before report has `UNPAIRED_LABEL` and `INSUFFICIENT_PAIRS`. The after report
has no calibration reasons and yields sessions 1 through 3, Y maximum 10,
origin `(140,380)`, Y-maximum anchor `(140,80)` and final anchor `(540,380)`.
All six after labels match the owned printed values. The original four paired
rows, including raw polygons/text/confidence, are identical between reports;
the before report did not retain the two unpaired rows. Image, detected
geometry, all other twenty-one runtime files, OCR model and inference code
remained unchanged. Both runs exited 0 with empty stderr. This is a functional
improvement on an owned control, not a private accuracy or speed claim.

| Evidence | SHA-256 |
| --- | --- |
| Owned PNG, 4072 bytes | `4569691af094c4d01e467dfb46ad12c035c28c94743bd211c7fa770566bea957` |
| Red TRX | `769d3d0598a125ac482c97c89206d30b402c29030b8d7c24b67c0bf29528c665` |
| Green TRX | `448e683e2b80718a29a7903ab40309bdc4e81bbb24c77e1fae8ede9a411ea083` |
| Before App DLL | `81aa39ff9f882744f65d95cec36f24e35d454e9dc51efd58d19ee3ec1dce190c` |
| After App DLL | `23d1f98707730c0918cc7aef3cf4a0f27cf05582561115cc1047ab63dcf3b901` |
| Before worker report | `e7d6345e14e47870a0a52df243531cf8f606ea7b34a323c5879a38344070f223` |
| After worker report | `f8755e482107d1a22de0825d6ad6a5deba65863db5f76a5fc69ef8e310a4adba` |

```text
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~CalibrationTickProposalTests|FullyQualifiedName~CalibrationTickViewModelTests|FullyQualifiedName~CalibrationLabelViewModelTests|FullyQualifiedName~AxisSuggestionTests"
New-TickCalibrationFixture.ps1 -OutputPath <owned.png> -OffsetLabelsWithMinorTicks
GraphReader.CalibrationOcrProbe.exe --tick-proposal-check <owned-request.json> <isolated-App.exe> <retained-452-ocr-assets> <isolated-Rust.exe>
```

## Current-source confirmation

The isolated App-only comparison deliberately retained its earlier OCR assembly
to isolate association behavior. After it finished, the same temporary directory
was synchronized with all twelve current source GraphReader assemblies and the
current App executable. This includes the earlier minor-X grouping repair and
current commit metadata. Rust and eight third-party libraries remain unchanged.
Separate frozen profile pins will identify this combined source checkpoint;
historical comparison identities are not rebound.

The probe-only build passed with zero warnings/errors in 8.1027628 seconds;
all twenty-two current runtime hashes still matched afterward. Its pure
self-test passed in 0.5170652 seconds without private reads. One current-source
owned check retained the complete six-pair, non-estimated draft in 2.9884037
seconds, with no entered session or Apply. This third owned invocation used
changed source components, rather than repeating either isolated comparison.

| Current-source evidence | SHA-256 |
| --- | --- |
| App executable | `53f881c44dbfb676402d60d09473fa638ee2029555145d51effce8b1bb81a1d8` |
| OCR DLL | `25a975ea8dd769466c107ff6e595d2ff085cda0346357f7c51ae53c9b90fefe5` |
| Probe DLL | `6e287c9f9feca7523bcc0a6973c27c1003be4309d3702bdad6e036aaa0275eac` |
| Pure self-test | `5195f44685591d19c6953cf5b2ae1014b30fe80c8886d9ba9c1d9676b1a98920` |
| Owned integration result | `dd2c147ce2abf227a98e81ef5800d1d1e6592a3b8b1902ff90a871d2526b2561` |
| Fixed development report | `931b5ccd1d572be5f75d67b8e488758495b0d02d4fdc1cf3833e35358bd8f830` |

One full current-source confirmation then completed all twelve development
projects and twelve effective panels: eleven prepared panels and one genuine
fallback. Seven panels returned OCR and five failed geometry. There were no
timeouts, OCR failures, unsupported dimensions, unfinished or unattempted
panels. Source, assignment and selected-inventory digests match the earlier
fixed sample. Fifty-one reserved projects remain excluded, with zero sealed
payload reads, truth scoring or image writes.

Availability is unchanged: **117 regions, 72 numeric rows, 63 pairs, zero
complete session drafts, zero partial X drafts and two partial Y drafts**.
One panel carries explicitly estimated positions. Overlapping reasons remain:
insufficient pairs 4; unpaired labels 3; inconsistent spacing, invalid range,
nonmonotonic labels and missing observed zero 2 each; duplicate association and
invalid numeric label 1 each. Native calls retain two envelope-budget failures,
two exact-contact-budget failures and one multiple-candidate failure. Extra
counts do not establish recognition accuracy and nothing was applied/exported.

Wall time was **32.2886181 seconds**; geometry 9374.8425 ms, OCR 20,801.2283 ms,
pairing 52.748 ms and preparation 1048.0257 ms. Median OCR call was 2361.4791 ms
and the longest 6882.5912 ms. Exit code was 0 with empty stderr. This is one
changed-source diagnostic run, not a speed or independent-accuracy comparison.
The report is 54,491 bytes. Its historical `association_tolerance` field remains
a symmetric geometric diagnostic, not the new directional acceptance bound;
actual paired flags and reasons are authoritative. Any diagnosis recomputes
the current directional bound from the recorded bounds and ticks.

```text
GraphReader.CalibrationOcrProbe.exe --real-dev-directional-association-trace-self-test
GraphReader.CalibrationOcrProbe.exe --real-dev-directional-association-trace <authorized-corpus> <current-source-App.exe> <retained-452-ocr-assets> <current-source-Rust.exe> --explicit-opt-in
```

**PASS for the owned association repair and current-source integration;
automatic-calibration availability on this development sample remains FAIL.**
No package, dependency, model or production switch is part of this batch.
The bounded metadata is temporarily active for geometric diagnosis; retire
the completed output folder after that consumer finishes and this outcome is
pushed. Original inputs, shared user Data and the sole 0.4.52 package remain
outside the cleanup scope.
