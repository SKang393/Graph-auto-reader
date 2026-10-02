<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Proposal model production registration

The application previously selected the proposal adapter only for V24 P1's
historical model ID and checksum. A later approved proposal model would enter
the incompatible dense adapter. Its raster artifact provider also had no
production registration path.

Composition now selects proposal models through their checksum-bound tensor
contract. The factory validates model identity, payload, manifest, CPU approval,
preprocessing, geometry and threshold before initializing the inference host.
Only the existing five supported profiles are accepted: full-frame or plot-domain
legacy geometry, enclosed geometry, and balanced enclosed geometry at 0.25 or
the existing cascade cutoff of 0.10. Unsupported combinations fail closed.

The workflow evidence validator compares the actual adapter's proposal domain,
geometry and threshold. It no longer infers those settings from a partial ID
suffix. Raster registration requires the existing complete OCR acceptance gate
and exact application, OCR and configuration identities. Its default constructor
remains unapproved, and marker-model approval alone cannot approve it.

## Verification

The [verification record](GOAL-22-PROPOSAL-PRODUCTION-REGISTRATION.json) binds
all nine changed source/test files, logs and test results. All **636 application
tests pass**, with no skips. Application tests and the native evaluation tool
build with zero warnings and errors. Checks took **87.119 seconds**, using the
reviewed OpenCV runtime and the shared CPU guard.

Tests cover all supported profiles, original-frame execution, retained
suppression evidence, candidate-only diagnostics, altered bytes, unsupported
settings, missing approval, and rejection of marker approval as raster approval.
The first run had one outdated expectation that approved enclosed geometry must
throw. The retained failure is documented; the repaired test checks geometry
prerequisites under both approval states.

## Remaining acceptance work

The full OCR synthetic-sealed reader still rejects unsupported source schemas.
This repair preserves that boundary. A positive factory test uses isolated
test manifests and does not establish model accuracy or successful production
raster execution. Actual production activation, model-store changes and a
packaged build did not occur.

No new dependency, imported weight, private data or sealed input was used.
Source and test fixtures are owned Apache-2.0 material. Build 433 remains 0.4.33.
Goal22 remains incomplete while accuracy, accepted evidence, activation and both
final 2.0.0 distribution checks remain outstanding.
