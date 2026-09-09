# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param(
    [switch]$AllowDirty,
    [switch]$FastTestsOnly,
    [switch]$SkipRestore,
    [string]$OutputRoot,
    [string]$ReviewedOpenCvEvidenceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DevPortable.Common.ps1')
. (Join-Path $PSScriptRoot 'VersionPolicy.ps1')

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'artifacts\dev-portable'
}
else {
    $OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
}

$buildsRoot = Join-Path $OutputRoot 'builds'
$stagingRoot = Join-Path $OutputRoot '.staging'
$latestPath = Join-Path $OutputRoot 'latest.json'
$failurePath = Join-Path $OutputRoot 'last-failure.json'
$sharedDataRoot = Join-Path $OutputRoot 'Data'
$currentCommand = 'initialize'
$diagnostics = [System.Collections.Generic.List[string]]::new()
$testsRun = [System.Collections.Generic.List[string]]::new()
$stagingDirectory = $null
$successfulBuildDirectory = $null

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Description,

        [Parameter(Mandatory)]
        [scriptblock]$Command
    )

    $script:currentCommand = $Description
    $script:diagnostics.Clear()
    Write-Host "[$Description]"
    $previousErrorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Command 2>&1 | ForEach-Object {
            $line = [string]$_
            if ($script:diagnostics.Count -ge 400) {
                $script:diagnostics.RemoveAt(0)
            }
            $script:diagnostics.Add($line)
            Write-Host $line
        }
        $commandExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorPreference
    }
    if ($commandExitCode -ne 0) {
        throw "Command failed with exit code ${commandExitCode}: $Description"
    }
}

function Get-CentralVersion {
    return (Get-GraphReaderCentralVersion -RepositoryRoot $repositoryRoot).Value
}

function Assert-PreparedBuildVersion {
    param([Parameter(Mandatory)][string]$Version)

    $ledgerPath = Join-Path $repositoryRoot 'docs\BUILD_LEDGER.json'
    if (-not (Test-Path -LiteralPath $ledgerPath -PathType Leaf)) {
        throw "The tracked build ledger is missing: $ledgerPath"
    }
    $ledger = Get-Content -LiteralPath $ledgerPath -Raw | ConvertFrom-Json
    if ($ledger.schemaVersion -ne 1) {
        throw 'The tracked build ledger schemaVersion must be 1.'
    }
    $builds = @($ledger.builds)
    if ($builds.Count -eq 0) {
        throw 'The tracked build ledger contains no builds.'
    }
    $latestBuild = $builds | Sort-Object { [int]$_.buildNumber } -Descending | Select-Object -First 1
    $latestVersion = [string]$latestBuild.version
    $expectedVersion = Get-NextGraphReaderVersion -Version $latestVersion
    $stablePromotion = Test-GraphReaderStablePromotion -FromVersion $latestVersion -ToVersion $Version
    if ($Version -cne $expectedVersion -and -not $stablePromotion) {
        throw "Central version '$Version' would reuse or skip a packaged-build version. Prepare and commit '$expectedVersion', or the explicit '$(Get-GraphReaderStablePromotionVersion)' promotion, before building."
    }
}

function Get-GitOutput {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = & git -C $repositoryRoot @Arguments 2>$null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) {
        throw "Git command failed: git $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }

    return ($output -join [Environment]::NewLine).Trim()
}

try {
    $currentCommand = 'read repository state'
    if ($AllowDirty.IsPresent) {
        throw '-AllowDirty is retired: every produced build must be committed, recorded, and pushed.'
    }
    $version = Get-CentralVersion
    $commit = Get-GitOutput -Arguments @('rev-parse', 'HEAD')
    $shortCommit = Get-GitOutput -Arguments @('rev-parse', '--short=8', 'HEAD')
    $status = Get-GitOutput -Arguments @('status', '--porcelain', '--untracked-files=normal')
    $isDirty = -not [string]::IsNullOrWhiteSpace($status)
    if ($isDirty) {
        throw 'The working tree is dirty. Commit the checkpoint before producing a build.'
    }
    Assert-PreparedBuildVersion -Version $version

    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $buildsRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $sharedDataRoot -Force | Out-Null

    if (-not $SkipRestore.IsPresent) {
        Invoke-CheckedCommand -Description 'dotnet restore GraphAutoReader.slnx' -Command {
            & dotnet restore (Join-Path $repositoryRoot 'GraphAutoReader.slnx')
        }
    }

    $fastTestCommands = @(
        [pscustomobject]@{
            Name = 'GraphReader.App.Tests'
            Project = 'tests\GraphReader.App.Tests\GraphReader.App.Tests.csproj'
            Filter = $null
        },
        [pscustomobject]@{
            Name = 'GraphReader.Domain.Tests'
            Project = 'tests\GraphReader.Domain.Tests\GraphReader.Domain.Tests.csproj'
            Filter = $null
        },
        [pscustomobject]@{
            Name = 'Goal 19 integration and packaging smoke'
            Project = 'tests\GraphReader.Integration.Tests\GraphReader.Integration.Tests.csproj'
            Filter = 'FullyQualifiedName~IntegrationSmoke|FullyQualifiedName~PackagingContractTests|FullyQualifiedName~GitIgnoreContractTests'
        }
    )

    foreach ($test in $fastTestCommands) {
        $testsRun.Add($test.Name)
        $projectPath = Join-Path $repositoryRoot $test.Project
        if ([string]::IsNullOrWhiteSpace([string]$test.Filter)) {
            Invoke-CheckedCommand -Description "dotnet test $($test.Name)" -Command {
                & dotnet test $projectPath -c Release --no-restore
            }
        }
        else {
            $filter = [string]$test.Filter
            Invoke-CheckedCommand -Description "dotnet test $($test.Name)" -Command {
                & dotnet test $projectPath -c Release --no-restore --filter $filter
            }
        }
    }

    if (-not $FastTestsOnly.IsPresent) {
        $testsRun.Add('Full Release solution')
        Invoke-CheckedCommand -Description 'dotnet test GraphAutoReader.slnx -c Release' -Command {
            & dotnet test (Join-Path $repositoryRoot 'GraphAutoReader.slnx') -c Release --no-restore
        }
        $testsRun.Add('Public synthetic scoreboard')
        Invoke-CheckedCommand -Description 'public synthetic scoreboard' -Command {
            & dotnet run -c Release --no-restore --project (Join-Path $repositoryRoot 'tools\GraphReader.Benchmarks') -- --suite public
        }
        $testsRun.Add('Packaging regression')
        Invoke-CheckedCommand -Description 'packaging regression' -Command {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repositoryRoot 'packaging\tests\Test-ReleaseArtifact.Tests.ps1')
        }
        $testsRun.Add('Development portable packaging regression')
        Invoke-CheckedCommand -Description 'development portable packaging regression' -Command {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repositoryRoot 'packaging\tests\Test-DevPortable.Tests.ps1')
        }
        $testsRun.Add('Localization audit regression')
        Invoke-CheckedCommand -Description 'localization audit regression' -Command {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repositoryRoot 'packaging\localization\Test-LocalizationAudit.ps1')
        }
        $testsRun.Add('Reviewed OpenCV runtime packaging regression')
        Invoke-CheckedCommand -Description 'reviewed OpenCV runtime packaging regression' -Command {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repositoryRoot 'packaging\opencv-source\tests\Test-InstallReviewedRuntime.Tests.ps1')
        }
    }

    $buildTimestamp = [DateTimeOffset]::UtcNow
    $buildName = '{0}-{1}-{2}' -f $version, $buildTimestamp.ToString('yyyyMMddTHHmmssfffZ'), $shortCommit
    $successfulBuildDirectory = Join-Path $buildsRoot $buildName
    if (Test-Path -LiteralPath $successfulBuildDirectory) {
        throw "The immutable build folder already exists: $successfulBuildDirectory"
    }

    $stagingDirectory = Join-Path $stagingRoot ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

    Invoke-CheckedCommand -Description 'dotnet publish GraphReader.App' -Command {
        & dotnet publish (Join-Path $repositoryRoot 'src\GraphReader.App\GraphReader.App.csproj') `
            -c Release `
            -r win-x64 `
            --self-contained true `
            --no-restore `
            -p:PublishSingleFile=false `
            -p:DebugSymbols=false `
            -p:DebugType=None `
            -o $stagingDirectory
    }

    Copy-DevPortableRequiredContent `
        -RepositoryRoot $repositoryRoot `
        -DestinationRoot $stagingDirectory

    $executablePath = Join-Path $stagingDirectory 'GraphReader.App.exe'
    $runtimeConfigPath = Join-Path $stagingDirectory 'GraphReader.App.runtimeconfig.json'
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "Published executable is missing: $executablePath"
    }
    if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
        throw "Published runtime configuration is missing: $runtimeConfigPath"
    }

    $publishedOpenCvRuntime = @(Get-ChildItem -LiteralPath $stagingDirectory -Recurse -File -Filter 'OpenCvSharpExtern.dll')
    if ($publishedOpenCvRuntime.Count -ne 1) {
        throw "Expected exactly one published OpenCvSharpExtern.dll, found $($publishedOpenCvRuntime.Count)."
    }
    if (-not [string]::IsNullOrWhiteSpace($ReviewedOpenCvEvidenceRoot)) {
        $reviewedOpenCvEvidencePath = [IO.Path]::GetFullPath($ReviewedOpenCvEvidenceRoot)
        Invoke-CheckedCommand -Description 'install reviewed source-built OpenCV runtime' -Command {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repositoryRoot 'packaging\opencv-source\Install-ReviewedRuntime.ps1') `
                -EvidenceRoot $reviewedOpenCvEvidencePath `
                -DestinationRoot $stagingDirectory
        }
        $openCvRuntime = Get-Content -LiteralPath (Join-Path $stagingDirectory 'reviewed-opencv-runtime.json') -Raw | ConvertFrom-Json
        if ([bool]$openCvRuntime.releaseApproved -or
            [string]$openCvRuntime.binarySha256 -ne '87c12460daba638b36e916ea2bb832d0759fbf094b8639919a7ce11b0cca5791') {
            throw 'Reviewed OpenCV runtime metadata is invalid or unexpectedly release-approved.'
        }
    }
    else {
        $openCvRuntime = [pscustomobject][ordered]@{
            schema = 'graphreader.development-opencv-runtime.v1'
            runtimeId = 'opencvsharpextern-nuget-development-baseline'
            binarySha256 = (Get-FileHash -LiteralPath $publishedOpenCvRuntime[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            provenanceValidated = $false
            cleanMachineEvidence = $false
            releaseApproved = $false
        }
    }
    $executableSha256 = (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash.ToLowerInvariant()

    Write-Utf8NoBom -Path (Join-Path $stagingDirectory 'portable.mode') -Content "development portable`r`n"
    Write-Utf8NoBom -Path (Join-Path $stagingDirectory 'DEVELOPMENT_BUILD.txt') -Content @"
Graph Auto Reader Development Preview
Local maintainer testing only. Do not redistribute or publish.
Version: $version
Commit: $commit
Dirty: $($isDirty.ToString().ToLowerInvariant())
Built UTC: $($buildTimestamp.ToString('O'))
Runtime mode: ManualPreview
OpenCV runtime: $($openCvRuntime.runtimeId)
OpenCV SHA-256: $($openCvRuntime.binarySha256)
"@

    $availableModelIds = @(Get-DevPortableApprovedModelIds `
            -RepositoryRoot $repositoryRoot `
            -Diagnostics $diagnostics)
    $unavailableStages = @('enhancement', 'axis', 'ocr', 'markers', 'legends', 'phases')
    $buildInfo = [ordered]@{
        schemaVersion = 2
        version = $version
        commit = $commit
        shortCommit = $shortCommit
        dirty = $isDirty
        buildTimeUtc = $buildTimestamp.ToString('O')
        runtimeMode = 'ManualPreview'
        executableSha256 = $executableSha256
        testsRun = @($testsRun)
        openCvRuntime = $openCvRuntime
        availableModelIds = $availableModelIds
        unavailableStages = $unavailableStages
        localOnlyWarning = 'Development Preview. Local maintainer testing only. Do not redistribute or publish.'
    }
    Write-JsonFile -Path (Join-Path $stagingDirectory 'build-info.json') -Value $buildInfo

    $currentCommand = 'portable smoke'
    $previousDataRoot = [Environment]::GetEnvironmentVariable('GRAPHREADER_DEV_PORTABLE_DATA_ROOT', 'Process')
    try {
        [Environment]::SetEnvironmentVariable(
            'GRAPHREADER_DEV_PORTABLE_DATA_ROOT',
            [System.IO.Path]::GetFullPath($sharedDataRoot),
            'Process')
        $smokeProcess = Start-Process `
            -FilePath $executablePath `
            -ArgumentList '--portable-smoke' `
            -WorkingDirectory $stagingDirectory `
            -WindowStyle Hidden `
            -PassThru `
            -Wait
        if ($smokeProcess.ExitCode -ne 0) {
            throw "Portable smoke exited with code $($smokeProcess.ExitCode)."
        }

        $productionRuntimeSmokeProcess = Start-Process `
            -FilePath $executablePath `
            -ArgumentList '--production-runtime-smoke' `
            -WorkingDirectory $stagingDirectory `
            -WindowStyle Hidden `
            -PassThru `
            -Wait
        if ($productionRuntimeSmokeProcess.ExitCode -ne 2) {
            throw "Development portable unexpectedly reported compiled Production runtime mode (exit $($productionRuntimeSmokeProcess.ExitCode))."
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'GRAPHREADER_DEV_PORTABLE_DATA_ROOT',
            $previousDataRoot,
            'Process')
    }

    Move-Item -LiteralPath $stagingDirectory -Destination $successfulBuildDirectory
    $stagingDirectory = $null

    $currentCommand = 'finalize development portable metadata'
    $outputPrefix = [System.IO.Path]::GetFullPath($OutputRoot).TrimEnd('\', '/') +
        [System.IO.Path]::DirectorySeparatorChar
    $fullBuildDirectory = [System.IO.Path]::GetFullPath($successfulBuildDirectory)
    if (-not $fullBuildDirectory.StartsWith($outputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The successful build directory is outside the development portable output root.'
    }
    $relativeBuildDirectory = $fullBuildDirectory.Substring($outputPrefix.Length)
    $latest = [ordered]@{
        schemaVersion = 2
        version = $version
        commit = $commit
        shortCommit = $shortCommit
        dirty = $isDirty
        buildTimeUtc = $buildTimestamp.ToString('O')
        buildDirectory = $relativeBuildDirectory.Replace('\', '/')
        executable = ($relativeBuildDirectory.Replace('\', '/') + '/GraphReader.App.exe')
        executableSha256 = $executableSha256
        dataRoot = 'Data'
        openCvRuntime = [ordered]@{
            runtimeId = [string]$openCvRuntime.runtimeId
            binarySha256 = [string]$openCvRuntime.binarySha256
            provenanceValidated = [bool]$openCvRuntime.provenanceValidated
            cleanMachineEvidence = [bool]$openCvRuntime.cleanMachineEvidence
            releaseApproved = [bool]$openCvRuntime.releaseApproved
        }
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Run-Latest-DevPortable.ps1') -Destination $OutputRoot -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Run-Latest-DevPortable.cmd') -Destination $OutputRoot -Force
    Write-JsonFileAtomic -Path $latestPath -Value $latest

    if (Test-Path -LiteralPath $failurePath -PathType Leaf) {
        Remove-Item -LiteralPath $failurePath -Force
    }

    Write-Host "Development portable ready: $successfulBuildDirectory"
    Write-Host "Executable: $(Join-Path $successfulBuildDirectory 'GraphReader.App.exe')"
    Write-Host "Shared data: $sharedDataRoot"
}
catch {
    if (Test-Path -LiteralPath $OutputRoot -PathType Container) {
        $failure = [ordered]@{
            schemaVersion = 1
            failedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
            command = $currentCommand
            error = $_.Exception.Message
            diagnostics = @($diagnostics)
            priorLatestPreserved = Test-Path -LiteralPath $latestPath -PathType Leaf
        }
        Write-JsonFileAtomic -Path $failurePath -Value $failure
    }

    Write-Error $_
    exit 1
}
finally {
    if ($null -ne $stagingDirectory -and (Test-Path -LiteralPath $stagingDirectory -PathType Container)) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
