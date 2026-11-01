<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Detection evidence rollback

An automatic run can update a panel's export evidence before that panel's
review projection has completed. Previously, only the promoted-marker conflict
path restored the prior snapshot. Cancellation or another detection or
projection failure could leave unfinished evidence in the panel store while the
workspace still displayed the prior review.

The workspace now restores prior evidence for panels without a completed
checkpoint on those failure paths. Evidence for completed checkpoints remains
available. For a panel first registered during the failed attempt, temporary
export evidence is cleared while its immutable original image is retained. The
custom automatic-workflow entry point applies the same rollback when its call
or review projection fails. Cancellation is checked after that call returns.
This change does not approve a model or change recognition and export gates.

The new integration scenarios cover cancellation and a calibration failure on
the second panel after evidence changes, preservation of an earlier completed
checkpoint, and a successful export of the prior second panel. A first-run
failure checks removal of unfinished export evidence and retention of original
bytes. The custom-workflow tests cover cancellation, an exception, and rejected
projection. The two second-panel rollback cases failed before the repair and
passed afterward.

`dotnet test GraphAutoReader.slnx -c Release --no-restore -m:1
-p:UseSharedCompilation=false` passed 2,439 tests, skipped 25 and failed none.
The skipped tests include opt-in model and machine-specific checks; this run
does not constitute packaged or clean-Windows acceptance.
