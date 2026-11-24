param()
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$output = Join-Path $root 'generated'
if (Test-Path $output) { throw 'generated already exists; preserve or move it before regenerating.' }
& dotnet build (Join-Path $root 'Generator\Evaluation.Generator.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Generator build failed.' }
$dll = Join-Path $root 'Generator\bin\Release\net10.0\Evaluation.Generator.dll'
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
New-Item -ItemType Directory -Path $output | Out-Null
$files = @()
foreach ($language in @('en', 'pt-BR')) {
    $seedPath = Join-Path $root "fixtures\$language.seed.json"
    $seed = Get-Content -LiteralPath $seedPath -Raw | ConvertFrom-Json
    if ($seed.Count -ne 20) { throw 'Protocol v1 requires exactly twenty seed excerpts.' }
    foreach ($size in @(
        @{ Name = 'minute'; Blocks = 1; Timed = $true },
        @{ Name = 'hour'; Blocks = 60; Timed = $true },
        @{ Name = '12hour'; Blocks = 720; Timed = $true },
        @{ Name = 'untimed'; Blocks = 1; Timed = $false }
    )) {
        $id = "sr-eval-v1-$language-$($size.Name)"
        $path = Join-Path $output "$id.jsonl"
        # Consume the native stdout pipe a line at a time; never accumulate the twelve-hour transcript.
        & dotnet $dll $seedPath $size.Blocks $size.Timed.ToString() |
            ForEach-Object -Begin { $writer = [System.IO.StreamWriter]::new($path, $false, $utf8) } -Process {
                $writer.Write($_)
                $writer.Write("`n")
            } -End { $writer.Dispose() }
        if ($LASTEXITCODE -ne 0) { throw 'Generation failed; generated directory is incomplete.' }
        $goldPath = Join-Path $output "$id.coverage.jsonl"
        $gold = [System.IO.StreamWriter]::new($goldPath, $false, $utf8)
        try {
            for ($block = 1; $block -le $size.Blocks; $block++) {
                $marker = $block.ToString('D6', [Globalization.CultureInfo]::InvariantCulture)
                $segment = 's' + ($block * 20).ToString('D6', [Globalization.CultureInfo]::InvariantCulture)
                $row = [ordered]@{
                    claimId = "batch-$marker"
                    requiredMeaning = $seed[19].Replace('{block}', $marker)
                    excerptId = $segment
                    startMs = $(if ($size.Timed) { ($block * 20 - 1) * 3000 } else { $null })
                }
                $gold.Write(($row | ConvertTo-Json -Compress))
                $gold.Write("`n")
            }
        } finally { $gold.Dispose() }
        $files += [ordered]@{
            corpusId = $id
            recordingId = "$id-recording"
            successfulTranscriptionAttemptId = "$id-attempt-001"
            language = $language
            transcriptPath = "generated\$id.jsonl"
            transcriptSha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            utf8Bytes = (Get-Item -LiteralPath $path).Length
            segments = $size.Blocks * 20
            blocks = $size.Blocks
            savedAudioTimelineMs = $(if ($size.Timed) { $size.Blocks * 60000 } else { $null })
            audioAvailable = $false
            tokenCount = $null
            tokenizerIdentity = $null
            coveragePath = "generated\$id.coverage.jsonl"
            coverageSha256 = (Get-FileHash -LiteralPath $goldPath -Algorithm SHA256).Hash.ToLowerInvariant()
            coverageBytes = (Get-Item -LiteralPath $goldPath).Length
            uniqueBatchDecisions = $size.Blocks
            seedPath = "fixtures\$language.seed.json"
            seedSha256 = (Get-FileHash -LiteralPath $seedPath -Algorithm SHA256).Hash.ToLowerInvariant()
            license = 'CC0-1.0'
        }
    }
}
$manifest = [ordered]@{
    schemaVersion = 1
    corpusVersion = 'sr-eval-v1'
    provenance = 'Original AI-assisted synthetic fixtures authored for issue #3; no recordings or third-party text.'
    normalization = 'UTF-8 without BOM; LF; compact JSONL; authored Unicode text unchanged; one final LF per record.'
    excerptIdentity = 'successfulTranscriptionAttemptId + transcriptSha256 + one-based sNNNNNN'
    tokenCountStatus = 'Unmeasured: requires exact approved tokenizer/model profile; duration and bytes are not token estimates.'
    assets = @(
        foreach ($relative in @('Generate.ps1', 'Generator\CorpusGenerator.cs', 'Generator\Program.cs',
            'queries.json', 'chunk-plan.json', 'protocol.md', 'scoring-rubric.md', 'LICENSE-corpus.md')) {
            [ordered]@{
                path = $relative
                sha256 = (Get-FileHash -LiteralPath (Join-Path $root $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    )
    files = $files
}
[System.IO.File]::WriteAllText((Join-Path $output 'corpus-manifest.json'), ($manifest | ConvertTo-Json -Depth 8).Replace("`r`n", "`n") + "`n", $utf8)
Write-Output "Generated eight transcripts and exact coverage ledgers in $output"
