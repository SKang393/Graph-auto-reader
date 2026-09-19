<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Read fragmented labels inside detected plots

Saved V44/V45 development outputs showed multiword labels represented by
separate detected boxes. In V45, unmatched text regions account for 612 of
653 character edits; text is unchanged on 130 of the 131 truths matched by
both models. These findings point to localization and grouping, not a reason
to start another training run.

## Bounded implementation

The opt-in `original-db-head-inside-plot-v1` composition groups aligned boxes
only when their complete union lies inside the detected plot and crosses no
detected phase-divider X position. It reuses the existing participant-lane
alignment constants. Recognition then reads the combined original-pixel
crop; it does not concatenate guessed text or assign a legend role from truth.

The default is off. Missing divider geometry skips grouping; an explicitly
empty list means the detector measured no dividers. Enabled grouping and its
divider inputs have distinct cache identities. Disabled behavior preserves
the existing cache keys. Stable membership IDs retain raw-to-effective
region provenance, minimum confidence, and original coordinates.

`OcrRequest.PhaseDividerXs` is optional runtime metadata. It preserves the
positional constructor and deconstruction signature and does not change any
frozen project, vision-result, or model-manifest schema.

## Diagnosis before implementation

An attempted right-of-plot grouping produced no groups or metric changes:
all 11 development legend truths lie inside the detected plot. The bounded
inside-plot rule was then checked once on all 709 historical-train and 183
development truths with the existing constants.

| Fixed model | Train TP / FP / FN change | Dev TP / FP / FN change |
| --- | --- | --- |
| V44 | +1 / -2 / -1 | +2 / -5 / -2 |
| V45 | +1 / -2 / -1 | +3 / -9 / -3 |

No previously matched truth was lost, and no merged member group mapped to
different truths in this diagnostic. These observations are not a universal
safety guarantee or a recognition-accuracy result. Prior broader grouping
rules that merged unrelated phase/condition labels remain rejected.

Local diagnostic: `artifacts/goal22-runs/ocr-inside-plot-assembly-diagnostic-v1/aggregate-v1.json`,
SHA-256 `d9fe8499db82337a22f66ecf7809bfe8d462aff17e45320a480d140d17b55438`.
The diagnostic took 6.4 seconds wall time without inference or training.

## Validation and scope

The integrated runtime builds with zero warnings and errors. The complete OCR
test project passes 283 tests with 16 existing optional skips; 25 application
factory tests, 28 evidence-tool self-checks, and 18 Python scorer tests pass.
Independent review verified coordinates, divider provenance, cache identity,
preserved default behavior, independent grouping replay, and full denominators.

The application trial uses fixed V44 weights and the same recognizer, native
runtime, and original synthetic images. It is an unapproved development
composition. No private or sealed data, optimizer step, new model revision,
model promotion, packaged build, or release is part of this repair. Existing
Apache-2.0 model notices remain required; no dependency was added.

## Actual application result

All 37 panels completed without panel or recognition failures in 34.935
seconds. Authenticated full scoring took 18.820 seconds. The development
denominator remains 183 text regions and 1,019 characters; all five central
acceptance bars still fail.

| Development measure | V44 control before grouping | Inside-plot grouping |
| --- | ---: | ---: |
| Matched text regions | 139 | 141 |
| Extra detections | 48 | 43 |
| Missed text regions | 44 | 42 |
| Exactly recognized labels | 126 | 128 |
| Character edits | 624 | 567 |
| Correct roles | 110 | 110 |

Raw detector outputs are preserved separately from grouped regions. Role
accuracy is unchanged: grouping words does not supply missing legend-symbol
context. No production approval follows from this partial improvement.

- Runtime report: `artifacts/goal22-runs/ocr-inside-plot-runtime-v1/application-evaluation-v1/report.json`
- Runtime report SHA-256: `15a28150ef5d8258ebfef94ed3867a331a305c5aa696b61c371ab3f77869665f`
- Full score: `artifacts/goal22-runs/ocr-inside-plot-runtime-v1/full-score-v1.json`
- Full score SHA-256: `31c15dd06dd4dd22d85f352ad65a8b58c44891748dd7f2f4f85382b0226954b4`
- Candidate SHA-256: `4f5b773cccaf90f7c091a688aa92c34f565b4200dd593b4eb195d083a1078807`
- Runtime source manifest SHA-256: `524779946a7b0555d2981119db852586f41e1c5027775f97aff206570c46284f`

The first scoring invocation rejected a mistyped manifest checksum before
scoring; its log is retained. Correcting the invocation reused the completed
application result and did not repeat inference.

## Integration verification, September 19, 2026

The lead rechecked all ten bound runtime source files, all four execution
assemblies, the scorer and scorer-test identities, and the saved report and
candidate checksums before integration. The V44 control and this trial use
identical detector and recognizer descriptors, native runtime identity, and
license inputs. Original Gray8/BGR pixel hashes and raw detection identities
and geometry agree across all 37 panels. The application assemblies differ
because this repair adds the opt-in grouping path.

The historical-train comparison retains 709 truths: matches rise from 624 to
625, extra detections fall from 20 to 18, exact recognition stays at 396,
and character edits fall from 1,425 to 1,396. The control's additional 153
supplemental-train truths are outside this 37-panel trial and are not included
in that comparison. Development retains all 183 truths; no denominator was
reduced.

Saved test records and build/self-test logs were checked without rerunning
completed inference, training, or unchanged test suites. GitHub verification
will check the integrated commit separately. This source checkpoint does not
produce a packaged application build or advance the version.
