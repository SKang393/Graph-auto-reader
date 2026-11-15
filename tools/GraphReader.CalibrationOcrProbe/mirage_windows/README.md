<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Local Mirage Windows diagnostics

These nine project-authored files are byte-identical copies of the reviewed
2026-11-15 diagnostic sources. They preserve reproducibility when ignored run
outputs are retired. They are tooling only, not an App dependency or production
OCR admission. See [the outcome](../../../docs/MIRAGE-WINDOWS-ADAPTER-20261115.md).
Local Git attributes preserve the exact helper bytes and line endings required
by the reviewed runtime handshake hashes.

No supplied adapter, handoff, image, response, runtime, model or other payload
is included. The supplied `response_adapter.py` has no redistribution license;
the maintainer authorized local diagnostic use only. Obtain that file from the
authorized handoff, rather than downloading a substitute. Its SHA-256 is
`224a74de1ffccc11f438fbb6fbc7b2f1b74ef86434e254d418a557b6f199eccc`.

## Setup in an ignored scratch directory

1. Copy these `.py` and `.ps1` source files to an explicitly selected ignored
   scratch root. Keep downloads/output there, never in this tracked directory.
2. Supply the authorized adapter under `<scratch>/supplied/response_adapter.py`.
   Owned modes additionally expect the exact authorized `synthetic.png` and
   `test_adapter.py` there. Their pins are in `windows_probe.py`; no originals
   or raw handoff results need changing. No font binary is needed.
3. Fetch the following two public text files into scratch, checking their exact
   SHA-256 before use. They are prerequisites to `prepare_windows.py`:

   | File | Pinned source | SHA-256 |
   | --- | --- | --- |
   | `release-manifest.json` | `https://huggingface.co/Cactus-Compute/cactus/resolve/4e822c9036be2eb0e47ef556df32a818bb1572ef/releases/4.1.0/manifest.json` | `32e182f6f811acde1109084ee5f5c9a606098b95cc8774f27ece8bc1a506de3b` |
   | `model-card.md` | `https://huggingface.co/Cactus-Compute/mirage/resolve/3d03cee88c0acbe4e71b94b567ab14e6e55f40c9/README.md` | `9c69381d336583e6b4069663f4028aeee9c0512d5abc6781c63d96ff54554d4f` |

4. Run the copied `prepare_windows.py`. It verifies the pinned Cactus 4.1.0
   manifest, downloads only the Windows wheel/header/license and Mirage model,
   verifies wheel RECORD, and extracts only its pinned DLL. This executes no
   Cactus code. Runtime/model declarations are Apache-2.0; distribution/admission
   is not inferred from successful downloading.

Example commands use the existing local Python, without installing an environment:

```powershell
$python = 'E:/Graph-auto-reader/tools/gpu-training/venv/Scripts/python.exe'
& $python -B "$scratch/prepare_windows.py"
& $python -B -m unittest discover -s $scratch -p 'test_*.py' -v
```

The nine project-authored pure tests do not invoke Cactus. The supplied adapter's
separate five-test suite can be run from `<scratch>/supplied` when authorized.

## Bounded native commands and interpretation

After verifying source/asset identities and obtaining the assigned execution slot:

```powershell
pwsh -NoProfile -File "$scratch/run-owned.ps1" -Mode default
# Separate authorization/result review preceded the fixed text-only call:
pwsh -NoProfile -File "$scratch/run-owned.ps1" -Mode text-only
# Exact original owned input, without copying the image:
pwsh -NoProfile -File "$scratch/run-original-default.ps1"
# Persistent fixed text-only JSON-lines transport, parent controls deadlines:
& $python -B -u "$scratch/windows_worker.py"
```

Paths and source identities are run-specific. The owned launchers use the
existing Python path above; the original-input wrapper names the explicitly
authorized 1200-by-1700 source in place. Missing/mismatched inputs fail closed.
Do not replace them with a private image or silently adjust pins/options.
The historical C# paired modes bind the exact ignored scratch path recorded
in `MiragePairedWorker.cs`, plus the unchanged 0.4.51 baseline. They are closed
diagnostic routes, not current App or CI dependencies. Retiring scratch leaves
those native modes unavailable until their prerequisites are deliberately
recreated under a new authorized task; the pure C# fake/mapping modes do not
need these assets. This README does not authorize repeating the private pass.

Owned launchers enforce 170/175-second bounds and collision refusal. The worker
verifies a BelowNormal half-logical-core Windows job before loading Cactus,
requests one native thread, and reuses one model. Persistent mode never writes
request pixels or raw replies; it returns all text rows, including spurious
ones, with normalized 0–1000 boxes relative to the provided image.

Strict JSON validity is independent of diagnostic recovery. Only the observed
text-row `score:-nan(ind)` token can become diagnostic null/unknown. A repaired
response remains strict-invalid with nonempty repair metadata. No strings or
finite scores change; invalid boxes, other nonfinite values and overflow fail.
The successful-calibration consumer must still require strict validity, no
errors/repairs and known scores. Narrative and objects are never evidence.

## Reviewed source identities

| File | SHA-256 |
| --- | --- |
| `prepare_windows.py` | `0557bd31d511c5674d97c641b8c9f250cb4862a5409a7c4eb83529667c02de96` |
| `windows_probe.py` | `e4a7978e0c96e2aa798864c1157ff6182aab03ebfe8d2ebbe1da27c4e73cd350` |
| `windows_worker.py` | `050b63e76f9625fd647e4968d22a8c1cedab8c1ceec418efd925c52b45fc4867` |
| `score_diagnostic.py` | `a53be74d1f8c3f634f73dd40fa8ca22d797e832d99964d34145a130cba4a7cbe` |
| `test_windows_worker.py` | `11fc32b052241c0d9c122f7bd3c7852f3a3a3e13ac7c7fbc850402a0ce8d1af1` |
| `test_score_diagnostic.py` | `4f3d9a46ae54aae35e718e690bd149079504547c6ca2ebb03f6060457b6cb300` |
| `windows_original_default.py` | `8496404babd31e227c39632b9c00dbb58aed2c652d6b633d2f830337bb3b55ef` |
| `run-owned.ps1` | `19ddde1ab391e59d11d5cf4e3656b3e010cb81a79d3d471528240c4b6f6b94c1` |
| `run-original-default.ps1` | `fb4d59143ad5354f70e217276f0ff0640c9f6cc2b16a330d929dd07a9475f7b6` |

After outcomes are pushed and active consumers finish, retire scratch downloads,
models/raw outputs/caches. Preserve authorized user originals and the current
application package. Source preservation authorizes no further experiment.
