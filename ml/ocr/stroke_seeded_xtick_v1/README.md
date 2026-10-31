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
font inputs are read. This directory does not train a model.

```powershell
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('-m', 'unittest', 'ml.ocr.stroke_seeded_xtick_v1.test_materialize', '-v')
& .\tools\gpu-training\Run-GpuPython.ps1 -PythonArguments @('ml/ocr/stroke_seeded_xtick_v1/materialize.py')
```

Run output is project-local under `artifacts/stroke-seeded-xtick-v1-train-verified-v6` by
default. The result is synthetic train input only, not a held-out probe,
acceptance corpus, or production model.
