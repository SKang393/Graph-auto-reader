<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Provisional session-origin verification

The previous candidate workflow reported that the first observed marker was not
session one on some panels that also had no absolute session origin. An
unlabeled tick can precede the first marker, but its provisional lattice
position alone cannot establish the marker's absolute session. The axis fitter
now retains ordinal-only assignments and reports that the absolute origin needs
review. It also refuses to place a requested maximum session using that
provisional origin.

Compare the same 20 authorized, exposed training images and 21 panels against
the pinned `wide-horizontal-review-v2` capture. Use the same unapproved
candidate composition, model binding, original raster manifest, and CPU inference.
The current composition also includes the later unreadable or non-tick
orientation-review safeguard, which did not have its own full-image comparison.
Record aggregate calibration statuses, presence of the no-origin reason, removal
of unsupported first-session claims, markers, review flags, and completed
workflows. Check separately for any other changed reasons or observations.
Neither the training images nor the imperfect annotations establish general
accuracy; all unresolved calibrations must still require review. No model
training, sealed payload access, production activation, or release is part of
this verification.
