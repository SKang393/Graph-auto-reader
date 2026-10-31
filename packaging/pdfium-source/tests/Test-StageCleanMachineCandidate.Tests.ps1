# SPDX-License-Identifier: Apache-2.0
# Synthetic-only staging fixtures. All output remains under project artifacts.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$script = Join-Path $projectRoot 'packaging\pdfium-source\Stage-CleanMachineCandidate.ps1'
$originalScript = $script
$testRoot = Join-Path $projectRoot ('artifacts\temp\pdfium-candidate-test-' + [Guid]::NewGuid().ToString('N'))
$repo = Join-Path $testRoot 'fixture'
$evidence = Join-Path $repo 'artifacts\evidence'
$candidate = Join-Path $repo 'artifacts\candidate'
New-Item -ItemType Directory -Path (Join-Path $evidence 'bin'), $candidate, (Join-Path $repo 'packaging\common') -Force | Out-Null
function Write-Text([string]$Path, [string]$Value) { [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false)) }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Must-Fail([scriptblock]$Action, [string]$Name, [string]$Expected) {
    try { & $Action } catch {
        if ($_.Exception.Message -notmatch $Expected) { throw "$Name failed for the wrong reason: $($_.Exception.Message)" }
        return
    }
    throw "$Name unexpectedly succeeded."
}
$exe = Join-Path $candidate 'GraphReader.App.exe'
$dll = Join-Path $candidate 'GraphReader.App.dll'
Write-Text $exe 'synthetic app host'
Write-Text $dll 'synthetic app assembly'
Write-Text (Join-Path $evidence 'bin\graphreader_pdfium_renderer.exe') 'synthetic renderer'
Write-Text (Join-Path $evidence 'source-lock.json') 'synthetic lock'
Write-Text (Join-Path $evidence 'build-manifest.json') 'synthetic manifest'
Write-Text (Join-Path $evidence 'notice.txt') "REVIEW STATUS: COMPLETE`nSynthetic notice."
$approval = [ordered]@{
    schemaVersion = 1; rendererId = 'graphreader-pdfium-renderer'; rendererVersion = '2870fa9244b0f0f69fb743fab1e08deefcb07b2b'
    source = 'https://pdfium.googlesource.com/pdfium'; sourceRevision = '2870fa9244b0f0f69fb743fab1e08deefcb07b2b'; licenseSpdx = 'BSD-3-Clause'
    reviewApproved = $true; redistributionApproved = $true; bundlingApproved = $true
    binaryPath = 'bin/graphreader_pdfium_renderer.exe'; binarySha256 = Hash (Join-Path $evidence 'bin\graphreader_pdfium_renderer.exe')
    sourceLockPath = 'source-lock.json'; sourceLockSha256 = Hash (Join-Path $evidence 'source-lock.json')
    buildManifestPath = 'build-manifest.json'; buildManifestSha256 = Hash (Join-Path $evidence 'build-manifest.json')
    noticePath = 'notice.txt'; noticeSha256 = Hash (Join-Path $evidence 'notice.txt')
}
$approvalPath = Join-Path $evidence 'reviewed-approval.json'
Write-Text $approvalPath ($approval | ConvertTo-Json)
# Exercise the real script logic against synthetic bytes by replacing only its
# independently pinned approval digest in an isolated fixture copy. The live
# script itself remains pinned to the reviewed production approval.
$sourceText = Get-Content -LiteralPath $script -Raw
$realPin = '40f47b14c42748b8fb43d0905d1694746186d6b9018d5bce4c97a93305836936'
if (-not $sourceText.Contains($realPin)) { throw 'The live reviewed approval pin is missing.' }
$fixtureScriptDirectory = Join-Path $repo 'packaging\pdfium-source'
New-Item -ItemType Directory -Path $fixtureScriptDirectory -Force | Out-Null
$fixtureScript = Join-Path $fixtureScriptDirectory 'Stage-CleanMachineCandidate.ps1'
Write-Text $fixtureScript ($sourceText.Replace($realPin, (Hash $approvalPath)))
$script = $fixtureScript
function Set-FixtureApprovalPin { Write-Text $fixtureScript ($sourceText.Replace($realPin, (Hash $approvalPath))) }
$auditPath = Join-Path $repo 'packaging\common\release-audit.json'
$audit = @{ components = @(@{ id = 'pdfium-native'; reviewStatus = 'reviewed'; checksumPolicy = 'exact-binary'; artifactSha256 = $approval.binarySha256 }); mandatoryEvidenceGates = @(@{ id = 'pdfium-clean-machine-load'; status = 'blocked' }) }
Write-Text $auditPath ($audit | ConvertTo-Json -Depth 8)
$auditHash = Hash $auditPath
$arguments = @{ CandidateRoot = $candidate; EvidenceRoot = $evidence; RepositoryRoot = $repo; ExpectedExecutableSha256 = (Hash $exe); ExpectedApplicationDllSha256 = (Hash $dll) }
# Exercise the real script with powershell.exe -File and no RepositoryRoot.
# Synthetic approval must fail its real pin, after default root initialization.
$oldPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    $actualOutput = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $originalScript `
        -CandidateRoot $candidate -EvidenceRoot $evidence `
        -ExpectedExecutableSha256 (Hash $exe) -ExpectedApplicationDllSha256 (Hash $dll) 2>&1 | Out-String)
    $actualExit = $LASTEXITCODE
} finally { $ErrorActionPreference = $oldPreference }
if ($actualExit -eq 0 -or $actualOutput -notmatch 'independently pinned approval') {
    throw "Real -File default root did not reach the approval guard: $actualOutput"
}
# Also verify the complete success path through a fixture copy with a matching
# synthetic pin, using -File and the default inferred fixture repository root.
$defaultCandidate = Join-Path $repo 'artifacts\default-candidate'
New-Item -ItemType Directory -Path $defaultCandidate | Out-Null
Copy-Item $exe (Join-Path $defaultCandidate 'GraphReader.App.exe')
Copy-Item $dll (Join-Path $defaultCandidate 'GraphReader.App.dll')
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $fixtureScript `
    -CandidateRoot $defaultCandidate -EvidenceRoot $evidence `
    -ExpectedExecutableSha256 (Hash $exe) -ExpectedApplicationDllSha256 (Hash $dll)
if ($LASTEXITCODE -ne 0 -or -not (Test-Path (Join-Path $defaultCandidate 'pdfium-clean-machine-candidate.json'))) {
    throw 'Fixture -File default-root success failed.'
}
$wrongHashArguments = $arguments.Clone()
$wrongHashArguments.ExpectedExecutableSha256 = '0' * 64
Must-Fail { & $script @wrongHashArguments } 'Application hash mismatch' 'Candidate application bytes'
$overlapArguments = $arguments.Clone()
$overlapArguments.CandidateRoot = $evidence
Must-Fail { & $script @overlapArguments } 'Overlapping directories' 'must be disjoint'
if (Test-Path (Join-Path $candidate 'pdfium')) { throw 'Mismatch wrote candidate files.' }
$preview = Join-Path $repo 'artifacts\preview'
New-Item -ItemType Directory -Path $preview | Out-Null
Copy-Item $exe (Join-Path $preview 'GraphReader.App.exe')
Copy-Item $dll (Join-Path $preview 'GraphReader.App.dll')
Write-Text (Join-Path $preview 'portable.mode') ''
$previewArguments = $arguments.Clone()
$previewArguments.CandidateRoot = $preview
Must-Fail { & $script @previewArguments } 'Preview sentinel' 'portable preview payload'
$originalApproval = Get-Content -LiteralPath $approvalPath -Raw
Write-Text (Join-Path $evidence 'notice.txt') "REVIEW STATUS: COMPLETE`nTampered notice."
$approval.noticeSha256 = Hash (Join-Path $evidence 'notice.txt')
Write-Text $approvalPath ($approval | ConvertTo-Json)
Must-Fail { & $script @arguments } 'Self-consistent tampered approval' 'independently pinned approval'
Write-Text (Join-Path $evidence 'notice.txt') "REVIEW STATUS: COMPLETE`nSynthetic notice."
Write-Text $approvalPath $originalApproval
$approval.reviewApproved = 'true'
Write-Text $approvalPath ($approval | ConvertTo-Json)
Set-FixtureApprovalPin
Must-Fail { & $script @arguments } 'Non-boolean approval' 'Approval field reviewApproved'
$approval.reviewApproved = $true
$approval.noticeSha256 = Hash (Join-Path $evidence 'notice.txt')
$approval.noticePath = '../outside.txt'
Write-Text $approvalPath ($approval | ConvertTo-Json)
Set-FixtureApprovalPin
Must-Fail { & $script @arguments } 'Resource traversal' 'Invalid approved PDFium resource path'
$approval.noticePath = 'notice.txt'
Write-Text $approvalPath $originalApproval
Set-FixtureApprovalPin
Write-Text (Join-Path $evidence 'notice.txt') 'tampered'
Must-Fail { & $script @arguments } 'Tampered resource' 'resource missing or changed'
Write-Text (Join-Path $evidence 'notice.txt') "REVIEW STATUS: COMPLETE`nSynthetic notice."
& $script @arguments
$receipt = Get-Content -LiteralPath (Join-Path $candidate 'pdfium-clean-machine-candidate.json') -Raw | ConvertFrom-Json
if ($receipt.status -cne 'diagnostic-only' -or $receipt.releaseApproved -ne $false -or $receipt.cleanMachineEvidence -ne $false) { throw 'Candidate receipt overclaims.' }
if (@(Get-ChildItem (Join-Path $candidate 'pdfium') -File -Recurse).Count -ne 5) { throw 'Candidate does not contain exactly the five reviewed files.' }
if ((Hash $auditPath) -cne $auditHash) { throw 'Release audit changed.' }
Must-Fail { & $script @arguments } 'Overwrite' 'must not already exist'
Write-Host 'PDFium clean-machine candidate synthetic tests passed.'
