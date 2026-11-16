# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$testRepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $testRepoRoot 'packaging\Get-WindowsCiScope.ps1') -FunctionsOnly

$passed = 0
$head = 'a' * 40
$before = 'b' * 40
$repository = 'owner/repository'
$goodRun = [pscustomobject]@{
    id = 45123
    path = '.github/workflows/windows-ci.yml'
    head_sha = $before
    event = 'push'
    head_branch = 'main'
    status = 'completed'
    conclusion = 'success'
    head_repository = [pscustomobject]@{ full_name = $repository }
}
$docRecords = [object[]]@(
    [pscustomobject]@{ Status = 'M'; Path = 'docs/INTERNAL-PREVIEW-458.md' },
    [pscustomobject]@{ Status = 'A'; Path = 'docs/BUILD_LEDGER.json' }
)

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param([Parameter(Mandatory)][object]$Actual, [Parameter(Mandatory)][object]$Expected, [Parameter(Mandatory)][string]$Message)
    if ($Actual -ne $Expected) { throw "$Message Expected '$Expected', found '$Actual'." }
}

function Get-Decision {
    param(
        [string]$Event = 'push',
        [string]$TargetRef = 'refs/heads/main',
        [string]$Parent = $before,
        [string]$CurrentHead = $head,
        [bool]$DiffAvailable = $true,
        [object[]]$Records = $docRecords,
        [bool]$LookupSucceeded = $true,
        [object[]]$Runs = @($goodRun)
    )
    Get-WindowsCiScopeDecision -EventName $Event -Ref $TargetRef -BeforeSha $Parent -HeadSha $head `
        -CurrentHeadSha $CurrentHead -DiffAvailable $DiffAvailable -ChangedRecords $Records `
        -ParentLookupSucceeded $LookupSucceeded -ParentRuns $Runs -Repository $repository
}

function Assert-Full {
    param([Parameter(Mandatory)][object]$Decision, [Parameter(Mandatory)][string]$Case)
    Assert-True $Decision.FullProductValidation "$Case must run full product validation."
    if (-not [string]::IsNullOrEmpty($Decision.ParentCiRunId)) { throw "$Case must not emit a trusted parent id." }
}

$decision = Get-Decision
Assert-True (-not $decision.FullProductValidation) 'Verified docs-only push should skip product validation.'
Assert-Equal $decision.Reason 'docs_only_verified_parent' 'Verified docs-only reason.'
Assert-Equal $decision.ParentCiRunId '45123' 'Verified exact parent run id.'
$passed++

$nestedDocs = Get-Decision -Records ([object[]]@([pscustomobject]@{ Status = 'M'; Path = 'docs/records/outcome.md' }))
Assert-True (-not $nestedDocs.FullProductValidation) 'Nested Markdown outcome should be eligible.'
$passed++

$probeReadmeRecords = [object[]]@([pscustomobject]@{ Status = 'M'; Path = 'tools/GraphReader.CalibrationOcrProbe/README.md' })
$probeDocs = Get-Decision -Records ([object[]]($docRecords + $probeReadmeRecords))
Assert-True (-not $probeDocs.FullProductValidation) 'Probe usage documentation with other outcomes should reuse a verified parent.'
Assert-Equal $probeDocs.Reason 'docs_only_verified_parent' 'Probe documentation reuse reason.'
Assert-Equal $probeDocs.ParentCiRunId '45123' 'Probe documentation must retain the exact trusted parent.'
$passed++
Assert-Full (Get-Decision -Records $probeReadmeRecords -Runs ([object[]]@())) 'probe README without verified parent'
Assert-Full (Get-Decision -Records ([object[]]@([pscustomobject]@{ Status = 'D'; Path = 'tools/GraphReader.CalibrationOcrProbe/README.md' }))) 'probe README deletion'
Assert-Full (Get-Decision -Records ([object[]]@([pscustomobject]@{ Status = 'R100'; Path = 'tools/GraphReader.CalibrationOcrProbe/README.md' }))) 'probe README rename'
$passed += 3
foreach ($path in @(
    'tools/GraphReader.CalibrationOcrProbe/Program.cs',
    'tools/GraphReader.CalibrationOcrProbe/GraphReader.CalibrationOcrProbe.csproj',
    'tools/GraphReader.CalibrationOcrProbe/README.md.bak',
    'tools/GraphReader.CalibrationOcrProbe/README.MD',
    'tools/GraphReader.Other/README.md',
    'tools/GraphReader.CalibrationOcrProbe/../README.md'
)) {
    $mixed = [object[]]($probeReadmeRecords + @([pscustomobject]@{ Status = 'M'; Path = $path }))
    Assert-Full (Get-Decision -Records $mixed) "probe README mixed with $path"
    $passed++
}

foreach ($case in @(
    @{ Name = 'pull request'; Event = 'pull_request'; Ref = 'refs/pull/8/merge' },
    @{ Name = 'tag'; Event = 'push'; Ref = 'refs/tags/v0.4.58' },
    @{ Name = 'manual dispatch'; Event = 'workflow_dispatch'; Ref = 'refs/heads/main' }
)) {
    Assert-Full (Get-Decision -Event $case.Event -TargetRef $case.Ref) $case.Name
    $passed++
}

Assert-Full (Get-Decision -CurrentHead ('c' * 40)) 'current HEAD mismatch'
Assert-Full (Get-Decision -Parent ('0' * 40)) 'zero before SHA'
Assert-Full (Get-Decision -Parent 'bad-sha') 'malformed before SHA'
Assert-Full (Get-Decision -DiffAvailable $false) 'diff command error'
Assert-Full (Get-Decision -Records ([object[]]@())) 'empty diff'
Assert-Full (Get-Decision -Records ([object[]]@([pscustomobject]@{ Status = 'M'; Path = 'src/GraphReader.Axis/Axis.cs' }))) 'source change'
Assert-Full (Get-Decision -Records ([object[]]@([pscustomobject]@{ Status = 'M'; Path = '.github/workflows/windows-ci.yml' }))) 'workflow change'
Assert-Full (Get-Decision -Records ([object[]]@([pscustomobject]@{ Status = 'M'; Path = 'docs/BUILD_LEDGER.json.bak' }))) 'ledger backup file'
Assert-Full (Get-Decision -Records ([object[]]@([pscustomobject]@{ Status = 'M'; Path = 'docs/../src/Axis.md' }))) 'path traversal'
Assert-Full (Get-Decision -Records ([object[]]@([pscustomobject]@{ Status = 'M'; Path = 'docs/OUTCOME.MD' }))) 'case-mismatched extension'
Assert-Full (Get-Decision -Records ([object[]]@([pscustomobject]@{ Status = 'D'; Path = 'docs/old-outcome.md' }))) 'deletion'
Assert-Full (Get-Decision -Records ([object[]]@([pscustomobject]@{ Status = 'R100'; Path = 'docs/new-outcome.md' }))) 'rename status'
$passed += 12

foreach ($runCase in @(
    @{ Name = 'missing parent run' },
    @{ Name = 'cancelled parent run' },
    @{ Name = 'pending parent run' },
    @{ Name = 'failed parent run' },
    @{ Name = 'wrong parent SHA' },
    @{ Name = 'wrong workflow' },
    @{ Name = 'wrong event' },
    @{ Name = 'wrong branch' },
    @{ Name = 'wrong repository' }
)) {
    $run = [pscustomobject]@{
        id = 45123
        path = '.github/workflows/windows-ci.yml'
        head_sha = $before
        event = 'push'
        head_branch = 'main'
        status = 'completed'
        conclusion = 'success'
        head_repository = [pscustomobject]@{ full_name = $repository }
    }
    switch ($runCase.Name) {
        'missing parent run' { $run = $null }
        'cancelled parent run' { $run.status = 'completed'; $run.conclusion = 'cancelled' }
        'pending parent run' { $run.status = 'in_progress'; $run.conclusion = $null }
        'failed parent run' { $run.status = 'completed'; $run.conclusion = 'failure' }
        'wrong parent SHA' { $run.head_sha = 'c' * 40 }
        'wrong workflow' { $run.path = '.github/workflows/other.yml' }
        'wrong event' { $run.event = 'workflow_dispatch' }
        'wrong branch' { $run.head_branch = 'feature' }
        'wrong repository' { $run.head_repository.full_name = 'fork/repository' }
    }
    $runs = if ($null -eq $run) { [object[]]@() } else { [object[]]@($run) }
    Assert-Full (Get-Decision -Runs $runs) $runCase.Name
    $passed++
}
Assert-Full (Get-Decision -LookupSucceeded $false -Runs $null) 'parent API authorization/error'
$passed++

$validGitBytes = [System.Text.Encoding]::UTF8.GetBytes("M`0docs/outcome.md`0A`0docs/BUILD_LEDGER.json`0")
$parsed = ConvertFrom-WindowsCiGitStatusBytes -Bytes $validGitBytes
Assert-True $parsed.Valid 'NUL-delimited git status should parse.'
Assert-Equal $parsed.Records.Count 2 'NUL-delimited record count.'
$passed++

$emptyParsed = ConvertFrom-WindowsCiGitStatusBytes -Bytes ([byte[]]@())
Assert-True $emptyParsed.Valid 'Empty diff should parse as available and be rejected by the scope decision.'
Assert-Full (Get-Decision -Records $emptyParsed.Records) 'empty parsed diff'
$passed++

$gitHead = Invoke-WindowsCiGitBytes -RepositoryRoot $testRepoRoot -Arguments @('rev-parse', '--verify', 'HEAD^{commit}') -MaximumOutputBytes 256
Assert-True $gitHead.Success 'Local git HEAD query should succeed.'
$gitHeadSha = [System.Text.UTF8Encoding]::new($false, $true).GetString($gitHead.Bytes).Trim()
Assert-True (Test-WindowsCiFullSha $gitHeadSha) 'Local git HEAD output should be a full SHA.'
$emptyGitDiff = Invoke-WindowsCiGitBytes -RepositoryRoot $testRepoRoot -Arguments @('diff', '--name-status', '--no-renames', '-z', 'HEAD', 'HEAD', '--')
Assert-True $emptyGitDiff.Success 'Local git diff query should succeed.'
$passed++

Assert-True (-not (ConvertFrom-WindowsCiGitStatusBytes -Bytes ([System.Text.Encoding]::UTF8.GetBytes("M`0docs/outcome.md"))).Valid) 'Missing NUL terminator must fail closed.'
Assert-True (-not (ConvertFrom-WindowsCiGitStatusBytes -Bytes ([byte[]](0xFF, 0x00))).Valid) 'Invalid UTF-8 must fail closed.'
Assert-True (-not (ConvertFrom-WindowsCiGitStatusBytes -Bytes ([System.Text.Encoding]::UTF8.GetBytes("R100`0docs/old.md`0docs/new.md`0"))).Valid) 'Rename tuple must fail closed.'
$passed += 3

$outputPath = Join-Path ([System.IO.Path]::GetTempPath()) ('windows-ci-scope-' + [Guid]::NewGuid().ToString('N') + '.txt')
try {
    $missingInputs = Invoke-WindowsCiScopeClassifier -RepositoryRoot '' -HeadSha '' -EventName '' -Ref '' `
        -BeforeSha '' -Repository '' -ApiToken '' -GitHubOutput $outputPath
    Assert-True $missingInputs.FullProductValidation 'Missing top-level event inputs must fail closed.'
    Assert-True ((Get-Content -LiteralPath $outputPath -Raw).Contains('full_product_validation=true')) 'Missing inputs must emit full validation.'
    $passed++
}
finally {
    Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
}

Write-Host "PASS $passed Windows CI scope classifier assertions"
