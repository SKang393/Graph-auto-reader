# Axis candidate evidence exporter

This artifact-local tool runs the existing `GraphReader.Axis` geometry detector
on the single pinned public synthetic validation image in `input-binding.json`.
It consumes only the image and its manifest hash; it does not read annotations,
truth labels, private/sealed data, train, or change detector settings.

Run from `E:\Graph-auto-reader` with the project-local .NET CLI and NuGet
caches configured:

```powershell
dotnet run --project tools\GraphReader.AxisCandidateEvidence\GraphReader.AxisCandidateEvidence.csproj -c Release -- --output artifacts/goal22-runs/<fresh-name>/report.json
```

The destination must be new and under `artifacts/goal22-runs`. A successful
report contains the complete ordered `candidate_snapshots` emitted by the
production candidate observer and a separately computed `normalized_features`
matrix. The matrix uses the 17 names and formulas in
`ml/axis/candidate_ranker_foundation_v1/features.py`; its rows are one-based and
retain observer order. The report pins the input, detector source, tool source,
assemblies, native OpenCV module, and options. Failures and cancellations have
empty candidate arrays and a structured `failure` record.

The focused test project uses generated fixture pixels only:

```powershell
dotnet test tools\GraphReader.AxisCandidateEvidence\tests\GraphReader.AxisCandidateEvidence.Tests\GraphReader.AxisCandidateEvidence.Tests.csproj -c Release -m:1 -p:UseSharedCompilation=false
```

The checked-in binding is one public development sample, not an acceptance
split. The recorded C#/Python feature comparison is limited to that image and
does not establish accuracy, detector parity beyond the exported candidates,
training value, or production readiness.

## Production-panel batch mode

The separate batch mode accepts an externally pinned input manifest. Invoke it with:

    dotnet tools/GraphReader.AxisCandidateEvidence/bin/Release/net10.0-windows/win-x64/GraphReader.AxisCandidateEvidence.dll --panel-input-manifest artifacts/goal22-runs/<reviewed-input>.json --panel-input-sha256 <externally-reviewed-sha256> --output artifacts/goal22-runs/<fresh-output>/report.json

The manifest binds the versioned panelization admission binding and receipt,
all source and per-source panelization hashes, the complete panelizer runtime
file map, production source hashes, exporter source/runtime hashes, and the
candidate provider/detector options. The selected-panels list is explicit: an
empty list means all panels, while a nonempty list limits candidate extraction
but still validates and reports full source and panel denominators. Detected
panels and full-image fallbacks remain separate in the report. Candidate
coordinates are panel-local original pixels; exact source/panel transforms and
source crop rectangles are retained alongside them.

The batch path uses the production raster decoder and CreateAxisFrame on
authenticated panel PNG bytes. It verifies decoded dimensions, checksum,
Gray8 stride and identity axis-frame coordinates before extracting OpenCV
line candidates and recording the detector's ordered observer snapshot. It
consumes no annotations and emits no labels.
