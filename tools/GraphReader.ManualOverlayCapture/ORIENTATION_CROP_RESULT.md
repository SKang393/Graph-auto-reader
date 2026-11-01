<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR orientation-only crop reconstruction

For each of the two previously traced horizontal tick labels, the production
cropper reproduced the recorded workflow crop exactly using the original bounds
and detector angle. Changing only the angle to horizontal reproduced the
recorded direct-reread crop exactly. The earlier recognizer trace showed that
the workflow readings were wrong and the direct-reread readings were exact.
This isolates the crop-angle difference for these two exposed examples; it
does not establish accuracy on other text or behavior on vertical labels.

| Evidence | SHA-256 |
| --- | --- |
| Protocol | `65615d34b797958eba9982f8908508299541ed8111fbca08e647bd62acfe56c7` |
| Pre-run registration | `1dc856da1c23b61948653bffe35cae5b94455c3236bf16731672fa52fac5c08e` |
| Runtime description before and after | `4403047c86f8652e00e0f88d41c07b22c23e485c824bfe20a7672b25c2ae0e6b` |
| Aggregate reconstruction | `6af83b2d6e968183037bf165d7a9b77978ad064432951ea5ff1d58d83f940106` |

The Release diagnostic build passed with no warnings. Registration inputs and
the before/after runtime descriptions were verified. This reconstruction ran
no recognizer or optimizer, opened no sealed payload, and changed no model
approval or release gate. Private per-region evidence remains project-local.
