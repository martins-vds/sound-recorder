param([int]$Port = 18761)

$ErrorActionPreference = 'Stop'
$prototype = Join-Path $PSScriptRoot '..\src\SoundRecorder.Desktop\Prototypes'
Write-Host "Throwaway UI prototype: http://127.0.0.1:$Port/meeting-recorder.html?variant=A"
Write-Host 'Use A, B, or C in the URL. Ctrl+C stops the local server. No real audio is accessed.'
python -m http.server $Port --bind 127.0.0.1 --directory $prototype
if ($LASTEXITCODE -ne 0) {
    throw "Prototype server exited with code $LASTEXITCODE."
}
