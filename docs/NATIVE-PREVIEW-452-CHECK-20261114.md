<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Native preview 0.4.52 observation attempt

This record covers one bounded, read-only Computer Use attempt after the lead
activated 0.4.52 and launched it normally. It does not establish a passed native
interaction check.

The lead supplied process ID `198920` and executable:

```text
E:/Graph-auto-reader/artifacts/dev-portable/builds/0.4.52-20261115T021403359Z-68781bcc/GraphReader.App.exe
```

One fresh `sky.list_windows()` was issued through `@oai/sky` in `node_repl`.
Its initial output was truncated at the conversation boundary. A subsequent
local JavaScript call read only the cached `qa452Windows` result, without
calling Sky again. That call completed in 0.1 seconds and returned exactly one
matching window:

- Window ID: `815732612`.
- Title: `Graph Auto Reader`.
- App: `process:` followed by the exact retained 0.4.52 executable path above.

The lead interrupted the overall attempt after its setup/observation window
had been exceeded without a state-start notification. No `get_window_state`
call was issued. No screenshot, accessibility state, activation, project-open,
click, typing, edit, save or close input occurred. The exact total attempt
duration is not established by the available terminal results. The interruption
was lead initiated, not evidence of a human Escape press or an app failure.

No further GUI recovery or input was attempted. App closure was not verified
by this agent. The following checks remain pending:

- Three-click manual-axis draft Cancel/Dismiss, including the first in-bounds
  click remaining in collection mode.
- OCR row selection independently of inclusion, review invalidation and cached
  refit after selection changes.
- Marker-proposal Remove without adding a point or overwriting an owned seed.
- Foreground device-check interruption and native verification of the updated
  device-picker and marker-history behavior.

Existing owned projects and `artifacts/native-451-ui-check` originals were
untouched. Earlier native checks remain recorded separately in
[the 451 record](NATIVE-PREVIEW-451-CHECK-20261115.md). This attempt provides
window-identity evidence only; it is inconclusive for rendering and interaction.

## Subsequent single state read

The lead separately authorized exactly one state read on the already returned
window, without another list, activation or setup. After notifying the lead,
the agent called `sky.get_window_state` with screenshot and accessibility text
enabled, a requested 15-second timeout and one-second outer yield. The call
completed on the first short wait; no timeout or interruption occurred. Its
exact elapsed duration was not reported by the nested tool.

The response identified the same window `815732612` and exact 0.4.52 executable.
Its accessibility tree described the Graph Auto Reader landing page: Open,
Settings, disabled Save/Analyze/Review/Export, and recent owned QA projects.
However, the returned 1266 by 793 screenshot visibly showed a Codex chat rather
than Graph Auto Reader. This screenshot/tree target mismatch prevents safe
interaction. It is tool evidence, not an application rendering failure.

No activation or app input followed. No further state read or retry was issued,
and no screenshot was saved to disk. All pending interaction checks above remain
pending. This successful state-return boundary is distinct from the earlier
setup interruption; the 0.4.52 state call itself did not stall.

## Authorized target-selection recovery

The lead then authorized one documented recovery, with each operation in a
separate call and notification before each operation. Using the previously
returned window ID and app identifier, `get_window` returned the exact 0.4.52
window in 0.2 seconds. `activate_window` on that returned object completed in
0.7 seconds. A fresh screenshot-and-text `get_window_state` completed in
0.4 seconds. Each request used a 15-second requested timeout and one-second
outer yield.

The final screenshot visibly shows the dark Graph Auto Reader landing page,
matching the returned accessibility tree: Open and Settings are available;
Analyze, Review and Export are disabled; the recent owned QA projects are
listed. The window remains `815732612` at the exact retained 0.4.52 path.
Explicit activation resolved the observed target mismatch in this recovery.

No clicks, keys, project opens, edits, saves or close actions followed. The
agent stopped after reporting the matching observation to the lead and awaits
the next scoped interaction assignment. All interaction checks listed above
remain pending. No screenshot was saved to disk.

## Actual packaged interaction checks

After the matching target recovery, the lead assigned the remaining native
checks. All input used Sky through `node_repl`, with a matching screenshot and
tree before each action and a refresh afterward. No private corpus was opened.
Some immediate post-action trees lagged the screenshot; a settled observation
was used before claiming state transitions. The window was maximized for the
manual controls.

| Check | Actual observation |
| --- | --- |
| Automatic device label after profiling | Initial preference was CPU. Temporarily selected Automatic. During the actual built-in check, the picker showed `Automatic (CPU until checked)`. On completion, both picker and status visibly showed `Automatic (GPU: AMD Radeon(TM) Graphics)`. The label did not become blank. |
| Preference restoration | Selected CPU again. Picker visibly showed CPU and status said `Devices checked. Your explicit device choice is unchanged.` This is native behavior evidence, not a new performance comparison. |
| Marker proposal Remove | Opened the owned one-point seed and selected its marker panel. Find similar returned two proposals while the saved point count stayed one. Remove reduced proposals to one, removed that outline, retained point count one and left Undo disabled. |
| Marker Undo/Redo status | Explicitly added the remaining proposal: count two and an added-point status. Undo restored count one and reset proposal/status to the default guidance. Redo restored count two without resurrecting proposals or stale success status. Final Undo restored count one and removed the dirty indicator. |
| Partial manual draft | Mark axes for label reading, then one in-bounds canvas click, retained collection mode and Dismiss. Dismiss removed the draft and retained saved calibration values 10/1/3 and point count one. |
| Complete manual draft | A separate three-click draft showed `Axes marked by you`, blank proposal numeric fields and disabled Apply. No numeric values were inferred from clicks. |
| Row selection versus inclusion | Its explicit CPU label read returned six rows. Clicking the first row's text selected it and enabled the relevant value-copy actions while its separate inclusion checkbox remained visibly checked. |
| Cached refit and exclusion | Refit produced six estimated pairs and values 10/1/3, with estimated-position guidance and disabled Apply until review. Checked review without Apply. Excluding the first Y row immediately cleared computed values, reset the review checkbox and disabled Apply. Refit retained valid X 1/3 and blank Y with an explicit incomplete-Y message. All six raw rows remained visible in the list; no Read command was issued for either refit. |
| Completed draft Dismiss | Dismiss removed the draft without applying it. Saved manual values 10/1/3 and point count one remained. A scripted Escape before this did not dismiss the completed draft, which is expected because CancelCommand is enabled during collection or busy/crop operations, not a completed idle draft. |

Foreground interruption of device profiling remains **inconclusive**. A final
explicit built-in check under the restored CPU override completed before the
foreground operation was reached. No cancellation is claimed, and no additional
profiling retry was performed.

No Apply, Save, Save as, Export or project-close action was performed in this
interaction pass. The app was left open with Settings visible, CPU selected and
the original one-point state restored. GUI ownership was returned to the lead.
No new screenshot or image files were written.

Post-check SHA-256 verification confirms both owned originals are unchanged:

- `artifacts/native-451-ui-check/owned-one-point-seed.garproj`:
  `c5076e5e432fa7ecec35a5e050e629a14881b72d91ab259bcc10a4475f8f29dd`.
- `artifacts/native-451-ui-check/reviewed-owned-project-451.garproj`:
  `d31ce808196fec991047bd79b6c24c83b3245feffdabd338eabcb9e7a6cf8e53`.

The two packaged fixes and the listed native review interactions passed their
scoped checks. This does not establish production automatic-analysis accuracy,
native save/export coverage for this build, or profiling cancellation.

## Active-collection keyboard Cancel

A final distinct check exercised the window-level Escape binding during active
manual-axis collection. Starting from a fresh matching state of the same owned
project, Settings was hidden and Mark axes for label reading was selected.
After one in-bounds click at the first X anchor, the collection prompt and
Dismiss remained present. The accessibility focus field reported the Series
name edit, not the canvas, both immediately and in a separate settled read.
The lead clarified that the window-level KeyBinding does not require canvas
focus; no canvas focus is claimed here.

Exactly one Escape was then issued during collection. It returned
`Operation cancelled.` A settled screenshot and tree confirmed collection and
its Dismiss control were absent, with normal selection guidance restored.
The series still contained one point, saved manual values remained 10/1/3,
and the tab had no dirty indicator. No Apply, Save or profiling was performed.
This active-collection keyboard Cancel check passed independently of the
expected completed-draft Escape no-op above.

The app remains open with Settings hidden, CPU preference restored, and the
original one-point state. GUI ownership was released to the lead after this
observation. Foreground profiling interruption remains the sole unverified
interaction from this assigned set.
