param(
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\SoundRecorder"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $root "artifacts\publish\win-x64"

& (Join-Path $PSScriptRoot "publish.ps1") `
    -Configuration Release `
    -OutputDirectory $publishDirectory

New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $InstallDirectory -Recurse -Force
Write-Host "Sound Recorder installed at $InstallDirectory"
