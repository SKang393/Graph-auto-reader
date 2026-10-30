# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 Sungwoo Kang
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$PythonArguments = @()
)

$ErrorActionPreference = 'Stop'
$python = Join-Path $PSScriptRoot 'venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
    throw "The project GPU environment is missing: $python. CPU fallback is forbidden."
}

$stateRoot = Join-Path $PSScriptRoot '.state'
$environment = @{
    TEMP = Join-Path $stateRoot 'temp'
    TMP = Join-Path $stateRoot 'temp'
    PIP_CACHE_DIR = Join-Path $stateRoot 'cache\pip'
    PIP_NO_CACHE_DIR = '1'
    LOCALAPPDATA = Join-Path $stateRoot 'localappdata'
    APPDATA = Join-Path $stateRoot 'appdata'
    TORCH_HOME = Join-Path $stateRoot 'cache\torch'
    TORCHINDUCTOR_CACHE_DIR = Join-Path $stateRoot 'cache\inductor'
    MIOPEN_USER_DB_PATH = Join-Path $stateRoot 'cache\miopen-db'
    MIOPEN_CUSTOM_CACHE_DIR = Join-Path $stateRoot 'cache\miopen'
    PYTHONDONTWRITEBYTECODE = '1'
    PYTHONNOUSERSITE = '1'
    VIRTUAL_ENV = Join-Path $PSScriptRoot 'venv'
    PATH = "$(Join-Path $PSScriptRoot 'venv\Scripts');$env:PATH"
}
$previous = @{}
$exitCode = 1
try {
    foreach ($name in $environment.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    foreach ($name in @('TEMP', 'PIP_CACHE_DIR', 'LOCALAPPDATA', 'APPDATA',
            'TORCH_HOME', 'TORCHINDUCTOR_CACHE_DIR', 'MIOPEN_USER_DB_PATH',
            'MIOPEN_CUSTOM_CACHE_DIR')) {
        New-Item -ItemType Directory -Force -Path $environment[$name] | Out-Null
    }
    if ($PythonArguments.Count -eq 0) {
        $PythonArguments = @((Join-Path $PSScriptRoot 'probe.py'))
    }
    & $python -B -u @PythonArguments
    $exitCode = $LASTEXITCODE
}
finally {
    foreach ($name in $previous.Keys) {
        if ($null -eq $previous[$name]) {
            Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        }
        else {
            [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
        }
    }
}
exit $exitCode
