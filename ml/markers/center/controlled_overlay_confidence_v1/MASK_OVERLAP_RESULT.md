<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Synthetic mask-overlap result

The frozen candidate's ONNX outputs and 45,396 authenticated proposal patches
were reproduced for all nine synthetic family scenes. At the unchanged 0.1
operating threshold and five-pixel matcher, 203 detections matched annotated
markers and 58 did not.

| Detection group | Count | Center on synthetic text mask | Center on synthetic artifact mask | Center on both |
| --- | ---: | ---: | ---: | ---: |
| Matched | 203 | 29 | 72 | 0 |
| Unmatched | 58 | 20 | 3 | 0 |

The mask is queried at the rounded predicted center with the fixture's existing
0.5 binary-mask cutoff. This is not a runtime filter: it describes synthetic
annotation channels, which can cover true markers and need not match the
production OCR or artifact masks. In particular, suppressing every detection
whose center overlaps either channel would discard 101 matched detections in
this sample, without accounting for any changes to non-maximum suppression.
These observations do not establish the causes of the unmatched detections,
full-scene product accuracy, or a safe suppression rule.

| Evidence | SHA-256 |
| --- | --- |
| Pre-run registration | `b3fc21e1356f5a84d0ea09d60f1aebfb7f906b37bcfda0e06151703f9b0459c3` |
| Protocol | `82f9b307fd1d1144409f58bf3917adb6084e61c090c5d4e058e7af0945d97e2b` |
| Diagnostic source | `95ca608f2165648667a99294e11142d43fea4481d7691d50b223844109c42bd1` |
| Frozen development report | `056aaca3479a8c77b7d5eda37ee55b6d77013282d2079082cbe887ec5956cd19` |
| Candidate ONNX | `35b50adbd6961293b888c5de3f2e5594b9cda7a9cb95d6c68b425999271a1535` |
| Aggregate result | `379cc887cd39edb163a75b09b70e80d12c105e5039085fa84019380b3f0fb370` |

The diagnostic used CPU ONNX inference, no optimizer steps, no real-image
reads, and no sealed-payload reads. One focused test and pyflakes passed. The
candidate remains unapproved; the output remains in the project-local active
investigation folder.
