# Conversation Recording

The language of capturing conversations, producing local transcripts, and discussing their evidence.

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

**Transcript excerpt**:
An identifiable portion of one transcript, with a timestamp when available or a stable reference otherwise.
_Avoid_: Recording excerpt, unattributed quote

**Transcript claim**:
A candidate interpretation of content in one transcript, with its source excerpt references and stated qualifications. It is not an independently verified real-world fact.
_Avoid_: Verified fact, transcript summary

**Transcript summary**:
An overview, topics, explicit decisions, and action items supported by evidence from one transcript.
_Avoid_: Meeting minutes, inferred decisions

**Transcript discussion**:
A question-and-answer conversation grounded in one selected transcript.
_Avoid_: Cross-recording discussion, general-purpose chat

**Transcript analysis**:
A transcript summary or transcript discussion belonging to the exact immutable transcript produced by one successful transcription job.
_Avoid_: Recording-wide analysis, analysis of the latest transcript

**Transcription model**:
The speech-recognition data required to turn recorded speech into text locally.
_Avoid_: Cloud transcription service

**Downloaded model**:
A transcription model whose complete local files have passed compatibility and integrity checks. Different model families and encodings are distinct choices.
_Avoid_: Download in progress, selected model

**Selected model**:
The downloaded model chosen for newly requested transcription jobs. Each job retains the model and language chosen when that job was queued.
_Avoid_: Model for all existing jobs

**Recording artifacts**:
The audio recording and every completed transcript and subtitle output associated with it, including outputs from earlier transcription jobs.
_Avoid_: Output folder, downloaded models

**Library entry**:
The local identity and metadata of a recording and its retained artifacts. It can remain available for transcript review after its audio has been deleted.
_Avoid_: Audio file, transcription job

**Stopped transcription job**:
A transcription job interrupted by an explicit user request. Restarting it requests a new attempt from the beginning, not a continuation of the old attempt.
_Avoid_: Paused transcription job, stopped recording

**Recognition progress**:
An estimate of how much saved audio has been traversed during speech recognition. It is not elapsed-time progress or completion of transcript output writing.
_Avoid_: Total job progress, estimated time remaining

**Interrupted recording**:
A recording ended by a failure or system interruption rather than a normal stop or the captured-duration limit.
_Avoid_: Complete recording

**Recovered recording**:
Available audio restored after an interruption, which may omit the final moments before that interruption.
_Avoid_: Complete recording
