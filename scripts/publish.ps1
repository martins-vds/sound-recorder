param(
    [string]$Configuration = "Release",
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\publish\win-x64"),
    [ValidateSet("tiny", "base", "small", "medium", "large-v3-turbo", "large-v3")]
    [string]$DownloadModel
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\SoundRecorder.Desktop\SoundRecorder.Desktop.csproj"

dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $OutputDirectory

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

if ($DownloadModel) {
    $modelFileName = "ggml-$DownloadModel.bin"
    $modelUrl = "https://huggingface.co/sandrohanea/whisper.net/resolve/v5/classic/$modelFileName"
    $modelDirectory = Join-Path $OutputDirectory "models"
    $cacheDirectory = Join-Path $env:LOCALAPPDATA "SoundRecorder\model-cache"
    $cachedModelPath = Join-Path $cacheDirectory $modelFileName
    $stagingDirectory = Join-Path $env:TEMP "sound-recorder-model-$([guid]::NewGuid().ToString('N'))"
    $modelPath = Join-Path $stagingDirectory $modelFileName
    $manifestPath = Join-Path $stagingDirectory "manifest.json"
    $packagePath = Join-Path $modelDirectory "$($DownloadModel).zip"

    try {
        New-Item -ItemType Directory -Force -Path $stagingDirectory | Out-Null
        New-Item -ItemType Directory -Force -Path $modelDirectory | Out-Null
        if (Test-Path $cachedModelPath) {
            $cachedLength = (Get-Item $cachedModelPath).Length
            if ($cachedLength -gt 0) {
                Write-Host "Using cached $modelFileName from $cachedModelPath"
                Copy-Item $cachedModelPath $modelPath
            }
        }

        if (-not (Test-Path $modelPath)) {
            New-Item -ItemType Directory -Force -Path $cacheDirectory | Out-Null
            $downloadPath = "$cachedModelPath.download"
            try {
                Write-Host "Downloading $modelFileName from Whisper.net v5..."
                Invoke-WebRequest -Uri $modelUrl -OutFile $downloadPath
                if ((Get-Item $downloadPath).Length -eq 0) {
                    throw "Downloaded model is empty."
                }

                Move-Item $downloadPath $cachedModelPath -Force
                Copy-Item $cachedModelPath $modelPath
            }
            finally {
                if (Test-Path $downloadPath) {
                    Remove-Item $downloadPath -Force
                }
            }
        }

        $sha256 = (Get-FileHash -Path $modelPath -Algorithm SHA256).Hash
        @{
            FormatVersion = 1
            Id = "whisper-$DownloadModel"
            Engine = "Whisper.net 1.9"
            ModelFile = $modelFileName
            Sha256 = $sha256
            Platform = "Windows11-x64"
            Languages = @("English", "Brazilian Portuguese")
        } | ConvertTo-Json | Set-Content -Path $manifestPath -Encoding utf8

        Compress-Archive -Path $modelPath, $manifestPath -DestinationPath $packagePath -Force
        Write-Host "Created model package at $packagePath"
    }
    finally {
        if (Test-Path $stagingDirectory) {
            Remove-Item $stagingDirectory -Recurse -Force
        }
    }
}

Write-Host "Published Sound Recorder to $OutputDirectory"
