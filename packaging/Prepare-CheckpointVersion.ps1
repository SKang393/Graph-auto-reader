# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$CheckHead,
    [switch]$PrepareNext,
    [switch]$PromoteStable
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'VersionPolicy.ps1')

$selectedModeCount = @(
    $CheckHead.IsPresent,
    $PrepareNext.IsPresent,
    $PromoteStable.IsPresent
).Where({ $_ }).Count
if ($selectedModeCount -gt 1) {
    throw '-CheckHead, -PrepareNext, and -PromoteStable are mutually exclusive.'
}
if ($selectedModeCount -eq 0) {
    $PrepareNext = $true
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}
else {
    $RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
}

$propsPath = Join-Path $RepositoryRoot 'Directory.Build.props'
$workingVersion = Get-GraphReaderCentralVersion -Path $propsPath

function Invoke-GitText {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & git -C $RepositoryRoot @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }
    return ($output -join [Environment]::NewLine).Trim()
}

function Get-CommittedVersion {
    param([Parameter(Mandatory)][string]$Revision)

    $content = Invoke-GitText -Arguments @('show', ('{0}:Directory.Build.props' -f $Revision))
    if ([string]::IsNullOrWhiteSpace($content)) {
        throw "Directory.Build.props is unavailable at Git revision '$Revision'."
    }
    return Get-GraphReaderVersionFromProjectXml -Content $content -Description "$Revision`:Directory.Build.props"
}

function Set-VersionFields {
    param([Parameter(Mandatory)][string]$Version)

    $content = Get-Content -LiteralPath $propsPath -Raw
    $replacements = [ordered]@{
        Version = $Version
        AssemblyVersion = "$Version.0"
        FileVersion = "$Version.0"
        InformationalVersion = $Version
    }
    foreach ($entry in $replacements.GetEnumerator()) {
        $pattern = '<{0}>[^<]*</{0}>' -f [regex]::Escape([string]$entry.Key)
        $matches = [regex]::Matches($content, $pattern)
        if ($matches.Count -ne 1) {
            throw "Expected exactly one $($entry.Key) element in $propsPath."
        }
        $replacement = '<{0}>{1}</{0}>' -f $entry.Key, $entry.Value
        $content = [regex]::Replace($content, $pattern, $replacement)
    }

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($propsPath, $content, $encoding)
}

function Get-CheckpointLedgerState {
    $ledgerPath = Join-Path $RepositoryRoot 'docs\BUILD_LEDGER.json'
    if (-not (Test-Path -LiteralPath $ledgerPath -PathType Leaf)) {
        return $null
    }

    try {
        $ledger = Get-Content -LiteralPath $ledgerPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "The checkpoint build ledger is not valid JSON: $ledgerPath."
    }

    if ($ledger.schemaVersion -ne 1) {
        throw "The checkpoint build ledger schemaVersion must be 1: $ledgerPath."
    }

    $builds = @($ledger.builds)
    if ($builds.Count -eq 0) {
        throw "The checkpoint build ledger contains no builds: $ledgerPath."
    }

    $maxBuild = $builds | Sort-Object { [int]$_.buildNumber } -Descending | Select-Object -First 1
    if ($null -eq $maxBuild.version -or $null -eq $maxBuild.buildNumber) {
        throw "The checkpoint build ledger's maximum build is incomplete: $ledgerPath."
    }

    return [pscustomobject][ordered]@{
        Path = $ledgerPath
        Count = $builds.Count
        MaxBuildNumber = [int]$maxBuild.buildNumber
        MaxVersion = ConvertTo-GraphReaderVersion -Version ([string]$maxBuild.version)
    }
}

if ($CheckHead.IsPresent) {
    $ledger = Get-CheckpointLedgerState
    if ($null -ne $ledger) {
        $ledgerVersion = $ledger.MaxVersion.Value
        $ledgerSuccessor = Get-NextGraphReaderVersion -Version $ledgerVersion
        $committedVersionForCheck = Get-CommittedVersion -Revision 'HEAD'
        $stablePromotion = Test-GraphReaderStablePromotion `
            -FromVersion $ledgerVersion `
            -ToVersion $workingVersion.Value
        $validTransition = $workingVersion.Value -ceq $ledgerVersion -or
            $workingVersion.Value -ceq $ledgerSuccessor -or
            $stablePromotion
        $expected = "ledger max '$ledgerVersion' or successor '$ledgerSuccessor'"
        $hasParent = $true
    }
    else {
        $parent = Invoke-GitText -Arguments @('rev-parse', 'HEAD^')
        $hasParent = -not [string]::IsNullOrWhiteSpace($parent)
        if ([string]::IsNullOrWhiteSpace($parent)) {
            $expected = '0.0.1'
            $validTransition = $workingVersion.Value -eq $expected
            $stablePromotion = $false
        }
        else {
            $parentVersion = Get-CommittedVersion -Revision $parent
            $expected = Get-NextGraphReaderVersion -Version $parentVersion.Value
            $stablePromotion = Test-GraphReaderStablePromotion `
                -FromVersion $parentVersion.Value `
                -ToVersion $workingVersion.Value
            $validTransition = $workingVersion.Value -eq $expected -or $stablePromotion
        }
    }

    if (-not $validTransition) {
        $promotionHint = if (-not $hasParent) {
            ''
        }
        else {
            " The only nonsequential transition is an explicit pre-2.0 promotion to '$(Get-GraphReaderStablePromotionVersion)'."
        }
        $sourceHint = if ($null -ne $ledger) { ' from the checkpoint ledger.' } else { ' from its first parent.' }
        throw "Checkpoint version '$($workingVersion.Value)' is invalid for HEAD. Expected '$expected'$sourceHint$promotionHint Run packaging/Prepare-CheckpointVersion.ps1 before committing."
    }

    $transitionName = if ($stablePromotion) { 'stable promotion' } else { 'sequential checkpoint' }
    Write-Host "Checkpoint version verified: $($workingVersion.Value) ($transitionName)"
    exit 0
}

$committedVersion = Get-CommittedVersion -Revision 'HEAD'
$stableVersion = Get-GraphReaderStablePromotionVersion
$ledger = Get-CheckpointLedgerState

if ($PromoteStable.IsPresent) {
    if (-not (Test-GraphReaderStablePromotion -FromVersion $committedVersion.Value -ToVersion $stableVersion)) {
        throw "Stable promotion is permitted only from a committed version below '$stableVersion' to '$stableVersion'. Current committed version: '$($committedVersion.Value)'."
    }
    if ($workingVersion.Value -eq $stableVersion) {
        Write-Host "Stable promotion version already prepared: $($committedVersion.Value) -> $stableVersion"
        exit 0
    }
    if ($workingVersion.Value -ne $committedVersion.Value) {
        throw "Working version '$($workingVersion.Value)' is neither committed version '$($committedVersion.Value)' nor stable promotion version '$stableVersion'."
    }

    Set-VersionFields -Version $stableVersion
    Write-Host "Prepared stable promotion: $($committedVersion.Value) -> $stableVersion"
    exit 0
}

if ($null -eq $ledger) {
    $nextVersion = Get-NextGraphReaderVersion -Version $committedVersion.Value
    if ($workingVersion.Value -eq $nextVersion) {
        Write-Host "Checkpoint version already prepared: $nextVersion"
        exit 0
    }
    if ($workingVersion.Value -ne $committedVersion.Value) {
        throw "Working version '$($workingVersion.Value)' is neither committed version '$($committedVersion.Value)' nor its successor '$nextVersion'."
    }
    $preparedVersion = $nextVersion
}
else {
    $ledgerVersion = $ledger.MaxVersion.Value
    $nextVersion = Get-NextGraphReaderVersion -Version $ledgerVersion
    if ($workingVersion.Value -eq $nextVersion) {
        Write-Host "Checkpoint version already prepared: $nextVersion"
        exit 0
    }
    if ($workingVersion.Value -ne $ledgerVersion) {
        throw "Working version '$($workingVersion.Value)' is neither ledger max '$ledgerVersion' nor its successor '$nextVersion'."
    }
    $preparedVersion = $nextVersion
}

Set-VersionFields -Version $preparedVersion
Write-Host "Prepared checkpoint version: $($workingVersion.Value) -> $preparedVersion"
