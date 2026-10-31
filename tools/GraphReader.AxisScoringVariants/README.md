# Generated axis scoring variants

This local diagnostic generates 33 deterministic line-candidate fixtures and
passes only those candidates to the current `AxisGeometryDetector`. The cases
cover clean and short axes, bounding frames, dotted phase dividers, isolated
scatter-like strokes, disconnected and near-parallel competing axes above and below the true axis, and fragmented axes at
three horizontal offsets. The generated corner coordinates are compared with
ranked candidates only after detection. They are never supplied to the detector.

The tool reads no public, development, sealed, manual, or other corpus data.
It runs no model inference or training. It does not render or decode images,
call the OpenCV line extractor, classify markers, or establish end-to-end
accuracy. A scoring failure is diagnostic evidence, not product acceptance.

From `E:\Graph-auto-reader`, with project-local .NET caches and temporary files:

```powershell
dotnet test tools/GraphReader.AxisScoringVariants/tests/GraphReader.AxisScoringVariants.Tests/GraphReader.AxisScoringVariants.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false
$sourceBindingSha256 = (Get-FileHash tools/GraphReader.AxisScoringVariants/source-bindings-v5.json -Algorithm SHA256).Hash.ToLowerInvariant()
dotnet run --project tools/GraphReader.AxisScoringVariants/GraphReader.AxisScoringVariants.csproj -c Release --no-build -- --output E:\Graph-auto-reader\artifacts\goal22-runs\axis-scoring-synthetic-variants-v5\report.json --expected-source-manifest-sha256 $sourceBindingSha256
```

The report destination must be a new JSON file within `artifacts/goal22-runs`.
It verifies a caller-pinned manifest of the fixed tool/Axis source closure and
holds read-only handles throughout evaluation. It records those source hashes,
the manifest hash and the tool and Axis assembly hashes. These bindings do not
independently prove the binaries were built from the pinned sources. It records
per-case uncertainty and corner
error, and every ranked pair's score, corner error, geometry and support statistics. A result requiring review
or rejecting a fixture is reported as such; it is not silently counted as a
successful recognition.
