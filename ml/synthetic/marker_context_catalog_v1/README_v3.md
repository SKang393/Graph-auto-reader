# Train-only marker context preflight v3

`preflight_v3.py` renders eight fixed in-memory train scenes, one for each
design and connection-mode combination. The source and font inventory, recipe
indices and required bins are bound by `preflight_binding_v3.json`, SHA-256
`76c91cd1bb6a87e86e459a816b101f0f99522a174b173d16ed41bd8991b6c6ca`.
No generated raster files or model artifacts are written.

On Python 3.13.7, Pillow 12.3.0 and NumPy 2.3.5, the bound run reported:

| Design | Connected positives / visible negatives | Isolated positives / visible negatives |
| --- | ---: | ---: |
| AB | 20 / 1 | 17 / 1 |
| ABAB | 28 / 1 | 20 / 0 |
| Multiple baseline | 17 / 1 | 22 / 1 |
| Alternating treatments | 28 / 2 | 28 / 2 |

Every reported negative passed a single-glyph ablation check. Overall required
bins are nonzero, but the ABAB isolated cell has no negative. A nonzero bin is
not evidence of adequate distribution or model performance. No dev, reserved,
private or sealed scene was rendered, and no optimization occurred. Source
files are hashed before and after but are not locked; image-level split
independence, external generator independence and production crop/mask parity
are not established. This diagnostic does not approve training or production.

Run from the project root, with temporary paths kept inside the project:

```powershell
$env:PYTHONDONTWRITEBYTECODE='1'
$env:TEMP='E:\Graph-auto-reader\artifacts\test-temp'
$env:TMP=$env:TEMP
python E:\Graph-auto-reader\ml\synthetic\marker_context_catalog_v1\preflight_v3.py --binding-sha256 76c91cd1bb6a87e86e459a816b101f0f99522a174b173d16ed41bd8991b6c6ca
```

The repeated preflight output is stored at
`artifacts/goal22-runs/marker-context-train-preflight-20261031-v3.log`
(SHA-256 `f4d34ae271bcb4a0513bd19f7abcc79149fdc0fbaa9a050e006ef6dfc9e4d16e`).
