<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native review corrections, 2026-11-14

ManualPreview 0.4.49 native review exposed four usability issues. The following
source corrections were made after the 0.4.50 build: neither 0.4.49 nor 0.4.50
contains these corrections. Native verification of an application built from the
updated source remains pending.

- **Automatic device selection:** after background profiling, the status showed
  the selected GPU but the Automatic combo box appeared blank. The device
  choices refresh now ignores transient selection writes while WPF replaces the
  items and then republishes the selected choice. See
  `src/GraphReader.App/ViewModels/MainWindowViewModel.CalibrationDevice.cs` and
  `tests/GraphReader.App.Tests/CalibrationDeviceViewModelTests.cs`.
- **Undo and Redo status:** after undoing calibration, the status still said the
  calibration was saved. History operations now show localized completion
  messages. See `src/GraphReader.App/ViewModels/MainWindowViewModel.cs`,
  `src/GraphReader.App/Localization/LocalizationKeys.cs`,
  `src/GraphReader.App/Localization/Resources.en-US.xaml` and
  `tests/GraphReader.App.Tests/ManualAxisDraftTests.cs`.
- **Marker review count and status:** one available result appeared with an
  enabled bulk button showing zero, and accepting the final result displayed
  the empty-search message. The count is refreshed when busy state changes;
  accepting or removing the last candidate now reports the completed action
  or that no unreviewed matches remain. A search that initially finds no
  matches keeps its original guidance. See
  `src/GraphReader.App/ViewModels/MainWindowViewModel.MarkerExemplar.cs`,
  `MainWindowViewModel.cs`, the localization files above, and
  `tests/GraphReader.App.Tests/MarkerExemplarBatchViewModelTests.cs`.
- **Boundary pixel cells:** a fractional marker center just outside a numeric
  anchor was excluded despite lying on the same boundary pixel. The shared
  predicate and matching region use pixel cells `[n - 0.5, n + 0.5)`, with
  ties assigned to the higher pixel, while retaining the exact fractional
  coordinates and full-image template constraints. See
  `src/GraphReader.App/Services/MarkerExemplarGeometry.cs`,
  `src/GraphReader.App/Integration/MarkerExemplar/MarkerExemplarMatcher.cs` and
  `tests/GraphReader.App.Tests/MarkerExemplarBackendTests.cs`. Admitting a
  candidate does not guarantee that correlation will match it; calibration
  anchors also do not establish the full physical extent of a plot.

Focused validation used normal host access, Release, and existing dependencies:

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter 'FullyQualifiedName~CalibrationDeviceViewModelTests' --verbosity minimal
# 4 passed, 0 failed; test duration 460 ms.

dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter 'FullyQualifiedName~ManualAxisDraftTests|FullyQualifiedName~CalibrationDeviceViewModelTests|FullyQualifiedName~Localization' --verbosity minimal
# 18 passed, 0 failed; test duration 2 s.

dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter 'FullyQualifiedName~MarkerExemplarBatchViewModelTests|FullyQualifiedName~MarkerExemplarViewModelTests|FullyQualifiedName~Localization' --verbosity minimal
# 21 passed, 0 failed; test duration 2 s.

dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter 'FullyQualifiedName~MarkerExemplarBackendTests|FullyQualifiedName~MarkerExemplarViewModelTests|FullyQualifiedName~MarkerExemplarWorkspaceTests' --verbosity minimal
# 22 passed, 0 failed; test duration 919 ms.
```

These overlapping focused suites are reported separately. They cover WPF
bindings, history behavior, status text, and actual OpenCV endpoint matching;
they do not establish native UI acceptance or general marker accuracy. No
dependency, model, similarity threshold, export guard or frozen schema changed.
