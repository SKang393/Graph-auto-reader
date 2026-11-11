# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

param([switch]$FixtureOnly)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$profileRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installerPath = Join-Path $profileRoot 'Install-ReviewedRuntime.ps1'
$releaseInstallerPath = Join-Path $profileRoot 'Install-ReleaseRuntime.ps1'
$passed = 0
$failed = 0

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [string]$Content
    )

    $parent = Split-Path $Path -Parent
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function New-Fixture {
    param([switch]$TamperSource)

    $root = Join-Path ([IO.Path]::GetTempPath()) ('GraphReader-ReviewedOpenCvInstall-' + [Guid]::NewGuid().ToString('N'))
    $repository = Join-Path $root 'repository'
    $profile = Join-Path $repository 'packaging\opencv-source'
    $evidence = Join-Path $root 'evidence'
    $destination = Join-Path $root 'destination'
    New-Item -ItemType Directory -Path (Join-Path $profile 'review'), (Join-Path $repository 'packaging\common'), (Join-Path $repository 'packaging\evidence'), (Join-Path $repository 'packaging\clean-machine'), (Join-Path $evidence 'bin'), (Join-Path $destination 'runtimes\win-x64\native') -Force | Out-Null
    Copy-Item -LiteralPath $installerPath -Destination (Join-Path $profile 'Install-ReviewedRuntime.ps1')

    $reviewedBytes = [Text.Encoding]::UTF8.GetBytes('reviewed deterministic OpenCV runtime')
    $sourcePath = Join-Path $evidence 'bin\OpenCvSharpExtern.dll'
    [IO.File]::WriteAllBytes($sourcePath, $reviewedBytes)
    $reviewedHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($TamperSource.IsPresent) {
        [IO.File]::WriteAllText($sourcePath, 'tampered runtime', [Text.UTF8Encoding]::new($false))
    }
    [IO.File]::WriteAllText((Join-Path $destination 'runtimes\win-x64\native\OpenCvSharpExtern.dll'), 'package runtime', [Text.UTF8Encoding]::new($false))

    $validator = @'
param([string]$EvidenceRoot)
if (-not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)) { exit 1 }
exit 0
'@
    Write-Utf8NoBom -Path (Join-Path $profile 'Test-SourceAuditEvidence.ps1') -Content $validator
    Write-Utf8NoBom -Path (Join-Path $profile 'review\Test-SourceBuildReviewPolicy.ps1') -Content $validator
    $policy = [ordered]@{
        schemaVersion = 1
        profileId = 'graphreader-axis-minimal-win-x64'
        evidenceRootName = 'fixture-evidence'
        overallReviewStatus = 'reviewed-provenance-only'
        binarySha256 = $reviewedHash
        sourceRevisions = [ordered]@{
            openCvSharp = 'fixture-opencvsharp'
            openCv = 'fixture-opencv'
            vcpkg = 'fixture-vcpkg'
        }
        noticeBundle = [ordered]@{ reviewStatus = 'complete' }
        microsoftStaticRuntimeAttestation = [ordered]@{ status = 'recorded-private' }
    }
    Write-Utf8NoBom -Path (Join-Path $profile 'review\source-build-review-policy.json') -Content ($policy | ConvertTo-Json -Depth 10)

    $expectedCommit = ('c' * 40)
    $expectedVersion = '0.0.21'
    $executablePath = Join-Path $destination 'GraphReader.App.exe'
    Write-Utf8NoBom -Path $executablePath -Content "fixture self-contained application`n"
    $executableSha256 = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $vmId = '11111111-2222-4333-8444-555555555555'
    $cleanMachineHarnessPath = Join-Path $repository 'packaging\clean-machine\Invoke-GraphReaderCleanMachineValidation.ps1'
    Write-Utf8NoBom -Path $cleanMachineHarnessPath -Content "# fixture clean-machine harness`n"
    $cleanMachineHarnessSha256 = (Get-FileHash -LiteralPath $cleanMachineHarnessPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $cleanMachineEvidence = [ordered]@{
        schema = 'graphreader.opencv-clean-machine-load.v1'
        status = 'pass'
        observedAtUtc = '2026-08-10T00:00:00.0000000+00:00'
        harnessSha256 = $cleanMachineHarnessSha256
        vmProvenance = [ordered]@{
            schema = 'graphreader.clean-windows-vm-provenance.v1'
            isoSha256 = ('e' * 64)
            officialIsoSha256 = ('e' * 64)
            isoSha256Verified = $true
            environmentKind = 'fresh-windows-evaluation-vm'
            networkMode = 'none'
            freshInstall = $true
            vmId = $vmId
            vmConfigurationSha256 = ('f' * 64)
            qemuInstallerSha256 = ('1' * 64)
            qemuVersion = 'QEMU emulator version fixture'
            expectedOsBuild = '26200'
            expectedOsUbr = 6584
            expectedCommit = $expectedCommit
            expectedVersion = $expectedVersion
            expectedExecutableSha256 = $executableSha256
            expectedOpenCvSha256 = $reviewedHash
            expectedHarnessSha256 = $cleanMachineHarnessSha256
        }
        machine = [ordered]@{
            productName = 'Microsoft Windows 11 Enterprise Evaluation'
            version = '10.0.26200'
            buildNumber = '26200'
            architecture = '64-bit'
            installTimeUtc = '2026-08-10T00:00:00.0000000Z'
            installAgeHours = 1.25
            powerShellVersion = '5.1.26200.1'
            is64BitOperatingSystem = $true
            is64BitProcess = $true
            manufacturer = 'QEMU'
            model = 'Standard PC (Q35 + ICH9, 2009)'
            biosManufacturer = 'SeaBIOS'
            machineUuid = $vmId
            updateBuildRevision = 6584
            developerToolsOnPath = @()
            networkQuerySucceeded = $true
            networkAdapters = @()
            networkAdaptersUp = 0
        }
        payload = [ordered]@{
            root = 'D:\payload'
            version = $expectedVersion
            commit = $expectedCommit
            dirty = $false
            executablePath = 'D:\payload\GraphReader.App.exe'
            executableSha256 = $executableSha256
            portableModePresent = $true
            openCvPath = 'D:\payload\OpenCvSharpExtern.dll'
            openCvSha256 = $reviewedHash
            modelPayloadFileCount = 0
            reparsePointCount = 0
        }
        nativeLoad = [ordered]@{
            attempted = $true
            succeeded = $true
            win32Error = $null
            flags = 'LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS'
        }
        applicationSmoke = [ordered]@{
            argument = '--portable-smoke'
            startedUtc = '2026-08-10T00:00:01.0000000+00:00'
            finishedUtc = '2026-08-10T00:00:02.0000000+00:00'
            timeoutSeconds = 60
            timedOut = $false
            exitCode = 0
            passed = $true
        }
        failures = @()
    }
    $cleanMachineEvidencePath = Join-Path $repository 'packaging\evidence\opencv-clean-machine.json'
    Write-Utf8NoBom -Path $cleanMachineEvidencePath -Content (($cleanMachineEvidence | ConvertTo-Json -Depth 12) + [Environment]::NewLine)
    $cleanMachineEvidenceHash = (Get-FileHash -LiteralPath $cleanMachineEvidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $releaseAudit = [ordered]@{
        schemaVersion = 1
        mandatoryEvidenceGates = @([ordered]@{
                id = 'opencv-clean-machine-load'
                description = 'Fixture clean-machine evidence.'
                status = 'pass'
                evidence = @([ordered]@{
                        path = 'packaging/evidence/opencv-clean-machine.json'
                        sha256 = $cleanMachineEvidenceHash
                    })
                notes = 'Fixture only.'
            })
        components = @([ordered]@{
                id = 'opencvsharp-native'
                checksumPolicy = 'exact-binary'
                artifactSha256 = $reviewedHash
            })
    }
    Write-Utf8NoBom -Path (Join-Path $repository 'packaging\common\release-audit.json') -Content ($releaseAudit | ConvertTo-Json -Depth 10)

    return [pscustomobject]@{
        Root = $root
        Repository = $repository
        Evidence = $evidence
        Destination = $destination
        ReviewedHash = $reviewedHash
        CleanMachineEvidencePath = $cleanMachineEvidencePath
        ExpectedCommit = $expectedCommit
        ExpectedVersion = $expectedVersion
    }
}

if ($FixtureOnly) { New-Fixture; return }

function Update-CleanMachineEvidenceHash {
    param([Parameter(Mandatory = $true)][pscustomobject]$Fixture)

    $auditPath = Join-Path $Fixture.Repository 'packaging\common\release-audit.json'
    $audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
    $audit.mandatoryEvidenceGates[0].evidence[0].sha256 =
        (Get-FileHash -LiteralPath $Fixture.CleanMachineEvidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Utf8NoBom -Path $auditPath -Content (($audit | ConvertTo-Json -Depth 12) + [Environment]::NewLine)
}

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

function Invoke-InstallerFixture {
    param(
        [Parameter(Mandatory = $true)] [pscustomobject]$Fixture,
        [switch]$DiscardOutput
    )

    $previousErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        if ($DiscardOutput.IsPresent) {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerPath -EvidenceRoot $Fixture.Evidence -DestinationRoot $Fixture.Destination -RepositoryRoot $Fixture.Repository 2>&1 | Out-Null
        }
        else {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerPath -EvidenceRoot $Fixture.Evidence -DestinationRoot $Fixture.Destination -RepositoryRoot $Fixture.Repository 2>&1 | Out-Host
        }
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorPreference
    }
}

function Invoke-ReleaseInstallerFixture {
    param(
        [Parameter(Mandatory = $true)] [pscustomobject]$Fixture,
        [switch]$DiscardOutput
    )

    $previousErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        if ($DiscardOutput.IsPresent) {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $releaseInstallerPath -EvidenceRoot $Fixture.Evidence -DestinationRoot $Fixture.Destination -ExpectedCommit $Fixture.ExpectedCommit -ExpectedVersion $Fixture.ExpectedVersion -RepositoryRoot $Fixture.Repository 2>&1 | Out-Null
        }
        else {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $releaseInstallerPath -EvidenceRoot $Fixture.Evidence -DestinationRoot $Fixture.Destination -ExpectedCommit $Fixture.ExpectedCommit -ExpectedVersion $Fixture.ExpectedVersion -RepositoryRoot $Fixture.Repository 2>&1 | Out-Host
        }
        return $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorPreference
    }
}

Invoke-Case -Name 'Exact reviewed runtime replaces the package payload and remains non-release-approved' -Action {
    $fixture = New-Fixture
    try {
        $exitCode = Invoke-InstallerFixture -Fixture $fixture
        if ($exitCode -ne 0) { throw "Installer exited $exitCode." }
        $installed = Get-ChildItem -LiteralPath $fixture.Destination -Recurse -File -Filter 'OpenCvSharpExtern.dll'
        $actualHash = (Get-FileHash -LiteralPath $installed.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $fixture.ReviewedHash) { throw 'Installed runtime hash does not match the reviewed runtime.' }
        $metadata = Get-Content -LiteralPath (Join-Path $fixture.Destination 'reviewed-opencv-runtime.json') -Raw | ConvertFrom-Json
        if (-not [bool]$metadata.provenanceValidated -or [bool]$metadata.cleanMachineEvidence -or [bool]$metadata.releaseApproved) {
            throw 'Runtime metadata crossed the provenance-only approval boundary.'
        }
    }
    finally {
        if (Test-Path -LiteralPath $fixture.Root) { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
    }
}

Invoke-Case -Name 'Tampered source runtime is rejected before replacement' -Action {
    $fixture = New-Fixture -TamperSource
    try {
        $exitCode = Invoke-InstallerFixture -Fixture $fixture -DiscardOutput
        if ($exitCode -eq 0) { throw 'Tampered source runtime unexpectedly passed.' }
        $remaining = (Get-FileHash -LiteralPath (Join-Path $fixture.Destination 'runtimes\win-x64\native\OpenCvSharpExtern.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($remaining -eq $fixture.ReviewedHash) { throw 'Destination changed after rejected tampering.' }
    }
    finally {
        if (Test-Path -LiteralPath $fixture.Root) { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
    }
}

Invoke-Case -Name 'Ambiguous published native destinations are rejected' -Action {
    $fixture = New-Fixture
    try {
        New-Item -ItemType Directory -Path (Join-Path $fixture.Destination 'duplicate') -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $fixture.Destination 'duplicate\OpenCvSharpExtern.dll'), 'duplicate', [Text.UTF8Encoding]::new($false))
        $exitCode = Invoke-InstallerFixture -Fixture $fixture -DiscardOutput
        if ($exitCode -eq 0) { throw 'Ambiguous destination unexpectedly passed.' }
    }
    finally {
        if (Test-Path -LiteralPath $fixture.Root) { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
    }
}

Invoke-Case -Name 'Direct clean-machine evidence promotes the exact reviewed runtime for release' -Action {
    $fixture = New-Fixture
    try {
        $exitCode = Invoke-ReleaseInstallerFixture -Fixture $fixture
        if ($exitCode -ne 0) { throw "Release installer exited $exitCode." }
        $metadata = Get-Content -LiteralPath (Join-Path $fixture.Destination 'reviewed-opencv-runtime.json') -Raw | ConvertFrom-Json
        if (-not [bool]$metadata.cleanMachineEvidence -or -not [bool]$metadata.releaseApproved) {
            throw 'Direct passing evidence did not promote the reviewed runtime.'
        }
        if ([string]$metadata.binarySha256 -ne $fixture.ReviewedHash) {
            throw 'Release metadata does not retain the exact reviewed runtime hash.'
        }
    }
    finally {
        if (Test-Path -LiteralPath $fixture.Root) { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
    }
}

Invoke-Case -Name 'Tampered clean-machine evidence blocks release promotion before replacement' -Action {
    $fixture = New-Fixture
    try {
        Write-Utf8NoBom -Path $fixture.CleanMachineEvidencePath -Content '{"status":"tampered"}'
        $exitCode = Invoke-ReleaseInstallerFixture -Fixture $fixture -DiscardOutput
        if ($exitCode -eq 0) { throw 'Tampered clean-machine evidence unexpectedly promoted the runtime.' }
        if (Test-Path -LiteralPath (Join-Path $fixture.Destination 'reviewed-opencv-runtime.json')) {
            throw 'Release metadata was written after evidence rejection.'
        }
    }
    finally {
        if (Test-Path -LiteralPath $fixture.Root) { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
    }
}

Invoke-Case -Name 'Hash-valid arbitrary JSON cannot promote release runtime' -Action {
    $fixture = New-Fixture
    try {
        Write-Utf8NoBom -Path $fixture.CleanMachineEvidencePath -Content '{"status":"pass","machine":"fixture"}'
        Update-CleanMachineEvidenceHash -Fixture $fixture
        $exitCode = Invoke-ReleaseInstallerFixture -Fixture $fixture -DiscardOutput
        if ($exitCode -eq 0) { throw 'Arbitrary hash-valid JSON unexpectedly promoted the runtime.' }
    }
    finally {
        if (Test-Path -LiteralPath $fixture.Root) { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
    }
}

Invoke-Case -Name 'Fail-status structured evidence cannot promote release runtime' -Action {
    $fixture = New-Fixture
    try {
        $report = Get-Content -LiteralPath $fixture.CleanMachineEvidencePath -Raw | ConvertFrom-Json
        $report.status = 'fail'
        Write-Utf8NoBom -Path $fixture.CleanMachineEvidencePath -Content (($report | ConvertTo-Json -Depth 12) + [Environment]::NewLine)
        Update-CleanMachineEvidenceHash -Fixture $fixture
        $exitCode = Invoke-ReleaseInstallerFixture -Fixture $fixture -DiscardOutput
        if ($exitCode -eq 0) { throw 'Fail-status evidence unexpectedly promoted the runtime.' }
    }
    finally {
        if (Test-Path -LiteralPath $fixture.Root) { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
    }
}

Invoke-Case -Name 'Wrong payload hash cannot promote release runtime' -Action {
    $fixture = New-Fixture
    try {
        $report = Get-Content -LiteralPath $fixture.CleanMachineEvidencePath -Raw | ConvertFrom-Json
        $report.payload.executableSha256 = ('9' * 64)
        Write-Utf8NoBom -Path $fixture.CleanMachineEvidencePath -Content (($report | ConvertTo-Json -Depth 12) + [Environment]::NewLine)
        Update-CleanMachineEvidenceHash -Fixture $fixture
        $exitCode = Invoke-ReleaseInstallerFixture -Fixture $fixture -DiscardOutput
        if ($exitCode -eq 0) { throw 'Wrong payload hash unexpectedly promoted the runtime.' }
    }
    finally {
        if (Test-Path -LiteralPath $fixture.Root) { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
    }
}

Invoke-Case -Name 'Different published executable cannot reuse clean-machine evidence' -Action {
    $fixture = New-Fixture
    try {
        Write-Utf8NoBom -Path (Join-Path $fixture.Destination 'GraphReader.App.exe') -Content "different application bytes`n"
        $exitCode = Invoke-ReleaseInstallerFixture -Fixture $fixture -DiscardOutput
        if ($exitCode -eq 0) { throw 'Different published executable unexpectedly reused clean-machine evidence.' }
    }
    finally {
        if (Test-Path -LiteralPath $fixture.Root) { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
    }
}

Invoke-Case -Name 'Failed native load cannot promote release runtime' -Action {
    $fixture = New-Fixture
    try {
        $report = Get-Content -LiteralPath $fixture.CleanMachineEvidencePath -Raw | ConvertFrom-Json
        $report.nativeLoad.succeeded = $false
        $report.nativeLoad.win32Error = 126
        Write-Utf8NoBom -Path $fixture.CleanMachineEvidencePath -Content (($report | ConvertTo-Json -Depth 12) + [Environment]::NewLine)
        Update-CleanMachineEvidenceHash -Fixture $fixture
        $exitCode = Invoke-ReleaseInstallerFixture -Fixture $fixture -DiscardOutput
        if ($exitCode -eq 0) { throw 'Failed native load unexpectedly promoted the runtime.' }
    }
    finally {
        if (Test-Path -LiteralPath $fixture.Root) { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
    }
}

Invoke-Case -Name 'Failed application smoke cannot promote release runtime' -Action {
    $fixture = New-Fixture
    try {
        $report = Get-Content -LiteralPath $fixture.CleanMachineEvidencePath -Raw | ConvertFrom-Json
        $report.applicationSmoke.passed = $false
        $report.applicationSmoke.exitCode = 2
        Write-Utf8NoBom -Path $fixture.CleanMachineEvidencePath -Content (($report | ConvertTo-Json -Depth 12) + [Environment]::NewLine)
        Update-CleanMachineEvidenceHash -Fixture $fixture
        $exitCode = Invoke-ReleaseInstallerFixture -Fixture $fixture -DiscardOutput
        if ($exitCode -eq 0) { throw 'Failed application smoke unexpectedly promoted the runtime.' }
    }
    finally {
        if (Test-Path -LiteralPath $fixture.Root) { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
    }
}

Invoke-Case -Name 'String false provenance cannot promote release runtime' -Action {
    $fixture = New-Fixture
    try {
        $report = Get-Content -LiteralPath $fixture.CleanMachineEvidencePath -Raw | ConvertFrom-Json
        $report.vmProvenance.isoSha256Verified = 'false'
        Write-Utf8NoBom -Path $fixture.CleanMachineEvidencePath -Content (($report | ConvertTo-Json -Depth 12) + [Environment]::NewLine)
        Update-CleanMachineEvidenceHash -Fixture $fixture
        $exitCode = Invoke-ReleaseInstallerFixture -Fixture $fixture -DiscardOutput
        if ($exitCode -eq 0) { throw 'String false provenance unexpectedly promoted the runtime.' }
    }
    finally {
        if (Test-Path -LiteralPath $fixture.Root) { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
    }
}

Invoke-Case -Name 'Blocked clean-machine gate cannot promote reviewed runtime bytes' -Action {
    $fixture = New-Fixture
    try {
        $auditPath = Join-Path $fixture.Repository 'packaging\common\release-audit.json'
        $audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
        $audit.mandatoryEvidenceGates[0].status = 'blocked'
        Write-Utf8NoBom -Path $auditPath -Content ($audit | ConvertTo-Json -Depth 10)
        $exitCode = Invoke-ReleaseInstallerFixture -Fixture $fixture -DiscardOutput
        if ($exitCode -eq 0) { throw 'Blocked clean-machine gate unexpectedly promoted the runtime.' }
        if (Test-Path -LiteralPath (Join-Path $fixture.Destination 'reviewed-opencv-runtime.json')) {
            throw 'Release metadata was written for a blocked gate.'
        }
    }
    finally {
        if (Test-Path -LiteralPath $fixture.Root) { Remove-Item -LiteralPath $fixture.Root -Recurse -Force }
    }
}

Write-Host "Reviewed OpenCV runtime packaging tests: $passed passed, $failed failed."
if ($failed -ne 0) { exit 1 }
