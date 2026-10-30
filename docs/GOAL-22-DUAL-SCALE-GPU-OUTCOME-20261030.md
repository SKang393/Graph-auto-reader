# Dual-scale context trial P1

P1 failed the preregistered development bars and is not approved for release or production use. Its one authorized trial is closed. No private or sealed data was read, so no sealed run was consumed.

At the fixed confidence threshold of 0.1, the candidate and frozen V37 baseline produced:

| Development set | Baseline precision / recall | P1 precision / recall | Baseline TP / FP | P1 TP / FP | Truth markers |
| --- | --- | --- | --- | --- | --- |
| Component, 167 scenes | 92.13% / 96.96% | 90.23% / 97.21% | 1943 / 166 | 1948 / 211 | 2004 |
| Family, 9 scenes | 77.69% / 98.06% | 72.40% / 98.06% | 202 / 58 | 202 / 77 | 206 |

Both sets required at least 95% precision and 95% recall. The family prohibited-structure hit rate was 1.08% for P1. Operating-threshold sensitivity in the report is descriptive only, not a replacement acceptance rule.

The run trained eight epochs and 2,264 optimizer steps on `AMD Radeon RX 6600 XT`, `gfx1032`, `cuda:0`; it recorded zero CPU optimizer steps. The local V37 state remained unchanged. ONNX comparison covered 270,236 rows, with maximum absolute error 5.7220458984375e-6 and zero confidence decision changes. Export emitted PyTorch tracing warnings, and MIOpen reported missing grouped-convolution tuning; the recorded comparison and development metrics are limited to these evaluated inputs. Bitwise reproducibility was not guaranteed by the runtime.

The family training population differed from V37's population, and the family artifact masks were incomplete. Family training and validation sources were disjoint, but the historical component benchmark does not have established training-source disjointness. The run therefore does not isolate architecture effects and is not a production-input or independent holdout result.

- Source commit: `e7e35dde8688a97661762a1a2cdeea266c3f7b3b`.
- Runner source bundle SHA-256: `32a5be1eccaa13fca814784d737c18f90a2145815a5cfac14e03d78748e64b7c`.
- Immutable source snapshot SHA-256: `f9a5246b9870522c013329a560616f31d30ebddcc4f84fde6deca73305cbf6b5`.
- Full report SHA-256: `f0c783914a37bed645171f53d7262f495c019d40a6ac9f299017d44d9e011edf`.
- Checkpoint SHA-256: `44fb6ce4a1fa7b43a03eeccae02cb9c9db99ca1e8de4e0fbe79cd2b29a2968b8`.
- ONNX SHA-256: `4810ca08b15c033c67a832ff2f3fdbe44f7a46c4b3b79b359ddf61835268cf41`.

The report and full training seal remain under `ml/markers/center/dual_scale_context_v1/P1-run-20261030` and `ml/markers/training-seals/marker-center/marker-center-dual-scale-context-v1/P1`. The compact tracked outcome is `ml/markers/center/dual_scale_context_v1/P1_RESULT.json`. The preregistered runner was launched through `tools/gpu-training/Run-GpuPython.ps1` with `-m ml.markers.center.dual_scale_context_v1.runner` and the P1 output directory.
