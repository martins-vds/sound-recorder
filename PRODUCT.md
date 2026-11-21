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

The user requested valuable missing capabilities, clearer hierarchy, and fewer visible controls. Inline transcript review and library search are proposed concepts, not implemented product capabilities. No cloud, AI summary, speaker identification, audio playback, or real capture is implied by the prototype.

## Product Principles

- Make meeting capture and transcript review the primary tasks.
- Preserve existing capabilities through progressive disclosure.
- Keep recording state and safe stopping visible.
- Clearly distinguish simulated data and conceptual features from production behavior.

## Evidence on Hand

Existing implementation and copy: `src/SoundRecorder.Desktop/MainWindow.xaml`, `src/SoundRecorder.Desktop/MainViewModel.cs`, `src/SoundRecorder.Core/ProductCopy.cs`, and `README.md`.

## Accessibility & Inclusion

Preserve keyboard operation, labelled controls, visible focus, live capture feedback, and desktop resizing. Browser checks do not establish native WPF or screen-reader compatibility.
