# Sound Recorder Repository Instructions

## Repository purpose

This repository implements the version-one product contract in C#/.NET. Treat `GLOSSARY.md` and the files under `tests\SoundRecorder.Core.Tests\Features\` as the behavioral source of truth; maintain a single canonical copy of each feature file.

## Build and validation

- Build: `dotnet build SoundRecorder.slnx`
- Run the app: `dotnet run --project .\src\SoundRecorder.Desktop\SoundRecorder.Desktop.csproj`
- Run Reqnroll scenarios: `dotnet test .\tests\SoundRecorder.Core.Tests\SoundRecorder.Core.Tests.csproj`
- Run FlaUI UI scenarios on an unlocked, otherwise idle interactive desktop: `dotnet test .\tests\SoundRecorder.Ui.Tests\SoundRecorder.Ui.Tests.csproj`
- Run one scenario: add `--filter "FullyQualifiedName~StartARecording"`
- Run mutation from `tests\SoundRecorder.Core.Tests`: `dotnet tool run dotnet-stryker --config-file ..\..\stryker-config.json`
- `@release_acceptance` scenarios stay `@ignore` until the versioned corpus and final candidate engine/model exist.

## Implementation and testing

- Use C# for application code and automated test implementations, with Reqnroll for executable Gherkin specifications.
- Before implementing or changing behavior, editing tests or requirements, or running quality analysis, read [Testing Instructions](instructions/testing.instructions.md). That file owns the test-driven workflow and the mandatory CRAP and mutation-score gates.
- Use `ILogger` for operational events and source-generated `[LoggerMessage]` methods for application logging. Keep recording/transcript content and paths containing user titles out of log messages.

## Requirements architecture

- `tests\SoundRecorder.Core.Tests\Features\capture.feature` owns the recording session contract: source/device selection, permission, levels, pause/resume, WAV format, naming, duration limits, and wake behavior.
- `tests\SoundRecorder.Core.Tests\Features\transcription.feature` owns saved-audio processing: persistent FIFO jobs, one local worker, explicit cancellation/retry, TXT/SRT output, and restart semantics.
- `tests\SoundRecorder.Core.Tests\Features\privacy-and-setup.feature` owns the offline Windows installation boundary, model lifecycle, accessibility, local-data policy, and network restrictions.
- `tests\SoundRecorder.Core.Tests\Features\reliability.feature` owns failure honesty and recovery: prerequisites, interrupted/recovered states, storage failures, process/power interruption, minimization, and exit.
- `tests\SoundRecorder.Core.Tests\Features\acceptance.feature` defines measurable release evidence and reference-machine thresholds. Keep benchmark/model selection explicitly separate from claims that acceptance has already been met.
- `tests\SoundRecorder.Ui.Tests\Features\` owns UI interaction specifications exercised through FlaUI against the real window with isolated test adapters; it does not replace the product contract or release evidence.

`SoundRecorder.Core` owns public state machines and remains independent of WPF, NAudio, Whisper, JSON, and Windows APIs. `SoundRecorder.Infrastructure` supplies those adapters. `SoundRecorder.Desktop` uses `MainViewModel` plus CommunityToolkit.Mvvm commands for asynchronous orchestration; code-behind is limited to dialogs, tray, and Windows lifecycle integration. Reqnroll discovers the feature files directly within the test project's `Features` folder.

Read the neighboring feature files when changing behavior: normal save/auto-queue connects capture, transcription, and reliability; model integrity and offline operation connect setup, transcription, and acceptance.

## Domain language and requirement conventions

- Use the exact terms in `GLOSSARY.md`. Distinguish a **recording** from a **transcript** and **transcription job**; distinguish **captured duration** from wall-clock time; and distinguish **complete**, **interrupted**, and **recovered** recordings.
- Preserve the selected-audio timeline: timestamps are relative to saved audio, with paused time absent. System audio is all sound from the selected playback device, not call-only audio.
- Express uncertainty and failure states honestly. A fallback device, destination, cloud service, automatic retry, or recovery success must never be implied unless the scenario explicitly permits it.
- Preserve user intent boundaries. Recording permission, recovery transcription, retries, model download/update, cancellation, and shortened recording despite low space are explicit actions.
- Keep offline and privacy invariants intact: recording and transcription are local, no account or cloud fallback is required, and unsolicited network/telemetry/update activity is out of scope.
- Keep accepted version-one scope concrete: Windows 11 x64, English and Brazilian Portuguese, 48 kHz 16-bit PCM WAV, one active recording, and one running transcription job. Capture continues to work even when transcription is active, with capture integrity taking priority.

## Editing feature specifications

- Keep each scenario as observable `Given`/`When`/`Then` behavior with measurable or stateful outcomes.
- Put shared feature-specific context in `Background` only when every scenario in that feature uses it; otherwise keep preconditions local to the scenario.
- Use `Scenario Outline` and `Examples` for meaningful permutations such as audio source, interruption phase, language, or prerequisite.
- Add acceptance measurements as reproducible evidence requirements, including corpus/model/application identity and normalization or machine conditions needed to reproduce the result.
- When a requirement changes, update `GLOSSARY.md` in the same change if terminology, state names, or avoided meanings change.
- Exercise `RecorderApplication`, `TranscriptionQueue`, and `RecoveryManager` through public seams; Windows, device, clock, and model boundaries are replaceable adapters in tests.
- Final transcript output is an atomic TXT/SRT pair. Mark a job successful only after both unique destination files exist.
- Model import validates compatibility and SHA-256 in staging and activates only afterward, preserving the previous working model on failure.
