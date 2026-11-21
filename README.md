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

Run the Reqnroll scenarios in `tests\SoundRecorder.Core.Tests\Features\`:

The workspace settings configure the official Cucumber VS Code extension to discover both Core and UI C# bindings. Release acceptance steps remain undefined until their benchmark fixtures and bindings are implemented.

```powershell
dotnet test .\tests\SoundRecorder.Core.Tests\SoundRecorder.Core.Tests.csproj
```

Run one scenario by its generated test name:

```powershell
dotnet test .\tests\SoundRecorder.Core.Tests\SoundRecorder.Core.Tests.csproj --filter "FullyQualifiedName~StartARecording"
```

The `@release_acceptance` scenarios are ignored until the versioned corpus and candidate engine/model exist, as described in `tests\SoundRecorder.Core.Tests\Features\acceptance.feature`.

The native worker regressions require a published executable and an existing local large-model fixture. They check normal completion, explicit worker errors, cancellation acknowledgment within one second, queue advancement within five seconds, and worker termination when only its parent is forcibly stopped. A test-only console host provides that parent without touching the desktop app. These tests never download a model:

```powershell
.\scripts\publish.ps1
$env:SOUND_RECORDER_TEST_MODEL = "C:\models\ggml-large-v3-turbo.bin"
$env:SOUND_RECORDER_TEST_WORKER = (Resolve-Path .\artifacts\publish\win-x64\SoundRecorder.Desktop.exe).Path
dotnet test .\tests\SoundRecorder.Core.Tests\SoundRecorder.Core.Tests.csproj --arch x64 --filter "FullyQualifiedName~WhisperCancellationTests"
```

Without those two environment variables the native integration tests are explicitly skipped. They are tagged `type=IntegrationTests`, separate from deterministic unit tests and release acceptance.

### Desktop UI scenarios

Run the FlaUI/UIA3 scenarios on an unlocked Windows interactive desktop. Do not use the keyboard or run another UI automation suite concurrently; the keyboard scenario briefly moves focus to its owned test window.

```powershell
dotnet test .\tests\SoundRecorder.Ui.Tests\SoundRecorder.Ui.Tests.csproj
```

The four feature files under `tests\SoundRecorder.Ui.Tests\Features\` cover recording controls and validation, model setup and errors, history and transcription jobs, accessible labels/live regions, keyboard activation, minimization/restoration, and explicit close choices. Each scenario launches the real `MainWindow` in `SoundRecorder.Ui.TestHost` with a unique disposable state directory. Tests run serially, use bounded waits, and terminate only their owned host process before deleting its files.

Capture, recognition, package selection, notification-area, clipboard, and Explorer effects use test adapters. Model package compatibility/SHA-256 validation and local history/job/transcript persistence use the real adapters. A small fixture window raises the notification-area restore event without touching the real system tray. No microphone, user recording/history, native transcription model, download, or system clipboard is used. These tests verify UI wiring, not real-device audio integrity, native recognition, screen-reader behavior, or release acceptance thresholds.

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

CRAP analysis uses the [martins-vds/crap4csharp fork](https://github.com/martins-vds/crap4csharp). Install its release package or build it from that fork. Analyze every handwritten C# file for one production project at a time:

```powershell
$sources = Get-ChildItem .\src\SoundRecorder.Infrastructure -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-Object -ExpandProperty FullName
crap4csharp @sources --test-project .\tests\SoundRecorder.Core.Tests\SoundRecorder.Core.Tests.csproj `
    --framework net10.0-windows --all-tests --crap-threshold 7.999999999999999
```

Repeat for Core and Desktop. Core's domain gate can use the default unit selection by omitting `--all-tests`; native adapter evidence uses the integration-inclusive selection shown above. Every executable member must have a numeric result: `N/A` is incomplete evidence even if the tool exits zero. The threshold is the largest representable value below 8, so a member scoring exactly 8 fails rather than passing the tool's inclusive boundary. CRAP does not score constructors.

Run Stryker separately for each production project using `--project SoundRecorder.Core.csproj`, `SoundRecorder.Infrastructure.csproj`, or `SoundRecorder.Desktop.csproj` from the test directory. Inspect the JSON report's unrounded detected/eligible mutant ratio: each project must exceed 98%; exit status or a rounded display alone is not proof.

For filesystem mutation runs, point `TEMP` and `TMP` at a dedicated disposable directory for that invocation. Mutated path construction must not expose the normal temporary directory or unrelated files to cleanup.

Real WASAPI checks are enabled with `SOUND_RECORDER_TEST_AUDIO=1`. Native Whisper adapter checks use `SOUND_RECORDER_TEST_NATIVE_MODEL` pointing to an existing local tiny GGML model. The checked-in speech fixture was generated offline with Windows SAPI and contains no user recording; these adapter tests do not establish release accuracy.

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
- `tests\SoundRecorder.Core.Tests\Features\`: canonical Reqnroll specifications discovered directly within the test project.

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
