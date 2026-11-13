<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Cactus Windows capability probe, 2026-11-13

## Decision

Keep Cactus out of the application for this development batch. Its published
Windows runtime loads, but two calls using the publisher's default Mirage
interface failed JSON parsing. The diagnostic retry identified the invalid JSON
number `-nan(ind)` in a text confidence field. No replacement, production
approval or OCR accuracy result follows from these calls. Continue the existing
licensed OCR path and the Rust geometry assistance described in the
[development direction](DEVELOPMENT-RESET-20261111.md).

This does not establish that every Cactus configuration fails. A text-only or
crop-specific configuration was not benchmarked. The current default-interface
failure is sufficient to defer integration while completing the usable workflow.

## Pinned inputs and license

- Runtime: Cactus 4.1.0 Windows x64, publisher revision
  `4e822c9036be2eb0e47ef556df32a818bb1572ef`.
  [Release manifest](https://huggingface.co/Cactus-Compute/cactus/blob/4e822c9036be2eb0e47ef556df32a818bb1572ef/releases/4.1.0/manifest.json).
- Runtime wheel SHA-256:
  `300f66c30a67e907686edd35f05b45ce82b78ae9449214f6776df983dcb2178f`.
  Extracted DLL, 1,870,336 bytes:
  `2b32201652e9bde6ee28a221464f52703b8b02bc03c16f18266aeedcdc61bdb2`.
  The DLL also matched the wheel's RECORD.
- Mirage model, 21,611,043 bytes:
  `4249a0f7515272ff76b9414bec17d7e7daf84b15b66c7fb4f17fce1e66f9f237`.
  [Publisher model card](https://huggingface.co/Cactus-Compute/mirage/blob/3d03cee88c0acbe4e71b94b567ab14e6e55f40c9/README.md).
- The release LICENSE and model card declare Apache-2.0. The actual release
  LICENSE SHA-256 is
  `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30`.
  Runtime source publication remains unresolved, separately from that license
  declaration. Neither runtime nor weights are bundled or added to Git.
- One project-owned synthetic image, 1200 x 1700 RGB8, SHA-256
  `75edb079ccfa8a3a2806bef467ec2dd10d13905bee762fd48ee9be21361be98d`.
  No private, sealed or training data was read; no optimizer ran.

## Method and observations

Used the existing checkout Python/Pillow with `ctypes`, without installing a
new environment or SDK. The pinned
[Mirage wrapper](https://github.com/cactus-compute/cactus/blob/bafcf7348607c89f30ff9fde8e59d20b76baa86f/cactus/agent/mirage.py)
and C API define packed RGB8, a 16 MiB output buffer, JSON options, and a
NUL-terminated JSON response. The probe used the same signature, default `{}`
options and null language/keywords. `CACTUS_TELEMETRY=0`; all temporary paths
were inside the ignored review directory. The process had below-normal priority,
one requested Cactus thread, and a queried Windows Job Object hard CPU cap:
flags 5, rate 416/10,000 across 12 logical processors, approximately half a
logical core in aggregate. No backend-specific Radeon acceleration was selected.
Each process had a 180-second parent deadline.

The interpreter was `tools/gpu-training/venv/Scripts/python.exe` in the primary
checkout. PowerShell started each local probe script with `Start-Process`,
`-WindowStyle Hidden`, stdout/stderr redirected into the review directory, and
`WaitForExit(180000)` followed by termination only on timeout. Both exited
within that bound. The scripts used the call sequence above; they were
`run-one-synthetic-probe.py` and `run-one-synthetic-diagnostic.py` in the ignored
review directory and are disposable, not application dependencies.

| Measurement | Initial call | Diagnostic retry |
| --- | ---: | ---: |
| Image decode | 0.934 s | 0.081 s |
| DLL load | 0.00144 s | 0.00154 s |
| Model read/hash/load | 0.263 s | 0.106 s |
| `cactus_describe` | 14.266 s | 14.738 s |
| JSON parsing | Failed at character 4944 | Failed at character 4944 |

The retry returned status 326 and 7,099 response bytes in the 16,777,216-byte
buffer. Its response SHA-256 was
`010bd2762cb8688acf9801168837f781bd0033fced3dc160caac18c645d69210`.
The failure location contains a confidence value of `-nan(ind)`, rather than
valid JSON. The response is far below buffer capacity and has a closing object;
the observed failure is invalid numeric serialization, not capacity exhaustion.
The first call did not retain its raw response or exact nonnegative status,
so that run alone could not identify the cause.

These are two separate process calls, not a warm-session benchmark or an
accuracy evaluation. Their capped default full-image workload is not directly
comparable with the existing OCR evidence route's CPU configuration or crop
workload. Vendor phone timings are not Windows timings.

## Retention

The local review directory is
`artifacts/cactus-review-20261111` in the active worktree. It contains one model,
the downloaded runtime artifacts, local provenance and small probe outputs.
No source image was copied. Keep it only until this report is pushed and the
diagnostic consumer finishes, then remove the disposable review directory under
[artifact retention](ARTIFACT-RETENTION.md). The existing application models and
the original owned input remain current dependencies.
