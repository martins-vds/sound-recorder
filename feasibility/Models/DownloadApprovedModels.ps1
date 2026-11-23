[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Cache,
    [ValidateSet('candidate-profile.json', 'qwen3-candidate-profile.json')][string]$ProfileFile = 'candidate-profile.json')

$ErrorActionPreference = 'Stop'
$profile = Get-Content (Join-Path $PSScriptRoot $ProfileFile) -Raw | ConvertFrom-Json
$cachePath = [IO.Path]::GetFullPath($Cache, (Get-Location).ProviderPath)
New-Item -ItemType Directory -Path $cachePath -Force | Out-Null
$drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($cachePath))
if ($drive.AvailableFreeSpace -lt $profile.downloadBytes + 1GB) {
    throw "Insufficient free space in the approved cache volume: $($drive.AvailableFreeSpace) bytes."
}

$lockPath = Join-Path $cachePath '.download.lock'
$owner = [Guid]::NewGuid().ToString('N')
$lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $marker = [Text.Encoding]::UTF8.GetBytes($owner)
    $lock.Write($marker, 0, $marker.Length)
    $lock.Flush()
    foreach ($file in $profile.files) {
        $path = Join-Path $cachePath $file.name
        if (Test-Path $path) {
            if ((Get-Item $path).Length -ne $file.bytes -or
                (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
                throw "Existing cache file differs from the approved size/SHA-256: $($file.name)"
            }
            Write-Output "Verified cached $($file.name)"
            continue
        }
        if (([Uri]$file.url).Scheme -ne 'https') {
            throw 'Only the approved pinned HTTPS files may be downloaded.'
        }
        $partial = "$path.partial"
        & curl.exe --fail --location --silent --show-error --proto '=https' --proto-redir '=https' `
            --connect-timeout 30 --speed-time 120 --speed-limit 1024 --retry 3 --retry-all-errors `
            --continue-at - --output $partial $file.url
        if ($LASTEXITCODE -ne 0) {
            throw "Approved file download failed: $($file.name), curl exit $LASTEXITCODE."
        }
        if ((Get-Item $partial).Length -ne $file.bytes -or
            (Get-FileHash $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
            throw "Downloaded file differs from the approved size/SHA-256: $($file.name)"
        }
        Move-Item $partial $path
        Write-Output "Verified $($file.name)"
    }
} finally {
    $lock.Dispose()
    if ([IO.File]::ReadAllText($lockPath) -eq $owner) {
        Remove-Item $lockPath
    }
}
