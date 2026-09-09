# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param(
    [string]$ManifestPath,
    [string]$OutputRoot,
    [switch]$SkipPublish,
    [switch]$Force,
    [switch]$AuditOnly,
    [string]$ReviewedOpenCvEvidenceRoot,
    [string]$ReviewedPdfiumEvidenceRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'VersionPolicy.ps1')

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $PSScriptRoot 'artifacts.json'
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $PSScriptRoot '..\artifacts\windows'
}

function Resolve-SafeChildPath {
    param(
        [Parameter(Mandatory)]
        [string]$Parent,

        [Parameter(Mandatory)]
        [string]$Child
    )

    if ([System.IO.Path]::IsPathRooted($Child)) {
        throw "Packaging paths must be relative: $Child"
    }

    $parentFullPath = [System.IO.Path]::GetFullPath($Parent)
    $childFullPath = [System.IO.Path]::GetFullPath((Join-Path $parentFullPath $Child))
    $parentPrefix = $parentFullPath.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

    if (-not $childFullPath.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Packaging path leaves its parent directory: $Child"
    }

    return $childFullPath
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Content
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [object]$Value
    )

    Write-Utf8NoBom -Path $Path -Content (($Value | ConvertTo-Json -Depth 20) + [Environment]::NewLine)
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

function Copy-DirectoryContent {
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

function Get-CentralVersion {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    return Get-GraphReaderCentralVersion -RepositoryRoot $RepositoryRoot
}

function Get-GitCommit {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $LASTEXITCODE = 0
    $commit = (& git -C $RepositoryRoot rev-parse HEAD 2>$null | Select-Object -First 1)
    $gitExitCode = $LASTEXITCODE
    if ($gitExitCode -ne 0 -or [string]::IsNullOrWhiteSpace([string]$commit)) {
        throw 'A Git commit is required to build Windows release artifacts.'
    }

    return ([string]$commit).Trim()
}

function Get-GitWorkingTreeDirty {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $LASTEXITCODE = 0
    $status = & git -C $RepositoryRoot status --porcelain --untracked-files=normal 2>$null
    $gitExitCode = $LASTEXITCODE
    if ($gitExitCode -ne 0) {
        throw 'Git working-tree status could not be determined.'
    }

    return -not [string]::IsNullOrWhiteSpace(($status -join [Environment]::NewLine))
}

function Get-ContractVersion {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $visionSchemaPath = Join-Path $RepositoryRoot 'contracts\vision-result.schema.json'
    $schema = Get-Content -LiteralPath $visionSchemaPath -Raw | ConvertFrom-Json
    $contractVersion = $schema.properties.contract_version.const
    if ($null -eq $contractVersion) {
        throw "Contract version is missing from $visionSchemaPath."
    }

    return [int]$contractVersion
}

function Resolve-ModelArchiveRelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$DeclaredPath,

        [Parameter(Mandatory)]
        [string]$ModelId,

        [Parameter(Mandatory)]
        [string]$ModelVersion
    )

    foreach ($component in @($ModelId, $ModelVersion)) {
        if ([string]::IsNullOrWhiteSpace($component) -or
            $component -in @('.', '..') -or
            $component.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
            $component.Contains('/') -or $component.Contains('\')) {
            throw "Model identity uses an unsafe path component: $component"
        }
    }

    if ([System.IO.Path]::IsPathRooted($DeclaredPath)) {
        throw "Model artifact paths must be relative: $DeclaredPath"
    }

    $normalized = $DeclaredPath.Replace('\', '/')
    if ($normalized -ne $normalized.Trim()) {
        throw "Model artifact paths cannot have leading or trailing whitespace: $DeclaredPath"
    }
    $segments = @($normalized.Split('/'))
    $invalidFileNameCharacters = [System.IO.Path]::GetInvalidFileNameChars()
    foreach ($segment in $segments) {
        if ([string]::IsNullOrWhiteSpace($segment) -or
            $segment -in @('.', '..') -or
            $segment.EndsWith('.') -or
            $segment.EndsWith(' ') -or
            $segment.IndexOfAny($invalidFileNameCharacters) -ge 0) {
            throw "Model artifact path is not a safe Windows relative path: $DeclaredPath"
        }

        $deviceName = [System.IO.Path]::GetFileNameWithoutExtension($segment).ToUpperInvariant()
        if ($deviceName -in @('CON', 'PRN', 'AUX', 'NUL') -or
            $deviceName -match '^(COM|LPT)[1-9]$') {
            throw "Model artifact path uses a reserved Windows name: $DeclaredPath"
        }
    }

    return "models/runtime/$ModelId/$ModelVersion/$normalized"
}

function Resolve-ModelSupportingFile {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$DeclaredPath,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ([string]::IsNullOrWhiteSpace($DeclaredPath) -or
        [System.IO.Path]::IsPathRooted($DeclaredPath) -or
        $DeclaredPath -ne $DeclaredPath.Trim()) {
        throw "$Description must use a nonempty relative path: $DeclaredPath"
    }

    $normalized = $DeclaredPath.Replace('\', '/')
    if (@($normalized.Split('/')) -contains '..') {
        throw "$Description leaves the repository: $DeclaredPath"
    }

    $resolved = Resolve-SafeChildPath -Parent $RepositoryRoot -Child $normalized
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "$Description is missing: $DeclaredPath"
    }
    Assert-NoModelReparsePoint -RepositoryRoot $RepositoryRoot -Path $resolved -Description $Description
    if ((Get-Item -LiteralPath $resolved).Length -eq 0) {
        throw "$Description cannot be empty: $DeclaredPath"
    }

    return $resolved
}

function Assert-NoModelReparsePoint {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $repositoryFullPath = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    $current = Get-Item -LiteralPath ([IO.Path]::GetFullPath($Path)) -Force
    $reachedRoot = $false
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description uses a reparse-point path: $($current.FullName)"
        }
        if ([string]$current.FullName.TrimEnd('\', '/') -ieq $repositoryFullPath) {
            $reachedRoot = $true
            break
        }
        $current = if ($current -is [IO.FileInfo]) { $current.Directory } else { $current.Parent }
    }
    if (-not $reachedRoot) {
        throw "$Description could not be proven beneath the repository root: $Path"
    }
}

function Get-ProductionApprovalPackageData {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [object]$Manifest,

        [Parameter(Mandatory)]
        [string]$ManifestName,

        [Parameter(Mandatory)]
        [object[]]$PackagedArtifacts
    )

    $allowedRootProperties = @(
        'manifest_version', 'model_id', 'model_version', 'task', 'source', 'license', 'sha256',
        'files', 'inputs', 'outputs', 'preprocessing', 'postprocessing', 'commercial_use',
        'redistribution', 'providers', 'benchmarks')
    $unknownProperties = @($Manifest.PSObject.Properties.Name | Where-Object { $_ -notin $allowedRootProperties })
    if ($unknownProperties.Count -gt 0) {
        throw "Bundled model manifest '$ManifestName' contains unsupported properties: $($unknownProperties -join ', ')."
    }

    if (@($Manifest.source.PSObject.Properties.Name | Sort-Object) -join '|' -ne 'name|revision|url' -or
        @($Manifest.license.PSObject.Properties.Name | Sort-Object) -join '|' -ne 'notice_path|reviewed|spdx') {
        throw "Bundled model manifest '$ManifestName' source or license object does not match the production contract."
    }
    $reviewedLicenseAllowlist = @(
        'Apache-2.0', 'MIT', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', 'Zlib',
        'BSL-1.0', 'Unlicense', 'CC0-1.0')
    if ([string]$Manifest.license.spdx -notin $reviewedLicenseAllowlist) {
        throw "Bundled model manifest '$ManifestName' license is not on the reviewed production allowlist."
    }
    if (@($Manifest.inputs).Count -eq 0 -or @($Manifest.outputs).Count -eq 0) {
        throw "Bundled model manifest '$ManifestName' requires nonempty input and output contracts."
    }

    if (@($Manifest.providers) -notcontains 'cpu') {
        throw "Bundled model manifest '$ManifestName' does not declare mandatory CPU fallback."
    }

    $onnxArtifacts = @($PackagedArtifacts | Where-Object {
            [System.IO.Path]::GetExtension([string]$_.declaredPath) -ieq '.onnx'
        })
    if ($onnxArtifacts.Count -ne 1) {
        throw "Bundled production model manifest '$ManifestName' must declare exactly one ONNX payload."
    }

    $approvals = @($Manifest.benchmarks | Where-Object {
            $null -ne $_.PSObject.Properties['production_approval'] -and
            $_.production_approval -is [bool] -and [bool]$_.production_approval -and
            $null -ne $_.PSObject.Properties['release_eligible'] -and
            $_.release_eligible -is [bool] -and [bool]$_.release_eligible -and
            $null -ne $_.PSObject.Properties['status'] -and
            $_.status -is [string] -and [string]$_.status -ieq 'pass'
        })
    if ($approvals.Count -ne 1) {
        throw "Bundled model manifest '$ManifestName' requires exactly one passing release-eligible production approval."
    }

    $approval = $approvals[0]
    if ($approval.profile -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$approval.profile)) {
        throw "Bundled model manifest '$ManifestName' production approval requires a nonempty profile."
    }
    if ([string]$approval.evidence_sha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw "Bundled model manifest '$ManifestName' production approval requires a valid evidence SHA-256."
    }

    $evidenceDeclaredPath = [string]$approval.evidence_path
    $evidenceSourcePath = Resolve-ModelSupportingFile `
        -RepositoryRoot $RepositoryRoot `
        -DeclaredPath $evidenceDeclaredPath `
        -Description "Model benchmark evidence for '$ManifestName'"
    $evidenceHash = (Get-FileHash -LiteralPath $evidenceSourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($evidenceHash -ne ([string]$approval.evidence_sha256).ToLowerInvariant()) {
        throw "Bundled model manifest '$ManifestName' benchmark evidence checksum differs: $evidenceDeclaredPath."
    }

    $noticeDeclaredPath = [string]$Manifest.license.notice_path
    $noticeSourcePath = Resolve-ModelSupportingFile `
        -RepositoryRoot $RepositoryRoot `
        -DeclaredPath $noticeDeclaredPath `
        -Description "Model notice for '$ManifestName'"
    $noticeHash = (Get-FileHash -LiteralPath $noticeSourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $modelId = [string]$Manifest.model_id
    $modelVersion = [string]$Manifest.model_version
    $noticeLeaf = [System.IO.Path]::GetFileName($noticeSourcePath)
    $evidenceLeaf = [System.IO.Path]::GetFileName($evidenceSourcePath)

    return [pscustomobject]@{
        NoticeDeclaredPath = $noticeDeclaredPath.Replace('\', '/')
        NoticeSourcePath = $noticeSourcePath
        NoticeArchivePath = "models/notices/$modelId/$modelVersion/$noticeLeaf"
        NoticeSha256 = $noticeHash
        EvidenceDeclaredPath = $evidenceDeclaredPath.Replace('\', '/')
        EvidenceSourcePath = $evidenceSourcePath
        EvidenceArchivePath = "models/evidence/$modelId/$modelVersion/$evidenceLeaf"
        EvidenceSha256 = $evidenceHash
        Profile = [string]$approval.profile
    }
}

function Get-ModelAudit {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string[]]$RequiredModelTasks
    )

    $manifestRoot = Join-Path $RepositoryRoot 'models\manifest'
    $issues = New-Object System.Collections.Generic.List[string]
    $models = New-Object System.Collections.Generic.List[object]
    $redistributableModelFileCount = 0
    $archivePaths = [System.Collections.Generic.Dictionary[string,string]]::new([System.StringComparer]::OrdinalIgnoreCase)

    $manifestFiles = @(Get-ChildItem -LiteralPath $manifestRoot -Recurse -Filter '*.json' -File)
    if ($manifestFiles.Count -eq 0) {
        $issues.Add('No model manifests were found.')
    }

    foreach ($manifestFile in $manifestFiles) {
        try {
            Assert-NoModelReparsePoint `
                -RepositoryRoot $RepositoryRoot `
                -Path $manifestFile.FullName `
                -Description "Model manifest '$($manifestFile.Name)'"
            $manifestJson = Get-Content -LiteralPath $manifestFile.FullName -Raw
            Assert-NoDuplicateJsonPropertyNames `
                -Json $manifestJson `
                -Description "Model manifest '$($manifestFile.Name)'"
            $manifest = $manifestJson | ConvertFrom-Json
            $requiredProperties = @(
                'manifest_version', 'model_id', 'model_version', 'task', 'source', 'license',
                'sha256', 'files', 'inputs', 'outputs', 'commercial_use', 'redistribution', 'providers',
                'benchmarks')
            foreach ($requiredProperty in $requiredProperties) {
                if ($manifest.PSObject.Properties.Name -notcontains $requiredProperty) {
                    $issues.Add("Model manifest '$($manifestFile.Name)' is missing '$requiredProperty'.")
                }
            }

            if (($manifest.manifest_version -isnot [long] -and $manifest.manifest_version -isnot [int]) -or
                [int]$manifest.manifest_version -ne 1) {
                $issues.Add("Model manifest '$($manifestFile.Name)' manifest_version must be integer 1.")
            }
            foreach ($stringProperty in @('model_id', 'model_version', 'task')) {
                if ($manifest.$stringProperty -isnot [string] -or
                    [string]::IsNullOrWhiteSpace([string]$manifest.$stringProperty)) {
                    $issues.Add("Model manifest '$($manifestFile.Name)' requires nonempty string '$stringProperty'.")
                }
            }
            if ([string]$manifest.task -notin @('super_resolution', 'ocr_detection', 'ocr_recognition', 'marker_center', 'marker_classifier', 'panelization')) {
                $issues.Add("Model manifest '$($manifestFile.Name)' has unsupported task '$($manifest.task)'.")
            }

            foreach ($objectProperty in @('source', 'license')) {
                if ($null -eq $manifest.$objectProperty -or $manifest.$objectProperty -is [string] -or $manifest.$objectProperty -is [System.Array]) {
                    $issues.Add("Model manifest '$($manifestFile.Name)' requires object '$objectProperty'.")
                }
            }
            foreach ($sourceProperty in @('name', 'url', 'revision')) {
                if ($manifest.source.$sourceProperty -isnot [string] -or
                    [string]::IsNullOrWhiteSpace([string]$manifest.source.$sourceProperty)) {
                    $issues.Add("Model manifest '$($manifestFile.Name)' requires source.$sourceProperty.")
                }
            }
            foreach ($licenseProperty in @('spdx', 'notice_path')) {
                if ($manifest.license.$licenseProperty -isnot [string] -or
                    [string]::IsNullOrWhiteSpace([string]$manifest.license.$licenseProperty)) {
                    $issues.Add("Model manifest '$($manifestFile.Name)' requires license.$licenseProperty.")
                }
            }
            if ($manifest.license.reviewed -isnot [bool]) {
                $issues.Add("Model manifest '$($manifestFile.Name)' license.reviewed must be boolean.")
            }
            if ([string]$manifest.license.spdx -match '(?i)(AGPL|GPL|SSPL|BUSL|non[- ]?commercial|unknown|unclear|incomplete|TBD)') {
                $issues.Add("Model manifest '$($manifestFile.Name)' uses a prohibited or unclear license.")
            }

            foreach ($arrayProperty in @('files', 'inputs', 'outputs', 'providers', 'benchmarks')) {
                if ($manifest.$arrayProperty -isnot [System.Array]) {
                    $issues.Add("Model manifest '$($manifestFile.Name)' '$arrayProperty' must be an array.")
                }
            }
            if (@($manifest.files).Count -eq 0 -or @($manifest.files | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$_) }).Count -gt 0) {
                $issues.Add("Model manifest '$($manifestFile.Name)' requires nonempty string file paths.")
            }
            $allowedProviders = @('cpu', 'directml', 'winml', 'cuda', 'openvino', 'vulkan')
            if (@($manifest.providers).Count -eq 0 -or @($manifest.providers | Where-Object { $_ -isnot [string] -or $_ -notin $allowedProviders }).Count -gt 0) {
                $issues.Add("Model manifest '$($manifestFile.Name)' has invalid execution providers.")
            }
            foreach ($booleanProperty in @('commercial_use', 'redistribution')) {
                if ($manifest.$booleanProperty -isnot [bool]) {
                    $issues.Add("Model manifest '$($manifestFile.Name)' '$booleanProperty' must be boolean.")
                }
            }

            if ([string]$manifest.sha256 -notmatch '^[a-fA-F0-9]{64}$') {
                $issues.Add("Model manifest '$($manifestFile.Name)' has an invalid SHA-256 value.")
            }

            $benchmarkEntries = if ($manifest.benchmarks -is [System.Array]) {
                @($manifest.benchmarks)
            }
            else {
                @()
            }
            $productionApprovals = @($benchmarkEntries | Where-Object {
                    $null -ne $_.PSObject.Properties['production_approval'] -and
                    $_.production_approval -is [bool] -and [bool]$_.production_approval -and
                    $null -ne $_.PSObject.Properties['release_eligible'] -and
                    $_.release_eligible -is [bool] -and [bool]$_.release_eligible -and
                    $null -ne $_.PSObject.Properties['status'] -and
                    $_.status -is [string] -and [string]$_.status -ieq 'pass'
                })
            $isProductionApproved = $productionApprovals.Count -eq 1
            if ($productionApprovals.Count -gt 1) {
                $issues.Add("Model manifest '$($manifestFile.Name)' has multiple passing release-eligible production approvals.")
            }

            $artifactHashes = [System.Collections.Generic.Dictionary[string,string]]::new([System.StringComparer]::Ordinal)
            $declaredModelFiles = @($manifest.files | ForEach-Object { ([string]$_).Replace('\', '/') })
            if ($declaredModelFiles.Count -eq 1) {
                if ([string]$manifest.sha256 -match '^[a-fA-F0-9]{64}$') {
                    $artifactHashes.Add($declaredModelFiles[0], ([string]$manifest.sha256).ToLowerInvariant())
                }
            }
            elseif ($declaredModelFiles.Count -gt 1) {
                $payloadHashProperty = $null
                if ($null -ne $manifest.preprocessing -and
                    $null -ne $manifest.preprocessing.PSObject.Properties['model_payload_sha256']) {
                    $payloadHashProperty = $manifest.preprocessing.PSObject.Properties['model_payload_sha256'].Value
                }

                if ($null -eq $payloadHashProperty -or
                    $payloadHashProperty -is [string] -or
                    $payloadHashProperty -is [System.Array]) {
                    $issues.Add("Multi-file model manifest '$($manifestFile.Name)' requires preprocessing.model_payload_sha256 object entries for every file.")
                }
                else {
                    foreach ($hashProperty in @($payloadHashProperty.PSObject.Properties)) {
                        $normalizedHashPath = ([string]$hashProperty.Name).Replace('\', '/')
                        $hashValue = [string]$hashProperty.Value
                        if ($hashValue -notmatch '^[a-fA-F0-9]{64}$') {
                            $issues.Add("Multi-file model manifest '$($manifestFile.Name)' has an invalid payload SHA-256 for '$normalizedHashPath'.")
                        }
                        elseif ($artifactHashes.ContainsKey($normalizedHashPath)) {
                            $issues.Add("Multi-file model manifest '$($manifestFile.Name)' duplicates payload checksum path '$normalizedHashPath'.")
                        }
                        else {
                            $artifactHashes.Add($normalizedHashPath, $hashValue.ToLowerInvariant())
                        }
                    }

                    foreach ($declaredModelFile in $declaredModelFiles) {
                        if (-not $artifactHashes.ContainsKey($declaredModelFile)) {
                            $issues.Add("Multi-file model manifest '$($manifestFile.Name)' has no payload checksum for '$declaredModelFile'.")
                        }
                    }
                    foreach ($artifactHashPath in @($artifactHashes.Keys)) {
                        if ($declaredModelFiles -cnotcontains $artifactHashPath) {
                            $issues.Add("Multi-file model manifest '$($manifestFile.Name)' has a payload checksum for undeclared file '$artifactHashPath'.")
                        }
                    }
                }
            }

            $noticeRelativePath = [string]$manifest.license.notice_path
            if ([System.IO.Path]::IsPathRooted($noticeRelativePath)) {
                $issues.Add("Model manifest '$($manifestFile.Name)' uses an absolute notice path.")
            }
            else {
                $noticePath = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $noticeRelativePath))
                $repositoryPrefix = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
                if (-not $noticePath.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
                    -not (Test-Path -LiteralPath $noticePath -PathType Leaf)) {
                    $issues.Add("Model manifest '$($manifestFile.Name)' has a missing or unsafe notice path '$noticeRelativePath'.")
                }
            }

            if (-not [bool]$manifest.license.reviewed) {
                $issues.Add("Model manifest '$($manifestFile.Name)' has not completed license review.")
            }

            if (-not [bool]$manifest.commercial_use) {
                $issues.Add("Model manifest '$($manifestFile.Name)' does not permit commercial use.")
            }

            $packagedArtifacts = New-Object System.Collections.Generic.List[object]
            $discoveredModelFileCount = 0
            foreach ($modelRelativePath in @($manifest.files)) {
                if ([System.IO.Path]::IsPathRooted([string]$modelRelativePath) -or
                    [string]$modelRelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
                    $issues.Add("Model manifest '$($manifestFile.Name)' uses an unsafe model file path '$modelRelativePath'.")
                    continue
                }

                try {
                    $archivePath = Resolve-ModelArchiveRelativePath `
                        -DeclaredPath ([string]$modelRelativePath) `
                        -ModelId ([string]$manifest.model_id) `
                        -ModelVersion ([string]$manifest.model_version)
                }
                catch {
                    $issues.Add("Model manifest '$($manifestFile.Name)' has an unsafe archive path '$modelRelativePath': $($_.Exception.Message)")
                    continue
                }

                $candidatePaths = @(
                    (Join-Path $RepositoryRoot ([string]$modelRelativePath)),
                    (Join-Path (Join-Path $RepositoryRoot 'models') ([string]$modelRelativePath)),
                    (Join-Path $manifestFile.DirectoryName ([string]$modelRelativePath)),
                    (Join-Path $RepositoryRoot (
                        "artifacts\production-model-store\source\$([string]$manifest.model_id)\$([string]$manifest.model_version)\$([string]$modelRelativePath)"))) | ForEach-Object {
                    [System.IO.Path]::GetFullPath($_)
                } | Sort-Object -Unique
                $locatedCandidates = @($candidatePaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
                if ($locatedCandidates.Count -eq 1) {
                    $located = $locatedCandidates[0]
                    Assert-NoModelReparsePoint `
                        -RepositoryRoot $RepositoryRoot `
                        -Path $located `
                        -Description "Model payload '$modelRelativePath' for '$($manifestFile.Name)'"
                    $discoveredModelFileCount++
                    $actualHash = (Get-FileHash -LiteralPath $located -Algorithm SHA256).Hash.ToLowerInvariant()
                    $normalizedModelRelativePath = ([string]$modelRelativePath).Replace('\', '/')
                    $expectedArtifactHash = if ($artifactHashes.ContainsKey($normalizedModelRelativePath)) {
                        $artifactHashes[$normalizedModelRelativePath]
                    }
                    else {
                        $null
                    }
                    if ($null -eq $expectedArtifactHash) {
                        $issues.Add("Model manifest '$($manifestFile.Name)' has no usable checksum for '$modelRelativePath'.")
                    }
                    elseif ($actualHash -ne $expectedArtifactHash) {
                        $issues.Add("Model file checksum does not match manifest '$($manifestFile.Name)': $modelRelativePath.")
                    }
                    elseif ([bool]$manifest.redistribution -and $isProductionApproved) {
                        if ($archivePaths.ContainsKey($archivePath)) {
                            $issues.Add("Model manifests '$($archivePaths[$archivePath])' and '$($manifestFile.Name)' resolve to the same archive path '$archivePath'.")
                        }
                        else {
                            $archivePaths.Add($archivePath, $manifestFile.Name)
                            $redistributableModelFileCount++
                            $packagedArtifacts.Add([ordered]@{
                                    declaredPath = ([string]$modelRelativePath).Replace('\', '/')
                                    sourcePath = [System.IO.Path]::GetFullPath($located)
                                    archivePath = $archivePath
                                    sha256 = $actualHash
                                })
                        }
                    }
                }
                elseif ($locatedCandidates.Count -gt 1) {
                    $issues.Add("Model manifest '$($manifestFile.Name)' resolves model file '$modelRelativePath' to multiple source files.")
                }
                elseif ($isProductionApproved) {
                    $issues.Add("Model manifest '$($manifestFile.Name)' references missing model file '$modelRelativePath'.")
                }
            }

            $productionPackageData = $null
            if ($discoveredModelFileCount -gt 0) {
                if (-not [bool]$manifest.redistribution) {
                    $issues.Add("Bundled model manifest '$($manifestFile.Name)' does not permit redistribution.")
                }

                $releaseBenchmarks = @($manifest.benchmarks | Where-Object {
                        $_.status -is [string] -and
                        [string]$_.status -eq 'pass' -and
                        $_.release_eligible -is [bool] -and
                        [bool]$_.release_eligible
                    })
                if ($releaseBenchmarks.Count -eq 0) {
                    $issues.Add("Bundled model manifest '$($manifestFile.Name)' has no passing release_eligible benchmark.")
                }

                try {
                    $productionPackageData = Get-ProductionApprovalPackageData `
                        -RepositoryRoot $RepositoryRoot `
                        -Manifest $manifest `
                        -ManifestName $manifestFile.Name `
                        -PackagedArtifacts $packagedArtifacts.ToArray()
                }
                catch {
                    $issues.Add($_.Exception.Message)
                }
            }

            $models.Add([ordered]@{
                    modelId = [string]$manifest.model_id
                    version = [string]$manifest.model_version
                    task = [string]$manifest.task
                    productionApproved = $isProductionApproved
                    manifest = $manifestFile.FullName.Substring($RepositoryRoot.Length).TrimStart('\', '/').Replace('\', '/')
                    manifestSha256 = (Get-FileHash -LiteralPath $manifestFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                    modelSha256 = ([string]$manifest.sha256).ToLowerInvariant()
                    redistribution = [bool]$manifest.redistribution
                    packagedArtifacts = $packagedArtifacts.ToArray()
                    productionPackageData = $productionPackageData
                })
        }
        catch {
            $issues.Add("Model manifest '$($manifestFile.Name)' is invalid JSON or cannot be audited: $($_.Exception.Message)")
        }
    }

    foreach ($modelIdGroup in @($models | Group-Object { [string]$_.modelId } | Sort-Object Name)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$modelIdGroup.Name) -and
            $modelIdGroup.Count -gt 1) {
            $manifests = @($modelIdGroup.Group | ForEach-Object { [string]$_.manifest } | Sort-Object) -join ', '
            $issues.Add("Model ID '$($modelIdGroup.Name)' is duplicated by manifests: $manifests.")
        }
    }

    $approvedTasks = New-Object System.Collections.Generic.List[string]
    foreach ($requiredTask in $RequiredModelTasks) {
        $approvedForTask = @($models | Where-Object {
                [string]$_.task -ceq $requiredTask -and
                [bool]$_.productionApproved -and
                @($_.packagedArtifacts).Count -gt 0 -and
                $null -ne $_.productionPackageData
            })
        if ($approvedForTask.Count -eq 0) {
            $issues.Add("Required production model task '$requiredTask' has no checksum-verified, release-eligible, production-approved payload.")
        }
        elseif ($approvedForTask.Count -gt 1) {
            $identities = @($approvedForTask | ForEach-Object {
                    "$($_.modelId)@$($_.version)"
                } | Sort-Object) -join ', '
            $issues.Add("Required production model task '$requiredTask' has multiple approved defaults: $identities.")
        }
        else {
            $approvedTasks.Add($requiredTask)
        }
    }

    if ($redistributableModelFileCount -eq 0) {
        $issues.Add('No checksum-verified redistributable model binary is available for the offline default workflow.')
    }

    return [pscustomobject]@{
        Issues = $issues.ToArray()
        Models = $models.ToArray()
        RedistributableModelFileCount = $redistributableModelFileCount
        RequiredModelTasks = $RequiredModelTasks
        ApprovedModelTasks = $approvedTasks.ToArray()
    }
}

function Get-ReleaseAudit {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $issues = New-Object System.Collections.Generic.List[string]
    $warnings = New-Object System.Collections.Generic.List[string]
    $mandatoryEvidenceGates = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::Ordinal)
    foreach ($requiredPath in @('LICENSE', 'NOTICE', 'THIRD_PARTY_NOTICES.md', 'LICENSES')) {
        $fullPath = Join-Path $RepositoryRoot $requiredPath
        if (-not (Test-Path -LiteralPath $fullPath)) {
            $issues.Add("Required license content is missing: $requiredPath.")
        }
    }

    $auditPath = Join-Path $RepositoryRoot 'packaging\common\release-audit.json'
    if (-not (Test-Path -LiteralPath $auditPath -PathType Leaf)) {
        $issues.Add('The tracked packaging/common/release-audit.json file is missing.')
        return [pscustomobject]@{
            Issues = $issues.ToArray()
            Warnings = $warnings.ToArray()
            Definition = $null
            Components = $null
            MandatoryEvidenceGates = $mandatoryEvidenceGates
        }
    }

    try {
        $audit = Get-Content -LiteralPath $auditPath -Raw | ConvertFrom-Json
    }
    catch {
        $issues.Add("The tracked release audit is invalid JSON: $($_.Exception.Message)")
        return [pscustomobject]@{
            Issues = $issues.ToArray()
            Warnings = $warnings.ToArray()
            Definition = $null
            Components = $null
            MandatoryEvidenceGates = $mandatoryEvidenceGates
        }
    }

    if ($audit.schemaVersion -isnot [long] -and $audit.schemaVersion -isnot [int] -or
        [int]$audit.schemaVersion -ne 1) {
        $issues.Add('The tracked release audit schemaVersion must be integer 1.')
    }

    if ($audit.components -isnot [System.Array] -or @($audit.components).Count -eq 0) {
        $issues.Add('The tracked release audit requires a nonempty components array.')
    }

    if ($audit.mandatoryEvidenceGates -isnot [System.Array] -or
        @($audit.mandatoryEvidenceGates).Count -eq 0) {
        $issues.Add('The tracked release audit requires a nonempty mandatoryEvidenceGates array.')
    }
    else {
        $repositoryPrefix = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        foreach ($gate in @($audit.mandatoryEvidenceGates)) {
            foreach ($propertyName in @('id', 'description', 'status', 'evidence', 'notes')) {
                if ($gate.PSObject.Properties.Name -notcontains $propertyName) {
                    $issues.Add("Mandatory release evidence gate '$($gate.id)' is missing '$propertyName'.")
                }
            }

            $gateId = [string]$gate.id
            if ([string]::IsNullOrWhiteSpace($gateId)) {
                $issues.Add('A mandatory release evidence gate has an empty id.')
                continue
            }
            if ($mandatoryEvidenceGates.ContainsKey($gateId)) {
                $issues.Add("Mandatory release evidence gate id '$gateId' is duplicated.")
                continue
            }
            $mandatoryEvidenceGates.Add($gateId, $gate)

            if ([string]::IsNullOrWhiteSpace([string]$gate.description)) {
                $issues.Add("Mandatory release evidence gate '$gateId' has no description.")
            }
            if ([string]::IsNullOrWhiteSpace([string]$gate.notes)) {
                $issues.Add("Mandatory release evidence gate '$gateId' has no notes.")
            }

            $gateStatus = [string]$gate.status
            if ($gateStatus -notin @('pass', 'blocked')) {
                $issues.Add("Mandatory release evidence gate '$gateId' has invalid status '$gateStatus'.")
            }
            if ($gate.evidence -isnot [System.Array]) {
                $issues.Add("Mandatory release evidence gate '$gateId' requires an evidence array.")
                continue
            }

            $gateEvidence = @($gate.evidence)
            if ($gateStatus -eq 'pass' -and $gateEvidence.Count -eq 0) {
                $issues.Add("Mandatory release evidence gate '$gateId' is marked pass without direct evidence.")
            }
            foreach ($evidence in $gateEvidence) {
                $evidencePathValue = [string]$evidence.path
                $evidenceSha256 = [string]$evidence.sha256
                if ([string]::IsNullOrWhiteSpace($evidencePathValue) -or
                    [System.IO.Path]::IsPathRooted($evidencePathValue)) {
                    $issues.Add("Mandatory release evidence gate '$gateId' has an unsafe evidence path '$evidencePathValue'.")
                    continue
                }
                if ($evidenceSha256 -notmatch '^[a-fA-F0-9]{64}$') {
                    $issues.Add("Mandatory release evidence gate '$gateId' has no exact evidence SHA-256 for '$evidencePathValue'.")
                    continue
                }

                $evidencePath = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $evidencePathValue))
                if (-not $evidencePath.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
                    -not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
                    $issues.Add("Mandatory release evidence gate '$gateId' has a missing or unsafe evidence path '$evidencePathValue'.")
                    continue
                }
                $actualEvidenceSha256 = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
                if (-not $actualEvidenceSha256.Equals($evidenceSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $issues.Add("Mandatory release evidence gate '$gateId' evidence checksum differs for '$evidencePathValue'.")
                }
            }

            if ($gateStatus -eq 'blocked') {
                $issues.Add("Mandatory release evidence gate '$gateId' is blocked: $($gate.notes)")
            }
        }
    }

    $components = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::Ordinal)
    $requiredProperties = @(
        'id', 'component', 'version', 'source', 'sourceRevision', 'license',
        'bundledOrDownloaded', 'artifactSha256', 'checksumPolicy', 'noticePaths',
        'commercialUse', 'redistribution', 'reviewStatus', 'reviewer', 'reviewDate', 'notes')
    foreach ($component in @($audit.components)) {
        foreach ($propertyName in $requiredProperties) {
            if ($component.PSObject.Properties.Name -notcontains $propertyName) {
                $issues.Add("Release audit component '$($component.id)' is missing '$propertyName'.")
            }
        }

        $componentId = [string]$component.id
        if ([string]::IsNullOrWhiteSpace($componentId)) {
            $issues.Add('A release audit component has an empty id.')
            continue
        }

        if ($components.ContainsKey($componentId)) {
            $issues.Add("Release audit component id '$componentId' is duplicated.")
            continue
        }
        $components.Add($componentId, $component)

        foreach ($fieldName in @('component', 'version', 'source', 'sourceRevision', 'license', 'bundledOrDownloaded', 'checksumPolicy', 'reviewStatus')) {
            $fieldValue = [string]$component.$fieldName
            if ([string]::IsNullOrWhiteSpace($fieldValue) -or $fieldValue -eq 'TBD') {
                $issues.Add("Release audit component '$componentId' has no exact $fieldName.")
            }
        }

        if ($component.noticePaths -isnot [System.Array] -or @($component.noticePaths).Count -eq 0) {
            $issues.Add("Release audit component '$componentId' requires a noticePaths array.")
        }
        else {
            foreach ($noticeRelativePath in @($component.noticePaths)) {
                $noticeValue = [string]$noticeRelativePath
                if ([string]::IsNullOrWhiteSpace($noticeValue) -or [System.IO.Path]::IsPathRooted($noticeValue)) {
                    $issues.Add("Release audit component '$componentId' has an unsafe notice path '$noticeValue'.")
                    continue
                }

                $noticePath = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $noticeValue))
                $repositoryPrefix = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
                if (-not $noticePath.StartsWith($repositoryPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
                    -not (Test-Path -LiteralPath $noticePath -PathType Leaf)) {
                    $issues.Add("Release audit component '$componentId' has a missing or unsafe notice path '$noticeValue'.")
                }
            }
        }

        if ($component.commercialUse -isnot [bool] -or -not [bool]$component.commercialUse) {
            $issues.Add("Release audit component '$componentId' does not explicitly permit commercial use.")
        }
        if ($component.redistribution -isnot [bool] -or -not [bool]$component.redistribution) {
            $issues.Add("Release audit component '$componentId' does not explicitly permit redistribution.")
        }

        $license = [string]$component.license
        if ($license -match '(?i)(AGPL|GPL|SSPL|BUSL|non[- ]?commercial|unknown|unclear|incomplete|TBD)') {
            $issues.Add("Release audit component '$componentId' uses a prohibited or unclear license '$license'.")
        }

        if ([string]$component.bundledOrDownloaded -eq 'bundled') {
            if ([string]$component.reviewStatus -ne 'reviewed') {
                $issues.Add("Bundled release component '$componentId' is not reviewed (status '$($component.reviewStatus)').")
            }

            $checksumPolicy = [string]$component.checksumPolicy
            if ($checksumPolicy -in @('exact-package', 'exact-binary')) {
                if ([string]$component.artifactSha256 -notmatch '^[a-fA-F0-9]{64}$') {
                    $issues.Add("Bundled release component '$componentId' requires an exact artifact SHA-256.")
                }
            }
            elseif ($checksumPolicy -ne 'release-sbom') {
                $issues.Add("Bundled release component '$componentId' uses unsupported checksum policy '$checksumPolicy'.")
            }
        }
    }

    if ($null -eq $audit.binaryCoverage -or -not [bool]$audit.binaryCoverage.firstMatchWins -or
        $audit.binaryCoverage.rules -isnot [System.Array] -or @($audit.binaryCoverage.rules).Count -eq 0) {
        $issues.Add('The tracked release audit requires ordered first-match publish-binary coverage rules.')
    }
    else {
        foreach ($rule in @($audit.binaryCoverage.rules)) {
            if ([string]::IsNullOrWhiteSpace([string]$rule.pattern) -or
                [string]::IsNullOrWhiteSpace([string]$rule.componentId) -or
                -not $components.ContainsKey([string]$rule.componentId)) {
                $issues.Add("Release binary coverage rule '$($rule.pattern)' references an invalid component.")
            }

            if ($null -ne $rule.PSObject.Properties['embeddedComponentIds']) {
                $embeddedIds = @($rule.embeddedComponentIds)
                if ($embeddedIds.Count -eq 0 -or
                    @($embeddedIds | Where-Object {
                            $_ -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$_)
                        }).Count -gt 0 -or
                    @($embeddedIds | Select-Object -Unique).Count -ne $embeddedIds.Count) {
                    $issues.Add("Release binary coverage rule '$($rule.pattern)' has invalid embedded component identities.")
                    continue
                }

                foreach ($embeddedId in $embeddedIds) {
                    if (-not $components.ContainsKey([string]$embeddedId)) {
                        $issues.Add("Release binary coverage rule '$($rule.pattern)' references unknown embedded component '$embeddedId'.")
                        continue
                    }

                    $embeddedComponent = $components[[string]$embeddedId]
                    if ([string]$embeddedComponent.reviewStatus -ne 'reviewed' -or
                        -not [bool]$embeddedComponent.commercialUse -or
                        -not [bool]$embeddedComponent.redistribution -or
                        [string]$embeddedComponent.checksumPolicy -ne 'exact-binary' -or
                        [string]$embeddedComponent.artifactSha256 -notmatch '^[a-fA-F0-9]{64}$') {
                        $issues.Add("Release binary coverage rule '$($rule.pattern)' references unreleasable embedded component '$embeddedId'.")
                    }
                }
            }
        }
    }

    if ($audit.emittedArtifactCoverage -isnot [System.Array] -or
        @($audit.emittedArtifactCoverage).Count -eq 0) {
        $issues.Add('The tracked release audit requires emittedArtifactCoverage definitions.')
    }
    else {
        foreach ($artifactCoverage in @($audit.emittedArtifactCoverage)) {
            if ([string]::IsNullOrWhiteSpace([string]$artifactCoverage.artifactKind) -or
                [string]::IsNullOrWhiteSpace([string]$artifactCoverage.fileNameTemplate) -or
                [string]$artifactCoverage.checksumPolicy -ne 'release-sbom' -or
                $artifactCoverage.componentIds -isnot [System.Array] -or
                @($artifactCoverage.componentIds).Count -eq 0) {
                $issues.Add("Release artifact coverage '$($artifactCoverage.artifactKind)' is incomplete.")
                continue
            }

            foreach ($componentId in @($artifactCoverage.componentIds)) {
                if (-not $components.ContainsKey([string]$componentId)) {
                    $issues.Add("Release artifact coverage '$($artifactCoverage.artifactKind)' references unknown component '$componentId'.")
                    continue
                }

                $artifactComponent = $components[[string]$componentId]
                if ([string]$artifactComponent.reviewStatus -ne 'reviewed' -or
                    -not [bool]$artifactComponent.commercialUse -or
                    -not [bool]$artifactComponent.redistribution) {
                    $issues.Add("Release artifact coverage '$($artifactCoverage.artifactKind)' references unreleasable component '$componentId'.")
                }
            }
        }
    }

    $ledgerPath = Join-Path $RepositoryRoot 'DEPENDENCY_PROVENANCE_LEDGER.csv'
    if (Test-Path -LiteralPath $ledgerPath -PathType Leaf) {
        foreach ($row in @(Import-Csv -LiteralPath $ledgerPath)) {
            $trackedMatch = @($audit.components | Where-Object { [string]$_.component -eq [string]$row.component })
            if ($trackedMatch.Count -eq 1 -and
                ([string]$trackedMatch[0].version -ne [string]$row.version -or
                 [string]$trackedMatch[0].reviewStatus -ne [string]$row.status)) {
                $warnings.Add("Local ledger differs from tracked release audit for '$($row.component)'; tracked audit remains authoritative.")
            }
        }
    }

    return [pscustomobject]@{
        Issues = $issues.ToArray()
        Warnings = $warnings.ToArray()
        Definition = $audit
        Components = $components
        MandatoryEvidenceGates = $mandatoryEvidenceGates
    }
}

function Assert-PublishBinaryCoverage {
    param(
        [Parameter(Mandatory)]
        [string]$PublishRoot,

        [Parameter(Mandatory)]
        [object]$ReleaseAudit
    )

    $failures = New-Object System.Collections.Generic.List[string]
    $mappings = New-Object System.Collections.Generic.List[object]
    $components = $ReleaseAudit.Components
    foreach ($binary in @(Get-ChildItem -LiteralPath $PublishRoot -Recurse -File | Where-Object {
                $_.Extension -in @('.exe', '.dll')
            })) {
        $relativePath = $binary.FullName.Substring([System.IO.Path]::GetFullPath($PublishRoot).Length).TrimStart('\', '/').Replace('\', '/')
        $matchedRule = $null
        foreach ($rule in @($ReleaseAudit.Definition.binaryCoverage.rules)) {
            [object[]]$allowedNames = @()
            if ($null -ne $rule.PSObject.Properties['allowedNames']) {
                $allowedNames = @($rule.allowedNames)
            }
            if ($binary.Name -like [string]$rule.pattern -and
                ($allowedNames.Count -eq 0 -or $allowedNames -contains $binary.Name)) {
                $matchedRule = $rule
                break
            }
        }

        if ($null -eq $matchedRule) {
            $failures.Add("Published binary '$relativePath' has no release-audit coverage rule.")
            continue
        }

        $component = $components[[string]$matchedRule.componentId]
        if ([string]$component.reviewStatus -ne 'reviewed' -or
            -not [bool]$component.commercialUse -or
            -not [bool]$component.redistribution) {
            $failures.Add("Published binary '$relativePath' maps to unreleasable component '$($component.id)'.")
            continue
        }

        $binarySha256 = (Get-FileHash -LiteralPath $binary.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([string]$component.checksumPolicy -eq 'exact-binary' -and
            -not $binarySha256.Equals([string]$component.artifactSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            $failures.Add("Published binary '$relativePath' differs from exact release-audit binary '$($component.id)'.")
            continue
        }

        $mappings.Add([ordered]@{
                path = $relativePath
                componentId = [string]$component.id
                sha256 = $binarySha256
                embeddedComponentIds = if ($null -ne $matchedRule.PSObject.Properties['embeddedComponentIds']) {
                    @($matchedRule.embeddedComponentIds | ForEach-Object { [string]$_ })
                }
                else {
                    @()
                }
            })
    }

    if ($failures.Count -gt 0) {
        throw "Publish binary coverage audit failed:`n - $($failures -join "`n - ")"
    }

    return $mappings.ToArray()
}

function Get-EmbeddedComponentRecords {
    param(
        [Parameter(Mandatory)]
        [object[]]$BinaryCoverage,

        [Parameter(Mandatory)]
        [object]$ReleaseAudit
    )

    $records = New-Object System.Collections.Generic.List[object]
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($coverage in $BinaryCoverage) {
        foreach ($componentId in @($coverage.embeddedComponentIds)) {
            $identity = "$($coverage.path)|$componentId"
            if (-not $seen.Add($identity)) {
                throw "Embedded release component mapping is duplicated: $identity"
            }

            $component = $ReleaseAudit.Components[[string]$componentId]
            $records.Add([ordered]@{
                    containerPath = [string]$coverage.path
                    containerSha256 = [string]$coverage.sha256
                    id = [string]$component.id
                    component = [string]$component.component
                    version = [string]$component.version
                    source = [string]$component.source
                    sourceRevision = [string]$component.sourceRevision
                    license = [string]$component.license
                    artifactSha256 = ([string]$component.artifactSha256).ToLowerInvariant()
                    checksumPolicy = [string]$component.checksumPolicy
                    noticePaths = @($component.noticePaths | ForEach-Object { [string]$_ })
                    commercialUse = [bool]$component.commercialUse
                    redistribution = [bool]$component.redistribution
                    reviewStatus = [string]$component.reviewStatus
                })
        }
    }

    return $records.ToArray()
}

function Resolve-ArtifactLeafName {
    param(
        [Parameter(Mandatory)]
        [string]$Template,

        [Parameter(Mandatory)]
        [string]$Version,

        [Parameter(Mandatory)]
        [string]$RuntimeIdentifier,

        [Parameter(Mandatory)]
        [string]$ExpectedExtension
    )

    $fileName = $Template.Replace('{version}', $Version).Replace('{rid}', $RuntimeIdentifier)
    if ([string]::IsNullOrWhiteSpace($fileName) -or
        [System.IO.Path]::IsPathRooted($fileName) -or
        [System.IO.Path]::GetFileName($fileName) -ne $fileName -or
        $fileName.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        [System.IO.Path]::GetExtension($fileName) -ne $ExpectedExtension) {
        throw "Artifact filename template produced an unsafe leaf name: '$fileName'."
    }

    return $fileName
}

function Get-FileManifest {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $rootFullPath = [System.IO.Path]::GetFullPath($Root)
    [string[]]$relativePaths = @(Get-ChildItem -LiteralPath $rootFullPath -Recurse -File | ForEach-Object {
            $_.FullName.Substring($rootFullPath.Length).TrimStart('\', '/').Replace('\', '/')
        })
    [System.Array]::Sort($relativePaths, [System.StringComparer]::Ordinal)

    return @($relativePaths | ForEach-Object {
            $relativePath = $_
            $fullPath = Join-Path $rootFullPath $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
            $item = Get-Item -LiteralPath $fullPath
            [ordered]@{
                path = $relativePath
                size = $item.Length
                sha256 = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
}

function Get-ManifestDigest {
    param(
        [Parameter(Mandatory)]
        [object[]]$Files
    )

    $byPath = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::Ordinal)
    foreach ($file in $Files) {
        $byPath.Add([string]$file.path, $file)
    }

    [string[]]$paths = @($byPath.Keys)
    [System.Array]::Sort($paths, [System.StringComparer]::Ordinal)
    $lines = @($paths | ForEach-Object {
            $file = $byPath[$_]
            "$($file.sha256)  $($file.path)"
        })
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }
}

function Assert-CommonPublishDefinition {
    param(
        [Parameter(Mandatory)]
        [object]$Definition
    )

    if ([int]$Definition.schemaVersion -ne 2) {
        throw "Unsupported common publish definition schema '$($Definition.schemaVersion)'."
    }
    $allowedModelTasks = @(
        'ocr_detection', 'ocr_recognition', 'marker_center', 'marker_classifier', 'panelization')
    $requiredModelTasks = @($Definition.requiredModelTasks)
    if ($requiredModelTasks.Count -eq 0 -or
        @($requiredModelTasks | Where-Object {
                $_ -isnot [string] -or
                [string]::IsNullOrWhiteSpace([string]$_) -or
                $allowedModelTasks -cnotcontains [string]$_
            }).Count -gt 0 -or
        @($requiredModelTasks | Select-Object -Unique).Count -ne $requiredModelTasks.Count) {
        throw 'Common publish requiredModelTasks must be a nonempty unique array of supported offline production tasks.'
    }
    if ([string]$Definition.project -ne 'src/GraphReader.App/GraphReader.App.csproj' -or
        [string]$Definition.configuration -ne 'Release' -or
        $Definition.selfContained -isnot [bool] -or -not [bool]$Definition.selfContained -or
        $Definition.publishSingleFile -isnot [bool] -or [bool]$Definition.publishSingleFile -or
        $Definition.debugSymbols -isnot [bool] -or [bool]$Definition.debugSymbols) {
        throw 'Common publish must use the GraphReader.App Release self-contained, multi-file, symbol-free contract.'
    }

    $expected = [ordered]@{
        'contracts' = 'contracts'
        'LICENSE' = 'LICENSE'
        'NOTICE' = 'NOTICE'
        'THIRD_PARTY_NOTICES.md' = 'THIRD_PARTY_NOTICES.md'
        'LICENSES' = 'LICENSES'
        'packaging/common/release-audit.json' = 'release-audit.json'
    }
    $requiredContent = @($Definition.requiredContent)
    if ($requiredContent.Count -ne $expected.Count) {
        throw 'Common publish requiredContent must contain only the approved distribution mappings.'
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($content in $requiredContent) {
        $source = ([string]$content.source).Replace('\', '/')
        $target = ([string]$content.target).Replace('\', '/')
        if (-not $expected.Contains($source) -or
            [string]$expected[$source] -ne $target -or
            -not $seen.Add($source)) {
            throw "Common publish contains an unapproved source-to-target mapping: '$source' -> '$target'."
        }
    }
}

function Get-RequiredContentArchivePaths {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [object]$Definition
    )

    $paths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($content in @($Definition.requiredContent)) {
        $sourcePath = Resolve-SafeChildPath -Parent $RepositoryRoot -Child ([string]$content.source)
        $target = ([string]$content.target).Replace('\', '/').TrimEnd('/')
        if (-not (Test-Path -LiteralPath $sourcePath)) {
            throw "Required distribution content is missing: $sourcePath"
        }

        $sourceItem = Get-Item -LiteralPath $sourcePath
        if ($sourceItem.PSIsContainer) {
            $sourcePrefix = $sourceItem.FullName.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
            foreach ($file in @(Get-ChildItem -LiteralPath $sourcePath -Recurse -File)) {
                $relative = $file.FullName.Substring($sourcePrefix.Length).Replace('\', '/')
                $archivePath = "$target/$relative"
                if (-not $paths.Add($archivePath)) {
                    throw "Required distribution content has a duplicate archive path: $archivePath"
                }
            }
        }
        elseif (-not $paths.Add($target)) {
            throw "Required distribution content has a duplicate archive path: $target"
        }
    }

    return [string[]]$paths
}

function Get-ApplicationPublishPaths {
    param(
        [Parameter(Mandatory)]
        [string]$PublishRoot,

        [Parameter(Mandatory)]
        [string[]]$NonApplicationPaths
    )

    $nonApplication = [System.Collections.Generic.HashSet[string]]::new(
        $NonApplicationPaths,
        [System.StringComparer]::Ordinal)
    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($file in @(Get-FileManifest -Root $PublishRoot)) {
        $path = [string]$file.path
        if ($nonApplication.Contains($path)) {
            continue
        }

        $isApprovedRootFile = $path -notmatch '/' -and (
            [System.IO.Path]::GetExtension($path) -in @('.exe', '.dll') -or
            $path -in @('GraphReader.App.deps.json', 'GraphReader.App.runtimeconfig.json'))
        $isApprovedSatelliteAssembly = $path -match '^(cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hans|zh-Hant)/[^/]+\.resources\.dll$'
        if (-not $isApprovedRootFile -and -not $isApprovedSatelliteAssembly) {
            throw "Application publish contains an unapproved payload path: $path"
        }
        $paths.Add($path)
    }

    if (-not $paths.Contains('GraphReader.App.exe')) {
        throw 'Application publish does not contain GraphReader.App.exe.'
    }

    return $paths.ToArray()
}

function Assert-WindowsX64Executable {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }

    [byte[]]$bytes = [System.IO.File]::ReadAllBytes($Path)
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

function Assert-PayloadAllowlist {
    param(
        [Parameter(Mandatory)]
        [string]$PayloadRoot,

        [Parameter(Mandatory)]
        [string[]]$AllowedPaths
    )

    $allowed = [System.Collections.Generic.HashSet[string]]::new(
        $AllowedPaths,
        [System.StringComparer]::Ordinal)
    $actual = @(Get-FileManifest -Root $PayloadRoot | ForEach-Object { [string]$_.path })
    $unexpected = @($actual | Where-Object { -not $allowed.Contains($_) })
    $missing = @($AllowedPaths | Where-Object { $actual -notcontains $_ })
    if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
        throw "Payload allowlist mismatch. Unexpected: $($unexpected -join ', '). Missing: $($missing -join ', ')."
    }
}

function Assert-NoForbiddenReleaseFiles {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $forbidden = New-Object System.Collections.Generic.List[string]
    foreach ($file in @(Get-ChildItem -LiteralPath $Root -Recurse -File)) {
        $relativePath = $file.FullName.Substring([System.IO.Path]::GetFullPath($Root).Length).TrimStart('\', '/').Replace('\', '/')
        $isForbidden = $relativePath -match '(?i)(^|/)(\.agents|private|human[-_ ]annotations?|training[-_ ]data|autosaves?|cache|obj|bin)(/|$)' -or
            $relativePath -match '(?i)(^|/)(AGENTS\.md|CODEX_[^/]*|DOCUMENT_MAP\.md|MASTER_DEVELOPMENT_PLAN\.md|ARCHITECTURE_CONTRACTS\.md|WINDOWS_DISTRIBUTION\.md|VERSIONING\.md|DEPENDENCY_PROVENANCE_LEDGER\.csv)$' -or
            $relativePath -match '(?i)\.(pdb|snupkg|user|suo|autosave)$'
        if ($isForbidden) {
            $forbidden.Add($relativePath)
        }
    }

    if ($forbidden.Count -gt 0) {
        throw "Forbidden files were found in release staging: $($forbidden -join ', ')"
    }
}

function New-ZipFromDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Force
    }

    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        [System.IO.Path]::GetFullPath($Source),
        [System.IO.Path]::GetFullPath($Destination),
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
}

$manifestFullPath = [System.IO.Path]::GetFullPath($ManifestPath)
$packagingRoot = Split-Path -Parent $manifestFullPath
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $packagingRoot '..'))
$manifest = Get-Content -LiteralPath $manifestFullPath -Raw | ConvertFrom-Json
$releaseVerifierPath = Join-Path $packagingRoot 'Test-ReleaseArtifact.ps1'
if (-not (Test-Path -LiteralPath $releaseVerifierPath -PathType Leaf)) {
    throw "Release definition verifier is missing: $releaseVerifierPath"
}
$null = & $releaseVerifierPath `
    -ManifestPath $manifestFullPath `
    -VersionFilePath (Join-Path $repositoryRoot 'Directory.Build.props')
$commonDefinitionPath = Join-Path $packagingRoot 'common\publish.json'
$commonDefinition = Get-Content -LiteralPath $commonDefinitionPath -Raw | ConvertFrom-Json
Assert-CommonPublishDefinition -Definition $commonDefinition

if ([int]$manifest.schemaVersion -ne 2) {
    throw "Unsupported packaging manifest schema version '$($manifest.schemaVersion)'."
}

$version = Get-CentralVersion -RepositoryRoot $repositoryRoot
if ([string]$manifest.versionSource -ne $version.Source) {
    throw "Packaging versionSource must be '$($version.Source)'."
}

if ([string]$manifest.rid -ne 'win-x64') {
    throw "The initial Windows release RID must be win-x64, found '$($manifest.rid)'."
}

$installerFileName = Resolve-ArtifactLeafName `
    -Template ([string]$manifest.installer.fileNameTemplate) `
    -Version $version.Value `
    -RuntimeIdentifier ([string]$manifest.rid) `
    -ExpectedExtension '.exe'
$portableFileName = Resolve-ArtifactLeafName `
    -Template ([string]$manifest.portable.fileNameTemplate) `
    -Version $version.Value `
    -RuntimeIdentifier ([string]$manifest.rid) `
    -ExpectedExtension '.zip'

$gitCommit = Get-GitCommit -RepositoryRoot $repositoryRoot
$gitWorkingTreeDirty = Get-GitWorkingTreeDirty -RepositoryRoot $repositoryRoot
$contractVersion = Get-ContractVersion -RepositoryRoot $repositoryRoot
$modelAudit = Get-ModelAudit `
    -RepositoryRoot $repositoryRoot `
    -RequiredModelTasks @($commonDefinition.requiredModelTasks)
$releaseAudit = Get-ReleaseAudit -RepositoryRoot $repositoryRoot
$releaseBlockers = New-Object System.Collections.Generic.List[string]
foreach ($issue in @($releaseAudit.Issues)) {
    $releaseBlockers.Add([string]$issue)
}
foreach ($issue in @($modelAudit.Issues)) {
    $releaseBlockers.Add([string]$issue)
}
$reviewedOpenCvRequired = $null -ne $releaseAudit.Components -and
    $releaseAudit.Components.ContainsKey('opencvsharp-native')
$reviewedOpenCvEvidencePath = $null
$reviewedOpenCvRuntimeSha256 = $null
if ($reviewedOpenCvRequired) {
    if ([string]::IsNullOrWhiteSpace($ReviewedOpenCvEvidenceRoot)) {
        $releaseBlockers.Add('The exact reviewed source-built OpenCV evidence root is required for the common release publish.')
    }
    else {
        try {
            $reviewedOpenCvEvidencePath = [IO.Path]::GetFullPath($ReviewedOpenCvEvidenceRoot)
            $reviewedOpenCvRuntimePath = Join-Path $reviewedOpenCvEvidencePath 'bin\OpenCvSharpExtern.dll'
            if (-not (Test-Path -LiteralPath $reviewedOpenCvEvidencePath -PathType Container) -or
                -not (Test-Path -LiteralPath $reviewedOpenCvRuntimePath -PathType Leaf)) {
                $releaseBlockers.Add('The exact reviewed source-built OpenCV evidence root or runtime binary is missing.')
            }
            else {
                $reviewedOpenCvRuntimeSha256 = (Get-FileHash -LiteralPath $reviewedOpenCvRuntimePath -Algorithm SHA256).Hash.ToLowerInvariant()
                $expectedOpenCvRuntimeSha256 = [string]$releaseAudit.Components['opencvsharp-native'].artifactSha256
                if (-not [string]::Equals(
                        $reviewedOpenCvRuntimeSha256,
                        $expectedOpenCvRuntimeSha256,
                        [StringComparison]::OrdinalIgnoreCase)) {
                    $releaseBlockers.Add('The reviewed source-built OpenCV runtime does not match the exact public audit binary.')
                }
            }
        }
        catch {
            $releaseBlockers.Add("The reviewed source-built OpenCV evidence root is invalid: $($_.Exception.Message)")
        }
    }
}
$reviewedPdfiumRequired = $null -ne $releaseAudit.Components -and
    $releaseAudit.Components.ContainsKey('pdfium-native')
$reviewedPdfiumEvidencePath = $null
$reviewedPdfiumRuntimeSha256 = $null
if ($reviewedPdfiumRequired) {
    if ([string]::IsNullOrWhiteSpace($ReviewedPdfiumEvidenceRoot)) {
        $releaseBlockers.Add('The exact reviewed PDFium evidence root is required for the common release publish.')
    }
    else {
        try {
            $reviewedPdfiumEvidencePath = [IO.Path]::GetFullPath($ReviewedPdfiumEvidenceRoot)
            $reviewedPdfiumApprovalPath = Join-Path $reviewedPdfiumEvidencePath 'reviewed-approval.json'
            if (-not (Test-Path -LiteralPath $reviewedPdfiumEvidencePath -PathType Container) -or
                -not (Test-Path -LiteralPath $reviewedPdfiumApprovalPath -PathType Leaf)) {
                $releaseBlockers.Add('The exact reviewed PDFium evidence root or approval is missing.')
            }
            else {
                $reviewedPdfiumApproval = Get-Content -LiteralPath $reviewedPdfiumApprovalPath -Raw | ConvertFrom-Json
                $reviewedPdfiumBinaryValue = [string]$reviewedPdfiumApproval.binaryPath
                if ([IO.Path]::IsPathRooted($reviewedPdfiumBinaryValue) -or
                    $reviewedPdfiumBinaryValue -match '(^|[\\/])\.\.([\\/]|$)') {
                    $releaseBlockers.Add('The reviewed PDFium approval uses an unsafe binary path.')
                }
                else {
                    $reviewedPdfiumRuntimePath = [IO.Path]::GetFullPath((Join-Path $reviewedPdfiumEvidencePath $reviewedPdfiumBinaryValue))
                    $reviewedPdfiumPrefix = $reviewedPdfiumEvidencePath.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
                    if (-not $reviewedPdfiumRuntimePath.StartsWith($reviewedPdfiumPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                        -not (Test-Path -LiteralPath $reviewedPdfiumRuntimePath -PathType Leaf)) {
                        $releaseBlockers.Add('The exact reviewed PDFium runner is missing or outside its evidence root.')
                    }
                    else {
                        $reviewedPdfiumRuntimeSha256 = (Get-FileHash -LiteralPath $reviewedPdfiumRuntimePath -Algorithm SHA256).Hash.ToLowerInvariant()
                        $expectedPdfiumRuntimeSha256 = [string]$releaseAudit.Components['pdfium-native'].artifactSha256
                        if (-not [string]::Equals(
                                $reviewedPdfiumRuntimeSha256,
                                $expectedPdfiumRuntimeSha256,
                                [StringComparison]::OrdinalIgnoreCase)) {
                            $releaseBlockers.Add('The reviewed PDFium runner does not match the exact public audit binary.')
                        }
                    }
                }
            }
        }
        catch {
            $releaseBlockers.Add("The reviewed PDFium evidence root is invalid: $($_.Exception.Message)")
        }
    }
}
if ($gitWorkingTreeDirty) {
    $releaseBlockers.Add('The Git working tree is dirty. Release artifacts require a clean committed tree.')
}

$auditResult = [pscustomobject]@{
    SchemaVersion = 1
    Version = $version.Value
    VersionSource = $version.Source
    RuntimeIdentifier = [string]$manifest.rid
    GitCommit = $gitCommit
    GitWorkingTreeDirty = $gitWorkingTreeDirty
    ContractVersion = $contractVersion
    ReleaseEligible = $version.ReleaseEligible
    ReleaseReady = $version.ReleaseEligible -and $releaseBlockers.Count -eq 0
    Blockers = $releaseBlockers.ToArray()
    Warnings = @($releaseAudit.Warnings)
    ModelManifestCount = @($modelAudit.Models).Count
    RedistributableModelFileCount = $modelAudit.RedistributableModelFileCount
    RequiredProductionModelTasks = @($modelAudit.RequiredModelTasks)
    ApprovedProductionModelTasks = @($modelAudit.ApprovedModelTasks)
    MandatoryEvidenceGateCount = $releaseAudit.MandatoryEvidenceGates.Count
    PassedMandatoryEvidenceGates = @($releaseAudit.MandatoryEvidenceGates.Values | Where-Object {
            [string]$_.status -eq 'pass'
        } | ForEach-Object { [string]$_.id } | Sort-Object)
    ReviewedOpenCvRuntimeRequired = $reviewedOpenCvRequired
    ReviewedOpenCvRuntimeSha256 = $reviewedOpenCvRuntimeSha256
    ReviewedPdfiumRuntimeRequired = $reviewedPdfiumRequired
    ReviewedPdfiumRuntimeSha256 = $reviewedPdfiumRuntimeSha256
    ArtifactsEmitted = $false
}

if ($AuditOnly) {
    $auditResult
    return
}

if (-not $version.ReleaseEligible) {
    throw "Version $($version.Value) is an internal build. Public Windows artifacts begin with the explicit 2.0.0 milestone; later releases follow the twentieth-checkpoint cadence."
}

if ($releaseBlockers.Count -gt 0) {
    throw "Windows release audit failed:`n - $($releaseBlockers -join "`n - ")"
}

if ($SkipPublish) {
    throw '-SkipPublish is not permitted for artifact emission. A release requires a fresh self-contained application publish.'
}

$outputRootFullPath = [System.IO.Path]::GetFullPath($OutputRoot)
$buildRoot = Resolve-SafeChildPath -Parent $outputRootFullPath -Child "$($version.Value)-$($manifest.rid)"
if (Test-Path -LiteralPath $buildRoot) {
    if (-not $Force) {
        throw "Build staging already exists: $buildRoot. Pass -Force to replace it."
    }
    else {
        $validatedBuildRoot = Resolve-SafeChildPath -Parent $outputRootFullPath -Child "$($version.Value)-$($manifest.rid)"
        Remove-Item -LiteralPath $validatedBuildRoot -Recurse -Force
    }
}

$commonPublishPath = Resolve-SafeChildPath -Parent $buildRoot -Child ([string]$manifest.commonPublish)
$installerStagePath = Resolve-SafeChildPath -Parent $buildRoot -Child ([string]$manifest.installer.stagingDirectory)
$portableStagePath = Resolve-SafeChildPath -Parent $buildRoot -Child ([string]$manifest.portable.stagingDirectory)
$releasePath = Resolve-SafeChildPath -Parent $buildRoot -Child ([string]$manifest.releaseDirectory)
New-Item -ItemType Directory -Path $commonPublishPath -Force | Out-Null
New-Item -ItemType Directory -Path $releasePath -Force | Out-Null

$projectPath = Resolve-SafeChildPath -Parent $repositoryRoot -Child ([string]$commonDefinition.project)
$publishArguments = @(
    'publish', $projectPath,
    '--configuration', [string]$commonDefinition.configuration,
    '--runtime', [string]$manifest.rid,
    '--self-contained', ([string]$commonDefinition.selfContained).ToLowerInvariant(),
    ('-p:PublishSingleFile=' + ([string]$commonDefinition.publishSingleFile).ToLowerInvariant()),
    '-p:GraphReaderRuntimeMode=Production',
    '-p:DebugSymbols=false',
    '-p:DebugType=None',
    '--output', $commonPublishPath)

& dotnet @publishArguments | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}
Assert-WindowsX64Executable `
    -Path (Join-Path $commonPublishPath 'GraphReader.App.exe') `
    -Description 'Published GraphReader.App.exe'
$productionRuntimeSmoke = Start-Process `
    -FilePath (Join-Path $commonPublishPath 'GraphReader.App.exe') `
    -ArgumentList '--production-runtime-smoke' `
    -WorkingDirectory $commonPublishPath `
    -WindowStyle Hidden `
    -PassThru `
    -Wait
if ($productionRuntimeSmoke.ExitCode -ne 0) {
    throw "Published GraphReader.App.exe did not report its compiled Production runtime mode (exit $($productionRuntimeSmoke.ExitCode))."
}

if ($reviewedOpenCvRequired) {
    $releaseOpenCvInstallerPath = Join-Path $packagingRoot 'opencv-source\Install-ReleaseRuntime.ps1'
    if (-not (Test-Path -LiteralPath $releaseOpenCvInstallerPath -PathType Leaf)) {
        throw "Release OpenCV runtime installer is missing: $releaseOpenCvInstallerPath"
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $releaseOpenCvInstallerPath `
        -EvidenceRoot $reviewedOpenCvEvidencePath `
        -DestinationRoot $commonPublishPath `
        -ExpectedCommit $gitCommit `
        -ExpectedVersion $version.Version `
        -RepositoryRoot $repositoryRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Release OpenCV runtime installation failed with exit code $LASTEXITCODE."
    }
    $releaseOpenCvMetadata = Get-Content -LiteralPath (Join-Path $commonPublishPath 'reviewed-opencv-runtime.json') -Raw | ConvertFrom-Json
    if (-not [bool]$releaseOpenCvMetadata.cleanMachineEvidence -or
        -not [bool]$releaseOpenCvMetadata.releaseApproved -or
        [string]$releaseOpenCvMetadata.binarySha256 -cne $reviewedOpenCvRuntimeSha256) {
        throw 'Common publish OpenCV runtime metadata did not cross the direct-evidence release boundary.'
    }
}

if ($reviewedPdfiumRequired) {
    $releasePdfiumInstallerPath = Join-Path $packagingRoot 'pdfium-source\Install-ReleaseRenderer.ps1'
    if (-not (Test-Path -LiteralPath $releasePdfiumInstallerPath -PathType Leaf)) {
        throw "Release PDFium renderer installer is missing: $releasePdfiumInstallerPath"
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $releasePdfiumInstallerPath `
        -EvidenceRoot $reviewedPdfiumEvidencePath `
        -DestinationRoot $commonPublishPath `
        -ExpectedCommit $gitCommit `
        -ExpectedVersion $version.Version `
        -RepositoryRoot $repositoryRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Release PDFium renderer installation failed with exit code $LASTEXITCODE."
    }
    $releasePdfiumApproval = Get-Content -LiteralPath (Join-Path $commonPublishPath 'pdfium\reviewed-approval.json') -Raw | ConvertFrom-Json
    if (-not [bool]$releasePdfiumApproval.reviewApproved -or
        -not [bool]$releasePdfiumApproval.redistributionApproved -or
        -not [bool]$releasePdfiumApproval.bundlingApproved -or
        [string]$releasePdfiumApproval.binarySha256 -cne $reviewedPdfiumRuntimeSha256) {
        throw 'Common publish PDFium renderer did not cross the direct-evidence release boundary.'
    }
}

$requiredContentPaths = @(Get-RequiredContentArchivePaths -RepositoryRoot $repositoryRoot -Definition $commonDefinition)
$modelArchivePaths = @($modelAudit.Models | Where-Object {
        @($_.packagedArtifacts).Count -gt 0 -and $null -ne $_.productionPackageData
    } | ForEach-Object {
        $modelId = [string]$_.modelId
        $modelVersion = [string]$_.version
        "models/manifest/$modelId/$modelVersion/manifest.json"
        $_.packagedArtifacts | ForEach-Object { [string]$_.archivePath }
        [string]$_.productionPackageData.NoticeArchivePath
        [string]$_.productionPackageData.EvidenceArchivePath
    }) + @('models/production-model-index.json')
$nonApplicationPaths = @($requiredContentPaths) + @($modelArchivePaths) + @('build-metadata.json')
$publishCollisions = @(Get-FileManifest -Root $commonPublishPath | Where-Object {
        [string]$_.path -in $nonApplicationPaths
    })
if ($publishCollisions.Count -gt 0) {
    throw "Application publish collides with reserved distribution paths: $(@($publishCollisions.path) -join ', ')"
}
$applicationPublishPaths = @(Get-ApplicationPublishPaths `
        -PublishRoot $commonPublishPath `
        -NonApplicationPaths $nonApplicationPaths)

foreach ($content in @($commonDefinition.requiredContent)) {
    $sourcePath = Resolve-SafeChildPath -Parent $repositoryRoot -Child ([string]$content.source)
    $targetPath = Resolve-SafeChildPath -Parent $commonPublishPath -Child ([string]$content.target)

    if (-not (Test-Path -LiteralPath $sourcePath)) {
        throw "Required distribution content is missing: $sourcePath"
    }

    if ((Get-Item -LiteralPath $sourcePath).PSIsContainer) {
        Copy-DirectoryContent -Source $sourcePath -Destination $targetPath
    }
    else {
        New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
    }
}

$packagedModelArtifacts = New-Object System.Collections.Generic.List[object]
$productionModelIndexEntries = New-Object System.Collections.Generic.List[object]
foreach ($model in @($modelAudit.Models | Where-Object {
            @($_.packagedArtifacts).Count -gt 0 -and $null -ne $_.productionPackageData
        })) {
    $modelId = [string]$model.modelId
    $modelVersion = [string]$model.version
    $manifestArchivePath = "models/manifest/$modelId/$modelVersion/manifest.json"
    $manifestTargetPath = Resolve-SafeChildPath -Parent $commonPublishPath -Child $manifestArchivePath
    $manifestSourcePath = Join-Path $repositoryRoot ([string]$model.manifest)
    Assert-NoModelReparsePoint `
        -RepositoryRoot $repositoryRoot `
        -Path $manifestSourcePath `
        -Description "Model manifest '$($model.manifest)'"
    New-Item -ItemType Directory -Path (Split-Path -Parent $manifestTargetPath) -Force | Out-Null
    Copy-Item -LiteralPath $manifestSourcePath -Destination $manifestTargetPath
    $manifestHash = (Get-FileHash -LiteralPath $manifestTargetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($manifestHash -ne [string]$model.manifestSha256) {
        throw "Packaged model manifest checksum changed while staging: $($model.manifest)"
    }

    $indexedPayloads = New-Object System.Collections.Generic.List[object]
    foreach ($artifact in @($model.packagedArtifacts)) {
        Assert-NoModelReparsePoint `
            -RepositoryRoot $repositoryRoot `
            -Path ([string]$artifact.sourcePath) `
            -Description "Model payload '$($artifact.declaredPath)' for '$($model.manifest)'"
        $targetPath = Resolve-SafeChildPath -Parent $commonPublishPath -Child ([string]$artifact.archivePath)
        if (Test-Path -LiteralPath $targetPath) {
            throw "Model artifact archive path is already occupied: $($artifact.archivePath)"
        }

        New-Item -ItemType Directory -Path (Split-Path -Parent $targetPath) -Force | Out-Null
        Copy-Item -LiteralPath ([string]$artifact.sourcePath) -Destination $targetPath
        $packagedHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($packagedHash -ne [string]$artifact.sha256) {
            throw "Packaged model checksum differs from manifest '$($model.manifest)': $($artifact.archivePath)"
        }

        $packagedModelArtifacts.Add([ordered]@{
                modelId = $modelId
                manifest = [string]$model.manifest
                declaredPath = [string]$artifact.declaredPath
                archivePath = [string]$artifact.archivePath
                sha256 = $packagedHash
            })
        $indexedPayloads.Add([ordered]@{
                declared_path = [string]$artifact.declaredPath
                path = ([string]$artifact.archivePath).Substring('models/'.Length)
                sha256 = $packagedHash
            })
    }

    $noticeTargetPath = Resolve-SafeChildPath `
        -Parent $commonPublishPath `
        -Child ([string]$model.productionPackageData.NoticeArchivePath)
    $evidenceTargetPath = Resolve-SafeChildPath `
        -Parent $commonPublishPath `
        -Child ([string]$model.productionPackageData.EvidenceArchivePath)
    New-Item -ItemType Directory -Path (Split-Path -Parent $noticeTargetPath) -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $evidenceTargetPath) -Force | Out-Null
    Assert-NoModelReparsePoint `
        -RepositoryRoot $repositoryRoot `
        -Path ([string]$model.productionPackageData.NoticeSourcePath) `
        -Description "Model notice for '$($model.manifest)'"
    Assert-NoModelReparsePoint `
        -RepositoryRoot $repositoryRoot `
        -Path ([string]$model.productionPackageData.EvidenceSourcePath) `
        -Description "Model benchmark evidence for '$($model.manifest)'"
    Copy-Item -LiteralPath ([string]$model.productionPackageData.NoticeSourcePath) -Destination $noticeTargetPath
    Copy-Item -LiteralPath ([string]$model.productionPackageData.EvidenceSourcePath) -Destination $evidenceTargetPath
    $noticeHash = (Get-FileHash -LiteralPath $noticeTargetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $evidenceHash = (Get-FileHash -LiteralPath $evidenceTargetPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($noticeHash -ne [string]$model.productionPackageData.NoticeSha256 -or
        $evidenceHash -ne [string]$model.productionPackageData.EvidenceSha256) {
        throw "Packaged notice or benchmark evidence checksum changed while staging: $($model.manifest)"
    }

    $productionModelIndexEntries.Add([ordered]@{
            model_id = $modelId
            model_version = $modelVersion
            manifest = [ordered]@{
                path = $manifestArchivePath.Substring('models/'.Length)
                sha256 = $manifestHash
            }
            payloads = $indexedPayloads.ToArray()
            notice = [ordered]@{
                declared_path = [string]$model.productionPackageData.NoticeDeclaredPath
                path = ([string]$model.productionPackageData.NoticeArchivePath).Substring('models/'.Length)
                sha256 = $noticeHash
            }
            benchmark_evidence = [ordered]@{
                declared_path = [string]$model.productionPackageData.EvidenceDeclaredPath
                path = ([string]$model.productionPackageData.EvidenceArchivePath).Substring('models/'.Length)
                sha256 = $evidenceHash
            }
        })
}

$productionModelIndexPath = Join-Path $commonPublishPath 'models\production-model-index.json'
Write-JsonFile -Path $productionModelIndexPath -Value ([ordered]@{
        schema_version = 1
        models = $productionModelIndexEntries.ToArray()
    })

$publishBinaryCoverage = @(Assert-PublishBinaryCoverage -PublishRoot $commonPublishPath -ReleaseAudit $releaseAudit)
$embeddedComponentRecords = @(Get-EmbeddedComponentRecords `
        -BinaryCoverage $publishBinaryCoverage `
        -ReleaseAudit $releaseAudit)
$embeddedComponentIds = @($embeddedComponentRecords | ForEach-Object { [string]$_.id } | Sort-Object -Unique)
$fontBearingPayloads = @($publishBinaryCoverage | Where-Object {
        @($_.embeddedComponentIds).Count -gt 0
    } | ForEach-Object {
        [ordered]@{
            path = [string]$_.path
            sha256 = [string]$_.sha256
            embeddedComponentIds = @($_.embeddedComponentIds | ForEach-Object { [string]$_ })
        }
    })
$buildUtc = [DateTime]::UtcNow.ToString(
    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
    [System.Globalization.CultureInfo]::InvariantCulture)
$buildMetadataPath = Join-Path $commonPublishPath 'build-metadata.json'
$buildMetadata = [ordered]@{
    schemaVersion = 1
    product = 'Graph Auto Reader'
    version = $version.Value
    rid = [string]$manifest.rid
    runtimeMode = 'Production'
    productionRuntimeSmokeExitCode = [int]$productionRuntimeSmoke.ExitCode
    gitCommit = $gitCommit
    buildUtc = $buildUtc
    contractVersion = $contractVersion
    applicationPublishFiles = $applicationPublishPaths
    publishBinaryCoverage = $publishBinaryCoverage
    packagedModelArtifacts = $packagedModelArtifacts.ToArray()
    productionModelIndex = [ordered]@{
        path = 'models/production-model-index.json'
        sha256 = (Get-FileHash -LiteralPath $productionModelIndexPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    modelManifests = @($modelAudit.Models | ForEach-Object {
            [ordered]@{
                modelId = $_.modelId
                version = $_.version
                manifest = $_.manifest
                manifestSha256 = $_.manifestSha256
                modelSha256 = $_.modelSha256
                redistribution = $_.redistribution
                archivePaths = @($_.packagedArtifacts | ForEach-Object { $_.archivePath })
            }
        })
}
Write-JsonFile -Path $buildMetadataPath -Value $buildMetadata

$commonAllowedPaths = @($applicationPublishPaths) + @($requiredContentPaths) + @($modelArchivePaths) + @('build-metadata.json')
Assert-PayloadAllowlist -PayloadRoot $commonPublishPath -AllowedPaths $commonAllowedPaths
Assert-NoForbiddenReleaseFiles -Root $commonPublishPath
Copy-DirectoryContent -Source $commonPublishPath -Destination $installerStagePath
Copy-DirectoryContent -Source $commonPublishPath -Destination $portableStagePath

$portableDefinitionPath = Resolve-SafeChildPath -Parent $packagingRoot -Child ([string]$manifest.portable.definition)
$portableDefinition = Get-Content -LiteralPath $portableDefinitionPath -Raw | ConvertFrom-Json
$portableSentinelPath = Resolve-SafeChildPath -Parent $portableStagePath -Child ([string]$portableDefinition.sentinel)
New-Item -ItemType File -Path $portableSentinelPath -Force | Out-Null

Assert-PayloadAllowlist -PayloadRoot $installerStagePath -AllowedPaths $commonAllowedPaths
Assert-PayloadAllowlist `
    -PayloadRoot $portableStagePath `
    -AllowedPaths (@($commonAllowedPaths) + @([string]$portableDefinition.sentinel))

$commonFiles = @(Get-FileManifest -Root $commonPublishPath)
$installerFiles = @(Get-FileManifest -Root $installerStagePath)
$portableFiles = @(Get-FileManifest -Root $portableStagePath)
$commonPayloadHash = Get-ManifestDigest -Files $commonFiles
$installerPayloadHash = Get-ManifestDigest -Files $installerFiles
$portablePayloadHash = Get-ManifestDigest -Files $portableFiles
if ($installerPayloadHash -ne $commonPayloadHash) {
    throw 'Installer staging does not exactly match the shared common publish payload.'
}

$portableCommonFiles = @($portableFiles | Where-Object { $_.path -ne [string]$portableDefinition.sentinel })
if ((Get-ManifestDigest -Files $portableCommonFiles) -ne $commonPayloadHash) {
    throw 'Portable staging differs from the shared publish by more than portable.mode.'
}

Assert-NoForbiddenReleaseFiles -Root $installerStagePath
Assert-NoForbiddenReleaseFiles -Root $portableStagePath

$installerArtifactPath = Resolve-SafeChildPath -Parent $releasePath -Child $installerFileName
$portableArtifactPath = Resolve-SafeChildPath -Parent $releasePath -Child $portableFileName

$installerPayloadArchive = Join-Path $buildRoot 'installer\payload.zip'
New-ZipFromDirectory -Source $installerStagePath -Destination $installerPayloadArchive
$installerProjectPath = Join-Path $packagingRoot 'installer\GraphReader.Installer.csproj'
$installerPublishPath = Join-Path $buildRoot 'installer\bootstrapper'
$installerPublishArguments = @(
    'publish', $installerProjectPath,
    '--configuration', 'Release',
    '--runtime', [string]$manifest.rid,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:DebugSymbols=false',
    '-p:DebugType=None',
    "-p:InstallerPayloadZip=$installerPayloadArchive",
    '--output', $installerPublishPath)
& dotnet @installerPublishArguments | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "Installer publish failed with exit code $LASTEXITCODE."
}

$builtInstallerPath = Join-Path $installerPublishPath 'GraphReader.Installer.exe'
if (-not (Test-Path -LiteralPath $builtInstallerPath -PathType Leaf)) {
    throw "Installer bootstrapper was not emitted: $builtInstallerPath"
}
Copy-Item -LiteralPath $builtInstallerPath -Destination $installerArtifactPath -Force
New-ZipFromDirectory -Source $portableStagePath -Destination $portableArtifactPath

$installerHash = (Get-FileHash -LiteralPath $installerArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
$portableHash = (Get-FileHash -LiteralPath $portableArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
$installerCoverageRecords = @($releaseAudit.Definition.emittedArtifactCoverage | Where-Object {
        [string]$_.artifactKind -eq 'installer'
    })
if ($installerCoverageRecords.Count -ne 1 -or
    [string]$installerCoverageRecords[0].fileNameTemplate -ne [string]$manifest.installer.fileNameTemplate) {
    throw 'Tracked installer artifact coverage is missing or does not match the installer filename template.'
}

$installerComponentIds = @($installerCoverageRecords[0].componentIds | ForEach-Object { [string]$_ })
$installerLicenses = @($installerComponentIds | ForEach-Object {
        [string]$releaseAudit.Components[$_].license
    } | Sort-Object -Unique)
$installerNoticePaths = @($installerComponentIds | ForEach-Object {
        @($releaseAudit.Components[$_].noticePaths)
    } | Sort-Object -Unique)
$installerProvenance = [ordered]@{
    componentIds = $installerComponentIds
    licenses = $installerLicenses
    noticePaths = $installerNoticePaths
    embeddedComponentIds = $embeddedComponentIds
    fontBearingPayloads = $fontBearingPayloads
    checksumPolicy = 'release-sbom'
    setupSha256 = $installerHash
    installedCopyName = [string]$installerCoverageRecords[0].installedCopyName
    installedCopySha256 = $installerHash
}

$portableComponentIds = @($publishBinaryCoverage | ForEach-Object { [string]$_.componentId } | Sort-Object -Unique)
$portableLicenses = @($portableComponentIds | ForEach-Object {
        [string]$releaseAudit.Components[$_].license
    } | Sort-Object -Unique)
$portableNoticePaths = @($portableComponentIds | ForEach-Object {
        @($releaseAudit.Components[$_].noticePaths)
    } | Sort-Object -Unique)
$releaseMetadata = [ordered]@{
    schemaVersion = 1
    product = 'Graph Auto Reader'
    version = $version.Value
    versionSource = $version.Source
    rid = [string]$manifest.rid
    gitCommit = $gitCommit
    buildUtc = $buildUtc
    contractVersion = $contractVersion
    commonPayload = [ordered]@{
        sha256 = $commonPayloadHash
        fileCount = $commonFiles.Count
        files = $commonFiles
    }
    embeddedComponents = $embeddedComponentRecords
    installer = [ordered]@{
        fileName = $installerFileName
        sha256 = $installerHash
        payloadSha256 = $installerPayloadHash
        sharedPayloadSha256 = $commonPayloadHash
        provenance = $installerProvenance
    }
    portable = [ordered]@{
        fileName = $portableFileName
        sha256 = $portableHash
        payloadSha256 = $portablePayloadHash
        sharedPayloadSha256 = $commonPayloadHash
        provenance = [ordered]@{
            componentIds = $portableComponentIds
            licenses = $portableLicenses
            noticePaths = $portableNoticePaths
            embeddedComponentIds = $embeddedComponentIds
            fontBearingPayloads = $fontBearingPayloads
            checksumPolicy = 'release-sbom'
            archiveSha256 = $portableHash
        }
    }
    versionPolicy = [ordered]@{
        releaseBuilds = @(Get-GraphReaderReleaseBuilds)
        stablePromotionRelease = (Get-GraphReaderStablePromotionVersion)
        upgrade = 'allowed'
        repair = 'same-version reinstall'
        downgrade = 'blocked unless --allow-downgrade is passed to the installer'
    }
}
$releaseMetadataPath = Join-Path $releasePath 'release-metadata.json'
Write-JsonFile -Path $releaseMetadataPath -Value $releaseMetadata

$releaseNotesTemplate = Get-Content -LiteralPath (Join-Path $packagingRoot 'common\RELEASE_NOTES.template.md') -Raw
$releaseNotes = $releaseNotesTemplate.Replace('{{VERSION}}', $version.Value).Replace('{{GIT_COMMIT}}', $gitCommit).Replace('{{BUILD_UTC}}', $buildUtc)
Write-Utf8NoBom -Path (Join-Path $releasePath 'RELEASE_NOTES.md') -Content $releaseNotes
Copy-Item -LiteralPath (Join-Path $packagingRoot 'common\KNOWN_LIMITATIONS.md') -Destination (Join-Path $releasePath 'KNOWN_LIMITATIONS.md') -Force

$sbomComponents = New-Object System.Collections.Generic.List[object]
$binaryCoverageByPath = [System.Collections.Generic.Dictionary[string,object]]::new([System.StringComparer]::Ordinal)
foreach ($coverage in $publishBinaryCoverage) {
    $binaryCoverageByPath.Add([string]$coverage.path, $coverage)
}
foreach ($file in $commonFiles) {
    $sbomComponent = [ordered]@{
        type = 'file'
        name = $file.path
        hashes = @([ordered]@{ alg = 'SHA-256'; content = $file.sha256 })
    }
    if ($binaryCoverageByPath.ContainsKey([string]$file.path)) {
        $coverage = $binaryCoverageByPath[[string]$file.path]
        $component = $releaseAudit.Components[[string]$coverage.componentId]
        $sbomComponent['licenses'] = @([ordered]@{ expression = [string]$component.license })
        $componentProperties = New-Object System.Collections.Generic.List[object]
        $componentProperties.Add(
            [ordered]@{ name = 'graphreader:releaseAuditComponentIds'; value = [string]$component.id })
        $componentProperties.Add(
            [ordered]@{ name = 'graphreader:noticePaths'; value = (@($component.noticePaths) -join ';') })
        $coverageEmbeddedIds = @($coverage.embeddedComponentIds | ForEach-Object { [string]$_ })
        if ($coverageEmbeddedIds.Count -gt 0) {
            $componentProperties.Add([ordered]@{
                    name = 'graphreader:embeddedComponentIds'
                    value = ($coverageEmbeddedIds -join ';')
                })
            $sbomComponent['components'] = @($coverageEmbeddedIds | ForEach-Object {
                    $embedded = $releaseAudit.Components[$_]
                    [ordered]@{
                        type = 'library'
                        'bom-ref' = [string]$embedded.id
                        name = [string]$embedded.component
                        version = [string]$embedded.version
                        hashes = @([ordered]@{
                                alg = 'SHA-256'
                                content = ([string]$embedded.artifactSha256).ToLowerInvariant()
                            })
                        licenses = @([ordered]@{ expression = [string]$embedded.license })
                        externalReferences = @([ordered]@{
                                type = 'distribution'
                                url = [string]$embedded.source
                            })
                        properties = @(
                            [ordered]@{ name = 'graphreader:sourceRevision'; value = [string]$embedded.sourceRevision },
                            [ordered]@{ name = 'graphreader:checksumPolicy'; value = [string]$embedded.checksumPolicy },
                            [ordered]@{ name = 'graphreader:noticePaths'; value = (@($embedded.noticePaths) -join ';') },
                            [ordered]@{ name = 'graphreader:embeddedIn'; value = [string]$coverage.path })
                    }
                })
        }
        $sbomComponent['properties'] = $componentProperties.ToArray()
    }
    $sbomComponents.Add($sbomComponent)
}

$sbomComponents.Add([ordered]@{
        type = 'file'
        name = $installerFileName
        hashes = @([ordered]@{ alg = 'SHA-256'; content = $installerHash })
        licenses = @($installerLicenses | ForEach-Object { [ordered]@{ expression = $_ } })
        properties = @(
            [ordered]@{ name = 'graphreader:releaseAuditComponentIds'; value = ($installerComponentIds -join ';') },
            [ordered]@{ name = 'graphreader:noticePaths'; value = ($installerNoticePaths -join ';') },
            [ordered]@{ name = 'graphreader:embeddedComponentIds'; value = ($embeddedComponentIds -join ';') },
            [ordered]@{ name = 'graphreader:fontBearingPayloads'; value = ($fontBearingPayloads | ConvertTo-Json -Compress -Depth 5) },
            [ordered]@{ name = 'graphreader:installedCopyName'; value = [string]$installerCoverageRecords[0].installedCopyName },
            [ordered]@{ name = 'graphreader:installedCopySha256'; value = $installerHash })
    })
$sbomComponents.Add([ordered]@{
        type = 'file'
        name = $portableFileName
        hashes = @([ordered]@{ alg = 'SHA-256'; content = $portableHash })
        licenses = @($portableLicenses | ForEach-Object { [ordered]@{ expression = $_ } })
        properties = @(
            [ordered]@{ name = 'graphreader:releaseAuditComponentIds'; value = ($portableComponentIds -join ';') },
            [ordered]@{ name = 'graphreader:noticePaths'; value = ($portableNoticePaths -join ';') },
            [ordered]@{ name = 'graphreader:embeddedComponentIds'; value = ($embeddedComponentIds -join ';') },
            [ordered]@{ name = 'graphreader:fontBearingPayloads'; value = ($fontBearingPayloads | ConvertTo-Json -Compress -Depth 5) })
    })

$sbom = [ordered]@{
    bomFormat = 'CycloneDX'
    specVersion = '1.6'
    serialNumber = "urn:uuid:$([Guid]::NewGuid())"
    version = 1
    metadata = [ordered]@{
        timestamp = $buildUtc
        component = [ordered]@{
            type = 'application'
            name = 'Graph Auto Reader'
            version = $version.Value
            licenses = @([ordered]@{ expression = 'Apache-2.0' })
        }
    }
    components = $sbomComponents.ToArray()
}
$sbomPath = Join-Path $releasePath 'sbom.cdx.json'
Write-JsonFile -Path $sbomPath -Value $sbom

$checksumTargets = @(
    $installerArtifactPath,
    $portableArtifactPath,
    $releaseMetadataPath,
    $sbomPath,
    (Join-Path $releasePath 'RELEASE_NOTES.md'),
    (Join-Path $releasePath 'KNOWN_LIMITATIONS.md'))
$checksumLines = @($checksumTargets | Sort-Object | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $(Split-Path -Leaf $_)"
    })
Write-Utf8NoBom -Path (Join-Path $releasePath 'SHA256SUMS.txt') -Content (($checksumLines -join [Environment]::NewLine) + [Environment]::NewLine)

$null = & $releaseVerifierPath `
    -ManifestPath $manifestFullPath `
    -ArtifactRoot $releasePath `
    -VersionFilePath (Join-Path $repositoryRoot 'Directory.Build.props') `
    -RequireReleaseVersion

[pscustomobject]@{
    SchemaVersion = 1
    Version = $version.Value
    VersionSource = $version.Source
    RuntimeIdentifier = [string]$manifest.rid
    GitCommit = $gitCommit
    ContractVersion = $contractVersion
    ReleaseEligible = $true
    ReleaseReady = $true
    CommonPublish = $commonPublishPath
    InstallerStage = $installerStagePath
    PortableStage = $portableStagePath
    ArtifactRoot = $releasePath
    InstallerArtifact = $installerArtifactPath
    PortableArtifact = $portableArtifactPath
    Checksums = Join-Path $releasePath 'SHA256SUMS.txt'
    Sbom = $sbomPath
    ReleaseMetadata = $releaseMetadataPath
    ArtifactsEmitted = $true
}
