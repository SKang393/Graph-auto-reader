<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Fixed 0.4.50 prepared-panel calibration diagnostic

The hosted 0.4.50 build from `99864561c4f4e18e6e39b34e258a0ed7434ed623`
contains the owned-tested black-axis antialias coverage repair. After owned proof,
one explicitly authorized confirmation processed the unchanged 120 real-dev
projects through the existing panelizer, genuine-no-panel full-image fallback,
Rust-first geometry and CPU OCR. Every returned panel remained in the accounting.
No private answer selected a panel, model, parameter or threshold. No manual-truth
residual scoring, calibration Apply, training, sealed reads, image writes or
private case output occurred. This is availability evidence, not independent
accuracy or full-workflow acceptance.

The new explicit `450` CLI modes have fixed pins in
[RealDevPrepared450CalibrationCheck.cs](../tools/GraphReader.CalibrationOcrProbe/RealDevPrepared450CalibrationCheck.cs).
Historical 449 commands retain their original pins and defaults. Candidate
preflight verifies and holds the executable, 21 runtime libraries, loaded managed
assemblies, Rust, model, manifest and notices before private payload selection.

## Verification and result

The isolated probe-only build passed with zero warnings/errors in 9.3961098 s.
The privacy/funnel self-test passed in 0.5835211 s with zero private reads.
The unchanged antialiased owned fixture passed in 9.3048918 s: both stacked
panels and the single panel returned geometry/OCR, six numeric readings each
and three complete measured proposals total. Source pixels, crop pixels and
translation mappings were checked without writing images.

The fixed confirmation ran from `2026-11-14T23:07:23.3148515Z` to
`2026-11-14T23:10:08.1281748Z`: exit 0, empty stderr, 164.8055801 s process
wall and 164346.4721 ms pipeline time.

| Aggregate | Count |
| --- | ---: |
| Projects attempted / hashed; distinct embedded images | 120 / 120; 118 |
| Preparation zero / one / multiple panels | 15 / 98 / 7 |
| Returned panels + genuine full-image fallbacks | 112 + 15 |
| Effective panels attempted / unattempted / unsupported / errors | 127 / 0 / 0 / 0 |
| Panels returning geometry and OCR | 50 |
| Numeric readings | 367 |
| Complete measured / estimated proposals | 0 / 0 |
| Fully processed projects with no complete proposal | 120 |

Geometry failures were 73 ambiguous and four native-unavailable results.
Pairing reasons included insufficient pairs 43, unpaired labels 33, invalid
numeric labels 17, inconsistent spacing five, invalid range three, duplicate
association two, nonmonotonic sequence two, ambiguous association one and
unobserved zero one. Reasons overlap. Fifty is a **panel** count, not a source
count. Preparation median/p95 was 29.4065/362.7001 ms.

Parent preprocessing and each child were independently capped at 0.4992 logical
core with BelowNormal priority; verified silent breakaway prevented nested quota
multiplication. Sampled combined memory reached 483663872 bytes, not a hard
memory ceiling. Source/call and overall deadlines remained 45/900 seconds.

## Identities and commands

- App DLL: `49e375f3110f62c386245210d091010f3ccd722216f26ca440c6958afdf9855a`
- App EXE: `da892f5a59faddfe24a42bf48ecca1548f6b2851921383d91c0f133421bc885e`
- Rust: `efed45d5c1de473599cf94ace56aa1e1b3bfd40228289db23ecce755b66e9794`
- Probe DLL: `376c260e46af36e9f85e6cf26ebbdf0690662c177ce4fa3e967340ba426f6412`
- Assignment: `decdac87c0c6d8ee8350b4e26bee2256c551ce20c518732f62fb6d990ea5850a`
- Inventory: `03b36370813337087eb9970ff9a9b7a0431f67dfb59be1e47062034b2d81b81d`
- Complete locked-source digest: `bbe77dd52fc4a08dd2e2a039de234677bbf0e9267d6b812864c8e87e16e5931f`
- Aggregate: `f9c2c9233b64058473eb90b59512d6051ee3ddcfa6bd2f6cb27845f95be49a8f`
- Owned report: `035b898b05ee629005700adb16a5ce15f4f2995dafff8098ed44c7d76c3d96e5`

From the active worktree, using existing E-local cache/home/TEMP:

```powershell
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false -p:BuildProjectReferences=false '-p:OutDir=E:/Graph-auto-reader/artifacts/goal22-worktrees/resume-20261111-phase2/artifacts/prepared-calibration-450-check/tool/'
$probe = "$PWD/artifacts/prepared-calibration-450-check/tool/GraphReader.CalibrationOcrProbe.exe"
$build = "$PWD/artifacts/hosted-preview-450/build"
& $probe --real-dev-calibration-self-test
& $probe --owned-prepared-calibration-450-check "$build/GraphReader.App.exe" "$build/review-assistance/ocr" "$build/native/graphreader-engine.exe"
& $probe --real-dev-prepared-calibration-450-check 'E:/Graph-auto-reader/data/manual data' "$build/GraphReader.App.exe" "$build/review-assistance/ocr" "$build/native/graphreader-engine.exe" --explicit-opt-in
```

Exact hosted libraries were staged into the isolated tool directory after its
build. Model/manifest identities remain 7839f12b/17d44e8e; license/notices and
dependencies are unchanged. No package activation occurred. Retain
`artifacts/prepared-calibration-450-check` only until this source/outcome is pushed
and active consumers finish; preserve original corpus and current runtime inputs.
**Automatic calibration readiness remains FAIL: zero complete proposals.**

The completed isolated probe and results were subsequently [retired after the outcome push](COMPLETED-DIAGNOSTIC-OUTPUTS-RETIREMENT-20261114.md).
