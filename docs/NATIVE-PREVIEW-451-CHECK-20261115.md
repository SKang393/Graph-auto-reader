<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native preview 0.4.51 workflow check

The verified retained 0.4.51 package opened through the normal latest-preview
launcher after activation. Settings identified 0.4.51. These are native Windows
interactions on project-owned generated images, not private accuracy evidence.
No screenshot archive or additional image copies were created.

## Device and project restoration

The fresh process restored the explicit RX 6600 XT preference saved in 0.4.49.
Selecting Automatic initially displayed CPU until checked. Cancel stopped an
active check and reported that the choice was unchanged. A subsequent explicit
Check devices completed and reported Automatic using AMD Radeon(TM) Graphics.
The collapsed picker then became blank despite the completed status. This is
a native defect; the previous SelectedItem-only test did not establish rendered
text. The [source correction](DEVICE-PICKER-REFRESH-20261115.md) has a themed STA
regression, but updated-source native verification is still pending. Explicit
CPU selection displayed CPU and persisted the `Cpu` preference. After normal
close and a fresh launcher process, Settings again displayed CPU and 0.4.51.

The saved 449 owned project reopened with two source tabs. The marker tab
displayed its three points, Intervention series, zero review issues and restored
calibration values 1, 3 and 10. Its file remained unchanged with SHA-256
`298ea1e158f8fef58b385bcb48fd3bef82576d4db4a9474571eaac8e13d72444`.

## Calibration correction and Undo

Suggest calibration on the owned tick image returned six measured pairs, with
Y values 10, 5, 0 and X values 1, 2, 3. The last-completed device label reported
AMD Radeon(TM) Graphics. Apply stayed disabled until explicit review.

After review enabled Apply, changing the top tick text to `invalid` cleared the
review and proposed values. Refit retained the visible invalid-number warning
and disabled Apply. Correcting it to `10.0` kept the raw recognized `10`, original
pixel location and confidence visible. Cached refit restored six matched pairs
and values 1, 3, 10, still requiring fresh review. Explicit confirmation and Apply
created the calibration. One Undo restored the uncalibrated state, defaults
1, 20, 100 and the status `Last change undone.` The prior stale saved-status
defect did not recur. No zero-confidence native OCR row was produced; that
specific case is covered only by the focused source tests.

## Marker batch, crop and saved outputs

An isolated one-point QA seed was derived from the unchanged owned project,
retaining the actual middle point, its series reference and calibration. It
contains no copied image and makes no claim to be a new user interaction:
9,145 bytes, SHA-256
`c5076e5e432fa7ecec35a5e050e629a14881b72d91ab259bcc10a4475f8f29dd`.
Find similar returned both remaining markers, including the right boundary,
and displayed `Add remaining matches (2)`. The point count stayed one until
acceptance. One bulk acceptance produced three points and reported two added,
zero skipped. One Undo restored one point; one Redo restored three. The main
Undo/Redo status was correct. The inline marker status remained stale after
Undo; the source correction and combined 9-test validation are recorded in the
linked device/history outcome. Updated-source native verification is pending.

Crop panel accepted two explicit canvas corners and displayed an outline before
Create panel. Create added a third, uncalibrated tab with zero copied points,
retaining the source tab. Save As wrote a separate 451 QA result. Its serialized
crop is `(50,47,546,387)` with reversible translation, original source checksum
and child checksum
`4ba64338b7f3b44e18c8dcb49eba2b51169611d16241a863943bc050d5079c23`.
Only the two original image sources are referenced. Their hashes are unchanged.

Export preview showed three points, one series, one phase, zero blockers and
zero warnings. Folder selection wrote the CSV and two audit files. CSV X values
are 1, 2, 3, Y is 4.995569294178258 and phase is a. Original marker X positions
are 204.08037094281292, 404.0803709428129 and 604.0803709428129, with Y
294.1329211746522. These remain measured/clicked coordinates, not substituted
ideal values. The saved calibration remains valid and user-confirmed, with no
OCR evidence IDs. The project retains its creator `app_version` 0.4.49; the
actual writer identity is independently bound to the retained 451 package.

The fresh process reopened all three saved tabs. Selecting the crop showed its
cropped image, no series and disabled Points. Selecting the marker tab showed
three points, its Intervention series and zero review issues. The session then
closed normally without an unsaved-work prompt.

| Owned output | Bytes | SHA-256 |
| --- | ---: | --- |
| reviewed-owned-project-451.garproj | 18,567 | `d31ce808196fec991047bd79b6c24c83b3245feffdabd338eabcb9e7a6cf8e53` |
| owned-marker-workflow_Intervention.csv | 88 | `a5efe5f7c3c148326c4d0f056590981c7c5268bbd8f6973ef9c764c23b002784` |
| owned-marker-workflow_Intervention.audit.csv | 1,316 | `a5bbe7681e55f4681cd9fd75b6e7a5f8d82ec755f5106bb94463f63d16fa9b5a` |
| owned-marker-workflow_Intervention.audit.json | 3,398 | `865012d7f1ab889032832f252d2a074a5d2adb857f120fd6240f15ccb0bbba4a` |

The 32,514-byte seed/result set is temporary under `artifacts/native-451-ui-check`.
The latest saved project and its two referenced owned PNGs remain the specific
input for native verification of the new device/history fixes; remove disposable
seed/export outputs after this outcome is pushed. Foreground interruption,
native row-selection/inclusion independence, proposal Remove and explicit
three-click draft Cancel/Dismiss remain unchecked in this 451 batch. Full visual
acceptance, clean-machine acceptance and production approval are not established.

## Later draft-control attempt

A subsequent native attempt opened the existing one-point seed and began
manual-axis collection. A canvas click appeared to end collection with invalid
anchor guidance, but recovery then returned GraphReader accessibility text with
a screenshot of another application. This is inconclusive native evidence,
not an established product defect. The lead stopped coordinate input, recovered
the unique GraphReader window, verified its screenshot and closed it normally
without saving. The seed SHA-256 remained
`c5076e5e432fa7ecec35a5e050e629a14881b72d91ab259bcc10a4475f8f29dd`.

An owned STA regression imported, calibrated and saved a one-point project,
reopened it, then sent one in-bounds pixel click through the view model. Draft
collection remained active and Undo disabled. All seven `ManualAxisDraftTests`
passed with zero skips, about two seconds in tests / 105.791 seconds command
wall time, using the existing Release test command filtered to that class.
Only the test changed; no production repair was justified. Native Cancel/Dismiss
acceptance remains pending and is not replaced by this test.

## Interrupted state-capture attempt

After the lead launched the retained 451 build with the normal launcher,
`sky.list_windows()` returned exactly one GraphReader window: ID `103491764`,
title `Graph Auto Reader`, process path
`E:/Graph-auto-reader/artifacts/dev-portable/builds/0.4.51-20261114T235320548Z-e5491791/GraphReader.App.exe`.
The owned seed and reviewed-project hashes matched their recorded values before
the attempt.

The next call requested `get_window`, activation and
`get_window_state(include_screenshot=true, include_text=true)` with a 45-second
tool timeout. It returned no screenshot or accessibility state. The lead
interrupted the stalled call after 766.9 seconds; the tool reported
`aborted by user`. This was a lead-initiated interruption, not evidence that the
human pressed Escape. The last completed substep is unknown.

No project-open, click, typing, edit, save or close input was issued. Without a
fresh matching screenshot/tree, no safe close was attempted. The app may still
be open; the lead asked the user to close it normally before 452 activation.
This attempt is inconclusive tool evidence, not a product failure or a passed
interaction check. No further Computer Use calls were made.

The assigned checks remain pending:

- Three-click manual-axis draft Cancel/Dismiss, including confirmation that the
  first in-bounds click keeps collection active.
- Independent OCR row selection versus inclusion, with review invalidation and
  cached refit after changing included rows.
- Marker-proposal Remove without adding a point or overwriting the seed.

The previously recorded foreground device-check interruption and native
verification of the updated device-picker/marker-history fixes also remain
pending. No completed earlier save, export, crop or restoration check is changed
by this attempt.

## Isolated reset and state-read recovery

The lead authorized one later read-only recovery to isolate the failing call.
`js_reset` completed, followed by a fresh `sky.list_windows()` that returned
promptly and again identified the unique 451 window `103491764` at the exact
retained executable path above.

The next request contained only `sky.get_window_state` on that returned window,
with screenshot and accessibility text requested. It performed no `get_window`,
activation or app input. The requested JavaScript timeout was 15 seconds and
the outer tool yield was one second. No state, screenshot or tree returned.
The lead interrupted at the approximately 60-second observation limit; the
terminal tool result reported `aborted by user after 80.9s`.

This narrows the tool blockage to the isolated state-read boundary after a
successful reset and window listing. It does not establish an application
failure, lock-screen condition or human Escape press. No further Computer Use
calls or input were issued. All pending interaction checks above remain pending,
and the lead's request for the user to close 451 normally remains unresolved.

## Screenshot-only branch

One final authorized observation disabled accessibility enumeration. The initial
local JavaScript reference reported `sky is not defined` before any Sky call;
the documented import restored the binding without a session reset. A fresh
window list again returned the unique retained 451 window `103491764`.

Exactly one direct `get_window_state(include_screenshot=true, include_text=false)`
followed, with no get, activation or input. It requested a 15-second timeout and
one-second outer yield but returned no screenshot or state. The lead interrupted
roughly 60 seconds after notification; the terminal result was
`aborted by user after 93.0s`. This is not evidence of a human Escape press or an
application failure. Disabling accessibility text did not avoid the blockage,
so accessibility enumeration alone is not established as its cause.

No more Computer Use recovery or input is planned. Pending interaction checks
remain unchanged; normal user closure of 451 remains the activation dependency.
