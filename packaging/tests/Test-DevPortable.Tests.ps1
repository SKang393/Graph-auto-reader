# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$buildScript = Join-Path $repositoryRoot 'packaging\Build-DevPortable.ps1'
$commonScript = Join-Path $repositoryRoot 'packaging\DevPortable.Common.ps1'
$watchScript = Join-Path $repositoryRoot 'packaging\Watch-DevPortable.ps1'
$ledgerGeneratorScript = Join-Path $repositoryRoot 'packaging\Generate-BuildLedger.ps1'
$launcherScript = Join-Path $repositoryRoot 'packaging\Run-Latest-DevPortable.ps1'
$launcherCommand = Join-Path $repositoryRoot 'packaging\Run-Latest-DevPortable.cmd'
$hostExecutable = (Get-Process -Id $PID).Path
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'GraphReader-DevPortableTests-' + [Guid]::NewGuid().ToString('N'))
$dirtyMarker = Join-Path $repositoryRoot ('.dev-portable-test-' + [Guid]::NewGuid().ToString('N') + '.txt')
$passed = 0

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-ExpectedFailure {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $hostExecutable @Arguments 1>$null 2>$null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -eq 0) {
        throw "Expected child PowerShell to fail: $($Arguments -join ' ')"
    }
}

function Assert-LauncherRejected {
    param([string]$Script, [string]$Marker, [string]$Reason, [string]$RepositoryRoot)
    if (Test-Path -LiteralPath $Marker) { Remove-Item -LiteralPath $Marker -Force }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $Script, '-Wait')
    if ($RepositoryRoot) { $arguments += @('-RepositoryRoot', $RepositoryRoot) }
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & $hostExecutable @arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    Assert-True ($exitCode -ne 0 -and $output.Contains($Reason)) "Launcher did not reject for '$Reason': $output"
    Assert-True (-not (Test-Path -LiteralPath $Marker)) 'Rejected launcher started its target.'
}

function Invoke-Git {
    param(
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & git -C $WorkingDirectory @Arguments 1>$null 2>$null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) {
        throw "git failed with exit code ${exitCode}: git -C $WorkingDirectory $($Arguments -join ' ')"
    }
}

try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null

    foreach ($script in @($buildScript, $commonScript, $watchScript, $ledgerGeneratorScript, $launcherScript)) {
        $tokens = $null
        $errors = $null
        [System.Management.Automation.Language.Parser]::ParseFile(
            $script,
            [ref]$tokens,
            [ref]$errors) | Out-Null
        Assert-True ($errors.Count -eq 0) "PowerShell parser errors in ${script}: $($errors -join ' | ')"
    }
    Assert-True (
        (Get-Content -LiteralPath $launcherScript -Raw).Contains(
            'artifacts\goal19-realesrgan\runtime-authorized-vcomp-14.44.35211')) `
        'Launcher default must select the exact manifest-authorized vcomp runtime profile.'

    $atomicOutputRoot = Join-Path $testRoot 'atomic metadata'
    New-Item -ItemType Directory -Path $atomicOutputRoot | Out-Null
    . $commonScript
    $atomicPath = Join-Path $atomicOutputRoot 'atomic-self-test.json'
    Write-JsonFileAtomic -Path $atomicPath -Value ([ordered]@{ generation = 1 })
    Write-JsonFileAtomic -Path $atomicPath -Value ([ordered]@{ generation = 2 })
    $atomicValue = Get-Content -LiteralPath (Join-Path $atomicOutputRoot 'atomic-self-test.json') -Raw | ConvertFrom-Json
    Assert-True ($atomicValue.generation -eq 2) 'Atomic metadata replacement did not publish the second generation.'
    Assert-True (@(Get-ChildItem -LiteralPath $atomicOutputRoot -File | Where-Object { $_.Name -match '\.(tmp|bak)$' }).Count -eq 0) `
        'Atomic metadata replacement left temporary files behind.'
    $passed++

    $requiredContentRoot = Join-Path $testRoot 'required content'
    $requiredContentOutput = Join-Path $testRoot 'required content output'
    New-Item -ItemType Directory -Path (Join-Path $requiredContentRoot 'packaging\common') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $requiredContentRoot 'LICENSES') -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $requiredContentRoot 'THIRD_PARTY_NOTICES.md'), 'Noto Sans notice')
    [System.IO.File]::WriteAllText((Join-Path $requiredContentRoot 'LICENSES\NotoSans-OFL-1.1.txt'), 'OFL-1.1')
    Write-JsonFile -Path (Join-Path $requiredContentRoot 'packaging\common\publish.json') -Value ([ordered]@{
            requiredContent = @(
                [ordered]@{ source = 'THIRD_PARTY_NOTICES.md'; target = 'THIRD_PARTY_NOTICES.md' },
                [ordered]@{ source = 'LICENSES'; target = 'LICENSES' }
            )
        })
    Copy-DevPortableRequiredContent -RepositoryRoot $requiredContentRoot -DestinationRoot $requiredContentOutput
    Assert-True (Test-Path -LiteralPath (Join-Path $requiredContentOutput 'THIRD_PARTY_NOTICES.md') -PathType Leaf) `
        'Development portable required-content copy omitted THIRD_PARTY_NOTICES.md.'
    Assert-True (Test-Path -LiteralPath (Join-Path $requiredContentOutput 'LICENSES\NotoSans-OFL-1.1.txt') -PathType Leaf) `
        'Development portable required-content copy omitted the Noto Sans OFL license.'
    $passed++

    $modelRoot = Join-Path $testRoot 'approved model discovery'
    $modelManifestRoot = Join-Path $modelRoot 'models\manifest'
    $modelPayloadRoot = Join-Path $modelRoot 'models\runtime'
    New-Item -ItemType Directory -Path $modelManifestRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $modelPayloadRoot -Force | Out-Null
    $singlePayload = Join-Path $modelPayloadRoot 'single.onnx'
    $parameterPayload = Join-Path $modelPayloadRoot 'multi.param'
    $binaryPayload = Join-Path $modelPayloadRoot 'multi.bin'
    [System.IO.File]::WriteAllBytes($singlePayload, [byte[]](1, 2, 3, 4))
    [System.IO.File]::WriteAllBytes($parameterPayload, [byte[]](5, 6, 7))
    [System.IO.File]::WriteAllBytes($binaryPayload, [byte[]](8, 9, 10, 11))
    $singleHash = (Get-FileHash -LiteralPath $singlePayload -Algorithm SHA256).Hash.ToLowerInvariant()
    $parameterHash = (Get-FileHash -LiteralPath $parameterPayload -Algorithm SHA256).Hash.ToLowerInvariant()
    $binaryHash = (Get-FileHash -LiteralPath $binaryPayload -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-JsonFile -Path (Join-Path $modelManifestRoot 'approved-single.json') -Value ([ordered]@{
            model_id = 'approved-single'
            license = @{ reviewed = $true }
            commercial_use = $true
            redistribution = $true
            sha256 = $singleHash
            files = @('runtime/single.onnx')
            benchmarks = @(@{ status = 'pass'; release_eligible = $true })
        })
    Write-JsonFile -Path (Join-Path $modelManifestRoot 'approved-multi.json') -Value ([ordered]@{
            model_id = 'approved-multi'
            license = @{ reviewed = $true }
            commercial_use = $true
            redistribution = $true
            sha256 = ('0' * 64)
            files = @('runtime/multi.param', 'runtime/multi.bin')
            preprocessing = @{ model_payload_sha256 = [ordered]@{
                    'runtime/multi.param' = $parameterHash
                    'runtime/multi.bin' = $binaryHash
                } }
            benchmarks = @(@{ status = 'pass'; release_eligible = $true })
        })
    Write-JsonFile -Path (Join-Path $modelManifestRoot 'unapproved.json') -Value ([ordered]@{
            model_id = 'unapproved'
            license = @{ reviewed = $true }
            commercial_use = $true
            redistribution = $true
            sha256 = $singleHash
            files = @('runtime/single.onnx')
            benchmarks = @(@{ status = 'fail'; release_eligible = $false })
        })
    Write-JsonFile -Path (Join-Path $modelManifestRoot 'invalid-multi.json') -Value ([ordered]@{
            model_id = 'invalid-multi'
            license = @{ reviewed = $true }
            commercial_use = $true
            redistribution = $true
            sha256 = ('0' * 64)
            files = @('runtime/multi.param', 'runtime/multi.bin')
            preprocessing = @{ model_payload_sha256 = @{
                    'runtime/multi.param' = $parameterHash
                } }
            benchmarks = @(@{ status = 'pass'; release_eligible = $true })
        })
    foreach ($name in @('duplicate-a.json', 'duplicate-b.json')) {
        Write-JsonFile -Path (Join-Path $modelManifestRoot $name) -Value ([ordered]@{
                model_id = 'duplicate-approved'
                license = @{ reviewed = $true }
                commercial_use = $true
                redistribution = $true
                sha256 = $singleHash
                files = @('runtime/single.onnx')
                benchmarks = @(@{ status = 'pass'; release_eligible = $true })
            })
    }
    $modelDiagnostics = [System.Collections.Generic.List[string]]::new()
    $availableModels = @(Get-DevPortableApprovedModelIds `
            -RepositoryRoot $modelRoot `
            -Diagnostics $modelDiagnostics)
    Assert-True ($availableModels.Count -eq 2) `
        "Approved model discovery returned $($availableModels.Count) models instead of two."
    Assert-True ($availableModels -contains 'approved-single') `
        'Approved single-file model was not discovered.'
    Assert-True ($availableModels -contains 'approved-multi') `
        'Approved multi-file model was not discovered.'
    Assert-True ($availableModels -notcontains 'unapproved') `
        'A benchmark-unapproved model was reported as available.'
    Assert-True ($availableModels -notcontains 'invalid-multi') `
        'A multi-file model with an incomplete checksum map was reported as available.'
    Assert-True ($availableModels -notcontains 'duplicate-approved') `
        'A duplicated approved model ID was reported as available.'
    Assert-True ((@($modelDiagnostics) -join [Environment]::NewLine) -like '*invalid-multi.json*') `
        'Invalid multi-file model diagnostics were not retained.'
    Assert-True ((@($modelDiagnostics) -join [Environment]::NewLine) -like "*duplicate model ID 'duplicate-approved'*") `
        'Duplicated approved model ID diagnostics were not retained.'
    $passed++

    $watcherRoot = Join-Path $testRoot 'watcher exact commit'
    $watcherPackagingRoot = Join-Path $watcherRoot 'packaging'
    $watcherOutputRoot = Join-Path $watcherRoot 'artifacts\dev-portable'
    $watcherBuildLog = Join-Path $watcherRoot 'build-commits.txt'
    New-Item -ItemType Directory -Path $watcherPackagingRoot | Out-Null
    New-Item -ItemType Directory -Path $watcherOutputRoot | Out-Null
    Copy-Item -LiteralPath $watchScript -Destination $watcherPackagingRoot
    [System.IO.File]::WriteAllText(
        (Join-Path $watcherPackagingRoot 'Build-DevPortable.ps1'),
        @'
param([switch]$AllowDirty, [switch]$FastTestsOnly)
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$commit = (& git -C $root rev-parse --verify HEAD).Trim()
[System.IO.File]::AppendAllText((Join-Path $root 'build-commits.txt'), $commit + [Environment]::NewLine)
if (Test-Path -LiteralPath (Join-Path $root 'delay-next-build') -PathType Leaf) {
    Remove-Item -LiteralPath (Join-Path $root 'delay-next-build') -Force
    Start-Sleep -Seconds 3
}
$outputRoot = Join-Path $root 'artifacts\dev-portable'
$buildRoot = Join-Path $outputRoot ('builds\0.0.0-test-' + $commit.Substring(0, 7))
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$executable = Join-Path $buildRoot 'GraphReader.App.exe'
[System.IO.File]::WriteAllText($executable, '')
$relativeExecutable = $executable.Substring($outputRoot.Length + 1).Replace('\', '/')
[System.IO.File]::WriteAllText(
    (Join-Path $outputRoot 'latest.json'),
    ('{"executable":"' + $relativeExecutable + '"}'))
'@)
    [System.IO.File]::WriteAllText((Join-Path $watcherRoot 'README.md'), 'watcher fixture')
    Invoke-Git -WorkingDirectory $watcherRoot -Arguments @('init', '--initial-branch=main')
    Invoke-Git -WorkingDirectory $watcherRoot -Arguments @('config', 'user.name', 'Graph Reader Test')
    Invoke-Git -WorkingDirectory $watcherRoot -Arguments @('config', 'user.email', 'graph-reader-test@example.invalid')
    Invoke-Git -WorkingDirectory $watcherRoot -Arguments @('add', '.')
    Invoke-Git -WorkingDirectory $watcherRoot -Arguments @('commit', '-m', 'Create watcher fixture')

    $watcherStdout = Join-Path $watcherRoot 'watcher.stdout.log'
    $watcherStderr = Join-Path $watcherRoot 'watcher.stderr.log'
    $watcherProcess = Start-Process -FilePath $hostExecutable -ArgumentList @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f (Join-Path $watcherPackagingRoot 'Watch-DevPortable.ps1')),
        '-NoLaunch') -RedirectStandardOutput $watcherStdout -RedirectStandardError $watcherStderr -PassThru -WindowStyle Hidden
    try {
        $startupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
        while ([DateTimeOffset]::UtcNow -lt $startupDeadline) {
            if ($watcherProcess.HasExited) {
                throw "Watcher exited during startup. stderr: $(Get-Content -LiteralPath $watcherStderr -Raw -ErrorAction SilentlyContinue)"
            }
            if ((Test-Path -LiteralPath $watcherStdout -PathType Leaf) -and
                ([string](Get-Content -LiteralPath $watcherStdout -Raw)) -like '*Press Ctrl+C to stop.*') {
                break
            }
            Start-Sleep -Milliseconds 100
        }
        Assert-True (([string](Get-Content -LiteralPath $watcherStdout -Raw)) -like '*Press Ctrl+C to stop.*') `
            'Watcher did not become ready for the exact-commit behavioral test.'

        Invoke-Git -WorkingDirectory $watcherRoot -Arguments @('commit', '--allow-empty', '-m', 'Move current branch ref')
        $expectedCommit = (& git -C $watcherRoot rev-parse --verify HEAD).Trim().ToLowerInvariant()
        $buildDeadline = [DateTimeOffset]::UtcNow.AddSeconds(12)
        while ([DateTimeOffset]::UtcNow -lt $buildDeadline -and
            -not (Test-Path -LiteralPath $watcherBuildLog -PathType Leaf)) {
            if ($watcherProcess.HasExited) {
                throw "Watcher exited before the ref-triggered build. stderr: $(Get-Content -LiteralPath $watcherStderr -Raw -ErrorAction SilentlyContinue)"
            }
            Start-Sleep -Milliseconds 100
        }
        Assert-True (Test-Path -LiteralPath $watcherBuildLog -PathType Leaf) `
            'Moving the current branch ref did not trigger a preview build.'
        Start-Sleep -Milliseconds 500
        $builtCommits = @(Get-Content -LiteralPath $watcherBuildLog | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        Assert-True ($builtCommits.Count -eq 1) `
            "A single ref move should queue one build, but recorded $($builtCommits.Count)."
        Assert-True ($builtCommits[0].Trim().ToLowerInvariant() -eq $expectedCommit) `
            "Watcher built commit $($builtCommits[0]) instead of exact HEAD $expectedCommit."
        Write-Host "Watcher ref trigger passed: commit=$expectedCommit builds=$($builtCommits.Count)"

        $sourceRoot = Join-Path $watcherRoot 'src'
        New-Item -ItemType Directory -Path $sourceRoot | Out-Null
        $debounceStart = [DateTimeOffset]::UtcNow
        [System.IO.File]::WriteAllText((Join-Path $sourceRoot 'Debounce.cs'), 'first')
        $debounceDeadline = $debounceStart.AddSeconds(12)
        do {
            $builtCommits = @(Get-Content -LiteralPath $watcherBuildLog | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($builtCommits.Count -ge 2) {
                break
            }
            Start-Sleep -Milliseconds 100
        } while ([DateTimeOffset]::UtcNow -lt $debounceDeadline)
        $debounceElapsed = [DateTimeOffset]::UtcNow - $debounceStart
        Assert-True ($builtCommits.Count -eq 2) `
            "A relevant source change should produce one debounced build; recorded $($builtCommits.Count)."
        Assert-True ($debounceElapsed.TotalSeconds -ge 1.5) `
            "Watcher built after $([Math]::Round($debounceElapsed.TotalSeconds, 2)) seconds; debounce must be at least 1.5 seconds."
        Write-Host "Watcher debounce passed: seconds=$([Math]::Round($debounceElapsed.TotalSeconds, 2)) builds=$($builtCommits.Count)"

        [System.IO.File]::WriteAllText((Join-Path $watcherRoot 'delay-next-build'), '')
        [System.IO.File]::WriteAllText((Join-Path $sourceRoot 'Queued.cs'), 'trigger delayed build')
        $delayedBuildDeadline = [DateTimeOffset]::UtcNow.AddSeconds(12)
        do {
            $builtCommits = @(Get-Content -LiteralPath $watcherBuildLog | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($builtCommits.Count -ge 3) {
                break
            }
            Start-Sleep -Milliseconds 100
        } while ([DateTimeOffset]::UtcNow -lt $delayedBuildDeadline)
        Assert-True ($builtCommits.Count -eq 3) 'The delayed build did not start.'

        [System.IO.File]::WriteAllText((Join-Path $sourceRoot 'DuringBuild.cs'), 'queue one follow-up')
        $queuedBuildDeadline = [DateTimeOffset]::UtcNow.AddSeconds(12)
        do {
            $builtCommits = @(Get-Content -LiteralPath $watcherBuildLog | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ($builtCommits.Count -ge 4) {
                break
            }
            Start-Sleep -Milliseconds 100
        } while ([DateTimeOffset]::UtcNow -lt $queuedBuildDeadline)
        Assert-True ($builtCommits.Count -eq 4) `
            "A change during a build should queue one follow-up build; recorded $($builtCommits.Count)."
        Start-Sleep -Seconds 3
        $builtCommits = @(Get-Content -LiteralPath $watcherBuildLog | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        Assert-True ($builtCommits.Count -eq 4) `
            "Queued changes should collapse into one follow-up build; recorded $($builtCommits.Count)."
        Write-Host "Watcher queued-build passed: total-builds=$($builtCommits.Count)"
    }
    finally {
        if ($null -ne $watcherProcess -and -not $watcherProcess.HasExited) {
            $watcherProcess.Kill()
            $watcherProcess.WaitForExit()
        }
        if ($null -ne $watcherProcess) {
            $watcherProcess.Dispose()
        }
    }
    $passed++

    $failureRoot = Join-Path $testRoot 'prior latest preservation'
    New-Item -ItemType Directory -Path $failureRoot | Out-Null
    $latestPath = Join-Path $failureRoot 'latest.json'
    $expectedLatest = '{"sentinel":"prior-success"}'
    [System.IO.File]::WriteAllText($latestPath, $expectedLatest)
    [System.IO.File]::WriteAllText($dirtyMarker, 'force dirty-tree rejection')

    Invoke-ExpectedFailure @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $buildScript,
        '-OutputRoot', $failureRoot)

    Assert-True ((Get-Content -LiteralPath $latestPath -Raw) -eq $expectedLatest) `
        'A failed dirty-tree build changed the prior latest.json.'
    $failurePath = Join-Path $failureRoot 'last-failure.json'
    Assert-True (Test-Path -LiteralPath $failurePath -PathType Leaf) `
        'A failed build did not produce last-failure.json.'
    $failure = Get-Content -LiteralPath $failurePath -Raw | ConvertFrom-Json
    Assert-True ($failure.priorLatestPreserved -eq $true) `
        'last-failure.json did not record preservation of the prior latest build.'
    $passed++

    $retentionRoot = Join-Path $testRoot 'retention push failure simulation'
    $retentionBuildsRoot = Join-Path $retentionRoot 'builds'
    $retentionOldBuild = Join-Path $retentionBuildsRoot '0.0.0-old'
    $retentionNewBuild = Join-Path $retentionBuildsRoot '0.0.0-new'
    New-Item -ItemType Directory -Path $retentionOldBuild -Force | Out-Null
    New-Item -ItemType Directory -Path $retentionNewBuild -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $retentionNewBuild 'GraphReader.App.exe'), 'new')
    $retentionExecutableHash = (Get-FileHash -LiteralPath (Join-Path $retentionNewBuild 'GraphReader.App.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-JsonFile -Path (Join-Path $retentionRoot 'latest.json') -Value ([ordered]@{
            schemaVersion = 2
            buildDirectory = 'builds/0.0.0-new'
            executable = 'builds/0.0.0-new/GraphReader.App.exe'
            executableSha256 = $retentionExecutableHash
        })
    $retentionLedgerPath = Join-Path $retentionRoot 'BUILD_LEDGER.json'
    Write-JsonFile -Path $retentionLedgerPath -Value ([ordered]@{
            schemaVersion = 1
            builds = @(
                [ordered]@{ directory = '0.0.0-old'; commit = ('a' * 40); buildTimeUtc = '2026-08-19T00:00:00Z'; retained = $false; recordIncomplete = $false },
                [ordered]@{ directory = '0.0.0-new'; commit = ('b' * 40); buildTimeUtc = '2026-08-19T00:01:00Z'; retained = $true; recordIncomplete = $false }
            )
        })
    & $hostExecutable -NoProfile -ExecutionPolicy Bypass -File $watchScript `
        -RetentionSimulation -RetentionSimulationRoot $retentionRoot 1>$null
    Assert-True ($LASTEXITCODE -eq 0) 'Retention push-failure simulation exited unsuccessfully.'
    Assert-True (Test-Path -LiteralPath $retentionOldBuild -PathType Container) `
        'A failed retention push deleted the previous build directory.'
    Assert-True (Test-Path -LiteralPath $retentionNewBuild -PathType Container) `
        'A failed retention push deleted the new retained build directory.'
    $retentionSuccessOutput = Join-Path $retentionRoot 'success-simulation.log'
    & $hostExecutable -NoProfile -ExecutionPolicy Bypass -File $watchScript `
        -RetentionSimulation -RetentionSimulationRoot $retentionRoot `
        -RetentionSimulationScenario PushSuccess 1>$retentionSuccessOutput
    Assert-True ($LASTEXITCODE -eq 0) 'Retention success-order simulation exited unsuccessfully.'
    Assert-True ((Get-Content -LiteralPath $retentionSuccessOutput -Raw) -like '*sequence=build,verify,ledger,commit,push,delete*planned-deletions=1*') `
        'Retention success-order simulation did not report the full build-to-delete sequence.'
    Assert-True (@(Get-ChildItem -LiteralPath $retentionBuildsRoot -Directory).Count -eq 2) `
        'Retention simulation unexpectedly deleted a build directory.'
    $incompleteLedger = Get-Content -LiteralPath $retentionLedgerPath -Raw | ConvertFrom-Json
    $incompleteLedger.builds[0].recordIncomplete = $true
    Write-JsonFile -Path $retentionLedgerPath -Value $incompleteLedger
    Invoke-ExpectedFailure @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $watchScript,
        '-RetentionSimulation',
        '-RetentionSimulationRoot', $retentionRoot,
        '-RetentionSimulationScenario', 'PushSuccess')
    $incompleteLedger.builds[0].recordIncomplete = $false
    Write-JsonFile -Path $retentionLedgerPath -Value $incompleteLedger
    $passed++

    $diagnosticsRoot = Join-Path $testRoot 'retired dirty build'
    $fakeToolRoot = Join-Path $testRoot 'unused fake dotnet'
    New-Item -ItemType Directory -Path $diagnosticsRoot | Out-Null
    New-Item -ItemType Directory -Path $fakeToolRoot | Out-Null
    [System.IO.File]::WriteAllText(
        (Join-Path $fakeToolRoot 'dotnet.cmd'),
        "@echo off`r`necho DIAGNOSTIC_SENTINEL 1>&2`r`nexit /b 7`r`n")
    $previousProcessPath = [Environment]::GetEnvironmentVariable('PATH', 'Process')
    try {
        [Environment]::SetEnvironmentVariable(
            'PATH',
            $fakeToolRoot + [System.IO.Path]::PathSeparator + $previousProcessPath,
            'Process')
        Invoke-ExpectedFailure @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $buildScript,
            '-OutputRoot', $diagnosticsRoot,
            '-AllowDirty',
            '-FastTestsOnly',
            '-SkipRestore')
    }
    finally {
        [Environment]::SetEnvironmentVariable('PATH', $previousProcessPath, 'Process')
    }
    $capturedFailure = Get-Content -LiteralPath (Join-Path $diagnosticsRoot 'last-failure.json') -Raw | ConvertFrom-Json
    Assert-True ($capturedFailure.command -eq 'read repository state') `
        "Retired dirty-build mode failed at the wrong command. Found '$($capturedFailure.command)': $($capturedFailure.error)"
    Assert-True ($capturedFailure.error -like '*-AllowDirty is retired*') `
        'Build-DevPortable did not reject the retired unrecorded dirty-build mode.'
    $passed++

    $launcherRepositoryRoot = Join-Path $testRoot 'launcher enhancement isolation'
    $launcherIsolationRoot = Join-Path $launcherRepositoryRoot 'artifacts\dev-portable'
    $launcherIsolationBuild = Join-Path $launcherIsolationRoot 'builds\isolated'
    New-Item -ItemType Directory -Path $launcherIsolationBuild -Force | Out-Null
    Copy-Item -LiteralPath $launcherScript -Destination $launcherIsolationRoot
    Copy-Item -LiteralPath $launcherCommand -Destination $launcherIsolationRoot
    $captureExecutable = Join-Path $launcherIsolationBuild 'capture.cmd'
    $captureOutput = Join-Path $launcherIsolationBuild 'enhancement-environment.txt'
    $startupImage = Join-Path $launcherIsolationRoot 'Chandler graph.png'
    [System.IO.File]::WriteAllBytes($startupImage, [byte[]](1, 2, 3))
    [System.IO.File]::WriteAllText(
        $captureExecutable,
        "@echo off`r`necho [%GRAPHREADER_REALESRGAN_RUNTIME_ROOT%] > `"%~dp0enhancement-environment.txt`"`r`necho [%GRAPHREADER_REALESRGAN_MANIFEST_PATH%] >> `"%~dp0enhancement-environment.txt`"`r`necho [%GRAPHREADER_PDFIUM_APPROVAL_PATH%] >> `"%~dp0enhancement-environment.txt`"`r`necho [%*] >> `"%~dp0enhancement-environment.txt`"`r`n")
    [System.IO.File]::WriteAllText((Join-Path $launcherIsolationBuild 'portable.mode'), '')
    $captureHash = (Get-FileHash -LiteralPath $captureExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    $fixtureLatest = [ordered]@{
            schemaVersion = 2
            version = '0.0.1'
            commit = ('a' * 40)
            buildDirectory = 'builds/isolated'
            executable = 'builds/isolated/capture.cmd'
            executableSha256 = $captureHash
        }
    $fixtureLedger = [ordered]@{
        schemaVersion = 1
        builds = @([ordered]@{ directory = 'isolated'; version = '0.0.1'; commit = ('a' * 40); executableSha256 = $captureHash; retained = $true })
    }
    $fixtureLedgerPath = Join-Path $launcherRepositoryRoot 'docs\BUILD_LEDGER.json'
    Write-JsonFile -Path (Join-Path $launcherIsolationRoot 'latest.json') -Value $fixtureLatest
    Write-JsonFile -Path $fixtureLedgerPath -Value $fixtureLedger
    $previousRuntimeRoot = [Environment]::GetEnvironmentVariable(
        'GRAPHREADER_REALESRGAN_RUNTIME_ROOT',
        'Process')
    $previousManifestPath = [Environment]::GetEnvironmentVariable(
        'GRAPHREADER_REALESRGAN_MANIFEST_PATH',
        'Process')
    $previousPdfiumApprovalPath = [Environment]::GetEnvironmentVariable(
        'GRAPHREADER_PDFIUM_APPROVAL_PATH',
        'Process')
    try {
        [Environment]::SetEnvironmentVariable(
            'GRAPHREADER_REALESRGAN_RUNTIME_ROOT',
            'C:\stale-runtime',
            'Process')
        [Environment]::SetEnvironmentVariable(
            'GRAPHREADER_REALESRGAN_MANIFEST_PATH',
            'C:\stale-manifest.json',
            'Process')
        [Environment]::SetEnvironmentVariable(
            'GRAPHREADER_PDFIUM_APPROVAL_PATH',
            'C:\stale-pdfium-approval.json',
            'Process')
        & (Join-Path $launcherIsolationRoot 'Run-Latest-DevPortable.cmd') `
            -Wait -DisableLocalEnhancement -DisableLocalPdfium -ImagePath $startupImage | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Disabled-enhancement launcher exited with code $LASTEXITCODE."
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'GRAPHREADER_REALESRGAN_RUNTIME_ROOT',
            $previousRuntimeRoot,
            'Process')
        [Environment]::SetEnvironmentVariable(
            'GRAPHREADER_REALESRGAN_MANIFEST_PATH',
            $previousManifestPath,
            'Process')
        [Environment]::SetEnvironmentVariable(
            'GRAPHREADER_PDFIUM_APPROVAL_PATH',
            $previousPdfiumApprovalPath,
            'Process')
    }
    $capturedEnvironment = @(Get-Content -LiteralPath $captureOutput)
    Assert-True ($capturedEnvironment.Count -eq 4) `
        'Launcher did not capture all child environment values and startup arguments.'
    Assert-True ($capturedEnvironment[0].Trim() -eq '[]') `
        'Disabled-enhancement launcher leaked an inherited runtime root to the child.'
    Assert-True ($capturedEnvironment[1].Trim() -eq '[]') `
        'Disabled-enhancement launcher leaked an inherited manifest path to the child.'
    Assert-True ($capturedEnvironment[2].Trim() -eq '[]') `
        'Disabled-PDFium launcher leaked an inherited approval path to the child.'
    Assert-True ($capturedEnvironment[3].Contains('--open-image')) `
        'Launcher did not pass the explicit startup image argument.'
    Assert-True ($capturedEnvironment[3].Contains($startupImage)) `
        'Launcher did not preserve the startup image path containing spaces.'
    $passed++

    $customLauncherRoot = Join-Path $testRoot 'custom output location'
    Copy-Item -LiteralPath $launcherIsolationRoot -Destination $customLauncherRoot -Recurse
    $customMarker = Join-Path $customLauncherRoot 'builds\isolated\enhancement-environment.txt'
    Remove-Item -LiteralPath $customMarker -Force
    $customLauncher = Join-Path $customLauncherRoot 'Run-Latest-DevPortable.ps1'
    & $hostExecutable -NoProfile -ExecutionPolicy Bypass -File $customLauncher -Wait -DisableLocalEnhancement -DisableLocalPdfium -RepositoryRoot $launcherRepositoryRoot | Out-Null
    Assert-True ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $customMarker)) 'Custom output launcher did not use the specified repository ledger.'
    Assert-LauncherRejected -Script $customLauncher -Marker $customMarker -Reason 'ledger missing' -RepositoryRoot (Join-Path $testRoot 'missing repository')
    $passed++

    $fixtureLedger.builds[0].retained = $false
    Write-JsonFile -Path $fixtureLedgerPath -Value $fixtureLedger
    Assert-LauncherRejected -Script $customLauncher -Marker $customMarker -Reason 'does not match the single retained build' -RepositoryRoot $launcherRepositoryRoot
    $fixtureLedger.builds[0].retained = $true
    $fixtureLedger.builds[0].commit = ('b' * 40)
    Write-JsonFile -Path $fixtureLedgerPath -Value $fixtureLedger
    Assert-LauncherRejected -Script $customLauncher -Marker $customMarker -Reason 'does not match the single retained build' -RepositoryRoot $launcherRepositoryRoot
    $fixtureLedger.builds[0].commit = ('a' * 40)
    Write-JsonFile -Path $fixtureLedgerPath -Value $fixtureLedger
    $passed++

    [System.IO.File]::AppendAllText($captureExecutable, "rem tampered`r`n")
    Invoke-ExpectedFailure @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $launcherIsolationRoot 'Run-Latest-DevPortable.ps1'),
        '-Wait')
    $passed++

    $launcherTraversalRepository = Join-Path $testRoot 'launcher traversal'
    $launcherRoot = Join-Path $launcherTraversalRepository 'artifacts\dev-portable'
    New-Item -ItemType Directory -Path $launcherRoot -Force | Out-Null
    Copy-Item -LiteralPath $launcherScript -Destination $launcherRoot
    Write-JsonFile -Path (Join-Path $launcherRoot 'latest.json') -Value ([ordered]@{
            schemaVersion = 2
            version = '0.0.1'
            commit = ('a' * 40)
            buildDirectory = 'builds/isolated'
            executable = '../outside.exe'
            executableSha256 = ('0' * 64)
        })
    Write-JsonFile -Path (Join-Path $launcherTraversalRepository 'docs\BUILD_LEDGER.json') -Value ([ordered]@{
            schemaVersion = 1
            builds = @([ordered]@{ directory = 'isolated'; version = '0.0.1'; commit = ('a' * 40); executableSha256 = ('0' * 64); retained = $true })
        })

    Invoke-ExpectedFailure @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Join-Path $launcherRoot 'Run-Latest-DevPortable.ps1'))
    $passed++

    Write-Host "Development portable packaging tests passed: $passed/12"
}
finally {
    if (Test-Path -LiteralPath $dirtyMarker -PathType Leaf) {
        Remove-Item -LiteralPath $dirtyMarker -Force
    }

    $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
    $temporaryPrefix = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/') +
        [System.IO.Path]::DirectorySeparatorChar
    if ($resolvedTestRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
