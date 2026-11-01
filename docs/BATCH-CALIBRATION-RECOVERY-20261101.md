<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Batch calibration recovery

Production analysis previously stopped at the first panel whose calibration
needed review, so later independent panels were never attempted. For a
recoverable calibration failure, the workspace now restores that panel's
prior evidence immediately and continues to later panels. Completed panels
are checkpointed as before. At the end, the operation still reports that
calibration is required and identifies the first failed panel only when its
evidence matches the current project, panel, and image. The status also says
when other panels completed or when multiple panels need review. Cancellation,
nonrecoverable failures, and other error categories still stop the batch.

The saved-warning lookup also now compares a tab to its retained panel-image
hash, while separately checking the source-file hash. This preserves phase
review warnings for cropped image panels after save and reopen; another panel
does not inherit them.

The new integration regression first failed with one detector call instead of
two. After the change it covers a failed first panel followed by a completed
panel, two calibration failures, a nonrecoverable calibration failure, and a
different recoverable failure. It checks immediate evidence restoration and
completed-panel retention. Three application cases cover partial and multiple
failure messages and an unidentified panel. A cropped-panel persistence
regression failed before the hash correction and passes afterward.

`dotnet test GraphAutoReader.slnx -c Release --no-restore -m:1
-p:NuGetAudit=false -p:UseSharedCompilation=false` passed 2,457 tests with 25
skipped. These are synthetic and component checks. They do not establish
automatic recognition, approved models, or a product release.
