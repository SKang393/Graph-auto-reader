<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native preview 0.4.49 workflow check

The maintainer confirmed that the corrected latest-preview launcher opened the
app. Computer Use then observed the retained 0.4.49 window on this Windows host.
The executable identity remains recorded in the [build ledger](BUILD_LEDGER.json).
Only generated, project-owned graph images were imported. No private corpus was
opened through the UI. This is native interaction evidence, not an accuracy gate
or clean-machine acceptance.

## Observed workflow

- Settings listed both Radeon adapters and CPU. Explicitly selecting the RX 6600
  XT displayed the selected adapter and wrote the existing device-settings file.
  The initial Automatic display was blank despite an Automatic status message.
- Suggest calibration on the owned 640 by 480 tick image returned six measured
  tick pairs. The UI reported the RX 6600 XT as the last completed OCR device.
  The draft showed Y maximum 10, first session 1 and final session 3. Apply was
  disabled until the anchor/label review checkbox was checked.
- Apply created a calibration and enabled Undo. Undo removed the calibration,
  restored the uncalibrated review issue, and restored the manual entry defaults.
  The status bar incorrectly retained the earlier calibration-saved message.
- The separate owned marker image returned six measured tick pairs. Its reviewed
  calibration was applied, then a series was created. A click just outside the
  first-session boundary was refused as a marker-search example with a visible
  reason. That point was undone; a middle-marker example was placed instead.
- Find similar returned one unapplied match. The series remained at one point.
  Individual Add changed the count to two, Undo to one, and Redo to two. The
  bulk button incorrectly showed zero remaining matches before acceptance; the
  final acceptance incorrectly showed empty-search advice afterward.
- The third visible marker was added manually. Save As wrote the isolated owned
  project. Export preview showed three points, one series, one phase, zero
  blockers and zero warnings. Explicit folder selection wrote the three output
  files. The app reported export completion and closed without an unsaved-work
  prompt; a subsequent window listing contained no GraphReader window.

## Saved evidence

The saved project contains two source tabs. The first has no calibration and no
points after Undo. The second has three points and a valid, user-confirmed manual
three-anchor calibration: (204,444) to (1,0), (204,144) to (1,10), and (604,444)
to (3,0). All anchor OCR evidence IDs remain null. Export uses observation order,
retains original pixel coordinates, and labels the reviewed points as manual.
The CSV contains x values 1, 2, 3, each with y 4.995569294178258 and phase a.
This is the actual clicked result, not a substituted value of 5.

| Owned output | Bytes | SHA-256 |
| --- | ---: | --- |
| reviewed-owned-project.garproj | 24,159 | `298ea1e158f8fef58b385bcb48fd3bef82576d4db4a9474571eaac8e13d72444` |
| owned-marker-workflow_Intervention.csv | 88 | `a5efe5f7c3c148326c4d0f056590981c7c5268bbd8f6973ef9c764c23b002784` |
| owned-marker-workflow_Intervention.audit.csv | 1,316 | `bbc1d46938929f7098099a89f3d6ba8288d4aa1532c3ed41191616343a5e34b0` |
| owned-marker-workflow_Intervention.audit.json | 3,398 | `0711e19636b4fffdf014d4089c070794d3884d14bda1b567621df5ec985de97c` |

These small outputs are temporarily under `artifacts/native-449-ui-check`.
The project still references its two owned input images, so those inputs remain
active until the fresh-process reopening check finishes. Inline Computer Use
observations were inspected; no duplicate screenshot archive was created.

## Corrections and remaining checks

The source correction batch preserves the Automatic ComboBox selection across
option refresh, replaces stale Undo/Redo status, refreshes the bulk marker count
after busy state ends, and distinguishes reviewed matches from a genuinely empty
search. Focused WPF binding/history/localization tests passed in the assigned
source checks. These fixes are not in either packaged 0.4.49 or 0.4.50.

Fresh-process preference restoration and project reopening remain pending.
CPU switching, Check devices/Cancel/foreground interruption, crop creation,
corrected-label UI, bulk Add as one Undo operation, and Remove still need native
checks in the appropriate integrated package. No UI latency benchmark was taken.
The matcher returned only one of the other two visible markers in this bounded
check; manual correction was verified, not automatic marker completeness.
