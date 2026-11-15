<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Device picker refresh correction

Native ManualPreview 0.4.51 showed a blank collapsed device picker after an
Automatic profile completed, even though the status reported its GPU. The prior
test checked the selected object but did not check the themed control's rendered
label after queued dispatcher work.

An improved STA reproduction applied the application ComboBox style, performed
layout, and waited for `ApplicationIdle`. Before the correction, the selected
item was present but `SelectionBoxItem` was an empty string. The view model now
preserves choice objects when the adapter identities and order are unchanged,
updating their labels through property notifications instead of replacing the
item collection. Adapter inventory changes still use the existing replacement
path. Provider selection, persistence, and resource policies are unchanged.

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter FullyQualifiedName~CalibrationDeviceViewModelTests --verbosity minimal
# 4 passed, 0 failed; test duration 597 ms.
```

The final test checks the themed collapsed control's rendered GPU label after
the idle queue, along with selection and settings state. An initial test compile
hit analyzer `CA1861` and was corrected. An intermediate assertion incorrectly
assumed the type of `SelectionBoxItem`; it was replaced by the rendered-text
assertion. Updated-source native verification remains pending. No package or
production approval was created by this source change.

## Marker history status and combined validation

The 0.4.51 native review also found that after adding two suggested markers and
undoing the batch, the inline marker status still said two points had been
added. The point count correctly returned from three to one and the main status
reported Undo. After a successful Undo or Redo, the view model now clears the
marker-assistance proposal and status using its existing invalidation path.
The batch regression checks the resulting status, empty proposals, and the
existing scientific-state Undo and Redo assertions.

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --filter 'FullyQualifiedName~CalibrationDeviceViewModelTests|FullyQualifiedName~MarkerExemplarBatchViewModelTests' --verbosity minimal
# 9 passed, 0 failed, 0 skipped; test duration 1 s; command wall 38.245 s.
```

The command used normal host access and E-local .NET, NuGet, and temporary
paths. These source tests do not replace native verification of the updated
application.
