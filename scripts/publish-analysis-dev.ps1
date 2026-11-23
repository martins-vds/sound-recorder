param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path $_ -PathType Container })]
    [string]$ModelDirectory,
    [ValidateSet("win-arm64", "win-x64")]
    [string]$RuntimeIdentifier = "win-arm64",
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\walking-skeleton\app")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$models = (Resolve-Path $ModelDirectory).Path
foreach ($file in @("Phi-4-mini-instruct-Q4_K_M.gguf", "nomic-embed-text-v2-moe.Q8_0.gguf")) {
    if (-not (Test-Path (Join-Path $models $file) -PathType Leaf)) {
        throw "The approved existing model cache is missing $file. This script never downloads models."
    }
}
$platform = if ($RuntimeIdentifier -eq "win-arm64") { "ARM64" } else { "x64" }
Push-Location $root
try {
    dotnet publish src\SoundRecorder.Desktop\SoundRecorder.Desktop.csproj `
        --configuration Debug --runtime $RuntimeIdentifier --self-contained false `
        "-p:RuntimeIdentifiers=$RuntimeIdentifier" "-p:PlatformTarget=$platform" `
        --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "Development publish failed: $LASTEXITCODE" }
    Set-Content (Join-Path $OutputDirectory "analysis-model-cache.txt") $models -Encoding utf8
    Write-Host "Experimental development build only; not an ARM64 shipping or installer claim."
    Write-Host "Cached model SHA-256 verification happens when analysis is explicitly requested."
    Write-Host ('Run: & "' + (Join-Path $OutputDirectory "SoundRecorder.Desktop.exe") + '"')
}
finally {
    Pop-Location
}
