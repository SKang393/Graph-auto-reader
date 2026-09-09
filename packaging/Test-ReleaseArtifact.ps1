# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param(
    [string]$ManifestPath,
    [string]$ArtifactRoot,
    [string]$VersionFilePath,
    [string]$LocalizationReportPath,
    [switch]$RequireReleaseVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'VersionPolicy.ps1')

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $PSScriptRoot 'artifacts.json'
}

function Assert-Equal {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Actual,

        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Expected,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ($Actual -ne $Expected) {
        throw "$Description. Expected '$Expected', found '$Actual'."
    }
}

function Get-ExpectedNotoComponents {
    return @(
        [pscustomobject]@{
            Id = 'noto-sans-regular'
            Name = 'Noto Sans Regular'
            FileName = 'NotoSans-Regular.ttf'
            Sha256 = '478c558ea716033cd60c03438f628dfa75694dcf6b5f6d505a2f05fd2b4f3823'
        },
        [pscustomobject]@{
            Id = 'noto-sans-medium'
            Name = 'Noto Sans Medium'
            FileName = 'NotoSans-Medium.ttf'
            Sha256 = '635d93d1131d791f2576de90b3bb0f7cdf61929906e8420a61b5f7f8e76420bb'
        },
        [pscustomobject]@{
            Id = 'noto-sans-semibold'
            Name = 'Noto Sans SemiBold'
            FileName = 'NotoSans-SemiBold.ttf'
            Sha256 = 'a4e91fd530ac2b4ef5367240144ff37d7d65d66cf76f2e9a2187b93c676f92d0'
        })
}

function Assert-NotoReleaseAuditMetadata {
    param(
        [Parameter(Mandatory)]
        [object]$ReleaseAudit,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $expected = @(Get-ExpectedNotoComponents)
    $components = @{}
    foreach ($component in @($ReleaseAudit.components)) {
        $components[[string]$component.id] = $component
    }

    foreach ($font in $expected) {
        if (-not $components.ContainsKey([string]$font.Id)) {
            throw "$Description is missing embedded component '$($font.Id)'."
        }

        $component = $components[[string]$font.Id]
        Assert-Equal -Actual ([string]$component.component) -Expected ([string]$font.Name) -Description "$Description component name differs for '$($font.Id)'"
        Assert-Equal -Actual ([string]$component.version) -Expected '2.015' -Description "$Description component version differs for '$($font.Id)'"
        Assert-Equal -Actual ([string]$component.license) -Expected 'OFL-1.1' -Description "$Description component license differs for '$($font.Id)'"
        Assert-Equal -Actual ([string]$component.checksumPolicy) -Expected 'exact-binary' -Description "$Description checksum policy differs for '$($font.Id)'"
        Assert-Equal -Actual ([string]$component.artifactSha256).ToLowerInvariant() -Expected ([string]$font.Sha256) -Description "$Description checksum differs for '$($font.Id)'"
        Assert-Equal -Actual ([string]$component.reviewStatus) -Expected 'reviewed' -Description "$Description review status differs for '$($font.Id)'"
        Assert-Equal -Actual ([bool]$component.commercialUse) -Expected $true -Description "$Description commercial-use status differs for '$($font.Id)'"
        Assert-Equal -Actual ([bool]$component.redistribution) -Expected $true -Description "$Description redistribution status differs for '$($font.Id)'"
        if ([string]$component.source -notmatch [regex]::Escape("/66c4b351c58f99ace5a6265d329080d74b057909/fonts/NotoSans/hinted/ttf/$($font.FileName)")) {
            throw "$Description source differs for '$($font.Id)'."
        }
        if ([string]$component.sourceRevision -notmatch 'noto-monthly-release-2026\.05\.01' -or
            [string]$component.sourceRevision -notmatch '66c4b351c58f99ace5a6265d329080d74b057909') {
            throw "$Description source revision differs for '$($font.Id)'."
        }

        [string[]]$noticePaths = @($component.noticePaths | ForEach-Object { [string]$_ })
        [Array]::Sort($noticePaths, [StringComparer]::Ordinal)
        Assert-Equal `
            -Actual ($noticePaths -join ';') `
            -Expected 'LICENSES/NotoSans-OFL-1.1.txt;THIRD_PARTY_NOTICES.md' `
            -Description "$Description notice paths differ for '$($font.Id)'"
    }

    $fontRules = @($ReleaseAudit.binaryCoverage.rules | Where-Object {
            [string]$_.pattern -ceq 'GraphReader.App.dll'
        })
    if ($fontRules.Count -ne 1 -or [string]$fontRules[0].componentId -ne 'graph-auto-reader') {
        throw "$Description must bind embedded Noto components to GraphReader.App.dll."
    }
    [string[]]$expectedIds = @($expected.Id)
    [string[]]$actualIds = @($fontRules[0].embeddedComponentIds | ForEach-Object { [string]$_ })
    [Array]::Sort($expectedIds, [StringComparer]::Ordinal)
    [Array]::Sort($actualIds, [StringComparer]::Ordinal)
    Assert-Equal `
        -Actual ($actualIds -join ';') `
        -Expected ($expectedIds -join ';') `
        -Description "$Description embedded Noto component binding differs"
}

function Assert-NotoGeneratedMetadata {
    param(
        [Parameter(Mandatory)]
        [object]$Metadata,

        [Parameter(Mandatory)]
        [string]$ApplicationSha256
    )

    $expected = @(Get-ExpectedNotoComponents)
    $records = @($Metadata.embeddedComponents)
    Assert-Equal -Actual $records.Count -Expected $expected.Count -Description 'Release metadata embedded Noto component count differs'
    foreach ($font in $expected) {
        $matches = @($records | Where-Object { [string]$_.id -ceq [string]$font.Id })
        if ($matches.Count -ne 1) {
            throw "Release metadata requires exactly one embedded component '$($font.Id)'."
        }
        $record = $matches[0]
        Assert-Equal -Actual ([string]$record.containerPath) -Expected 'GraphReader.App.dll' -Description "Release metadata container path differs for '$($font.Id)'"
        Assert-Equal -Actual ([string]$record.containerSha256).ToLowerInvariant() -Expected $ApplicationSha256 -Description "Release metadata container checksum differs for '$($font.Id)'"
        Assert-Equal -Actual ([string]$record.component) -Expected ([string]$font.Name) -Description "Release metadata component name differs for '$($font.Id)'"
        Assert-Equal -Actual ([string]$record.version) -Expected '2.015' -Description "Release metadata component version differs for '$($font.Id)'"
        Assert-Equal -Actual ([string]$record.license) -Expected 'OFL-1.1' -Description "Release metadata component license differs for '$($font.Id)'"
        Assert-Equal -Actual ([string]$record.artifactSha256).ToLowerInvariant() -Expected ([string]$font.Sha256) -Description "Release metadata component checksum differs for '$($font.Id)'"
        Assert-Equal -Actual ([string]$record.checksumPolicy) -Expected 'exact-binary' -Description "Release metadata checksum policy differs for '$($font.Id)'"
        if ([string]$record.source -notmatch [regex]::Escape("/66c4b351c58f99ace5a6265d329080d74b057909/fonts/NotoSans/hinted/ttf/$($font.FileName)")) {
            throw "Release metadata source differs for '$($font.Id)'."
        }
        if ([string]$record.sourceRevision -notmatch 'noto-monthly-release-2026\.05\.01' -or
            [string]$record.sourceRevision -notmatch '66c4b351c58f99ace5a6265d329080d74b057909') {
            throw "Release metadata source revision differs for '$($font.Id)'."
        }
        [string[]]$noticePaths = @($record.noticePaths | ForEach-Object { [string]$_ })
        [Array]::Sort($noticePaths, [StringComparer]::Ordinal)
        Assert-Equal -Actual ($noticePaths -join ';') -Expected 'LICENSES/NotoSans-OFL-1.1.txt;THIRD_PARTY_NOTICES.md' -Description "Release metadata notice paths differ for '$($font.Id)'"
    }

    [string[]]$expectedIds = @($expected.Id)
    [Array]::Sort($expectedIds, [StringComparer]::Ordinal)
    foreach ($distribution in @($Metadata.installer, $Metadata.portable)) {
        if ($null -eq $distribution.PSObject.Properties['provenance']) {
            throw 'Release metadata distribution provenance is missing for embedded Noto components.'
        }
        [string[]]$actualIds = @($distribution.provenance.embeddedComponentIds | ForEach-Object { [string]$_ })
        [Array]::Sort($actualIds, [StringComparer]::Ordinal)
        Assert-Equal -Actual ($actualIds -join ';') -Expected ($expectedIds -join ';') -Description 'Release metadata embedded Noto identities differ between distribution forms'
        $payloads = @($distribution.provenance.fontBearingPayloads)
        if ($payloads.Count -ne 1 -or
            [string]$payloads[0].path -cne 'GraphReader.App.dll' -or
            [string]$payloads[0].sha256 -ine $ApplicationSha256) {
            throw 'Release metadata font-bearing application payload differs between distribution forms.'
        }
        [string[]]$payloadIds = @($payloads[0].embeddedComponentIds | ForEach-Object { [string]$_ })
        [Array]::Sort($payloadIds, [StringComparer]::Ordinal)
        Assert-Equal -Actual ($payloadIds -join ';') -Expected ($expectedIds -join ';') -Description 'Release metadata font-bearing payload component identities differ'
    }
}

function Assert-NoDuplicateJsonPropertyNames {
    param(
        [Parameter(Mandatory)]
        [string]$Json,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $tokenPattern = '"(?:\\(?:["\\/bfnrt]|u[0-9A-Fa-f]{4})|[^"\\\x00-\x1F])*"|[{}\[\],:]'
    $contexts = [System.Collections.Generic.List[object]]::new()
    foreach ($match in [regex]::Matches($Json, $tokenPattern)) {
        $token = [string]$match.Value
        $current = if ($contexts.Count -gt 0) { $contexts[$contexts.Count - 1] } else { $null }
        switch ($token) {
            '{' {
                if ($null -ne $current -and $current.Kind -eq 'object' -and $current.State -eq 'value') {
                    $current.State = 'after-value'
                }
                $contexts.Add([pscustomobject]@{
                        Kind = 'object'
                        State = 'key'
                        Names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                    })
                continue
            }
            '[' {
                if ($null -ne $current -and $current.Kind -eq 'object' -and $current.State -eq 'value') {
                    $current.State = 'after-value'
                }
                $contexts.Add([pscustomobject]@{ Kind = 'array'; State = ''; Names = $null })
                continue
            }
            '}' {
                if ($contexts.Count -gt 0) {
                    $contexts.RemoveAt($contexts.Count - 1)
                }
                continue
            }
            ']' {
                if ($contexts.Count -gt 0) {
                    $contexts.RemoveAt($contexts.Count - 1)
                }
                continue
            }
            ':' {
                if ($null -ne $current -and $current.Kind -eq 'object' -and $current.State -eq 'colon') {
                    $current.State = 'value'
                }
                continue
            }
            ',' {
                if ($null -ne $current -and $current.Kind -eq 'object' -and
                    $current.State -in @('value', 'after-value')) {
                    $current.State = 'key'
                }
                continue
            }
        }

        if ($token.StartsWith('"', [StringComparison]::Ordinal) -and
            $null -ne $current -and $current.Kind -eq 'object') {
            if ($current.State -eq 'key') {
                $propertyName = [string]($token | ConvertFrom-Json)
                if (-not $current.Names.Add($propertyName)) {
                    throw "$Description contains duplicate JSON property '$propertyName'."
                }
                $current.State = 'colon'
            }
            elseif ($current.State -eq 'value') {
                $current.State = 'after-value'
            }
        }
    }
}

function Assert-RelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$RelativePath,

        [Parameter(Mandatory)]
        [string]$Description,

        [switch]$RequireFile
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "$Description must use a nonempty relative path: $RelativePath"
    }

    $rootFullPath = [System.IO.Path]::GetFullPath($Root)
    $resolvedPath = [System.IO.Path]::GetFullPath((Join-Path $rootFullPath $RelativePath))
    $rootPrefix = $rootFullPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $resolvedPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description leaves its allowed root: $RelativePath"
    }

    if ($RequireFile -and -not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "$Description is missing: $resolvedPath"
    }

    return $resolvedPath
}

function Assert-MandatoryReleaseEvidenceGates {
    param(
        [Parameter(Mandatory)]
        [object]$ReleaseAudit,

        [Parameter(Mandatory)]
        [string]$Description,

        [string]$RepositoryRoot
    )

    $gatesProperty = $ReleaseAudit.PSObject.Properties['mandatoryEvidenceGates']
    if ($null -eq $gatesProperty -or
        $gatesProperty.Value -isnot [System.Array] -or
        @($gatesProperty.Value).Count -eq 0) {
        throw "$Description requires a nonempty mandatoryEvidenceGates array."
    }

    $gateIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($gate in @($gatesProperty.Value)) {
        $gateId = [string]$gate.id
        if ([string]::IsNullOrWhiteSpace($gateId) -or -not $gateIds.Add($gateId)) {
            throw "$Description contains an empty or duplicate mandatory evidence gate id '$gateId'."
        }
        if ([string]$gate.status -ne 'pass') {
            throw "$Description mandatory evidence gate '$gateId' is not pass."
        }
        if ([string]::IsNullOrWhiteSpace([string]$gate.description) -or
            [string]::IsNullOrWhiteSpace([string]$gate.notes) -or
            $gate.evidence -isnot [System.Array] -or
            @($gate.evidence).Count -eq 0) {
            throw "$Description mandatory evidence gate '$gateId' lacks its direct evidence contract."
        }

        foreach ($evidence in @($gate.evidence)) {
            $evidencePath = [string]$evidence.path
            $evidenceSha256 = [string]$evidence.sha256
            if ([string]::IsNullOrWhiteSpace($evidencePath) -or
                [System.IO.Path]::IsPathRooted($evidencePath) -or
                $evidenceSha256 -notmatch '^[a-fA-F0-9]{64}$') {
                throw "$Description mandatory evidence gate '$gateId' has an invalid evidence record."
            }

            if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
                $resolvedEvidencePath = Assert-RelativePath `
                    -Root $RepositoryRoot `
                    -RelativePath $evidencePath `
                    -Description "$Description mandatory evidence gate '$gateId'" `
                    -RequireFile
                Assert-Equal `
                    -Actual (Get-Sha256 -Path $resolvedEvidencePath) `
                    -Expected $evidenceSha256.ToLowerInvariant() `
                    -Description "$Description mandatory evidence gate '$gateId' checksum differs"
            }
        }
    }
}

function Assert-BuildVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Version,

        [switch]$ReleaseRequired
    )

    $record = ConvertTo-GraphReaderVersion -Version $Version
    if ($ReleaseRequired -and -not $record.ReleaseEligible) {
        throw "Version '$Version' is an internal checkpoint and cannot be published. Public releases begin with the explicit 2.0.0 milestone and then follow the twentieth-checkpoint cadence."
    }
}

function Get-CentralVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    return (Get-GraphReaderCentralVersion -Path $Path).Value
}

function Assert-LocalizationReport {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Localization completeness report is missing: $Path"
    }

    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Assert-Equal -Actual ([string]$report.status) -Expected 'pass' -Description 'Localization audit status is invalid'

    foreach ($countName in @('missing_keys', 'duplicate_keys', 'unresolved_resource_references')) {
        $property = $report.counts.PSObject.Properties[$countName]
        if ($null -eq $property) {
            throw "Localization completeness report is missing counts.$countName."
        }

        Assert-Equal -Actual ([int]$property.Value) -Expected 0 -Description "Localization audit count '$countName' is nonzero"
    }
}

function Invoke-LocalizationAudit {
    param(
        [Parameter(Mandatory)]
        [string]$AuditScriptPath,

        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$ReportPath
    )

    $arguments = @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        ('"' + $AuditScriptPath + '"'),
        '-RepositoryRoot',
        ('"' + $RepositoryRoot + '"'),
        '-ReportPath',
        ('"' + $ReportPath + '"'),
        '-FailOnExtraKeys')
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'powershell.exe'
    $startInfo.Arguments = $arguments -join ' '
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    $process = [System.Diagnostics.Process]::Start($startInfo)
    $standardOutput = $process.StandardOutput.ReadToEnd()
    $standardError = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "Localization audit failed with exit code $($process.ExitCode). $standardOutput$standardError"
    }
}

function Get-ArchiveEntryNames {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$Archive
    )

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $names = [System.Collections.Generic.List[string]]::new()

    foreach ($entry in $Archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($name) -or $name.EndsWith('/', [System.StringComparison]::Ordinal)) {
            continue
        }

        if ($name.StartsWith('/', [System.StringComparison]::Ordinal) -or
            $name -match '^[A-Za-z]:' -or
            @($name.Split('/')) -contains '..') {
            throw "Portable archive contains an unsafe path: $name"
        }

        if (-not $seen.Add($name)) {
            throw "Portable archive contains a duplicate path: $name"
        }

        $names.Add($name)
    }

    return @($names)
}

function Assert-NoForbiddenArchiveEntries {
    param(
        [Parameter(Mandatory)]
        [string[]]$EntryNames
    )

    $forbiddenFilePatterns = @(
        '(?i)(^|/)AGENTS\.md$',
        '(?i)(^|/)CODEX_START_HERE\.md$',
        '(?i)(^|/)CODEX_GOAL_[^/]*\.md$',
        '(?i)(^|/)DOCUMENT_MAP\.md$',
        '(?i)\.(pdb|user|suo|tmp|bak|autosave|ckpt|pth|pt|safetensors)$'
    )
    $forbiddenSegments = @('.agents', '.codex', '.git', '.omo', 'TestResults', 'private', 'cache', 'autosave', 'recovery', 'obj')

    foreach ($entryName in $EntryNames) {
        foreach ($pattern in $forbiddenFilePatterns) {
            if ($entryName -match $pattern) {
                throw "Portable archive contains a forbidden file: $entryName"
            }
        }

        $segments = @($entryName.Split('/'))
        foreach ($segment in $forbiddenSegments) {
            if ($segments -contains $segment) {
                throw "Portable archive contains a forbidden path segment '$segment': $entryName"
            }
        }
    }
}

function Get-RequiredContentArchivePaths {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [object[]]$RequiredContent
    )

    $paths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($content in $RequiredContent) {
        $sourceRelativePath = ([string]$content.source).Replace('\', '/')
        $targetRelativePath = ([string]$content.target).Replace('\', '/').TrimEnd('/')
        if ([string]::IsNullOrWhiteSpace($targetRelativePath) -or
            [System.IO.Path]::IsPathRooted($targetRelativePath) -or
            @($targetRelativePath.Split('/')) -contains '..') {
            throw "Common required-content target is unsafe: $targetRelativePath"
        }

        $sourcePath = Assert-RelativePath `
            -Root $RepositoryRoot `
            -RelativePath $sourceRelativePath `
            -Description 'Common required-content source'
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            throw "Common required-content source is missing: $sourceRelativePath"
        }

        $sourceItem = Get-Item -LiteralPath $sourcePath
        if (-not $sourceItem.PSIsContainer) {
            $null = $paths.Add($targetRelativePath)
            continue
        }

        $sourceRoot = [System.IO.Path]::GetFullPath($sourcePath)
        foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -File -Recurse) {
            $relativePath = $file.FullName.Substring($sourceRoot.Length).TrimStart('\', '/').Replace('\', '/')
            $null = $paths.Add("$targetRelativePath/$relativePath")
        }
    }

    return @($paths)
}

function Assert-PortableArchiveAllowlist {
    param(
        [Parameter(Mandatory)]
        [string[]]$EntryNames,

        [Parameter(Mandatory)]
        [string[]]$RequiredContentPaths,

        [Parameter(Mandatory)]
        [string[]]$ModelArtifactPaths,

        [Parameter(Mandatory)]
        [string[]]$ApplicationPublishPaths
    )

    $allowedExactPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($path in @(
            $RequiredContentPaths
            $ModelArtifactPaths
            $ApplicationPublishPaths
            'portable.mode'
            'build-metadata.json')) {
        $null = $allowedExactPaths.Add([string]$path)
    }

    foreach ($entryName in $EntryNames) {
        if (-not $allowedExactPaths.Contains($entryName)) {
            throw "Portable archive entry is outside the release payload allowlist: $entryName"
        }
    }
}

function Get-ApplicationPublishArchivePaths {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string[]]$EntryNames
    )

    $buildMetadata = (Get-ZipEntryText -Archive $Archive -EntryName 'build-metadata.json') | ConvertFrom-Json
    if ([string]$buildMetadata.runtimeMode -cne 'Production' -or
        [int]$buildMetadata.productionRuntimeSmokeExitCode -ne 0) {
        throw 'Build metadata does not contain a passing direct compiled Production runtime probe.'
    }

    $paths = @($buildMetadata.applicationPublishFiles | ForEach-Object { ([string]$_).Replace('\', '/') })
    if ($paths.Count -eq 0 -or $paths -notcontains 'GraphReader.App.exe') {
        throw 'Build metadata requires a nonempty application publish allowlist containing GraphReader.App.exe.'
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($path in $paths) {
        $isApprovedRootFile = $path -notmatch '/' -and (
            [System.IO.Path]::GetExtension($path) -in @('.exe', '.dll') -or
            $path -in @('GraphReader.App.deps.json', 'GraphReader.App.runtimeconfig.json'))
        $isApprovedSatelliteAssembly = $path -match '^(cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hans|zh-Hant)/[^/]+\.resources\.dll$'
        if (-not $seen.Add($path) -or
            (-not $isApprovedRootFile -and -not $isApprovedSatelliteAssembly) -or
            $EntryNames -cnotcontains $path) {
            throw "Build metadata contains an invalid or missing application publish path: $path"
        }
    }

    return @($paths)
}

function Get-Sha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        return Get-StreamSha256 -Stream $stream
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-WindowsX64ExecutableStream {
    param(
        [Parameter(Mandatory)]
        [System.IO.Stream]$Stream,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $buffer = [System.IO.MemoryStream]::new()
    try {
        $Stream.CopyTo($buffer)
        [byte[]]$bytes = $buffer.ToArray()
    }
    finally {
        $buffer.Dispose()
    }

    if ($bytes.Length -lt 512 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        throw "$Description is not a complete Windows PE executable."
    }

    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0x40 -or $peOffset -gt $bytes.Length - 24 -or
        $bytes[$peOffset] -ne 0x50 -or $bytes[$peOffset + 1] -ne 0x45 -or
        $bytes[$peOffset + 2] -ne 0 -or $bytes[$peOffset + 3] -ne 0) {
        throw "$Description has an invalid PE header."
    }

    $coffOffset = $peOffset + 4
    $machine = [System.BitConverter]::ToUInt16($bytes, $coffOffset)
    $sectionCount = [System.BitConverter]::ToUInt16($bytes, $coffOffset + 2)
    $optionalHeaderSize = [System.BitConverter]::ToUInt16($bytes, $coffOffset + 16)
    $characteristics = [System.BitConverter]::ToUInt16($bytes, $coffOffset + 18)
    if ($machine -ne 0x8664 -or
        $sectionCount -eq 0 -or $sectionCount -gt 96 -or
        $optionalHeaderSize -lt 0xf0 -or
        ($characteristics -band 0x0002) -eq 0 -or
        ($characteristics -band 0x2000) -ne 0) {
        throw "$Description is not an AMD64 executable image."
    }

    $optionalOffset = $coffOffset + 20
    $sectionTableOffset = $optionalOffset + $optionalHeaderSize
    if ($sectionTableOffset -gt $bytes.Length - (40 * $sectionCount) -or
        [System.BitConverter]::ToUInt16($bytes, $optionalOffset) -ne 0x20b) {
        throw "$Description does not contain a sane PE32+ optional header."
    }

    $entryPoint = [System.BitConverter]::ToUInt32($bytes, $optionalOffset + 16)
    $sectionAlignment = [System.BitConverter]::ToUInt32($bytes, $optionalOffset + 32)
    $fileAlignment = [System.BitConverter]::ToUInt32($bytes, $optionalOffset + 36)
    $sizeOfImage = [System.BitConverter]::ToUInt32($bytes, $optionalOffset + 56)
    $sizeOfHeaders = [System.BitConverter]::ToUInt32($bytes, $optionalOffset + 60)
    if ($entryPoint -eq 0 -or
        $fileAlignment -eq 0 -or $fileAlignment -gt 65536 -or
        $sectionAlignment -lt $fileAlignment -or
        $sizeOfImage -eq 0 -or
        $sizeOfHeaders -eq 0 -or $sizeOfHeaders -gt $bytes.Length) {
        throw "$Description has invalid PE32+ image dimensions or entry point."
    }

    $entryPointIsExecutable = $false
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $sectionOffset = $sectionTableOffset + (40 * $index)
        $virtualSize = [System.BitConverter]::ToUInt32($bytes, $sectionOffset + 8)
        $virtualAddress = [System.BitConverter]::ToUInt32($bytes, $sectionOffset + 12)
        $rawSize = [System.BitConverter]::ToUInt32($bytes, $sectionOffset + 16)
        $rawPointer = [System.BitConverter]::ToUInt32($bytes, $sectionOffset + 20)
        $sectionCharacteristics = [System.BitConverter]::ToUInt32($bytes, $sectionOffset + 36)
        if ($rawSize -gt 0 -and
            ([uint64]$rawPointer + [uint64]$rawSize) -gt [uint64]$bytes.Length) {
            throw "$Description contains a section outside the executable file."
        }

        $mappedSize = [Math]::Max([uint64]$virtualSize, [uint64]$rawSize)
        if (($sectionCharacteristics -band 0x20000000) -ne 0 -and
            [uint64]$entryPoint -ge [uint64]$virtualAddress -and
            [uint64]$entryPoint -lt ([uint64]$virtualAddress + $mappedSize)) {
            $entryPointIsExecutable = $true
        }
    }

    if (-not $entryPointIsExecutable) {
        throw "$Description entry point is not contained in an executable section."
    }
}

function Get-StreamSha256 {
    param(
        [Parameter(Mandatory)]
        [System.IO.Stream]$Stream
    )

    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($algorithm.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-PayloadDigest {
    param(
        [Parameter(Mandatory)]
        [object[]]$Records
    )

    $sortedRecords = @(Sort-PayloadRecordsOrdinal -Records $Records)
    $lines = @($sortedRecords | ForEach-Object { "$($_.sha256)  $($_.path)" })
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    $stream = [System.IO.MemoryStream]::new($bytes, $false)
    try {
        return Get-StreamSha256 -Stream $stream
    }
    finally {
        $stream.Dispose()
    }
}

function Sort-PayloadRecordsOrdinal {
    param(
        [Parameter(Mandatory)]
        [object[]]$Records
    )

    [object[]]$sorted = @($Records)
    $comparison = [System.Comparison[object]]{
        param($left, $right)
        return [System.StringComparer]::Ordinal.Compare([string]$left.path, [string]$right.path)
    }
    [System.Array]::Sort($sorted, $comparison)
    return @($sorted)
}

function Get-DirectoryPayloadRecords {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [string[]]$Exclude = @()
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        throw "Release staging directory is missing: $Root"
    }

    $rootFullPath = [System.IO.Path]::GetFullPath($Root)
    $records = @(
        Get-ChildItem -LiteralPath $rootFullPath -File -Recurse | ForEach-Object {
            $relativePath = $_.FullName.Substring($rootFullPath.Length).TrimStart('\', '/').Replace('\', '/')
            if ($Exclude -notcontains $relativePath) {
                [pscustomobject]@{
                    path = $relativePath
                    size = [long]$_.Length
                    sha256 = Get-Sha256 -Path $_.FullName
                }
            }
        }
    )
    return @(Sort-PayloadRecordsOrdinal -Records $records)
}

function Get-ZipPayloadRecords {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$Archive,

        [string[]]$Exclude = @()
    )

    $records = @(
        $Archive.Entries | ForEach-Object {
            $name = $_.FullName.Replace('\', '/')
            if (-not [string]::IsNullOrWhiteSpace($name) -and
                -not $name.EndsWith('/', [System.StringComparison]::Ordinal) -and
                $Exclude -notcontains $name) {
                $stream = $_.Open()
                try {
                    [pscustomobject]@{
                        path = $name
                        size = [long]$_.Length
                        sha256 = Get-StreamSha256 -Stream $stream
                    }
                }
                finally {
                    $stream.Dispose()
                }
            }
        }
    )
    return @(Sort-PayloadRecordsOrdinal -Records $records)
}

function Assert-PayloadRecordsEqual {
    param(
        [Parameter(Mandatory)]
        [object[]]$Actual,

        [Parameter(Mandatory)]
        [object[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Description
    )

    Assert-Equal -Actual $Actual.Count -Expected $Expected.Count -Description "$Description file count differs"
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        Assert-Equal -Actual ([string]$Actual[$index].path) -Expected ([string]$Expected[$index].path) -Description "$Description path differs at index $index"
        Assert-Equal -Actual ([long]$Actual[$index].size) -Expected ([long]$Expected[$index].size) -Description "$Description size differs for $($Expected[$index].path)"
        Assert-Equal -Actual ([string]$Actual[$index].sha256) -Expected ([string]$Expected[$index].sha256) -Description "$Description checksum differs for $($Expected[$index].path)"
    }
}

function Get-ZipEntryText {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string]$EntryName
    )

    $matchingEntries = @($Archive.Entries | Where-Object {
            $_.FullName.Replace('\', '/') -eq $EntryName
        })
    if ($matchingEntries.Count -ne 1) {
        throw "Portable archive is missing required entry: $EntryName"
    }
    $entry = $matchingEntries[0]

    $stream = $entry.Open()
    $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true)
    try {
        return $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

function Assert-LicenseBundle {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string[]]$EntryNames
    )

    $notice = Get-ZipEntryText -Archive $Archive -EntryName 'THIRD_PARTY_NOTICES.md'
    $referencedLicenses = @(
        [regex]::Matches($notice, '(?i)LICENSES/[A-Za-z0-9._+\-]+(?:\.[A-Za-z0-9._+\-]+)*') |
            ForEach-Object { $_.Value.Replace('\', '/') } |
            Sort-Object -Unique
    )
    if ($referencedLicenses.Count -eq 0) {
        throw 'THIRD_PARTY_NOTICES.md does not reference any exact LICENSES path.'
    }

    $bundledLicenses = @($EntryNames | Where-Object { $_ -like 'LICENSES/*' } | Sort-Object -Unique)
    foreach ($licensePath in $referencedLicenses) {
        if ($bundledLicenses -notcontains $licensePath) {
            throw "THIRD_PARTY_NOTICES.md references a missing license file: $licensePath"
        }
    }
    foreach ($licensePath in $bundledLicenses) {
        if ($referencedLicenses -notcontains $licensePath) {
            throw "Portable archive contains an unreferenced license file: $licensePath"
        }
    }
}

function Assert-ModelManifests {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string[]]$EntryNames
    )

    $indexPath = 'models/production-model-index.json'
    $indexJson = Get-ZipEntryText -Archive $Archive -EntryName $indexPath
    Assert-NoDuplicateJsonPropertyNames -Json $indexJson -Description 'Production model package index'
    $index = $indexJson | ConvertFrom-Json
    if (@($index.PSObject.Properties.Name | Sort-Object) -join '|' -ne 'models|schema_version') {
        throw 'Production model package index has unsupported or missing root properties.'
    }
    Assert-Equal -Actual ([int]$index.schema_version) -Expected 1 -Description 'Production model package index version is invalid'
    if ($index.models -isnot [System.Array] -or @($index.models).Count -eq 0) {
        throw 'Production model package index must contain at least one model.'
    }

    $modelArtifactPaths = [System.Collections.Generic.List[string]]::new()
    $modelArtifactPaths.Add($indexPath)
    $identities = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($packageModel in @($index.models)) {
        if (@($packageModel.PSObject.Properties.Name | Sort-Object) -join '|' -ne
            'benchmark_evidence|manifest|model_id|model_version|notice|payloads') {
            throw 'Production model package entry has unsupported or missing properties.'
        }
        $modelId = [string]$packageModel.model_id
        $modelVersion = [string]$packageModel.model_version
        if ([string]::IsNullOrWhiteSpace($modelId) -or [string]::IsNullOrWhiteSpace($modelVersion) -or
            -not $identities.Add("$modelId`0$modelVersion")) {
            throw "Production model package contains an invalid or duplicate identity: $modelId $modelVersion"
        }

        $resources = @($packageModel.manifest, $packageModel.notice, $packageModel.benchmark_evidence) + @($packageModel.payloads)
        foreach ($resource in $resources) {
            $resourceNames = @($resource.PSObject.Properties.Name | Sort-Object) -join '|'
            if ($resourceNames -notin @('path|sha256', 'declared_path|path|sha256') -or
                [string]$resource.path -match '(^|/)(\.|\.\.)(/|$)' -or
                [IO.Path]::IsPathRooted([string]$resource.path) -or
                [string]$resource.sha256 -notmatch '^[a-fA-F0-9]{64}$') {
                throw "Production model package contains an invalid resource for '$modelId'."
            }
            $archivePath = "models/$([string]$resource.path)"
            $matchingEntries = @($Archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -ceq $archivePath })
            if ($matchingEntries.Count -ne 1) {
                throw "Production model package resource must exist exactly once: $archivePath"
            }
            $stream = $matchingEntries[0].Open()
            try { $actualHash = Get-StreamSha256 -Stream $stream } finally { $stream.Dispose() }
            Assert-Equal -Actual $actualHash -Expected ([string]$resource.sha256).ToLowerInvariant() -Description "Production model package checksum differs for '$archivePath'"
            $modelArtifactPaths.Add($archivePath)
        }

        $expectedManifestPath = "manifest/$modelId/$modelVersion/manifest.json"
        Assert-Equal -Actual ([string]$packageModel.manifest.path) -Expected $expectedManifestPath -Description 'Production model manifest path is not canonical'
        $manifestArchivePath = "models/$expectedManifestPath"
        $manifestJson = Get-ZipEntryText -Archive $Archive -EntryName $manifestArchivePath
        Assert-NoDuplicateJsonPropertyNames -Json $manifestJson -Description "Model manifest '$manifestArchivePath'"
        $manifest = $manifestJson | ConvertFrom-Json
        $allowedManifestProperties = @(
            'manifest_version', 'model_id', 'model_version', 'task', 'source', 'license', 'sha256',
            'files', 'inputs', 'outputs', 'preprocessing', 'postprocessing', 'commercial_use',
            'redistribution', 'providers', 'benchmarks')
        $requiredManifestProperties = @(
            'manifest_version', 'model_id', 'model_version', 'task', 'source', 'license', 'sha256',
            'files', 'inputs', 'outputs', 'commercial_use', 'redistribution', 'providers', 'benchmarks')
        if (@($manifest.PSObject.Properties.Name | Where-Object { $_ -notin $allowedManifestProperties }).Count -gt 0 -or
            @($requiredManifestProperties | Where-Object { $_ -notin $manifest.PSObject.Properties.Name }).Count -gt 0) {
            throw "Model manifest '$manifestArchivePath' does not match the production root contract."
        }
        Assert-Equal -Actual ([string]$manifest.model_id) -Expected $modelId -Description 'Packaged model identity differs from index'
        Assert-Equal -Actual ([string]$manifest.model_version) -Expected $modelVersion -Description 'Packaged model version differs from index'
        if (-not [bool]$manifest.license.reviewed -or -not [bool]$manifest.commercial_use -or
            -not [bool]$manifest.redistribution -or @($manifest.providers) -notcontains 'cpu') {
            throw "Model manifest '$manifestArchivePath' is not approved for commercial redistribution with offline CPU fallback."
        }
        if ([string]$manifest.license.spdx -match '(?i)(AGPL|GPL|SSPL|BUSL|non[- ]commercial|unknown|unclear)') {
            throw "Model manifest '$manifestArchivePath' uses a prohibited or unclear license."
        }

        $declaredNoticePath = (([string]$manifest.license.notice_path).Replace('\', '/'))
        if ([string]::IsNullOrWhiteSpace($declaredNoticePath) -or [IO.Path]::IsPathRooted($declaredNoticePath) -or
            @($declaredNoticePath.Split('/')) -contains '..') {
            throw "Model manifest '$manifestArchivePath' uses an unsafe notice path: $declaredNoticePath"
        }
        Assert-Equal -Actual ([string]$packageModel.notice.declared_path) -Expected $declaredNoticePath -Description 'Packaged model notice declaration differs from manifest'
        $expectedNoticePath = "notices/$modelId/$modelVersion/$([IO.Path]::GetFileName($declaredNoticePath))"
        Assert-Equal -Actual ([string]$packageModel.notice.path) -Expected $expectedNoticePath -Description "Production model notice path is not canonical for '$modelId'"

        $approvals = @($manifest.benchmarks | Where-Object {
                $_.production_approval -is [bool] -and [bool]$_.production_approval -and
                $_.release_eligible -is [bool] -and [bool]$_.release_eligible -and
                $_.status -is [string] -and [string]$_.status -ieq 'pass'
            })
        if ($approvals.Count -ne 1) {
            throw "Model manifest '$manifestArchivePath' has no passing release-eligible benchmark with production approval."
        }
        Assert-Equal -Actual ([string]$packageModel.benchmark_evidence.declared_path) -Expected (([string]$approvals[0].evidence_path).Replace('\', '/')) -Description 'Packaged benchmark declaration differs from manifest'
        Assert-Equal -Actual ([string]$packageModel.benchmark_evidence.sha256).ToLowerInvariant() -Expected ([string]$approvals[0].evidence_sha256).ToLowerInvariant() -Description 'Packaged benchmark checksum differs from manifest'
        $declaredEvidencePath = (([string]$approvals[0].evidence_path).Replace('\', '/'))
        $expectedEvidencePath = "evidence/$modelId/$modelVersion/$([IO.Path]::GetFileName($declaredEvidencePath))"
        Assert-Equal -Actual ([string]$packageModel.benchmark_evidence.path) -Expected $expectedEvidencePath -Description "Production model benchmark path is not canonical for '$modelId'"

        $modelFiles = @($manifest.files | ForEach-Object { ([string]$_).Replace('\', '/') })
        $payloads = @($packageModel.payloads)
        if ($payloads.Count -ne $modelFiles.Count -or
            @($modelFiles | Where-Object { [IO.Path]::GetExtension($_) -ieq '.onnx' }).Count -ne 1) {
            throw "Model manifest '$manifestArchivePath' has invalid production payload coverage."
        }
        $artifactHashes = @{}
        if ($modelFiles.Count -eq 1) {
            $artifactHashes[$modelFiles[0]] = ([string]$manifest.sha256).ToLowerInvariant()
        }
        else {
            foreach ($property in @($manifest.preprocessing.model_payload_sha256.PSObject.Properties)) {
                $artifactHashes[([string]$property.Name).Replace('\', '/')] = ([string]$property.Value).ToLowerInvariant()
            }
        }
        foreach ($payload in $payloads) {
            $declaredPath = ([string]$payload.declared_path).Replace('\', '/')
            $expectedPayloadPath = "runtime/$modelId/$modelVersion/$declaredPath"
            if ($modelFiles -cnotcontains $declaredPath -or
                -not $artifactHashes.ContainsKey($declaredPath) -or
                [string]$payload.sha256 -ine [string]$artifactHashes[$declaredPath]) {
                throw "Production model payload index differs from manifest for '$declaredPath'."
            }
            Assert-Equal -Actual ([string]$payload.path) -Expected $expectedPayloadPath -Description "Production model payload path is not canonical for '$modelId'"
        }
    }

    $indexedSet = [System.Collections.Generic.HashSet[string]]::new($modelArtifactPaths, [StringComparer]::Ordinal)
    foreach ($entryName in @($EntryNames | Where-Object { $_.StartsWith('models/', [StringComparison]::Ordinal) })) {
        if (-not $indexedSet.Contains($entryName)) {
            throw "Portable archive contains unlisted production model data: $entryName"
        }
    }

    return @($modelArtifactPaths)
}

function Assert-NotoArtifactParity {
    param(
        [Parameter(Mandatory)]
        [object[]]$CommonRecords,

        [Parameter(Mandatory)]
        [object[]]$InstallerRecords,

        [Parameter(Mandatory)]
        [object[]]$PortableStageRecords,

        [Parameter(Mandatory)]
        [object[]]$PortableArchiveRecords,

        [Parameter(Mandatory)]
        [string]$CommonPublishRoot,

        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    foreach ($relativePath in @(
            'GraphReader.App.dll',
            'THIRD_PARTY_NOTICES.md',
            'LICENSES/NotoSans-OFL-1.1.txt')) {
        $common = @($CommonRecords | Where-Object { [string]$_.path -ceq $relativePath })
        $installer = @($InstallerRecords | Where-Object { [string]$_.path -ceq $relativePath })
        $portableStage = @($PortableStageRecords | Where-Object { [string]$_.path -ceq $relativePath })
        $portableArchive = @($PortableArchiveRecords | Where-Object { [string]$_.path -ceq $relativePath })
        foreach ($candidateCount in @($common.Count, $installer.Count, $portableStage.Count, $portableArchive.Count)) {
            if ($candidateCount -ne 1) {
                throw "Noto artifact proof requires exactly one '$relativePath' in every shared package form."
            }
        }
        foreach ($candidate in @($installer[0], $portableStage[0], $portableArchive[0])) {
            Assert-Equal `
                -Actual ([string]$candidate.sha256) `
                -Expected ([string]$common[0].sha256) `
                -Description "Noto artifact proof checksum differs for '$relativePath'"
        }
    }

    foreach ($relativePath in @('THIRD_PARTY_NOTICES.md', 'LICENSES/NotoSans-OFL-1.1.txt')) {
        Assert-Equal `
            -Actual (Get-Sha256 -Path (Join-Path $CommonPublishRoot $relativePath)) `
            -Expected (Get-Sha256 -Path (Join-Path $RepositoryRoot $relativePath)) `
            -Description "Packaged Noto legal file differs from tracked source '$relativePath'"
    }

    $notice = Get-Content -LiteralPath (Join-Path $CommonPublishRoot 'THIRD_PARTY_NOTICES.md') -Raw
    foreach ($font in @(Get-ExpectedNotoComponents)) {
        foreach ($expectedText in @($font.FileName, $font.Sha256)) {
            if ($notice.IndexOf([string]$expectedText, [StringComparison]::Ordinal) -lt 0) {
                throw "Packaged THIRD_PARTY_NOTICES.md lacks '$expectedText'."
            }
        }
    }
    foreach ($expectedText in @(
            'noto-monthly-release-2026.05.01',
            '66c4b351c58f99ace5a6265d329080d74b057909',
            'LICENSES/NotoSans-OFL-1.1.txt')) {
        if ($notice.IndexOf($expectedText, [StringComparison]::Ordinal) -lt 0) {
            throw "Packaged THIRD_PARTY_NOTICES.md lacks '$expectedText'."
        }
    }

    $licenseHash = Get-Sha256 -Path (Join-Path $CommonPublishRoot 'LICENSES/NotoSans-OFL-1.1.txt')
    Assert-Equal `
        -Actual $licenseHash `
        -Expected 'cee9892f9f0cc8fe882c9e9537ee6a89621d86ee7ceaf70b02e2b2b1c25c061a' `
        -Description 'Packaged Noto OFL license checksum differs'
}

function Assert-ReleaseChecksums {
    param(
        [Parameter(Mandatory)]
        [string]$ArtifactRoot,

        [Parameter(Mandatory)]
        [string[]]$ExpectedFiles
    )

    $checksumPath = Join-Path $ArtifactRoot 'SHA256SUMS.txt'
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw "Release checksum manifest is missing: $checksumPath"
    }

    $records = @{}
    foreach ($line in Get-Content -LiteralPath $checksumPath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^([a-fA-F0-9]{64})\s+\*?([^/\\]+)$') {
            throw "SHA256SUMS.txt contains an invalid line: $line"
        }
        $fileName = $Matches[2]
        if ($records.ContainsKey($fileName)) {
            throw "SHA256SUMS.txt contains a duplicate file: $fileName"
        }
        $records[$fileName] = $Matches[1].ToLowerInvariant()
    }

    Assert-Equal -Actual $records.Count -Expected $ExpectedFiles.Count -Description 'Release checksum coverage count differs'
    foreach ($fileName in $ExpectedFiles) {
        if (-not $records.ContainsKey($fileName)) {
            throw "SHA256SUMS.txt does not cover release file: $fileName"
        }
        $actualHash = Get-Sha256 -Path (Join-Path $ArtifactRoot $fileName)
        Assert-Equal -Actual $actualHash -Expected $records[$fileName] -Description "Release checksum differs for '$fileName'"
    }
}

function Assert-InstallerEmbeddedPayload {
    param(
        [Parameter(Mandatory)]
        [string]$InstallerPath,

        [Parameter(Mandatory)]
        [string]$ExpectedDigest
    )

    if ($ExpectedDigest -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'Installer payload verification requires a 64-character SHA-256 digest.'
    }

    $normalizedDigest = $ExpectedDigest.ToLowerInvariant()
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $InstallerPath
    $startInfo.Arguments = "--verify-payload $normalizedDigest"
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    try {
        $process = [System.Diagnostics.Process]::Start($startInfo)
    }
    catch {
        throw "Installer payload verification could not execute '$InstallerPath': $($_.Exception.Message)"
    }
    if ($null -eq $process) {
        throw "Installer payload verification could not execute '$InstallerPath'."
    }

    try {
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            $null = $process.WaitForExit(5000)
            throw 'Installer payload verification exceeded 30 seconds.'
        }

        $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()

        if ($process.ExitCode -ne 0) {
            throw "Installer payload verification failed with exit code $($process.ExitCode): $($standardError.Trim())"
        }
        Assert-Equal `
            -Actual $standardOutput.Trim() `
            -Expected "Payload verification PASS: $normalizedDigest" `
            -Description 'Installer payload verification output differs'
        if (-not [string]::IsNullOrWhiteSpace($standardError)) {
            throw "Installer payload verification wrote unexpected error output: $($standardError.Trim())"
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-Sbom {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [object[]]$ExpectedFiles,

        [Parameter(Mandatory)]
        [string]$InstallerName,

        [Parameter(Mandatory)]
        [string]$ReleaseAuditPath
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "CycloneDX SBOM is missing: $Path"
    }
    $sbom = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Assert-Equal -Actual ([string]$sbom.bomFormat) -Expected 'CycloneDX' -Description 'SBOM format is invalid'
    Assert-Equal -Actual ([string]$sbom.metadata.component.version) -Expected $Version -Description 'SBOM application version differs'
    $components = @($sbom.components)
    if ($components.Count -eq 0) {
        throw 'SBOM contains no dependency components.'
    }

    $actualFiles = @(
        foreach ($component in $components) {
            if ([string]$component.type -ne 'file' -or [string]::IsNullOrWhiteSpace([string]$component.name)) {
                throw 'Every release SBOM component must identify one named file.'
            }
            $sha256 = @($component.hashes | Where-Object { [string]$_.alg -eq 'SHA-256' })
            if ($sha256.Count -ne 1 -or [string]$sha256[0].content -notmatch '^[a-fA-F0-9]{64}$') {
                throw "SBOM component '$($component.name)' lacks one exact SHA-256 checksum."
            }
            [pscustomobject]@{
                path = [string]$component.name
                sha256 = ([string]$sha256[0].content).ToLowerInvariant()
            }
        }
    )
    $expectedSbomFiles = @(Sort-PayloadRecordsOrdinal -Records @($ExpectedFiles | ForEach-Object {
            [pscustomobject]@{
                path = [string]$_.path
                sha256 = [string]$_.sha256
            }
        }))
    $actualSbomFiles = @(Sort-PayloadRecordsOrdinal -Records $actualFiles)
    Assert-Equal -Actual $actualSbomFiles.Count -Expected $expectedSbomFiles.Count -Description 'SBOM file coverage count differs'
    for ($index = 0; $index -lt $expectedSbomFiles.Count; $index++) {
        Assert-Equal -Actual $actualSbomFiles[$index].path -Expected $expectedSbomFiles[$index].path -Description "SBOM path differs at index $index"
        Assert-Equal -Actual $actualSbomFiles[$index].sha256 -Expected $expectedSbomFiles[$index].sha256 -Description "SBOM checksum differs for '$($expectedSbomFiles[$index].path)'"
    }

    $releaseAudit = Get-Content -LiteralPath $ReleaseAuditPath -Raw | ConvertFrom-Json
    $installerCoverage = @($releaseAudit.emittedArtifactCoverage | Where-Object {
            [string]$_.artifactKind -eq 'installer'
        })
    if ($installerCoverage.Count -ne 1 -or @($installerCoverage[0].componentIds).Count -eq 0) {
        throw 'Tracked release audit must contain one installer artifact coverage record with component identities.'
    }

    $auditComponents = @{}
    foreach ($component in @($releaseAudit.components)) {
        $auditComponents[[string]$component.id] = $component
    }

    $expectedNoto = @(Get-ExpectedNotoComponents)
    if (@($expectedNoto | Where-Object { -not $auditComponents.ContainsKey([string]$_.Id) }).Count -eq 0) {
        $fontBearingSbom = @($components | Where-Object { [string]$_.name -ceq 'GraphReader.App.dll' })
        if ($fontBearingSbom.Count -ne 1) {
            throw 'SBOM must contain exactly one font-bearing GraphReader.App.dll component.'
        }
        $fontBearingProperties = @{}
        foreach ($property in @($fontBearingSbom[0].properties)) {
            $fontBearingProperties[[string]$property.name] = [string]$property.value
        }
        [string[]]$expectedNotoIds = @($expectedNoto.Id)
        [string[]]$actualNotoIds = @(([string]$fontBearingProperties['graphreader:embeddedComponentIds']).Split(';'))
        [Array]::Sort($expectedNotoIds, [StringComparer]::Ordinal)
        [Array]::Sort($actualNotoIds, [StringComparer]::Ordinal)
        Assert-Equal -Actual ($actualNotoIds -join ';') -Expected ($expectedNotoIds -join ';') -Description 'SBOM embedded Noto component identities differ'

        $nestedComponents = @($fontBearingSbom[0].components)
        Assert-Equal -Actual $nestedComponents.Count -Expected $expectedNoto.Count -Description 'SBOM embedded Noto component count differs'
        foreach ($font in $expectedNoto) {
            $matches = @($nestedComponents | Where-Object { [string]$_.'bom-ref' -ceq [string]$font.Id })
            if ($matches.Count -ne 1) {
                throw "SBOM requires exactly one embedded component '$($font.Id)'."
            }
            $nested = $matches[0]
            $auditFont = $auditComponents[[string]$font.Id]
            Assert-Equal -Actual ([string]$nested.type) -Expected 'library' -Description "SBOM embedded component type differs for '$($font.Id)'"
            Assert-Equal -Actual ([string]$nested.name) -Expected ([string]$font.Name) -Description "SBOM embedded component name differs for '$($font.Id)'"
            Assert-Equal -Actual ([string]$nested.version) -Expected '2.015' -Description "SBOM embedded component version differs for '$($font.Id)'"
            $hashes = @($nested.hashes | Where-Object { [string]$_.alg -eq 'SHA-256' })
            if ($hashes.Count -ne 1) {
                throw "SBOM embedded component '$($font.Id)' requires one SHA-256 checksum."
            }
            Assert-Equal -Actual ([string]$hashes[0].content).ToLowerInvariant() -Expected ([string]$font.Sha256) -Description "SBOM embedded component checksum differs for '$($font.Id)'"
            $licenses = @($nested.licenses | ForEach-Object { [string]$_.expression })
            Assert-Equal -Actual ($licenses -join ';') -Expected 'OFL-1.1' -Description "SBOM embedded component license differs for '$($font.Id)'"
            $distributionReferences = @($nested.externalReferences | Where-Object { [string]$_.type -eq 'distribution' })
            if ($distributionReferences.Count -ne 1) {
                throw "SBOM embedded component '$($font.Id)' requires one distribution source."
            }
            Assert-Equal -Actual ([string]$distributionReferences[0].url) -Expected ([string]$auditFont.source) -Description "SBOM embedded component source differs for '$($font.Id)'"
            $nestedProperties = @{}
            foreach ($property in @($nested.properties)) {
                $nestedProperties[[string]$property.name] = [string]$property.value
            }
            Assert-Equal -Actual ([string]$nestedProperties['graphreader:sourceRevision']) -Expected ([string]$auditFont.sourceRevision) -Description "SBOM source revision differs for '$($font.Id)'"
            Assert-Equal -Actual ([string]$nestedProperties['graphreader:checksumPolicy']) -Expected 'exact-binary' -Description "SBOM checksum policy differs for '$($font.Id)'"
            Assert-Equal -Actual ([string]$nestedProperties['graphreader:noticePaths']) -Expected (@($auditFont.noticePaths) -join ';') -Description "SBOM notice paths differ for '$($font.Id)'"
            Assert-Equal -Actual ([string]$nestedProperties['graphreader:embeddedIn']) -Expected 'GraphReader.App.dll' -Description "SBOM embedded payload differs for '$($font.Id)'"
        }
    }

    foreach ($binaryFile in @($expectedSbomFiles | Where-Object {
                [IO.Path]::GetExtension([string]$_.path) -in @('.exe', '.dll') -and
                [string]$_.path -ne $InstallerName
            })) {
        $binaryName = [IO.Path]::GetFileName([string]$binaryFile.path)
        $coverageRule = $null
        foreach ($rule in @($releaseAudit.binaryCoverage.rules)) {
            [object[]]$allowedNames = @()
            if ($null -ne $rule.PSObject.Properties['allowedNames']) {
                $allowedNames = @($rule.allowedNames)
            }
            if ($binaryName -like [string]$rule.pattern -and
                ($allowedNames.Count -eq 0 -or $allowedNames -contains $binaryName)) {
                $coverageRule = $rule
                break
            }
        }
        if ($null -eq $coverageRule -or -not $auditComponents.ContainsKey([string]$coverageRule.componentId)) {
            throw "Published binary '$($binaryFile.path)' lacks tracked release-audit component coverage."
        }
        $auditComponent = $auditComponents[[string]$coverageRule.componentId]
        if ([string]$auditComponent.reviewStatus -ne 'reviewed' -or
            -not [bool]$auditComponent.commercialUse -or
            -not [bool]$auditComponent.redistribution) {
            throw "Published binary '$($binaryFile.path)' maps to an unreleasable audit component."
        }
        if ([string]$auditComponent.checksumPolicy -eq 'exact-binary' -and
            -not ([string]$binaryFile.sha256).Equals(
                [string]$auditComponent.artifactSha256,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Published binary '$($binaryFile.path)' differs from its exact release-audit binary."
        }

        $binarySbom = @($components | Where-Object { [string]$_.name -eq [string]$binaryFile.path })
        if ($binarySbom.Count -ne 1 -or @($binarySbom[0].licenses).Count -eq 0) {
            throw "Published binary '$($binaryFile.path)' lacks one SBOM component with a license identity."
        }
        $binaryLicenses = @($binarySbom[0].licenses | ForEach-Object { [string]$_.expression })
        if ($binaryLicenses.Count -ne 1 -or $binaryLicenses[0] -ne [string]$auditComponent.license) {
            throw "Published binary '$($binaryFile.path)' SBOM license differs from its audit component."
        }
        $binaryProperties = @{}
        foreach ($property in @($binarySbom[0].properties)) {
            $binaryProperties[[string]$property.name] = [string]$property.value
        }
        Assert-Equal -Actual ([string]$binaryProperties['graphreader:releaseAuditComponentIds']) -Expected ([string]$auditComponent.id) -Description "Published binary '$($binaryFile.path)' SBOM component identity differs"
        $expectedBinaryNotices = @($auditComponent.noticePaths | ForEach-Object { [string]$_ })
        $actualBinaryNotices = @(([string]$binaryProperties['graphreader:noticePaths']).Split(';'))
        [Array]::Sort($expectedBinaryNotices, [StringComparer]::Ordinal)
        [Array]::Sort($actualBinaryNotices, [StringComparer]::Ordinal)
        Assert-Equal -Actual ($actualBinaryNotices -join ';') -Expected ($expectedBinaryNotices -join ';') -Description "Published binary '$($binaryFile.path)' SBOM notice paths differ"
    }

    $expectedLicenses = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $expectedNotices = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($componentId in @($installerCoverage[0].componentIds)) {
        if (-not $auditComponents.ContainsKey([string]$componentId)) {
            throw "Installer release-audit coverage references unknown component '$componentId'."
        }
        $component = $auditComponents[[string]$componentId]
        if ([string]$component.reviewStatus -ne 'reviewed' -or
            -not [bool]$component.commercialUse -or
            -not [bool]$component.redistribution) {
            throw "Installer release-audit component '$componentId' is not release-approved."
        }
        $null = $expectedLicenses.Add([string]$component.license)
        foreach ($noticePath in @($component.noticePaths)) {
            $null = $expectedNotices.Add([string]$noticePath)
        }
    }

    $installerSbomComponents = @($components | Where-Object { [string]$_.name -eq $InstallerName })
    if ($installerSbomComponents.Count -ne 1) {
        throw 'SBOM must contain exactly one installer component.'
    }
    $installerSbom = $installerSbomComponents[0]
    $actualLicenses = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($license in @($installerSbom.licenses)) {
        $null = $actualLicenses.Add([string]$license.expression)
    }
    [string[]]$expectedLicenseValues = @($expectedLicenses)
    [string[]]$actualLicenseValues = @($actualLicenses)
    [Array]::Sort($expectedLicenseValues, [StringComparer]::Ordinal)
    [Array]::Sort($actualLicenseValues, [StringComparer]::Ordinal)
    if (($actualLicenseValues -join ';') -ne ($expectedLicenseValues -join ';')) {
        throw 'Installer SBOM license identities differ from the tracked release audit.'
    }

    $properties = @{}
    foreach ($property in @($installerSbom.properties)) {
        $properties[[string]$property.name] = [string]$property.value
    }
    $actualComponentIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($componentId in @(([string]$properties['graphreader:releaseAuditComponentIds']).Split(';'))) {
        $null = $actualComponentIds.Add($componentId)
    }
    [string[]]$expectedComponentIdValues = @($installerCoverage[0].componentIds | ForEach-Object { [string]$_ })
    [string[]]$actualComponentIdValues = @($actualComponentIds)
    [Array]::Sort($expectedComponentIdValues, [StringComparer]::Ordinal)
    [Array]::Sort($actualComponentIdValues, [StringComparer]::Ordinal)
    if (($actualComponentIdValues -join ';') -ne ($expectedComponentIdValues -join ';')) {
        throw 'Installer SBOM component identities differ from the tracked release audit.'
    }
    $actualNotices = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($noticePath in @(([string]$properties['graphreader:noticePaths']).Split(';'))) {
        $null = $actualNotices.Add($noticePath)
    }
    [string[]]$expectedNoticeValues = @($expectedNotices)
    [string[]]$actualNoticeValues = @($actualNotices)
    [Array]::Sort($expectedNoticeValues, [StringComparer]::Ordinal)
    [Array]::Sort($actualNoticeValues, [StringComparer]::Ordinal)
    if (($actualNoticeValues -join ';') -ne ($expectedNoticeValues -join ';')) {
        throw 'Installer SBOM notice paths differ from the tracked release audit.'
    }
    Assert-Equal -Actual ([string]$properties['graphreader:installedCopyName']) -Expected ([string]$installerCoverage[0].installedCopyName) -Description 'Installer SBOM installed-copy name differs'
    $installerFile = @($expectedSbomFiles | Where-Object { [string]$_.path -eq $InstallerName })
    Assert-Equal -Actual ([string]$properties['graphreader:installedCopySha256']) -Expected ([string]$installerFile[0].sha256) -Description 'Installer SBOM installed-copy checksum differs'
}

function Assert-PortableArchive {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string[]]$RequiredContentPaths
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entryNames = Get-ArchiveEntryNames -Archive $archive
        Assert-NoForbiddenArchiveEntries -EntryNames $entryNames

        foreach ($requiredEntry in @(
                'GraphReader.App.exe',
                'coreclr.dll',
                'hostfxr.dll',
                'hostpolicy.dll',
                'System.Private.CoreLib.dll',
                'PresentationFramework.dll',
                'portable.mode',
                'LICENSE',
                'NOTICE',
                'THIRD_PARTY_NOTICES.md',
                'release-audit.json')) {
            if ($entryNames -notcontains $requiredEntry) {
                throw "Portable archive is missing required root entry: $requiredEntry"
            }
        }

        if (@($entryNames | Where-Object { $_ -eq 'portable.mode' }).Count -ne 1) {
            throw 'Portable archive must contain exactly one root portable.mode sentinel.'
        }

        $applicationEntries = @($archive.Entries | Where-Object {
                $_.FullName.Replace('\', '/') -ceq 'GraphReader.App.exe'
            })
        if ($applicationEntries.Count -ne 1) {
            throw 'Portable archive must contain exactly one case-sensitive GraphReader.App.exe entry.'
        }
        $applicationStream = $applicationEntries[0].Open()
        try {
            Assert-WindowsX64ExecutableStream `
                -Stream $applicationStream `
                -Description 'Portable GraphReader.App.exe'
        }
        finally {
            $applicationStream.Dispose()
        }

        Assert-LicenseBundle -Archive $archive -EntryNames $entryNames
        $modelArtifactPaths = @(Assert-ModelManifests -Archive $archive -EntryNames $entryNames)
        $applicationPublishPaths = @(Get-ApplicationPublishArchivePaths `
                -Archive $archive `
                -EntryNames $entryNames)
        Assert-PortableArchiveAllowlist `
            -EntryNames $entryNames `
            -RequiredContentPaths $RequiredContentPaths `
            -ModelArtifactPaths $modelArtifactPaths `
            -ApplicationPublishPaths $applicationPublishPaths
        $releaseAudit = (Get-ZipEntryText -Archive $archive -EntryName 'release-audit.json') | ConvertFrom-Json
        Assert-Equal -Actual ([int]$releaseAudit.schemaVersion) -Expected 1 -Description 'Packaged release audit schema version is invalid'
        Assert-MandatoryReleaseEvidenceGates `
            -ReleaseAudit $releaseAudit `
            -Description 'Packaged release audit'
        if ($releaseAudit.components -isnot [System.Array] -or @($releaseAudit.components).Count -eq 0) {
            throw 'Packaged release audit requires a nonempty components array.'
        }
        if ($null -eq $releaseAudit.binaryCoverage -or
            $releaseAudit.binaryCoverage.rules -isnot [System.Array] -or
            @($releaseAudit.binaryCoverage.rules).Count -eq 0) {
            throw 'Packaged release audit requires nonempty binary coverage rules.'
        }
        $installerCoverage = @($releaseAudit.emittedArtifactCoverage | Where-Object {
                [string]$_.artifactKind -eq 'installer'
            })
        if ($installerCoverage.Count -ne 1 -or
            @($installerCoverage[0].componentIds).Count -eq 0 -or
            [string]$installerCoverage[0].installedCopyName -ne 'Uninstall.exe') {
            throw 'Packaged release audit requires one installer and Uninstall.exe provenance mapping.'
        }
        return Get-ZipPayloadRecords -Archive $archive -Exclude @('portable.mode')
    }
    finally {
        $archive.Dispose()
    }
}

$manifestFullPath = [System.IO.Path]::GetFullPath($ManifestPath)
$packagingRoot = Split-Path -Parent $manifestFullPath
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $packagingRoot '..'))
$manifest = Get-Content -LiteralPath $manifestFullPath -Raw | ConvertFrom-Json
$appProjectPath = Join-Path $repositoryRoot 'src\GraphReader.App\GraphReader.App.csproj'
$repositoryRequiresNoto = $false
if (Test-Path -LiteralPath $appProjectPath -PathType Leaf) {
    $appProjectContent = Get-Content -LiteralPath $appProjectPath -Raw
    $repositoryRequiresNoto = @(
        Get-ExpectedNotoComponents | Where-Object {
            $appProjectContent -notmatch [regex]::Escape([string]$_.FileName)
        }).Count -eq 0
}
$trackedReleaseAuditPath = Join-Path $repositoryRoot 'packaging\common\release-audit.json'
$notoMetadataChecked = $false
if (Test-Path -LiteralPath $trackedReleaseAuditPath -PathType Leaf) {
    $definitionReleaseAudit = Get-Content -LiteralPath $trackedReleaseAuditPath -Raw | ConvertFrom-Json
    $expectedNotoIds = @(Get-ExpectedNotoComponents | ForEach-Object { [string]$_.Id })
    $foundNotoIds = @($definitionReleaseAudit.components | Where-Object {
            [string]$_.id -in $expectedNotoIds
        })
    if ($repositoryRequiresNoto -or $foundNotoIds.Count -gt 0) {
        Assert-NotoReleaseAuditMetadata `
            -ReleaseAudit $definitionReleaseAudit `
            -Description 'Tracked release audit'
        $notoMetadataChecked = $true
    }
}
elseif ($repositoryRequiresNoto) {
    throw "Tracked release audit is missing: $trackedReleaseAuditPath"
}

Assert-Equal -Actual $manifest.schemaVersion -Expected 2 -Description 'Packaging manifest schema version is invalid'
Assert-Equal -Actual ([string]$manifest.rid) -Expected 'win-x64' -Description 'Packaging RID is invalid'
Assert-Equal -Actual ([string]$manifest.versionSource) -Expected 'Directory.Build.props#Project/PropertyGroup/Version' -Description 'Packaging version source is invalid'

if ([string]::IsNullOrWhiteSpace($VersionFilePath)) {
    $VersionFilePath = Join-Path $repositoryRoot 'Directory.Build.props'
}

$centralVersion = Get-CentralVersion -Path ([System.IO.Path]::GetFullPath($VersionFilePath))
Assert-BuildVersion -Version $centralVersion -ReleaseRequired:$RequireReleaseVersion
$version = $centralVersion

if ([string]::IsNullOrWhiteSpace([string]$manifest.commonPublish)) {
    throw 'The common publish path is required.'
}

$expectedInstallerName = "GraphAutoReader-$version-$($manifest.rid)-setup.exe"
$expectedPortableName = "GraphAutoReader-$version-$($manifest.rid)-portable.zip"
Assert-Equal -Actual ([string]$manifest.installer.fileNameTemplate) -Expected 'GraphAutoReader-{version}-{rid}-setup.exe' -Description 'Installer filename template is invalid'
Assert-Equal -Actual ([string]$manifest.portable.fileNameTemplate) -Expected 'GraphAutoReader-{version}-{rid}-portable.zip' -Description 'Portable filename template is invalid'

$commonDefinitionPath = Assert-RelativePath -Root $packagingRoot -RelativePath 'common/publish.json' -Description 'Common publish definition' -RequireFile
$installerDefinitionPath = Assert-RelativePath -Root $packagingRoot -RelativePath ([string]$manifest.installer.definition) -Description 'Installer definition' -RequireFile
$portableDefinitionPath = Assert-RelativePath -Root $packagingRoot -RelativePath ([string]$manifest.portable.definition) -Description 'Portable definition' -RequireFile

$null = Assert-RelativePath -Root $packagingRoot -RelativePath ([string]$manifest.commonPublish) -Description 'Common publish staging path'
$null = Assert-RelativePath -Root $packagingRoot -RelativePath ([string]$manifest.installer.stagingDirectory) -Description 'Installer staging path'
$null = Assert-RelativePath -Root $packagingRoot -RelativePath ([string]$manifest.portable.stagingDirectory) -Description 'Portable staging path'
$null = Assert-RelativePath -Root $packagingRoot -RelativePath ([string]$manifest.releaseDirectory) -Description 'Release output path'

$commonDefinition = Get-Content -LiteralPath $commonDefinitionPath -Raw | ConvertFrom-Json
$installerDefinition = Get-Content -LiteralPath $installerDefinitionPath -Raw | ConvertFrom-Json
$portableDefinition = Get-Content -LiteralPath $portableDefinitionPath -Raw | ConvertFrom-Json

Assert-Equal -Actual $commonDefinition.schemaVersion -Expected 2 -Description 'Common publish schema version is invalid'
Assert-Equal -Actual ([string]$commonDefinition.project) -Expected 'src/GraphReader.App/GraphReader.App.csproj' -Description 'Common publish project is invalid'
Assert-Equal -Actual ([string]$commonDefinition.configuration) -Expected 'Release' -Description 'Common publish configuration is invalid'
Assert-Equal -Actual $commonDefinition.selfContained -Expected $true -Description 'Common publish must be self-contained'
Assert-Equal -Actual $commonDefinition.debugSymbols -Expected $false -Description 'Release publish must exclude debug symbols'
$allowedModelTasks = @(
    'ocr_detection', 'ocr_recognition', 'marker_center', 'marker_classifier', 'panelization')
$requiredModelTasks = @($commonDefinition.requiredModelTasks)
if ($requiredModelTasks.Count -eq 0 -or
    @($requiredModelTasks | Where-Object {
            $_ -isnot [string] -or
            [string]::IsNullOrWhiteSpace([string]$_) -or
            $allowedModelTasks -cnotcontains [string]$_
        }).Count -gt 0 -or
    @($requiredModelTasks | Select-Object -Unique).Count -ne $requiredModelTasks.Count) {
    throw 'Common publish requiredModelTasks must be a nonempty unique array of supported offline production tasks.'
}
Assert-Equal -Actual $installerDefinition.schemaVersion -Expected 2 -Description 'Installer definition schema version is invalid'
Assert-Equal -Actual ([string]$installerDefinition.kind) -Expected 'installer' -Description 'Installer definition kind is invalid'
Assert-Equal -Actual ([string]$installerDefinition.format) -Expected 'setup-exe' -Description 'Installer format is invalid'
Assert-Equal -Actual $installerDefinition.commonPublishOnly -Expected $true -Description 'Installer must consume the common publish'
Assert-Equal -Actual ([string]$installerDefinition.scope) -Expected 'perUser' -Description 'Installer scope is invalid'
Assert-Equal -Actual $installerDefinition.requiresAdministrator -Expected $false -Description 'Installer must not require elevation by default'
Assert-Equal -Actual $installerDefinition.offlineCoreWorkflow -Expected $true -Description 'Installer core workflow must work offline'
Assert-Equal -Actual ([string]$installerDefinition.mutableDataRoot) -Expected '%LOCALAPPDATA%\GraphAutoReader' -Description 'Installed data root is invalid'
Assert-Equal -Actual ([string]$installerDefinition.installRoot) -Expected '%LOCALAPPDATA%\Programs\GraphAutoReader' -Description 'Installed application root is invalid'
Assert-Equal -Actual $installerDefinition.preserveUserDataOnUninstall -Expected $true -Description 'Installer uninstall must preserve user data'
Assert-Equal -Actual ([string]$installerDefinition.upgradePolicy) -Expected 'allow-newer-and-repair-same' -Description 'Installer upgrade and repair policy is invalid'
Assert-Equal -Actual ([string]$installerDefinition.downgradePolicy) -Expected 'blocked-by-default' -Description 'Installer downgrade policy is invalid'
Assert-Equal -Actual ([string]$portableDefinition.kind) -Expected 'portable' -Description 'Portable definition kind is invalid'
Assert-Equal -Actual $portableDefinition.schemaVersion -Expected 2 -Description 'Portable definition schema version is invalid'
Assert-Equal -Actual ([string]$portableDefinition.format) -Expected 'zip' -Description 'Portable format is invalid'
Assert-Equal -Actual $portableDefinition.commonPublishOnly -Expected $true -Description 'Portable package must consume the common publish'
Assert-Equal -Actual $portableDefinition.requiresAdministrator -Expected $false -Description 'Portable package must not require elevation'
Assert-Equal -Actual $portableDefinition.offlineCoreWorkflow -Expected $true -Description 'Portable core workflow must work offline'
Assert-Equal -Actual ([string]$portableDefinition.sentinel) -Expected 'portable.mode' -Description 'Portable sentinel name is invalid'
Assert-Equal -Actual ([string]$portableDefinition.mutableDataRoot) -Expected '.\Data' -Description 'Portable data root is invalid'
Assert-Equal -Actual $portableDefinition.registryConfigurationRequired -Expected $false -Description 'Portable mode must not depend on registry configuration'
Assert-Equal -Actual $portableDefinition.startMenuShortcut -Expected $false -Description 'Portable mode must not create a Start Menu shortcut'
Assert-Equal -Actual $portableDefinition.uninstallEntry -Expected $false -Description 'Portable mode must not create an uninstall entry'

$expectedContentMappings = [ordered]@{
    'contracts' = 'contracts'
    'LICENSE' = 'LICENSE'
    'NOTICE' = 'NOTICE'
    'THIRD_PARTY_NOTICES.md' = 'THIRD_PARTY_NOTICES.md'
    'LICENSES' = 'LICENSES'
    'packaging/common/release-audit.json' = 'release-audit.json'
}
$requiredContent = @($commonDefinition.requiredContent)
if ($requiredContent.Count -ne $expectedContentMappings.Count) {
    throw 'Common publish requiredContent must contain only the approved distribution mappings.'
}
$seenContentSources = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($content in $requiredContent) {
    $source = ([string]$content.source).Replace('\', '/')
    $target = ([string]$content.target).Replace('\', '/')
    if (-not $expectedContentMappings.Contains($source) -or
        [string]$expectedContentMappings[$source] -cne $target -or
        -not $seenContentSources.Add($source)) {
        throw "Common publish contains an unapproved source-to-target mapping: '$source' -> '$target'."
    }
}

$localizationChecked = $false
if (-not [string]::IsNullOrWhiteSpace($LocalizationReportPath)) {
    Assert-LocalizationReport -Path ([System.IO.Path]::GetFullPath($LocalizationReportPath))
    $localizationChecked = $true
}
elseif ([System.IO.Path]::GetFullPath($packagingRoot).Equals(
        [System.IO.Path]::GetFullPath($PSScriptRoot),
        [System.StringComparison]::OrdinalIgnoreCase)) {
    $localizationAuditPath = Join-Path $PSScriptRoot 'localization\Audit-Localization.ps1'
    if (Test-Path -LiteralPath $localizationAuditPath -PathType Leaf) {
        $temporaryLocalizationReport = Join-Path ([System.IO.Path]::GetTempPath()) (
            'GraphReader-Localization-' + [Guid]::NewGuid().ToString('N') + '.json')
        try {
            Invoke-LocalizationAudit `
                -AuditScriptPath $localizationAuditPath `
                -RepositoryRoot $repositoryRoot `
                -ReportPath $temporaryLocalizationReport
            Assert-LocalizationReport -Path $temporaryLocalizationReport
            $localizationChecked = $true
        }
        finally {
            if (Test-Path -LiteralPath $temporaryLocalizationReport -PathType Leaf) {
                Remove-Item -LiteralPath $temporaryLocalizationReport -Force
            }
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    if (-not (Test-Path -LiteralPath $trackedReleaseAuditPath -PathType Leaf)) {
        throw "Tracked release audit is missing: $trackedReleaseAuditPath"
    }
    $trackedReleaseAudit = Get-Content -LiteralPath $trackedReleaseAuditPath -Raw | ConvertFrom-Json
    Assert-MandatoryReleaseEvidenceGates `
        -ReleaseAudit $trackedReleaseAudit `
        -Description 'Tracked release audit' `
        -RepositoryRoot $repositoryRoot

    $artifactRootFullPath = [System.IO.Path]::GetFullPath($ArtifactRoot)
    $requiredContentArchivePaths = @(Get-RequiredContentArchivePaths `
            -RepositoryRoot $repositoryRoot `
            -RequiredContent @($commonDefinition.requiredContent))
    $installerArtifactPath = Join-Path $artifactRootFullPath $expectedInstallerName
    $portableArtifactPath = Join-Path $artifactRootFullPath $expectedPortableName

    $expectedReleaseFiles = @(
        $expectedInstallerName,
        $expectedPortableName,
        'KNOWN_LIMITATIONS.md',
        'RELEASE_NOTES.md',
        'release-metadata.json',
        'sbom.cdx.json',
        'SHA256SUMS.txt') | Sort-Object
    $releaseFiles = @(
        Get-ChildItem -LiteralPath $artifactRootFullPath -File | ForEach-Object { $_.Name } | Sort-Object
    )
    Assert-Equal -Actual ($releaseFiles -join "`n") -Expected ($expectedReleaseFiles -join "`n") -Description 'Release artifact allowlist differs'
    if (@(Get-ChildItem -LiteralPath $artifactRootFullPath -Directory).Count -ne 0) {
        throw 'Release artifact root must not contain directories.'
    }

    foreach ($artifactPath in @($installerArtifactPath, $portableArtifactPath)) {
        if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
            throw "Required release artifact is missing: $artifactPath"
        }

        if ((Get-Item -LiteralPath $artifactPath).Length -eq 0) {
            throw "Release artifact is empty: $artifactPath"
        }
    }

    $portablePayloadRecords = @(Assert-PortableArchive `
            -Path $portableArtifactPath `
            -RequiredContentPaths $requiredContentArchivePaths)

    $buildRoot = Split-Path -Parent $artifactRootFullPath
    $commonPublishPath = Join-Path $buildRoot ([string]$manifest.commonPublish)
    $installerStagePath = Join-Path $buildRoot ([string]$manifest.installer.stagingDirectory)
    $portableStagePath = Join-Path $buildRoot ([string]$manifest.portable.stagingDirectory)
    $commonPayloadRecords = @(Get-DirectoryPayloadRecords -Root $commonPublishPath)
    $installerPayloadRecords = @(Get-DirectoryPayloadRecords -Root $installerStagePath)
    $portableAllStageRecords = @(Get-DirectoryPayloadRecords -Root $portableStagePath)
    $portableStageRecords = @(Get-DirectoryPayloadRecords -Root $portableStagePath -Exclude @('portable.mode'))
    Assert-PayloadRecordsEqual -Actual $installerPayloadRecords -Expected $commonPayloadRecords -Description 'Installer and common payloads'
    Assert-PayloadRecordsEqual -Actual $portableStageRecords -Expected $commonPayloadRecords -Description 'Portable stage and common payloads'
    Assert-PayloadRecordsEqual -Actual $portablePayloadRecords -Expected $commonPayloadRecords -Description 'Portable ZIP and common payloads'
    if ($notoMetadataChecked) {
        Assert-NotoArtifactParity `
            -CommonRecords $commonPayloadRecords `
            -InstallerRecords $installerPayloadRecords `
            -PortableStageRecords $portableStageRecords `
            -PortableArchiveRecords $portablePayloadRecords `
            -CommonPublishRoot $commonPublishPath `
            -RepositoryRoot $repositoryRoot
    }

    $packagedReleaseAuditPath = Join-Path $commonPublishPath 'release-audit.json'
    Assert-Equal `
        -Actual (Get-Sha256 -Path $packagedReleaseAuditPath) `
        -Expected (Get-Sha256 -Path $trackedReleaseAuditPath) `
        -Description 'Packaged release audit differs from the tracked source'
    if ($notoMetadataChecked) {
        $packagedReleaseAudit = Get-Content -LiteralPath $packagedReleaseAuditPath -Raw | ConvertFrom-Json
        Assert-NotoReleaseAuditMetadata `
            -ReleaseAudit $packagedReleaseAudit `
            -Description 'Packaged release audit'
    }

    $portableSentinelPath = Join-Path $portableStagePath 'portable.mode'
    if (-not (Test-Path -LiteralPath $portableSentinelPath -PathType Leaf) -or
        (Get-Item -LiteralPath $portableSentinelPath).Length -ne 0) {
        throw 'Portable staging must contain one empty portable.mode sentinel.'
    }

    $metadataPath = Join-Path $artifactRootFullPath 'release-metadata.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    Assert-Equal -Actual ([int]$metadata.schemaVersion) -Expected 1 -Description 'Release metadata schema version is invalid'
    Assert-Equal -Actual ([string]$metadata.product) -Expected 'Graph Auto Reader' -Description 'Release metadata product is invalid'
    Assert-Equal -Actual ([string]$metadata.version) -Expected $version -Description 'Release metadata version differs'
    Assert-Equal -Actual ([string]$metadata.rid) -Expected 'win-x64' -Description 'Release metadata RID differs'
    if ([string]$metadata.gitCommit -notmatch '^[a-fA-F0-9]{40}$') {
        throw 'Release metadata Git commit must be a full 40-character SHA.'
    }
    $buildUtcValue = $metadata.buildUtc
    $buildTimestamp = [DateTimeOffset]::MinValue
    $buildUtcIsValid = if ($buildUtcValue -is [DateTime]) {
        $buildUtcValue.Kind -eq [DateTimeKind]::Utc
    }
    else {
        $buildUtcText = [string]$buildUtcValue
        $buildUtcText.EndsWith('Z', [StringComparison]::Ordinal) -and
            [DateTimeOffset]::TryParse(
                $buildUtcText,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::AssumeUniversal,
                [ref]$buildTimestamp) -and
            $buildTimestamp.Offset -eq [TimeSpan]::Zero
    }
    if (-not $buildUtcIsValid) {
        throw 'Release metadata buildUtc must be a canonical UTC timestamp ending in Z.'
    }
    Assert-Equal -Actual ([int]$metadata.contractVersion) -Expected 1 -Description 'Release metadata contract version differs'
    Assert-Equal -Actual ([int]$metadata.commonPayload.fileCount) -Expected $commonPayloadRecords.Count -Description 'Release metadata common payload count differs'

    $metadataPayloadRecords = @(Sort-PayloadRecordsOrdinal -Records @(
        $metadata.commonPayload.files | ForEach-Object {
            [pscustomobject]@{
                path = [string]$_.path
                size = [long]$_.size
                sha256 = ([string]$_.sha256).ToLowerInvariant()
            }
        }))
    Assert-PayloadRecordsEqual -Actual $metadataPayloadRecords -Expected $commonPayloadRecords -Description 'Release metadata and common payloads'
    if ($notoMetadataChecked) {
        $fontBearingApplication = @($commonPayloadRecords | Where-Object {
                [string]$_.path -ceq 'GraphReader.App.dll'
            })
        if ($fontBearingApplication.Count -ne 1) {
            throw 'Release metadata Noto proof requires exactly one GraphReader.App.dll payload.'
        }
        Assert-NotoGeneratedMetadata `
            -Metadata $metadata `
            -ApplicationSha256 ([string]$fontBearingApplication[0].sha256)
    }
    $commonPayloadDigest = Get-PayloadDigest -Records $commonPayloadRecords
    $portablePayloadDigest = Get-PayloadDigest -Records $portableAllStageRecords
    Assert-Equal -Actual ([string]$metadata.commonPayload.sha256).ToLowerInvariant() -Expected $commonPayloadDigest -Description 'Release metadata common payload digest differs'
    Assert-Equal -Actual ([string]$metadata.installer.fileName) -Expected $expectedInstallerName -Description 'Release metadata installer filename differs'
    Assert-Equal -Actual ([string]$metadata.portable.fileName) -Expected $expectedPortableName -Description 'Release metadata portable filename differs'
    Assert-Equal -Actual ([string]$metadata.installer.sha256).ToLowerInvariant() -Expected (Get-Sha256 -Path $installerArtifactPath) -Description 'Release metadata installer checksum differs'
    Assert-Equal -Actual ([string]$metadata.portable.sha256).ToLowerInvariant() -Expected (Get-Sha256 -Path $portableArtifactPath) -Description 'Release metadata portable checksum differs'
    Assert-Equal -Actual ([string]$metadata.installer.payloadSha256).ToLowerInvariant() -Expected $commonPayloadDigest -Description 'Installer payload identity differs'
    Assert-Equal -Actual ([string]$metadata.installer.sharedPayloadSha256).ToLowerInvariant() -Expected $commonPayloadDigest -Description 'Installer shared payload identity differs'
    Assert-Equal -Actual ([string]$metadata.portable.payloadSha256).ToLowerInvariant() -Expected $portablePayloadDigest -Description 'Portable full payload identity differs'
    Assert-Equal -Actual ([string]$metadata.portable.sharedPayloadSha256).ToLowerInvariant() -Expected $commonPayloadDigest -Description 'Portable shared payload identity differs'
    if ($null -eq $metadata.installer.PSObject.Properties['provenance']) {
        throw 'Release metadata installer provenance is missing.'
    }
    $installerProvenance = $metadata.installer.provenance
    foreach ($collectionName in @('componentIds', 'licenses', 'noticePaths')) {
        if (@($installerProvenance.$collectionName).Count -eq 0) {
            throw "Release metadata installer provenance requires nonempty $collectionName."
        }
    }
    Assert-Equal -Actual ([string]$installerProvenance.checksumPolicy) -Expected 'release-sbom' -Description 'Installer provenance checksum policy differs'
    Assert-Equal -Actual ([string]$installerProvenance.setupSha256).ToLowerInvariant() -Expected (Get-Sha256 -Path $installerArtifactPath) -Description 'Installer provenance setup checksum differs'
    Assert-Equal -Actual ([string]$installerProvenance.installedCopyName) -Expected 'Uninstall.exe' -Description 'Installer provenance installed-copy name differs'
    Assert-Equal -Actual ([string]$installerProvenance.installedCopySha256).ToLowerInvariant() -Expected (Get-Sha256 -Path $installerArtifactPath) -Description 'Installer provenance installed-copy checksum differs'
    Assert-InstallerEmbeddedPayload -InstallerPath $installerArtifactPath -ExpectedDigest $commonPayloadDigest

    $releaseBuilds = @($metadata.versionPolicy.releaseBuilds | ForEach-Object { [int]$_ })
    Assert-Equal -Actual ($releaseBuilds -join ',') -Expected '1,21,41,61,81' -Description 'Release metadata cadence differs'
    Assert-Equal -Actual ([string]$metadata.versionPolicy.stablePromotionRelease) -Expected (Get-GraphReaderStablePromotionVersion) -Description 'Stable-promotion release metadata differs'
    foreach ($policyName in @('upgrade', 'repair', 'downgrade')) {
        if ([string]::IsNullOrWhiteSpace([string]$metadata.versionPolicy.$policyName)) {
            throw "Release metadata version policy '$policyName' is missing."
        }
    }
    if ([string]$metadata.versionPolicy.downgrade -notmatch '(?i)(block|refus|prevent|not supported)') {
        throw 'Release metadata must explicitly block unsupported downgrades.'
    }

    $checksummedReleaseFiles = @($expectedReleaseFiles | Where-Object { $_ -ne 'SHA256SUMS.txt' })
    Assert-ReleaseChecksums -ArtifactRoot $artifactRootFullPath -ExpectedFiles $checksummedReleaseFiles
    $sbomExpectedFiles = @(
        $commonPayloadRecords
        [pscustomobject]@{ path = $expectedInstallerName; sha256 = Get-Sha256 -Path $installerArtifactPath }
        [pscustomobject]@{ path = $expectedPortableName; sha256 = Get-Sha256 -Path $portableArtifactPath }
    )
    Assert-Sbom `
        -Path (Join-Path $artifactRootFullPath 'sbom.cdx.json') `
        -Version $version `
        -ExpectedFiles $sbomExpectedFiles `
        -InstallerName $expectedInstallerName `
        -ReleaseAuditPath $trackedReleaseAuditPath
}

[pscustomobject]@{
    Manifest = $manifestFullPath
    Version = $version
    RuntimeIdentifier = [string]$manifest.rid
    ReleaseVersionRequired = [bool]$RequireReleaseVersion
    Installer = $expectedInstallerName
    Portable = $expectedPortableName
    ArtifactFilesChecked = -not [string]::IsNullOrWhiteSpace($ArtifactRoot)
    ArtifactProofStatus = if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) { 'NOT_PERFORMED' } else { 'PASS' }
    NotoMetadataChecked = $notoMetadataChecked
    NotoArtifactProofStatus = if (-not $notoMetadataChecked) {
        'NOT_APPLICABLE'
    }
    elseif ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
        'NOT_PERFORMED'
    }
    else {
        'PASS'
    }
    LocalizationChecked = $localizationChecked
    Status = 'PASS'
}
