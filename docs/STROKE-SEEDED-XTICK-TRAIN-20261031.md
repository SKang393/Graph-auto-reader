<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Stroke-seeded XTick training crops

The production OCR recovery currently associates detected text components
with axis strokes. It does not generate a crop from each stroke. This
train-only experiment defines a new 48-by-48 source-window proposal and
records its reversible mapping to the original panel and model canvas.

The verified V6 materialization rendered 24 procedural source groups and
681 crops: 235 complete one-label positives, 409 text-free backgrounds and
37 excluded crops containing other text. The verifier checked all 1,362 image
and annotation files. Only rows marked `trainable_single_box` may be used for
the one-box model. The generator uses project-local Noto fonts and preserves
source group identities for future separation. It applies the renderer's
recorded affine warp to nested source-frame annotations, rejecting non-affine
cases. No model training or production activation was performed.

- Manifest SHA-256: `12565af18cd69b680b978454058ab0f4448336931e5db7b3c128b861af9e0f1a`
- Generator SHA-256: `98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d`
- Focused tests: 8 passed, including warp, crop mapping and target admission.

Earlier attempted outputs are superseded and must not be consumed. Synthetic
crop verification alone does not demonstrate image recognition accuracy or
independent source performance.

## GPU train-fit experiment

The first model used 644 admitted V6 crops from 24 correlated synthetic
sources, skipping the 37 excluded rows. Fresh initialization and 30 epochs
produced 2,760 optimizer steps on the AMD Radeon RX 6600 XT. At score, NMS IoU
and match IoU 0.5, training-image recall was 0.9957, precision 0.1302 and
false detections averaged 2.43 per image. The highest-scoring box reached
IoU 0.5 in 155 of 235 positive images. These are training-fit results and
show substantial false positives; the model is not approved for production.

- Trainer SHA-256: `34ea86ba936af7e2f2d80acf656270acfcc524a2cb126680715adf91358b275c`
- Training report SHA-256: `68ae7bfdf505609b3b6a92d6c2db85dd8c43da579202081d6e99baffb1ba7d77`
- Checkpoint SHA-256: `82df31aa572a3920de34b6bd80f0bd9f354880175f76bb0b9916250297fb2cec`
- Training admission and geometry tests: 4 passed.

## Hard-negative train-fit experiment

P2 kept the V6 inputs, architecture, seeds and 30-epoch schedule, but used the
32 highest-scoring negative cells per image for the background loss. On the
RX 6600 XT it completed 2,760 optimizer steps. At score, NMS IoU and match IoU
0.5, precision increased from 0.1302 to 0.3687, recall decreased from 0.9957
to 0.9915, and false detections fell from 2.427 to 0.620 per training image.
The top-scoring box reached IoU 0.5 on 205 of 235 positives. The results are
limited to correlated training crops and are not production acceptance.

- Trainer SHA-256: `dfa9fc50455c9102198084062a4a0f391a4a2b674697a26a87abeb2bb30ab9d6`
- P2 report SHA-256: `cc013b5eda70af9681dbd87cc3e496a668cc29d0dcfba8d0841250782bdcb615`
- P2 checkpoint SHA-256: `9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351`
- P1/P2 admission and loss tests: 6 passed.

## Source-disjoint procedural probe

Frozen P2 inference used 323 admitted crops from 16 new source groups.
Source IDs, identities, full-panel pixels and retained crop-image hashes did
not overlap the V6 train corpus. At score, NMS IoU and match IoU 0.5, recall
was 0.9273, precision 0.2898 and false detections averaged 1.161 per image.
The highest-scoring box reached IoU 0.5 for 133 of 165 positive rows.

The probe also excludes four unsupported perspective-warp recipes, 36 crops
with text confusers and 63 background crops whose image hashes matched train
images. The 323 scored rows include 88 within-probe image repeats. These
choices can bias the background error rate; the result is a synthetic
development diagnostic, not formal product acceptance or a real-graph test.

- Probe manifest SHA-256: `127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba`
- Frozen P2 checkpoint SHA-256: `9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351`
- Corrected evaluation SHA-256: `97932d6f2b12debdd009014d2cbcfd5ff63db8754ccc664079df2551d7242340`
- Probe tests: 3 passed; inference used the RX 6600 XT with no optimizer steps.

## One-box-per-seed analysis

An RX 6600 XT inference-only analysis compared the frozen P2 model's full NMS
output with retaining the top-scoring box per unambiguous seed. At score and
match IoU 0.5, probe precision rose from 0.2898 to 0.8160, while recall fell
from 0.9273 to 0.8061. The count of false positives fell from 375 to 30, but
20 previously matched positive rows were lost. Excluded crops stayed out of
scoring. The consumed probe's repetition and background exclusions above
still limit interpretation; this does not establish production readiness.

- Analyzer SHA-256: `732650575a1878e380387c4a066b6e3c5b80755d61293e5f1aafd711bed8254d`
- Analysis report SHA-256: `66ba6032cdd9ef66fb14ac8b810bbb3de5e79422dbf9eaca04b096e8cacab970`
- Top-one scoring tests: 4 passed; no optimizer steps ran.

## Detector plus numeric recognizer diagnostic

Frozen P2 top-one boxes were mapped through the application `OcrCropBatcher`
into the project-trained V5 numeric component recognizer. On the consumed
probe, 133 of 165 positive crops had a detected box with IoU at least 0.5.
For those 133 matches, the recognizer returned 21 exact strings, 29 incorrect
strings and 83 empty alternatives; the latter are not runtime errors. At
score 0.5 there were also 30 false detections and 32 positive misses. This is
an exploratory synthetic diagnostic, not a full graph workflow or approval.

The detector export used the RX 6600 XT without optimizer steps. A separate
DirectML recognizer attempt crashed natively with `0xC0000005`; the completed
recognizer diagnostic used CPU inference. V5 remains unapproved and is not in
the production model store. The probe's previously noted exclusions and
duplicate-image weighting apply.

- Detector checkpoint SHA-256: `9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351`
- V5 recognizer SHA-256: `9db95c41ce396e8b2dff3b525556615528a00ca87f4cc531274374b961417c84`
- Corrected report SHA-256: `438a3b4c1628e3810d51f5c8a3ab41ee2ea6ab2c3b42ba567b0ef4469b4b941b`
- Diagnostic build succeeded and 3 report checks passed.

## Ground-truth crop comparison

The same recognizer and production cropper were run on annotated boxes and
P2 predicted boxes for all 165 positive crops in the consumed probe. The
annotated boxes produced 32 exact strings, 32 wrong strings and 101 empty
alternatives. Predicted boxes produced 23 exact, 30 wrong and 112 empty;
neither set produced explicit recognizer failures. This shows that box
localization alone does not explain the empty outputs. The adapter's batch
timing cannot determine the reason for each individual empty result.

- Comparison runner SHA-256: `08b420f83e7ef250333acaac32653a54c7ae0411154a92126b6d80b79f3e19d6`
- Corrected report SHA-256: `8bb9ee29ab4ac0ee0f09572fabd367624d82cc11d5a33d42a044752d56bd018e`
- Diagnostic build and 4 report checks passed; inference used CPU.

## Component rejection diagnosis

An isolated diagnostic invoked the adapter's exact crop encoding and captured
its actual model logits. Its derived results matched the adapter on all 165
predicted positive crops. The 112 empty results comprised 82 component
geometry/glyph-count rejections, 12 model reject-class results, 11 confidence
rejections and 7 numeric-grammar rejections. The adapter exposes only one
combined component-rejection flag, so this does not distinguish its causes.
The remaining crops produced 23 exact and 30 incorrect strings. These are
consumed synthetic probe results, not product acceptance.

- Diagnostic runner SHA-256: `0c617bf3c145d0e74b22a7909325cd83e7590675819073593254008d001c3156`
- Report SHA-256: `1d23fd6c3f84f0a12d4eda8b630bd41ca620f406198e8540cb81d40b48eb8e75`
- Diagnostic build and 3 report checks passed; inference used CPU.

A subsequent sensitivity check shifted that one-pixel centerline by signed
perpendicular offsets of 1 and 2 pixels on the same consumed synthetic probe.
No shifted mask overlapped the reconstructed target glyph pixels, but some
shifts produced new wrong readings. Predicted-box exact counts were 23 at
baseline, 37 at exact geometry, and 27, 44, 23 and 32 at offsets -1, +1, -2
and +2 respectively. This is a synthetic perturbation, not evidence from
production-detected strokes. Report SHA-256:
`7c6d095a59490be6fdb749faf36900c710df6a8b1e77d8548065ab8097ef3e15`.
The diagnostic build and 3 report checks passed.

## Production axis stroke alignment

The production raster decoder and OpenCV axis detector processed all 24
panels reconstructed from 16 hash-verified consumed synthetic sources. Of
422 annotated tick geometries, 181 were visible. The detector emitted 37
usable X strokes; 25 matched visible ticks, 156 visible ticks were unmatched,
and 12 detections were unmatched. Matching tolerance was the larger of 3
pixels and half the annotated stroke length. Earlier reports that counted
invisible ticks in the recall denominator are superseded.

Among 23 matched positive cases with verified glyph masks, two detected
centerlines touched glyph pixels, three pixels in total. This consumed
synthetic diagnostic does not establish real-graph performance or a safe
production erasure rule. Report SHA-256:
`7a9aa9b5dc6b73de11eac01f856984b55ed537d4614250836022c73973c77de2`.
The diagnostic build, 4 alignment tests and 3 report checks passed.

All 181 visible synthetic strokes measured approximately 8 pixels, below the
default Hough minimum line length of 12 pixels. An isolated comparison that
lowered only this minimum to 5 produced 23 matches and 9 unmatched detections,
versus 25 and 12 under the default. Its sole emitted Hough tick was unmatched,
and axis uncertainty remained common. The result does not justify changing the
production default. Diagnostic report SHA-256:
`acc2c0bf20153647b5f1470c812c6de7e567c7a35869970b276dde17bd83640e`.
The runner built and its metric test passed.

An attribution pass on the same panels found that, of 156 missed visible
ticks, 53 had no candidate midpoint within 10 pixels and 81 had a horizontal
nearest candidate. The remaining nearest candidates failed other observation
or tick-eligibility checks. The selected axis orientation and placement were
generally close to the rendered axis; one selected axis had a shortened span.
This is evidence about a consumed synthetic set, not proof that nearby lines
are the correct missing stroke. Report SHA-256:
`fd17c37c4f7eb78bed6cb2924f91030ad868aa2fb9be27e8aa1d31b781f3aa17`.
The diagnostic built and 4 geometry-gate tests passed.

A follow-up changed both Hough vote threshold and minimum line length to 5,
leaving other settings fixed. Against 181 visible ticks it matched 36 with 8
unmatched detections, versus 25 and 12 under defaults; all 24 candidate panels
still required review. The candidate generated 19,855 line candidates versus
10,850 by default and remains insufficient for production. The diagnostic
reproduced default results on every panel. Report SHA-256:
`c50ba592ea2334dd412335a0c04dec397b9566ce5ac44aa642436516653c1a46`.

An isolated raster-ink scan using the selected axis recovered 73 of the 181
visible ticks but produced 292 unmatched proposals. Fixtures confirmed false
proposals from a thick border, a grid crossing and a nearby text stroke. The
scan is not suitable for production. On the same panels, the default detector
matched 25 ticks with 12 unmatched detections. Diagnostic report SHA-256:
`95ce064ca9006d1fd1ed5521a0a309b4a3a0b3e18ade084adb191d98a8941455`.

An additional encoding-only check reproduced the component-rejection flag on
all 165 crops: all 82 rejections were caused by a component height ratio of at
least 0.75, with none caused by the maximum glyph count. The synthetic tick
stroke geometrically intersected the padded extraction on 74 of those 82
rejected crops, but 8 of 9 crops without that intersection were also rejected.
Overlap alone therefore does not establish the cause. The aggregate report
SHA-256 is `1a56694aec60058f43116aac295303ba3fbe24b8bd878674cd52a860efcc1c92`;
the diagnostic build and 3 report checks passed.

An isolated vertical crop-margin sweep on the same consumed probe did not
support changing production settings. For predicted boxes, increasing margin
from 0.25 to 0.50 and 0.75 reduced tall rejections from 82 to 61 and 22, but
also reduced exact readings from 23 to 13 and 4. Ground-truth exact readings
likewise fell from 32 to 14 and 6. The 0.25 results reproduced the baseline;
all 158 forced low-score background crops returned empty. Report SHA-256:
`59b98d96c3717b444f822f4f45e2549048f70c9ec33fcfc7e723c9a8494377ff`.
The diagnostic build and 3 report checks passed.

## Geometry-only stroke-removal diagnostic

On a copy of each consumed synthetic probe image, a diagnostic erased only
the annotated one-pixel tick centerline before applying the existing cropper
and V5 recognizer. Exact readings on predicted boxes rose from 23 to 37 of 165;
on annotated boxes they rose from 32 to 67. The 165 reconstructed glyph masks
matched their recorded hashes and none of 4,117 erased pixels overlapped
their 90,889 nonzero glyph pixels. Forced backgrounds stayed empty. These
are known synthetic stroke coordinates, not detected real-world geometry;
stroke width, antialiasing and independent acceptance remain untested.

- Diagnostic runner SHA-256: `7b56f3eb2e5ba7b239f3c59dde12aaa8e976267db03ac192253c6129e5e2f288`
- Corrected report SHA-256: `330df878f43020e10dd7332a6545b92fb36590f124d00efc23a2ba4155262df4`
- Diagnostic build and 3 report checks passed; inference used CPU.
