# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Test-ReleaseArtifact.ps1'))
$versionPolicyScript = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\VersionPolicy.ps1'))
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("GraphReader-PackagingTests-" + [Guid]::NewGuid().ToString('N'))
$passed = 0
$failed = 0

function Write-JsonFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$Value
    )

    $parent = Split-Path -Parent $Path
    $null = New-Item -ItemType Directory -Path $parent -Force
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
}

function New-PackagingFixture {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [string]$Version = '0.0.18'
    )

    $root = Join-Path $testRoot $Name
    $packaging = Join-Path $root 'packaging'
    $null = New-Item -ItemType Directory -Path (Join-Path $packaging 'common') -Force
    $null = New-Item -ItemType Directory -Path (Join-Path $packaging 'installer') -Force
    $null = New-Item -ItemType Directory -Path (Join-Path $packaging 'portable') -Force

    @"
<Project>
  <PropertyGroup>
    <Version>$Version</Version>
    <AssemblyVersion>$Version.0</AssemblyVersion>
    <FileVersion>$Version.0</FileVersion>
    <InformationalVersion>$Version</InformationalVersion>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Encoding utf8

    $manifest = [ordered]@{
        schemaVersion = 2
        versionSource = 'Directory.Build.props#Project/PropertyGroup/Version'
        version = $Version
        rid = 'win-x64'
        commonPublish = 'common/publish'
        releaseDirectory = 'release'
        installer = [ordered]@{
            definition = 'installer/installer.json'
            fileNameTemplate = 'GraphAutoReader-{version}-{rid}-setup.exe'
            stagingDirectory = 'installer/staging'
        }
        portable = [ordered]@{
            definition = 'portable/portable.json'
            fileNameTemplate = 'GraphAutoReader-{version}-{rid}-portable.zip'
            stagingDirectory = 'portable/staging'
        }
    }
    Write-JsonFile -Path (Join-Path $packaging 'artifacts.json') -Value $manifest

    $common = [ordered]@{
        schemaVersion = 2
        project = 'src/GraphReader.App/GraphReader.App.csproj'
        configuration = 'Release'
        selfContained = $true
        publishSingleFile = $false
        debugSymbols = $false
        requiredModelTasks = @('marker_center')
        requiredContent = @(
            @{ source = 'contracts'; target = 'contracts' },
            @{ source = 'LICENSE'; target = 'LICENSE' },
            @{ source = 'NOTICE'; target = 'NOTICE' },
            @{ source = 'THIRD_PARTY_NOTICES.md'; target = 'THIRD_PARTY_NOTICES.md' },
            @{ source = 'LICENSES'; target = 'LICENSES' },
            @{ source = 'packaging/common/release-audit.json'; target = 'release-audit.json' }
        )
    }
    Write-JsonFile -Path (Join-Path $packaging 'common/publish.json') -Value $common

    $installer = [ordered]@{
        schemaVersion = 2
        kind = 'installer'
        format = 'setup-exe'
        commonPublishOnly = $true
        scope = 'perUser'
        requiresAdministrator = $false
        offlineCoreWorkflow = $true
        mutableDataRoot = '%LOCALAPPDATA%\GraphAutoReader'
        installRoot = '%LOCALAPPDATA%\Programs\GraphAutoReader'
        preserveUserDataOnUninstall = $true
        upgradePolicy = 'allow-newer-and-repair-same'
        downgradePolicy = 'blocked-by-default'
    }
    Write-JsonFile -Path (Join-Path $packaging 'installer/installer.json') -Value $installer

    $portable = [ordered]@{
        schemaVersion = 2
        kind = 'portable'
        format = 'zip'
        commonPublishOnly = $true
        requiresAdministrator = $false
        offlineCoreWorkflow = $true
        sentinel = 'portable.mode'
        mutableDataRoot = '.\Data'
        registryConfigurationRequired = $false
        startMenuShortcut = $false
        uninstallEntry = $false
    }
    Write-JsonFile -Path (Join-Path $packaging 'portable/portable.json') -Value $portable

    return [pscustomobject]@{
        Root = $root
        Manifest = Join-Path $packaging 'artifacts.json'
    }
}

function Get-TestSha256 {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

function Get-TestPayloadRecords {
    param([Parameter(Mandatory)][string]$Root)

    $rootFullPath = [System.IO.Path]::GetFullPath($Root)
    [object[]]$records = @(Get-ChildItem -LiteralPath $rootFullPath -Recurse -File | ForEach-Object {
            [pscustomobject]@{
                path = $_.FullName.Substring($rootFullPath.Length).TrimStart('\', '/').Replace('\', '/')
                size = [long]$_.Length
                sha256 = Get-TestSha256 -Path $_.FullName
            }
        })
    $comparison = [System.Comparison[object]]{
        param($left, $right)
        return [System.StringComparer]::Ordinal.Compare([string]$left.path, [string]$right.path)
    }
    [System.Array]::Sort($records, $comparison)
    return @($records)
}

function Get-TestPayloadDigest {
    param([Parameter(Mandatory)][object[]]$Records)

    $material = @($Records | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($material)))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

function Copy-TestTree {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    $null = New-Item -ItemType Directory -Path $Destination -Force
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Update-TestChecksums {
    param([Parameter(Mandatory)][string]$ReleaseRoot)

    $lines = @(Get-ChildItem -LiteralPath $ReleaseRoot -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
            Sort-Object -Property FullName | ForEach-Object {
                "$(Get-TestSha256 -Path $_.FullName)  $($_.Name)"
            })
    $lines | Set-Content -LiteralPath (Join-Path $ReleaseRoot 'SHA256SUMS.txt') -Encoding ascii
}

function New-TestInstaller {
    param(
        [Parameter(Mandatory)][string]$PayloadRoot,
        [Parameter(Mandatory)][string]$Destination
    )

    $payloadRecords = @(Get-TestPayloadRecords -Root $PayloadRoot)
    $payloadDigest = Get-TestPayloadDigest -Records $payloadRecords
    $cacheRoot = Join-Path $testRoot ("_installer-" + $payloadDigest)
    $cachedInstaller = Join-Path $cacheRoot 'GraphReader.Installer.exe'
    if (-not (Test-Path -LiteralPath $cachedInstaller -PathType Leaf)) {
        $null = New-Item -ItemType Directory -Path $cacheRoot -Force
        $payloadArchive = Join-Path $cacheRoot 'payload.zip'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        if (Test-Path -LiteralPath $payloadArchive -PathType Leaf) {
            Remove-Item -LiteralPath $payloadArchive -Force
        }
        [IO.Compression.ZipFile]::CreateFromDirectory($PayloadRoot, $payloadArchive)
        $projectPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\installer\GraphReader.Installer.csproj'))
        $publishRoot = Join-Path $cacheRoot 'publish'
        $arguments = @(
            'publish', $projectPath,
            '--configuration', 'Release',
            '--runtime', 'win-x64',
            '--self-contained', 'false',
            '-p:PublishSingleFile=true',
            '-p:DebugSymbols=false',
            '-p:DebugType=None',
            "-p:InstallerPayloadZip=$payloadArchive",
            '--output', $publishRoot,
            '--nologo')
        $publishOutput = & dotnet @arguments 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) {
            throw "Fixture installer publish failed with exit code $LASTEXITCODE. $publishOutput"
        }

        $publishedInstaller = Join-Path $publishRoot 'GraphReader.Installer.exe'
        if (-not (Test-Path -LiteralPath $publishedInstaller -PathType Leaf)) {
            throw "Fixture installer publish did not emit GraphReader.Installer.exe: $publishedInstaller"
        }
        Copy-Item -LiteralPath $publishedInstaller -Destination $cachedInstaller -Force
    }

    Copy-Item -LiteralPath $cachedInstaller -Destination $Destination -Force
}

function New-TestApplicationExecutable {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [switch]$FullPublish
    )

    $fixtureRoot = Join-Path $testRoot '_application-publish'
    $publishedExecutable = Join-Path $fixtureRoot 'publish/GraphReader.App.exe'
    if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
        $null = New-Item -ItemType Directory -Path $fixtureRoot -Force
        @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <AssemblyName>GraphReader.App</AssemblyName>
    <DebugSymbols>false</DebugSymbols>
    <DebugType>None</DebugType>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $fixtureRoot 'GraphReader.App.csproj') -Encoding utf8
        @'
using System;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
    }
}
'@ | Set-Content -LiteralPath (Join-Path $fixtureRoot 'Program.cs') -Encoding utf8

        $publishOutput = & dotnet publish `
            (Join-Path $fixtureRoot 'GraphReader.App.csproj') `
            --configuration Release `
            --runtime win-x64 `
            --self-contained true `
            -p:PublishSingleFile=false `
            -p:DebugSymbols=false `
            -p:DebugType=None `
            --output (Join-Path $fixtureRoot 'publish') `
            --nologo 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
            throw "Fixture application publish failed. $publishOutput"
        }
    }

    if ($FullPublish) {
        Copy-TestTree -Source (Join-Path $fixtureRoot 'publish') -Destination $Destination
    }
    else {
        Copy-Item -LiteralPath $publishedExecutable -Destination $Destination -Force
    }
}

function Update-TestInstallerRecords {
    param(
        [Parameter(Mandatory)][string]$ReleaseRoot,
        [Parameter(Mandatory)][string]$InstallerName
    )

    $installerPath = Join-Path $ReleaseRoot $InstallerName
    $installerHash = Get-TestSha256 -Path $installerPath
    $metadataPath = Join-Path $ReleaseRoot 'release-metadata.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $metadata.installer.sha256 = $installerHash
    $metadata.installer.provenance.setupSha256 = $installerHash
    $metadata.installer.provenance.installedCopySha256 = $installerHash
    Write-JsonFile -Path $metadataPath -Value $metadata

    $sbomPath = Join-Path $ReleaseRoot 'sbom.cdx.json'
    $sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
    $installerComponents = @($sbom.components | Where-Object { [string]$_.name -eq $InstallerName })
    if ($installerComponents.Count -ne 1) {
        throw 'Synthetic SBOM does not contain exactly one installer component.'
    }
    $installerComponents[0].hashes[0].content = $installerHash
    $installedCopyProperties = @($installerComponents[0].properties | Where-Object {
            [string]$_.name -eq 'graphreader:installedCopySha256'
        })
    if ($installedCopyProperties.Count -ne 1) {
        throw 'Synthetic installer SBOM lacks installed-copy checksum provenance.'
    }
    $installedCopyProperties[0].value = $installerHash
    Write-JsonFile -Path $sbomPath -Value $sbom
    Update-TestChecksums -ReleaseRoot $ReleaseRoot
}

function Update-TestPortableModelManifest {
    param(
        [Parameter(Mandatory)][object]$Fixture,
        [Parameter(Mandatory)][scriptblock]$Mutation
    )

    $editRoot = Join-Path $Fixture.BuildRoot ('zip-edit-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $editRoot -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($Fixture.PortablePath, $editRoot)
    $manifestPath = Join-Path $editRoot 'models/manifest/fixture-model/1.0.0/manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    & $Mutation $manifest
    Write-JsonFile -Path $manifestPath -Value $manifest
    $indexPath = Join-Path $editRoot 'models/production-model-index.json'
    $index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
    $index.models[0].manifest.sha256 = Get-TestSha256 -Path $manifestPath
    Write-JsonFile -Path $indexPath -Value $index
    Remove-Item -LiteralPath $Fixture.PortablePath -Force
    [IO.Compression.ZipFile]::CreateFromDirectory($editRoot, $Fixture.PortablePath)
}

function New-TestDirectoryJunction {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Target
    )

    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force
    $junction = New-Item -ItemType Junction -Path $Path -Target $Target
    if (($junction.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw "Test junction was not created as a reparse point: $Path"
    }
    return $junction.FullName
}

function Update-TestPortableModelIndex {
    param(
        [Parameter(Mandatory)][object]$Fixture,
        [Parameter(Mandatory)][scriptblock]$Mutation
    )

    $editRoot = Join-Path $Fixture.BuildRoot ('index-edit-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $editRoot -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($Fixture.PortablePath, $editRoot)
    $indexPath = Join-Path $editRoot 'models/production-model-index.json'
    $index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
    & $Mutation $index
    Write-JsonFile -Path $indexPath -Value $index
    Remove-Item -LiteralPath $Fixture.PortablePath -Force
    [IO.Compression.ZipFile]::CreateFromDirectory($editRoot, $Fixture.PortablePath)
}

function Add-TestPortableDuplicateJsonProperty {
    param(
        [Parameter(Mandatory)][object]$Fixture,
        [Parameter(Mandatory)][string]$EntryPath,
        [Parameter(Mandatory)][string]$MatchPattern,
        [Parameter(Mandatory)][string]$DuplicateProperty,
        [switch]$RebindManifestHash
    )

    $editRoot = Join-Path $Fixture.BuildRoot ('duplicate-json-edit-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $editRoot -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($Fixture.PortablePath, $editRoot)
    $entryFullPath = Join-Path $editRoot $EntryPath
    $json = Get-Content -LiteralPath $entryFullPath -Raw
    $match = [regex]::Match($json, $MatchPattern)
    if (-not $match.Success) {
        throw "Duplicate JSON test marker was not found in '$EntryPath': $MatchPattern"
    }
    $json = $json.Insert($match.Index + $match.Length, ",`r`n$DuplicateProperty")
    [IO.File]::WriteAllText($entryFullPath, $json, [Text.UTF8Encoding]::new($false))

    if ($RebindManifestHash) {
        $indexPath = Join-Path $editRoot 'models/production-model-index.json'
        $index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
        $index.models[0].manifest.sha256 = Get-TestSha256 -Path $entryFullPath
        Write-JsonFile -Path $indexPath -Value $index
    }

    Remove-Item -LiteralPath $Fixture.PortablePath -Force
    [IO.Compression.ZipFile]::CreateFromDirectory($editRoot, $Fixture.PortablePath)
}

function Add-TestSourceDuplicateJsonProperty {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$MatchPattern,
        [Parameter(Mandatory)][string]$DuplicateProperty
    )

    $json = Get-Content -LiteralPath $ManifestPath -Raw
    $match = [regex]::Match($json, $MatchPattern)
    if (-not $match.Success) {
        throw "Source duplicate JSON test marker was not found: $MatchPattern"
    }
    $json = $json.Insert($match.Index + $match.Length, ",`r`n$DuplicateProperty")
    [IO.File]::WriteAllText($ManifestPath, $json, [Text.UTF8Encoding]::new($false))
}

function Add-TestPortableEntry {
    param(
        [Parameter(Mandatory)][object]$Fixture,
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][byte[]]$Content
    )

    $editRoot = Join-Path $Fixture.BuildRoot ('entry-edit-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $editRoot -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($Fixture.PortablePath, $editRoot)
    $targetPath = Join-Path $editRoot $RelativePath
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force
    [IO.File]::WriteAllBytes($targetPath, $Content)
    Remove-Item -LiteralPath $Fixture.PortablePath -Force
    [IO.Compression.ZipFile]::CreateFromDirectory($editRoot, $Fixture.PortablePath)
}

function Relocate-TestPortableModelResource {
    param(
        [Parameter(Mandatory)][object]$Fixture,
        [Parameter(Mandatory)][ValidateSet('manifest', 'payload', 'notice', 'benchmark_evidence')][string]$ResourceKind
    )

    $editRoot = Join-Path $Fixture.BuildRoot ('relocation-edit-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $editRoot -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($Fixture.PortablePath, $editRoot)
    $indexPath = Join-Path $editRoot 'models/production-model-index.json'
    $index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
    $resource = if ($ResourceKind -eq 'payload') {
        $index.models[0].payloads[0]
    }
    else {
        $index.models[0].$ResourceKind
    }
    $currentRelativePath = [string]$resource.path
    $currentPath = Join-Path (Join-Path $editRoot 'models') $currentRelativePath
    $relocatedRelativePath = "relocated/$ResourceKind/$([IO.Path]::GetFileName($currentRelativePath))"
    $relocatedPath = Join-Path (Join-Path $editRoot 'models') $relocatedRelativePath
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $relocatedPath) -Force
    Move-Item -LiteralPath $currentPath -Destination $relocatedPath
    $resource.path = $relocatedRelativePath
    Write-JsonFile -Path $indexPath -Value $index
    Remove-Item -LiteralPath $Fixture.PortablePath -Force
    [IO.Compression.ZipFile]::CreateFromDirectory($editRoot, $Fixture.PortablePath)
}

function Remove-TestPortableEntry {
    param(
        [Parameter(Mandatory)][object]$Fixture,
        [Parameter(Mandatory)][string]$RelativePath
    )

    $editRoot = Join-Path $Fixture.BuildRoot ('zip-remove-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $editRoot -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($Fixture.PortablePath, $editRoot)
    $target = [IO.Path]::GetFullPath((Join-Path $editRoot $RelativePath))
    $editPrefix = [IO.Path]::GetFullPath($editRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $target.StartsWith($editPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "Cannot remove missing or unsafe portable fixture entry: $RelativePath"
    }
    Remove-Item -LiteralPath $target -Force
    Remove-Item -LiteralPath $Fixture.PortablePath -Force
    [IO.Compression.ZipFile]::CreateFromDirectory($editRoot, $Fixture.PortablePath)
}

function Add-TestPayloadEntryAndSynchronize {
    param(
        [Parameter(Mandatory)][object]$Fixture,
        [Parameter(Mandatory)][string]$RelativePath,
        [Parameter(Mandatory)][byte[]]$Content
    )

    foreach ($root in @($Fixture.CommonRoot, $Fixture.InstallerRoot, $Fixture.PortableRoot)) {
        $targetPath = Join-Path $root $RelativePath
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force
        [IO.File]::WriteAllBytes($targetPath, $Content)
        if ([IO.Path]::GetExtension($RelativePath) -in @('.exe', '.dll')) {
            $buildMetadataPath = Join-Path $root 'build-metadata.json'
            $buildMetadata = Get-Content -LiteralPath $buildMetadataPath -Raw | ConvertFrom-Json
            $normalizedPath = $RelativePath.Replace('\', '/')
            if (@($buildMetadata.applicationPublishFiles) -cnotcontains $normalizedPath) {
                $buildMetadata.applicationPublishFiles += $normalizedPath
                Write-JsonFile -Path $buildMetadataPath -Value $buildMetadata
            }
        }
    }

    $installerName = 'GraphAutoReader-2.0.0-win-x64-setup.exe'
    $portableName = 'GraphAutoReader-2.0.0-win-x64-portable.zip'
    $installerPath = Join-Path $Fixture.ReleaseRoot $installerName
    $portablePath = Join-Path $Fixture.ReleaseRoot $portableName
    Remove-Item -LiteralPath $installerPath, $portablePath -Force
    New-TestInstaller -PayloadRoot $Fixture.InstallerRoot -Destination $installerPath
    [IO.Compression.ZipFile]::CreateFromDirectory($Fixture.PortableRoot, $portablePath)

    $commonRecords = @(Get-TestPayloadRecords -Root $Fixture.CommonRoot)
    $portableRecords = @(Get-TestPayloadRecords -Root $Fixture.PortableRoot)
    $commonDigest = Get-TestPayloadDigest -Records $commonRecords
    $portableDigest = Get-TestPayloadDigest -Records $portableRecords
    $installerHash = Get-TestSha256 -Path $installerPath
    $portableHash = Get-TestSha256 -Path $portablePath

    $metadataPath = Join-Path $Fixture.ReleaseRoot 'release-metadata.json'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $metadata.commonPayload.sha256 = $commonDigest
    $metadata.commonPayload.fileCount = $commonRecords.Count
    $metadata.commonPayload.files = $commonRecords
    $metadata.installer.sha256 = $installerHash
    $metadata.installer.payloadSha256 = $commonDigest
    $metadata.installer.sharedPayloadSha256 = $commonDigest
    $metadata.installer.provenance.setupSha256 = $installerHash
    $metadata.installer.provenance.installedCopySha256 = $installerHash
    $metadata.portable.sha256 = $portableHash
    $metadata.portable.payloadSha256 = $portableDigest
    $metadata.portable.sharedPayloadSha256 = $commonDigest
    Write-JsonFile -Path $metadataPath -Value $metadata

    $sbomPath = Join-Path $Fixture.ReleaseRoot 'sbom.cdx.json'
    $sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
    $sbom.components = @($sbom.components | Where-Object {
            [string]$_.name -notin @($installerName, $portableName, $RelativePath, 'build-metadata.json')
        })
    $sbom.components += [pscustomobject]@{
        type = 'file'
        name = 'build-metadata.json'
        hashes = @([pscustomobject]@{
                alg = 'SHA-256'
                content = (Get-TestSha256 -Path (Join-Path $Fixture.CommonRoot 'build-metadata.json'))
            })
    }
    $addedComponent = [ordered]@{
        type = 'file'
        name = $RelativePath.Replace('\', '/')
        hashes = @([pscustomobject]@{ alg = 'SHA-256'; content = (Get-TestSha256 -Path (Join-Path $Fixture.CommonRoot $RelativePath)) })
    }
    if ([IO.Path]::GetExtension($RelativePath) -in @('.exe', '.dll')) {
        $addedComponent['licenses'] = @([pscustomobject]@{ expression = 'Apache-2.0' })
        $addedComponent['properties'] = @(
            [pscustomobject]@{ name = 'graphreader:releaseAuditComponentIds'; value = 'fixture-app' },
            [pscustomobject]@{ name = 'graphreader:noticePaths'; value = 'LICENSE;NOTICE' })
    }
    $sbom.components += [pscustomobject]$addedComponent
    $sbom.components += [pscustomobject]@{
        type = 'file'
        name = $installerName
        hashes = @([pscustomobject]@{ alg = 'SHA-256'; content = $installerHash })
        licenses = @([pscustomobject]@{ expression = 'Apache-2.0' }, [pscustomobject]@{ expression = 'MIT' })
        properties = @(
            [pscustomobject]@{ name = 'graphreader:releaseAuditComponentIds'; value = 'fixture-app;fixture-runtime' },
            [pscustomobject]@{ name = 'graphreader:noticePaths'; value = 'LICENSE;LICENSES/MIT.txt;NOTICE' },
            [pscustomobject]@{ name = 'graphreader:installedCopyName'; value = 'Uninstall.exe' },
            [pscustomobject]@{ name = 'graphreader:installedCopySha256'; value = $installerHash })
    }
    $sbom.components += [pscustomobject]@{
        type = 'file'
        name = $portableName
        hashes = @([pscustomobject]@{ alg = 'SHA-256'; content = $portableHash })
    }
    Write-JsonFile -Path $sbomPath -Value $sbom
    Update-TestChecksums -ReleaseRoot $Fixture.ReleaseRoot
}

function New-ReleaseFixture {
    param([Parameter(Mandatory)][string]$Name)

    $fixture = New-PackagingFixture -Name $Name -Version '2.0.0'
    $buildRoot = Join-Path $fixture.Root 'build'
    $commonRoot = Join-Path $buildRoot 'common/publish'
    $installerRoot = Join-Path $buildRoot 'installer/staging'
    $portableRoot = Join-Path $buildRoot 'portable/staging'
    $releaseRoot = Join-Path $buildRoot 'release'
    foreach ($path in @($commonRoot, $releaseRoot)) {
        $null = New-Item -ItemType Directory -Path $path -Force
    }

    $releaseGateEvidencePath = Join-Path $fixture.Root 'packaging/release-gate-evidence.json'
    Write-JsonFile -Path $releaseGateEvidencePath -Value ([ordered]@{ schemaVersion = 1; status = 'pass' })
    $releaseGateEvidenceHash = Get-TestSha256 -Path $releaseGateEvidencePath
    $releaseAuditSource = Join-Path $fixture.Root 'packaging/common/release-audit.json'
    Write-JsonFile -Path $releaseAuditSource -Value ([ordered]@{
            schemaVersion = 1
            description = 'Synthetic tracked release audit fixture.'
            mandatoryEvidenceGates = @(@{
                    id = 'fixture-release-gate'
                    description = 'Synthetic fixture release gate.'
                    status = 'pass'
                    evidence = @(@{
                            path = 'packaging/release-gate-evidence.json'
                            sha256 = $releaseGateEvidenceHash
                        })
                    notes = 'Synthetic direct evidence for packaging tests.'
                })
            components = @(
                @{
                    id = 'fixture-app'
                    component = 'Fixture application and installer'
                    version = '2.0.0'
                    source = 'synthetic'
                    sourceRevision = 'fixture'
                    license = 'Apache-2.0'
                    bundledOrDownloaded = 'bundled'
                    artifactSha256 = $null
                    checksumPolicy = 'release-sbom'
                    noticePaths = @('LICENSE', 'NOTICE')
                    commercialUse = $true
                    redistribution = $true
                    reviewStatus = 'reviewed'
                    reviewer = 'fixture'
                    reviewDate = '2026-08-03'
                    notes = 'Synthetic test only.'
                },
                @{
                    id = 'fixture-runtime'
                    component = 'Fixture runtime'
                    version = '10.0.0'
                    source = 'synthetic'
                    sourceRevision = 'fixture'
                    license = 'MIT'
                    bundledOrDownloaded = 'bundled'
                    artifactSha256 = $null
                    checksumPolicy = 'release-sbom'
                    noticePaths = @('LICENSES/MIT.txt')
                    commercialUse = $true
                    redistribution = $true
                    reviewStatus = 'reviewed'
                    reviewer = 'fixture'
                    reviewDate = '2026-08-03'
                    notes = 'Synthetic test only.'
                })
            binaryCoverage = @{
                firstMatchWins = $true
                rules = @(
                    @{ pattern = '*.exe'; componentId = 'fixture-app' },
                    @{ pattern = '*.dll'; componentId = 'fixture-app' })
            }
            emittedArtifactCoverage = @(@{
                    artifactKind = 'installer'
                    fileNameTemplate = 'GraphAutoReader-{version}-{rid}-setup.exe'
                    installedCopyName = 'Uninstall.exe'
                    componentIds = @('fixture-app', 'fixture-runtime')
                    checksumPolicy = 'release-sbom'
                })
        })
    Copy-Item -LiteralPath $releaseAuditSource -Destination (Join-Path $commonRoot 'release-audit.json')

    New-TestApplicationExecutable -Destination (Join-Path $commonRoot 'GraphReader.App.exe')
    $applicationPublishFiles = @(
        'GraphReader.App.exe',
            'GraphReader.App.dll',
            'coreclr.dll',
            'hostfxr.dll',
            'hostpolicy.dll',
            'System.Private.CoreLib.dll',
            'PresentationCore.dll',
            'PresentationFramework.dll',
            'WindowsBase.dll',
            'wpfgfx_cor3.dll')
    foreach ($wpfFile in @($applicationPublishFiles | Where-Object { $_ -ne 'GraphReader.App.exe' })) {
        [IO.File]::WriteAllBytes(
            (Join-Path $commonRoot $wpfFile),
            [Text.Encoding]::UTF8.GetBytes("fixture:$wpfFile"))
    }
    Write-JsonFile -Path (Join-Path $commonRoot 'build-metadata.json') -Value ([ordered]@{
            schemaVersion = 1
            runtimeMode = 'Production'
            productionRuntimeSmokeExitCode = 0
            applicationPublishFiles = $applicationPublishFiles
        })
    'Apache-2.0' | Set-Content -LiteralPath (Join-Path $commonRoot 'LICENSE') -Encoding utf8
    'Graph Auto Reader notice' | Set-Content -LiteralPath (Join-Path $commonRoot 'NOTICE') -Encoding utf8
    'License: `LICENSES/MIT.txt`' | Set-Content -LiteralPath (Join-Path $commonRoot 'THIRD_PARTY_NOTICES.md') -Encoding utf8
    $null = New-Item -ItemType Directory -Path (Join-Path $commonRoot 'LICENSES') -Force
    'MIT License' | Set-Content -LiteralPath (Join-Path $commonRoot 'LICENSES/MIT.txt') -Encoding utf8
    $null = New-Item -ItemType Directory -Path (Join-Path $commonRoot 'contracts') -Force
    '{}' | Set-Content -LiteralPath (Join-Path $commonRoot 'contracts/model-manifest.schema.json') -Encoding utf8
    $modelId = 'fixture-model'
    $modelVersion = '1.0.0'
    $manifestDirectory = Join-Path $commonRoot "models/manifest/$modelId/$modelVersion"
    $runtimeDirectory = Join-Path $commonRoot "models/runtime/$modelId/$modelVersion"
    $noticeDirectory = Join-Path $commonRoot "models/notices/$modelId/$modelVersion"
    $evidenceDirectory = Join-Path $commonRoot "models/evidence/$modelId/$modelVersion"
    foreach ($directory in @($manifestDirectory, $runtimeDirectory, $noticeDirectory, $evidenceDirectory)) {
        $null = New-Item -ItemType Directory -Path $directory -Force
    }
    $noticePath = Join-Path $noticeDirectory 'LICENSE'
    'Synthetic model notice' | Set-Content -LiteralPath $noticePath -Encoding utf8
    $evidencePath = Join-Path $evidenceDirectory 'model-benchmark.json'
    '{"status":"pass"}' | Set-Content -LiteralPath $evidencePath -Encoding utf8
    $modelPath = Join-Path $runtimeDirectory 'test-model.onnx'
    [IO.File]::WriteAllBytes($modelPath, [byte[]](1, 2, 3, 4))
    $modelHash = Get-TestSha256 -Path $modelPath
    $evidenceHash = Get-TestSha256 -Path $evidencePath
    $modelManifestPath = Join-Path $manifestDirectory 'manifest.json'
    Write-JsonFile -Path $modelManifestPath -Value ([ordered]@{
            manifest_version = 1
            model_id = $modelId
            model_version = $modelVersion
            task = 'marker_center'
            source = @{ name = 'synthetic fixture'; url = 'local://fixture'; revision = '1' }
            license = @{ spdx = 'Apache-2.0'; notice_path = 'LICENSE'; reviewed = $true }
            sha256 = $modelHash
            files = @('test-model.onnx')
            inputs = @(@{ name = 'input' })
            outputs = @(@{ name = 'output' })
            commercial_use = $true
            redistribution = $true
            providers = @('cpu')
            benchmarks = @(@{
                    profile = 'fixture'
                    status = 'pass'
                    release_eligible = $true
                    production_approval = $true
                    evidence_path = 'packaging/model-benchmark.json'
                    evidence_sha256 = $evidenceHash
                })
        })
    $manifestHash = Get-TestSha256 -Path $modelManifestPath
    $noticeHash = Get-TestSha256 -Path $noticePath
    Write-JsonFile -Path (Join-Path $commonRoot 'models/production-model-index.json') -Value ([ordered]@{
            schema_version = 1
            models = @([ordered]@{
                    model_id = $modelId
                    model_version = $modelVersion
                    manifest = @{ path = "manifest/$modelId/$modelVersion/manifest.json"; sha256 = $manifestHash }
                    payloads = @(@{
                            declared_path = 'test-model.onnx'
                            path = "runtime/$modelId/$modelVersion/test-model.onnx"
                            sha256 = $modelHash
                        })
                    notice = @{
                        declared_path = 'LICENSE'
                        path = "notices/$modelId/$modelVersion/LICENSE"
                        sha256 = $noticeHash
                    }
                    benchmark_evidence = @{
                        declared_path = 'packaging/model-benchmark.json'
                        path = "evidence/$modelId/$modelVersion/model-benchmark.json"
                        sha256 = $evidenceHash
                    }
                })
        })

    Copy-TestTree -Source (Join-Path $commonRoot 'contracts') -Destination (Join-Path $fixture.Root 'contracts')
    Copy-TestTree -Source (Join-Path $commonRoot 'LICENSES') -Destination (Join-Path $fixture.Root 'LICENSES')
    foreach ($rootFileName in @('LICENSE', 'NOTICE', 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item `
            -LiteralPath (Join-Path $commonRoot $rootFileName) `
            -Destination (Join-Path $fixture.Root $rootFileName)
    }

    Copy-TestTree -Source $commonRoot -Destination $installerRoot
    Copy-TestTree -Source $commonRoot -Destination $portableRoot
    [IO.File]::WriteAllBytes((Join-Path $portableRoot 'portable.mode'), [byte[]]::new(0))

    $installerName = 'GraphAutoReader-2.0.0-win-x64-setup.exe'
    $portableName = 'GraphAutoReader-2.0.0-win-x64-portable.zip'
    $installerPath = Join-Path $releaseRoot $installerName
    $portablePath = Join-Path $releaseRoot $portableName
    New-TestInstaller -PayloadRoot $installerRoot -Destination $installerPath
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($portableRoot, $portablePath)

    $commonRecords = @(Get-TestPayloadRecords -Root $commonRoot)
    $portableRecords = @(Get-TestPayloadRecords -Root $portableRoot)
    $commonDigest = Get-TestPayloadDigest -Records $commonRecords
    $portableDigest = Get-TestPayloadDigest -Records $portableRecords
    Write-JsonFile -Path (Join-Path $releaseRoot 'release-metadata.json') -Value ([ordered]@{
            schemaVersion = 1
            product = 'Graph Auto Reader'
            version = '2.0.0'
            versionSource = 'Directory.Build.props#Project/PropertyGroup/Version'
            rid = 'win-x64'
            gitCommit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
            buildUtc = '2026-08-03T12:00:00Z'
            contractVersion = 1
            commonPayload = @{ sha256 = $commonDigest; fileCount = $commonRecords.Count; files = $commonRecords }
            installer = @{
                fileName = $installerName
                sha256 = Get-TestSha256 -Path $installerPath
                payloadSha256 = $commonDigest
                sharedPayloadSha256 = $commonDigest
                provenance = @{
                    componentIds = @('fixture-app', 'fixture-runtime')
                    licenses = @('Apache-2.0', 'MIT')
                    noticePaths = @('LICENSE', 'LICENSES/MIT.txt', 'NOTICE')
                    checksumPolicy = 'release-sbom'
                    setupSha256 = Get-TestSha256 -Path $installerPath
                    installedCopyName = 'Uninstall.exe'
                    installedCopySha256 = Get-TestSha256 -Path $installerPath
                }
            }
            portable = @{
                fileName = $portableName
                sha256 = Get-TestSha256 -Path $portablePath
                payloadSha256 = $portableDigest
                sharedPayloadSha256 = $commonDigest
            }
            versionPolicy = @{
                releaseBuilds = @(1, 21, 41, 61, 81)
                stablePromotionRelease = '2.0.0'
                upgrade = 'allowed'
                repair = 'same-version reinstall'
                downgrade = 'blocked by default'
            }
        })
    'Release notes' | Set-Content -LiteralPath (Join-Path $releaseRoot 'RELEASE_NOTES.md') -Encoding utf8
    'Known limitations' | Set-Content -LiteralPath (Join-Path $releaseRoot 'KNOWN_LIMITATIONS.md') -Encoding utf8

    $sbomFiles = @(
        $commonRecords
        [pscustomobject]@{ path = $installerName; sha256 = Get-TestSha256 -Path $installerPath }
        [pscustomobject]@{ path = $portableName; sha256 = Get-TestSha256 -Path $portablePath }
    )
    $sbomComponents = @($sbomFiles | ForEach-Object {
            $component = [ordered]@{
                type = 'file'
                name = $_.path
                hashes = @(@{ alg = 'SHA-256'; content = $_.sha256 })
            }
            if ([IO.Path]::GetExtension([string]$_.path) -in @('.exe', '.dll') -and
                [string]$_.path -ne $installerName) {
                $component['licenses'] = @(@{ expression = 'Apache-2.0' })
                $component['properties'] = @(
                    @{ name = 'graphreader:releaseAuditComponentIds'; value = 'fixture-app' },
                    @{ name = 'graphreader:noticePaths'; value = 'LICENSE;NOTICE' })
            }
            elseif ([string]$_.path -eq $installerName) {
                $component['licenses'] = @(
                    @{ expression = 'Apache-2.0' },
                    @{ expression = 'MIT' })
                $component['properties'] = @(
                    @{ name = 'graphreader:releaseAuditComponentIds'; value = 'fixture-app;fixture-runtime' },
                    @{ name = 'graphreader:noticePaths'; value = 'LICENSE;LICENSES/MIT.txt;NOTICE' },
                    @{ name = 'graphreader:installedCopyName'; value = 'Uninstall.exe' },
                    @{ name = 'graphreader:installedCopySha256'; value = $_.sha256 })
            }
            $component
        })
    Write-JsonFile -Path (Join-Path $releaseRoot 'sbom.cdx.json') -Value ([ordered]@{
            bomFormat = 'CycloneDX'
            specVersion = '1.6'
            version = 1
            metadata = @{ component = @{ type = 'application'; name = 'Graph Auto Reader'; version = '2.0.0' } }
            components = $sbomComponents
        })
    Update-TestChecksums -ReleaseRoot $releaseRoot

    $localizationReport = Join-Path $fixture.Root 'localization.json'
    Write-JsonFile -Path $localizationReport -Value (@{
            schema_version = 1
            status = 'pass'
            counts = @{ missing_keys = 0; duplicate_keys = 0; unresolved_resource_references = 0 }
        })
    return [pscustomobject]@{
        Manifest = $fixture.Manifest
        BuildRoot = $buildRoot
        CommonRoot = $commonRoot
        InstallerRoot = $installerRoot
        PortableRoot = $portableRoot
        ReleaseRoot = $releaseRoot
        PortablePath = $portablePath
        LocalizationReport = $localizationReport
    }
}

function New-ModelAuditFixture {
    param([Parameter(Mandatory)][string]$Name)

    $fixture = New-PackagingFixture -Name $Name -Version '2.0.0'
    foreach ($directory in @('contracts', 'models/manifest', 'models', 'LICENSES')) {
        $null = New-Item -ItemType Directory -Path (Join-Path $fixture.Root $directory) -Force
    }
    'Apache-2.0' | Set-Content -LiteralPath (Join-Path $fixture.Root 'LICENSE') -Encoding utf8
    'Fixture notice' | Set-Content -LiteralPath (Join-Path $fixture.Root 'NOTICE') -Encoding utf8
    'License: `LICENSES/MIT.txt`' | Set-Content -LiteralPath (Join-Path $fixture.Root 'THIRD_PARTY_NOTICES.md') -Encoding utf8
    'MIT License' | Set-Content -LiteralPath (Join-Path $fixture.Root 'LICENSES/MIT.txt') -Encoding utf8
    Write-JsonFile -Path (Join-Path $fixture.Root 'contracts/vision-result.schema.json') -Value (@{
            properties = @{ contract_version = @{ const = 1 } }
        })
    $releaseGateEvidencePath = Join-Path $fixture.Root 'packaging/release-gate-evidence.json'
    Write-JsonFile -Path $releaseGateEvidencePath -Value ([ordered]@{ schemaVersion = 1; status = 'pass' })
    $releaseGateEvidenceHash = Get-TestSha256 -Path $releaseGateEvidencePath
    Write-JsonFile -Path (Join-Path $fixture.Root 'packaging/common/release-audit.json') -Value ([ordered]@{
            schemaVersion = 1
            description = 'Synthetic audit-only fixture.'
            mandatoryEvidenceGates = @(@{
                    id = 'fixture-release-gate'
                    description = 'Synthetic fixture release gate.'
                    status = 'pass'
                    evidence = @(@{
                            path = 'packaging/release-gate-evidence.json'
                            sha256 = $releaseGateEvidenceHash
                        })
                    notes = 'Synthetic direct evidence for packaging tests.'
                })
            components = @(@{
                    id = 'fixture-app'
                    component = 'Fixture app'
                    version = '2.0.0'
                    source = 'synthetic'
                    sourceRevision = 'fixture'
                    license = 'Apache-2.0'
                    bundledOrDownloaded = 'bundled'
                    artifactSha256 = $null
                    checksumPolicy = 'release-sbom'
                    noticePaths = @('LICENSE', 'NOTICE')
                    commercialUse = $true
                    redistribution = $true
                    reviewStatus = 'reviewed'
                    reviewer = 'fixture'
                    reviewDate = '2026-08-03'
                    notes = 'Synthetic fixture.'
                })
            binaryCoverage = @{
                firstMatchWins = $true
                rules = @(
                    @{ pattern = '*.exe'; componentId = 'fixture-app' },
                    @{ pattern = '*.dll'; componentId = 'fixture-app' })
            }
            emittedArtifactCoverage = @(@{
                    artifactKind = 'installer'
                    fileNameTemplate = 'GraphAutoReader-{version}-{rid}-setup.exe'
                    installedCopyName = 'Uninstall.exe'
                    componentIds = @('fixture-app')
                    checksumPolicy = 'release-sbom'
                })
        })

    $validModelPath = Join-Path $fixture.Root 'models/valid-model.onnx'
    [IO.File]::WriteAllBytes($validModelPath, [byte[]](5, 6, 7, 8))
    $validModelHash = Get-TestSha256 -Path $validModelPath
    $evidencePath = Join-Path $fixture.Root 'packaging/model-benchmark.json'
    '{"profile":"fixture","status":"pass"}' | Set-Content -LiteralPath $evidencePath -Encoding utf8
    $evidenceHash = Get-TestSha256 -Path $evidencePath
    $baseManifest = [ordered]@{
        manifest_version = 1
        model_id = 'fixture-valid'
        model_version = '1.0.0'
        task = 'marker_center'
        source = @{ name = 'fixture'; url = 'local://fixture'; revision = '1' }
        license = @{ spdx = 'Apache-2.0'; notice_path = 'LICENSE'; reviewed = $true }
        sha256 = $validModelHash
        files = @('valid-model.onnx')
        inputs = @(@{ name = 'input' })
        outputs = @(@{ name = 'output' })
        commercial_use = $true
        redistribution = $true
        providers = @('cpu')
        benchmarks = @(@{
                profile = 'fixture'
                status = 'pass'
                release_eligible = $true
                production_approval = $true
                evidence_path = 'packaging/model-benchmark.json'
                evidence_sha256 = $evidenceHash
            })
    }
    Write-JsonFile -Path (Join-Path $fixture.Root 'models/manifest/valid.json') -Value $baseManifest
    $missingManifest = [ordered]@{}
    foreach ($property in $baseManifest.GetEnumerator()) {
        $missingManifest[$property.Key] = $property.Value
    }
    $missingManifest.model_id = 'fixture-missing'
    $missingManifest.files = @('missing-model.onnx')
    Write-JsonFile -Path (Join-Path $fixture.Root 'models/manifest/missing.json') -Value $missingManifest
    Copy-Item -LiteralPath $testScript -Destination (Join-Path $fixture.Root 'packaging/Test-ReleaseArtifact.ps1')
    Copy-Item -LiteralPath $versionPolicyScript -Destination (Join-Path $fixture.Root 'packaging/VersionPolicy.ps1')

    & git -C $fixture.Root init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the isolated audit fixture repository.' }
    & git -C $fixture.Root add --all
    if ($LASTEXITCODE -ne 0) { throw 'Could not stage the isolated audit fixture.' }
    & git -C $fixture.Root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Create audit fixture'
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit the isolated audit fixture.' }

    return $fixture
}

function New-ValidModelBuildFixture {
    param(
        [Parameter(Mandatory)][string]$Name,
        [switch]$IncludeNoto
    )

    $fixture = New-PackagingFixture -Name $Name -Version '2.0.0'
    foreach ($directory in @('contracts', 'models/manifest', 'models', 'LICENSES')) {
        $null = New-Item -ItemType Directory -Path (Join-Path $fixture.Root $directory) -Force
    }
    'Apache-2.0' | Set-Content -LiteralPath (Join-Path $fixture.Root 'LICENSE') -Encoding utf8
    'Fixture notice' | Set-Content -LiteralPath (Join-Path $fixture.Root 'NOTICE') -Encoding utf8
    'License: `LICENSES/MIT.txt`' | Set-Content -LiteralPath (Join-Path $fixture.Root 'THIRD_PARTY_NOTICES.md') -Encoding utf8
    'MIT License' | Set-Content -LiteralPath (Join-Path $fixture.Root 'LICENSES/MIT.txt') -Encoding utf8
    $sourceRepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    if ($IncludeNoto) {
        Copy-Item `
            -LiteralPath (Join-Path $sourceRepositoryRoot 'THIRD_PARTY_NOTICES.md') `
            -Destination (Join-Path $fixture.Root 'THIRD_PARTY_NOTICES.md') `
            -Force
        $notoFixtureNotice = Get-Content `
            -LiteralPath (Join-Path $fixture.Root 'THIRD_PARTY_NOTICES.md') `
            -Raw
        $referencedLicensePaths = @(
            [regex]::Matches(
                $notoFixtureNotice,
                '(?i)LICENSES/[A-Za-z0-9._+\-]+(?:\.[A-Za-z0-9._+\-]+)*') |
                ForEach-Object { [string]$_.Value } |
                Sort-Object -Unique)
        foreach ($licensePath in $referencedLicensePaths) {
            Copy-Item `
                -LiteralPath (Join-Path $sourceRepositoryRoot $licensePath) `
                -Destination (Join-Path $fixture.Root $licensePath) `
                -Force
        }
    }
    Write-JsonFile -Path (Join-Path $fixture.Root 'contracts/vision-result.schema.json') -Value (@{
            properties = @{ contract_version = @{ const = 1 } }
        })
    $releaseGateEvidencePath = Join-Path $fixture.Root 'packaging/release-gate-evidence.json'
    Write-JsonFile -Path $releaseGateEvidencePath -Value ([ordered]@{ schemaVersion = 1; status = 'pass' })
    $releaseGateEvidenceHash = Get-TestSha256 -Path $releaseGateEvidencePath

    Write-JsonFile -Path (Join-Path $fixture.Root 'packaging/common/release-audit.json') -Value ([ordered]@{
            schemaVersion = 1
            description = 'Synthetic valid-model build fixture.'
            mandatoryEvidenceGates = @(@{
                    id = 'fixture-release-gate'
                    description = 'Synthetic fixture release gate.'
                    status = 'pass'
                    evidence = @(@{
                            path = 'packaging/release-gate-evidence.json'
                            sha256 = $releaseGateEvidenceHash
                        })
                    notes = 'Synthetic direct evidence for packaging tests.'
                })
            components = @(
                @{
                    id = 'fixture-app'
                    component = 'Fixture application'
                    version = '2.0.0'
                    source = 'synthetic'
                    sourceRevision = 'fixture'
                    license = 'Apache-2.0'
                    bundledOrDownloaded = 'bundled'
                    artifactSha256 = $null
                    checksumPolicy = 'release-sbom'
                    noticePaths = @('LICENSE', 'NOTICE')
                    commercialUse = $true
                    redistribution = $true
                    reviewStatus = 'reviewed'
                    reviewer = 'fixture'
                    reviewDate = '2026-08-03'
                    notes = 'Synthetic fixture.'
                },
                @{
                    id = 'fixture-runtime'
                    component = 'Fixture runtime'
                    version = '10.0.0'
                    source = 'synthetic'
                    sourceRevision = 'fixture'
                    license = 'MIT'
                    bundledOrDownloaded = 'bundled'
                    artifactSha256 = $null
                    checksumPolicy = 'release-sbom'
                    noticePaths = @('LICENSES/MIT.txt')
                    commercialUse = $true
                    redistribution = $true
                    reviewStatus = 'reviewed'
                    reviewer = 'fixture'
                    reviewDate = '2026-08-03'
                    notes = 'Synthetic fixture.'
                })
            binaryCoverage = @{
                firstMatchWins = $true
                rules = @(
                    @{ pattern = '*.exe'; componentId = 'fixture-app' },
                    @{ pattern = '*.dll'; componentId = 'fixture-app' })
            }
            emittedArtifactCoverage = @(@{
                    artifactKind = 'installer'
                    fileNameTemplate = 'GraphAutoReader-{version}-{rid}-setup.exe'
                    installedCopyName = 'Uninstall.exe'
                    componentIds = @('fixture-app', 'fixture-runtime')
                    checksumPolicy = 'release-sbom'
                })
        })
    if ($IncludeNoto) {
        $fixtureAuditPath = Join-Path $fixture.Root 'packaging/common/release-audit.json'
        $fixtureAudit = Get-Content -LiteralPath $fixtureAuditPath -Raw | ConvertFrom-Json
        $sourceAudit = Get-Content `
            -LiteralPath (Join-Path $sourceRepositoryRoot 'packaging/common/release-audit.json') `
            -Raw | ConvertFrom-Json
        foreach ($componentId in @(
                'graph-auto-reader',
                'noto-sans-regular',
                'noto-sans-medium',
                'noto-sans-semibold')) {
            $matches = @($sourceAudit.components | Where-Object { [string]$_.id -ceq $componentId })
            if ($matches.Count -ne 1) {
                throw "Source release audit requires exactly one '$componentId' component for the Noto fixture."
            }
            $fixtureAudit.components += $matches[0]
        }
        $fixtureAudit.binaryCoverage.rules = @(
            [pscustomobject]@{
                pattern = 'GraphReader.App.dll'
                componentId = 'graph-auto-reader'
                embeddedComponentIds = @(
                    'noto-sans-regular',
                    'noto-sans-medium',
                    'noto-sans-semibold')
            },
            [pscustomobject]@{ pattern = '*.exe'; componentId = 'fixture-app' },
            [pscustomobject]@{ pattern = '*.dll'; componentId = 'fixture-app' })
        Write-JsonFile -Path $fixtureAuditPath -Value $fixtureAudit
    }

    $validModelPath = Join-Path $fixture.Root 'models/valid-model.onnx'
    [IO.File]::WriteAllBytes($validModelPath, [byte[]](5, 6, 7, 8))
    $validModelHash = Get-TestSha256 -Path $validModelPath
    $evidencePath = Join-Path $fixture.Root 'packaging/model-benchmark.json'
    '{"profile":"fixture","status":"pass"}' | Set-Content -LiteralPath $evidencePath -Encoding utf8
    $evidenceHash = Get-TestSha256 -Path $evidencePath
    Write-JsonFile -Path (Join-Path $fixture.Root 'models/manifest/valid.json') -Value ([ordered]@{
            manifest_version = 1
            model_id = 'fixture-valid'
            model_version = '1.0.0'
            task = 'marker_center'
            source = @{ name = 'fixture'; url = 'local://fixture'; revision = '1' }
            license = @{ spdx = 'Apache-2.0'; notice_path = 'LICENSE'; reviewed = $true }
            sha256 = $validModelHash
            files = @('valid-model.onnx')
            inputs = @(@{ name = 'input' })
            outputs = @(@{ name = 'output' })
            commercial_use = $true
            redistribution = $true
            providers = @('cpu')
            benchmarks = @(@{
                    profile = 'fixture'
                    status = 'pass'
                    release_eligible = $true
                    production_approval = $true
                    evidence_path = 'packaging/model-benchmark.json'
                    evidence_sha256 = $evidenceHash
                })
        })

    $applicationProjectRoot = Join-Path $fixture.Root 'src/GraphReader.App'
    $null = New-Item -ItemType Directory -Path $applicationProjectRoot -Force
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <AssemblyName>GraphReader.App</AssemblyName>
    <RootNamespace>GraphReader.App</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../GraphReader.Inference/GraphReader.Inference.csproj" />
  </ItemGroup>
  <Target Name="RemoveFixtureImportLibrary" AfterTargets="Publish">
    <Delete Files="$(PublishDir)onnxruntime.lib" />
  </Target>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $applicationProjectRoot 'GraphReader.App.csproj') -Encoding utf8
    if ($IncludeNoto) {
        $applicationProjectPath = Join-Path $applicationProjectRoot 'GraphReader.App.csproj'
        $applicationProject = Get-Content -LiteralPath $applicationProjectPath -Raw
        $fontResources = @'
  <ItemGroup>
    <Resource Include="Assets\Fonts\NotoSans-Regular.ttf" />
    <Resource Include="Assets\Fonts\NotoSans-Medium.ttf" />
    <Resource Include="Assets\Fonts\NotoSans-SemiBold.ttf" />
  </ItemGroup>
'@
        $applicationProject = $applicationProject.Replace('</Project>', "$fontResources</Project>")
        $applicationProject | Set-Content -LiteralPath $applicationProjectPath -Encoding utf8

        $fontFixtureRoot = Join-Path $applicationProjectRoot 'Assets/Fonts'
        $null = New-Item -ItemType Directory -Path $fontFixtureRoot -Force
        foreach ($fontName in @('NotoSans-Regular.ttf', 'NotoSans-Medium.ttf', 'NotoSans-SemiBold.ttf')) {
            Copy-Item `
                -LiteralPath (Join-Path $sourceRepositoryRoot "src/GraphReader.App/Assets/Fonts/$fontName") `
                -Destination (Join-Path $fontFixtureRoot $fontName) `
                -Force
        }
    }
    @'
using System;

namespace GraphReader.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
    }
}
'@ | Set-Content -LiteralPath (Join-Path $applicationProjectRoot 'Program.cs') -Encoding utf8

    $inferenceFixtureRoot = Join-Path $fixture.Root 'src/GraphReader.Inference'
    $null = New-Item -ItemType Directory -Path $inferenceFixtureRoot -Force
    Get-ChildItem -LiteralPath ([IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot '..\..\src\GraphReader.Inference'))) -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $inferenceFixtureRoot
    }
    $packageVersionsPath = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\..\Directory.Packages.props'))
    Copy-Item -LiteralPath $packageVersionsPath -Destination (Join-Path $fixture.Root 'Directory.Packages.props')

    $probeRoot = Join-Path $fixture.Root 'tests/ModelStoreProbe'
    $null = New-Item -ItemType Directory -Path $probeRoot -Force
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/GraphReader.Inference/GraphReader.Inference.csproj" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $probeRoot 'ModelStoreProbe.csproj') -Encoding utf8
    @'
using GraphReader.Inference;

var store = new ProductionModelStore(args[0]);
var model = await store.ResolveAsync(args[1], args[2], null, CancellationToken.None);
Console.WriteLine($"{model.Identity.ModelId}|{model.Identity.Version}|{model.Identity.FilePath}");
'@ | Set-Content -LiteralPath (Join-Path $probeRoot 'Program.cs') -Encoding utf8

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\installer\GraphReader.Installer.csproj') -Destination (Join-Path $fixture.Root 'packaging/installer/GraphReader.Installer.csproj')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\installer\Program.cs') -Destination (Join-Path $fixture.Root 'packaging/installer/Program.cs')
    Copy-Item -LiteralPath $testScript -Destination (Join-Path $fixture.Root 'packaging/Test-ReleaseArtifact.ps1')
    Copy-Item -LiteralPath $versionPolicyScript -Destination (Join-Path $fixture.Root 'packaging/VersionPolicy.ps1')
    'Release {{VERSION}} from {{GIT_COMMIT}} at {{BUILD_UTC}}' | Set-Content -LiteralPath (Join-Path $fixture.Root 'packaging/common/RELEASE_NOTES.template.md') -Encoding utf8
    'Synthetic fixture limitation.' | Set-Content -LiteralPath (Join-Path $fixture.Root 'packaging/common/KNOWN_LIMITATIONS.md') -Encoding utf8
    '/release-output/' | Set-Content -LiteralPath (Join-Path $fixture.Root '.gitignore') -Encoding ascii

    & git -C $fixture.Root init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the isolated valid-model fixture repository.' }
    & git -C $fixture.Root add --all
    if ($LASTEXITCODE -ne 0) { throw 'Could not stage the isolated valid-model fixture.' }
    & git -C $fixture.Root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Create valid model fixture'
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit the isolated valid-model fixture.' }

    return [pscustomobject]@{
        Root = $fixture.Root
        Manifest = $fixture.Manifest
        OutputRoot = Join-Path $fixture.Root 'release-output'
        ModelHash = $validModelHash
        ExpectedArchivePath = 'models/runtime/fixture-valid/1.0.0/valid-model.onnx'
    }
}

function New-MultiFileModelBuildFixture {
    param([Parameter(Mandatory)][string]$Name)

    $fixture = New-ValidModelBuildFixture -Name $Name
    $singlePayloadPath = Join-Path $fixture.Root 'models/valid-model.onnx'
    if (-not (Test-Path -LiteralPath $singlePayloadPath -PathType Leaf)) {
        throw 'The base multi-file fixture is missing its single model payload.'
    }
    Remove-Item -LiteralPath $singlePayloadPath -Force

    $parameterPath = Join-Path $fixture.Root 'models/multi/model.onnx'
    $binaryPath = Join-Path $fixture.Root 'models/multi/labels.txt'
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $parameterPath) -Force
    [IO.File]::WriteAllBytes($parameterPath, [byte[]](10, 20, 30, 40))
    [IO.File]::WriteAllBytes($binaryPath, [byte[]](50, 60, 70, 80, 90))
    $parameterHash = Get-TestSha256 -Path $parameterPath
    $binaryHash = Get-TestSha256 -Path $binaryPath
    Write-JsonFile -Path (Join-Path $fixture.Root 'models/manifest/valid.json') -Value ([ordered]@{
            manifest_version = 1
            model_id = 'fixture-multi-file'
            model_version = '1.0.0'
            task = 'marker_center'
            source = @{ name = 'fixture package'; url = 'local://fixture-package'; revision = '1' }
            license = @{ spdx = 'Apache-2.0'; notice_path = 'LICENSE'; reviewed = $true }
            sha256 = $parameterHash
            files = @('multi/model.onnx', 'multi/labels.txt')
            inputs = @(@{ name = 'input' })
            outputs = @(@{ name = 'output' })
            preprocessing = [ordered]@{
                model_payload_sha256 = [ordered]@{
                    'multi/model.onnx' = $parameterHash
                    'multi/labels.txt' = $binaryHash
                }
            }
            commercial_use = $true
            redistribution = $true
            providers = @('cpu')
            benchmarks = @(@{
                    profile = 'fixture'
                    status = 'pass'
                    release_eligible = $true
                    production_approval = $true
                    evidence_path = 'packaging/model-benchmark.json'
                    evidence_sha256 = (Get-TestSha256 -Path (Join-Path $fixture.Root 'packaging/model-benchmark.json'))
                })
        })
    & git -C $fixture.Root add --all
    if ($LASTEXITCODE -ne 0) { throw 'Could not stage the multi-file model fixture.' }
    & git -C $fixture.Root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Use multi-file model payload'
    if ($LASTEXITCODE -ne 0) { throw 'Could not commit the multi-file model fixture.' }

    return [pscustomobject]@{
        Root = $fixture.Root
        Manifest = $fixture.Manifest
        OutputRoot = $fixture.OutputRoot
        ExpectedArtifacts = @(
            [pscustomobject]@{ ArchivePath = 'models/runtime/fixture-multi-file/1.0.0/multi/model.onnx'; Sha256 = $parameterHash },
            [pscustomobject]@{ ArchivePath = 'models/runtime/fixture-multi-file/1.0.0/multi/labels.txt'; Sha256 = $binaryHash })
    }
}

function Invoke-Gate {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $argumentList = @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        ('"' + $testScript + '"')) + @($Arguments | ForEach-Object {
            '"' + ([string]$_).Replace('"', '\"') + '"'
        })
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'powershell.exe'
    $startInfo.Arguments = $argumentList -join ' '
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    $process = [System.Diagnostics.Process]::Start($startInfo)
    $standardOutput = $process.StandardOutput.ReadToEnd()
    $standardError = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = $standardOutput + $standardError
    }
}

function Assert-Case {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Body
    )

    try {
        & $Body
        $script:passed++
        Write-Output "PASS $Name"
    }
    catch {
        $script:failed++
        Write-Output "FAIL $Name`n$($_.Exception.Message)`n$($_.ScriptStackTrace)"
    }
}

function Assert-ExitCode {
    param(
        [Parameter(Mandatory)]
        [object]$Result,

        [Parameter(Mandatory)]
        [int]$Expected,

        [string]$Contains
    )

    if ($Result.ExitCode -ne $Expected) {
        throw "Expected exit $Expected, found $($Result.ExitCode). Output: $($Result.Output)"
    }

    if (-not [string]::IsNullOrWhiteSpace($Contains) -and $Result.Output -notlike "*$Contains*") {
        throw "Expected output to contain '$Contains'. Output: $($Result.Output)"
    }
}

try {
    Assert-Case 'Repository Noto metadata is validated while definition-only artifact proof is explicit' {
        $fixture = New-PackagingFixture -Name 'noto-definition-proof' -Version '0.0.21'
        Copy-Item `
            -LiteralPath (Join-Path $PSScriptRoot '..\common\release-audit.json') `
            -Destination (Join-Path $fixture.Root 'packaging\common\release-audit.json')
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest)
        Assert-ExitCode -Result $result -Expected 0 -Contains 'NotoMetadataChecked'
        foreach ($expectedText in @('ArtifactProofStatus', 'NotoArtifactProofStatus', 'NOT_PERFORMED')) {
            if ($result.Output -notlike "*$expectedText*") {
                throw "Definition-only output does not state '$expectedText'. Output: $($result.Output)"
            }
        }
    }

    Assert-Case 'Repository Noto metadata checksum drift is rejected during definition preflight' {
        $fixture = New-PackagingFixture -Name 'noto-definition-tamper' -Version '0.0.21'
        $auditPath = Join-Path $fixture.Root 'packaging\common\release-audit.json'
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\common\release-audit.json') -Destination $auditPath
        $audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
        $regular = @($audit.components | Where-Object { [string]$_.id -eq 'noto-sans-regular' })
        $regular[0].artifactSha256 = ('0' * 64)
        Write-JsonFile -Path $auditPath -Value $audit
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest)
        Assert-ExitCode -Result $result -Expected 1 -Contains "checksum differs for 'noto-sans-regular'"
    }

    Assert-Case 'Internal build passes definition preflight' {
        $fixture = New-PackagingFixture -Name 'internal'
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest)
        Assert-ExitCode -Result $result -Expected 0 -Contains 'PASS'
    }

    Assert-Case 'Internal build is refused by the release gate' {
        $fixture = New-PackagingFixture -Name 'non-release-refusal'
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest, '-RequireReleaseVersion')
        Assert-ExitCode -Result $result -Expected 1 -Contains 'cannot be published'
    }

    Assert-Case 'Post-milestone release cadence build passes the release gate' {
        $fixture = New-PackagingFixture -Name 'release' -Version '2.0.1'
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest, '-RequireReleaseVersion')
        Assert-ExitCode -Result $result -Expected 0 -Contains 'PASS'
    }

    Assert-Case 'Stable 2.0.0 promotion passes the release gate' {
        $fixture = New-PackagingFixture -Name 'stable-release' -Version '2.0.0'
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest, '-RequireReleaseVersion')
        Assert-ExitCode -Result $result -Expected 0 -Contains 'PASS'
    }

    Assert-Case 'Verifier rejects required-content overwrite mapping' {
        $fixture = New-PackagingFixture -Name 'verifier-required-content-overwrite'
        $definitionPath = Join-Path $fixture.Root 'packaging/common/publish.json'
        $definition = Get-Content -LiteralPath $definitionPath -Raw | ConvertFrom-Json
        $licenseMapping = @($definition.requiredContent | Where-Object { [string]$_.source -eq 'LICENSE' })
        $licenseMapping[0].target = 'GraphReader.App.exe'
        Write-JsonFile -Path $definitionPath -Value $definition
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'unapproved source-to-target mapping'
    }

    Assert-Case 'Verifier rejects extra required-content mapping' {
        $fixture = New-PackagingFixture -Name 'verifier-extra-required-content'
        $definitionPath = Join-Path $fixture.Root 'packaging/common/publish.json'
        $definition = Get-Content -LiteralPath $definitionPath -Raw | ConvertFrom-Json
        $definition.requiredContent += [pscustomobject]@{ source = 'article.pdf'; target = 'article.pdf' }
        Write-JsonFile -Path $definitionPath -Value $definition
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'only the approved distribution mappings'
    }

    Assert-Case 'Out-of-range version is rejected' {
        $fixture = New-PackagingFixture -Name 'invalid-version' -Version '0.100.1'
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'must use x.y.z'
    }

    Assert-Case 'Legacy manifest version cannot override the central version' {
        $fixture = New-PackagingFixture -Name 'version-mismatch'
        (Get-Content -LiteralPath $fixture.Manifest -Raw).Replace('0.0.18', '0.0.19') |
            Set-Content -LiteralPath $fixture.Manifest -Encoding utf8
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest)
        Assert-ExitCode -Result $result -Expected 0 -Contains 'Version'
        if ($result.Output -notlike '*0.0.18*') {
            throw "Expected the central version 0.0.18. Output: $($result.Output)"
        }
    }

    Assert-Case 'Definition path traversal is rejected' {
        $fixture = New-PackagingFixture -Name 'path-traversal'
        $manifest = Get-Content -LiteralPath $fixture.Manifest -Raw | ConvertFrom-Json
        $manifest.portable.definition = '../outside.json'
        Write-JsonFile -Path $fixture.Manifest -Value $manifest
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'leaves its allowed root'
    }

    Assert-Case 'Unsafe installer filename template is rejected' {
        $fixture = New-PackagingFixture -Name 'unsafe-installer-template'
        $manifest = Get-Content -LiteralPath $fixture.Manifest -Raw | ConvertFrom-Json
        $manifest.installer.fileNameTemplate = '../GraphAutoReader-{version}-{rid}-setup.exe'
        Write-JsonFile -Path $fixture.Manifest -Value $manifest
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'Installer filename template is invalid'
    }

    Assert-Case 'Unsafe portable filename template is rejected' {
        $fixture = New-PackagingFixture -Name 'unsafe-portable-template'
        $manifest = Get-Content -LiteralPath $fixture.Manifest -Raw | ConvertFrom-Json
        $manifest.portable.fileNameTemplate = 'C:\temp\GraphAutoReader-{version}-{rid}-portable.zip'
        Write-JsonFile -Path $fixture.Manifest -Value $manifest
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'Portable filename template is invalid'
    }

    Assert-Case 'Portable registry dependency is rejected' {
        $fixture = New-PackagingFixture -Name 'registry'
        $definitionPath = Join-Path $fixture.Root 'packaging/portable/portable.json'
        $definition = Get-Content -LiteralPath $definitionPath -Raw | ConvertFrom-Json
        $definition.registryConfigurationRequired = $true
        Write-JsonFile -Path $definitionPath -Value $definition
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'registry configuration'
    }

    Assert-Case 'Portable mutable data cannot escape Data' {
        $fixture = New-PackagingFixture -Name 'portable-path'
        $definitionPath = Join-Path $fixture.Root 'packaging/portable/portable.json'
        $definition = Get-Content -LiteralPath $definitionPath -Raw | ConvertFrom-Json
        $definition.mutableDataRoot = '%LOCALAPPDATA%\GraphAutoReader'
        Write-JsonFile -Path $definitionPath -Value $definition
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'Portable data root is invalid'
    }

    Assert-Case 'Installed mutable data must use LocalAppData' {
        $fixture = New-PackagingFixture -Name 'installed-path'
        $definitionPath = Join-Path $fixture.Root 'packaging/installer/installer.json'
        $definition = Get-Content -LiteralPath $definitionPath -Raw | ConvertFrom-Json
        $definition.mutableDataRoot = '.\Data'
        Write-JsonFile -Path $definitionPath -Value $definition
        $result = Invoke-Gate -Arguments @('-ManifestPath', $fixture.Manifest)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'Installed data root is invalid'
    }

    Assert-Case 'Localization failures block verification' {
        $fixture = New-PackagingFixture -Name 'localization'
        $reportPath = Join-Path $fixture.Root 'localization-report.json'
        Write-JsonFile -Path $reportPath -Value ([ordered]@{
                schema_version = 1
                status = 'fail'
                counts = [ordered]@{
                    missing_keys = 1
                    duplicate_keys = 0
                    unresolved_resource_references = 0
                }
            })
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-LocalizationReportPath', $reportPath)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'Localization audit status'
    }

    Assert-Case 'Complete release with mixed-case WPF filenames passes every deep verification gate' {
        $fixture = New-ReleaseFixture -Name 'complete-release'
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-RequireReleaseVersion',
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 0 -Contains 'ArtifactFilesChecked'
        if ($result.Output -notlike '*ArtifactProofStatus*PASS*') {
            throw "Artifact verification did not report PASS proof status. Output: $($result.Output)"
        }
    }

    Assert-Case 'Unexpected release-root content is rejected' {
        $fixture = New-ReleaseFixture -Name 'release-allowlist'
        'unexpected' | Set-Content -LiteralPath (Join-Path $fixture.ReleaseRoot 'extra.txt') -Encoding utf8
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'allowlist differs'
    }

    Assert-Case 'Unexpected private document is rejected even when payloads and release records agree' {
        $fixture = New-ReleaseFixture -Name 'portable-payload-allowlist'
        Add-TestPayloadEntryAndSynchronize `
            -Fixture $fixture `
            -RelativePath 'notes.txt' `
            -Content ([Text.Encoding]::UTF8.GetBytes('synthetic private notes'))
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'outside the release payload allowlist: notes.txt'
    }

    Assert-Case 'One known culture satellite resource directory is allowed' {
        $fixture = New-ReleaseFixture -Name 'culture-resource-allowlist'
        Add-TestPayloadEntryAndSynchronize `
            -Fixture $fixture `
            -RelativePath 'fr/GraphReader.App.resources.dll' `
            -Content ([byte[]](1, 3, 3, 7))
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 0 -Contains 'PASS'
    }

    Assert-Case 'Unknown nested resource directory is rejected' {
        $fixture = New-ReleaseFixture -Name 'unknown-resource-directory'
        Add-TestPayloadEntryAndSynchronize `
            -Fixture $fixture `
            -RelativePath 'not-a-culture/GraphReader.App.resources.dll' `
            -Content ([byte[]](1, 3, 3, 7))
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'invalid or missing application publish path'
    }

    Assert-Case 'Missing packaged tracked release audit is rejected' {
        $fixture = New-ReleaseFixture -Name 'missing-release-audit'
        Remove-TestPortableEntry -Fixture $fixture -RelativePath 'release-audit.json'
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'missing required root entry: release-audit.json'
    }

    Assert-Case 'Framework-dependent portable payload is rejected' {
        $fixture = New-ReleaseFixture -Name 'framework-dependent-portable'
        Remove-TestPortableEntry -Fixture $fixture -RelativePath 'coreclr.dll'
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'missing required root entry: coreclr.dll'
    }

    Assert-Case 'Shared installer payload drift is rejected' {
        $fixture = New-ReleaseFixture -Name 'shared-payload'
        'drift' | Add-Content -LiteralPath (Join-Path $fixture.InstallerRoot 'NOTICE') -Encoding utf8
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'Installer and common payloads'
    }

    Assert-Case 'Release checksum tampering is rejected' {
        $fixture = New-ReleaseFixture -Name 'checksum-tamper'
        'tamper' | Add-Content -LiteralPath (Join-Path $fixture.ReleaseRoot 'RELEASE_NOTES.md') -Encoding utf8
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'Release checksum differs'
    }

    Assert-Case 'Unsupported downgrade metadata is rejected' {
        $fixture = New-ReleaseFixture -Name 'downgrade-policy'
        $metadataPath = Join-Path $fixture.ReleaseRoot 'release-metadata.json'
        $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        $metadata.versionPolicy.downgrade = 'allowed'
        Write-JsonFile -Path $metadataPath -Value $metadata
        Update-TestChecksums -ReleaseRoot $fixture.ReleaseRoot
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'block unsupported downgrades'
    }

    Assert-Case 'Release metadata requires canonical Z timestamp' {
        $fixture = New-ReleaseFixture -Name 'noncanonical-build-utc'
        $metadataPath = Join-Path $fixture.ReleaseRoot 'release-metadata.json'
        $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        $metadata.buildUtc = '2026-08-03T12:00:00+00:00'
        Write-JsonFile -Path $metadataPath -Value $metadata
        Update-TestChecksums -ReleaseRoot $fixture.ReleaseRoot
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'canonical UTC timestamp ending in Z'
    }

    Assert-Case 'A four-byte MZ fake installer is rejected' {
        $fixture = New-ReleaseFixture -Name 'fake-installer'
        $installerName = 'GraphAutoReader-2.0.0-win-x64-setup.exe'
        [IO.File]::WriteAllBytes(
            (Join-Path $fixture.ReleaseRoot $installerName),
            [byte[]](0x4d, 0x5a, 0x00, 0x00))
        Update-TestInstallerRecords -ReleaseRoot $fixture.ReleaseRoot -InstallerName $installerName
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'could not execute'
    }

    Assert-Case 'A fake GraphReader.App.exe is rejected after all release records are synchronized' {
        $fixture = New-ReleaseFixture -Name 'fake-application'
        Add-TestPayloadEntryAndSynchronize `
            -Fixture $fixture `
            -RelativePath 'GraphReader.App.exe' `
            -Content ([byte[]](0x4d, 0x5a, 0x00))
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'GraphReader.App.exe is not a complete Windows PE executable'
    }

    Assert-Case 'A prohibited model license is rejected' {
        $fixture = New-ReleaseFixture -Name 'prohibited-model-license'
        Update-TestPortableModelManifest -Fixture $fixture -Mutation {
            param($manifest)
            $manifest.license.spdx = 'GPL-3.0-only'
        }
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'prohibited or unclear license'
    }

    Assert-Case 'A noncommercial model is rejected' {
        $fixture = New-ReleaseFixture -Name 'noncommercial-model'
        Update-TestPortableModelManifest -Fixture $fixture -Mutation {
            param($manifest)
            $manifest.commercial_use = $false
        }
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'not approved for commercial redistribution'
    }

    Assert-Case 'A nonredistributable model is rejected' {
        $fixture = New-ReleaseFixture -Name 'nonredistributable-model'
        Update-TestPortableModelManifest -Fixture $fixture -Mutation {
            param($manifest)
            $manifest.redistribution = $false
        }
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'not approved for commercial redistribution'
    }

    Assert-Case 'Model notice traversal is rejected' {
        $fixture = New-ReleaseFixture -Name 'model-notice-traversal'
        Update-TestPortableModelManifest -Fixture $fixture -Mutation {
            param($manifest)
            $manifest.license.notice_path = '../LICENSE'
        }
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'unsafe notice path'
    }

    Assert-Case 'A model without a release-eligible benchmark is rejected' {
        $fixture = New-ReleaseFixture -Name 'model-benchmark'
        Update-TestPortableModelManifest -Fixture $fixture -Mutation {
            param($manifest)
            $manifest.benchmarks[0].release_eligible = $false
        }
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'no passing release-eligible benchmark'
    }

    Assert-Case 'Duplicate package index identity path and hash fields are rejected before conversion' {
        $mutations = @(
            @{
                Name = 'identity'
                Pattern = '"model_id"\s*:\s*"fixture-model"'
                Duplicate = '        "model_id": "fixture-model"'
            },
            @{
                Name = 'path'
                Pattern = '(?s)"manifest"\s*:\s*\{.*?"path"\s*:\s*"manifest/fixture-model/1\.0\.0/manifest\.json"'
                Duplicate = '          "path": "manifest/fixture-model/1.0.0/manifest.json"'
            },
            @{
                Name = 'hash'
                Pattern = '(?s)"manifest"\s*:\s*\{.*?"sha256"\s*:\s*"[0-9a-fA-F]{64}"'
                Duplicate = ('          "sha256": "' + ('0' * 64) + '"')
            })
        foreach ($mutation in $mutations) {
            $fixture = New-ReleaseFixture -Name ("duplicate-index-json-" + $mutation.Name)
            Add-TestPortableDuplicateJsonProperty `
                -Fixture $fixture `
                -EntryPath 'models/production-model-index.json' `
                -MatchPattern $mutation.Pattern `
                -DuplicateProperty $mutation.Duplicate
            $result = Invoke-Gate -Arguments @(
                '-ManifestPath', $fixture.Manifest,
                '-ArtifactRoot', $fixture.ReleaseRoot,
                '-LocalizationReportPath', $fixture.LocalizationReport)
            Assert-ExitCode -Result $result -Expected 1 -Contains 'duplicate JSON property'
        }
    }

    Assert-Case 'Duplicate manifest license provider and approval fields are rejected before conversion' {
        $mutations = @(
            @{
                Name = 'license'
                Pattern = '"spdx"\s*:\s*"Apache-2\.0"'
                Duplicate = '        "spdx": "GPL-3.0-only"'
            },
            @{
                Name = 'provider'
                Pattern = '"providers"\s*:\s*\[\s*"cpu"\s*\]'
                Duplicate = '    "providers": [ "directml" ]'
            },
            @{
                Name = 'approval'
                Pattern = '"production_approval"\s*:\s*true'
                Duplicate = '        "production_approval": false'
            })
        foreach ($mutation in $mutations) {
            $fixture = New-ReleaseFixture -Name ("duplicate-manifest-json-" + $mutation.Name)
            Add-TestPortableDuplicateJsonProperty `
                -Fixture $fixture `
                -EntryPath 'models/manifest/fixture-model/1.0.0/manifest.json' `
                -MatchPattern $mutation.Pattern `
                -DuplicateProperty $mutation.Duplicate `
                -RebindManifestHash
            $result = Invoke-Gate -Arguments @(
                '-ManifestPath', $fixture.Manifest,
                '-ArtifactRoot', $fixture.ReleaseRoot,
                '-LocalizationReportPath', $fixture.LocalizationReport)
            Assert-ExitCode -Result $result -Expected 1 -Contains 'duplicate JSON property'
        }
    }

    Assert-Case 'Production model index traversal is rejected for every resource kind' {
        $mutations = @(
            @{ Name = 'manifest'; Apply = { param($index) $index.models[0].manifest.path = '../manifest.json' } },
            @{ Name = 'payload'; Apply = { param($index) $index.models[0].payloads[0].path = '../model.onnx' } },
            @{ Name = 'notice'; Apply = { param($index) $index.models[0].notice.path = '../LICENSE' } },
            @{ Name = 'benchmark evidence'; Apply = { param($index) $index.models[0].benchmark_evidence.path = '../benchmark.json' } })
        foreach ($mutation in $mutations) {
            $fixture = New-ReleaseFixture -Name ("model-index-traversal-" + ([string]$mutation.Name).Replace(' ', '-'))
            Update-TestPortableModelIndex -Fixture $fixture -Mutation $mutation.Apply
            $result = Invoke-Gate -Arguments @(
                '-ManifestPath', $fixture.Manifest,
                '-ArtifactRoot', $fixture.ReleaseRoot,
                '-LocalizationReportPath', $fixture.LocalizationReport)
            Assert-ExitCode `
                -Result $result `
                -Expected 1 `
                -Contains "invalid resource for 'fixture-model'"
        }
    }

    Assert-Case 'Safe checksummed in-root model resource relocation is rejected' {
        foreach ($resourceKind in @('manifest', 'payload', 'notice', 'benchmark_evidence')) {
            $fixture = New-ReleaseFixture -Name ("model-resource-relocation-" + $resourceKind)
            Relocate-TestPortableModelResource -Fixture $fixture -ResourceKind $resourceKind
            $result = Invoke-Gate -Arguments @(
                '-ManifestPath', $fixture.Manifest,
                '-ArtifactRoot', $fixture.ReleaseRoot,
                '-LocalizationReportPath', $fixture.LocalizationReport)
            Assert-ExitCode -Result $result -Expected 1 -Contains 'path is not canonical'
        }
    }

    Assert-Case 'Unlisted production model data is rejected' {
        $fixture = New-ReleaseFixture -Name 'unlisted-production-model-data'
        Add-TestPortableEntry `
            -Fixture $fixture `
            -RelativePath 'models/runtime/fixture-model/1.0.0/unlisted.bin' `
            -Content ([byte[]](9, 8, 7))
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'unlisted production model data'
    }

    Assert-Case 'AuditOnly rejects duplicate source manifest approval license redistribution and nested fields' {
        $mutations = @(
            @{
                Name = 'redistribution'
                Property = 'redistribution'
                Pattern = '"redistribution"\s*:\s*true'
                Duplicate = '    "redistribution": false'
            },
            @{
                Name = 'license'
                Property = 'spdx'
                Pattern = '"spdx"\s*:\s*"Apache-2\.0"'
                Duplicate = '        "spdx": "GPL-3.0-only"'
            },
            @{
                Name = 'approval'
                Property = 'production_approval'
                Pattern = '"production_approval"\s*:\s*true'
                Duplicate = '        "production_approval": false'
            },
            @{
                Name = 'source'
                Property = 'revision'
                Pattern = '"revision"\s*:\s*"1"'
                Duplicate = '        "revision": "attacker-revision"'
            })
        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        foreach ($mutation in $mutations) {
            $fixture = New-ValidModelBuildFixture -Name ("duplicate-source-manifest-" + $mutation.Name)
            $sourceManifestPath = Join-Path $fixture.Root 'models/manifest/valid.json'
            Add-TestSourceDuplicateJsonProperty `
                -ManifestPath $sourceManifestPath `
                -MatchPattern $mutation.Pattern `
                -DuplicateProperty $mutation.Duplicate
            $audit = & $buildScript `
                -ManifestPath $fixture.Manifest `
                -OutputRoot $fixture.OutputRoot `
                -AuditOnly
            $duplicateBlockers = @($audit.Blockers | Where-Object {
                    $_ -like "*duplicate JSON property '$($mutation.Property)'*"
                })
            if ($audit.ReleaseReady -or $audit.ArtifactsEmitted -or $duplicateBlockers.Count -ne 1) {
                throw "AuditOnly did not reject duplicate source property '$($mutation.Property)' exactly once. Actual blockers: $(@($audit.Blockers) -join ' | ')"
            }
        }
    }

    Assert-Case 'Duplicate release model IDs are rejected' {
        $fixture = New-ValidModelBuildFixture -Name 'duplicate-model-id'
        Copy-Item `
            -LiteralPath (Join-Path $fixture.Root 'models/manifest/valid.json') `
            -Destination (Join-Path $fixture.Root 'models/manifest/duplicate.json')
        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $audit = & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $fixture.OutputRoot -AuditOnly
        if ($audit.ReleaseReady -or $audit.ArtifactsEmitted -or
            @($audit.Blockers | Where-Object { $_ -like "Model ID '*' is duplicated by manifests:*" }).Count -ne 1) {
            throw "Duplicate release model ID was not rejected exactly. Actual blockers: $(@($audit.Blockers) -join ' | ')"
        }
    }

    Assert-Case 'Production model staging rejects traversal for payload notice and benchmark evidence' {
        $cases = @(
            @{
                Name = 'payload'
                Expected = 'uses an unsafe model file path'
                Apply = { param($manifest) $manifest.files = @('../outside.onnx') }
            },
            @{
                Name = 'notice'
                Expected = 'missing or unsafe notice path'
                Apply = { param($manifest) $manifest.license.notice_path = '../LICENSE' }
            },
            @{
                Name = 'evidence'
                Expected = 'leaves the repository'
                Apply = { param($manifest) $manifest.benchmarks[0].evidence_path = '../benchmark.json' }
            })
        foreach ($case in $cases) {
            $fixture = New-ValidModelBuildFixture -Name ("staging-traversal-" + $case.Name)
            $manifestPath = Join-Path $fixture.Root 'models/manifest/valid.json'
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            & $case.Apply $manifest
            Write-JsonFile -Path $manifestPath -Value $manifest
            $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
            $audit = & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $fixture.OutputRoot -AuditOnly
            if ($audit.ReleaseReady -or $audit.ArtifactsEmitted -or
                @($audit.Blockers | Where-Object { $_ -like "*$($case.Expected)*" }).Count -eq 0) {
                throw "Staging did not reject $($case.Name) traversal. Actual blockers: $(@($audit.Blockers) -join ' | ')"
            }
            if (Test-Path -LiteralPath $fixture.OutputRoot) {
                throw "Traversal audit emitted staging for $($case.Name)."
            }
        }
    }

    Assert-Case 'Production model staging rejects reparse-point ancestors for every resource kind' {
        foreach ($kind in @('manifest', 'payload', 'notice', 'evidence')) {
            $fixture = New-ValidModelBuildFixture -Name ("staging-reparse-" + $kind)
            $manifestPath = Join-Path $fixture.Root 'models/manifest/valid.json'
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $junctionPath = $null
            try {
                switch ($kind) {
                    'manifest' {
                        $source = Join-Path $fixture.Root 'manifest-source'
                        $null = New-Item -ItemType Directory -Path $source -Force
                        Move-Item -LiteralPath $manifestPath -Destination (Join-Path $source 'valid.json')
                        [IO.Directory]::Delete((Split-Path -Parent $manifestPath))
                        $junctionPath = New-TestDirectoryJunction `
                            -Path (Join-Path $fixture.Root 'models/manifest') `
                            -Target $source
                    }
                    'payload' {
                        $source = Join-Path $fixture.Root 'payload-source'
                        $null = New-Item -ItemType Directory -Path $source -Force
                        Move-Item `
                            -LiteralPath (Join-Path $fixture.Root 'models/valid-model.onnx') `
                            -Destination (Join-Path $source 'valid-model.onnx')
                        $junctionPath = New-TestDirectoryJunction `
                            -Path (Join-Path $fixture.Root 'models/payload-link') `
                            -Target $source
                        $manifest.files = @('payload-link/valid-model.onnx')
                        Write-JsonFile -Path $manifestPath -Value $manifest
                    }
                    'notice' {
                        $source = Join-Path $fixture.Root 'notice-source'
                        $null = New-Item -ItemType Directory -Path $source -Force
                        Copy-Item -LiteralPath (Join-Path $fixture.Root 'LICENSE') -Destination (Join-Path $source 'LICENSE')
                        $junctionPath = New-TestDirectoryJunction `
                            -Path (Join-Path $fixture.Root 'support/notice-link') `
                            -Target $source
                        $manifest.license.notice_path = 'support/notice-link/LICENSE'
                        Write-JsonFile -Path $manifestPath -Value $manifest
                    }
                    'evidence' {
                        $source = Join-Path $fixture.Root 'evidence-source'
                        $null = New-Item -ItemType Directory -Path $source -Force
                        Move-Item `
                            -LiteralPath (Join-Path $fixture.Root 'packaging/model-benchmark.json') `
                            -Destination (Join-Path $source 'model-benchmark.json')
                        $junctionPath = New-TestDirectoryJunction `
                            -Path (Join-Path $fixture.Root 'packaging/evidence-link') `
                            -Target $source
                        $manifest.benchmarks[0].evidence_path = 'packaging/evidence-link/model-benchmark.json'
                        Write-JsonFile -Path $manifestPath -Value $manifest
                    }
                }

                $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
                $audit = & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $fixture.OutputRoot -AuditOnly
                if ($audit.ReleaseReady -or $audit.ArtifactsEmitted -or
                    @($audit.Blockers | Where-Object { $_ -like '*uses a reparse-point path*' }).Count -eq 0) {
                    throw "Staging did not reject the $kind reparse-point ancestor. Actual blockers: $(@($audit.Blockers) -join ' | ')"
                }
                if (Test-Path -LiteralPath $fixture.OutputRoot) {
                    throw "Reparse-point audit emitted staging for $kind."
                }
            }
            finally {
                if (-not [string]::IsNullOrWhiteSpace($junctionPath) -and
                    (Test-Path -LiteralPath $junctionPath)) {
                    [IO.Directory]::Delete($junctionPath)
                }
            }
        }
    }

    Assert-Case 'A published binary missing from the SBOM is rejected' {
        $fixture = New-ReleaseFixture -Name 'sbom-coverage'
        $sbomPath = Join-Path $fixture.ReleaseRoot 'sbom.cdx.json'
        $sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
        $sbom.components = @($sbom.components | Where-Object { [string]$_.name -ne 'GraphReader.App.exe' })
        Write-JsonFile -Path $sbomPath -Value $sbom
        Update-TestChecksums -ReleaseRoot $fixture.ReleaseRoot
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'SBOM file coverage count differs'
    }

    Assert-Case 'Installer SBOM provenance removal is rejected' {
        $fixture = New-ReleaseFixture -Name 'installer-sbom-provenance'
        $sbomPath = Join-Path $fixture.ReleaseRoot 'sbom.cdx.json'
        $sbom = Get-Content -LiteralPath $sbomPath -Raw | ConvertFrom-Json
        $installerName = 'GraphAutoReader-2.0.0-win-x64-setup.exe'
        $installerComponent = @($sbom.components | Where-Object { [string]$_.name -eq $installerName })
        $installerComponent[0].properties = @($installerComponent[0].properties | Where-Object {
                [string]$_.name -ne 'graphreader:noticePaths'
            })
        Write-JsonFile -Path $sbomPath -Value $sbom
        Update-TestChecksums -ReleaseRoot $fixture.ReleaseRoot
        $result = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $fixture.ReleaseRoot,
            '-LocalizationReportPath', $fixture.LocalizationReport)
        Assert-ExitCode -Result $result -Expected 1 -Contains 'Installer SBOM notice paths differ'
    }

    Assert-Case 'Valid redistributable model and embedded Noto payload pass generated artifact verification' {
        $fixture = New-ValidModelBuildFixture -Name 'valid-model-build' -IncludeNoto
        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))

        $result = & $buildScript `
            -ManifestPath $fixture.Manifest `
            -OutputRoot $fixture.OutputRoot
        if (-not $result.ArtifactsEmitted -or -not $result.ReleaseReady) {
            throw 'The valid model fixture did not emit release-ready artifacts.'
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead([string]$result.PortableArtifact)
        try {
            $modelEntries = @($archive.Entries | Where-Object {
                    $_.FullName.Replace('\', '/') -ceq $fixture.ExpectedArchivePath
                })
            if ($modelEntries.Count -ne 1) {
                throw "Expected exactly one portable model at '$($fixture.ExpectedArchivePath)', found $($modelEntries.Count)."
            }
            $modelStream = $modelEntries[0].Open()
            $algorithm = [Security.Cryptography.SHA256]::Create()
            try {
                $archiveHash = ([BitConverter]::ToString($algorithm.ComputeHash($modelStream))).Replace('-', '').ToLowerInvariant()
            }
            finally {
                $algorithm.Dispose()
                $modelStream.Dispose()
            }
            if ($archiveHash -ne $fixture.ModelHash) {
                throw "Portable model checksum differs. Expected $($fixture.ModelHash), found $archiveHash."
            }
        }
        finally {
            $archive.Dispose()
        }

        $metadata = Get-Content -LiteralPath (Join-Path $result.CommonPublish 'build-metadata.json') -Raw | ConvertFrom-Json
        $packagedModels = @($metadata.packagedModelArtifacts)
        if ($packagedModels.Count -ne 1 -or
            [string]$packagedModels[0].archivePath -cne $fixture.ExpectedArchivePath -or
            [string]$packagedModels[0].sha256 -ne $fixture.ModelHash) {
            throw 'Build metadata does not record the exact packaged model path and checksum.'
        }

        $indexPath = Join-Path $result.CommonPublish 'models/production-model-index.json'
        $index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
        if ([int]$index.schema_version -ne 1 -or @($index.models).Count -ne 1 -or
            [string]$index.models[0].manifest.path -cne 'manifest/fixture-valid/1.0.0/manifest.json' -or
            [string]$index.models[0].payloads[0].path -cne 'runtime/fixture-valid/1.0.0/valid-model.onnx' -or
            -not ([string]$index.models[0].notice.path).StartsWith('notices/fixture-valid/1.0.0/', [StringComparison]::Ordinal) -or
            -not ([string]$index.models[0].benchmark_evidence.path).StartsWith('evidence/fixture-valid/1.0.0/', [StringComparison]::Ordinal)) {
            throw 'Production model index does not use the canonical schema-v1 layout.'
        }

        $probeProject = Join-Path $fixture.Root 'tests/ModelStoreProbe/ModelStoreProbe.csproj'
        $probeOutput = & dotnet run `
            --project $probeProject `
            --configuration Release `
            -- `
            (Join-Path $result.CommonPublish 'models') `
            'fixture-valid' `
            '1.0.0'
        if ($LASTEXITCODE -ne 0) {
            throw "ProductionModelStore probe failed with exit code $LASTEXITCODE."
        }
        $expectedProbe = "fixture-valid|1.0.0|$(Join-Path $result.CommonPublish $fixture.ExpectedArchivePath)"
        if ([string]($probeOutput | Select-Object -Last 1) -cne $expectedProbe) {
            throw 'ProductionModelStore did not resolve the exact common-publish model bytes.'
        }

        $verification = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $result.ArtifactRoot,
            '-RequireReleaseVersion')
        Assert-ExitCode -Result $verification -Expected 0 -Contains 'NotoArtifactProofStatus'
        if ($verification.Output -notlike '*NotoArtifactProofStatus*PASS*') {
            throw "Generated Noto artifact verification did not report PASS. Output: $($verification.Output)"
        }

        Add-Content `
            -LiteralPath (Join-Path $fixture.Root 'THIRD_PARTY_NOTICES.md') `
            -Value 'tampered tracked notice' `
            -Encoding utf8
        $tamperResult = Invoke-Gate -Arguments @(
            '-ManifestPath', $fixture.Manifest,
            '-ArtifactRoot', $result.ArtifactRoot,
            '-RequireReleaseVersion')
        Assert-ExitCode `
            -Result $tamperResult `
            -Expected 1 `
            -Contains "Packaged Noto legal file differs from tracked source 'THIRD_PARTY_NOTICES.md'"
    }

    Assert-Case 'Valid multi-file model payloads are emitted and verified independently' {
        $fixture = New-MultiFileModelBuildFixture -Name 'valid-multi-file-model-build'
        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $result = & $buildScript `
            -ManifestPath $fixture.Manifest `
            -OutputRoot $fixture.OutputRoot
        if (-not $result.ArtifactsEmitted -or -not $result.ReleaseReady) {
            throw 'The valid multi-file model fixture did not emit release-ready artifacts.'
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead([string]$result.PortableArtifact)
        try {
            foreach ($expected in $fixture.ExpectedArtifacts) {
                $matches = @($archive.Entries | Where-Object {
                        $_.FullName.Replace('\', '/') -ceq $expected.ArchivePath
                    })
                if ($matches.Count -ne 1) {
                    throw "Expected one portable model payload at '$($expected.ArchivePath)', found $($matches.Count)."
                }
                $stream = $matches[0].Open()
                $algorithm = [Security.Cryptography.SHA256]::Create()
                try {
                    $actualHash = ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
                }
                finally {
                    $algorithm.Dispose()
                    $stream.Dispose()
                }
                if ($actualHash -ne $expected.Sha256) {
                    throw "Portable model payload checksum differs for '$($expected.ArchivePath)'."
                }
            }
        }
        finally {
            $archive.Dispose()
        }

        $metadata = Get-Content -LiteralPath (Join-Path $result.CommonPublish 'build-metadata.json') -Raw | ConvertFrom-Json
        $packagedModels = @($metadata.packagedModelArtifacts)
        if ($packagedModels.Count -ne 2) {
            throw "Expected two packaged model metadata records, found $($packagedModels.Count)."
        }
        foreach ($expected in $fixture.ExpectedArtifacts) {
            $metadataMatch = @($packagedModels | Where-Object {
                    [string]$_.archivePath -ceq $expected.ArchivePath -and
                    [string]$_.sha256 -eq $expected.Sha256
                })
            if ($metadataMatch.Count -ne 1) {
                throw "Build metadata does not record exact multi-file payload '$($expected.ArchivePath)'."
            }
        }
    }

    Assert-Case 'Multi-file model missing a declared payload checksum is rejected' {
        $fixture = New-MultiFileModelBuildFixture -Name 'multi-file-missing-checksum'
        $modelManifestPath = Join-Path $fixture.Root 'models/manifest/valid.json'
        $modelManifest = Get-Content -LiteralPath $modelManifestPath -Raw | ConvertFrom-Json
        $modelManifest.preprocessing.model_payload_sha256.PSObject.Properties.Remove('multi/labels.txt')
        Write-JsonFile -Path $modelManifestPath -Value $modelManifest
        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $audit = & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $fixture.OutputRoot -AuditOnly
        $expected = "Multi-file model manifest 'valid.json' has no payload checksum for 'multi/labels.txt'."
        if ($audit.ReleaseReady -or $audit.ArtifactsEmitted -or @($audit.Blockers) -notcontains $expected) {
            throw "Missing-checksum multi-file manifest was not rejected exactly. Actual blockers: $(@($audit.Blockers) -join ' | ')"
        }
    }

    Assert-Case 'Multi-file model checksum map with an undeclared payload is rejected' {
        $fixture = New-MultiFileModelBuildFixture -Name 'multi-file-extra-checksum'
        $modelManifestPath = Join-Path $fixture.Root 'models/manifest/valid.json'
        $modelManifest = Get-Content -LiteralPath $modelManifestPath -Raw | ConvertFrom-Json
        $modelManifest.preprocessing.model_payload_sha256 | Add-Member `
            -NotePropertyName 'multi/extra.bin' `
            -NotePropertyValue ('0' * 64)
        Write-JsonFile -Path $modelManifestPath -Value $modelManifest
        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $audit = & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $fixture.OutputRoot -AuditOnly
        $expected = "Multi-file model manifest 'valid.json' has a payload checksum for undeclared file 'multi/extra.bin'."
        if ($audit.ReleaseReady -or $audit.ArtifactsEmitted -or @($audit.Blockers) -notcontains $expected) {
            throw "Extra-checksum multi-file manifest was not rejected exactly. Actual blockers: $(@($audit.Blockers) -join ' | ')"
        }
    }

    Assert-Case 'Multi-file model payload hash mismatch is rejected' {
        $fixture = New-MultiFileModelBuildFixture -Name 'multi-file-hash-mismatch'
        $modelManifestPath = Join-Path $fixture.Root 'models/manifest/valid.json'
        $modelManifest = Get-Content -LiteralPath $modelManifestPath -Raw | ConvertFrom-Json
        $modelManifest.preprocessing.model_payload_sha256.'multi/labels.txt' = ('0' * 64)
        Write-JsonFile -Path $modelManifestPath -Value $modelManifest
        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $audit = & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $fixture.OutputRoot -AuditOnly
        $expected = "Model file checksum does not match manifest 'valid.json': multi/labels.txt."
        if ($audit.ReleaseReady -or $audit.ArtifactsEmitted -or @($audit.Blockers) -notcontains $expected) {
            throw "Hash-mismatched multi-file payload was not rejected exactly. Actual blockers: $(@($audit.Blockers) -join ' | ')"
        }
    }

    Assert-Case 'Required-content target cannot overwrite the application publish' {
        $fixture = New-ValidModelBuildFixture -Name 'required-content-overwrite'
        $definitionPath = Join-Path $fixture.Root 'packaging/common/publish.json'
        $definition = Get-Content -LiteralPath $definitionPath -Raw | ConvertFrom-Json
        $licenseMapping = @($definition.requiredContent | Where-Object { [string]$_.source -eq 'LICENSE' })
        $licenseMapping[0].target = 'GraphReader.App.exe'
        Write-JsonFile -Path $definitionPath -Value $definition
        & git -C $fixture.Root add --all
        & git -C $fixture.Root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Mutate required content target'
        if ($LASTEXITCODE -ne 0) { throw 'Could not commit the required-content mutation fixture.' }

        $commonPublishRoot = Join-Path $fixture.OutputRoot '0.0.21-win-x64/common/publish'
        $null = New-Item -ItemType Directory -Path $commonPublishRoot -Force
        $appPath = Join-Path $commonPublishRoot 'GraphReader.App.exe'
        [IO.File]::WriteAllBytes($appPath, [byte[]](0x4d, 0x5a, 0x01))
        $beforeHash = Get-TestSha256 -Path $appPath
        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $blocked = $false
        try {
            & $buildScript `
                -ManifestPath $fixture.Manifest `
                -OutputRoot $fixture.OutputRoot `
                -SkipPublish | Out-Null
        }
        catch {
            $blocked = $_.Exception.Message -like "*unapproved source-to-target mapping*LICENSE*GraphReader.App.exe*"
        }
        if (-not $blocked) {
            throw 'Build did not reject the reserved required-content target mapping.'
        }
        if ((Get-TestSha256 -Path $appPath) -ne $beforeHash) {
            throw 'Build overwrote GraphReader.App.exe before rejecting the invalid mapping.'
        }
    }

    Assert-Case 'Build rejects SkipPublish before artifact emission' {
        $fixture = New-ValidModelBuildFixture -Name 'build-skip-publish'
        $commonPublishRoot = Join-Path $fixture.OutputRoot '0.0.21-win-x64/common/publish'
        $null = New-Item -ItemType Directory -Path $commonPublishRoot -Force
        [IO.File]::WriteAllBytes((Join-Path $commonPublishRoot 'GraphReader.App.exe'), [byte[]](0x4d, 0x5a, 0x00))
        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $blocked = $false
        try {
            & $buildScript `
                -ManifestPath $fixture.Manifest `
                -OutputRoot $fixture.OutputRoot `
                -SkipPublish | Out-Null
        }
        catch {
            $blocked = $_.Exception.Message -like '*-SkipPublish*not permitted*'
        }
        if (-not $blocked) {
            throw 'Build did not reject SkipPublish artifact emission.'
        }
        if (Test-Path -LiteralPath (Join-Path $fixture.OutputRoot '0.0.21-win-x64/release')) {
            throw 'Build created release artifacts after rejecting the fake application executable.'
        }
    }

    Assert-Case 'Build invokes definition preflight before creating staging' {
        $fixture = New-ValidModelBuildFixture -Name 'build-definition-preflight'
        $definitionPath = Join-Path $fixture.Root 'packaging/common/publish.json'
        $definition = Get-Content -LiteralPath $definitionPath -Raw | ConvertFrom-Json
        $definition.selfContained = $false
        Write-JsonFile -Path $definitionPath -Value $definition
        & git -C $fixture.Root add --all
        & git -C $fixture.Root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Mutate common publish definition'
        if ($LASTEXITCODE -ne 0) { throw 'Could not commit the definition-preflight mutation fixture.' }

        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $blocked = $false
        try {
            & $buildScript `
                -ManifestPath $fixture.Manifest `
                -OutputRoot $fixture.OutputRoot | Out-Null
        }
        catch {
            $blocked = $_.Exception.Message -like '*Common publish must be self-contained*'
        }
        if (-not $blocked) {
            throw 'Build did not invoke the standalone definition preflight.'
        }
        if (Test-Path -LiteralPath $fixture.OutputRoot) {
            throw 'Build created staging before rejecting the invalid common publish definition.'
        }
    }

    Assert-Case 'Blocked mandatory evidence gate keeps release audit closed' {
        $fixture = New-ValidModelBuildFixture -Name 'blocked-mandatory-evidence-gate'
        $releaseAuditPath = Join-Path $fixture.Root 'packaging/common/release-audit.json'
        $releaseAudit = Get-Content -LiteralPath $releaseAuditPath -Raw | ConvertFrom-Json
        $releaseAudit.mandatoryEvidenceGates[0].status = 'blocked'
        $releaseAudit.mandatoryEvidenceGates[0].notes = 'Fixture direct evidence remains incomplete.'
        $releaseAudit.mandatoryEvidenceGates[0].evidence = @()
        Write-JsonFile -Path $releaseAuditPath -Value $releaseAudit
        & git -C $fixture.Root add --all
        & git -C $fixture.Root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Block mandatory evidence gate'
        if ($LASTEXITCODE -ne 0) { throw 'Could not commit the blocked-gate fixture.' }

        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $audit = & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $fixture.OutputRoot -AuditOnly
        $expected = "Mandatory release evidence gate 'fixture-release-gate' is blocked: Fixture direct evidence remains incomplete."
        if (@($audit.Blockers) -notcontains $expected -or $audit.ReleaseReady -or $audit.ArtifactsEmitted) {
            throw "Blocked mandatory evidence gate was not enforced exactly. Actual blockers: $(@($audit.Blockers) -join ' | ')"
        }
    }

    Assert-Case 'Mandatory evidence gate cannot pass without direct evidence' {
        $fixture = New-ValidModelBuildFixture -Name 'empty-mandatory-evidence-gate'
        $releaseAuditPath = Join-Path $fixture.Root 'packaging/common/release-audit.json'
        $releaseAudit = Get-Content -LiteralPath $releaseAuditPath -Raw | ConvertFrom-Json
        $releaseAudit.mandatoryEvidenceGates[0].evidence = @()
        Write-JsonFile -Path $releaseAuditPath -Value $releaseAudit
        & git -C $fixture.Root add --all
        & git -C $fixture.Root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Remove mandatory evidence'
        if ($LASTEXITCODE -ne 0) { throw 'Could not commit the empty-evidence fixture.' }

        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $audit = & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $fixture.OutputRoot -AuditOnly
        $expected = "Mandatory release evidence gate 'fixture-release-gate' is marked pass without direct evidence."
        if (@($audit.Blockers) -notcontains $expected -or $audit.ReleaseReady -or $audit.ArtifactsEmitted) {
            throw "Empty mandatory evidence was not rejected exactly. Actual blockers: $(@($audit.Blockers) -join ' | ')"
        }
    }

    Assert-Case 'Mandatory evidence checksum mismatch keeps release audit closed' {
        $fixture = New-ValidModelBuildFixture -Name 'mandatory-evidence-checksum-mismatch'
        $releaseAuditPath = Join-Path $fixture.Root 'packaging/common/release-audit.json'
        $releaseAudit = Get-Content -LiteralPath $releaseAuditPath -Raw | ConvertFrom-Json
        $releaseAudit.mandatoryEvidenceGates[0].evidence[0].sha256 = ('0' * 64)
        Write-JsonFile -Path $releaseAuditPath -Value $releaseAudit
        & git -C $fixture.Root add --all
        & git -C $fixture.Root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Tamper mandatory evidence checksum'
        if ($LASTEXITCODE -ne 0) { throw 'Could not commit the evidence-checksum fixture.' }

        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $audit = & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $fixture.OutputRoot -AuditOnly
        $expected = "Mandatory release evidence gate 'fixture-release-gate' evidence checksum differs for 'packaging/release-gate-evidence.json'."
        if (@($audit.Blockers) -notcontains $expected -or $audit.ReleaseReady -or $audit.ArtifactsEmitted) {
            throw "Tampered mandatory evidence was not rejected exactly. Actual blockers: $(@($audit.Blockers) -join ' | ')"
        }
    }

    Assert-Case 'Exact-binary coverage rejects different published bytes' {
        $fixture = New-ValidModelBuildFixture -Name 'exact-binary-mismatch'
        $releaseAuditPath = Join-Path $fixture.Root 'packaging/common/release-audit.json'
        $releaseAudit = Get-Content -LiteralPath $releaseAuditPath -Raw | ConvertFrom-Json
        $releaseAudit.components[0].checksumPolicy = 'exact-binary'
        $releaseAudit.components[0].artifactSha256 = ('0' * 64)
        Write-JsonFile -Path $releaseAuditPath -Value $releaseAudit
        & git -C $fixture.Root add --all
        & git -C $fixture.Root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Require exact binary'
        if ($LASTEXITCODE -ne 0) { throw 'Could not commit the exact-binary fixture.' }

        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $blocked = $false
        try {
            & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $fixture.OutputRoot | Out-Null
        }
        catch {
            $blocked = $_.Exception.Message -like "*differs from exact release-audit binary 'fixture-app'*"
        }
        if (-not $blocked) {
            throw 'Different published bytes were not rejected by exact-binary coverage.'
        }
        $releaseRoot = Join-Path $fixture.OutputRoot '0.0.21-win-x64/release'
        if (Test-Path -LiteralPath $releaseRoot -PathType Container) {
            $emittedArtifacts = @(Get-ChildItem -LiteralPath $releaseRoot -File -ErrorAction SilentlyContinue)
            if ($emittedArtifacts.Count -gt 0) {
                throw 'Exact-binary mismatch emitted release artifacts.'
            }
        }
    }

    Assert-Case 'Common publish requires exact reviewed OpenCV evidence when the release audit selects it' {
        $fixture = New-ValidModelBuildFixture -Name 'reviewed-opencv-release-input'
        $releaseAuditPath = Join-Path $fixture.Root 'packaging/common/release-audit.json'
        $releaseAudit = Get-Content -LiteralPath $releaseAuditPath -Raw | ConvertFrom-Json
        $component = $releaseAudit.components[0] | ConvertTo-Json -Depth 10 | ConvertFrom-Json
        $component.id = 'opencvsharp-native'
        $component.checksumPolicy = 'exact-binary'
        $component | Add-Member -NotePropertyName artifactSha256 -NotePropertyValue ('1' * 64) -Force
        $releaseAudit.components = @($releaseAudit.components) + @($component)
        Write-JsonFile -Path $releaseAuditPath -Value $releaseAudit
        & git -C $fixture.Root add --all
        & git -C $fixture.Root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Select reviewed OpenCV runtime'
        if ($LASTEXITCODE -ne 0) { throw 'Could not commit the reviewed OpenCV fixture.' }

        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $audit = & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $fixture.OutputRoot -AuditOnly
        $expected = 'The exact reviewed source-built OpenCV evidence root is required for the common release publish.'
        if ($audit.ReleaseReady -or $audit.ArtifactsEmitted -or @($audit.Blockers) -notcontains $expected) {
            throw "Missing reviewed OpenCV release input was not rejected exactly. Actual blockers: $(@($audit.Blockers) -join ' | ')"
        }
        if (-not [bool]$audit.ReviewedOpenCvRuntimeRequired -or
            -not [string]::IsNullOrWhiteSpace([string]$audit.ReviewedOpenCvRuntimeSha256)) {
            throw 'Reviewed OpenCV audit metadata did not remain fail closed without exact runtime bytes.'
        }
    }

    Assert-Case 'Common publish requires exact reviewed PDFium evidence when the release audit selects it' {
        $fixture = New-ValidModelBuildFixture -Name 'reviewed-pdfium-release-input'
        $releaseAuditPath = Join-Path $fixture.Root 'packaging/common/release-audit.json'
        $releaseAudit = Get-Content -LiteralPath $releaseAuditPath -Raw | ConvertFrom-Json
        $component = $releaseAudit.components[0] | ConvertTo-Json -Depth 10 | ConvertFrom-Json
        $component.id = 'pdfium-native'
        $component.checksumPolicy = 'exact-binary'
        $component | Add-Member -NotePropertyName artifactSha256 -NotePropertyValue ('2' * 64) -Force
        $releaseAudit.components = @($releaseAudit.components) + @($component)
        Write-JsonFile -Path $releaseAuditPath -Value $releaseAudit
        & git -C $fixture.Root add --all
        & git -C $fixture.Root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Select reviewed PDFium runtime'
        if ($LASTEXITCODE -ne 0) { throw 'Could not commit the reviewed PDFium fixture.' }

        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $audit = & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $fixture.OutputRoot -AuditOnly
        $expected = 'The exact reviewed PDFium evidence root is required for the common release publish.'
        if ($audit.ReleaseReady -or $audit.ArtifactsEmitted -or @($audit.Blockers) -notcontains $expected) {
            throw "Missing reviewed PDFium release input was not rejected exactly. Actual blockers: $(@($audit.Blockers) -join ' | ')"
        }
        if (-not [bool]$audit.ReviewedPdfiumRuntimeRequired -or
            -not [string]::IsNullOrWhiteSpace([string]$audit.ReviewedPdfiumRuntimeSha256)) {
            throw 'Reviewed PDFium audit metadata did not remain fail closed without exact runtime bytes.'
        }
    }

    Assert-Case 'Rejected research manifest does not become a missing release payload' {
        $fixture = New-ModelAuditFixture -Name 'rejected-research-model-audit'
        $missingPath = Join-Path $fixture.Root 'models/manifest/missing.json'
        $missing = Get-Content -LiteralPath $missingPath -Raw | ConvertFrom-Json
        $missing.benchmarks = @([pscustomobject]@{
                status = 'fail'
                release_eligible = $false
                production_approval = $false
            })
        Write-JsonFile -Path $missingPath -Value $missing
        & git -C $fixture.Root add --all
        & git -C $fixture.Root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Reject research model'
        if ($LASTEXITCODE -ne 0) { throw 'Could not commit the rejected research-model fixture.' }

        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $audit = & $buildScript -ManifestPath $fixture.Manifest -AuditOnly
        $obsoleteBlocker = "Model manifest 'missing.json' references missing model file 'missing-model.onnx'."
        if (@($audit.Blockers) -contains $obsoleteBlocker) {
            throw 'A rejected research manifest was incorrectly treated as a required release payload.'
        }
        if (@($audit.ApprovedProductionModelTasks) -notcontains 'marker_center') {
            throw 'The checksum-approved marker-center fixture did not satisfy its explicit required task.'
        }
    }

    Assert-Case 'Explicit required production task blocks an otherwise valid model set' {
        $fixture = New-ValidModelBuildFixture -Name 'required-model-task-audit'
        $definitionPath = Join-Path $fixture.Root 'packaging/common/publish.json'
        $definition = Get-Content -LiteralPath $definitionPath -Raw | ConvertFrom-Json
        $definition.requiredModelTasks = @('marker_center', 'ocr_detection')
        Write-JsonFile -Path $definitionPath -Value $definition
        & git -C $fixture.Root add --all
        & git -C $fixture.Root -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet -m 'Require OCR detector'
        if ($LASTEXITCODE -ne 0) { throw 'Could not commit the required-model-task fixture.' }

        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $audit = & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $fixture.OutputRoot -AuditOnly
        $expected = "Required production model task 'ocr_detection' has no checksum-verified, release-eligible, production-approved payload."
        if (@($audit.Blockers) -notcontains $expected) {
            throw "Missing explicit required-task blocker. Actual blockers: $(@($audit.Blockers) -join ' | ')"
        }
        if ($audit.ReleaseReady -or $audit.ArtifactsEmitted) {
            throw 'The missing required OCR detector did not keep the release audit closed.'
        }
    }

    Assert-Case 'One valid model cannot mask a second manifest missing its model file' {
        $fixture = New-ModelAuditFixture -Name 'missing-model-audit'
        $buildScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Build-Windows.ps1'))
        $audit = & $buildScript -ManifestPath $fixture.Manifest -AuditOnly
        if ($audit.ReleaseReady -or $audit.ArtifactsEmitted) {
            throw 'Audit-only mode incorrectly reported the missing-model fixture as release-ready or emitted artifacts.'
        }
        if ([int]$audit.RedistributableModelFileCount -ne 1) {
            throw "Expected one valid redistributable model, found $($audit.RedistributableModelFileCount)."
        }
        $expectedBlocker = "Model manifest 'missing.json' references missing model file 'missing-model.onnx'."
        if (@($audit.Blockers) -notcontains $expectedBlocker) {
            throw "Missing per-manifest blocker. Actual blockers: $(@($audit.Blockers) -join ' | ')"
        }

        $outputRoot = Join-Path $fixture.Root 'release-output'
        $blocked = $false
        try {
            & $buildScript -ManifestPath $fixture.Manifest -OutputRoot $outputRoot | Out-Null
        }
        catch {
            $blocked = $_.Exception.Message -like "*missing.json*missing-model.onnx*"
        }
        if (-not $blocked) {
            throw 'Normal packaging did not fail closed on the missing model file.'
        }
        if (Test-Path -LiteralPath $outputRoot) {
            throw 'Normal packaging created output despite the missing model release blocker.'
        }
    }
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

Write-Output "Packaging verification tests: $passed passed, $failed failed."
if ($failed -ne 0) {
    exit 1
}
