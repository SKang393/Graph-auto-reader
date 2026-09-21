<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Raster import repair, 2026-09-21

Five of 21 explicitly open OCR coverage images failed before recognition.
Partial axis lengths created duplicate plot groups, nearby legend frames created
overlapping padded figure proposals, and fractional shared boundaries claimed the
same encoded pixel. The standalone raster path now combines these overlapping
proposals, keeps complete observed axis extents, and uses integer shared cuts.
Combined proposals carry a review warning. The PDF default path is unchanged.
Invalid caller-injected overlaps, checksum errors and corrupt images still fail.

The same 21 sources now all import, producing 26 panels. This proves the import
crashes are repaired, not that every panel is semantically correct. The 23-source
native baseline retains all 39 panel observations, 17 exports, six calibration
failures, 479/706 correct unique values and 474/724 correct rows. Every upstream,
export and scored metric is unchanged; all model weights are unchanged. In-memory
OCR likewise reproduces the prior aggregate exactly across 892 labels.

Validation: 428 application tests and 59 PDF tests pass. One existing PDF test
requires the separately reviewed external renderer and remains skipped. The
native build is warning-free. Archive, in-memory and worker suites pass 13, 30
and 15 checks. The repair job takes 198.45 seconds; native workflow inference
takes 146.33 seconds and its complete comparison job takes 162.28 seconds.
Exact commands, source hashes and evidence references are retained in the
[aggregate report](GOAL-22-RASTER-IMPORT-REPAIR.json) and referenced local logs.

The explicitly open archive also passes the real C# archive reader. Constant
failure-stage labels now distinguish import, axes, recognition and scoring
without exposing source identities, paths, text or inner exceptions. No hidden
case was inspected or replayed. The previous sealed execution-error accounting
remains unchanged, with two unused reserves.

The broader composed OCR run advances to an axis-stage failure. A separate native
workflow diagnosis identifies an invalid OCR exclusion line in the tall-image
fixture. Twenty other open images reach calibration but cannot export safely;
the one unchanged baseline control exports. These are OCR coverage fixtures,
not successful end-to-end acceptance evidence. Fix the coordinate defect on
owned development data, align the diagnostic native runtime with the product,
then reassess complete OCR metrics. Do not infer a sealed accuracy result.

No dependency, model weight, license, training, private read, sealed read,
production activation or package changes. Build 433 remains 0.4.33. The preceding
0937429 source checkpoint passed CI run 35567082036. All four Goal 22 outcomes
remain incomplete; work continues through OCR, marker/calibration, authentic
real acceptance, production activation and both 2.0.0 distributions.
