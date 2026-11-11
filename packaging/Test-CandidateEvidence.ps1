# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

<#[External evidence validation only. This does not approve or publish a release.]#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepositoryRoot,
    [Parameter(Mandatory)][string]$CandidateRoot,
    [Parameter(Mandatory)][string]$ReceiptPath,
    [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [string]$ReviewedOpenCvEvidenceRoot,
    [string]$ReviewedPdfiumEvidenceRoot,
    [switch]$AllowFixture
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\','/')
$CandidateRoot = [IO.Path]::GetFullPath($CandidateRoot).TrimEnd('\','/')
$ReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
$repoPrefix = $RepositoryRoot + [IO.Path]::DirectorySeparatorChar
if (-not $ReceiptPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Receipt must be inside the repository.' }
if ($ExpectedSourceCommit -cnotmatch '^[0-9a-f]{40}$') { throw 'Expected source commit must be a full lowercase SHA-1.' }
function Hash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}
function Assert-Hash([string]$Value, [string]$Label) { if ($Value -cnotmatch '^[0-9a-f]{64}$') { throw "Invalid $Label SHA-256." } }
function Assert-NoReparse([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse point is not permitted: $current" }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
function Get-TrackedBytes([string]$Revision, [string]$Path) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
    try {
        $process = Start-Process -FilePath 'git' -ArgumentList @('-C', ('"{0}"' -f $RepositoryRoot), 'show', ('"{0}:{1}"' -f $Revision, $Path)) -RedirectStandardOutput $temp -Wait -PassThru -WindowStyle Hidden
        if ($process.ExitCode -ne 0) { throw "Cannot read tracked $Path at $Revision." }
        return [IO.File]::ReadAllBytes($temp)
    } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
}
function BytesHash([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
}
function Assert-TrackedFile([string]$Revision, [string]$RelativePath, [string]$FilePath) {
    $expected = (& git -C $RepositoryRoot rev-parse "$($Revision):$RelativePath").Trim()
    if ($LASTEXITCODE -ne 0) { throw "Tracked file is missing: $RelativePath at $Revision." }
    $actual = (& git -C $RepositoryRoot hash-object "--path=$RelativePath" $FilePath).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -cne $expected) { throw "Evidence record differs from tracked $Revision`: $RelativePath" }
}
function Safe-Leaf([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or [IO.Path]::GetFileName($Name) -cne $Name -or $Name -in @('.', '..') -or $Name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $Name.Contains(':')) { throw 'Unsafe artifact filename.' }
    return Join-Path $CandidateRoot $Name
}
$metadataPath = Join-Path $CandidateRoot 'release-metadata.json'
foreach ($path in @($RepositoryRoot, $CandidateRoot, $ReceiptPath, $metadataPath)) { Assert-NoReparse $path }
$metadata = Get-Content -Raw -LiteralPath $metadataPath | ConvertFrom-Json
$receipt = Get-Content -Raw -LiteralPath $ReceiptPath | ConvertFrom-Json
if ($null -ne $receipt.PSObject.Properties['fixture'] -and $receipt.fixture -eq $true -and -not $AllowFixture) { throw 'Synthetic fixture evidence is not accepted without -AllowFixture.' }
if ($metadata.schemaVersion -ne 1 -or $metadata.gitCommit -cne $ExpectedSourceCommit -or $metadata.version -cne $ExpectedVersion -or $metadata.rid -cne 'win-x64') { throw 'Candidate metadata source or version differs.' }
if ($receipt.schema -cne 'graphreader.candidate-evidence.v1' -or $receipt.review.status -cne 'approved' -or [string]::IsNullOrWhiteSpace([string]$receipt.review.reviewer)) { throw 'Receipt lacks an approved named review.' }
if ($receipt.identity.sourceCommit -cne $ExpectedSourceCommit -or $receipt.identity.version -cne $ExpectedVersion -or $receipt.identity.metadataSha256 -cne (Hash $metadataPath)) { throw 'Receipt candidate identity differs.' }
foreach ($kind in @('installer','portable')) {
    $file = Safe-Leaf ([string]$metadata.$kind.fileName)
    Assert-NoReparse $file
    $hash = Hash $file
    Assert-Hash ([string]$metadata.$kind.sha256) $kind
    if ($hash -cne [string]$metadata.$kind.sha256 -or $hash -cne [string]$receipt.identity.$kind) { throw "$kind artifact identity differs." }
}
$commonRoot = [IO.Path]::GetFullPath((Join-Path $CandidateRoot '..\common\publish')).TrimEnd('\','/')
if (-not (Test-Path -LiteralPath $commonRoot -PathType Container)) { throw 'Common publish directory missing.' }
Assert-NoReparse $commonRoot
foreach ($item in @(Get-ChildItem -LiteralPath $commonRoot -Force -Recurse)) { if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point is not permitted: $($item.FullName)" } }
$records = @(Get-ChildItem -LiteralPath $commonRoot -File -Recurse | ForEach-Object {
    [pscustomobject]@{ path = $_.FullName.Substring($commonRoot.Length).TrimStart('\','/').Replace('\','/'); sha256 = Hash $_.FullName; size = $_.Length }
})
[string[]]$paths = @($records | ForEach-Object path)
[Array]::Sort($paths, [StringComparer]::Ordinal)
$lines = @($paths | ForEach-Object { $p = $_; $record = @($records | Where-Object { $_.path -ceq $p }); if ($record.Count -ne 1) { throw 'Duplicate common payload path.' }; "$($record[0].sha256)  $p" })
$digest = BytesHash ([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))
if ($digest -cne [string]$metadata.commonPayload.sha256 -or $digest -cne [string]$receipt.identity.commonPayload -or $records.Count -ne [int]$metadata.commonPayload.fileCount) { throw 'Common payload identity differs.' }
if ($metadata.installer.sharedPayloadSha256 -cne $digest -or $metadata.portable.sharedPayloadSha256 -cne $digest) { throw 'Shared payload metadata differs.' }
$declared = @($metadata.commonPayload.files)
if ($declared.Count -ne $records.Count) { throw 'Common payload file list differs.' }
foreach ($actualRecord in $records) {
    $matches = @($declared | Where-Object { $_.path -ceq $actualRecord.path })
    if ($matches.Count -ne 1 -or [string]$matches[0].sha256 -cne $actualRecord.sha256 -or [long]$matches[0].size -ne $actualRecord.size) { throw "Common payload file list differs: $($actualRecord.path)" }
}
$appRecord = @($records | Where-Object { $_.path -ceq 'GraphReader.App.exe' })
if ($appRecord.Count -ne 1) { throw 'Common payload must contain exactly one application executable.' }
$gateChecks = [ordered]@{
    'installer-clean-windows-vm' = @('install','launch','repair_or_reinstall','upgrade','uninstall','user_data_preservation','start_menu','path_with_spaces','offline_workflow','image_import','batch_import','pdf_import','model_discovery','cpu_fallback','save_autosave_recovery','csv_export','licenses_notices','settings_path_isolation')
    'portable-clean-windows-vm' = @('extract_and_launch','offline_workflow','image_import','batch_import','pdf_import','model_discovery','cpu_fallback','save_autosave_recovery','csv_export','licenses_notices','settings_path_isolation','no_writes_outside_allowed_roots','registry_independence','spaces_and_unicode_path','read_only_diagnostics')
    'release-artifact-pair-parity' = @('version','source_commit','contract','models','notices','checksums','forbidden_file_scan','common_payload')
}
$vm = $receipt.vmProvenance
if ($vm.environmentKind -cne 'fresh-windows-evaluation-vm' -or $vm.networkMode -cne 'none' -or $vm.freshInstall -isnot [bool] -or $vm.freshInstall -ne $true -or $vm.isoSha256Verified -isnot [bool] -or $vm.isoSha256Verified -ne $true) { throw 'Receipt clean-machine provenance is incomplete.' }
if ($vm.vmId -isnot [string] -or $vm.observedVmId -isnot [string] -or $vm.vmId -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' -or $vm.observedVmId -ine $vm.vmId -or $vm.vmId -eq '00000000-0000-0000-0000-000000000000') { throw 'Receipt VM UUID is invalid or mismatched.' }
if ($vm.osBuild -isnot [string] -or $vm.osBuild -notmatch '^[0-9]+$' -or $vm.expectedOsBuild -cne $vm.osBuild -or ($vm.osUbr -isnot [long] -and $vm.osUbr -isnot [int]) -or $vm.osUbr -lt 0 -or ($vm.expectedOsUbr -isnot [long] -and $vm.expectedOsUbr -isnot [int]) -or $vm.expectedOsUbr -ne $vm.osUbr -or $vm.qemuVersion -isnot [string] -or $vm.qemuVersion -notmatch '^QEMU\s') { throw 'Receipt VM OS or QEMU identity is invalid.' }
foreach ($name in @('isoSha256','officialIsoSha256','vmConfigurationSha256','qemuInstallerSha256','expectedExecutableSha256')) { Assert-Hash ([string]$vm.$name) $name }
if ($vm.isoSha256 -cne $vm.officialIsoSha256) { throw 'Receipt ISO hashes differ.' }
if ($vm.expectedCommit -cne $ExpectedSourceCommit -or $vm.expectedVersion -cne $ExpectedVersion -or $vm.expectedExecutableSha256 -cne $appRecord[0].sha256) { throw 'Receipt VM candidate identity differs.' }
function Get-UtcTime([object]$Value) {
    if ($Value -is [DateTime]) { if ($Value.Kind -ne [DateTimeKind]::Utc) { throw 'Receipt VM timestamps are invalid.' }; return [DateTimeOffset]$Value }
    $parsed = [DateTimeOffset]::MinValue
    if ($Value -isnot [string] -or $Value -notmatch '(Z|\+00:00)$' -or -not [DateTimeOffset]::TryParse($Value, [ref]$parsed) -or $parsed.Offset -ne [TimeSpan]::Zero) { throw 'Receipt VM timestamps are invalid.' }
    return $parsed
}
$observed = Get-UtcTime $vm.observedAtUtc
$installed = Get-UtcTime $vm.installTimeUtc
$age = ($observed - $installed).TotalHours
if ($age -lt 0 -or $age -gt 24 -or $observed -gt [DateTimeOffset]::UtcNow) { throw 'Receipt VM install age at observation is invalid.' }
foreach ($id in $gateChecks.Keys) {
    $gate = @($receipt.gates | Where-Object { $_.id -ceq $id })
    if ($gate.Count -ne 1) { throw "Receipt must contain exactly one $id gate." }
    foreach ($check in $gateChecks[$id]) {
        $property = $gate[0].checks.PSObject.Properties[$check]
        if ($null -eq $property -or $property.Value -isnot [bool] -or $property.Value -ne $true) { throw "Receipt check $id/$check is not observed passing." }
    }
    $reportRelative = [string]$gate[0].report.path
    $harnessRelative = [string]$gate[0].report.harnessPath
    foreach ($relative in @($reportRelative, $harnessRelative)) {
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains('\') -or @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0 -or $relative.Contains(':')) { throw "Unsafe report or harness path: $relative" }
    }
    if (-not $harnessRelative.StartsWith('packaging/', [StringComparison]::Ordinal) -or -not $harnessRelative.EndsWith('.ps1', [StringComparison]::Ordinal)) { throw 'Report harness path is invalid.' }
    $reportPath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $reportRelative))
    Assert-NoReparse $reportPath
    Assert-Hash ([string]$gate[0].report.sha256) 'report'
    Assert-Hash ([string]$gate[0].report.harnessSha256) 'harness'
    if ((Hash $reportPath) -cne $gate[0].report.sha256) { throw "Report identity differs: $id" }
    Assert-TrackedFile 'HEAD' $reportRelative $reportPath
    if ((BytesHash (Get-TrackedBytes $ExpectedSourceCommit $harnessRelative)) -cne $gate[0].report.harnessSha256) { throw "Report harness identity differs: $id" }
    $report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
    if ($report.schema -cne 'graphreader.candidate-observation.v1' -or $report.gateId -cne $id -or $report.sourceCommit -cne $ExpectedSourceCommit -or $report.version -cne $ExpectedVersion -or $report.executableSha256 -cne $appRecord[0].sha256 -or $report.harnessSha256 -cne $gate[0].report.harnessSha256 -or $report.vmId -ine $vm.vmId -or (Get-UtcTime $report.observedAtUtc) -ne $observed) { throw "Report candidate or VM identity differs: $id" }
}
if (@($receipt.gates).Count -ne $gateChecks.Count) { throw 'Receipt has unexpected gates.' }
$auditRelative = 'packaging/common/release-audit.json'
$auditPath = Join-Path $RepositoryRoot $auditRelative
$receiptRelative = $ReceiptPath.Substring($repoPrefix.Length).Replace('\','/')
foreach ($pair in @(@($auditPath, $auditRelative), @($ReceiptPath, $receiptRelative))) {
    Assert-TrackedFile 'HEAD' $pair[1] $pair[0]
}
$currentAudit = Get-Content -Raw -LiteralPath $auditPath | ConvertFrom-Json
$sourceBytes = Get-TrackedBytes $ExpectedSourceCommit $auditRelative
$nativeEvidenceCommit = $null
if ($null -ne $metadata.PSObject.Properties['nativeEvidenceCommit']) {
    $nativeEvidenceCommit = [string]$metadata.nativeEvidenceCommit
    if ([string]::IsNullOrWhiteSpace($ReviewedOpenCvEvidenceRoot) -or [string]::IsNullOrWhiteSpace($ReviewedPdfiumEvidenceRoot)) { throw 'Frozen candidate verification requires both reviewed native evidence roots.' }
    . (Join-Path $PSScriptRoot 'FrozenNative.Common.ps1')
    Assert-FrozenNativeAuditTransition $RepositoryRoot $ExpectedSourceCommit $nativeEvidenceCommit
    $sourceBytes = Get-TrackedBytes $nativeEvidenceCommit $auditRelative
}
$embeddedPath = Join-Path $commonRoot 'release-audit.json'
Assert-TrackedFile $(if ($null -ne $nativeEvidenceCommit) { $nativeEvidenceCommit } else { $ExpectedSourceCommit }) $auditRelative $embeddedPath
$sourceAudit = [Text.Encoding]::UTF8.GetString($sourceBytes).TrimStart([char]0xfeff) | ConvertFrom-Json
foreach ($gate in @($sourceAudit.mandatoryEvidenceGates)) {
    if (-not $gateChecks.Contains([string]$gate.id) -and $gate.status -cne 'pass') { throw "Non-deferred prerequisite gate is not pass: $($gate.id)." }
}
foreach ($id in $gateChecks.Keys) {
    $sourceGate = @($sourceAudit.mandatoryEvidenceGates | Where-Object { $_.id -ceq $id })
    $currentGate = @($currentAudit.mandatoryEvidenceGates | Where-Object { $_.id -ceq $id })
    if ($sourceGate.Count -ne 1 -or $sourceGate[0].status -cne 'blocked' -or $currentGate.Count -ne 1 -or $currentGate[0].status -cne 'pass') { throw "Invalid deferred gate state: $id." }
    if (@($currentGate[0].evidence | Where-Object { $_.path -ceq $receiptRelative -and $_.sha256 -ceq (Hash $ReceiptPath) }).Count -ne 1) { throw "Tracked gate $id does not bind the reviewed receipt." }
    foreach ($field in @('status','evidence','notes')) { $sourceGate[0].$field = $currentGate[0].$field }
}
if (($sourceAudit | ConvertTo-Json -Depth 100 -Compress) -cne ($currentAudit | ConvertTo-Json -Depth 100 -Compress)) { throw 'Non-deferred audit content differs from candidate source.' }
if ($null -ne $nativeEvidenceCommit) {
    $workingChanges = @(& git -C $RepositoryRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0 -or $workingChanges.Count -ne 0) { throw 'Frozen candidate evidence verification requires a clean evidence checkout.' }
    & git -C $RepositoryRoot merge-base --is-ancestor $nativeEvidenceCommit HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Native evidence commit is not an ancestor of final evidence commit.' }
    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $null = $allowed.Add($auditRelative); $null = $allowed.Add($receiptRelative)
    foreach ($gate in $receipt.gates) { $null = $allowed.Add([string]$gate.report.path) }
    $changed = @(& git -C $RepositoryRoot diff --no-renames --name-only $nativeEvidenceCommit HEAD)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect final evidence transition.' }
    foreach ($path in $changed) { if (-not $allowed.Contains([string]$path)) { throw "Final evidence commit changes unrelated source: $path" } }
    $tempRoot = Join-Path $RepositoryRoot ('artifacts\candidate-native-verification-' + [Guid]::NewGuid().ToString('N'))
    Assert-NoReparse $tempRoot
    try {
        $cvRoot = Join-Path $tempRoot 'opencv'; $pdfRoot = Join-Path $tempRoot 'pdfium'
        New-Item -ItemType Directory -Path $cvRoot,$pdfRoot -Force | Out-Null
        foreach ($root in @($cvRoot,$pdfRoot)) {
            foreach ($name in @('GraphReader.App.exe','GraphReader.App.dll')) { Copy-Item -LiteralPath (Join-Path $commonRoot $name) -Destination $root }
        }
        Copy-Item -LiteralPath (Join-Path $commonRoot 'OpenCvSharpExtern.dll') -Destination $cvRoot
        & (Join-Path $RepositoryRoot 'packaging\opencv-source\Install-ReleaseRuntime.ps1') -RepositoryRoot $RepositoryRoot -EvidenceRoot $ReviewedOpenCvEvidenceRoot -DestinationRoot $cvRoot -ExpectedCommit $ExpectedSourceCommit -ExpectedVersion $ExpectedVersion | Out-Null
        & (Join-Path $RepositoryRoot 'packaging\pdfium-source\Install-ReleaseRenderer.ps1') -RepositoryRoot $RepositoryRoot -EvidenceRoot $ReviewedPdfiumEvidenceRoot -DestinationRoot $pdfRoot -ExpectedCommit $ExpectedSourceCommit -ExpectedVersion $ExpectedVersion | Out-Null
    }
    finally {
        $prefix = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts')).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
        if ([IO.Path]::GetFullPath($tempRoot).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $tempRoot)) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
    }
}
$isFixture = $null -ne $receipt.PSObject.Properties['fixture'] -and $receipt.fixture -eq $true
[pscustomobject]@{ status = $(if ($isFixture) { 'fixture-evidence-validated' } else { 'evidence-validated' }); sourceCommit = $ExpectedSourceCommit; version = $ExpectedVersion; releaseApproved = $false; published = $false }
