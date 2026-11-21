# Product

<!-- impeccable:product-schema 1 -->

This record accompanies the throwaway UX prototype branch, not a production redesign decision.

## Platform

The shipped application is native Windows 11 x64 WPF. The approved prototype is a browser-based comparison, not a web migration.

## Users

The confirmed primary workflow is recording meetings and reviewing transcripts.

## Product Purpose

Capture microphone audio, system audio, or both, and transcribe recordings locally.

## Operating Context

Recordings, transcription jobs, model packages, and TXT/SRT outputs are local. A compatible model is required for transcription, not for recording.

## Capabilities and Constraints

Preserve source and device selection, English and Brazilian Portuguese, optional titles, destination choice, explicit permission confirmation, start/pause/resume/stop, model import or explicit download, history, job cancellation/retry, transcript viewing, and output-folder access.

All sound from the selected playback device may be captured, including calls, notifications, and other applications. Single-application capture and echo cancellation are not guaranteed. Recommend headphones for combined capture. Permission confirmation is not a guarantee of legal compliance.

The user requested valuable missing capabilities, clearer hierarchy, and fewer visible controls. Inline transcript review and library search are proposed concepts, not implemented product capabilities. No cloud, AI summary, speaker identification, real meeting audio, or live capture is implied by the prototype.

The model-selection prototype must list only previously downloaded models in its selector, offer the full library-supported download catalog separately, and show download progress. The pinned Whisper.net 1.9.1 downloader enumerates 12 Whisper model families and 6 encodings (72 combinations). Custom compatible packages use import; auxiliary VAD and encoder files are not transcription-model options. Downloads, verification, selection, and language compatibility are demonstrated with synthetic in-memory state, not production implementation.

Recording review offers functional playback of the existing synthetic test clip while reading an illustrative transcript. The user confirmed that audio-only deletion retains outputs and their library entry; full deletion removes associated artifacts/history. Job deletion removes only history. Stop interrupts one job, keeps existing outputs, and restarts from the beginning; Cancel handles queued/running work. Pause is unavailable in Whisper.net 1.9.1. Its recognition progress callback supports coarse estimates, not loading/preprocessing/output-writing progress; worker integration is still required. All job execution and deletion remain simulated.

## Product Principles

- Make meeting capture and transcript review the primary tasks.
- Preserve existing capabilities through progressive disclosure.
- Keep recording state and safe stopping visible.
- Clearly distinguish simulated data and conceptual features from production behavior.

## Evidence on Hand

Existing implementation and copy: `src/SoundRecorder.Desktop/MainWindow.xaml`, `src/SoundRecorder.Desktop/MainViewModel.cs`, `src/SoundRecorder.Core/ProductCopy.cs`, and `README.md`.

## Accessibility & Inclusion

Preserve keyboard operation, labelled controls, visible focus, live capture feedback, and desktop resizing. Browser checks do not establish native WPF or screen-reader compatibility.
