# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param(
    [string]$BuildRoot,
    [string]$LatestPath,
    [string]$OutputPath,
    [string]$AssignedUtc = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'VersionPolicy.ps1')

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($BuildRoot)) {
    $BuildRoot = Join-Path $repositoryRoot 'artifacts\dev-portable\builds'
}
if ([string]::IsNullOrWhiteSpace($LatestPath)) {
    $LatestPath = Join-Path $repositoryRoot 'artifacts\dev-portable\latest.json'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot 'docs\BUILD_LEDGER.json'
}

function ConvertTo-BuildVersion {
    param([Parameter(Mandatory)][int]$BuildNumber)

    $major = [Math]::Floor($BuildNumber / 10000)
    $remainder = $BuildNumber % 10000
    $minor = [Math]::Floor($remainder / 100)
    $patch = $remainder % 100
    return "$major.$minor.$patch"
}

function Get-OptionalString {
    param(
        [Parameter(Mandatory)][object]$Object,
        [Parameter(Mandatory)][string]$Name
    )

    $rawValue = $null
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) {
            $rawValue = $Object[$Name]
        }
    }
    else {
        $property = $Object.PSObject.Properties[$Name]
        if ($null -ne $property) {
            $rawValue = $property.Value
        }
    }
    if ($null -eq $rawValue) {
        return $null
    }
    if ($rawValue -is [DateTime]) {
        return $rawValue.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    }
    if ($rawValue -is [DateTimeOffset]) {
        return $rawValue.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    }
    $value = ([string]$rawValue).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $null
    }
    return $value
}

function Get-OpenCvRuntimeHash {
    param([Parameter(Mandatory)][object]$BuildInfo)

    $runtimeProperty = $BuildInfo.PSObject.Properties['openCvRuntime']
    if ($null -ne $runtimeProperty -and $null -ne $runtimeProperty.Value) {
        $nested = Get-OptionalString -Object $runtimeProperty.Value -Name 'binarySha256'
        if ($null -ne $nested) {
            return $nested.ToLowerInvariant()
        }
    }
    $legacy = Get-OptionalString -Object $BuildInfo -Name 'openCvRuntimeSha256'
    if ($null -eq $legacy) {
        return $null
    }
    return $legacy.ToLowerInvariant()
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if (-not (Test-Path -LiteralPath $BuildRoot -PathType Container)) {
    throw "Portable build directory is missing: $BuildRoot"
}
if (-not (Test-Path -LiteralPath $LatestPath -PathType Leaf)) {
    throw "Portable latest metadata is missing: $LatestPath"
}

$latest = Get-Content -LiteralPath $LatestPath -Raw | ConvertFrom-Json
$latestBuildDirectory = Get-OptionalString -Object $latest -Name 'buildDirectory'
if ($null -eq $latestBuildDirectory) {
    throw 'latest.json does not contain buildDirectory.'
}
$retainedDirectory = [System.IO.Path]::GetFileName($latestBuildDirectory.Replace('/', '\'))
if ([string]::IsNullOrWhiteSpace($retainedDirectory)) {
    throw "latest.json buildDirectory is invalid: $latestBuildDirectory"
}

$directories = @(Get-ChildItem -LiteralPath $BuildRoot -Directory | Sort-Object Name)
if ($directories.Count -eq 0) {
    throw "No portable build directories found: $BuildRoot"
}

$priorBuilds = [System.Collections.Generic.List[object]]::new()
$priorByDirectory = @{}
$previousVersion = $null
if (Test-Path -LiteralPath $OutputPath -PathType Leaf) {
    $priorLedger = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
    if ($priorLedger.schemaVersion -ne 1) {
        throw "Existing ledger has unsupported schemaVersion: $($priorLedger.schemaVersion)"
    }
    $priorRows = @($priorLedger.builds)
    foreach ($priorRow in $priorRows | Sort-Object buildNumber) {
        $number = [int]$priorRow.buildNumber
        $currentVersion = (ConvertTo-GraphReaderVersion -Version ([string]$priorRow.version)).Value
        $currentOrdinal = (ConvertTo-GraphReaderVersion -Version $currentVersion).Ordinal
        if ($number -ne $currentOrdinal) {
            throw "Existing ledger build number $number does not equal version '$currentVersion' ordinal $currentOrdinal."
        }
        if ($priorBuilds.Count -eq 0 -and ($number -ne 1 -or $currentVersion -cne '0.0.1')) {
            throw "Existing ledger first version must be 0.0.1, found '$currentVersion'."
        }
        if ($null -ne $previousVersion -and
            -not (Test-GraphReaderVersionTransition -FromVersion $previousVersion -ToVersion $currentVersion)) {
            throw "Existing ledger has an invalid version transition at build ${number}: '$previousVersion' -> '$currentVersion'."
        }
        $directoryName = Get-OptionalString -Object $priorRow -Name 'directory'
        if ($null -eq $directoryName -or $priorByDirectory.ContainsKey($directoryName)) {
            throw "Existing ledger directory identities are not unique."
        }
        $priorByDirectory[$directoryName] = $priorRow
        $priorBuilds.Add($priorRow)
        $previousVersion = $currentVersion
    }
}
$priorMaxBuildNumber = if ($priorBuilds.Count -eq 0) {
    0
}
else {
    [int]$priorBuilds[$priorBuilds.Count - 1].buildNumber
}
$lastAssignedVersion = $previousVersion
$computedRecords = [System.Collections.Generic.List[object]]::new()
$buildNumber = 0
$nextBuildNumber = $priorMaxBuildNumber + 1
foreach ($directory in $directories) {
    $isNewDirectory = -not $priorByDirectory.ContainsKey($directory.Name)
    if ($priorByDirectory.ContainsKey($directory.Name)) {
        $buildNumber = [int]$priorByDirectory[$directory.Name].buildNumber
        $assignedVersion = [string]$priorByDirectory[$directory.Name].version
    }
    else {
        $buildNumber = 0
        $assignedVersion = $null
    }
    $nameMatch = [regex]::Match(
        $directory.Name,
        '^(?<version>\d+\.\d+\.\d+)-(?<timestamp>\d{8}T\d{9}Z)-(?<short>[0-9a-fA-F]{8})$')
    if (-not $nameMatch.Success) {
        throw "Portable build directory name is invalid: $($directory.Name)"
    }
    $directoryVersion = $nameMatch.Groups['version'].Value
    $directoryTimestamp = $nameMatch.Groups['timestamp'].Value
    $directoryShortCommit = $nameMatch.Groups['short'].Value.ToLowerInvariant()
    $metadataPath = Join-Path $directory.FullName 'build-info.json'
    $missing = [System.Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        $missing.Add('build-info.json')
        $info = [pscustomobject]@{}
    }
    else {
        try {
            $info = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        }
        catch {
            $missing.Add('valid build-info.json')
            $info = [pscustomobject]@{}
        }
    }

    $stampedVersion = Get-OptionalString -Object $info -Name 'version'
    $commit = Get-OptionalString -Object $info -Name 'commit'
    $buildTimeUtc = Get-OptionalString -Object $info -Name 'buildTimeUtc'
    if ($null -ne $stampedVersion -and $stampedVersion -cne $directoryVersion) {
        throw "Stamped version '$stampedVersion' does not match directory '$($directory.Name)'."
    }
    if ($isNewDirectory) {
        if ($priorBuilds.Count -eq 0) {
            $buildNumber = $nextBuildNumber
            $nextBuildNumber++
            $assignedVersion = ConvertTo-BuildVersion -BuildNumber $buildNumber
        }
        else {
            $expectedSuccessor = Get-NextGraphReaderVersion -Version $lastAssignedVersion
            $candidateVersion = if ($null -eq $stampedVersion) { $expectedSuccessor } else { $stampedVersion }
            if (-not (Test-GraphReaderVersionTransition -FromVersion $lastAssignedVersion -ToVersion $candidateVersion)) {
                throw "New build '$($directory.Name)' has an invalid version transition from '$lastAssignedVersion' to '$candidateVersion'; it reuses a prior ordinal or skips the required successor '$expectedSuccessor'."
            }
            $assignedRecord = ConvertTo-GraphReaderVersion -Version $candidateVersion
            $assignedVersion = $assignedRecord.Value
            $buildNumber = $assignedRecord.Ordinal
            $nextBuildNumber = $buildNumber + 1
            $lastAssignedVersion = $assignedVersion
        }
    }
    $shortCommit = Get-OptionalString -Object $info -Name 'shortCommit'
    if ($null -ne $shortCommit -and $shortCommit.ToLowerInvariant() -cne $directoryShortCommit) {
        throw "Short commit '$shortCommit' does not match directory '$($directory.Name)'."
    }
    if ($null -ne $buildTimeUtc) {
        try {
            $parsedBuildTime = [DateTimeOffset]::Parse(
                $buildTimeUtc,
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::AssumeUniversal)
            $normalizedTimestamp = $parsedBuildTime.ToUniversalTime().ToString(
                "yyyyMMdd'T'HHmmssfff'Z'",
                [Globalization.CultureInfo]::InvariantCulture)
            if ($normalizedTimestamp -cne $directoryTimestamp) {
                throw "Build time '$buildTimeUtc' does not match directory '$($directory.Name)'."
            }
        }
        catch {
            if ($_.Exception.Message -like "Build time '* does not match directory '*") {
                throw
            }
            throw "Build time '$buildTimeUtc' is invalid for directory '$($directory.Name)'."
        }
    }

    $executablePath = Join-Path $directory.FullName 'GraphReader.App.exe'
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        $executableSha256 = $null
    }
    else {
        $actualExecutableSha256 = Get-Sha256 -Path $executablePath
        $recordedExecutableSha256 = Get-OptionalString -Object $info -Name 'executableSha256'
        if ($null -ne $recordedExecutableSha256 -and
            $recordedExecutableSha256.ToLowerInvariant() -cne $actualExecutableSha256) {
            throw "Executable SHA-256 metadata does not match: $($directory.Name)"
        }
        $executableSha256 = $actualExecutableSha256
    }
    $openCvRuntimeSha256 = Get-OpenCvRuntimeHash -BuildInfo $info
    $openCvPath = Join-Path $directory.FullName 'OpenCvSharpExtern.dll'
    if (Test-Path -LiteralPath $openCvPath -PathType Leaf) {
        $actualOpenCvRuntimeSha256 = Get-Sha256 -Path $openCvPath
        if ($null -ne $openCvRuntimeSha256 -and $openCvRuntimeSha256 -cne $actualOpenCvRuntimeSha256) {
            throw "OpenCV runtime SHA-256 metadata does not match: $($directory.Name)"
        }
        $openCvRuntimeSha256 = $actualOpenCvRuntimeSha256
    }
    foreach ($field in @(
            @{ Name = 'version'; Value = $stampedVersion },
            @{ Name = 'commit'; Value = $commit },
            @{ Name = 'buildTimeUtc'; Value = $buildTimeUtc },
            @{ Name = 'executableSha256'; Value = $executableSha256 })) {
        if ($null -eq $field.Value -and -not $missing.Contains($field.Name)) {
            $missing.Add($field.Name)
        }
    }

    $isRetained = $directory.Name -ceq $retainedDirectory
    $recordIncomplete = $missing.Count -gt 0
    $releaseEligible = if ($priorBuilds.Count -eq 0 -and $buildNumber -le 432) {
        ($buildNumber % 20) -eq 1
    }
    else {
        Test-GraphReaderReleaseVersion -Version $assignedVersion
    }
    $releaseStatus = if ($releaseEligible -and $buildNumber -le 432) {
        'missed-historical'
    }
    elseif ($releaseEligible) {
        'pending'
    }
    else {
        'internal'
    }
    $computedRecords.Add([ordered]@{
            buildNumber = $buildNumber
            version = $assignedVersion
            directory = $directory.Name
            stampedVersion = $stampedVersion
            commit = if ($null -eq $commit) { $null } else { $commit.ToLowerInvariant() }
            buildTimeUtc = $buildTimeUtc
            executableSha256 = if ($null -eq $executableSha256) { $null } else { $executableSha256.ToLowerInvariant() }
            openCvRuntimeSha256 = $openCvRuntimeSha256
            releaseEligible = $releaseEligible
            releaseStatus = $releaseStatus
            retained = $false
            recordIncomplete = $recordIncomplete
            missingMetadata = @($missing)
        })
}

$computedByDirectory = @{}
foreach ($computedRecord in $computedRecords) {
    $computedByDirectory[$computedRecord.directory] = $computedRecord
}
$records = [System.Collections.Generic.List[object]]::new()
foreach ($priorRecord in $priorBuilds) {
    $computed = $computedByDirectory[$priorRecord.directory]
    if ($null -ne $computed) {
        foreach ($fieldName in @('stampedVersion', 'commit', 'buildTimeUtc', 'executableSha256', 'openCvRuntimeSha256')) {
            $priorValue = Get-OptionalString -Object $priorRecord -Name $fieldName
            $currentValue = Get-OptionalString -Object $computed -Name $fieldName
            if ($null -ne $priorValue -and $priorValue.ToLowerInvariant() -cne ([string]$currentValue).ToLowerInvariant()) {
                throw "Existing ledger identity differs for '$($priorRecord.directory)' field '$fieldName'."
            }
        }
    }
    $records.Add($priorRecord)
}
foreach ($computedRecord in $computedRecords) {
    if (-not $priorByDirectory.ContainsKey($computedRecord.directory)) {
        $records.Add($computedRecord)
    }
}
foreach ($record in $records) {
    $record.retained = $record.directory -ceq $retainedDirectory
}
$retainedRecords = @($records | Where-Object { $_.retained })
if ($retainedRecords.Count -ne 1 -or $retainedRecords[0].directory -cne $retainedDirectory) {
    throw "Exactly one retained entry must match latest.json buildDirectory '$retainedDirectory'."
}
$retainedRecord = $retainedRecords[0]
foreach ($field in @(
        @{ Latest = 'version'; Record = 'stampedVersion'; Label = 'stamped version' },
        @{ Latest = 'commit'; Record = 'commit'; Label = 'commit' },
        @{ Latest = 'buildTimeUtc'; Record = 'buildTimeUtc'; Label = 'build time' },
        @{ Latest = 'executableSha256'; Record = 'executableSha256'; Label = 'executable SHA-256' })) {
    $latestValue = Get-OptionalString -Object $latest -Name $field.Latest
    $recordValue = Get-OptionalString -Object $retainedRecord -Name $field.Record
    if ($null -eq $latestValue -or $null -eq $recordValue -or
        $latestValue.ToLowerInvariant() -cne $recordValue.ToLowerInvariant()) {
        throw "latest.json $($field.Label) does not match the retained ledger record."
    }
}

$assigned = if ([string]::IsNullOrWhiteSpace($AssignedUtc)) {
    [DateTimeOffset]::UtcNow.ToString('o')
}
else {
    ([DateTimeOffset]::Parse($AssignedUtc, [Globalization.CultureInfo]::InvariantCulture)).ToUniversalTime().ToString('o')
}
$ledger = [ordered]@{
    schemaVersion = 1
    policy = 'one packaged application build consumes one build number; build number equals version ordinal; 2.0.0 promotion allocates build 20000 without intermediate records'
    assignedUtc = $assigned
    builds = @($records)
}
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
$json = ($ledger | ConvertTo-Json -Depth 10).Replace("`r`n", "`n")
[System.IO.File]::WriteAllText($resolvedOutput, $json + "`n", [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    outputPath = $resolvedOutput
    buildCount = $records.Count
    retainedDirectory = $retainedDirectory
    incompleteCount = @($records | Where-Object { $_.recordIncomplete }).Count
    releaseEligibleCount = @($records | Where-Object { $_.releaseEligible }).Count
}
