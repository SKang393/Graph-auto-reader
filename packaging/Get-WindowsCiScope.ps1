# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param(
    [switch]$FunctionsOnly,
    [string]$RepositoryRoot = $env:GITHUB_WORKSPACE,
    [string]$HeadSha = $env:GITHUB_SHA,
    [string]$EventName = $env:GITHUB_EVENT_NAME,
    [string]$Ref = $env:GITHUB_REF,
    [string]$BeforeSha = $env:GITHUB_EVENT_BEFORE,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$ApiToken = $env:GITHUB_TOKEN,
    [string]$GitHubOutput = $env:GITHUB_OUTPUT
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-WindowsCiScopeDecision {
    param(
        [Parameter(Mandatory)][bool]$FullProductValidation,
        [Parameter(Mandatory)][string]$Reason,
        [string]$ParentCiRunId
    )
    [pscustomobject][ordered]@{
        FullProductValidation = $FullProductValidation
        Reason = $Reason
        ParentCiRunId = $ParentCiRunId
    }
}

function Test-WindowsCiFullSha {
    param([AllowNull()][string]$Value)
    return $null -ne $Value -and $Value -cmatch '^[0-9a-fA-F]{40}$'
}

function Test-WindowsCiPermittedPath {
    param([Parameter(Mandatory)][string]$Path)
    if ([string]::IsNullOrEmpty($Path) -or $Path.Contains('\') -or $Path.StartsWith('/', [StringComparison]::Ordinal)) {
        return $false
    }
    $segments = $Path.Split('/')
    if ($segments.Count -lt 2 -or $segments -contains '' -or $segments -contains '.' -or $segments -contains '..') {
        return $false
    }
    if ($Path -ceq 'docs/BUILD_LEDGER.json') { return $true }
    return $Path -cmatch '^docs/(?:[^/]+/)*[^/]+\.md$'
}

function Get-WindowsCiTrustedParentRunId {
    param(
        [AllowNull()][object[]]$Runs,
        [Parameter(Mandatory)][string]$ParentSha,
        [Parameter(Mandatory)][string]$Repository
    )
    if ($null -eq $Runs) { return $null }
    foreach ($run in $Runs) {
        if ($null -eq $run) { continue }
        $properties = $run.PSObject.Properties
        $required = @('id', 'path', 'head_sha', 'event', 'head_branch', 'status', 'conclusion', 'head_repository')
        if ($required.Where({ $null -eq $properties[$_] }).Count -ne 0) { continue }
        $headRepository = $run.head_repository
        if ($null -eq $headRepository -or $null -eq $headRepository.PSObject.Properties['full_name']) { continue }
        $runId = 0L
        if (-not [long]::TryParse([string]$run.id, [ref]$runId) -or $runId -le 0) { continue }
        if ($run.path -cne '.github/workflows/windows-ci.yml' -or
            $run.head_sha -cne $ParentSha -or
            $run.event -cne 'push' -or
            $run.head_branch -cne 'main' -or
            $run.status -cne 'completed' -or
            $run.conclusion -cne 'success' -or
            $headRepository.full_name -cne $Repository) { continue }
        return [string]$runId
    }
    return $null
}

function Get-WindowsCiScopeDecision {
    param(
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$EventName,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$Ref,
        [AllowNull()][AllowEmptyString()][string]$BeforeSha,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$HeadSha,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$CurrentHeadSha,
        [Parameter(Mandatory)][bool]$DiffAvailable,
        [AllowNull()][object[]]$ChangedRecords,
        [Parameter(Mandatory)][bool]$ParentLookupSucceeded,
        [AllowNull()][object[]]$ParentRuns,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$Repository
    )
    if ($EventName -cne 'push' -or $Ref -cne 'refs/heads/main') {
        return New-WindowsCiScopeDecision $true 'full_event'
    }
    if (-not (Test-WindowsCiFullSha $HeadSha) -or $HeadSha -cne $CurrentHeadSha) {
        return New-WindowsCiScopeDecision $true 'full_head_unverified'
    }
    if (-not (Test-WindowsCiFullSha $BeforeSha) -or $BeforeSha -match '^0+$') {
        return New-WindowsCiScopeDecision $true 'full_before_unverified'
    }
    if (-not $DiffAvailable -or $null -eq $ChangedRecords) {
        return New-WindowsCiScopeDecision $true 'full_diff_unavailable'
    }
    if ($ChangedRecords.Count -eq 0) {
        return New-WindowsCiScopeDecision $true 'full_empty_diff'
    }
    foreach ($record in $ChangedRecords) {
        if ($null -eq $record -or $null -eq $record.PSObject.Properties['Status'] -or
            $null -eq $record.PSObject.Properties['Path']) {
            return New-WindowsCiScopeDecision $true 'full_diff_malformed'
        }
        # Deletions, type changes, copies and renames always take the full path.
        if ($record.Status -cne 'A' -and $record.Status -cne 'M') {
            return New-WindowsCiScopeDecision $true 'full_path_status'
        }
        if (-not (Test-WindowsCiPermittedPath ([string]$record.Path))) {
            return New-WindowsCiScopeDecision $true 'full_changed_path'
        }
    }
    if (-not $ParentLookupSucceeded) {
        return New-WindowsCiScopeDecision $true 'full_parent_ci_unavailable'
    }
    $parentRunId = Get-WindowsCiTrustedParentRunId -Runs $ParentRuns -ParentSha $BeforeSha -Repository $Repository
    if ($null -eq $parentRunId) {
        return New-WindowsCiScopeDecision $true 'full_parent_ci_unverified'
    }
    return New-WindowsCiScopeDecision $false 'docs_only_verified_parent' $parentRunId
}

function ConvertFrom-WindowsCiGitStatusBytes {
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes)
    if ($Bytes.Length -eq 0) {
        return [pscustomobject]@{ Valid = $true; Records = [object[]]@() }
    }
    if ($Bytes[-1] -ne 0) {
        return [pscustomobject]@{ Valid = $false; Records = $null }
    }
    try {
        $encoding = [System.Text.UTF8Encoding]::new($false, $true)
        $text = $encoding.GetString($Bytes)
    }
    catch [System.Text.DecoderFallbackException] {
        return [pscustomobject]@{ Valid = $false; Records = $null }
    }
    $parts = $text.Split([char]0)
    if ($parts.Count -lt 3 -or $parts[-1] -cne '' -or (($parts.Count - 1) % 2) -ne 0) {
        return [pscustomobject]@{ Valid = $false; Records = $null }
    }
    $records = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt ($parts.Count - 1); $index += 2) {
        $status = $parts[$index]
        $path = $parts[$index + 1]
        if ($status -notmatch '^[A-Z][0-9]*$' -or [string]::IsNullOrEmpty($path)) {
            return [pscustomobject]@{ Valid = $false; Records = $null }
        }
        $records.Add([pscustomobject]@{ Status = $status; Path = $path })
    }
    return [pscustomobject]@{ Valid = $true; Records = $records.ToArray() }
}

function Invoke-WindowsCiGitBytes {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string[]]$Arguments,
        [int]$TimeoutMilliseconds = 15000,
        [int]$MaximumOutputBytes = 2MB
    )
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('-C')
    $startInfo.ArgumentList.Add($RepositoryRoot)
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $memory = [System.IO.MemoryStream]::new()
    try {
        if (-not $process.Start()) { return [pscustomobject]@{ Success = $false; Bytes = [byte[]]@() } }
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $buffer = [byte[]]::new(8192)
        while ($true) {
            $readTask = $process.StandardOutput.BaseStream.ReadAsync($buffer, 0, $buffer.Length)
            if (-not $readTask.Wait($TimeoutMilliseconds)) {
                try { $process.Kill($true) } catch { }
                return [pscustomobject]@{ Success = $false; Bytes = [byte[]]@() }
            }
            $read = $readTask.Result
            if ($read -eq 0) { break }
            if ($memory.Length + $read -gt $MaximumOutputBytes) {
                try { $process.Kill($true) } catch { }
                return [pscustomobject]@{ Success = $false; Bytes = [byte[]]@() }
            }
            $memory.Write($buffer, 0, $read)
        }
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            try { $process.Kill($true) } catch { }
            return [pscustomobject]@{ Success = $false; Bytes = [byte[]]@() }
        }
        $null = $stderrTask.GetAwaiter().GetResult()
        return [pscustomobject]@{ Success = ($process.ExitCode -eq 0); Bytes = $memory.ToArray() }
    }
    catch {
        if (-not $process.HasExited) { try { $process.Kill($true) } catch { } }
        return [pscustomobject]@{ Success = $false; Bytes = [byte[]]@() }
    }
    finally {
        $memory.Dispose()
        $process.Dispose()
    }
}

function Get-WindowsCiParentRuns {
    param(
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$Repository,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$ParentSha,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$ApiToken
    )
    if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
        -not (Test-WindowsCiFullSha $ParentSha) -or [string]::IsNullOrWhiteSpace($ApiToken)) {
        throw 'Parent CI lookup inputs are unavailable.'
    }
    $encodedSha = [Uri]::EscapeDataString($ParentSha)
    $uri = "https://api.github.com/repos/$Repository/actions/workflows/windows-ci.yml/runs?head_sha=$encodedSha&branch=main&event=push&per_page=100"
    $headers = @{
        Accept = 'application/vnd.github+json'
        Authorization = "Bearer $ApiToken"
        'X-GitHub-Api-Version' = '2022-11-28'
        'User-Agent' = 'GraphAutoReader-Windows-CI-Scope'
    }
    $response = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers -TimeoutSec 10
    if ($null -eq $response -or $null -eq $response.PSObject.Properties['workflow_runs'] -or
        $response.workflow_runs -isnot [System.Collections.IEnumerable]) {
        throw 'Parent CI response is malformed.'
    }
    return ,@($response.workflow_runs)
}

function Write-WindowsCiScopeOutput {
    param(
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$OutputPath,
        [Parameter(Mandatory)][psobject]$Decision
    )
    if ([string]::IsNullOrWhiteSpace($OutputPath)) { throw 'GITHUB_OUTPUT is not available.' }
    $full = if ($Decision.FullProductValidation) { 'true' } else { 'false' }
    $parentRun = if ($null -ne $Decision.ParentCiRunId) { $Decision.ParentCiRunId } else { '' }
    $text = "full_product_validation=$full`nscope_reason=$($Decision.Reason)`nparent_ci_run_id=$parentRun`n"
    [System.IO.File]::AppendAllText($OutputPath, $text, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-WindowsCiScopeClassifier {
    param(
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$RepositoryRoot,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$HeadSha,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$EventName,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$Ref,
        [AllowNull()][AllowEmptyString()][string]$BeforeSha,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$Repository,
        [AllowNull()][AllowEmptyString()][string]$ApiToken,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][string]$GitHubOutput
    )
    $decision = New-WindowsCiScopeDecision $true 'full_classifier_error'
    try {
        if ($EventName -cne 'push' -or $Ref -cne 'refs/heads/main') {
            $decision = New-WindowsCiScopeDecision $true 'full_event'
        }
        elseif (-not (Test-WindowsCiFullSha $HeadSha) -or -not (Test-WindowsCiFullSha $BeforeSha) -or $BeforeSha -match '^0+$') {
            $decision = New-WindowsCiScopeDecision $true 'full_before_unverified'
        }
        else {
            $headResult = Invoke-WindowsCiGitBytes -RepositoryRoot $RepositoryRoot -Arguments @('rev-parse', '--verify', 'HEAD^{commit}') -MaximumOutputBytes 256
            if (-not $headResult.Success) {
                $decision = New-WindowsCiScopeDecision $true 'full_head_unverified'
            }
            else {
                $headText = [System.Text.UTF8Encoding]::new($false, $true).GetString($headResult.Bytes).Trim()
                if ($headText -cne $HeadSha) {
                    $decision = New-WindowsCiScopeDecision $true 'full_head_unverified'
                }
                else {
                    $diffResult = Invoke-WindowsCiGitBytes -RepositoryRoot $RepositoryRoot -Arguments @(
                        'diff', '--name-status', '--no-renames', '-z', $BeforeSha, $HeadSha, '--')
                    $parsed = if ($diffResult.Success) { ConvertFrom-WindowsCiGitStatusBytes -Bytes $diffResult.Bytes } else { $null }
                    if ($null -eq $parsed -or -not $parsed.Valid) {
                        $decision = New-WindowsCiScopeDecision $true 'full_diff_unavailable'
                    }
                    else {
                        $decision = Get-WindowsCiScopeDecision -EventName $EventName -Ref $Ref -BeforeSha $BeforeSha -HeadSha $HeadSha `
                            -CurrentHeadSha $headText -DiffAvailable $true -ChangedRecords $parsed.Records `
                            -ParentLookupSucceeded $false -ParentRuns @() -Repository $Repository
                        if ($decision.Reason -eq 'full_parent_ci_unavailable') {
                            try {
                                $parentRuns = Get-WindowsCiParentRuns -Repository $Repository -ParentSha $BeforeSha -ApiToken ([string]$ApiToken)
                                $decision = Get-WindowsCiScopeDecision -EventName $EventName -Ref $Ref -BeforeSha $BeforeSha -HeadSha $HeadSha `
                                    -CurrentHeadSha $headText -DiffAvailable $true -ChangedRecords $parsed.Records `
                                    -ParentLookupSucceeded $true -ParentRuns $parentRuns -Repository $Repository
                            }
                            catch {
                                $decision = New-WindowsCiScopeDecision $true 'full_parent_ci_unavailable'
                            }
                        }
                    }
                }
            }
        }
    }
    catch {
        $decision = New-WindowsCiScopeDecision $true 'full_classifier_error'
    }
    Write-WindowsCiScopeOutput -OutputPath $GitHubOutput -Decision $decision
    return $decision
}

if (-not $FunctionsOnly) {
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = (Get-Location).Path }
    $RepositoryRoot = [System.IO.Path]::GetFullPath($RepositoryRoot)
    $null = Invoke-WindowsCiScopeClassifier -RepositoryRoot $RepositoryRoot -HeadSha ([string]$HeadSha) `
        -EventName ([string]$EventName) -Ref ([string]$Ref) -BeforeSha $BeforeSha -Repository ([string]$Repository) `
        -ApiToken $ApiToken -GitHubOutput ([string]$GitHubOutput)
}
