<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Export acceptance tests

Run `dotnet test tests/GraphReader.Export.Tests -c Release`.

The file-writing checks exercise two intervention exports with minimal CSV,
extended audit CSV, and audit JSON. Both ordinary names and a 300-character
prefix are tested. The latter forces filename truncation and collision
resolution. The tests verify exact UTF-8 contents, returned paths, and absence
of temporary files.

## Long-name staging regression

On Windows, appending a GUID and temporary suffix to the already bounded export
filename exceeded the filesystem component limit. Preview succeeded while the
actual export returned `EXPORT_WRITE_FAILED`. Staging now uses a short random
filename in the destination directory; final filenames, collision protection,
scientific values, and audit contents are unchanged.

Validation on .NET SDK 10.0.302: the long-name regression failed before the fix
while its ordinary-name control passed. After the fix all 37 export tests passed
(288 ms test execution). No model or private-data evaluation was involved.

The headless `ImagePersistenceExportSmokeTests` integration group also passed
all 5 tests on source `ffa00e08` (912 ms test execution). It covers synthetic
image/batch import, calibration evidence, corrected-row export, save/reopen,
and autosave recovery while preserving original bytes. Run with:

```
dotnet test tests/GraphReader.Integration.Tests -c Release --filter FullyQualifiedName~ImagePersistenceExportSmokeTests
```

This verifies module integration, not real-graph model accuracy or interactive
native UI behavior. Temporary isolated build outputs are disposable after the
result record is published; the active packaged application is unchanged.
