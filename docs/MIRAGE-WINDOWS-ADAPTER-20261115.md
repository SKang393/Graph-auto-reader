<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Mirage Windows adapter diagnostic

## Supplied input and scope

The maintainer supplied `C:/Users/Sungwoo/mirage-handoff-20261115/files` for this
explicitly authorized local diagnostic. The helper files have no supplied
redistribution license; no redistribution or product inclusion is claimed.
The lead verified handoff archive SHA-256
`10bdf93bcb38853881b10e474a3c1c9dcc80bb322f1e0d140023cc6b7968b709`.
`HANDOFF.md` was read first; originals remain unchanged. Only the two adapter
files, handoff note and fixed owned synthetic PNG were copied into the ignored
worktree directory `artifacts/mirage-comparison-20261115/supplied`.

The supplied Linux observation is diagnostic only: six planted label strings
and two spurious text detections, with a misleading generated narrative. Neither
narrative nor object output is calibration evidence. The current 0.4.51
application/model baseline is unchanged.

The fixed 600-by-400 PNG, 4467 bytes, has SHA-256
`6f1e161799715d031cd8391263df264030dec2a1baecf3b56fff8ea146c00ca2`.
It is project-owned synthetic content, not private or sealed data. Runtime/model
files, supplied unlicensed helpers and raw diagnostic content remain Git-ineligible; this aggregate record
and owned adapter source descriptions are eligible. No data trains a model.

## Verified dependencies and ABI

Pinned Windows Cactus 4.1.0 release revision
`4e822c9036be2eb0e47ef556df32a818bb1572ef`, manifest SHA-256
`32e182f6f811acde1109084ee5f5c9a606098b95cc8774f27ece8bc1a506de3b`.
Only the Windows wheel, public header, release license and exact Mirage model
were fetched into the isolated folder. The wheel RECORD was verified before
extracting its sole DLL. Exact identities:

- Wheel: `300f66c30a67e907686edd35f05b45ce82b78ae9449214f6776df983dcb2178f`
- DLL: `2b32201652e9bde6ee28a221464f52703b8b02bc03c16f18266aeedcdc61bdb2`
- Model: `4249a0f7515272ff76b9414bec17d7e7daf84b15b66c7fb4f17fce1e66f9f237`
- Header: `9be7eb25b1d89180495d04a0d2c27053f282dac85add50cacc48d73302819d9d`
- Apache-2.0 license: `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30`
- Pinned model card: `9c69381d336583e6b4069663f4028aeee9c0512d5abc6781c63d96ff54554d4f`

The actual header declares `cactus_describe(const unsigned char* rgb, int width,
int height, const char* options, const char* language, const char* keywords,
char* out, int out_capacity) -> int`. Negative status is failure; nonnegative
status is not proof of valid output. The pinned public header does not document
positive status semantics. The diagnostic retains raw status, bytes/hash and
nonfinite token field/offsets separately from strict and supplied-adapter parsing.

Both release license and model card declare Apache-2.0; runtime source
publication/production admission remain unresolved. Nothing is bundled into the
application. No new environment, installation or paid API was used, and no
application package was created for this diagnostic.

## Preparation and owned Windows results

Existing Python: `E:/Graph-auto-reader/tools/gpu-training/venv/Scripts/python.exe`.
Supplied `response_adapter.py` and `test_adapter.py` remain byte-identical.
Five adapter tests passed in 0.4675967 seconds process wall. That adapter repairs
only a confidence-key `-nan(ind)` to unknown/null; it does not repair `score`.
Fresh Windows output must establish its actual field before any extension.

`prepare_windows.py` only fetches/verifies assets. The separate preflight checks
source/asset hashes and creates,
assigns and queries a half-logical-core Windows job with BelowNormal priority.
The actual probe requests one Cactus thread and disables telemetry. Default
options remain exactly `{}` with null language/keywords and the same PNG bytes.
The native output buffer is 16 MiB. A 170-second process timer plus 175-second
parent deadline bounds the owned call. No confidence becomes certainty; no
calibration is applied. After explicit lead review, both fixed calls ran with:

```powershell
pwsh -NoProfile -File artifacts/mirage-comparison-20261115/run-owned.ps1 -Mode default
pwsh -NoProfile -File artifacts/mirage-comparison-20261115/run-owned.ps1 -Mode text-only
```

| Mode | Process wall | Describe | Native status | Raw bytes |
| --- | ---: | ---: | ---: | ---: |
| `{}` default | 15.6912039 s | 13825.0113 ms | 171 | 3576 |
| `{"sections":["text"]}` | 17.9558444 s | 13071.7198 ms | 165 | 437 |

Both exited 0, load status 0, with strict-valid JSON, diagnostic-valid parsing,
zero repairs and zero nonfinite tokens. Both retained the same eight
`string`/`bbox`/`score` rows: `10,1,5,0,+,1,2,3`. Six are planted labels;
the extra `1` and `+` remain visible spurious detections, never suppressed by
fixture-specific rules. All scores were finite. The actual field is `score`;
no score repair was used for these calls. Separate cold calls do not establish warm speed or
independent accuracy. The old larger-input NaN was not reproduced by this input.

Raw SHA-256: default
`75e16a632d5845aca8e32010b7a5e39882a3467989e896650663570988581592`;
text-only `5b933edaabaeed4dfbefb98444c50fce35911e080f245195c88c738d2be6aa01`.
Both helpers stayed unchanged: `windows_probe.py`
`e4a7978e0c96e2aa798864c1157ff6182aab03ebfe8d2ebbe1da27c4e73cd350`,
`run-owned.ps1` `19ddde1ab391e59d11d5cf4e3656b3e010cb81a79d3d471528240c4b6f6b94c1`.

## Persistent transport and original-input reproduction

Source-ready `windows_worker.py` reuses the verified support functions and
unchanged supplied adapter. It emits a checksum/budget-bound
ready frame, then accepts bounded UTF-8 JSON lines with request ID, dimensions
and packed RGB base64. Options are always text-only. One capped model instance
is loaded for the process; source-side limits are 4096 per side, 16,777,216
pixels, 16 MiB native output. Parent deadlines kill the owned child if needed.
Raw/private pixels and replies remain memory-only; only text rows are returned,
including spurious rows, null unknown scores, strict/diagnostic flags and hashes.
The consumer must require strict validity, no repairs/errors and known scores
before calibration use. Narrative/objects are excluded. Worker SHA-256:
`050b63e76f9625fd647e4968d22a8c1cedab8c1ceec418efd925c52b45fc4867`.
No persistent or private invocation has run in this session.

A separate original-input wrapper was reviewed and executed once by the lead.
It read the authorized 1200-by-1700 owned source in place, after checking 2,327,711 bytes
and SHA-256 `75edb079ccfa8a3a2806bef467ec2dd10d13905bee762fd48ee9be21361be98d`.
It preserves `{}`, the existing helper/pins and 170/175-second limits, without
copying the image or changing parameters. Wrapper/launcher hashes:
`8496404babd31e227c39632b9c00dbb58aed2c652d6b633d2f830337bb3b55ef` /
`fb4d59143ad5354f70e217276f0ff0640c9f6cc2b16a330d929dd07a9475f7b6`.
Exit 0 took 14.0977099 seconds wall, 13184.9531 ms describe. Native status 326,
7099 bytes, raw SHA-256
`010bd2762cb8688acf9801168837f781bd0033fced3dc160caac18c645d69210`:
identical to the historical response. Strict parsing failed at character/byte
4944 on text row `t`, bbox `[972,0,1000,23]`, `score:-nan(ind)`.

New project-authored `score_diagnostic.py` recovers only that observed text-row
score token as null/unknown, preserving raw hash, exact offsets and repair
metadata. It refuses non-text score repair, bbox/other-field NaN, overflow and
marker collisions; quoted strings and finite scores remain unchanged. Its SHA-256:
`a53be74d1f8c3f634f73dd40fa8ca22d797e832d99964d34145a130cba4a7cbe`.
The worker binds it in `score_diagnostic_sha256` and uses it only for diagnostic
rows after strict failure. Successful-calibration strict guards are unchanged.

Nine pure helper/transport tests passed in 1.228627 seconds, with zero native
executions. Offline inspection of the retained original raw recovered 32 text
rows: 31 finite scores plus the one null-score `t` row. The resulting response
still has `strict_json_valid=false`, `parse_error=NATIVE_JSON_INVALID`, one
repair at offset 4944 and `requires_review=true`. Neither a corrected JSON view
nor unknown confidence validates the native response. Historical raw/result,
supplied adapter and `windows_probe.py` bytes remain unchanged. No native rerun
or parameter change was used for recovery.

Retain the isolated subtree only for the named owned Windows check and subsequent
paired harness integration. After their pushed outcome and consumer release,
retire disposable binaries/model/raw outputs; preserve the C: originals and
the sole packaged application. These owned adapter checks read no private,
sealed or training inputs. The subsequent authorized fixed development pass
is recorded separately in the
[paired pipeline outcome](MIRAGE-PAIRED-PIPELINE-20261115.md).

## Authored reproducibility sources

Nine project-authored Python/PowerShell helpers and pure tests are preserved
byte-for-byte under
[`tools/GraphReader.CalibrationOcrProbe/mirage_windows`](../tools/GraphReader.CalibrationOcrProbe/mirage_windows/README.md).
The README records exact source pins and local-only setup prerequisites. No
supplied unlicensed helper/test/handoff, image, raw/result, DLL/model or other
payload is included. Copies were hash-checked against the reviewed active
sources; no helper behavior or active run file changed. This permits later
scratch retirement after the pushed outcome and active paired consumer finish,
without adding a product dependency or authorizing another run.
