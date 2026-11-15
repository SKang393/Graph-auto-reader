<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Foreground work interrupts native device profiling

The remaining interaction from the [0.4.52 native record](NATIVE-PREVIEW-452-CHECK-20261114.md)
was verified in one bounded Computer Use attempt on 2026-11-15. This checks the
retained package, not the newer unpackaged geometry/localization source.

The app was launched directly from the sole retained 0.4.52 build. Its local
`Data` directory was absent before launch, so this used fresh portable QA state,
not the launcher's shared user Data. Existing owned input:
`artifacts/native-451-ui-check/owned-one-point-seed.garproj`.

| Identity | SHA-256 |
| --- | --- |
| Package EXE | `c7979e7d88482fb239e6655d4939aea15755d605a472730c116776cda1da8e27` |
| Package DLL | `c04429796bc17ee3f44cd1b7e548e3c524a3733910af2b2dc33440ef4d8e33e1` |
| Owned project, before and after | `c5076e5e432fa7ecec35a5e050e629a14881b72d91ab259bcc10a4475f8f29dd` |

## Observations

1. Opened the owned project. Settings showed `Automatic (GPU: AMD Radeon(TM)
   Graphics)` before the explicit check; no device preference was edited.
2. Pressed **Check devices** once. Screenshot evidence showed the check button
   disabled, Cancel enabled, and `Checking devices using a built-in sample.
   Your image is not used.`
3. Started **Suggest calibration** on its owned tick panel while that check was
   active. Foreground processing began, the settings popup closed, and the
   anchor draft appeared. No values were applied.
4. Settings then showed `Device check canceled. Your current choice is
   unchanged.` and `Last completed label read: GPU, AMD Radeon(TM) Graphics.`
   The automatic device choice remained the same. The app remained responsive.
5. Selected the project's other owned panel. Its visible marker count remained
   one; saved manual values were Y=10, first session=1, final session=3, with
   Undo and Redo disabled. No Apply, Save, Export or point-edit action occurred.
6. Closed the app normally. A fresh window enumeration returned no Graph Auto
   Reader windows, and the original project hash remained unchanged.

The foreground action was calibration processing rather than reopening a
project, avoiding file-dialog delay before actual foreground work starts.
Native screenshots establish the overlap and explicit canceled status. Tool
round-trip time is not an application latency benchmark.

The file dialog's accessibility focus lagged its screenshot, and one stale
element index was rejected without input. A fresh screenshot and visible
filename caret allowed coordinate focus and correct path entry. No repeated
profiling attempt or helper workaround was needed. Screenshots were inspected
inline; no screenshot files or private images were saved.

This closes only the foreground-profiling interruption check. It does not
establish automatic calibration accuracy, installer/clean-guest acceptance,
new-source native acceptance, or production approval. No new dependency,
model, paid service, package or license was introduced.

The new build-local QA state contains five files / 9,568 bytes, including an
automatic snapshot and settings; it does not replace either owned original.
After this record is pushed, the new build-local QA `Data` directory may be
retired after confirming no app process remains. Preserve the shared portable
Data directory, both owned input projects, model assets and the packaged build.
