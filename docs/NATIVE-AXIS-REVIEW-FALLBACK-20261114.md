<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Bounded native axis review fallback

The owned six-case backend comparison found one useful difference: padding a
tickless graph caused Rust to return `NO_AXIS`, while the existing OpenCV line
provider and geometry reasoner recovered its axes with a partial-axis warning.
The same native reasoner selected a best pair on a two-panel image, so it could
not be adopted without an explicit uniqueness check. No private answers,
candidate comparison on private data, training or new dependency were used.

Manual Preview now tries Rust first. Only `NO_AXIS` and `NO_INTERSECTION`
without geometry permit the native fallback. Ambiguity, malformed input,
resource failure, timeout and cancellation do not permit another backend.
The fallback accepts exactly one ranked pair, finite original-pixel bounds
and rectangle alignment within one pixel. Its optional 128 examined-pair
budget fails before any partial best result can be accepted. Existing geometry
callers keep their prior behavior unless they request that budget.

The isolated App worker verifies the existing half-logical-core Windows Job
cap and BelowNormal priority before processing, and uses one OpenCV thread.
Requests are limited to 32 MiB encoded images and 4,096-pixel dimensions;
responses to 4,096 bytes. Worker/client deadlines are 12/15 seconds. Cancel
kills the owned process tree and waits up to one second for exit. OpenCV line
extraction and family construction precede the pair budget. No hard memory
limit or general noisy-image throughput is claimed.

Native proposals contain no measured tick positions or values. Provider and
uncertainty metadata stay source-bound; partial axes show a persistent,
localized caution. Numeric labels remain review hints, estimated positions
remain visibly estimated, and Apply requires explicit review. Dismiss, source
invalidation, manual correction and Undo retain their existing behavior. The
worker refuses Production mode before reading the request.

## Verification

The full App suite passed **1,034 tests** in 45 seconds; the Axis suite passed
**131 tests**, with two existing actual-worker tests skipped, in two seconds.
The first sandboxed App attempt timed out connecting to its test host before
executing any test. The same compiled suite passed outside the sandbox without
altered tests or timeout limits. Independent review then found the missing
post-kill exit wait. After that isolated fix, all **16** relevant client/routing
tests passed. The final probe-only build passed with zero warnings/errors in
6.94 seconds. No full-suite result is attributed to a later untested package.

The actual worker diagnostic took **17.747 s**, including the intentional
idle-input watchdog test. Parent and child independently used a verified
0.4992-logical-core cap on this 12-logical-processor host, not a joint cap.

| Owned input | Final path/result | Elapsed |
| --- | --- | ---: |
| Clean axes | Rust proposal | 180.497 ms |
| Padded tickless axes | Native proposal, `y_axis_partial`, no ticks | 842.308 ms |
| Connected corner | Rust proposal | 52.036 ms |
| Matching empty corner | No proposal | 632.861 ms |
| Two panels | Rust ambiguity retained | 45.409 ms |
| Blank | No proposal | 594.115 ms |

Direct native two-panel processing also refused a proposal. Malformed input
returned a structured failure. The idle worker exited 3 after 12,474.483 ms,
including process startup. Cancellation at observed startup completed with the
child already exited in 177.213 ms; a fresh native request then succeeded.
This does not prove cancellation during OpenCV computation. The 9,252,864-byte
memory sample describes startup only. An in-process Production call returned
2 without reading input or writing output; no Production package was launched.

An additional owned label-bearing control used the existing fixture generator
with `-NoTickMarks -WhiteMarginPixels 640`: 1,920 by 1,760 pixels, PNG 19,085
bytes, SHA-256 `3ab961abbc0b61aea7cbde197fd8cb5957cfd7a75ff9c567ea7da80da597c323`.
The actual Rust-first/native/OCR path produced a complete six-label review draft
in **6,016.037 ms process time**: geometry 1,614.066 ms, OCR 2,963.940 ms,
pipeline 5,570.668 ms. All six positions were estimated, none measured, and
calibration was not applied. Labels were X 1/2/3 and Y 10/5/0. Proposed Y
anchors were one pixel below the fixture's printed axis positions, reinforcing
the need for visible review. This is an owned functional check, not real-data
accuracy or production approval. The last fixed real-dev result remains zero
complete calibrations across 120 cases.

## Reproduction and identities

Use the existing shared compiler output for local validation; it is not another
packaged build. Relevant commands, from the worktree:

```powershell
$build = Join-Path $PWD 'artifacts/hardware-final-build'
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false "-p:OutDir=$build/"
dotnet test tests/GraphReader.Axis.Tests/GraphReader.Axis.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false "-p:OutDir=$build/"
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -p:BuildProjectReferences=false "-p:OutDir=$build/"
```

The test commands used `artifacts/native-axis-check` for TRX output and
`artifacts/hardware-final-build` as the existing output. Restore the reviewed
OpenCV `c96f91b3` binary to its two existing output locations after compilation;
the NuGet copy step otherwise replaces it. Run the probe's documented
`--owned-native-axis-check` and `--owned-native-tick-proposal-check` modes with
absolute App/Rust/asset paths and the hash-bound owned request. The label mode
uses detected geometry, not the fixture request's known plot bounds.

| Local validation artifact | SHA-256 |
| --- | --- |
| App EXE | `f82157b1bd899553afe5d020b58a12053f035f0f431d025b6400dc985270c882` |
| App DLL | `f6b9b6eacddb82908bf301e5f7f385819e6b1345bc8c55892da60a3d3ecb28e5` |
| Probe DLL | `256bf286f530dcd9e5aa68fde344e32c34638008f376df268887adbec283204a` |
| Rust worker | `9cd273aa2465e3b472137f5afa091770e5507797dbbf062ee117f21e650c5879` |
| Final worker report, 2,123 bytes | `e4a9297df26af3e5415572c0bce52dd0702bd6024ee146ce57d9c9b02ce0d0c6` |
| Final OCR report, 5,133 bytes | `14be08ed01827eb40ec1d294e7fd785de2961abdd0483b9dbc9ff7477411bc96` |

Existing OpenCV/OCR licenses, assets and notices are unchanged. Native window
interaction remains blocked by the recorded Computer Use launch failure.
Retire `artifacts/native-axis-check` and `artifacts/axis-backend-check` after
this source/outcome is pushed and package consumers finish. Preserve current
runtime dependencies, original user data and the one retained package.
