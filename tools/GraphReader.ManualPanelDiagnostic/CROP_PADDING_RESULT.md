<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR horizontal-padding comparison

The same candidate recognizer, decoder, source boxes, vertical padding, and
runtime were used with standard horizontal padding and with zero horizontal
padding. The zero-padding experiment was chosen after observing the padded
development results, so this is an exploratory comparison on previously
exposed images, not an independent validation.

| Measurement | Standard padding | Zero horizontal padding |
| --- | ---: | ---: |
| Training: exact reads on 46 matched reviewed labels | 46 | 46 |
| Training: numeric primaries on 16 unmatched proposals | 7 | 1 |
| Development: exact reads on 115 transcribed labels | 111 | 108 |
| Development: correct parsed numeric values on those 115 | 115 | 115 |
| Development: numeric primaries on 12 other-text fragments | 4 | 8 |
| Development: numeric primaries on two structure and one uncertain crop | 2 | 1 |

All three development exact-read regressions were literal `1` becoming `l`,
which the numeric parser interprets as 1 but the nonliteral-reading safeguard
requires users to review. Four other-text fragments that previously produced
nonnumeric text produced numeric primaries with zero horizontal padding, and
the four existing numeric other-text primaries remained numeric. The training
set also has six reviewed labels without a matching proposal; all six remain in
the denominator as missed. Unmatched proposals were not assumed to be false
positives.

The development results do not support globally removing horizontal padding.
Both profiles misread some nonnumeric crops as numbers, and neither result
establishes localization, role assignment, complete calibration, or automatic
recognition. The candidate and model remain unapproved; production crop
settings and review gates were not changed.

| Evidence | SHA-256 |
| --- | --- |
| Comparison protocol | `51f0f4c0a2ecc1c9e9c72cd2d62ac9bf032055422656a7245eb78daaf524cbd3` |
| Pre-run registration | `b2868f334f0e3c581b0aed5f6eaf184fe450d4dd282cbebc0e7df617e987a193` |
| Runtime description before and after | `a700c13055f63fa817043a5667165888587276e7c5a2a8a6fd38c99238185295` |
| Training, standard / zero padding | `379523781fd5e16b9186cd50d901e69d4319e2b0487c8450020fbefc0e527472` / `7efa7264a50fe7d29988b8af9d08e3ef36233c176f69257b9cfe63d793ec63c8` |
| Development, standard / zero padding | `d4456528962d6dcd22a8d52ab4ebad9cf68ed24a561fd119280050d938a662eb` / `c727c0d1a45c0091c998c2278c5a416e3ce0556641294b46437c74314d89ef73` |
| Aggregate comparison | `9d5b68eb680f5b3da4983bd9fc2ece8d4b54f7a6c657483b0dd947732b079535` |

The repeated standard-padding outputs were identical to the previous readings
for all 62 training and 134 development proposals. The diagnostic Release
build passed with zero warnings; six focused tests and pyflakes passed. The
captures used CPU inference with no optimizer steps or sealed-payload reads.
Private images, labels, and per-crop outputs remain in project-local artifacts.
