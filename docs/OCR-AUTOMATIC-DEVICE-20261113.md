<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Automatic device selection for calibration label assistance

## Scope

Continue [the development reset](DEVELOPMENT-RESET-20261111.md) after the
[bounded CPU/GPU execution batch](OCR-HARDWARE-CHECK-20261113.md), source
`7f141c6698cdd2f503130da0182e0508576abe55`. This is device selection for the
review-only OCR reader, not model selection or automatic-calibration approval.
No training, private images, new weights, dependencies or packaged build.

Fresh settings use Automatic and resolve to CPU until a valid measurement is
available. The application schedules one bounded comparison after an idle delay.
Foreground work cancels it. Settings also provides an explicit device check,
cancellation and CPU/GPU overrides. A check never overrides an explicit choice.

The comparison generates one project-owned 320 by 200 digit raster in memory
(192,054 bytes). It measures CPU and at most two enumerated hardware adapters,
sequentially: one cold read and three warm reads each. Each actual worker keeps
the existing half-logical-core CPU Job Object limit, one inference session,
one inference thread and, for GPU, a matching delay after each recognition batch.
This pacing is not a GPU utilization cap.

Each device has a 30-second deadline; the comparison has a 90-second total
deadline. A GPU is eligible only when every output matches stable CPU output
by candidate identity, text, numeric value and polygon, and the requested GPU
actually ran without CPU fallback. A GPU must improve the measured four-read
total by at least 15 percent to displace CPU. This conservative timing policy
is not an accuracy threshold, and the small fixture does not represent every
graph size or workload. First-read latency and warm latency are recorded
separately. Selection may remain CPU even when a GPU wins warm reads alone.

The bounded settings file stores the preference and measured device with an
identity derived from runtime binary, model and manifest hashes plus OS,
processor and DXGI hardware information. Moving identical files does not
invalidate a profile. Changed runtime/model/hardware identity does. No file
paths, images or predictions are persisted in the profile. Driver changes that
change adapter LUID also invalidate the profile; this is not a standalone
driver-version inventory. Per-read adapter and memory-headroom checks still
apply, with visible CPU fallback.

## Validation and retention

The coordinated probe/App build passed with zero warnings/errors in 29.59 s.
The first compile failed in 46.96 s on CA2016 (explicit task cancellation token)
and two CA1859 findings (private array parameters); these were repaired without
relaxing warnings. Test compilation then found the obsolete `DataTestMethod`
attribute; replacing it with `TestMethod` allowed 62/62 focused tests to pass
in 2 s. A final lifecycle review found that cancellation during identity reading
could schedule another automatic attempt in the same launch. Marking the attempt
when it starts, and adding the targeted regression, closed this edge. The final
rebuild and 63/63 focused tests passed in 2 s of test time. The final App DLL is
`29f013ebdab78241eac3887a60ed98921e2b9b9277c79eb3130b1a804047d972`.
Only idle-attempt bookkeeping changed after the real worker measurement below;
that measurement is kept bound to its actual earlier DLL instead of relabeled.

The actual `--automatic-device-check` probe exited 0 in 20.5091994 s. It ran
four reads on each of the three devices. All outputs matched and no fallback
was accepted as GPU execution. No owned worker remained after completion.

| Device | Cold read ms | Warm median ms | Four-read total ms |
| --- | ---: | ---: | ---: |
| CPU | 2693.2131 | 1163.9569 | 6109.0038 |
| RX 6600 XT, index 0 / LUID 82535 | 4529.1887 | 327.1390 | 5846.5050 |
| Integrated Radeon, index 1 / LUID 89512 | 5941.7723 | 511.8624 | 7636.0847 |

Automatic selected CPU. The discrete GPU's four-read total improved by about
4.3 percent, below the predefined 15 percent switching margin; its warm reads
were substantially quicker. These measurements include startup, IPC and GPU
pacing, differ from recognition-only measurements and are not latency guarantees.

The probe verified saved Auto-profile reload, changed-identity CPU fallback,
and an explicit CPU override surviving a profile update. The settings JSON
was 281 bytes; the result was 1,105 bytes with empty stderr. Exact identities:

- Profile: `1cb425a35a18a775768087af45acdf96c6fb05eb89ce9effe052f4a4c151fa2a`.
- Result SHA-256: `6c1edbf1f70909cf5b975668d72c0b5f2360dd9925d765444b6aa473589d6ed8`.
- Tested App DLL: `a0a8bfebd476afeaad5f57d55ec04bfaf9eb7eb23b0c0c6eac760d4d754b1856`.
- Probe DLL: `20745e273a827fe29a74127380d061a2dfc945adfece9e146692d3a6c6c98d71`.
- Manifest: `17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a`.
- Model remains `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`.

Native UI checks remain waiting in REV-011 after the user stopped Computer Use.
Headless persistence checks do not claim native selector save/reopen acceptance.

Commands used from the active worktree, with `DOTNET_CLI_HOME` and
`NUGET_PACKAGES` pointing to the existing checkout tools and `TEMP`/`TMP` under
`artifacts/automatic-device-check/tmp`:

```powershell
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false '-p:OutDir=E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\hardware-final-build\' --verbosity minimal
artifacts/hardware-final-build/GraphReader.CalibrationOcrProbe.exe --automatic-device-check E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\hardware-final-build\GraphReader.App.exe E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\hardware-device-assets E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\automatic-device-check\settings
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false '-p:OutDir=E:\Graph-auto-reader\artifacts\goal22-worktrees\resume-20261111-phase2\artifacts\hardware-final-build\' --filter 'FullyQualifiedName~CalibrationOcrDeviceOptimizerTests|FullyQualifiedName~CalibrationDeviceViewModelTests|FullyQualifiedName~CalibrationOcrDeviceSettingsTests|FullyQualifiedName~CalibrationOcrDeviceBenchmarkTests|FullyQualifiedName~CalibrationOcrDeviceIdentityTests|FullyQualifiedName~CalibrationLabelViewModelTests|FullyQualifiedName~ResourcesAndLocalizationTests|FullyQualifiedName~ShellContractTests' --results-directory artifacts/automatic-device-check --logger 'trx;LogFileName=automatic-device-final.trx' --verbosity minimal
```

The profile probe requires an empty settings directory and never uses live user
settings. Generated glyph code is project-owned Apache-2.0. Existing OCR weights
and runtime notices are unchanged; no license or production approval changed.

Expected temporary output is below 2 MB for logs/profile records. Reuse
`artifacts/hardware-final-build` for routine compilation. The historical
`artifacts/hardware-device-assets` staging is no longer a current input:
current probes load the verified 0.4.46 package's `review-assistance/ocr` assets.
All four staged files (7,867,699 path bytes) match that keeper, including the
reader, recognizer manifest and notices. Retire this staging after the new
source/outcome record is pushed and the scoped process check is empty.
E: had 360,323,801,088 bytes free before this batch. Do not duplicate
models or environments. Retire completed logs after this source/outcome is
pushed; retain active QA App dependencies until that App is closed by the user.
Retire the three regenerable App smoke screenshots under
`artifacts/dev-portable/evidence` after this push too (74,499 bytes). Historical
documentation now marks the old captures as retired. The isolated compiler
output remains the active input for the next headless calibration integration.

Hosted source CI for this batch passed:
[run 43538562465](https://github.com/SKang393/Graph-auto-reader/actions/runs/43538562465),
job 132273429202. The full .NET suite reported 2,560 passes and 30 skips,
including App 879/879, with zero build warnings/errors. Packaging, Rust,
localization and artifact-policy checks passed. Release jobs were skipped;
no additional packaged build was created.

## Varied workloads, 2026-11-14

The next source batch, based on `d7fe8b1046102af7d2bedb636b45d4d464951db7`,
uses A/B/A/B within the same four requests per device. A retains the original
320 x 200 bytes; B is a 960 x 320 owned bitmap with three separate glyphs and
an 18-digit row. Both are generated once in memory, with no external fonts or
user images. Each device has one persistent worker. Each repeated result must
match the corresponding CPU fixture, including failure, normalized numeric,
role and axis fields; confidence equality is not required. The warm median is
the mixed reused B/A/B workload, not either fixture's steady-state latency.

The 15% selection margin, two-GPU limit, 30-second device deadline, 90-second
total deadline, half-core worker budget and GPU pacing are unchanged. Existing
App-DLL identity binding invalidates an older measured profile. The failure
message now suggests cropping one graph with its labels and any visible zero,
retrying, or using manual three-anchor calibration. No values are inferred.

The App compiled, but the first test compilation failed after 57.47 s with
CA2012 at `CalibrationOcrDeviceBenchmarkTests.cs:64`: directly awaiting a
`ValueTask` synchronously is not guaranteed to block. The fixture now converts
it with `AsTask()` before the STA test waits. Test-only compilation passed in
20.33 s and the probe-only build in 7.74 s, both with zero warnings/errors.
The sandboxed VSTest command could not connect to its testhost within 30 s;
zero tests executed. With local testhost access, the same bounded filter passed
**23/23**, zero skips/failures, in 523 ms test time (2.814 s command time):

```powershell
$out = Join-Path (Get-Location).Path 'artifacts/hardware-final-build/'
dotnet vstest "$out/GraphReader.App.Tests.dll" '--TestCaseFilter:FullyQualifiedName~CalibrationOcrDeviceBenchmarkTests|FullyQualifiedName~CalibrationOcrDeviceOptimizerTests' '--ResultsDirectory:artifacts/ocr-padding-check' '--Logger:trx;LogFileName=varied-device-benchmark-recheck.trx'
```

The checks cover actual BMP decoding, component selection and different crop
widths, exact request/image order, per-fixture repeat and semantic mismatch
rejection, confidence tolerance, cancellation, disposal and optimizer behavior.
TRX SHA-256: `583aafa8f34d21e18c23ce3814ef39d528f457ff89805fee9331f9c345e98d74`.

The existing `--automatic-device-check` then exited 0 in **45.0222434 s**,
using the new App and unchanged review assets from the verified 0.4.42 package.
All three devices matched their CPU fixture outputs, with no accepted fallback:

| Device | Cold A ms | Reused B/A/B median ms | Four-read total ms |
| --- | ---: | ---: | ---: |
| CPU | 2909.6551 | 2333.8302 | 8803.8409 |
| RX 6600 XT, index 0 / LUID 82535 | 3515.0004 | 2226.8145 | 8446.7172 |
| Integrated Radeon, index 1 / LUID 89512 | 18397.4270 | 2019.1550 | 26471.9225 |

Auto selected CPU: the RX's four-read gain was about 4.1%, below the unchanged
15% rule. These totals include startup, IPC and pacing; the microbenchmark's
recognition-only improvement is a different measurement. This single host/run
does not establish universal latency or justify changing the selection rule.
Disposable settings verified profile reload, changed-identity CPU fallback and
an explicit CPU override surviving profile updates. Live user settings were
untouched. No owned probe remained after completion; stderr was empty.

- Result: 1,109 bytes, SHA-256 `272688c0b66118f0f24a97de6e60cbb1aab294674d53ac680c535905aa391aeb`.
- App DLL: `8ad13d2a7f530423626d126b36df656ab33f631ee4b57e7bf6e158842cc12512`.
- Probe DLL: `dfc7e7cc2e984fbbb5eed43e58ccd6a84f8ab61c74cc82b677ef53567d6fae29`.
- Profile identity: `03c9290b6ad5629f45bd1e184b3c4890d31249641b30fd5b5d47a0672d8f20e6`.

The 281-byte settings file, report, abort/recheck TRXs and metadata under
`artifacts/ocr-padding-check` were retired after the packaged consumers finished
and record `46b6d039` was pushed. The original corpus and active
native QA dependencies remain untouched. No new dependency, model, training,
private/sealed read, native-interaction pass or production approval is implied.
