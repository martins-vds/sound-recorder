param(
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\SoundRecorder"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $root "artifacts\publish\win-x64"

dotnet publish (Join-Path $root "src\SoundRecorder.Desktop\SoundRecorder.Desktop.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory

New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null
Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $InstallDirectory -Recurse -Force
Write-Host "Sound Recorder installed at $InstallDirectory"
