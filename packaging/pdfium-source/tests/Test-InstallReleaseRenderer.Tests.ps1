# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

param([switch]$FixtureOnly)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$profileRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseInstallerPath = Join-Path $profileRoot 'Install-ReleaseRenderer.ps1'
$validatorPath = Join-Path $profileRoot 'Test-ReviewedPdfiumEvidence.ps1'
$rendererSmokeScriptPath = Join-Path $profileRoot 'Test-PdfiumRunner.ps1'
$cleanMachineHarnessPath = [IO.Path]::GetFullPath((Join-Path $profileRoot '..\clean-machine\Invoke-GraphReaderPdfiumCleanMachineValidation.ps1'))
$expectedCommit = ('d' * 40)
$expectedVersion = '0.0.21'
$passed = 0
$failed = 0

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Content
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)] [string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function New-Fixture {
    $root = Join-Path ([IO.Path]::GetTempPath()) ('GraphReader-PdfiumReleaseInstall-' + [Guid]::NewGuid().ToString('N'))
    $repository = Join-Path $root 'repository'
    $profile = Join-Path $repository 'packaging\pdfium-source'
    $evidence = Join-Path $root 'evidence'
    $destination = Join-Path $root 'destination'
    $cleanMachineProfile = Join-Path $repository 'packaging\clean-machine'
    New-Item -ItemType Directory -Path $profile, $cleanMachineProfile, (Join-Path $repository 'packaging\common'), (Join-Path $repository 'packaging\evidence'), (Join-Path $evidence 'bin'), $destination -Force | Out-Null
    Copy-Item -LiteralPath $validatorPath -Destination (Join-Path $profile 'Test-ReviewedPdfiumEvidence.ps1')
    Copy-Item -LiteralPath $rendererSmokeScriptPath -Destination (Join-Path $profile 'Test-PdfiumRunner.ps1')
    Copy-Item -LiteralPath $cleanMachineHarnessPath -Destination (Join-Path $cleanMachineProfile 'Invoke-GraphReaderPdfiumCleanMachineValidation.ps1')

    $executablePath = Join-Path $destination 'GraphReader.App.exe'
    $applicationDllPath = Join-Path $destination 'GraphReader.App.dll'
    [IO.File]::WriteAllBytes($executablePath, [Text.Encoding]::UTF8.GetBytes('exact production common publish executable'))
    [IO.File]::WriteAllBytes($applicationDllPath, [Text.Encoding]::UTF8.GetBytes('exact production application assembly'))
    $executableHash = Get-Sha256 $executablePath
    $applicationDllHash = Get-Sha256 $applicationDllPath

    $binaryPath = Join-Path $evidence 'bin\graphreader_pdfium_renderer.exe'
    $sourceLockPath = Join-Path $evidence 'source-lock.json'
    $buildManifestPath = Join-Path $evidence 'build-manifest.json'
    $noticePath = Join-Path $evidence 'third-party-notices.reviewed.txt'
    [IO.File]::WriteAllBytes($binaryPath, [Text.Encoding]::UTF8.GetBytes('exact reviewed PDFium runner'))
    Write-Utf8NoBom -Path $sourceLockPath -Content '{"schemaVersion":1,"profileId":"graphreader-pdfium-minimal-win-x64"}'
    Write-Utf8NoBom -Path $buildManifestPath -Content '{"schemaVersion":1,"profileId":"graphreader-pdfium-minimal-win-x64"}'
    Write-Utf8NoBom -Path $noticePath -Content "REVIEW STATUS: COMPLETE`nControlled PDFium fixture notice.`n"

    $binaryHash = Get-Sha256 $binaryPath
    $approval = [ordered]@{
        schemaVersion = 1
        rendererId = 'graphreader-pdfium-renderer'
        rendererVersion = '2870fa9244b0f0f69fb743fab1e08deefcb07b2b'
        binaryPath = 'bin/graphreader_pdfium_renderer.exe'
        binarySha256 = $binaryHash
        source = 'https://pdfium.googlesource.com/pdfium'
        sourceRevision = '2870fa9244b0f0f69fb743fab1e08deefcb07b2b'
        sourceLockPath = 'source-lock.json'
        sourceLockSha256 = Get-Sha256 $sourceLockPath
        buildManifestPath = 'build-manifest.json'
        buildManifestSha256 = Get-Sha256 $buildManifestPath
        licenseSpdx = 'BSD-3-Clause'
        noticePath = 'third-party-notices.reviewed.txt'
        noticeSha256 = Get-Sha256 $noticePath
        reviewApproved = $true
        redistributionApproved = $true
        bundlingApproved = $true
    }
    Write-Utf8NoBom -Path (Join-Path $evidence 'reviewed-approval.json') -Content ($approval | ConvertTo-Json -Depth 10)

    $cleanMachineEvidencePath = Join-Path $repository 'packaging\evidence\pdfium-clean-machine.json'
    $vmId = '8edb9b52-d3a1-4cda-96b0-54cc5696630a'
    $isoHash = ('b' * 64)
    $harnessHash = Get-Sha256 (Join-Path $cleanMachineProfile 'Invoke-GraphReaderPdfiumCleanMachineValidation.ps1')
    $smokeScriptHash = Get-Sha256 (Join-Path $profile 'Test-PdfiumRunner.ps1')
    $approvalHash = Get-Sha256 (Join-Path $evidence 'reviewed-approval.json')
    $cleanMachineEvidence = [ordered]@{
        schema = 'graphreader.pdfium-clean-machine-load.v1'
        status = 'pass'
        harnessSha256 = $harnessHash
        failures = @()
        vmProvenance = [ordered]@{
            schema = 'graphreader.clean-windows-vm-provenance.v1'
            isoSha256 = $isoHash
            officialIsoSha256 = $isoHash
            isoSha256Verified = $true
            environmentKind = 'fresh-windows-evaluation-vm'
            networkMode = 'none'
            freshInstall = $true
            vmId = $vmId
            vmConfigurationSha256 = ('c' * 64)
            qemuInstallerSha256 = ('e' * 64)
            qemuVersion = 'QEMU test fixture'
            expectedOsBuild = '26200'
            expectedOsUbr = 6584
            expectedCommit = $expectedCommit
            expectedVersion = $expectedVersion
            expectedExecutableSha256 = $executableHash
            expectedApplicationDllSha256 = $applicationDllHash
            expectedPdfiumRendererSha256 = $binaryHash
            expectedPdfiumHarnessSha256 = $harnessHash
            expectedRendererSmokeScriptSha256 = $smokeScriptHash
        }
        machine = [ordered]@{
            productName = 'Microsoft Windows 11 Enterprise Evaluation'
            buildNumber = '26200'
            architecture = '64-bit'
            installAgeHours = 1.0
            is64BitOperatingSystem = $true
            is64BitProcess = $true
            manufacturer = 'QEMU'
            machineUuid = $vmId
            updateBuildRevision = 6584
            developerToolsOnPath = @()
            networkQuerySucceeded = $true
            networkAdaptersUp = 0
        }
        payload = [ordered]@{
            commit = $expectedCommit
            version = $expectedVersion
            executableSha256 = $executableHash
            applicationDllSha256 = $applicationDllHash
            approvalSha256 = $approvalHash
            rendererSha256 = $binaryHash
            resources = @(
                [ordered]@{ label = 'renderer'; relativePath = 'bin/graphreader_pdfium_renderer.exe'; sha256 = $binaryHash },
                [ordered]@{ label = 'source lock'; relativePath = 'source-lock.json'; sha256 = [string]$approval.sourceLockSha256 },
                [ordered]@{ label = 'build manifest'; relativePath = 'build-manifest.json'; sha256 = [string]$approval.buildManifestSha256 },
                [ordered]@{ label = 'notice'; relativePath = 'third-party-notices.reviewed.txt'; sha256 = [string]$approval.noticeSha256 }
            )
            pdfiumFileCount = 5
            reparsePointCount = 0
        }
        rendererSmoke = [ordered]@{
            scriptSha256 = $smokeScriptHash
            timedOut = $false
            exitCode = 0
            contractPassed = $true
            result = [ordered]@{
                schemaVersion = 1
                runnerSha256 = $binaryHash
                inputKind = 'controlled-synthetic-fixture'
                inputSha256 = '2ebb9f3a7cdec5c76773fc7796f3056a80cc0bdaa1935b1b27e10bb9d581cf8b'
                inputUnchanged = $true
                rawSha256 = 'b549878f8641965a3c78a659177f4b4e027fa250d161d33b9a3df551e8f72158'
                width = 72
                height = 72
                stride = 288
                payloadLength = 20736
                stdout = 'OK 72 72 288'
                stderr = ''
            }
        }
        applicationPackagedFallback = [ordered]@{
            arguments = @('--production-runtime-smoke', '--require-packaged-pdfium')
            environmentApprovalUnset = $true
            timedOut = $false
            exitCode = 0
            passed = $true
        }
    }
    Write-Utf8NoBom -Path $cleanMachineEvidencePath -Content ($cleanMachineEvidence | ConvertTo-Json -Depth 14)
    $releaseAudit = [ordered]@{
        schemaVersion = 1
        mandatoryEvidenceGates = @([ordered]@{
                id = 'pdfium-clean-machine-load'
                description = 'Fixture clean-machine evidence.'
                status = 'pass'
                evidence = @([ordered]@{
                        path = 'packaging/evidence/pdfium-clean-machine.json'
                        sha256 = Get-Sha256 $cleanMachineEvidencePath
                    })
                notes = 'Fixture only.'
            })
        components = @([ordered]@{
                id = 'pdfium-native'
                checksumPolicy = 'exact-binary'
                artifactSha256 = $binaryHash
            })
    }
    Write-Utf8NoBom -Path (Join-Path $repository 'packaging\common\release-audit.json') -Content ($releaseAudit | ConvertTo-Json -Depth 10)

    return [pscustomobject]@{
        Root = $root
        Repository = $repository
        Evidence = $evidence
        Destination = $destination
        BinaryHash = $binaryHash
        ExecutablePath = $executablePath
        ExecutableHash = $executableHash
        ApplicationDllPath = $applicationDllPath
        ApplicationDllHash = $applicationDllHash
        CleanMachineHarnessPath = Join-Path $cleanMachineProfile 'Invoke-GraphReaderPdfiumCleanMachineValidation.ps1'
        RendererSmokeScriptPath = Join-Path $profile 'Test-PdfiumRunner.ps1'
        CleanMachineEvidencePath = $cleanMachineEvidencePath
    }
}

if ($FixtureOnly) { New-Fixture; return }

function Invoke-Case {
    param(
        [Parameter(Mandatory = $true)] [string]$Name,
        [Parameter(Mandatory = $true)] [scriptblock]$Action
    )
    try {
        & $Action
        $script:passed++
        Write-Host "PASS $Name"
    }
    catch {
        $script:failed++
        Write-Host "FAIL $Name`: $($_.Exception.Message)"
    }
}

function Set-CleanMachineEvidence {
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Fixture,
        [Parameter(Mandatory = $true)][object]$Evidence
    )

    Write-Utf8NoBom -Path $Fixture.CleanMachineEvidencePath -Content ($Evidence | ConvertTo-Json -Depth 14)
    $auditPath = Join-Path $Fixture.Repository 'packaging\common\release-audit.json'
    $audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
    $audit.mandatoryEvidenceGates[0].evidence[0].sha256 = Get-Sha256 $Fixture.CleanMachineEvidencePath
    Write-Utf8NoBom -Path $auditPath -Content ($audit | ConvertTo-Json -Depth 14)
}

function Get-CleanMachineEvidence {
    param([Parameter(Mandatory = $true)][pscustomobject]$Fixture)
    return Get-Content -LiteralPath $Fixture.CleanMachineEvidencePath -Raw | ConvertFrom-Json
}

function Invoke-Installer {
    param([Parameter(Mandatory = $true)] [pscustomobject]$Fixture)
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $releaseInstallerPath `
            -EvidenceRoot $Fixture.Evidence `
            -DestinationRoot $Fixture.Destination `
            -ExpectedCommit $expectedCommit `
            -ExpectedVersion $expectedVersion `
            -RepositoryRoot $Fixture.Repository 2>&1 | Out-Null
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Remove-Fixture {
    param([Parameter(Mandatory = $true)] [pscustomobject]$Fixture)
    if (Test-Path -LiteralPath $Fixture.Root) {
        $resolved = [IO.Path]::GetFullPath($Fixture.Root)
        if ([IO.Path]::GetFileName($resolved).StartsWith('GraphReader-PdfiumReleaseInstall-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}

Invoke-Case -Name 'Direct reviewed and clean-machine evidence stages the exact portable PDFium payload' -Action {
    $fixture = New-Fixture
    try {
        $exitCode = Invoke-Installer $fixture
        if ($exitCode -ne 0) { throw "Installer exited $exitCode." }
        $target = Join-Path $fixture.Destination 'pdfium'
        $approval = Get-Content -LiteralPath (Join-Path $target 'reviewed-approval.json') -Raw | ConvertFrom-Json
        $runnerPath = Join-Path $target ([string]$approval.binaryPath)
        if ((Get-Sha256 $runnerPath) -ne $fixture.BinaryHash) { throw 'Staged runner hash changed.' }
        $actual = @(Get-ChildItem -LiteralPath $target -Recurse -File)
        if ($actual.Count -ne 5) { throw "Staged payload contains $($actual.Count) files instead of five." }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'Tampered clean-machine evidence blocks staging' -Action {
    $fixture = New-Fixture
    try {
        Write-Utf8NoBom -Path $fixture.CleanMachineEvidencePath -Content '{"status":"tampered"}'
        if ((Invoke-Installer $fixture) -eq 0) { throw 'Tampered evidence unexpectedly passed.' }
        if (Test-Path -LiteralPath (Join-Path $fixture.Destination 'pdfium')) { throw 'Rejected staging wrote a target.' }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'Hash-valid arbitrary clean-machine JSON blocks staging' -Action {
    $fixture = New-Fixture
    try {
        Set-CleanMachineEvidence -Fixture $fixture -Evidence ([ordered]@{ status = 'pass'; machine = 'fixture' })
        if ((Invoke-Installer $fixture) -eq 0) { throw 'Arbitrary evidence unexpectedly passed.' }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'Fail-status clean-machine report blocks staging' -Action {
    $fixture = New-Fixture
    try {
        $evidence = Get-CleanMachineEvidence $fixture
        $evidence.status = 'fail'
        Set-CleanMachineEvidence -Fixture $fixture -Evidence $evidence
        if ((Invoke-Installer $fixture) -eq 0) { throw 'Fail-status evidence unexpectedly passed.' }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'Wrong clean-machine renderer checksum blocks staging' -Action {
    $fixture = New-Fixture
    try {
        $evidence = Get-CleanMachineEvidence $fixture
        $evidence.payload.rendererSha256 = ('0' * 64)
        Set-CleanMachineEvidence -Fixture $fixture -Evidence $evidence
        if ((Invoke-Installer $fixture) -eq 0) { throw 'Wrong renderer evidence unexpectedly passed.' }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'Failed clean-machine render blocks staging' -Action {
    $fixture = New-Fixture
    try {
        $evidence = Get-CleanMachineEvidence $fixture
        $evidence.rendererSmoke.contractPassed = $false
        Set-CleanMachineEvidence -Fixture $fixture -Evidence $evidence
        if ((Invoke-Installer $fixture) -eq 0) { throw 'Failed renderer smoke unexpectedly passed.' }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'Failed application packaged fallback blocks staging' -Action {
    $fixture = New-Fixture
    try {
        $evidence = Get-CleanMachineEvidence $fixture
        $evidence.applicationPackagedFallback.passed = $false
        Set-CleanMachineEvidence -Fixture $fixture -Evidence $evidence
        if ((Invoke-Installer $fixture) -eq 0) { throw 'Failed application fallback unexpectedly passed.' }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'String Boolean in application fallback blocks staging' -Action {
    $fixture = New-Fixture
    try {
        $evidence = Get-CleanMachineEvidence $fixture
        $evidence.applicationPackagedFallback.environmentApprovalUnset = 'true'
        Set-CleanMachineEvidence -Fixture $fixture -Evidence $evidence
        if ((Invoke-Installer $fixture) -eq 0) { throw 'String Boolean unexpectedly passed.' }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'String Boolean in reviewed approval blocks staging' -Action {
    $fixture = New-Fixture
    try {
        $approvalPath = Join-Path $fixture.Evidence 'reviewed-approval.json'
        $approval = Get-Content -LiteralPath $approvalPath -Raw | ConvertFrom-Json
        $approval.reviewApproved = 'true'
        Write-Utf8NoBom -Path $approvalPath -Content ($approval | ConvertTo-Json -Depth 10)

        $evidence = Get-CleanMachineEvidence $fixture
        $evidence.payload.approvalSha256 = Get-Sha256 $approvalPath
        Set-CleanMachineEvidence -Fixture $fixture -Evidence $evidence

        if ((Invoke-Installer $fixture) -eq 0) { throw 'String approval Boolean unexpectedly passed.' }
        if (Test-Path -LiteralPath (Join-Path $fixture.Destination 'pdfium')) {
            throw 'Rejected string approval Boolean wrote a target.'
        }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'Changed common-publish executable blocks staging' -Action {
    $fixture = New-Fixture
    try {
        [IO.File]::AppendAllText($fixture.ExecutablePath, 'changed')
        if ((Invoke-Installer $fixture) -eq 0) { throw 'Changed application executable unexpectedly passed.' }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'Changed common-publish application assembly blocks staging' -Action {
    $fixture = New-Fixture
    try {
        [IO.File]::AppendAllText($fixture.ApplicationDllPath, 'changed')
        if ((Invoke-Installer $fixture) -eq 0) { throw 'Changed application assembly unexpectedly passed.' }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'Multiple clean-machine reports block staging' -Action {
    $fixture = New-Fixture
    try {
        $auditPath = Join-Path $fixture.Repository 'packaging\common\release-audit.json'
        $audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
        $audit.mandatoryEvidenceGates[0].evidence = @(
            $audit.mandatoryEvidenceGates[0].evidence[0],
            $audit.mandatoryEvidenceGates[0].evidence[0])
        Write-Utf8NoBom -Path $auditPath -Content ($audit | ConvertTo-Json -Depth 14)
        if ((Invoke-Installer $fixture) -eq 0) { throw 'Multiple clean-machine reports unexpectedly passed.' }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'Stale PDFium harness checksum blocks staging' -Action {
    $fixture = New-Fixture
    try {
        $evidence = Get-CleanMachineEvidence $fixture
        $evidence.harnessSha256 = ('0' * 64)
        Set-CleanMachineEvidence -Fixture $fixture -Evidence $evidence
        if ((Invoke-Installer $fixture) -eq 0) { throw 'Stale harness evidence unexpectedly passed.' }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'Blocked clean-machine gate blocks staging' -Action {
    $fixture = New-Fixture
    try {
        $auditPath = Join-Path $fixture.Repository 'packaging\common\release-audit.json'
        $audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
        $audit.mandatoryEvidenceGates[0].status = 'blocked'
        Write-Utf8NoBom -Path $auditPath -Content ($audit | ConvertTo-Json -Depth 10)
        if ((Invoke-Installer $fixture) -eq 0) { throw 'Blocked gate unexpectedly passed.' }
        if (Test-Path -LiteralPath (Join-Path $fixture.Destination 'pdfium')) { throw 'Blocked staging wrote a target.' }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'Release-audit binary mismatch blocks staging' -Action {
    $fixture = New-Fixture
    try {
        $auditPath = Join-Path $fixture.Repository 'packaging\common\release-audit.json'
        $audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
        $audit.components[0].artifactSha256 = ('0' * 64)
        Write-Utf8NoBom -Path $auditPath -Content ($audit | ConvertTo-Json -Depth 10)
        if ((Invoke-Installer $fixture) -eq 0) { throw 'Mismatched binary unexpectedly passed.' }
    }
    finally { Remove-Fixture $fixture }
}

Invoke-Case -Name 'Existing PDFium destination is never overwritten' -Action {
    $fixture = New-Fixture
    try {
        $target = Join-Path $fixture.Destination 'pdfium'
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Write-Utf8NoBom -Path (Join-Path $target 'owned.txt') -Content 'preserve'
        if ((Invoke-Installer $fixture) -eq 0) { throw 'Existing target unexpectedly passed.' }
        if ((Get-Content -LiteralPath (Join-Path $target 'owned.txt') -Raw) -ne 'preserve') { throw 'Existing target changed.' }
    }
    finally { Remove-Fixture $fixture }
}

Write-Host "Release PDFium renderer packaging tests: $passed passed, $failed failed."
if ($failed -ne 0) { exit 1 }
