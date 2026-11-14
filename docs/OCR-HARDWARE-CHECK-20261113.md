<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Bounded OCR hardware comparison

## Scope and decision

One owned synthetic panel was processed by the retained PP-OCRv5 recognizer
on CPU, RX 6600 XT and integrated Radeon, sequentially. Each child retained one
session for one cold and three warm reads. All children used the verified
416/10000 Windows Job CPU rate on 12 logical processors, equivalent to 0.4992
logical cores, and one intra/inter-operation thread. DirectML reads waited for
at least their preceding batch's recognition duration before continuing.

The discrete GPU has the best warm latency in this observation. Both GPUs had
slower cold initialization plus first recognition and pacing than CPU. Keep CPU
as the conservative initial default while adding an explicit saved device
choice. Automatic selection for an unmeasured machine remains separate work.
No model weights, threshold, private-set candidate or production approval changed.

| Execution | Initialization | First read including pacing | Warm median including pacing | Warm maximum, 3 reads |
| --- | ---: | ---: | ---: | ---: |
| CPU | 1,008.312 ms | 1,270.500 ms | 1,189.676 ms | 1,205.611 ms |
| RX 6600 XT, DXGI 0 | 1,445.095 ms | 4,587.824 ms | 234.989 ms | 293.420 ms |
| Integrated Radeon, DXGI 1 | 1,427.788 ms | 3,803.336 ms | 537.455 ms | 602.436 ms |

These columns isolate initialization and recognition/pacing; their sum is not
the complete client or UI latency. All four reads plus preprocessing and other
child work took 6,256.808 ms on CPU, 7,293.816 ms on RX 6600 XT and 7,449.016 ms
on integrated Radeon. The complete sequential parent exited 0 in 23.0614809 s.
No child processes remained. Three warm samples do not establish tail latency
or performance on other graphs, drivers or concurrent workloads.

Both GPU sessions matched CPU on all nine candidate identities, recognized
texts, parsed numbers and original-pixel polygons in all four corresponding
rounds. There were zero missing/extra candidates or compared-field differences.
This is backend consistency on one input, not recognition accuracy.

## Resources and limits

DirectML may execute some kernels on CPU. A successful DirectML session is not
proof of pure GPU execution. Pacing is an idle-time policy, not a GPU utilization
cap. The CPU job is an aggregate interval budget, not a guarantee that an
individual core never briefly reaches 100%.

Private process memory after the four reads was 157,372,416 bytes for CPU,
255,586,304 for RX 6600 XT and 250,302,464 for integrated Radeon. Working sets
were 195,907,584, 204,828,672 and 209,739,776 bytes respectively. DXGI local
memory usage after the reads was 119,050,240 and 119,656,448 bytes for the two
GPU processes. Their reported process budgets were 7,736,619,008 and
33,421,934,592 bytes. DXGI process budget headroom is not total free physical
VRAM, and reservation availability is a separate quantity.

## Reproduction and evidence

```powershell
dotnet build tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false --verbosity minimal
dotnet test tests/GraphReader.Inference.Tests/GraphReader.Inference.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:NuGetAudit=false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~DxgiAdapterSelectionTests|FullyQualifiedName~ProviderAndSessionTests' --verbosity minimal
GraphReader.CalibrationOcrProbe.exe --hardware-check <absolute-owned-request.json>
```

The focused inference tests passed 31/31 in 951 ms. Initial builds found CA1822
on the stateless DXGI memory query and CA1826 on an indexable OCR alternative
collection. Marking the query static and using a checked index resolved these;
the final probe-only rebuild passed with zero warnings/errors in 5.50 s.

The bounded parent accepts at most two nonsoftware DXGI adapters, preserves
their actual enumeration index and LUID, gives each child 45 seconds, and has
a 150-second outer watchdog. Output is limited to 1 MB. Failed providers are
reported, never presented as successful CPU substitution. The sample contained
nine candidates, but the implementation accepts 1–128 and compares the actual
CPU population without a sample-specific count gate.

- Model SHA-256: `7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743`.
- Manifest SHA-256: `17259b3260c0d39b292be70b36e68d53a067992de3afeaf0809d4d981de4327a`.
- Owned source SHA-256: `75edb079ccfa8a3a2806bef467ec2dd10d13905bee762fd48ee9be21361be98d`.
- Original crop: `(0,0,1200,313)`; plot within crop: `(105,96,1095,260)`.
- Request SHA-256: `45410515a8ca77da83e6f2f99df2bffdf58f2b83ba0ce2201cc9fe920aa93668`.
- Result SHA-256: `237e5a45243f944757311214c01ae90ef436df05dd2433e58979750d3c105903`.
- Measured probe DLL SHA-256: `a914ef650ae6d38cf8d6a387649a135e253942de3a0a114e24bb32034838871e`.
- Measured inference DLL SHA-256: `2049a4500964f197004d19679de731e931ebd5d5500add838a37c2686c32aaff`.
- ONNX Runtime DLL SHA-256: `e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40`,
  file version `1.24.20260316.9.2d92497`.
- Result size: 20,543 bytes. Temporary request/result/TRX files remain under
  `artifacts/hardware-check` for the current integration consumer and are
  removed after this source/outcome batch is pushed.

The reader's existing Apache-2.0 license and notices remain unchanged. No new
runtime dependency, model download, dataset, training environment or package
was created for this comparison.

## Saved device selection and actual worker validation

ManualPreview now shares one bounded JSON preference store between the native
Settings selector and the OCR client. CPU is the initial default. Hardware
choices preserve DXGI index and LUID; invalid or stale saved selections return
to CPU with an explanation. Explicit CPU selection remains available if GPU
enumeration fails. The client snapshots a preference under its request gate and
replaces its child when the choice changes.

The worker rechecks the selected adapter and requires at least 256 MiB of
current DXGI process budget headroom before GPU initialization. Missing budget,
changed adapter, failed GPU initialization or failed GPU inference uses CPU
with an explicit reason. One reader is retained at a time. A GPU reader is
disposed before creating its CPU replacement; lifetime session counts and
failed-attempt time remain truthful. DirectML batches retain the half-core CPU
job, one-thread settings and one-to-one idle pacing. Original-image bindings,
numeric review and Production exclusion remain enforced.

The final App/probe build passed with zero warnings/errors in 25.53 seconds.
Integration corrected CS8604, CA1068, CA1859 and CA1305 compiler diagnostics;
test compilation also needed an explicit `System.IO` import and formatting
culture correction. The full App run passed 857 tests and failed one new test
whose expectation incorrectly allowed an unknown fallback reason. The test was
corrected to require rejection. A focused run then exposed WPF pack-resource
initialization that depended on earlier tests; explicit existing-style resource
initialization fixed both new fixtures. The final isolated checks passed 27/27
in 686 ms. The revised device-options test passed 1/1 in 482 ms and now reports
inconclusive if the installed runtime cannot initialize a DirectML device.
All five known fallback display cases and injected GPU-to-CPU lifetime/disposal
checks passed. These checks do not simulate a real graphics-driver crash.

```powershell
dotnet test tests/GraphReader.App.Tests/GraphReader.App.Tests.csproj -c Release --no-restore -m:1 -nr:false -p:BuildProjectReferences=false -p:NuGetAudit=false -p:UseSharedCompilation=false --verbosity minimal
GraphReader.CalibrationOcrProbe.exe --gpu-worker-check <owned-request.json> <App.exe> <reviewed-assets> 0 82535
```

The actual App worker check exited 0 in 12.9807112 s, with nine suggestions on
each successful read. Worker totals were 7,146.199 ms cold, 563.596 ms warm and
2,666.304 ms after restart. Warm recognition was 232.437 ms plus 242.915 ms
pacing; initialization was zero. The faster restart does not imply a persistent
application cache; OS/driver state and concurrent load were not controlled.
Cold and warm used PID 196420 and one session. Cancellation ended that process;
the restart used PID 199340, and disposal ended it. No App/probe process remained
after this headless check. All responses identified DirectML, RX 6600 XT index
0/LUID 82535, no fallback, the verified CPU budget and the pinned model.

- App DLL SHA-256: `3055c6ab1c93b5fea9ed568a572be7045a07d1e41f4d0bca541b281b231e4bdc`.
- Probe DLL SHA-256: `75ac7725d4a231efc254f4d1b683ce6f545317d6ef993771edcb65776bda80ee`.
- Lifecycle result SHA-256: `ca37797d05dad8f4fd39b5eec068c160ed339043382681935297fabf70dd1c62`.
- Lifecycle result size: 8,547 bytes; no stderr.

Native inspection showed the CPU default, both named Radeon devices, readable
theme styling and startup/pacing guidance. The user stopped Computer Use with
Escape before saved-choice and fresh-process UI verification completed. No
preference file had been produced in the isolated QA folder. Further window
interaction stopped. Native persistence acceptance is waiting in REV-011;
headless preference persistence and reload tests passed. This source batch is
not a new package, full automation approval or clean-machine verification.

After its outcome reaches main, retire the completed `artifacts/hardware-check`
results and generated App smoke-test screenshots under
`artifacts/dev-portable/evidence` (three files, 74,499 bytes). The native QA App
(PID 124600) remains open after Computer Use stopped. Preserve its binaries,
isolated `artifacts/hardware-native-qa` folder and referenced
`artifacts/calibration-review-assets` until that consumer ends; do not close
its window or remove active files as a cleanup shortcut. Afterward, preserve
any user-created projects and reuse reviewed assets from the retained 0.4.38
package instead of retaining the redundant staging copy. Keep the owned source,
runtime environments, current package, original model and shared user Data.

## Final provider declaration

Before integration, review found that the reused manifest still declared only
CPU. The review manifest now declares `cpu` and `directml` and links this bounded
diagnostic, with production approval still false. Its SHA-256 is
`17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a`.
Comparison after removing only `providers` and `benchmarks` confirmed every
other parsed manifest field is unchanged. Model bytes and all three consumer
pins were checked. The earlier manifest hashes above describe those historical
runs and are not replaced retroactively.

The open native QA App still needs its old manifest. Final validation therefore
used `artifacts/hardware-final-build` (routine compiler output, not a package)
and `artifacts/hardware-device-assets`. The latter hard-links model bytes from
the retained 0.4.38 package and copies only the small manifest/notices. A first
sandbox hard-link attempt was denied; the authorized exact-path creation then
succeeded. The isolated build passed with zero warnings/errors in 51.84 s.

The final actual-worker lifecycle check exited 0 in 20.3789886 s. Cold/warm
used PID 340400, cancellation ended it, restart used PID 324304, and disposal
ended the restarted process. All three successful reads identified DirectML,
the new manifest, one session, and nine suggestions. Worker totals were
6,159.063 ms cold, 802.378 ms warm and 10,025.091 ms after restart. Warm
recognition was 322.133 ms with 324.467 ms pacing and zero initialization.
These varying cold/restart times under uncontrolled desktop load reinforce the
initial CPU default; they are not a stable cross-provider speed claim.

- Final App DLL SHA-256: `693032306cebb2a55e7cc92a6797599bfe08c8691ac60ef8c2669a8994f9d780`.
- Final probe DLL SHA-256: `1ae682220b5fecb80f5a0cedb2dc11ce69ee064260a03caa4a3e5a0ae25324f3`.
- Final lifecycle JSON: 8,553 bytes, SHA-256
  `06fdbda7a9223f287f7a74dcd20041e120f76b5c294c2a7b6b399b56dec5ed5e`.

The isolated compiler output and current metadata assets are the input for the
next headless integration step. Retire them when that consumer ends; they do
not authorize another packaged version or duplicate runtime environment.
