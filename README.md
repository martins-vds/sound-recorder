# Sound Recorder

Offline Windows 11 x64 desktop recorder for microphone and system audio, with local Whisper transcription.

## Build

```powershell
dotnet build SoundRecorder.slnx
```

Run the desktop application:

```powershell
dotnet run --project .\src\SoundRecorder.Desktop\SoundRecorder.Desktop.csproj
```

Publish a self-contained Windows x64 build:

```powershell
.\scripts\publish.ps1
```

Optionally download an import-ready multilingual model package:

```powershell
.\scripts\publish.ps1 -DownloadModel base
```

Available values are `tiny`, `base`, `small`, `medium`, `large-v3-turbo`, and `large-v3`. The script downloads from the Whisper.net v5 model mirror, caches the model under `%LOCALAPPDATA%\SoundRecorder\model-cache`, calculates SHA-256, and writes the package under `models\` in the publish output. The app validates and activates that single bundled package on first launch. An already-active compatible local model is preserved. Later publishes reuse the cached model without downloading it again.

Install as the current standard user:

```powershell
.\scripts\install.ps1
```

## Test

Run the Reqnroll scenarios linked directly from `requirements\`:

```powershell
dotnet test .\tests\SoundRecorder.Core.Tests\SoundRecorder.Core.Tests.csproj
```

Run one scenario by its generated test name:

```powershell
dotnet test .\tests\SoundRecorder.Core.Tests\SoundRecorder.Core.Tests.csproj --filter "FullyQualifiedName~StartARecording"
```

The `@release_acceptance` scenarios are ignored until the versioned corpus and candidate engine/model exist, as described in `requirements\acceptance.feature`.

The native worker regressions require a published executable and an existing local large-model fixture. They check normal completion, explicit worker errors, cancellation acknowledgment within one second, queue advancement within five seconds, and worker termination when only its parent is forcibly stopped. A test-only console host provides that parent without touching the desktop app. These tests never download a model:

```powershell
.\scripts\publish.ps1
$env:SOUND_RECORDER_TEST_MODEL = "C:\models\ggml-large-v3-turbo.bin"
$env:SOUND_RECORDER_TEST_WORKER = (Resolve-Path .\artifacts\publish\win-x64\SoundRecorder.Desktop.exe).Path
dotnet test .\tests\SoundRecorder.Core.Tests\SoundRecorder.Core.Tests.csproj --arch x64 --filter "FullyQualifiedName~WhisperCancellationTests"
```

Without those two environment variables the native integration tests are explicitly skipped. They are tagged `type=IntegrationTests`, separate from deterministic unit tests and release acceptance.

## Quality gates

```powershell
dotnet tool restore
Push-Location .\tests\SoundRecorder.Core.Tests
try {
    dotnet tool run dotnet-stryker --config-file ..\..\stryker-config.json
} finally {
    Pop-Location
}
```

The Stryker configuration requires a mutation score of at least 99, enforcing a score above 98%.

CRAP analysis uses [microsoft/crap4csharp](https://github.com/microsoft/crap4csharp). From that tool's checkout, run:

```powershell
dotnet run --project <crap4csharp>\src\Crap4CSharp -c Release -- .\src\SoundRecorder.Core
```

## Diagnostics

The desktop app uses `ILogger` with compile-time `LoggerMessage` generation and a local JSONL provider. Each line contains timestamp, process/thread identity, category, level, event ID/name, structured properties, and exception diagnostics.

Logs are written to:

```text
%LOCALAPPDATA%\SoundRecorder\logs\sound-recorder-YYYYMMDD.jsonl
```

The app records startup readiness, capture lifecycle, recovery, queue transitions, model activation, transcript output, and unhandled failures. Logs do not include recording or transcript content.

## Architecture

- `SoundRecorder.Core`: public recording, queue, history, and recovery state machines without Windows or third-party dependencies.
- `SoundRecorder.Infrastructure`: NAudio/WASAPI capture, local Whisper processing, atomic TXT/SRT output, model-package validation, recovery, and JSON persistence.
- `SoundRecorder.Desktop`: MVVM-based WPF UI using CommunityToolkit.Mvvm. Async commands own recording/model/queue operations; code-behind remains limited to dialogs, notification-area behavior, and Windows lifecycle integration.
- `requirements\`: canonical Reqnroll specifications linked into the test project rather than copied.

Transcription runs in a headless child instance of the same executable (`--transcribe-worker`), without opening a window, loading application history, or starting another queue. Before sending audio work, the parent assigns that process to an anonymous Windows Job Object with a non-inherited kill-on-close handle. Windows therefore stops the worker if its parent crashes or is forcibly terminated; failure to establish that ownership aborts the job rather than running an unowned worker. Cancel still stops only that job's child process and waits for it to exit before releasing the worker. The parent removes its temporary processing audio after normal completion or cancellation; abrupt termination cannot run that file cleanup. This isolates native Whisper failures from recording and keeps cancellation bounded even when native inference cannot abort promptly. Only the parent queue publishes final TXT/SRT output.

Audio uses NAudio's modern WASAPI recorder with event synchronization, a dedicated MMCSS audio thread, and fixed 48 kHz capture formats. WASAPI packets marked silent are written as silence, invalid IEEE-float samples are replaced with silence, and sustained invalid device output stops the recording as interrupted instead of saving loud corrupted noise. Audio is flushed during capture for recovery; any silence padding uses whole audio frames so later packets remain sample-aligned. Empty WASAPI callbacks do not reset the live level meters; actual silent packets do. Normal capture finalizes to a 48 kHz 16-bit PCM WAV, mixing microphone audio into both stereo channels for combined capture. Transcription temporarily creates a 16 kHz mono processing copy for Whisper without changing the saved WAV. Transcription is FIFO with one worker, and output pairs are promoted only after both files complete. Model activation occurs after manifest compatibility and SHA-256 verification, while the previous working model remains available.

## Model package

Model installation is always explicit. A package is a ZIP containing `manifest.json` and its named model file:

```json
{
  "FormatVersion": 1,
  "Id": "candidate-model",
  "Engine": "Whisper.net 1.9",
  "ModelFile": "ggml-model.bin",
  "Sha256": "64-hex-character SHA-256 of the model file",
  "Platform": "Windows11-x64",
  "Languages": ["English", "Brazilian Portuguese"]
}
```

An explicitly requested download sends no recording audio or transcript content.
