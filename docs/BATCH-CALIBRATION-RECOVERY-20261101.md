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

## Per-panel review indicators, 2026-11-11

Calibration failures now retain a separate immutable evidence chain for every
failed panel. The window marks each uniquely matching panel with a localized,
accessible review indicator and selects the first matching failure. A missing,
mixed, or image-mismatched first chain no longer hides valid later failures.
Run, project, panel, and image identities must agree; evidence from another
run or project cannot mark a tab.

A completed detection clears only the indicators and edit history belonging
to newly completed panels. Failed, cancelled, and unchanged panels retain
their state, including when a batch returns successfully with only a subset
of panels. The regression cases use the existing STA dispatcher host for WPF
view models.

From baseline `cd1cd4d8`, validation used the existing project-local SDK/cache
environment (`DOTNET_CLI_HOME=E:\Graph-auto-reader\tools\dotnet-home` and
`NUGET_PACKAGES=E:\Graph-auto-reader\tools\nuget-packages`):

- `dotnet test tests/GraphReader.App.Tests -c Release --no-restore -m:1
  -nr:false --logger "console;verbosity=minimal"`: 796 passed, zero skipped or
  failed, 26 seconds.
- `dotnet test tests/GraphReader.Integration.Tests -c Release --no-restore
  -m:1 -nr:false --logger "console;verbosity=minimal"`: 180 passed, seven
  skipped, zero failed, 19 seconds.
- `git diff --check`: passed.

The first executed App run passed 795 tests and failed one shell-localization
check because the warning symbol was a literal. Moving that symbol into the
localization resources fixed the failure. Initial setup also encountered an
unsupported `dotnet test --source` option, a restore-process failure resolved
by serialized restore, and a sandbox testhost connection timeout before any
tests executed. The successful runs used normal host process access. None of
these void attempts is model evidence or a sealed-set run.

No dependency, schema, model, scientific calibration rule, or export gate
changed. No package was produced. These are component and integration tests,
not visual inspection or final distribution acceptance. Disposable test
compiler outputs can be regenerated with the commands
above and are retired when their active validation consumers finish.

## Saved warnings and recovery, 2026-11-11

The follow-up regression reproduced a missing indicator after saving and
reopening a cropped panel (one failing test, 902 ms). Warnings now persist in
the existing panel validation extension and are bound to project, panel,
source, source-file hash, panel-image hash, dimensions, page, and crop. A
reopened tab restores the warning only when that complete identity matches.
The source-file hash remains distinct from a cropped raster or PDF panel's
image hash. Unrelated validation keys remain intact.

Production services record calibration failures even without a window view
model. A completed projection clears the warning for that panel. Explicit
save, timer/event autosave, recovery, and save after successful reanalysis all
use the same persistence path. Previously validated production receipts and
CSV values remain available: the warning reports the failed later analysis
without falsely invalidating the earlier reviewed result.

The same App and Integration commands above passed 805 tests in 28 seconds and
180 tests in 12 seconds, respectively; the seven existing opt-in Integration
cases remained skipped. These runs additionally set `TEMP` and `TMP` to
`E:\Graph-auto-reader\tools\temp`. An initial test-only CA1826 failure from
calling `First()` on an indexed collection was corrected before the successful
runs. Static review found no remaining issue in warning binding, selective
clearing, saved validation preservation, or prior-receipt export. No frozen
schema, dependency, model, export gate, or package changed.

The preceding `edce79b6` checkpoint also passed
[Windows CI](https://github.com/SKang393/Graph-auto-reader/actions/runs/42974064247):
2,461 solution tests passed, 29 optional tests were skipped, and build,
localization, OCR accounting, and packaging checks succeeded. That CI run
predates this persistence follow-up and is not evidence for the newer source.
