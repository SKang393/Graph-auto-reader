<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- Copyright 2026 Sungwoo Kang -->

# Reviewed PDFium source build

This fail-closed Windows x64 build profile pins official PDFium revision
`2870fa9244b0f0f69fb743fab1e08deefcb07b2b` and depot_tools revision
`d22ef3bf62a8c3c76d9c7427015bdfec7665587a`. It builds a small local runner
against PDFium's public embedder API with V8, XFA, Skia, Fontations, and
PartitionAlloc disabled.
ICU data is linked statically, so no unchecked `icudtl.dat` sidecar is needed.
The source lock also bounds Ninja to four concurrent compile jobs so the build
does not depend on machine-wide processor count or transient memory pressure.
The tracked `native/pdfium-windows-hdc.patch` supplies the missing explicit
`fx_system.h` include required by the pinned standalone Windows build. The
script verifies the pinned header blob, applies the patch only while GN/Ninja
runs, records its checksum, and restores the official source bytes afterward.

The runner reads PDF bytes from standard input, renders one requested page,
and writes a bounded BGRA transfer file. `ReviewedPdfiumPageRendererBackend`
converts that transfer to PNG, validates it through the existing renderer
adapter, and kills the runner when cancellation is requested. No PDF bytes are
written to disk by the adapter.

Run from the repository root:

```powershell
powershell -File packaging/pdfium-source/Initialize-PdfiumSource.ps1
powershell -File packaging/pdfium-source/Initialize-WindowsSdkDebuggingTools.ps1
powershell -File packaging/pdfium-source/Build-ReviewedPdfium.ps1 -Phase All
powershell -File packaging/pdfium-source/Test-PdfiumRunner.ps1 -RunnerPath <runner> -EvidenceRoot <smoke-evidence>
powershell -File packaging/pdfium-source/Compare-PdfiumBuilds.ps1 -FirstEvidenceRoot <first> -SecondEvidenceRoot <second>
powershell -File packaging/pdfium-source/review/New-PdfiumDependencyNotice.ps1
powershell -File packaging/pdfium-source/review/Test-PdfiumDependencyReviewPolicy.ps1
powershell -File packaging/pdfium-source/review/tests/Test-PdfiumDependencyReviewPolicy.Tests.ps1
powershell -File packaging/pdfium-source/Test-ReviewedPdfiumEvidence.ps1
```

Source, dependencies, build products, license collection, and approvals stay
under ignored `artifacts/pdfium-source/` paths. Collection deliberately emits
`REVIEW STATUS: INCOMPLETE` and `reviewed-approval.candidate.json` with all
approval flags false. It does not approve a binary.

Source initialization places depot_tools' home, temporary files, Python and
Git caches under `artifacts/pdfium-source/host-state`. Its project-local
`git.bat` shim invokes the installed Git executable because the pinned
depot_tools requires that name on Windows. Initialization refuses to replace
an unexpected existing shim. Run the script in a separate PowerShell process:
it sets environment variables in the process that invokes it. Pass
`-HostStateOnly` to check this setup without starting a source download.
After source synchronization, initialization also bootstraps the pinned
depot_tools Python. The native build requires that Python inside the pinned
checkout, so GN cannot silently select a Windows Store launcher.

Two isolated `-EvidenceRoot` builds must pass `Compare-PdfiumBuilds.ps1` before
review. The comparison requires byte-identical runner binaries and identical
source lock, GN arguments, native overlay, target dependency graph, and PE
import evidence.

Independent review must reconcile every linked dependency and system import,
write `third-party-notices.reviewed.txt` with `REVIEW STATUS: COMPLETE` as its
first line, and create `reviewed-approval.json` with exact binary, source-lock,
build-manifest, and notice SHA-256 values. Runtime loading and evidence
validation both fail closed until those inputs exist and match exactly.

After that exact review is complete, create the ignored approval without
changing the tracked fail-closed policy:

```powershell
powershell -File packaging/pdfium-source/review/New-ReviewedPdfiumApproval.ps1 `
  -EvidenceRoot artifacts/pdfium-source/evidence `
  -ConfirmExactDependencyReview
```

The command revalidates all 240 linked labels, 15 component dispositions, 16
notice sources, four Windows system imports, retained hashes, and disabled
feature flags before writing local evidence. It does not supply clean-machine
evidence or public release authorization.

The tracked `review/dependency-review-policy.json`, exact reviewed target and
PE-import inventories, and deterministic
`third-party-notices.dependency-mapped.txt` close the mechanical mapping gap
without claiming approval. The policy maps all 240 retained target labels to
15 exact component dispositions, binds 16 notice sources by checksum, records
NASM as a build-only tool, and permits only the four observed Windows system
API imports. Both the policy and notice remain explicitly
`dependency-mapped-not-approved` until independent review promotes a separate
reviewed notice and ignored approval. Clean-machine evidence remains a distinct
mandatory gate.

## Internal development-portable staging

`Stage-InternalPortablePdfium.ps1` provides a separate, fail-closed staging
seam for manual internal portable testing. It accepts an explicit target under
the current repository and verifies the tracked dependency-review policy,
deterministic mapped notice, exact runner, source revision, source lock, build
manifest, dependency inventory, PE imports, and candidate approval before
writing anything.

```powershell
powershell -File packaging/pdfium-source/Stage-InternalPortablePdfium.ps1 `
  -EvidenceRoot artifacts/pdfium-source/evidence `
  -TargetRoot artifacts/dev-portable/pdfium-internal
```

The target contains only the runner, deterministic dependency-mapped notice,
and deterministic internal metadata. The metadata always records
`reviewApproved=false`, `cleanMachineEvidence=false`, and
`releaseApproved=false`. This seam does not create `reviewed-approval.json`,
does not enable the production PDF stage, and is prohibited for redistribution
or release packaging.

## Release common-publish staging

The clean-machine validator needs the reviewed files beside a Production-mode
application before the clean-machine gate can pass. To prepare that isolated
test candidate without changing the gate, first obtain a separately built,
fully populated Production-mode application directory under this project's
`artifacts` directory. Record its executable and application assembly hashes.
Then use:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File packaging/pdfium-source/Stage-CleanMachineCandidate.ps1 `
  -CandidateRoot <existing-project-local-candidate> `
  -EvidenceRoot <reviewed-project-local-evidence> `
  -ExpectedExecutableSha256 <sha256> `
  -ExpectedApplicationDllSha256 <sha256>
```

This copies the reviewed approval and four checksum-bound resources into a new
`pdfium` directory and writes a diagnostic-only candidate receipt. It verifies
the fixed reviewed approval SHA-256 and current reviewed PDFium audit component.
It rejects a `portable.mode` sentinel; the absence of that sentinel does not
prove Production mode. It does not validate the app's source commit or version,
execute the application, create a VM report, modify
release gates, or authorize release or redistribution. Run the canonical
clean-machine validator on the complete exact-hash candidate to verify its
Production mode and review the independent result. A failed staging attempt may
leave partial files for review.

The source and synthetic-only staging checks are exercised with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File packaging/pdfium-source/tests/Test-StageCleanMachineCandidate.Tests.ps1
```

`Install-ReleaseRenderer.ps1` is the separate release-only boundary. It refuses
to stage anything unless `reviewed-approval.json` validates, the exact runner
matches a reviewed `pdfium-native` public-audit component, and the
`pdfium-clean-machine-load` mandatory gate is directly passing with
checksum-bound repository evidence. It copies only the approval and its four
checksum-bound resources into `pdfium/` under the common publish. It never
overwrites an existing target.

`Build-Windows.ps1 -ReviewedPdfiumEvidenceRoot <path>` invokes this boundary
only when the public release audit contains the reviewed component. Production
composition automatically discovers `pdfium/reviewed-approval.json` beside the
application. The current candidate does not satisfy this release boundary, so
the public audit remains blocked and no release payload is emitted.
