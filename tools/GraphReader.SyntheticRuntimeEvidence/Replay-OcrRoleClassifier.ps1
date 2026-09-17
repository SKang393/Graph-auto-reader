# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AssemblyDirectory,

    [Parameter(Mandatory = $true)]
    [string] $OcrAssemblySha256,

    [Parameter(Mandatory = $true)]
    [string] $InferenceAssemblySha256,

    [Parameter(Mandatory = $true)]
    [string] $ClassifierSourcePath,

    [Parameter(Mandatory = $true)]
    [string] $ClassifierSourceSha256,

    [Parameter(Mandatory = $true)]
    [string] $EvaluationPath,

    [Parameter(Mandatory = $true)]
    [string] $EvaluationSha256,

    [Parameter(Mandatory = $true)]
    [ValidateSet("graph-text-role-classifier-v2", "graph-text-role-classifier-v3")]
    [string] $ExpectedClassifierVersion,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Require {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) {
        throw "OCR role replay rejected: $Message"
    }
}

function Get-Sha256 {
    param([string] $Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Assert-Sha256 {
    param([string] $Path, [string] $Expected, [string] $Label)
    Require ($Expected -cmatch '^[0-9a-f]{64}$') "$Label expected SHA-256 is invalid"
    $actual = Get-Sha256 $Path
    Require ($actual -ceq $Expected) "$Label SHA-256 changed"
    return $actual
}

function Resolve-OwnedFile {
    param([string] $Path, [string] $Root, [string] $Label)
    $candidate = if ([IO.Path]::IsPathRooted($Path)) {
        [IO.Path]::GetFullPath($Path)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $Root $Path))
    }
    $rootPrefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Require ($candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) "$Label is outside the repository"
    Require (Test-Path -LiteralPath $candidate -PathType Leaf) "$Label does not exist"
    return $candidate
}

function Resolve-OwnedDirectory {
    param([string] $Path, [string] $Root, [string] $Label)
    $candidate = if ([IO.Path]::IsPathRooted($Path)) {
        [IO.Path]::GetFullPath($Path)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $Root $Path))
    }
    $rootPrefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Require ($candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) "$Label is outside the repository"
    Require (Test-Path -LiteralPath $candidate -PathType Container) "$Label does not exist"
    return $candidate
}

function Read-AuthenticatedJson {
    param([string] $Path, [string] $ExpectedSha256, [string] $Label)
    [void](Assert-Sha256 $Path $ExpectedSha256 $Label)
    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function New-GenericList {
    param([Type] $ElementType)
    $definition = [System.Collections.Generic.List[int]].GetGenericTypeDefinition()
    $listType = $definition.MakeGenericType([Type[]]@($ElementType))
    return ,([Activator]::CreateInstance($listType))
}

function New-Instance {
    param([Type] $Type, [object[]] $Arguments)
    $constructors = @($Type.GetConstructors() | Where-Object { $_.GetParameters().Count -eq $Arguments.Count })
    Require ($constructors.Count -eq 1) "constructor binding changed for $($Type.FullName)"
    return $constructors[0].Invoke($Arguments)
}

function Assert-DoubleEqual {
    param([double] $Actual, [double] $Expected, [string] $Label)
    Require (
        [BitConverter]::DoubleToInt64Bits($Actual) -eq [BitConverter]::DoubleToInt64Bits($Expected)
    ) "$Label changed"
}

function Assert-BoundsEqual {
    param([object] $Actual, [object] $Expected, [string] $Label)
    Assert-DoubleEqual ([double]$Actual.Left) ([double]$Expected.left) "$Label left"
    Assert-DoubleEqual ([double]$Actual.Top) ([double]$Expected.top) "$Label top"
    Assert-DoubleEqual ([double]$Actual.Right) ([double]$Expected.right) "$Label right"
    Assert-DoubleEqual ([double]$Actual.Bottom) ([double]$Expected.bottom) "$Label bottom"
}

function Get-SortedCountMap {
    param([object[]] $Values)
    $result = [ordered]@{}
    foreach ($group in @($Values | Group-Object | Sort-Object Name)) {
        $result[[string]$group.Name] = [int]$group.Count
    }
    return $result
}

function Convert-RuntimeRoleName {
    param([object] $Role)
    switch -CaseSensitive ($Role.ToString()) {
        "YTick" { return "ytick" }
        "XTick" { return "xtick" }
        "AxisTitle" { return "axistitle" }
        "PhaseHeading" { return "phaseheading" }
        "LegendText" { return "legendtext" }
        "Participant" { return "participant" }
        "Annotation" { return "annotation" }
        "Other" { return "other" }
        default { throw "OCR role replay rejected: runtime role serialization changed" }
    }
}

$started = [Diagnostics.Stopwatch]::StartNew()
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$scriptPath = [IO.Path]::GetFullPath($PSCommandPath)
$scriptSha256 = Get-Sha256 $scriptPath

Require ($PSVersionTable.PSEdition -ceq "Core") "PowerShell Core is required"
Require ([Environment]::Version.Major -eq 10) ".NET 10 is required"

$assemblyRoot = Resolve-OwnedDirectory $AssemblyDirectory $repoRoot "assembly directory"
$ocrPath = Resolve-OwnedFile (Join-Path $assemblyRoot "GraphReader.Ocr.dll") $repoRoot "OCR assembly"
$inferencePath = Resolve-OwnedFile (Join-Path $assemblyRoot "GraphReader.Inference.dll") $repoRoot "inference assembly"
$classifierPath = Resolve-OwnedFile $ClassifierSourcePath $repoRoot "classifier source"
$evaluationFile = Resolve-OwnedFile $EvaluationPath $repoRoot "cached evaluation"
[void](Assert-Sha256 $ocrPath $OcrAssemblySha256 "OCR assembly")
[void](Assert-Sha256 $inferencePath $InferenceAssemblySha256 "inference assembly")
[void](Assert-Sha256 $classifierPath $ClassifierSourceSha256 "classifier source")
$evaluation = Read-AuthenticatedJson $evaluationFile $EvaluationSha256 "cached evaluation"

$sourceText = Get-Content -Raw -LiteralPath $classifierPath
$escapedVersion = [Regex]::Escape($ExpectedClassifierVersion)
Require (
    $sourceText -cmatch "internal\s+const\s+string\s+Version\s*=\s*`"$escapedVersion`"\s*;"
) "classifier source does not declare the expected version"

Require ($evaluation.schema -ceq "graphreader.participant-lane-candidate-evaluation.v1") "evaluation schema changed"
Require ($evaluation.development_only -eq $true) "evaluation is not development-only"
Require ($evaluation.synthetic_only -eq $true) "evaluation is not synthetic-only"
Require ($evaluation.private_data -eq $false) "evaluation declares private data"
Require ($evaluation.sealed_data -eq $false) "evaluation declares sealed data"
Require ($evaluation.truth_used_by_runtime -eq $false) "evaluation used truth at runtime"
Require ($evaluation.production_approved -eq $false) "evaluation unexpectedly declares production approval"
Require ($evaluation.status -ceq "panels_completed") "evaluation did not complete panels"
Require ([int]$evaluation.panel_count -eq 37) "evaluation panel count changed"
Require ([int]$evaluation.completed_panel_count -eq 37) "evaluation completed-panel count changed"
Require ([int]$evaluation.failed_panel_count -eq 0) "evaluation contains failed panels"
Require (@($evaluation.panels).Count -eq 37) "evaluation panel inventory changed"

$splitCounts = @($evaluation.panels | Group-Object split)
$trainSplit = @($splitCounts | Where-Object Name -CEQ "train")
$developmentSplit = @($splitCounts | Where-Object Name -CEQ "validation")
Require (
    $trainSplit.Count -eq 1 -and $trainSplit[0].Count -eq 28 -and
    $developmentSplit.Count -eq 1 -and $developmentSplit[0].Count -eq 9 -and
    $splitCounts.Count -eq 2
) "evaluation train/development panel split changed"

$candidatePath = Resolve-OwnedFile ([string]$evaluation.candidate.path) $repoRoot "candidate descriptor"
$candidate = Read-AuthenticatedJson $candidatePath ([string]$evaluation.candidate.sha256) "candidate descriptor"
Require ($candidate.schema -ceq "graphreader.frozen-db-head-ocr-candidate.v1") "candidate schema changed"
Require ($candidate.composition_version -ceq "original-db-head-participant-lane-v1") "candidate composition changed"
Require ($candidate.production_approved -eq $false) "candidate unexpectedly declares production approval"
Require ($candidate.training_input_ready -eq $false) "candidate unexpectedly declares training readiness"
Require ($evaluation.candidate.composition_version -ceq $candidate.composition_version) "evaluation candidate composition changed"

$recognizerManifestPath = Resolve-OwnedFile ([string]$candidate.recognizer.manifest_path) $repoRoot "recognizer manifest"
$recognizerManifest = Read-AuthenticatedJson (
    $recognizerManifestPath
) ([string]$candidate.recognizer.manifest_sha256) "recognizer manifest"
Require ($recognizerManifest.task -ceq "ocr_recognition") "recognizer manifest task changed"
Require ($recognizerManifest.model_id -ceq [string]$candidate.recognizer.model_id) "recognizer model ID changed"
Require ($recognizerManifest.model_version -ceq [string]$candidate.recognizer.model_version) "recognizer model version changed"
Require (@($recognizerManifest.inputs).Count -eq 1) "recognizer input inventory changed"
$recognizerInput = @($recognizerManifest.inputs)[0]
$inputShape = @($recognizerInput.shape)
Require (
    $recognizerInput.layout -ceq "NCHW" -and $inputShape.Count -eq 4 -and
    [string]$inputShape[0] -ceq "N" -and [int]$inputShape[1] -eq 3 -and
    [int]$inputShape[2] -eq 48 -and [string]$inputShape[3] -ceq "W"
) "recognizer dynamic input contract changed"
Require (
    [string]$recognizerManifest.preprocessing.width_policy -ceq "paddle_batch_max_wh_ratio_v1" -and
    [int]$recognizerManifest.preprocessing.minimum_width -eq 320 -and
    [int]$recognizerManifest.preprocessing.maximum_width -eq 4096
) "recognizer crop-width contract changed"

$evaluationAssemblies = @{}
foreach ($row in @($evaluation.execution_assemblies)) {
    Require (-not $evaluationAssemblies.ContainsKey([string]$row.name)) "duplicate evaluation assembly identity"
    $evaluationAssemblies[[string]$row.name] = $row
}
$candidateAssemblies = @{}
foreach ($row in @($candidate.execution_assemblies)) {
    Require (-not $candidateAssemblies.ContainsKey([string]$row.name)) "duplicate candidate assembly identity"
    $candidateAssemblies[[string]$row.name] = $row
}
Require ($candidateAssemblies.Count -eq $evaluationAssemblies.Count) "candidate/evaluation assembly inventory changed"
foreach ($name in $evaluationAssemblies.Keys) {
    Require ($candidateAssemblies.ContainsKey($name)) "candidate/evaluation assembly name changed"
    Require (
        [string]$candidateAssemblies[$name].path -ceq [string]$evaluationAssemblies[$name].path -and
        [string]$candidateAssemblies[$name].sha256 -ceq [string]$evaluationAssemblies[$name].sha256
    ) "candidate/evaluation assembly binding changed"
}
Require ($evaluationAssemblies.ContainsKey("GraphReader.Ocr")) "evaluation lacks OCR assembly identity"
Require ($evaluationAssemblies.ContainsKey("GraphReader.Inference")) "evaluation lacks inference assembly identity"
if ($ExpectedClassifierVersion -ceq "graph-text-role-classifier-v2") {
    Require (
        [string]$evaluationAssemblies["GraphReader.Ocr"].sha256 -ceq $OcrAssemblySha256
    ) "v2 OCR assembly differs from the cached evaluation runtime"
    Require (
        [string]$evaluationAssemblies["GraphReader.Inference"].sha256 -ceq $InferenceAssemblySha256
    ) "v2 inference assembly differs from the cached evaluation runtime"
}
else {
    Require (
        [string]$evaluationAssemblies["GraphReader.Ocr"].sha256 -cne $OcrAssemblySha256
    ) "v3 replay did not supply a distinct OCR assembly"
}

$loadedGraphReader = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {
    $_.GetName().Name.StartsWith("GraphReader.", [StringComparison]::Ordinal)
})
Require ($loadedGraphReader.Count -eq 0) "GraphReader assemblies were already loaded; use a fresh process"

$loadContext = [Runtime.Loader.AssemblyLoadContext]::Default
$inferenceAssembly = $loadContext.LoadFromAssemblyPath($inferencePath)
$ocrAssembly = $loadContext.LoadFromAssemblyPath($ocrPath)
Require ($inferenceAssembly.GetName().Name -ceq "GraphReader.Inference") "wrong inference assembly loaded"
Require ($ocrAssembly.GetName().Name -ceq "GraphReader.Ocr") "wrong OCR assembly loaded"
Require ([IO.Path]::GetFullPath($inferenceAssembly.Location) -ceq $inferencePath) "inference assembly load path changed"
Require ([IO.Path]::GetFullPath($ocrAssembly.Location) -ceq $ocrPath) "OCR assembly load path changed"

$flagsPublicStatic = [Reflection.BindingFlags]::Public -bor [Reflection.BindingFlags]::Static
$flagsNonPublicStatic = [Reflection.BindingFlags]::NonPublic -bor [Reflection.BindingFlags]::Static
$pointType = $ocrAssembly.GetType("GraphReader.Ocr.OcrPoint", $true)
$rectangleType = $ocrAssembly.GetType("GraphReader.Ocr.OcrRectangle", $true)
$polygonType = $ocrAssembly.GetType("GraphReader.Ocr.OcrPolygon", $true)
$evidenceType = $ocrAssembly.GetType("GraphReader.Ocr.OcrRegionEvidence", $true)
$regionType = $ocrAssembly.GetType("GraphReader.Ocr.OcrDetectedRegion", $true)
$optionsType = $ocrAssembly.GetType("GraphReader.Ocr.OcrPipelineOptions", $true)
$cropModeType = $ocrAssembly.GetType("GraphReader.Ocr.OcrCropWidthMode", $true)
$assemblerType = $ocrAssembly.GetType("GraphReader.Ocr.ParticipantLaneTextRegionAssembler", $true)
$pipelineType = $ocrAssembly.GetType("GraphReader.Ocr.OcrPipeline", $true)
$classifierType = $ocrAssembly.GetType("GraphReader.Ocr.GraphTextRoleClassifier", $true)

$versionField = $classifierType.GetField("Version", $flagsNonPublicStatic)
Require ($null -ne $versionField) "classifier version field is unavailable"
$runtimeClassifierVersion = [string]$versionField.GetRawConstantValue()
Require ($runtimeClassifierVersion -ceq $ExpectedClassifierVersion) "runtime classifier version changed"

$compositionField = $assemblerType.GetField("CompositionVersion", $flagsPublicStatic)
Require ($null -ne $compositionField) "participant-lane composition field is unavailable"
$assemblyCompositionVersion = [string]$compositionField.GetRawConstantValue()
Require ($assemblyCompositionVersion -ceq "participant-lane-aligned-word-assembly-v1") "participant-lane composition changed"
$assembleMethod = $assemblerType.GetMethod("AssembleWithMembership", $flagsPublicStatic)
Require ($null -ne $assembleMethod) "participant-lane assembler API changed"
$enrichMethod = $pipelineType.GetMethod("EnrichGeometry", $flagsNonPublicStatic)
Require ($null -ne $enrichMethod -and $enrichMethod.GetParameters().Count -eq 3) "geometry enrichment API changed"
$classifyMethod = $classifierType.GetMethod("Classify", $flagsPublicStatic)
Require ($null -ne $classifyMethod -and $classifyMethod.GetParameters().Count -eq 3) "classifier API changed"

$pipelineOptions = [Activator]::CreateInstance($optionsType)
$optionsType.GetProperty("StageVersion").SetValue($pipelineOptions, [string]$candidate.recognizer.model_version)
$optionsType.GetProperty("CropWidth").SetValue($pipelineOptions, [int]$recognizerManifest.preprocessing.minimum_width)
$optionsType.GetProperty("CropHeight").SetValue($pipelineOptions, [int]$inputShape[2])
$dynamicCropMode = [Enum]::Parse($cropModeType, "PaddleBatchMaximumAspectRatio", $false)
$optionsType.GetProperty("CropWidthMode").SetValue($pipelineOptions, $dynamicCropMode)
$optionsType.GetProperty("MaximumCropWidth").SetValue(
    $pipelineOptions,
    [int]$recognizerManifest.preprocessing.maximum_width)
$optionsType.GetProperty("EnableParticipantLaneAssembly").SetValue($pipelineOptions, $true)
Require (
    [bool]$optionsType.GetProperty("InferVerticalOrientationForTallRegions").GetValue($pipelineOptions)
) "candidate geometry-enrichment option changed"

$regionRows = [Collections.Generic.List[object]]::new()
$allRawCount = 0
$allEffectiveCount = 0
$allRecognizedCount = 0
$assembledGroupCount = 0
$panelIdSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$rawKeySet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$effectiveKeySet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

foreach ($panel in @($evaluation.panels)) {
    $panelId = [string]$panel.panel_id
    Require ($panelIdSet.Add($panelId)) "panel ID is duplicated"
    Require ($panel.status -ceq "completed") "panel status changed"
    Require ($panel.assembly_context.composition_version -ceq $assemblyCompositionVersion) "panel assembly composition changed"
    Require (@($panel.assembly_context.plot_bounds_panel_ltrb).Count -eq 4) "panel plot bounds changed"
    Require (@($panel.region_failures).Count -eq 0) "cached evaluation contains recognition failures"

    $plot = @($panel.assembly_context.plot_bounds_panel_ltrb)
    $plotArguments = [object[]]::new(4)
    $plotArguments[0] = [double]$plot[0]
    $plotArguments[1] = [double]$plot[1]
    $plotArguments[2] = ([double]$plot[2] - [double]$plot[0])
    $plotArguments[3] = ([double]$plot[3] - [double]$plot[1])
    $plotBounds = New-Instance -Type $rectangleType -Arguments $plotArguments
    Require ([bool]$rectangleType.GetProperty("IsValid").GetValue($plotBounds)) "panel plot bounds are invalid"

    $rawRegions = New-GenericList $regionType
    $panelRawIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($raw in @($panel.raw_detector_regions)) {
        Require ($null -eq $raw.context) "raw detector context is no longer null"
        Require ($raw.coordinate_space -ceq "source_original_pixels") "raw coordinate space changed"
        $rawId = [string]$raw.region_id
        Require ($panelRawIds.Add($rawId)) "raw region ID is duplicated within a panel"
        Require ($rawKeySet.Add("$panelId`n$rawId")) "panel/raw region identity is duplicated"
        $points = New-GenericList $pointType
        foreach ($point in @($raw.panel_polygon.points)) {
            $pointArguments = [object[]]::new(2)
            $pointArguments[0] = [double]$point.x
            $pointArguments[1] = [double]$point.y
            $points.Add((New-Instance -Type $pointType -Arguments $pointArguments))
        }
        $polygonArguments = [object[]]::new(1)
        $polygonArguments[0] = $points
        $polygon = New-Instance -Type $polygonType -Arguments $polygonArguments
        Assert-BoundsEqual ($polygonType.GetProperty("Bounds").GetValue($polygon)) $raw.panel_polygon.bounds "raw polygon"

        $evidence = $null
        if ($null -ne $raw.evidence) {
            $reasons = New-GenericList ([string])
            foreach ($reason in @($raw.evidence.reasons)) {
                $reasons.Add([string]$reason)
            }
            $evidenceArguments = [object[]]::new(6)
            $evidenceArguments[0] = [int]$raw.evidence.component_count
            $evidenceArguments[1] = [double]$raw.evidence.ink_density
            $evidenceArguments[2] = [double]$raw.evidence.text_likelihood
            $evidenceArguments[3] = [double]$raw.evidence.structure_likelihood
            $evidenceArguments[4] = [bool]$raw.evidence.likely_graph_structure
            $evidenceArguments[5] = $reasons
            $evidence = New-Instance -Type $evidenceType -Arguments $evidenceArguments
        }
        $regionArguments = [object[]]::new(7)
        $regionArguments[0] = [string]$raw.region_id
        $regionArguments[1] = $polygon
        $regionArguments[2] = [double]$raw.orientation_degrees
        $regionArguments[3] = [double]$raw.detection_confidence
        $regionArguments[4] = $null
        $regionArguments[5] = [string]$raw.coordinate_space
        $regionArguments[6] = $evidence
        $region = New-Instance -Type $regionType -Arguments $regionArguments
        $rawRegions.Add($region)
    }

    $assembleArguments = [object[]]::new(2)
    $assembleArguments[0] = $rawRegions
    $assembleArguments[1] = $plotBounds
    $groups = @($assembleMethod.Invoke($null, $assembleArguments))
    $expectedGroups = @($panel.effective_regions)
    Require ($groups.Count -eq $expectedGroups.Count) "effective region count changed"
    $effectiveRegions = New-GenericList $regionType
    $panelMemberIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($index = 0; $index -lt $groups.Count; $index++) {
        $group = $groups[$index]
        $region = $group.Region
        $expected = $expectedGroups[$index]
        $members = @($group.MemberRegionIds)
        $expectedMembers = @($expected.member_raw_region_ids)
        Require ($region.RegionId -ceq [string]$expected.region_id) "effective region ID changed"
        Require ($members.Count -eq $expectedMembers.Count) "effective membership count changed"
        for ($memberIndex = 0; $memberIndex -lt $members.Count; $memberIndex++) {
            Require ([string]$members[$memberIndex] -ceq [string]$expectedMembers[$memberIndex]) "effective membership changed"
            Require ($panelMemberIds.Add([string]$members[$memberIndex])) "raw region appears in multiple effective groups"
        }
        $expectedKind = if ($members.Count -eq 1) { "identity" } else { "participant_lane" }
        Require ($expected.assembly_kind -ceq $expectedKind) "effective assembly kind changed"
        Require ($region.CoordinateSpace -ceq [string]$expected.coordinate_space) "effective coordinate space changed"
        Assert-BoundsEqual $region.Polygon.Bounds $expected.panel_polygon.bounds "effective bounds"
        Require (
            $effectiveKeySet.Add("$panelId`n$($region.RegionId)")
        ) "panel/effective region identity is duplicated"
        if ($members.Count -gt 1) {
            $assembledGroupCount++
        }
        $effectiveRegions.Add($region)
    }
    Require ($panelMemberIds.Count -eq $rawRegions.Count) "effective groups do not partition raw regions"
    foreach ($raw in @($panel.raw_detector_regions)) {
        Require ($panelMemberIds.Contains([string]$raw.region_id)) "effective groups omit a raw region"
    }

    $enrichArguments = [object[]]::new(3)
    $enrichArguments[0] = $effectiveRegions
    $enrichArguments[1] = $plotBounds
    $enrichArguments[2] = $pipelineOptions
    $enriched = @($enrichMethod.Invoke($null, $enrichArguments))
    Require ($enriched.Count -eq $groups.Count) "geometry enrichment changed region count"
    $enrichedById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($region in $enriched) {
        $enrichedById.Add([string]$region.RegionId, $region)
    }

    $recognized = @($panel.recognized_regions)
    Require ($recognized.Count -eq $groups.Count) "recognized/effective region partition changed"
    $panelRecognizedIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($saved in $recognized) {
        $regionId = [string]$saved.region_id
        Require ($panelRecognizedIds.Add($regionId)) "recognized region ID is duplicated within a panel"
        Require ($enrichedById.ContainsKey($regionId)) "recognized region is absent from effective regions"
        $region = $enrichedById[$regionId]
        Assert-BoundsEqual $region.Polygon.Bounds $saved.panel_polygon.bounds "recognized geometry"
        Require ($saved.coordinate_space -ceq $region.CoordinateSpace) "recognized coordinate space changed"
        $classification = $classifyMethod.Invoke(
            $null,
            [object[]]@($region, [string]$saved.text, $plotBounds))
        $replayedRole = Convert-RuntimeRoleName $classification.Role
        $recordedRole = ([string]$saved.role).ToLowerInvariant()
        Require (
            @("ytick", "xtick", "axistitle", "phaseheading", "legendtext", "participant", "annotation", "other") -ccontains $recordedRole
        ) "saved role serialization changed"
        $reasons = @($classification.Reasons | ForEach-Object { [string]$_ })
        $regionRows.Add([ordered]@{
            split = [string]$panel.split
            panel_id = [string]$panel.panel_id
            region_id = $regionId
            text = [string]$saved.text
            recorded_role = $recordedRole
            replayed_role = $replayedRole
            changed = $recordedRole -cne $replayedRole
            replayed_confidence = [double]$classification.Confidence
            replayed_reasons = $reasons
            panel_bounds_ltrb = @(
                ([double]$region.Polygon.Bounds.Left),
                ([double]$region.Polygon.Bounds.Top),
                ([double]$region.Polygon.Bounds.Right),
                ([double]$region.Polygon.Bounds.Bottom))
        })
    }
    Require ($panelRecognizedIds.Count -eq $enrichedById.Count) "recognized IDs do not cover effective regions"
    foreach ($regionId in $enrichedById.Keys) {
        Require ($panelRecognizedIds.Contains($regionId)) "effective region lacks recognized text"
    }

    $allRawCount += $rawRegions.Count
    $allEffectiveCount += $groups.Count
    $allRecognizedCount += $recognized.Count
}

Require ($allRawCount -eq 831) "full raw-region denominator changed"
Require ($allEffectiveCount -eq 823) "full effective-region denominator changed"
Require ($allRecognizedCount -eq 823) "full recognized-region denominator changed"
Require ($rawKeySet.Count -eq 831) "full panel/raw-region identity inventory changed"
Require ($effectiveKeySet.Count -eq 823) "full panel/effective-region identity inventory changed"
Require ($regionRows.Count -eq 823) "full replay denominator changed"

$changedRows = @($regionRows | Where-Object { $_.changed })
if ($ExpectedClassifierVersion -ceq "graph-text-role-classifier-v2") {
    Require ($changedRows.Count -eq 0) "v2 replay does not reproduce all recorded roles"
}

$recordedCounts = Get-SortedCountMap @($regionRows | ForEach-Object { $_.recorded_role })
$replayedCounts = Get-SortedCountMap @($regionRows | ForEach-Object { $_.replayed_role })
$transitionCounts = Get-SortedCountMap @($regionRows | ForEach-Object {
    "$($_.recorded_role)->$($_.replayed_role)"
})
$splitReplay = [ordered]@{}
foreach ($split in @("train", "validation")) {
    $splitRows = @($regionRows | Where-Object { $_.split -ceq $split })
    $splitChanged = @($splitRows | Where-Object { $_.changed })
    $splitReplay[$split] = [ordered]@{
        regions = $splitRows.Count
        recorded_role_counts = Get-SortedCountMap @($splitRows | ForEach-Object { $_.recorded_role })
        replayed_role_counts = Get-SortedCountMap @($splitRows | ForEach-Object { $_.replayed_role })
        transition_counts = Get-SortedCountMap @($splitRows | ForEach-Object {
            "$($_.recorded_role)->$($_.replayed_role)"
        })
        unchanged = $splitRows.Count - $splitChanged.Count
        changed = $splitChanged.Count
    }
}
$affectedPanels = @($changedRows | ForEach-Object { $_.panel_id } | Sort-Object -Unique)

Require ((Get-Sha256 $scriptPath) -ceq $scriptSha256) "replay script changed during execution"
Require ((Get-Sha256 $classifierPath) -ceq $ClassifierSourceSha256) "classifier source changed during execution"
Require ((Get-Sha256 $ocrPath) -ceq $OcrAssemblySha256) "OCR assembly changed during execution"
Require ((Get-Sha256 $inferencePath) -ceq $InferenceAssemblySha256) "inference assembly changed during execution"
Require ((Get-Sha256 $evaluationFile) -ceq $EvaluationSha256) "cached evaluation changed during execution"

$output = if ([IO.Path]::IsPathRooted($OutputPath)) {
    [IO.Path]::GetFullPath($OutputPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputPath))
}
$artifactPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
Require ($output.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) "output path is outside artifacts"
Require (-not (Test-Path -LiteralPath $output)) "output path already exists"
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))

$started.Stop()
$report = [ordered]@{
    schema = "graphreader.cached-ocr-role-classifier-replay.v1"
    status = "cached_role_replay_complete"
    scope = "project-owned-synthetic-train-development-role-only-diagnostic"
    synthetic_only = $true
    private_reads = 0
    sealed_reads = 0
    truth_rows_read = 0
    model_inference_runs = 0
    optimizer_steps = 0
    metrics_gate = $false
    production_approved = $false
    classifier_version = $runtimeClassifierVersion
    old_v2_recorded_role_identity_enforced = $ExpectedClassifierVersion -ceq "graph-text-role-classifier-v2"
    inputs = [ordered]@{
        evaluation = [ordered]@{ path = [IO.Path]::GetRelativePath($repoRoot, $evaluationFile).Replace('\', '/'); sha256 = $EvaluationSha256 }
        candidate = [ordered]@{ path = [IO.Path]::GetRelativePath($repoRoot, $candidatePath).Replace('\', '/'); sha256 = [string]$evaluation.candidate.sha256 }
        recognizer_manifest = [ordered]@{ path = [IO.Path]::GetRelativePath($repoRoot, $recognizerManifestPath).Replace('\', '/'); sha256 = [string]$candidate.recognizer.manifest_sha256 }
        classifier_source = [ordered]@{ path = [IO.Path]::GetRelativePath($repoRoot, $classifierPath).Replace('\', '/'); sha256 = $ClassifierSourceSha256 }
        replay_script = [ordered]@{ path = [IO.Path]::GetRelativePath($repoRoot, $scriptPath).Replace('\', '/'); sha256 = $scriptSha256 }
        ocr_assembly = [ordered]@{ path = [IO.Path]::GetRelativePath($repoRoot, $ocrPath).Replace('\', '/'); sha256 = $OcrAssemblySha256; mvid = $ocrAssembly.ManifestModule.ModuleVersionId.ToString("D") }
        inference_assembly = [ordered]@{ path = [IO.Path]::GetRelativePath($repoRoot, $inferencePath).Replace('\', '/'); sha256 = $InferenceAssemblySha256; mvid = $inferenceAssembly.ManifestModule.ModuleVersionId.ToString("D") }
    }
    candidate_pipeline_options = [ordered]@{
        stage_version = [string]$pipelineOptions.StageVersion
        crop_width = [int]$pipelineOptions.CropWidth
        crop_height = [int]$pipelineOptions.CropHeight
        crop_width_mode = $pipelineOptions.CropWidthMode.ToString()
        maximum_crop_width = [int]$pipelineOptions.MaximumCropWidth
        infer_vertical_orientation_for_tall_regions = [bool]$pipelineOptions.InferVerticalOrientationForTallRegions
        enable_participant_lane_assembly = [bool]$pipelineOptions.EnableParticipantLaneAssembly
    }
    authenticated_population = [ordered]@{
        panels = 37
        train_panels = 28
        development_panels = 9
        raw_regions = $allRawCount
        effective_regions = $allEffectiveCount
        recognized_regions = $allRecognizedCount
        assembled_groups = $assembledGroupCount
        recognition_failures = 0
    }
    role_replay = [ordered]@{
        recorded_role_counts = $recordedCounts
        replayed_role_counts = $replayedCounts
        transition_counts = $transitionCounts
        unchanged = $regionRows.Count - $changedRows.Count
        changed = $changedRows.Count
        affected_panel_ids = $affectedPanels
        by_split = $splitReplay
    }
    guarantees = @(
        "Cached text and geometry were inputs and were not changed.",
        "Raw regions were reassembled by the loaded public ParticipantLaneTextRegionAssembler and matched exact cached IDs, ordered memberships, and bounds.",
        "The loaded private OcrPipeline.EnrichGeometry method was invoked by reflection with authenticated candidate-derived options.",
        "No image, truth row, model weight, inference session, threshold, or optimizer was read or executed."
    )
    elapsed_milliseconds = [Math]::Round($started.Elapsed.TotalMilliseconds, 3)
    regions = @($regionRows)
}

$json = $report | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($output, $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Output ($report | Select-Object schema, status, classifier_version, @{ Name = "changed"; Expression = { $_.role_replay.changed } })
