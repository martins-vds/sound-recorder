# Sound Recorder

Offline Windows 11 x64 desktop recorder for microphone and system audio, with local Whisper transcription.

## Native meeting workspace

The WPF workspace follows approved prototype **B**: a searchable meeting library on the left, transcript review on the right, and persistent capture controls below. Search covers saved titles and retained transcript text. Capture setup and permission remain independent of the selected meeting.

Playback opens the selected real WAV only when requested, supports pause/seek/volume, and pauses when leaving review. Completed TXT/SRT remain available after job-history or audio-only deletion; another attempt's recognition progress does not replace the completed transcript.

**Downloaded models** contains only complete, locally verified artifacts. **Download other models** offers all 12 Whisper.net 1.9.1 model families and six encodings: 72 choices. Downloads use pinned-revision metadata and SHA-256 verification, show real transferred-byte progress or an indeterminate state, and expose verification separately. Progress remains in the header after leaving settings. Import/download registration never changes selection; selecting a model is explicit. English-only choices stay listed but are not silently substituted for Brazilian Portuguese transcription.

Each new job snapshots its model and language at enqueue. **Stop** interrupts a running attempt; **Cancel** also handles queued work. Restart creates a new attempt from the beginning and preserves earlier outputs. Pause/resume transcription is unavailable. Recognition percentages are coarse audio-position estimates, not elapsed time or whole-job completion; preparation and output writing remain indeterminate. Both final output files must be published before Completed.

**Delete recording** confirms either audio-only removal or removal of audio, all owned transcript/subtitle pairs, and associated history. Related workers and playback handles are released first. Partial deletion remains actionable and retryable rather than being reported as complete. **Delete job** removes terminal history only, retaining audio and owned outputs. Downloaded models and unrelated recordings are untouched.

The implementation is not release-certified until the strict quality, full visual matrix, and native/device acceptance gates are complete. The frozen browser reference remains on `prototype/approved-meeting-workspace-b` at `8410c7782506313510ae917d3f160a08f47e769e`; its synthetic media and simulations are not production behavior. See the implementation plan for opening its separate reference worktree.

## Approved implementation plan and specifications

Follow the [meeting workspace implementation plan](requirements/meeting-workspace-implementation-plan.md). It includes the frozen-reference worktree/preview command, approved public test seams, migration and ownership requirements, dependency-ordered vertical slices, and mandatory side-by-side prototype checks before editing, after the first runnable change, and before every UI-slice commit.

The Core and native UI Gherkin files are executable and discoverable. Completed workspace scenarios are unignored; remaining `@planned` markers are tracking metadata, not skip instructions. Undefined, skipped, or unexecuted scenarios remain incomplete evidence. Native/device release acceptance is separate from the deterministic workspace tests.

Each new transcription job retains its model and language at enqueue; later selection changes affect only new jobs. During implementation, keep [the scoped visual-reference instructions](.github/instructions/approved-meeting-workspace.instructions.md) active and record repeated comparison evidence as required by the plan.

## Local transcript analysis: production lifecycle, UNVERIFIED output

The [formal implementation baseline](requirements/local-transcript-analysis-design.md#formal-implementation-baseline---2026-11-23) is approved. The existing **Experimental local transcript analysis** controls now use the production SQLite store, durable FIFO coordinator and pinned Phi-mini/Nomic processor. This is not semantic verification or release certification.

1. Select one successful saved transcript and expand its analysis controls.
2. Open **Analysis model setup**. Reuse the approved cache containing `Phi-4-mini-instruct-Q4_K_M.gguf` and `nomic-embed-text-v2-moe.Q8_0.gguf`: enter its directory and explicitly choose **Verify and select directory**. Alternatively import the exact pinned ZIP. **Download pinned profile...** shows filenames, roles, licenses, total bytes and local destination, and requires confirmation before requesting a download. Nothing downloads or infers automatically.
3. Choose **Generate summary**, or enter a question and choose **Ask**. Questions cover the whole selected transcript unless **Local lookup only (not exhaustive)** is explicitly checked. Lookup cannot establish exhaustive coverage or absence. Optional follow-up context requires an explicit antecedent; prior generated answers are not source evidence.
4. Select a candidate to inspect original excerpts and timestamps. Source TXT/SRT hashes are rechecked; missing or changed source is an explicit error, never a replacement attempt. Every model claim remains **UNVERIFIED**, even with resolvable citations.

Readiness verifies the selected profile offline without loading a model. `SOUNDRECORDER_ANALYSIS_MODELS` supplies only an editable directory suggestion, not permission to select or infer. Generation and embedding remain separate from Whisper transcription models. The configured preflight reserves 256 MiB of disk and requires 4 GiB available memory; these conservative implementation budgets are not measured supported hardware limits or an OOM guarantee.

Completed summary/discussion history and per-transcript analysis job statuses restore locally after restart. Previously queued/running work becomes **Interrupted**, not Complete or automatically retried. Select a released unfinished job and explicitly request **Retry analysis (original pins)** for a new job, or **Resume compatible checkpoint** for its retained compatible revision. Every request pins the successful transcription attempt, language, original TXT/SRT hashes, effective model/configuration hashes and verified directory. Changing capture language or current model selection never retargets an older request.

Capture intent blocks new native inference leases immediately. Starting capture cancels analysis and waits asynchronously for its current native call to release before capture starts. Selection changes and close also cancel/drain owned work; delayed progress/results cannot publish against another selection. Cancellation is a safe-boundary request, not instantaneous native interruption. Concurrent transcription remains excluded from new Desktop analysis requests.

Private results/indexes/checkpoints are in `%LOCALAPPDATA%\SoundRecorder\analysis.sqlite`; scheduling metadata is in `analysis-jobs.sqlite`; diagnostic claim ledgers are in `analysis-diagnostics\<attempt-id>`. Full recording deletion removes only its independently owned attempts from the store, journal and diagnostics, including retained outputs whose transcription history was deleted. Audio-only and transcription-job-history deletion retain analysis. Failures stop deletion before source artifacts are removed and remain explicit; missing/corrupt existing analysis databases are not silently recreated.

If the analysis journal becomes unavailable during work, cancellation still reaches the worker and capture/close wait for safe release. The active analysis command remains busy until that release even when saving cancellation fails; model setup and new analysis remain unavailable meanwhile. The UI reports unavailable storage and potentially stale job metadata; another analysis or full recording deletion requires repairing storage and restarting. Source artifacts are not discarded to conceal that failure.

The reader previews at most 200 output portions/candidates; complete records and citations remain in the store. Processing keeps bounded context and five-/three-minute generation deadlines for summary/question stages. Full source traversal is not proof of faithful interpretation. There is no cloud fallback, model tuning, new model trial or 12-hour acceptance claim. See [PROCESSING.md](src/SoundRecorder.Infrastructure/Analysis/PROCESSING.md) for processing bounds and the queue bridge.

For a **separate** framework-dependent ARM64 development publish (requires the ARM64 .NET 10 Windows Desktop runtime):

```powershell
dotnet publish .\src\SoundRecorder.Desktop\SoundRecorder.Desktop.csproj -c Release -r win-arm64 `
    --self-contained false -o .\artifacts\manual-validation\formal-desktop
& .\artifacts\manual-validation\formal-desktop\SoundRecorder.Desktop.exe
```

Do not publish over an existing validation app in use. Publishing `win-x64` also works, but compilation is not native x64 execution or distribution acceptance. Strict full-owner CRAP/mutation gates and the prototype-B/native acceptance matrix remain required; earlier failing owner gates are not waived by this integration.

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

The feature files under `tests\SoundRecorder.Ui.Tests\Features\` cover workspace navigation/search, recording controls, playback/artifact deletion, model setup/progress, transcription jobs, analysis restoration, accessibility, and lifecycle. Each scenario launches the real `MainWindow` in `SoundRecorder.Ui.TestHost` with a unique disposable state directory. Tests run serially, use bounded waits, and terminate only their owned host process before deleting its files.

`MainWindow` loads the shared native WPF styles from `src\SoundRecorder.Desktop\Themes\RecorderTheme.xaml`, so the desktop app and isolated host use the same typography, spacing, and controls. Colors use dynamic Windows system brushes. Recording actions, status, captured duration, and feedback stay visible while setup and history scroll; model controls use the available width rather than a fixed URL field. The layout scenarios check the 760 x 560 and 980 x 720 window sizes using DPI-aware bounds and save local screenshots under the UI test output's `TestResults\Layout\` folder.

Capture, recognition, audio output, package selection, notification-area, clipboard, and Explorer effects use controlled adapters. WAV/TXT/SRT files, package/inventory validation, owned-artifact deletion, and history persistence are real. Model-download cases use an in-memory HTTP handler with tiny synthetic GGML bytes, actual staging/SHA-256 checks, and controlled transfer/verification gates. No microphone, user recording/history, physical audio output, real network download, or system clipboard is used. These tests verify UI wiring, not real-device audio integrity, native recognition, screen-reader behavior, or release acceptance thresholds.

## Quality gates

```powershell
dotnet tool restore
Push-Location .\tests\SoundRecorder.Core.Tests
try {
    dotnet stryker --config-file ..\..\stryker-config.json --project SoundRecorder.Core.csproj
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

Repeat for Core, Desktop and Analysis. Analysis is exercised by **both** Core consumer tests and migrated historical Harness tests. Select every handwritten owner source and combine the suites by line-union coverage:

```powershell
$analysisSources = Get-ChildItem .\src\SoundRecorder.Analysis -Filter *.cs |
    Select-Object -ExpandProperty FullName
$analysisFilter = "(type!=IntegrationTests&Category!=type:IntegrationTests)|purpose=NativeAdapterSmoke|purpose=PhiMiniNative"
crap4csharp @analysisSources `
    --test-project .\tests\SoundRecorder.Core.Tests\SoundRecorder.Core.Tests.csproj `
    --test-project .\feasibility\Models\Harness.Tests\Models.Harness.Tests.csproj `
    --test-filter $analysisFilter --crap-threshold 7.999999999999999 --dry-run
```

After previewing, remove `--dry-run` to execute. Omit `--framework`: each suite has one TFM, respectively `net10.0-windows` and `net10.0`, and both reports are required. Reqnroll's existing integration tags appear as `Category=type:IntegrationTests`; xUnit uses `type=IntegrationTests`. The filter honors both declarations, then includes only the five bounded native adapter cases already authorized for foundation verification. Configure `SR_APPROVED_MODEL_DIRECTORY` to the existing approved cache. These checks never download models and are not semantic model comparisons. A Harness-only model-free run is development feedback, not the full-owner gate.

Core's domain gate can use the default unit selection by omitting `--all-tests`; native adapter evidence uses the integration-inclusive selection shown above. Every executable member must have a numeric result: `N/A` is incomplete evidence even if the tool exits zero. The threshold is the largest representable value below 8, so a member scoring exactly 8 fails rather than passing the tool's inclusive boundary. CRAP does not score constructors.

Run Stryker separately for each production project using `--project SoundRecorder.Core.csproj`, `SoundRecorder.Infrastructure.csproj`, or `SoundRecorder.Desktop.csproj` from the test directory. Inspect the JSON report's unrounded detected/eligible mutant ratio: each project must exceed 98%; exit status or a rounded display alone is not proof.

Run full-owner Analysis mutation from `src\SoundRecorder.Analysis` with `dotnet stryker`. Its owner-local configuration selects both test projects, the same declared-integration filter and the five authorized cached-model native cases. The installed runner also supports repeated `--test-project` arguments from the production owner. Native checks run in one test session to avoid competing native loads and shared diagnostic-file writers. Do not build or run another validation command against those outputs while mutation is active. The historical Harness configuration remains a narrower diagnostic, not the production gate. Moving the engine does not reset earlier Infrastructure/analysis quality failures or waive native coverage. See the design's foundation checkpoints for actual scope and results.

The Desktop project keeps ordinary XAML compilation unchanged. Its WPF temporary type-inspection project disables implicit XAML re-inclusion and compiles local types even during a design-time analysis, avoiding duplicate generated sources without disabling handwritten compilation or source generators.

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

- `SoundRecorder.Core`: public recording, queue, history, and recovery state machines, plus transcript-analysis request/result/evidence and service contracts; independent of WPF, SQLite, JSON, Whisper and LLamaSharp.
- `SoundRecorder.Analysis`: in-process LLamaSharp CPU generation/embedding, bounded source processing, exact-evidence SQLite ledger and pinned profile resources; independent of WPF and Whisper.
- `SoundRecorder.Infrastructure`: NAudio/WASAPI capture, local Whisper processing, atomic TXT/SRT output, model-package validation, recovery, JSON persistence, and the Core-facing local analysis adapter.
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
