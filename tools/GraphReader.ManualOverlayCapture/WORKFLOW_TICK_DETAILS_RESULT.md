<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Detector-emitted tick evidence

The unapproved candidate workflow was rerun on the same 20 original training
images with its tick observations retained. All 21 panels kept the same status,
error, center observations, and accepted markers as the preceding capture. All
20 images failed calibration, all 21 panels returned the structured
recalibration-required failure, and 20 panels flagged axis geometry for review.
The center and accepted-marker stages again matched 250 and 207 of 332 recorded
points. Those point annotations are incomplete and cannot establish precision.

The workflow emitted 115 Y-tick regions that its numeric parser accepted; all
115 primary strings were literal numbers. Eighteen panels emitted at least
three numeric Y-tick regions. Seventeen panels had a competing numeric-reading
warning, covering 51 of those regions. The existing axis-connected warning
appeared on one panel. These counts describe emitted candidates, not the
accuracy or recall of the detector.

The exploratory relative-position rule from the crop study identified three
regions across two panels. After the selection was frozen, an assistant
inspected a prediction-blind sheet and classified all three as fragments of
vertical text, not tick labels. Two already had region-specific review
warnings; one did not. Both panels still had separate axis-review warnings.
The same assistant had already inspected this image collection, and the three
examples do not establish a general error rate or safe automatic acceptance.
No recognition or release gate was relaxed in this diagnostic.

| Evidence | SHA-256 |
| --- | --- |
| Capture protocol | `b233194fe52ee4fb1ce8f0ba968f0eca46eea6e0d51f2771f52b744652215678` |
| Pre-run registration | `27497bd986da8fd84b1dd629700fdb5cdd3f82df48deb6365a2b7ad863faaccc` |
| Runtime description before and after | `1590d1e3cb183b39a02bc3a2858fd0eb5b94fca5dcba7352365fda1948b8c10e` |
| Full workflow report | `20437621a2de87625273f2ca3e6148e2b06bf56f374bd6236d509873b73226a3` |
| Aggregate tick score | `905f5a9730f8cb16243ddb9beffbdd1db9e31902e504b952d652fdc02e1e8542` |
| Aggregate marker score | `cb6e61c774fb03c85f7e7e31b6b794c94b660b073848a5281dd2ae5e8187b203` |
| Private review sheet manifest / labels | `a45e054c18ea9241fac9e215e6ac6f44b9d020fb9024961991b0f6f98fbbb805` / `344f84c0d66f786578c7451be47deb1c284c7da9b4e5eceeb1fe5ee5e483e903` |

The diagnostic Release build passed without warnings. Six focused scorer and
review-selection tests and pyflakes passed. CPU inference and image processing
were used; no optimizer or sealed payload was used. Private images, per-region
text, and review sheets remain in project-local artifacts for the active lane
investigation.
