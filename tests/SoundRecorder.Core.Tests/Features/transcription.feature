@requirements @v1
Feature: Transcribe saved recordings locally
  Audio is saved before transcription begins.
  Transcription produces text in the selected spoken language, not a translation.

  Scenario Outline: Select the spoken language
    Given the user is preparing a recording
    When the user selects "<language>"
    Then the recording retains that language for its automatic transcription job

    Examples:
      | language             |
      | English              |
      | Brazilian Portuguese |

  Scenario: Automatically transcribe a normally saved recording
    Given a compatible transcription model is installed
    When a recording is saved after Stop or the four-hour limit
    Then a transcription job is queued automatically
    And the saved audio exists before the job begins
    And speech recognition runs entirely on the local computer

  Scenario: Record before installing a model
    Given no compatible transcription model is installed
    When the user saves a recording
    Then the audio is retained
    And the app clearly indicates that transcription needs a model
    And no transcription job for that recording enters the queue yet
    When the user installs a compatible model and requests transcription
    Then a job for that recording enters the queue

  Scenario: Process jobs in order while allowing another recording
    Given several transcription jobs are queued
    When the worker is available
    Then queued jobs start in first-in, first-out order
    And at most one transcription job runs at a time
    And the user can start another recording while transcription runs
    And uninterrupted audio capture takes priority over transcription speed

  Scenario: Save usable transcript outputs
    Given a transcription job completes successfully
    Then its transcript can be viewed and copied in the app
    And a UTF-8 TXT file and a UTF-8 SRT file are saved beside the WAV file
    And the TXT file includes timestamped text segments
    And the SRT file contains subtitle cues
    And the job is not marked successful until both files are saved

  Scenario: Use saved-audio time for transcript segments
    Given a recording includes a paused interval
    When its transcript is produced
    Then timestamps begin on a timeline whose origin is zero
    And timestamps refer to positions in the saved audio
    And paused time is absent from that timeline
    And cues are ordered and non-overlapping
    And each cue starts before it ends
    And no cue extends beyond the saved audio duration
    And neither word-level timing nor a particular segmentation style is promised

  Scenario: Cancel a queued job
    Given a transcription job has not started
    When the user cancels the job
    Then the job is removed from the queue
    And the WAV file and any earlier successful transcripts remain unchanged
    And no new final transcript files are produced

  Scenario: Cancel a running job
    Given a transcription job is running on the reference PC
    When the user cancels the job
    Then cancellation is acknowledged within 1 second
    And the transcription worker is released within 5 seconds
    And the next queued job can start
    And the WAV file and any earlier successful transcripts remain unchanged
    And no new final transcript files are produced by the canceled job

  Scenario: Retry without replacing earlier results
    Given a recording has already been transcribed or a prior job failed or was canceled
    When the user explicitly requests transcription again
    Then the user may select a different supported language for the new job
    And the recording's audio remains unchanged
    And a successful job saves a uniquely named TXT and SRT pair
    And all earlier successful output files remain unchanged

  Scenario: Report a failed transcription
    Given a transcription job encounters a processing or output-write failure
    Then the app marks the job as failed and displays an actionable error
    And the saved WAV file remains unchanged
    And incomplete output is not presented as a successful transcript pair
    And the job is retried only after an explicit user request

  Scenario: Restore work after a restart
    Given jobs were queued but not started before the app exited
    When the app starts again
    Then those jobs retain their queue order and are available to run
    And a job interrupted while running is marked interrupted
    And the user is offered an explicit retry of the interrupted job
    And the interrupted job is not silently restarted

  Scenario: Keep a local recording history
    Given recordings and transcription jobs exist
    When the app restarts
    Then the local recording list and job statuses remain available
    And the user can open a recording's output folder
    And the user can request transcription again
    And externally moved or deleted files are reported explicitly
    And missing files are not silently recreated
    And the app never automatically deletes recordings or transcripts

  Scenario: Keep transcription scope bounded
    Then version one does not require live transcription
    And version one does not require importing existing audio files
    And version one does not require translation or automatic language detection
    And mixed-language conversation accuracy is not guaranteed
    And version one does not require speaker identification
    And an audio source is not presented as the identity of an individual speaker
    And version one does not require transcript editing, in-app audio playback, search, or deletion
