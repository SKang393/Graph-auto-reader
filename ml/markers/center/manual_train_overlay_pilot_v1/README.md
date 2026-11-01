# Manual train-only marker overlay feasibility pilot

This isolated pilot takes two examples from the authenticated 104-project train
snapshot. It groups rows by connected components over study ID and exact
embedded-image SHA-256, then assigns components by a fixed SHA-256 signature to
an internal build partition or an internal train-only holdout. The holdout is
not the official dev split and does not establish historical independence.

The selected Engauge point is only a placement anchor. The extractor verifies
the selected project bytes, embedded PNG bytes, dimensions, and point identity
against the frozen plan before writing source PNGs under ignored `artifacts/`.
The renderer replaces only a fixed opaque white patch and injects a filled
circle plus a short line distractor. Only those two deliberately injected
shapes are labeled. Original structures remain unknown everywhere, including
outside the patch. This cannot estimate full-image false-positive precision.

The two-source pilot tests deterministic grouping, byte/dimension guards,
pixel preservation outside the patch, and label-scope accounting. It is a
feasibility check only. It does not provide enough independent data to support
training, model selection, or a product claim. No optimizer is constructed.

Private project pixels, case IDs, coordinates, extracted source PNGs, overlays,
and receipts stay in ignored E-drive `artifacts/goal22-runs/` outputs. Do not
copy those outputs into Git or user-visible reports. The source snapshot records
0 development payload reads, 0 sealed payload reads, and 0 optimizer steps.

Focused test:

```powershell
tools\gpu-training\venv\Scripts\python.exe -B -m unittest ml.markers.center.manual_train_overlay_pilot_v1.test_pilot -v
```

The metadata-only plan command is:

```powershell
tools\gpu-training\venv\Scripts\python.exe -B -m ml.markers.center.manual_train_overlay_pilot_v1.plan
```

The frozen plan is written to
`artifacts/goal22-runs/marker-train-overlay-pilot-plan-v2/plan.json`. The
source extractor writes only to a fresh `artifacts/goal22-runs/` directory.

The source extractor requires the externally supplied hashes recorded with the
frozen plan and protocol. Its only allowed input split is the existing train
partition. It does not open development or sealed project payloads.

## Completed feasibility run

The fixed source manifest SHA-256 is
`a4722b25227227a89c8a13ba7e4788d7e68ab524c9219760198823a2a254cb57`. The
metadata-only grouping produced 22 study-plus-exact-image connected components
(14 internal build, 8 internal holdout); those partitions are assigned
signatures inside the train set, not independent validation.

The frozen plan is
`artifacts/goal22-runs/marker-train-overlay-pilot-plan-v2/plan.json`, SHA-256
`eac5d357a7440537d209e2c22e2af957e06d4edc39e2036ac69b03d427a26243`. Source
extraction completed for two distinct selected sources. Its
`source-receipt.json` SHA-256 is
`0018f10fa55782c6e9df8834f232bbbc4408136aa86384722555fcb0efeaea80`.
The overlay receipt is
`artifacts/goal22-runs/marker-train-overlay-pilot-render-v3/receipt.json`,
SHA-256 `53d07836b9c55a95a0392c363ebf2b02a059869e976c85404932018d9016cb12`.
It records two source images, four injected shapes, exact pixel preservation
outside the opaque patches, zero dev/sealed payload reads, and zero optimizer
steps. The renderer used Python 3.13.7 and Pillow 12.3.0.

This validates the small deterministic overlay plumbing only. Two sources and
four injected shapes do not support a model-training claim or an estimate of
natural marker-versus-artifact accuracy. A future RX 6600 XT experiment would
need a substantially larger source-grouped train corpus and labels restricted
to injected shapes; native image content remains unknown.
