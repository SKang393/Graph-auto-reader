<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# OCR orientation-only crop reconstruction

For the two previously traced training cases, reconstruct the crop directly
from the checksum-verified full source image, frozen detector bounds, and
frozen detector orientation. Produce a second crop using the same bounds and
horizontal orientation. Use the exact production cropper and candidate crop
options, and compare their pixel hashes and dimensions with the recorded
workflow and direct-reread crops. Read no visual labels, run no recognizer or
optimizer, and open no sealed payload.

If both hashes match for a case, the captured crop difference is accounted for
by the orientation setting under the reconstructed conditions. This checks two
exposed examples, not detector accuracy, genuine vertical-text behavior, or
release acceptance. No production rule or model approval changes during this
diagnostic. Inputs and per-region evidence remain project-local.
