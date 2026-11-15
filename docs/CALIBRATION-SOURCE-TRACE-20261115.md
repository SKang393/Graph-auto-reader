<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Calibration source trace after measured-label grouping

## Scope and identities

One changed-source engineering trace followed the X/Y grouping fixes and the
[native review fallback repair](GRAY-AXIS-REVIEW-FALLBACK-20261115.md). It used
the same first 12 anonymized, sorted development projects as the
[unchanged 0.4.52 trace](AUTOMATIC-CALIBRATION-DIAGNOSIS-20261115.md). The baseline
was not rerun. This is diagnosis of authorized development inputs, not an
independent accuracy evaluation, model comparison or threshold selection.

The fixed selector still contains 120 development projects and excludes the
51 reserved projects. Both traces have the same identities:

| Identity | SHA-256 |
| --- | --- |
| Assignment | `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a` |
| Selected inventory | `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d` |
| Twelve-source digest | `4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1` |

The new route binds source `bc513f79` plus the fallback repair to 20 exact
runtime library hashes in `RealDevPostGroupingTracePins.cs`, App EXE
`0777827c284e6dbbfc6bf675d4662c23564f93a7cab276e0fa211c56942177e4`, App DLL
`68025084f67e6bd5ab7087a94e19904316dc3adee8948869625defc170c44683`, and Rust
`cfc8e59a08b4aeaad84cc4742aa8a8c2f211a8f575f0d6bcfb6beb5f5a1880fd`.
This is unpackaged, framework-dependent source output; the trace reports
`.NET 10.0.10`. It is not the self-contained baseline package. The OCR model,
manifest, notices and reviewed OpenCV runtime retain their prior checksums.

The final probe DLL was
`e3d1df02a1960f7304fc46eb4ab5ae52607e4ad5b95b318e531dcd4414a3f1ad`.
Its owned self-test checks X-only historical requests, X+Y new requests,
structural response matching and full terminal-status accounting. Missing
arguments produce the dedicated sanitized error without opening private data.
Historical 451, 452 and Mirage verifier bindings remain unchanged.

## Execution and complete accounting

```text
GraphReader.CalibrationOcrProbe.exe --real-dev-source-post-grouping-trace <original-corpus> <source-App.exe> <retained-452-ocr-assets> <source-Rust.exe> --explicit-opt-in
```

The single invocation ran from `2026-11-15T06:54:46.8365407Z` to
`2026-11-15T06:55:17.1477939Z`, exit 0, empty stderr, **30.3036239 seconds** wall
time. It attempted and finished all 12 projects and all 12 effective panels:
11 prepared panels and one genuine no-panel fallback. There were no unattempted
or unfinished panels, timeouts, OCR failures, unsupported dimensions or uncaught
panel failures. Five completed panel attempts failed geometry; completed
execution does not mean successful calibration.

| Stage/result | Recorded 0.4.52 baseline | Changed source |
| --- | ---: | ---: |
| Effective panels | 12 | 12 |
| Geometry/OCR responses | 7 | 7 |
| Geometry failures | 5 | 5 |
| Native fallback attempts / returned geometry | 0 / 0 | 5 / 0 |
| OCR regions | 113 | 117 |
| Literal numeric rows | 67 | 72 |
| Associated labels | 58 | 63 |
| Available partial X drafts | 0 | 0 |
| Available partial Y drafts | 2 | 2 |
| Complete drafts | 0 | 0 |
| Median OCR call | 2289.7463 ms | 2334.7933 ms |
| Longest OCR call, including cold startup | 6954.5489 ms | 6967.1291 ms |
| Whole trace wall time | 23.9217936 s | 30.3036239 s |

All five native attempts returned `OPENCV_AXIS_AMBIGUOUS`; the composition kept
the original Rust `AMBIGUOUS_AXIS`. The owned gray recovery did not establish
recovery on this sample. Geometry calls totalled 7546.4746 ms and OCR calls
20535.897 ms. Extra proposals or associations do not prove correct recognition.
One panel contained explicitly estimated label positions; it did not produce
a complete draft. No values were applied or exported.

Overlapping calibration reasons across the seven OCR panels were: insufficient
pairs 4, unpaired labels 3, inconsistent spacing 2, invalid range 2,
nonmonotonic labels 2, observed zero required 2, duplicate association 1 and
invalid numeric label 1. The reason-reporting repair can expose additional
failures; these counts are not independent error-rate measurements.

Every sampled crop had zero offset; nonzero original-coordinate mapping is
covered only by the owned mapping control. No sealed payload was read, truth
scored, image written, model trained or promoted. Originals were read through
the fixed selector's locked, checksum-verified path. This development inventory
is not presented as a pristine independent acceptance split.

Report: 54,200 bytes, SHA-256
`ce639a593d26d0e9eda56772626418fbb8a82955efaf6fb7718be887e14ae457`.

## Acceptance and next defect

**FAIL for automatic-calibration availability on this sample: 0/12 complete
drafts.** The owned source repairs and integrated 1063-test App suite pass, but
those controls do not override this product limitation. The next work inspects
the saved development trace for geometry rejection and label-association causes,
then reproduces specific defects on owned controls. Do not rerun unchanged
inference, relax a gate to fit these answers or alter the independent split.

The saved-trace/source diagnosis identified two next owned controls. One of
seven OCR panels still has a merged whitespace-separated numeric row; source
grouping requires support at both adjacent ticks, which can fail when unlabeled
minor ticks separate actual labels. That exact cause is not established from
metadata alone. Two panels fail only the positive first-anchor range rule even
though their full tick sequences fit. Test whether choosing an existing observed
positive anchor while retaining every fit pair preserves the current contract
and export mapping. Do not infer a missing anchor or relax the range rule.

No package or release was created. The sole 0.4.52 package and shared user Data
remain intact. `artifacts/source-calibration-trace` is bounded sensitive local
metadata retained only for the active defect-diagnosis consumer. Retire it after
that consumer finishes and this aggregate source/outcome is pushed. No per-case
predictions, private labels or images belong in Git.
The reviewed closeout scope is four files / 56,403 bytes; subsequent owned
reproductions need only the recorded failure modes and current source.
