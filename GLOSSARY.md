# Conversation Recording

The language of capturing conversations and producing local transcripts.

## Language

**Recording**:
Audio captured during one start-to-stop session, excluding paused intervals.
_Avoid_: Transcript, transcription job

**Audio source**:
The microphone input, system audio, or combination selected for a recording.
_Avoid_: Speaker

**System audio**:
Sound played through the selected Windows playback device, potentially including calls, notifications, and other applications.
_Avoid_: Call-only audio

**Captured duration**:
The duration of audio included in a recording, excluding paused intervals.
_Avoid_: Wall-clock duration

**Transcript**:
Text representing speech in a recording's selected language, with segment timestamps relative to its saved audio.
_Avoid_: Translation, recording

**Transcription job**:
One attempt to produce a transcript from a recording in a selected language.
_Avoid_: Recording, transcript

**Transcription model**:
The speech-recognition data required to turn recorded speech into text locally.
_Avoid_: Cloud transcription service

**Interrupted recording**:
A recording ended by a failure or system interruption rather than a normal stop or the captured-duration limit.
_Avoid_: Complete recording

**Recovered recording**:
Available audio restored after an interruption, which may omit the final moments before that interruption.
_Avoid_: Complete recording
