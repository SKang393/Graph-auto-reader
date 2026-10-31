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
