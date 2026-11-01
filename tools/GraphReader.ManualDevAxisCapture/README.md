<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Development-only axis capture

This diagnostic selects the existing 16-project study-partitioned development
subset from the frozen 120-project real-development inventory. The metadata
preflight found five studies. The selector inspects the frozen inventory metadata
but never opens any of the 51 real-sealed project payloads. It reads the 104
training projects' existing image-hash manifest to flag exact image overlap;
it does not open training project payloads during this capture.

`--capture` reads only the selected 16 development projects, checks the opened
path and project hash, and writes distinct source images plus an anonymized
case/image manifest to an ignored project-local directory. The parser necessarily
loads each project in memory, including its calibration, but the capture does
not export calibration anchors into the inference manifest. `--truth` rereads
the same projects only after supplied manifest and prediction hashes match, then
writes the anchors separately under that ignored directory. No case names,
source paths, images, or transcriptions belong in tracked results.

The recorded three-point calibration can locate graph X = 0, but that position
is not a reliable reference for a drawn vertical axis. Some graphs start at
session 1, and some have no visible vertical axis. The calibration extrapolation
output is a diagnostic, not an axis accuracy score. For visible-line review,
report the reference method and cases with no visible or ambiguous line
separately; never substitute an extrapolated origin for a missing line.
Account for source studies and exact train-image overlap when interpreting
results. This is development analysis on imperfect manual data, not independent
expert adjudication, a sealed test, or product acceptance.

```powershell
dotnet build tools/GraphReader.ManualDevAxisCapture/GraphReader.ManualDevAxisCapture.csproj -c Release -m:1
dotnet tools/GraphReader.ManualDevAxisCapture/bin/Release/net10.0-windows/GraphReader.ManualDevAxisCapture.dll --metadata
dotnet tools/GraphReader.ManualDevAxisCapture/bin/Release/net10.0-windows/GraphReader.ManualDevAxisCapture.dll --capture
```

The capture directory is create-only. No optimizer or GPU training is run by
this tool. Reproduction requires the frozen authorized local corpus.
