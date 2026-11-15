<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Calibration localization regression

Source baseline: `b33d732fe93ec3d9d369c8e1cb62b9fa8bc25d5d`.

Windows CI [43701560566](https://github.com/SKang393/Graph-auto-reader/actions/runs/43701560566)
failed in `ShellContractTests.ManualCalibrationExposesFirstVisibleSessionAsLocalizedInput`,
line 117. The test expected a static resource while the input now binds its
mode-localized label. That job's app tests reported 1,118 passed, one failed,
zero skipped. The later localization audit did not run. The earlier focused
printed-X checks did not include this shell contract; they did not establish a
green full CI run.

Inspection also found that mode changes notified the new localized getters,
but language changes did not. Two new STA tests reproduce stale accessible
names after a culture event, one for session coordinates and one for printed X.

## Change

- Check the real shell's first/final X Name and HelpText bindings, and the
  validation Text and accessible Name bindings.
- Subscribe to localization changes after view-model initialization; detach
  on disposal. Refresh manual and suggested mode labels, review summary and
  geometry-warning text without changing coordinates or clearing review.
- Exercise the actual WPF controls and bindings in both coordinate modes,
  including validation visibility and text, language-change notifications,
  preserved values/review/Apply eligibility, and event detachment after disposal.
  The fake culture service uses distinguishable strings from the English
  resource dictionary. It does not add or claim a shipped French translation.

## Validation

Normal Windows host, Debug, existing .NET/NuGet cache, serial compiler. VSTest
requires the host's local IPC; no sandbox timeout was treated as product evidence.

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj --configuration Debug --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter 'FullyQualifiedName~ShellContractTests|FullyQualifiedName~CalibrationTickViewModelTests|FullyQualifiedName~ResourcesAndLocalizationTests' --logger 'trx;LogFileName=green.trx' --results-directory artifacts/calibration-localization-check
```

| Attempt | Result | Wall seconds |
| --- | --- | ---: |
| Initial new-test build | Void: CA1861 constant resource array; repaired with a static readonly field | 63.7753 |
| First two-case test | Void: WPF resource assembly was initialized too late; zero valid culture assertions | 15.0524 |
| Corrected regression before fix | 0 passed, 2 failed, 0 skipped; accessible Name remained English after culture change | 12.3715 |
| Fix plus targeted shell/calibration/localization suite | 54 passed, 0 failed, 0 skipped; test duration approximately 3 seconds | 37.8425 |

The two-case command uses filter
`FullyQualifiedName~CalibrationAccessibleTextRefreshesOnLanguageChangeWithoutResettingReview`
and `-p:BuildProjectReferences=false` after the references are built.
Raw TRX SHA-256 identities:

- Resource-setup failure `red.trx`: `34bea28a4db2a297c4eef2cac61c871a4f39f87c4373b5e0af53bd73dd05a3eb`.
- Valid reproduction `red-culture.trx`: `96be23a8d443137ba311a26172130d684557526fe2843ea264636a4544a46b9d`.
- Passing suite `green.trx`: `d20219f4d68bf6c0c8a9e144fba61bbd4759de37bcece127e8eaf86f18838270`.

Full Windows CI is required on the pushed fix before declaring the integrated
head green. No package or version change is part of this source correction.
These checks establish WPF binding behavior, not shown-window accessibility
acceptance. Prior native review evidence remains in
[the printed-X outcome](PRINTED-X-REVIEW-20261115.md).

No new dependency, model, translation, private input or sealed evaluation was
used. Project code remains Apache-2.0. The in-progress Axis/OCR changes are
excluded from this source commit; clean full CI validates this fix independently.

## Retention

After the fix and this record are pushed and CI investigation finishes,
`artifacts/calibration-localization-check` is disposable. Regenerate it with
the commands above rather than keeping completed test output.
The previous completed directional/printed-X run outputs and temporary source
preview data were retired at `2026-11-15T09:56:10.7588665Z`: 33 generated files,
602,947 bytes, five temporary model hardlinks and the preview's empty portable
marker. The runtime model keeper hashes were rechecked; the active headless
runtime, original inputs, user data and sole packaged 0.4.52 build were retained.
