# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $projectRoot 'packaging\FrozenNative.Common.ps1')
$testRoot = Join-Path $projectRoot ('artifacts\frozen-native-fixture-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $testRoot 'repository'
$publish = Join-Path $testRoot 'publish'
function Save-Json([string]$Path, [object]$Object) { New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null; [IO.File]::WriteAllText($Path, (($Object | ConvertTo-Json -Depth 20) + "`n"), [Text.UTF8Encoding]::new($true)) }
function Commit([string]$Message) { & git -C $repo add --all; & git -C $repo -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m $Message; if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture commit failed.' }; return (& git -C $repo rev-parse HEAD).Trim() }
function Reject([scriptblock]$Action, [string]$Reason) { try { & $Action | Out-Null } catch { if ($_.Exception.Message -like "*$Reason*") { return }; throw }; throw "Expected rejection: $Reason" }
try {
    New-Item -ItemType Directory -Path $repo,$publish -Force | Out-Null
    & git -C $repo init --quiet
    $auditPath = Join-Path $repo 'packaging/common/release-audit.json'
    $audit = [ordered]@{schemaVersion=1; mandatoryEvidenceGates=@(@{id='opencv-clean-machine-load';status='blocked';notes='synthetic';evidence=@()},@{id='pdfium-clean-machine-load';status='blocked';notes='synthetic';evidence=@()},@{id='production-workflow';status='pass';notes='synthetic';evidence=@()})}
    Save-Json $auditPath $audit
    New-Item -ItemType Directory -Path (Join-Path $repo 'packaging/clean-machine') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $repo 'packaging/clean-machine/fixture-harness.ps1'), '# synthetic fixture')
    $source = Commit 'Synthetic source A'
    [IO.File]::WriteAllText((Join-Path $publish 'GraphReader.App.exe'), 'synthetic EXE A')
    [IO.File]::WriteAllText((Join-Path $publish 'GraphReader.App.dll'), 'synthetic DLL A')
    $receipt = [pscustomobject]@{schema='graphreader.frozen-native-publish.v1';sourceCommit=$source;version='0.4.36';runtimeMode='Production';files=@(Get-FrozenNativeFiles $publish)}
    $null = Assert-FrozenNativeFiles $publish $receipt '0.4.36'
    $preview = Join-Path $testRoot 'preview'
    New-Item -ItemType Directory -Path $preview | Out-Null
    Copy-Item -LiteralPath (Join-Path $publish 'GraphReader.App.exe') -Destination $preview
    [IO.File]::WriteAllText((Join-Path $preview 'portable.mode'), '')
    $info = [ordered]@{schemaVersion=2;runtimeMode='ManualPreview';commit=$source;version='0.4.36';dirty=$false;executableSha256=(Get-FileHash -LiteralPath (Join-Path $preview 'GraphReader.App.exe') -Algorithm SHA256).Hash.ToLowerInvariant();availableModelIds=@()}
    Save-Json (Join-Path $preview 'build-info.json') $info
    Assert-FrozenOpenCvDiagnostic $preview $receipt '0.4.36'
    $info.commit=('a'*40); Save-Json (Join-Path $preview 'build-info.json') $info
    Reject { Assert-FrozenOpenCvDiagnostic $preview $receipt '0.4.36' } 'not the clean model-free source-A preview'
    $info.commit=$source; Save-Json (Join-Path $preview 'build-info.json') $info
    [IO.File]::AppendAllText((Join-Path $preview 'GraphReader.App.exe'), 'tamper')
    Reject { Assert-FrozenOpenCvDiagnostic $preview $receipt '0.4.36' } 'does not match the frozen production host'
    [IO.File]::AppendAllText((Join-Path $publish 'GraphReader.App.dll'), 'tamper')
    Reject { Assert-FrozenNativeFiles $publish $receipt '0.4.36' } 'Frozen publish file differs'
    [IO.File]::WriteAllText((Join-Path $publish 'GraphReader.App.dll'), 'synthetic DLL A')
    foreach ($gate in $audit.mandatoryEvidenceGates | Where-Object { $_.id -like '*clean-machine-load' }) {
        $name = if ($gate.id -eq 'opencv-clean-machine-load') { 'opencv.json' } else { 'pdfium.json' }
        $relative = "packaging/evidence/$name"
        $report = Join-Path $repo $relative
        Save-Json $report ([ordered]@{fixture=$true; status='synthetic-only'})
        $gate.status='pass'; $gate.notes='synthetic fixture'; $gate.evidence=@(@{path=$relative;sha256=(Get-FileHash -LiteralPath $report -Algorithm SHA256).Hash.ToLowerInvariant()})
    }
    Save-Json $auditPath $audit
    $evidence = Commit 'Synthetic evidence B'
    Assert-FrozenNativeAuditTransition $repo $source $evidence
    Reject { Assert-FrozenNativeAuditTransition $repo ('a'*40) $evidence } 'not an ancestor'
    [IO.File]::AppendAllText((Join-Path $repo 'packaging/clean-machine/fixture-harness.ps1'), 'tamper')
    $changed = Commit 'Synthetic harness drift'
    Reject { Assert-FrozenNativeAuditTransition $repo $source $changed } 'changes source or unrelated files'
    $audit.mandatoryEvidenceGates[2].notes='unrelated drift'; Save-Json $auditPath $audit
    $changedAudit = Commit 'Synthetic audit drift'
    Reject { Assert-FrozenNativeAuditTransition $repo $source $changedAudit } 'changes non-native audit content'
    Write-Host 'Frozen native synthetic handoff checks passed.'
}
finally {
    $prefix = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    if ([IO.Path]::GetFullPath($testRoot).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $testRoot)) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
