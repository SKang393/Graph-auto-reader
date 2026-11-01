<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Phase-divider clustering and warning persistence

The phase detector previously compared each segment to a cluster's minimum X.
The resulting weighted centers could be closer than the service's own minimum
separation, so valid input evidence was rejected. It now groups against the
current weighted center. If a group spans more than the cluster tolerance, the
phase result requests review because nearby lines may represent separate
boundaries. Segment evidence remains attached, including evidence from aligned
panels. Manual and cross-panel conflicts still use the existing validation.

The review screen presents this warning in plain language. Production phase
warnings retained in validated panel provenance are also presented after
reopening. Opening a project clears the prior in-memory run and replaces old
panel evidence only after the reopened receipt and source validate. A rejected
receipt cannot supply a warning or authorize production export.

The same seven previously exposed training panels were rerun through the
unapproved candidate composition with simulated manual confirmation of
digitization-derived calibration. Five panels had completed before this
repair; all seven completed afterward. The two prior phase failures completed,
with no new failures or too-close divider pairs. Accepted marker geometry was
identical in all seven panels, and returned point geometry was identical in
the five panels that completed in both runs. Two panels carried the wide
cluster warning. The workflow returned 297 candidate points; 79 of 130 unique
recorded points matched within five pixels. The recorded points are incomplete
and imperfect, so unmatched candidates are not established false positives.

This is an assisted training-only diagnostic. It does not establish automatic
calibration, correct phase or series assignments, graph-value accuracy,
precision, independent generalization, model approval, or product acceptance.
No optimizer step, development payload, or sealed payload was used.

| Evidence | SHA-256 |
| --- | --- |
| Pre-capture registration | `8a965c13b31939f5a70b1f13b4593f6222bc082f1fab02a8be3089af90c3cd4d` |
| Runtime description before and after | `f6e3de4eabc4ecd95626c4644e992fe033fa241d62a5ad1fa1b3ada03814a93b` |
| Assisted capture | `a7849215ff96357d800c7bb699ed1ff5b94b14cbee42acae2aea59e66e0cbf8e` |
| Aggregate phase comparison | `b6a54a72eada07f71380a4db7dff57a43eeb81b8278ac4247defbc142636bfa2` |
| Aggregate recorded-point comparison | `a05e88c03776560a3294a0ced0ffb79dc4761443f3e0fb5572ce3c3aee2fb67a` |

`dotnet test GraphAutoReader.slnx -c Release --no-restore -m:1
-p:NuGetAudit=false -p:UseSharedCompilation=false` passed 2,450 tests with 25
skipped. The diagnostic Release build succeeded with no warnings or errors.
The phase-score unit test and pyflakes passed. CPU inference was used only for
the diagnostic capture. Private inputs and per-panel observations remain in
ignored project-local artifacts.
