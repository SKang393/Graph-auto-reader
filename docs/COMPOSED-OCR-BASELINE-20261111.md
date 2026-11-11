<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Frozen composed OCR baseline, 2026-11-11

This is an owned synthetic, open train/dev diagnostic at source
`facfabf76ccf0305153dc2daddabcb9f91ff4e76`. It does not activate a model or
authorize a sealed/real run. Private reads, sealed reads and optimizer steps: zero.

## Inputs and execution

The canonical inventory contains 20 train and 3 dev sources, 39 panels total.
All 69 image/annotation/scene identities were checked. The result receipt also
rechecked 79 input bindings and 63 runtime files after execution.

| Binding | SHA-256 |
| --- | --- |
| Request | `24de1d2797b5916fd78d4785ecdf5b533308e6943e79d228c1567a03185685ce` |
| Generator report | `2891886457562d2d49fb96ef3b3b635463299b2013b1eddac03332dc10c31598` |
| V44 detector | `615495cc882b6ae8633081d89c3ab08c0d4a80a04aa36bbde4157fc7828f5923` |
| English reader | `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743` |
| Retry candidate | `9a76a93f97c300f917e523b4b25802746338a89257c5a1f823e3a20efe9b821b` |
| Result | `8eedadcb3416b5e2a195b2f77ecb2f10e1aa8de7cf99a3b9de724de3c7367708` |
| Execution evidence | `dca38a7850a0ab0b18f9a6298ab38a7db77086360fcde8fe10bcd941c1f7cbd7` |
| Outcome receipt | `8db823b6b2a111a0eda614d13fff0dc5204b9642f1bce61df7dc35b544fcacb1` |

Composition: `original-db-head-tick-header-and-legend-lanes-v4`, CPU,
one intra-op thread, graph optimization disabled. The compile took 52.91 seconds
with zero warnings/errors. The successful invocation was:

```text
runtime/GraphReader.SyntheticRuntimeEvidence.exe --check-composed-ocr-prerequisite-dev artifacts/goal22-runs/composed-ocr-parent-development-v1/request.json 24de1d2797b5916fd78d4785ecdf5b533308e6943e79d228c1567a03185685ce artifacts/goal22-runs/composed-ocr-v44-facfabf7-retry-20261111/candidate.json 9a76a93f97c300f917e523b4b25802746338a89257c5a1f823e3a20efe9b821b
```

Working directory: historical worktree `artifacts/goal22-worktrees/resume-20260908`.
The executable was in that run's `runtime` directory. It exited zero in 97.229
seconds wall time; producer timing was 93.612 seconds, about 4.07 seconds per
source. This includes the composed pipeline and does not isolate its stages.

## Results

| Measure | Train | Dev |
| --- | ---: | ---: |
| Sources / panels | 20 / 30 | 3 / 9 |
| Truth regions | 709 | 183 |
| Raw TP / FP / FN | 616 / 30 / 93 | 157 / 41 / 26 |
| Assembled TP / FP / FN | 641 / 14 / 68 | 182 / 7 / 1 |
| Assembled precision | 97.8626% | 96.2963% |
| Assembled recall | 90.4090% | 99.4536% |
| Exact recognition | 420/709, 59.2384% | 178/183, 97.2678% |
| Role correctness | 472/709, 66.5726% | 176/183, 96.1749% |
| Character errors | 1318/3415, 38.5944% | 14/1019, 1.3739% |
| Explicit recognition failures | 0 | 0 |

The existing pure checker confirmed aggregate accounting and the canonical dev
bars. It did not replay inference or perform production admission. The train
split fails four of the corresponding metric bars; this remains visible rather
than becoming an invented additional gate.

All three dev images use the generator's system-mono font profile. Train has
10 system-sans and 10 system-serif images. Train also has 25 recorded layout
repairs while dev has none. This measured distribution mismatch limits the
interpretation of a three-image dev pass. It does not prove a font-specific
cause, justify tuning against private answers, or establish real-image quality.

## Initial attempt and retention

The first attempt exited 1 after 69.875 seconds with
`COMPOSED_OCR_MEMORY_DEV_FAILED:inference` and an empty result. It is void with
no metrics and no sealed-budget consumption. Its adjacent OpenCV binary was
`1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd`,
different from the declared reviewed runtime. That mismatch could independently
cause post-run identity failure, but the generic error does not prove the cause.
Installing the already reviewed runtime and creating a distinct candidate
receipt preceded the successful retry. No model, source or metric bar changed.

The two completed run directories are disposable after this aggregate record
is pushed and active inspections finish. Keep the canonical source inventory
and current selected models; do not retain copied runtimes or predictions merely
for possible future use. A future benchmark regenerates its own candidate
against its actual source/runtime identity. Retention closeout is recorded below
after verification. Production acceptance remains **FAIL**.

After outcome commit `8399b567e853aa806e507e9d837ecfb4a15bffe7` was verified
on `origin/main`, both run directories above were removed: 132 files,
130,373,975 bytes. Exact absolute-path containment, no-reparse checks and an
inactive-consumer process check preceded removal. Canonical inputs, selected
model payloads, reviewed native dependencies and the current build were kept.

## Open diagnostic scope correction

The explicit owned-input diagnostic command now accepts both train and dev
sources and preserves each source's split in its diagnostic projection. This
permits investigating the measured distribution mismatch without relabeling
train as dev. Private, real, sealed and registered-reserve inputs remain rejected;
the aggregate/admission path is unchanged. The tool build passed with zero
warnings/errors. `--self-test-composed-ocr-memory` passed 69 checks in a combined
8.480-second build/self-test invocation, with zero inference/private/sealed reads.
