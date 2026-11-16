<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Explicit axis-choice availability on fixed development images

## Scope and identities

One changed-path diagnostic follows the integrated
[incomplete-search](INCOMPLETE-AXIS-REVIEW-20261116.md) and
[tilted-choice](TILTED-AXIS-CHOICES-20261116.md) changes. Product source
`96f89b0418e605183ce1040ec57c8093cba6cc6f` passed full Windows CI before the run.
The check measures whether explicit review returns choices, not whether any
choice is correct. Ordinary automatic geometry, OCR, fitting and export are not
repeated, and no private answer selects a threshold, model or candidate.

The retained metadata selector fixes 120 development projects and excludes 51
reserved payloads. Only the first twelve projects sorted by anonymized case ID
are opened, through locked image-only extraction. All prepared panels are
reviewed; the existing genuine no-panel result permits one full-image fallback.
No calibration anchors or digitized points are constructed or inspected.
The exclusions remain protected, but prior inventory exposure means they are
not described as a pristine independent evaluation set. Sealed uses: **zero**.

| Identity | SHA-256 |
| --- | --- |
| Assignment | `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a` |
| Development inventory | `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d` |
| Ordered twelve source/image digest | `4736ba4a0e3a9727076f82c3ff83f315f76cd8d00456fc60943b3ddd7ff48fd1` |
| Runtime manifest | `716eef1c5dd666c11b186b425eda6d65afdc4683ea54c27099382d70bfd00058` |
| Source App executable | `530cacafba075bd373895aa41f81835429f76c66de31aa5828b7a8dcc263de07` |
| Source App DLL | `8115f14a7c4747293f7605ad3d5118ae0e9ce9191ba201c728db325d52a20d8d` |
| Source Axis DLL | `450ce9dd93523773cece0947e741813bdb94ec28fed6814706125241c58cb4ee` |
| Packaged 457 Rust worker | `19e24a703907281341b183ffc0c16a6a7ab12bb1c4a2b4e97f41e16a76268347` |
| Reviewed OpenCV native DLL | `c96f91b3ec1843e822642d25aceef0591efcf2b3ca999fac72ae5fcddc7f3b31` |
| Executed probe DLL | `db8546dff7f5d00033ab756fc63cdb036948fa4d358899d3506412362a827f88` |

This is an unpackaged source runtime compiled and locally tested before its
outcome commit, not a new packaged 457 binary. Its separate pin profile binds
all twenty runtime libraries, loaded managed assemblies and the actual native
OpenCV module before selection. Historical profiles remain unchanged.

## Preflight and execution

Initial runtime inspection refused the NuGet default native OpenCV hash
`1fa122bdb8e94175e7719fb8aa8f2ab211268a756f5d0c7a13c710ed79ae30cd`
before any corpus or worker access. Only the disposable source-bin copy was
replaced with the reviewed keeper DLL used in local tests. Source review then
caught a new route bypassing the existing explicit opt-in guard. The route was
moved behind that guard and independently re-reviewed before execution.
Neither preflight defect was a private experiment.

Probe-only restore took **4.425 s** and compilation **9.852 s**, with zero
warnings/errors and `BuildProjectReferences=false` preserving the frozen App.
The owned self-test passed in **0.455 s**: two pin refusals, three split refusals,
missing opt-in refusal, image-only XML extraction, and fake complete,
incomplete, unavailable, timeout and invalid-response accounting. It read no
private payload and invoked no worker or model. This self-test is added to CI.

```powershell
dotnet restore tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj --no-dependencies --disable-parallel -p:NuGetAudit=false
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Debug --no-restore -m:1 -nr:false -p:BuildProjectReferences=false -p:UseSharedCompilation=false -p:NuGetAudit=false
GraphReader.CalibrationOcrProbe.exe --real-dev-axis-choice-availability-self-test
GraphReader.CalibrationOcrProbe.exe --real-dev-axis-choice-availability-check <absolute-corpus-root> <pinned-source-App.exe> <pinned-457-Rust.exe> --explicit-opt-in
```

The single private invocation finished `2026-11-16T07:58:38.2071126Z`, exit **0**,
empty stderr, **16.515 s** wall / **16.269 s** internal. A create-new start
receipt prevents an accidental duplicate invocation. The parent CPU allowance
was **0.4992 logical cores**, with existing child limits, 45 seconds per project,
240 seconds logical deadline and 245 seconds hard watchdog. No limit was raised.

## Results and complete failure accounting

| Stage or outcome | Count |
| --- | ---: |
| Declared / attempted / completed projects | 12 / 12 / 12 |
| Source reads completed / failed | 12 / 0 |
| Preparation completed / failed / unattempted | 12 / 0 / 0 |
| Genuine no-panel full-image fallbacks | 1 |
| Planned / attempted / finished panels | 12 / 12 / 12 |
| Complete searches with choices | 6 |
| Incomplete searches with choices | 6 |
| Unavailable / provider error / timeout / cancellation / unsupported / failed | 0 each |
| Unattempted projects / panels | 0 / 0 |
| Rectangular alternatives / affine alternatives | 465 / 390 |
| Rust review calls / native fallback review calls | 12 / 8 |

Rust returned four successful review results, seven `NO_AXIS` results and one
`AMBIGUOUS_AXIS`; all eight native fallbacks returned valid choices without an
error. These intermediate failures remain counted, even though fallback
completed. The final providers were four Rust and eight OpenCV results.
Source/image digest and final metadata checks both matched their frozen values.

Recorded stage times were **0.537 s** source reading, **2.184 s** preparation
and **13.357 s** review. Source and prepared image bytes each totaled
**2,036,035**, held in memory without image writes. Cumulative managed allocation
was **737,055,984 bytes**; this is not peak memory or retained disk usage.

All 27 protected input/shared-Data hashes and twenty source-runtime library
hashes remained unchanged. There were zero normal Suggest calls, separate tick
measurement calls, OCR calls, fits, truth reads or reserved payload reads.
Rust review's own geometry response can contain measured ticks; no downstream
label association was attempted. No candidate was selected, applied or exported.

Aggregate report SHA-256:
`c4c406263d7510acc984753dad2fb5786fd3c94f9ea049ceb3bffffccf76b136`.
Execution receipt: `d4313782c52b8d8ce1bb0ae6fe94aaf65ff73acdf66e3d8f70a9dc55c5b27058`.
Owned self-test report: `839aaef5b7f71d23792438dbdb36c265583748ab673dab295dbe39d97e83cdb6`.

## Interpretation and closeout

The changed chooser offers review alternatives on all twelve development
panels, with incomplete-search status on six. The 855 alternatives are not
855 correct axes, and no unvisited global winner is inferred. These outcomes
cannot be compared as an accuracy gain against the earlier ordinary automatic
geometry trace: the routes and outputs differ. Complete automatic numeric
calibration and native visual acceptance remain unverified by this diagnostic.
Do not repeat this unchanged private check for packaging or UI work.

No dependencies, model approvals or licenses changed. Original data stays local;
only aggregate results and binary identities are tracked. After source/outcome
integration and CI/review close, retire `artifacts/axis-choice-availability-check`
and disposable probe compiler output. Retain originals, user Data, active
native-QA inputs, the protected affine runtime checkpoint, and the one current
packaged build. No package or production approval is implied by this result.

One 0.4.58 internal preview is prepared to deliver the accumulated incomplete
and tilted-choice product changes, rather than the diagnostic itself. Its
exact-source CI, hosted build, upload verification and guarded activation must
finish before replacing 0.4.57. No private rerun is needed for that delivery.
