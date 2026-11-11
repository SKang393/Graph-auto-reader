# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$verifier = Join-Path $projectRoot 'packaging\Test-CandidateEvidence.ps1'
$testRoot = Join-Path $projectRoot ('artifacts\candidate-evidence-fixture-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $testRoot 'fixture-repository'
$artifactRoot = Join-Path $testRoot 'candidate\release'
$commonRoot = Join-Path $testRoot 'candidate\common\publish'
$auditPath = Join-Path $repo 'packaging\common\release-audit.json'
$receiptPath = Join-Path $repo 'evidence\fixture-receipt.json'
$ids = @('installer-clean-windows-vm','portable-clean-windows-vm','release-artifact-pair-parity')
function Write-Json([string]$Path, [object]$Value) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 30) + "`n"), [Text.UTF8Encoding]::new($false))
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Commit-Fixture([string]$Message) {
    & git -C $repo add --all
    if ($LASTEXITCODE -ne 0) { throw 'Fixture git add failed.' }
    & git -C $repo -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m $Message
    if ($LASTEXITCODE -ne 0) { throw 'Fixture git commit failed.' }
}
function Reject([string]$Reason) {
    try { & $verifier -RepositoryRoot $repo -CandidateRoot $artifactRoot -ReceiptPath $receiptPath -ExpectedSourceCommit $sourceCommit -ExpectedVersion '2.0.0' -AllowFixture | Out-Null } catch {
        if ($_.Exception.Message -like "*$Reason*") { return }
        throw "Expected '$Reason', got: $($_.Exception.Message)"
    }
    throw "Expected rejection: $Reason"
}
try {
    New-Item -ItemType Directory -Path $repo,$artifactRoot,$commonRoot -Force | Out-Null
    & git -C $repo init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Fixture git init failed.' }
    $sourceAudit = [ordered]@{ schemaVersion = 1; mandatoryEvidenceGates = @(
        [ordered]@{ id = 'production-workflow'; status = 'pass'; notes = 'fixture'; evidence = @() },
        ($ids | ForEach-Object { [ordered]@{ id = $_; status = 'blocked'; notes = 'fixture'; evidence = @() } })
    ) }
    # Keep the fixture gate array flat, including on Windows PowerShell.
    $sourceAudit.mandatoryEvidenceGates = @([ordered]@{ id = 'production-workflow'; status = 'pass'; notes = 'fixture'; evidence = @() }) + @($ids | ForEach-Object { [ordered]@{ id = $_; status = 'blocked'; notes = 'fixture'; evidence = @() } })
    Write-Json $auditPath $sourceAudit
    [IO.File]::WriteAllText($auditPath, [IO.File]::ReadAllText($auditPath), [Text.UTF8Encoding]::new($true))
    $harnessPath = Join-Path $repo 'packaging\fixture-harness.ps1'
    [IO.File]::WriteAllText($harnessPath, '# Synthetic fixture harness')
    Commit-Fixture 'Fixture candidate source'
    $sourceCommit = (& git -C $repo rev-parse HEAD).Trim()
    Copy-Item -LiteralPath $auditPath -Destination (Join-Path $commonRoot 'release-audit.json')
    Copy-Item -LiteralPath $auditPath -Destination (Join-Path $testRoot 'source-audit.json')
    [IO.File]::WriteAllText((Join-Path $commonRoot 'GraphReader.App.exe'), 'synthetic fixture application')
    [IO.File]::WriteAllText((Join-Path $artifactRoot 'fixture-setup.exe'), 'fixture installer')
    [IO.File]::WriteAllText((Join-Path $artifactRoot 'fixture-portable.zip'), 'fixture portable')
    $payloadFileHash = Hash (Join-Path $commonRoot 'release-audit.json')
    $appHash = Hash (Join-Path $commonRoot 'GraphReader.App.exe')
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $payloadHash = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes("$appHash  GraphReader.App.exe`n$payloadFileHash  release-audit.json")))).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
    $metadataPath = Join-Path $artifactRoot 'release-metadata.json'
    $metadata = [ordered]@{ schemaVersion=1; gitCommit=$sourceCommit; version='2.0.0'; rid='win-x64'; commonPayload=@{sha256=$payloadHash; fileCount=2; files=@(@{path='release-audit.json'; size=(Get-Item (Join-Path $commonRoot 'release-audit.json')).Length; sha256=$payloadFileHash}, @{path='GraphReader.App.exe'; size=(Get-Item (Join-Path $commonRoot 'GraphReader.App.exe')).Length; sha256=$appHash})}; installer=@{fileName='fixture-setup.exe'; sha256=(Hash (Join-Path $artifactRoot 'fixture-setup.exe')); sharedPayloadSha256=$payloadHash}; portable=@{fileName='fixture-portable.zip'; sha256=(Hash (Join-Path $artifactRoot 'fixture-portable.zip')); sharedPayloadSha256=$payloadHash} }
    Write-Json $metadataPath $metadata
    $checks = @(
        @('install','launch','repair_or_reinstall','upgrade','uninstall','user_data_preservation','start_menu','path_with_spaces','offline_workflow','image_import','batch_import','pdf_import','model_discovery','cpu_fallback','save_autosave_recovery','csv_export','licenses_notices','settings_path_isolation'),
        @('extract_and_launch','offline_workflow','image_import','batch_import','pdf_import','model_discovery','cpu_fallback','save_autosave_recovery','csv_export','licenses_notices','settings_path_isolation','no_writes_outside_allowed_roots','registry_independence','spaces_and_unicode_path','read_only_diagnostics'),
        @('version','source_commit','contract','models','notices','checksums','forbidden_file_scan','common_payload')
    )
    $gates = for ($i=0; $i -lt 3; $i++) {
        $values = [ordered]@{}; foreach ($name in $checks[$i]) { $values[$name] = $true }
        $reportRelative = "evidence/fixture-report-$i.json"
        $reportPath = Join-Path $repo $reportRelative
        Write-Json $reportPath ([ordered]@{schema='graphreader.candidate-observation.v1'; gateId=$ids[$i]; sourceCommit=$sourceCommit; version='2.0.0'; executableSha256=$appHash; harnessSha256=(Hash $harnessPath); vmId='8edb9b52-d3a1-4cda-96b0-54cc5696630a'; observedAtUtc='2026-10-31T16:00:00Z'})
        [ordered]@{ id=$ids[$i]; checks=$values; report=@{path=$reportRelative; sha256=(Hash $reportPath); harnessPath='packaging/fixture-harness.ps1'; harnessSha256=(Hash $harnessPath)} }
    }
    $receipt = [ordered]@{ schema='graphreader.candidate-evidence.v1'; fixture=$true; review=@{status='approved'; reviewer='Synthetic fixture'}; identity=@{sourceCommit=$sourceCommit; version='2.0.0'; metadataSha256=(Hash $metadataPath); commonPayload=$payloadHash; installer=$metadata.installer.sha256; portable=$metadata.portable.sha256}; vmProvenance=@{environmentKind='fresh-windows-evaluation-vm'; networkMode='none'; freshInstall=$true; isoSha256Verified=$true; vmId='8edb9b52-d3a1-4cda-96b0-54cc5696630a'; observedVmId='8edb9b52-d3a1-4cda-96b0-54cc5696630a'; osBuild='26200'; expectedOsBuild='26200'; osUbr=6584; expectedOsUbr=6584; observedAtUtc='2026-10-31T16:00:00Z'; installTimeUtc='2026-10-31T14:00:00Z'; qemuVersion='QEMU emulator version 11.0'; qemuInstallerSha256=('c'*64); expectedCommit=$sourceCommit; expectedVersion='2.0.0'; expectedExecutableSha256=$appHash; isoSha256=('a'*64); officialIsoSha256=('a'*64); vmConfigurationSha256=('b'*64)}; gates=$gates }
    Write-Json $receiptPath $receipt
    $currentAudit = Get-Content -Raw $auditPath | ConvertFrom-Json
    foreach ($gate in $currentAudit.mandatoryEvidenceGates | Where-Object { $_.id -in $ids }) { $gate.status='pass'; $gate.notes='fixture reviewed'; $gate.evidence=@(@{path='evidence/fixture-receipt.json'; sha256=(Hash $receiptPath)}) }
    Write-Json $auditPath $currentAudit
    Commit-Fixture 'Fixture reviewed evidence'
    try { & $verifier -RepositoryRoot $repo -CandidateRoot $artifactRoot -ReceiptPath $receiptPath -ExpectedSourceCommit $sourceCommit -ExpectedVersion '2.0.0' | Out-Null; throw 'Fixture receipt was accepted without explicit fixture mode.' } catch { if ($_.Exception.Message -notlike '*Synthetic fixture evidence*') { throw } }
    $result = & $verifier -RepositoryRoot $repo -CandidateRoot $artifactRoot -ReceiptPath $receiptPath -ExpectedSourceCommit $sourceCommit -ExpectedVersion '2.0.0' -AllowFixture
    if ($result.status -ne 'fixture-evidence-validated' -or $result.releaseApproved -ne $false) { throw 'Valid fixture was not verified as evidence only.' }
    $receipt.gates[0].checks.install = $false; Write-Json $receiptPath $receipt; Reject 'not observed passing'
    $receipt.gates[0].checks.install = $true; $receipt.review.reviewer = ''; Write-Json $receiptPath $receipt; Reject 'named review'
    $receipt.review.reviewer = 'Synthetic fixture'; $receipt.identity.sourceCommit = ('a'*40); Write-Json $receiptPath $receipt; Reject 'candidate identity differs'
    $receipt.identity.sourceCommit = $sourceCommit; Write-Json $receiptPath $receipt
    $receipt.vmProvenance.freshInstall = 'true'; Write-Json $receiptPath $receipt; Reject 'provenance is incomplete'
    $receipt.vmProvenance.freshInstall = $true; Write-Json $receiptPath $receipt
    $receipt.vmProvenance.vmId = 'fixture'; Write-Json $receiptPath $receipt; Reject 'UUID is invalid'
    $receipt.vmProvenance.vmId = '8edb9b52-d3a1-4cda-96b0-54cc5696630a'; $receipt.vmProvenance.osBuild = 'fixture'; Write-Json $receiptPath $receipt; Reject 'OS or QEMU identity'
    $receipt.vmProvenance.osBuild = '26200'; $receipt.vmProvenance.osUbr = '6584'; Write-Json $receiptPath $receipt; Reject 'OS or QEMU identity'
    $receipt.vmProvenance.osUbr = 6584; $receipt.vmProvenance.installTimeUtc = '2026-10-30T14:00:00Z'; Write-Json $receiptPath $receipt; Reject 'install age at observation'
    $receipt.vmProvenance.installTimeUtc = '2026-10-31T17:00:00Z'; Write-Json $receiptPath $receipt; Reject 'install age at observation'
    $receipt.vmProvenance.installTimeUtc = '2026-10-31T14:00:00Z'; $receipt.vmProvenance.expectedExecutableSha256 = ('d'*64); Write-Json $receiptPath $receipt; Reject 'VM candidate identity differs'
    $receipt.vmProvenance.expectedExecutableSha256 = $appHash; $receipt.vmProvenance.expectedCommit = ('a'*40); Write-Json $receiptPath $receipt; Reject 'VM candidate identity differs'
    $receipt.vmProvenance.expectedCommit = $sourceCommit; $receipt.gates[0].report.harnessSha256 = ('e'*64); Write-Json $receiptPath $receipt; Reject 'harness identity differs'
    $receipt.gates[0].report.harnessSha256 = Hash $harnessPath; Write-Json $receiptPath $receipt
    $metadata.commonPayload.files[0].size++; Write-Json $metadataPath $metadata; $receipt.identity.metadataSha256 = Hash $metadataPath; Write-Json $receiptPath $receipt; Reject 'Common payload file list differs'
    $metadata.commonPayload.files[0].size--; $metadata.installer.sharedPayloadSha256 = ('0'*64); Write-Json $metadataPath $metadata; $receipt.identity.metadataSha256 = Hash $metadataPath; Write-Json $receiptPath $receipt; Reject 'Shared payload metadata differs'
    $metadata.installer.sharedPayloadSha256 = $payloadHash; Write-Json $metadataPath $metadata; $receipt.identity.metadataSha256 = Hash $metadataPath; Write-Json $receiptPath $receipt
    [IO.File]::AppendAllText((Join-Path $artifactRoot 'fixture-setup.exe'), 'tampered'); Reject 'installer artifact identity differs'
    [IO.File]::WriteAllText((Join-Path $artifactRoot 'fixture-setup.exe'), 'fixture installer')
    [IO.File]::AppendAllText((Join-Path $commonRoot 'release-audit.json'), ' '); Reject 'Common payload identity differs'
    Copy-Item -LiteralPath (Join-Path $testRoot 'source-audit.json') -Destination (Join-Path $commonRoot 'release-audit.json') -Force
    foreach ($gate in $currentAudit.mandatoryEvidenceGates | Where-Object { $_.id -in $ids }) { $gate.evidence[0].sha256 = Hash $receiptPath }
    $currentAudit | Add-Member -NotePropertyName components -NotePropertyValue @('unexpected change')
    Write-Json $auditPath $currentAudit
    Commit-Fixture 'Fixture unrelated audit change'
    Reject 'Non-deferred audit content differs'
    Write-Host 'Candidate evidence synthetic fixture checks passed.'
}
finally {
    $prefix = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
