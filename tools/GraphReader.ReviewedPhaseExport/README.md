# Export one reviewed phase

This local Windows command reads a saved Graph Auto Reader project whose X scale restarts across phases. It writes one phase's scientific CSV, the normal audit files and a receipt tying the reviewer's two X ticks to the saved project hash and export run. Use the same source image as the saved project.

Build the tool and list the panel and phase IDs from the project.

~~~powershell
dotnet build tools/GraphReader.ReviewedPhaseExport -c Release
$tool = 'tools/GraphReader.ReviewedPhaseExport/bin/Release/net10.0-windows/GraphReader.ReviewedPhaseExport.dll'
dotnet $tool list observations.garproj
~~~

Select the phase by ID. Read two labeled ticks in that phase and enter each original source pixel X followed by its session value. The final argument confirms those readings. For example, if the ticks at pixels 30 and 90 read 1 and 3, run

~~~powershell
dotnet $tool export observations.garproj $panelId $phaseId 30 1 90 3 new-output --confirm-ticks
~~~

Use a new destination for each phase. If the export preview produces warnings, inspect them and rerun with `--acknowledge-warnings` after confirming they apply. Each placed point in the selected phase needs an explicit acceptance and an explicit phase label. The command stops if any review evidence is missing or if the ordinary exporter cannot represent the selected points and series relationships. Once the command succeeds, keep the scope receipt with the CSV and audits.

This prototype works with a confirmed axis-aligned Y calibration and a whole-panel observation-order fallback. The saved Y values and original point and phase IDs supply the export. Each X value is a linear estimate from the two supplied ticks. Missing visits remain gaps. Scope covers placed observations only. The panel can be reopened in the desktop app for further review.

To run the synthetic end-to-end control on Windows after building the command

~~~powershell
$env:GRAPHREADER_PHASE_EXPORT_DLL = (Resolve-Path $tool).Path
dotnet test tests/GraphReader.Integration.Tests -c Release --filter FullyQualifiedName~ReviewedPhaseExportCliTests
~~~
