# Server recognition head and crop diagnosis

The unchanged approved-intake server reader exceeds 95% literal accuracy when
given authored text boxes on both owned development sets. Automatic region
selection, crop geometry and assembly remain useful repair targets. No new
reader training is authorized by this diagnostic result.

## Exact runtime inputs and model containment

Capture all 1,345 text crops from 44 existing synthetic sources: 709 training,
183 CSV development and 453 native development targets. Source identities are
disjoint across the three inventories. Every target remains counted. The
existing application raster decoder, crop batcher and recognizer normalization
produce the 102 BGR batches. A blank-only capture session records input tensors
without model inference or recognition labels influencing inference.

The new utility isolates the original reader's final 120-by-18,385 weight
matrix and 18,385-element bias. The backbone and all 18,384 token classes,
including ten multi-scalar flag tokens, remain intact. It rejects incompatible
graphs, invalid replacement values and ambiguous or unsupported target text.
No number, case, spacing or Unicode correction is introduced.

An unchanged head round trip reproduces the reviewed ONNX payload byte for
byte: `0980049401eceb506952f769765a6b95be07cb1197dcbb13c93f107a328f8418`.
Across every crop, the separated head and original model have maximum absolute
probability error 0.000000358 and zero argmax changes. The potential trainable
head has 2,224,585 parameters. No optimizer runs.

## Complete diagnostic results

| Authored-box recognition | Exact / all text targets | Exact rate | Character errors / truth characters |
| --- | ---: | ---: | ---: |
| Train | 454 / 709 | 64.03% | 1306 / 3415 |
| CSV development | 177 / 183 | 96.72% | 6 / 1019 |
| Native development | 433 / 453 | 95.58% | 24 / 3100 |

The actual V46/server pipeline reads 412/453 native targets exactly. Pairing
all targets finds 402 exact in both configurations, 31 exact only with authored
boxes, ten exact only in the actual workflow and ten wrong in both. The
workflow has 19 unmatched targets and 22 matched regions with wrong text.
Known-box cropping is not a safe runtime replacement: it loses ten readings,
and truth boxes are unavailable for real user inputs.

This comparison changes region selection, geometry, source context and
downstream recovery/assembly together. It does not establish a crop-only
causal effect. The 255 training errors remain counted and occur in eight
sources containing degraded renderings. Their low score does not justify
deleting sources or selecting new weights from this diagnostic alone.

## Verification and decision

All 23 utility tests pass in 4.230 seconds. The capture helper builds with
zero warnings/errors in 4.44 seconds. Production crop capture takes 12.173
seconds; inference and parity take 248.025 seconds. The full chain takes
279.300 seconds, using one heavy local job, twelve CPU threads, Idle priority,
disabled ONNX graph optimization and the existing 80% CPU guard.

The [bound result](GOAL-22-SERVER-RECOGNITION-HEAD-FEASIBILITY.json) references
the source snapshots, exact runtime assemblies, synthetic inventories,
model/license files, tests, complete predictions and parity evidence.
Keep V44/English/V27/V5 as the retained model configuration. Next diagnose
automatic crop/region isolation and assembly; no training revision is opened.

The server model remains the explicitly approved Apache-2.0 intake with its
original notices. Project source and synthetic inputs are Apache-2.0; no
dependency or external weights are added. No private/sealed data, production
activation, package or release is involved. This is technical feasibility and
open synthetic diagnosis, not model acceptance. Goal 22 remains incomplete,
and build 433 remains 0.4.33.
