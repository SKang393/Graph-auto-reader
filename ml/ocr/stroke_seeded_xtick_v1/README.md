# Stroke-seeded XTick crop candidate V1

This train-only materializer turns full synthetic graph panels into fixed-size
crop candidates centered on rendered horizontal-axis tick strokes. One source
panel yields several stroke instances, including hidden-label/background cases.
All crops preserve `source_id`, panel ID and tick ID so later split assignments
can keep every crop from one graph source together.

Labels use the Pillow renderer's actual text-alpha raster. The materializer
reconstructs each rendered x-tick glyph alpha mask from the renderer-recorded
font, text, position and anchor, checks the resulting tight mask bounds against
`rendered_pixel_box`, then transforms each intersecting glyph box into crop
pixels. A one-to-many crop target list is intentional because adjacent tick
labels can fall within a coarse window. Hidden ticks with no glyph in the crop
are background-only. Visible labels that are unmatched or clipped by the
proposal window are explicitly excluded, never mislabeled as background.
Any other visible text role intersecting a crop also excludes it from this
XTick-only target set. Multiple XTick glyphs and clipped neighboring labels are
recorded for audit but the crop is excluded from the one-box training set. A
positive is trainable only when its sole complete glyph target matches that
seeded stroke's visible label. A background crop is trainable only when it has
no glyph targets and no confuser text.

## Crop and production relationship

The candidate source window is 48x48 original grayscale pixels, placed from the
axis stroke center and plot baseline without consulting label coordinates. The
window is resized to a 96x96 content area and mid-gray padded to 256x96 (8-bit
128 approximates the production crop batcher's neutral float 0.5). Out-of-panel
source pixels use white graph-paper background. The
manifest records forward and inverse affine maps, including negative window
origins and clipped panel edges.

Production `TickLaneTextRegionRecovery` does not create this crop. It uses
geometry-only stroke seeds to associate an independently detected connected
component with one measured tick. Production `OcrCropBatcher` then crops a
detected glyph region in original-pixel coordinates, adds its configured
padding, and preserves aspect ratio into a padded recognition tensor. This
candidate is an explicit coarse crop contract for a future stroke-conditioned
recognition/detection route, not a claim of production equivalence.

The synthetic renderer's camera-warp path transforms top-level annotation lists
but leaves nested `panel[*].ticks` and `panel[*].texts` in their pre-warp frame
(`ml/synthetic/renderer.py`, `_transform_annotations` and panel-list
finalization in `render_scene`). This materializer applies the recorded final
forward transform to nested stroke points and glyph alpha masks, and rejects
non-affine matrices for this candidate recipe. It does not modify the renderer.
A separate renderer-contract fix should update nested annotations or define
this split coordinate behavior explicitly.

## Bounds and use

The default materialization generates 24 deterministic training-only source
cases, at most 72 panels and at most 900 crops. Rendering and layout resolve
only the project's NotoSans Regular, Medium and SemiBold font assets; the
manifest records resolved font provenance per source. No external image or
font inputs are read.

```powershell
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('-m', 'unittest', 'ml.ocr.stroke_seeded_xtick_v1.test_materialize', '-v')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/materialize.py')
```

Run output is project-local under `artifacts/stroke-seeded-xtick-v1-train-verified-v6` by
default. The result is synthetic train input only, not a held-out probe,
acceptance corpus, or production model.

## P1 GPU train-fit experiment

`train_gpu.py` pins the exact V6 manifest SHA-256 and validates every listed
image and annotation. It admits only the 235 `trainable_single_box` positives
and 409 `trainable_single_box` backgrounds; it verifies but skips the 37
excluded records. The V3 spatial locator architecture and target/loss helpers
are reused with fresh random initialization, 30 epochs, 7 crops per batch and
92 batches per epoch (2,760 optimizer steps). Every epoch consumes all 644
admitted crops once and mixes both classes. Execution rejects CPU fallback and
requires the project RX 6600 XT launcher.

The report includes cell-fit scores plus decoded image-level IoU and NMS metrics
before and after training. Score thresholds are 0.25 and 0.5; NMS IoU and match
IoU are 0.5; evaluation caps post-threshold candidates at 100 per image. All
metrics are train-fit only. The 644 crops come from 24 source graphs with 8 to
48 trainable crops per source, so crops within each source are correlated and
this result says nothing about held-out generalization.

```powershell
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('-m', 'unittest', 'ml.ocr.stroke_seeded_xtick_v1.test_training', '-v')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/train_gpu.py')
```

The run writes an exploratory checkpoint and aggregate report under
`artifacts/stroke-seeded-xtick-v1-p1-rx6600xt-fit-v1`. It is diagnostic model
fit only, not production activation or OCR acceptance.

## P2 top-32 hard-negative train-fit comparison

P2 keeps the V6 manifest, 644 admitted crops, V3 model, fresh initialization,
model seed, shuffle seed, AdamW settings, 30 epochs, 92 batches per epoch, box
loss, thresholds and image metrics unchanged. Its single training change is
the existing V4 per-image top-32 highest-logit background-cell mask for the
negative BCE term. Each selected batch image contributes exactly 32 negative
cells; positives are excluded. The shared P1 loop and the V4 selector are
reused. P2 does not initialize from the P1 checkpoint. Before-training cell
and image metrics matched P1 exactly, confirming the same seeded initialization
and admitted input order.

Run command:

```powershell
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/train_gpu_p2.py')
```

The RX 6600 XT run completed 2,760 optimizer steps in 115.3 seconds. For 644
train-fit crops (235 positive, 409 background), NMS IoU and match IoU both 0.5,
and max 100 detections per image:

| Score threshold | Recipe | Precision | Recall | False positives | FP/image | Top-box recall |
| ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 0.25 | P1 all negatives | 0.1132 | 0.9957 | 1,834 | 2.848 | 0.6596 |
| 0.25 | P2 top-32 | 0.3019 | 0.9957 | 541 | 0.840 | 0.8723 |
| 0.50 | P1 all negatives | 0.1302 | 0.9957 | 1,563 | 2.427 | 0.6596 |
| 0.50 | P2 top-32 | 0.3687 | 0.9915 | 399 | 0.620 | 0.8723 |

Top-confidence positive-box mean IoU rose from 0.5753 (155/235 at IoU >= 0.5)
to 0.7187 (205/235). This is a strong train-fit false-positive reduction;
P2 still averages 0.62 false detections per image at score 0.5, and its recall
is one positive lower than P1. These correlated crops from 24 synthetic source
graphs are not held-out evidence or production acceptance.

P1 report/checkpoint SHA-256 values are respectively
`68ae7bfdf505609b3b6a92d6c2db85dd8c43da579202081d6e99baffb1ba7d77` and
`82df31aa572a3920de34b6bd80f0bd9f354880175f76bb0b9916250297fb2cec`.
P2 config/trainer/report/checkpoint hashes are respectively
`1ae28371439c7106631337c12db8f1661f7152778532628db8b3163c6b4cd5e5`,
`dfa9fc50455c9102198084062a4a0f391a4a2b674697a26a87abeb2bb30ab9d6`,
`cc013b5eda70af9681dbd87cc3e496a668cc29d0dcfba8d0841250782bdcb615`, and
`9b0e52ab0ebde0faef51326441d6c01e38f1d94c0a017a5530c75a5902c10351`.

## P2 source-disjoint synthetic diagnostic

The frozen P2 checkpoint was evaluated without training on a newly rendered
corpus from the same project-local procedural renderer and crop/alpha-mask
labeler. Twenty candidate recipes used seeds 27104100 through 27104119 and
fresh source IDs. Four hand-drawn candidates were excluded before writing
because their camera transforms were non-affine. The accepted 16 source groups
cover 24 panels and renderer families in a 4/4/4/4 mixture; V6 train had a
different 6/6/9/3 style mix. Probe session counts range from 7 to 26, compared
with V6 train's 7 to 18; font sizes span 8 to 28 px versus 10 to 24 px. The
generator records each recipe, source-group identity, full-panel pixel hash,
label bounds, crop affine maps and exclusion reasons.

The materializer detected 63 background crops whose exact image bytes matched
V6 training images and omitted them from the probe corpus, recording case,
source and class without retaining the overlapping hash. Final separation
checks found zero overlap in source IDs, source identity hashes, full-panel
pixel hashes or stored crop image hashes. The manifest contains 359 crops:
165 positives, 158 backgrounds and 36 explicit non-XTick-text exclusions.
Four additional source recipes were excluded for non-affine transforms.

For image metrics, all 323 admitted crops were scored, including 88
byte-identical duplicates within the probe (37 positive and 51 background
rows). These represent 235 unique admitted crop images, 128 positive and 107
background. The evaluator verifies that duplicate bytes have identical class,
label and target box before scoring every row. The 63 training-image collision
exclusions remove only backgrounds, which can make the measured background
false-positive rate optimistic. Repeated rows also weight those images more
than once.

On the 323 row-weighted crops, with NMS and match IoU both 0.5 and maximum 100
detections per image:

| Score threshold | Precision | Recall | False positives | FP/image | FP/background image | Top-box recall |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 0.25 | 0.2290 | 0.9273 | 515 | 1.594 | 0.000 | 0.8061 |
| 0.50 | 0.2898 | 0.9273 | 375 | 1.161 | 0.000 | 0.8061 |

Top-confidence mean IoU on the 165 positive rows was 0.6483; 133/165 reached
IoU >= 0.5. The train-fit P2 score-0.5 metrics were 0.3687 precision, 0.9915
recall and 0.620 false positives per image, so this new source mix shows
weaker positive recall/localization and lower precision. No P1 checkpoint was
run on this probe, so the result does not isolate P2's change on this source
distribution. This is a consumed exploratory synthetic diagnostic, not formal
acceptance or production evidence.

Probe commands:

```powershell
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/probe_materialize.py')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('-m','unittest','ml.ocr.stroke_seeded_xtick_v1.test_probe','-v')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/probe_evaluate_gpu.py')
```

The probe manifest hash is
`127b276ee9837f48ccd2123930969d70717b0f833b05da28d9351b047ecfa1ba`.
Its generator and crop materializer hashes are
`c0a6d0559bdd384a84785926fa4dbf0230814f0663b55eff978f5ee32ff6a7a6` and
`98b7630e4b6b2df3dc7a1a99c33b27da63fde61f2f75adfe86a1ed351c66bf9d`.
The final evaluation report, evaluator, config, and test hashes are recorded
in the v2 run artifacts and source files; the v1 evaluation output is retained
as a superseded receipt because its duplicate-row field name incorrectly said
those rows were skipped, though all 323 crops were scored.

## P2 one-detection-per-seed analysis

The frozen P2 checkpoint was evaluated again on the same 644 V6 train-fit rows
and the same consumed 323-row source-disjoint probe. Baseline NMS aggregates
were recomputed and matched the saved P2 train report and probe-v2 report.
Then each unambiguous stroke-seeded crop retained only its highest-confidence
valid decoded box when that score met threshold. The match IoU was 0.5; NMS
comparison used IoU 0.5 and at most 100 detections per crop. Crops with
multiple/ambiguous targets or non-XTick text remain excluded/needs-review, not
background negatives. The 37 V6 and 36 probe label exclusions were all
`non_xtick_text_intersects_crop`; the probe also records 63 omitted training
image-hash collisions and four non-affine candidate source recipes.

| Dataset | Threshold | Method | Precision | Recall | FP | FN |
| --- | ---: | --- | ---: | ---: | ---: | ---: |
| V6 train-fit, 235 positive | 0.25 | NMS | 0.3019 | 0.9957 | 541 | 1 |
| V6 train-fit, 235 positive | 0.25 | top one | 0.8613 | 0.8723 | 33 | 30 |
| V6 train-fit, 235 positive | 0.50 | NMS | 0.3687 | 0.9915 | 399 | 2 |
| V6 train-fit, 235 positive | 0.50 | top one | 0.8613 | 0.8723 | 33 | 30 |
| Consumed probe, 165 positive | 0.25 | NMS | 0.2290 | 0.9273 | 515 | 12 |
| Consumed probe, 165 positive | 0.25 | top one | 0.8061 | 0.8061 | 32 | 32 |
| Consumed probe, 165 positive | 0.50 | NMS | 0.2898 | 0.9273 | 375 | 12 |
| Consumed probe, 165 positive | 0.50 | top one | 0.8160 | 0.8061 | 30 | 32 |

Top-one reduced redundant false detections on positive crops, while losing 28
train-fit positives and 20 consumed-probe positives relative to NMS at score
0.5. On V6 train-fit, top-one retained three background false positives; on
the consumed probe all top-one false positives were on positive crops, while
background-only FP remained zero. Probe background rows include repeated
images and omit 63 train-hash collisions. The consumed probe is no longer
untouched; this analysis is exploratory, not product acceptance. No model
training occurred.

```powershell
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('-m','unittest','ml.ocr.stroke_seeded_xtick_v1.test_top_one_seed_analysis','-v')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/top_one_seed_analysis.py')
```
