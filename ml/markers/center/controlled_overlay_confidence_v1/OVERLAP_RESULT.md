<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Controlled patch overlap and conservative split

The source-grouped controlled dataset contained 70 holdout tensors identical
to build tensors. In all, 92 of 120 holdout patches had an ink plane already
present in build, including those 70 exact three-plane duplicates. The overlap
was discovered after materialization; this audit and its split are post-hoc,
not blind. No model predictions were used to select exclusions.

Keeping the first build occurrence of each ink plane leaves 194 patches: 98
markers and 96 distractors from ten contributing groups. Excluding build ink
planes from holdout and keeping one occurrence of each remaining ink plane
leaves 24 patches: ten markers and 14 distractors from two groups. The audit
excluded 20 repeated build ink planes and four repeated novel holdout ink
planes. No identical ink plane carried conflicting controlled labels.

An independent readback confirmed 194 and 24 unique ink hashes with no
cross-partition overlap. This protects against the measured exact-pixel
leakage, but the samples still reuse a small set of artificial shape families.
The two-group holdout is too small to establish product accuracy. The original
334-patch dataset and all exclusions remain available inside project artifacts.

| Evidence | SHA-256 |
| --- | --- |
| Protocol | `03829143d1c5e4f45417fa56ea77ef2dfcc57d4526817eacb3806997b47a4` |
| Registration | `0f9cd9617c4722ecb5769021a676f3843cd87111fba5586b5b12d5e3ebcfd18f` |
| Dataset receipt | `d1390de8797a75bc0c821996a327938b1dcd729de4f7b1f67d130e3c5fe29de6` |
| Audit and selection report | `866dea7af54ab5c87f7c63fb37540ed94cef61195fcffd40754edb1fc351f080` |

Four focused tests and pyflakes passed. No model inference, optimizer step,
development or sealed payload read, or production approval occurred in this
audit. Selection indices and per-patch identities remain project-local.
