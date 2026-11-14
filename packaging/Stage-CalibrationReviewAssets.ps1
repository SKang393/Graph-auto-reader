# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModelPath,
    [Parameter(Mandatory)][string]$DestinationRoot,
    [Parameter(Mandatory)][ValidateSet('LocalEvaluation', 'InternalPreview')][string]$Purpose
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$DestinationRoot = [IO.Path]::GetFullPath($DestinationRoot)
if (Test-Path -LiteralPath $DestinationRoot) { throw 'Review asset destination must be new; existing assets are never overwritten.' }
$inputs = @(
    @{ Name='en_PP-OCRv5_mobile_rec.onnx'; Path=[IO.Path]::GetFullPath($ModelPath); Hash='7839f12b644f574eaf677e92a11bd3e337f4b2f910160666073888783fece743' },
    @{ Name='recognizer.json'; Path=(Join-Path $repositoryRoot 'models/review-assistance/ocr/recognizer.json'); Hash='17d44e8e86fa60183c30531c05c02e8da463ea9632dd494cf039227e3c4b0b2a' },
    @{ Name='PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt'; Path=(Join-Path $repositoryRoot 'LICENSES/PaddlePaddle-PP-OCRv5-Models-Apache-2.0.txt'); Hash='c71d239df91726fc519c6eb72d318ec65820627232b2f796219e87dcf35d0ab4' },
    @{ Name='PaddlePaddle-PP-OCRv5-Models-Notice.txt'; Path=(Join-Path $repositoryRoot 'LICENSES/PaddlePaddle-PP-OCRv5-Models-Notice.txt'); Hash='8d81f5d0c58547cce471c24f82efe768a9d907d06764f67e90cc680c6d777729' }
)
$buffers = @{}
foreach ($item in $inputs) {
    if ((Get-Item -LiteralPath $item.Path).Length -gt 64MB) { throw 'Review asset exceeds its bounded intake.' }
    $bytes = [IO.File]::ReadAllBytes($item.Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $observed = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    if ($observed -cne $item.Hash) { throw "Pinned review asset differs: $($item.Name)" }
    $buffers.Add($item.Name, $bytes)
}
New-Item -ItemType Directory -Path $DestinationRoot | Out-Null
foreach ($item in $inputs) {
    $output = [IO.File]::Open((Join-Path $DestinationRoot $item.Name), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $output.Write($buffers[$item.Name], 0, $buffers[$item.Name].Length) }
    finally { $output.Dispose() }
}
$profile = [ordered]@{ schema='graphreader.calibration-review-assets.v1'; purpose=$Purpose; productionApproved=$false; provider='Cpu'; modelSha256=$inputs[0].Hash; manifestSha256=$inputs[1].Hash }
[IO.File]::WriteAllText((Join-Path $DestinationRoot 'review-assistance.json'), ($profile | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
Write-Output "Staged exact calibration review assets for $Purpose; production approval remains false."
