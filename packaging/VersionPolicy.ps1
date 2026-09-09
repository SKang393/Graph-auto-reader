# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

Set-StrictMode -Version Latest

function ConvertTo-GraphReaderVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Version
    )

    $match = [regex]::Match(
        $Version,
        '^(?<major>0|[1-9][0-9]?)\.(?<minor>0|[1-9][0-9]?)\.(?<build>0|[1-9][0-9]?)$')
    if (-not $match.Success) {
        throw "Version '$Version' must use x.y.z with each component from 0 through 99."
    }

    $major = [int]$match.Groups['major'].Value
    $minor = [int]$match.Groups['minor'].Value
    $build = [int]$match.Groups['build'].Value
    $ordinal = ($major * 10000) + ($minor * 100) + $build
    $value = "$major.$minor.$build"
    $cadenceEligible = ($ordinal % 20) -eq 1
    $stablePromotionRelease = $value -ceq (Get-GraphReaderStablePromotionVersion)
    $publicCadenceEligible = $ordinal -gt (Get-GraphReaderStablePromotionOrdinal) -and $cadenceEligible

    return [pscustomobject][ordered]@{
        Value = $value
        Major = $major
        Minor = $minor
        Build = $build
        Ordinal = $ordinal
        CadenceEligible = $cadenceEligible
        StablePromotionRelease = $stablePromotionRelease
        ReleaseEligible = $publicCadenceEligible -or $stablePromotionRelease
    }
}

function Get-GraphReaderStablePromotionVersion {
    return '2.0.0'
}

function Get-GraphReaderStablePromotionOrdinal {
    return 20000
}

function Test-GraphReaderStablePromotion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FromVersion,

        [Parameter(Mandatory)]
        [string]$ToVersion
    )

    $from = ConvertTo-GraphReaderVersion -Version $FromVersion
    $to = ConvertTo-GraphReaderVersion -Version $ToVersion
    return $from.Ordinal -lt (Get-GraphReaderStablePromotionOrdinal) -and
        $to.Value -ceq (Get-GraphReaderStablePromotionVersion)
}

function Get-NextGraphReaderVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Version
    )

    $current = ConvertTo-GraphReaderVersion -Version $Version
    if ($current.Ordinal -eq 999999) {
        throw 'Version 99.99.99 has no successor within the supported x.y.z range.'
    }

    $nextOrdinal = $current.Ordinal + 1
    $major = [Math]::Floor($nextOrdinal / 10000)
    $remainder = $nextOrdinal % 10000
    $minor = [Math]::Floor($remainder / 100)
    $build = $remainder % 100
    return "$major.$minor.$build"
}

function Test-GraphReaderVersionTransition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FromVersion,

        [Parameter(Mandatory)]
        [string]$ToVersion
    )

    if (Test-GraphReaderStablePromotion -FromVersion $FromVersion -ToVersion $ToVersion) {
        return $true
    }

    return (Get-NextGraphReaderVersion -Version $FromVersion) -ceq
        (ConvertTo-GraphReaderVersion -Version $ToVersion).Value
}

function Test-GraphReaderReleaseVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Version
    )

    return [bool](ConvertTo-GraphReaderVersion -Version $Version).ReleaseEligible
}

function Get-GraphReaderReleaseBuilds {
    return @(1, 21, 41, 61, 81)
}

function Get-GraphReaderVersionFromProjectXml {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Content,

        [string]$Description = 'Directory.Build.props'
    )

    [xml]$project = $Content
    $versionNodes = @($project.Project.PropertyGroup.Version | Where-Object { $_ })
    if ($versionNodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$versionNodes[0])) {
        throw "$Description must contain exactly one nonempty Version element."
    }

    $record = ConvertTo-GraphReaderVersion -Version ([string]$versionNodes[0])
    $assemblyVersion = [string]$project.Project.PropertyGroup.AssemblyVersion
    $fileVersion = [string]$project.Project.PropertyGroup.FileVersion
    $informationalVersion = [string]$project.Project.PropertyGroup.InformationalVersion
    if ($assemblyVersion -ne "$($record.Value).0" -or
        $fileVersion -ne "$($record.Value).0" -or
        $informationalVersion -ne $record.Value) {
        throw "$Description assembly, file, and informational versions must agree with Version '$($record.Value)'."
    }

    return [pscustomobject][ordered]@{
        Value = $record.Value
        Major = $record.Major
        Minor = $record.Minor
        Build = $record.Build
        Ordinal = $record.Ordinal
        CadenceEligible = $record.CadenceEligible
        StablePromotionRelease = $record.StablePromotionRelease
        ReleaseEligible = $record.ReleaseEligible
        Source = 'Directory.Build.props#Project/PropertyGroup/Version'
    }
}

function Get-GraphReaderCentralVersion {
    [CmdletBinding(DefaultParameterSetName = 'Repository')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Repository')]
        [string]$RepositoryRoot,

        [Parameter(Mandatory, ParameterSetName = 'Path')]
        [string]$Path
    )

    $versionPath = if ($PSCmdlet.ParameterSetName -eq 'Repository') {
        Join-Path $RepositoryRoot 'Directory.Build.props'
    }
    else {
        $Path
    }

    if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) {
        throw "Central version file is missing: $versionPath"
    }

    return Get-GraphReaderVersionFromProjectXml `
        -Content (Get-Content -LiteralPath $versionPath -Raw) `
        -Description $versionPath
}
