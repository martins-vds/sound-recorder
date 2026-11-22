@ui @type:IntegrationTests @meeting_workspace
Feature: Listen to recordings and confirm artifact deletion in the workspace
  Playback uses disposable real WAV fixtures and a controllable playback adapter.
  The UI test host never uses user recordings or a user's output destination.

  Scenario Outline: Read while using playback at supported sizes
    Given the isolated recorder UI is open with "playable transcript" fixtures
    When the user resizes the recorder to <width> by <height>
    And the user plays the selected recording and scrolls its transcript
    Then the actual fixture WAV is the playback source
    And playback controls stay visible with at least two complete transcript text lines
    And playback position advances without starting a transcription job

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario: Preserve playback while unrelated job progress updates the UI
    Given the isolated recorder UI is open with "playable transcript and running job" fixtures
    When the user plays the selected recording
    And that job reports another recognition estimate
    Then playback continues from its existing position
    And the visible completed transcript is identified as the previous output

  Scenario: Pause playback when leaving review and reset it for another recording
    Given the isolated recorder UI is open with "two playable transcripts" fixtures
    When the user plays the selected recording and opens model management
    Then playback is paused
    When the user returns and selects a different recording
    Then playback uses the newly selected WAV from its beginning
    And it does not start automatically

  Scenario Outline: Cancel deletion through the confirmation dialog
    Given the isolated recorder UI is open with "playable transcript" fixtures
    When the user resizes the recorder to <width> by <height>
    When the user opens Delete recording
    Then the dialog offers audio-only and all-artifacts scopes and explains their consequences
    And the default scope is audio only
    When the user dismisses the dialog with Escape
    Then audio, transcript, subtitles, library entry, and job history remain unchanged
    And keyboard focus returns to the recording's review

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario Outline: Delete only audio and continue reviewing retained outputs
    Given the isolated recorder UI is open with "playable transcript" fixtures
    When the user resizes the recorder to <width> by <height>
    When the user confirms audio-only deletion
    Then the library entry remains selected and explains that audio was deleted
    And its transcript and subtitle outputs remain viewable
    And playback and Transcribe again are unavailable
    And Open output folder remains available for retained outputs

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario: Delete all artifacts and move to a remaining recording
    Given the isolated recorder UI is open with "multiple transcript attempts" fixtures
    When the user confirms deletion of all artifacts for the selected recording
    Then that recording and its job history disappear from the workspace
    And a remaining recording is selected or the empty-library state is shown
    And downloaded models and other recordings remain unchanged

  Scenario: Delete retained artifacts after audio was already removed
    Given the isolated recorder UI is open with "audio deleted but transcripts retained" fixtures
    When the user opens deletion for the selected entry
    Then audio-only deletion is unavailable
    And all-artifacts deletion names the retained outputs and history
    When the user confirms all-artifacts deletion
    Then the retained outputs and the entry are removed

  Scenario: Keep an actionable entry after partial deletion failure
    Given the isolated recorder UI is open with "artifact deletion failure" fixtures
    When the user confirms all-artifacts deletion
    Then the UI reports the remaining failed artifact and a retry action
    And complete deletion is not announced
    And the unfinished library entry remains available

  Scenario: Explain missing or corrupt audio without replacing it
    Given the isolated recorder UI is open with "unusable audio and retained transcript" fixtures
    When the user requests playback
    Then an actionable audio error is shown
    And retained transcript review remains available
    And no alternative audio or synthetic playback source is substituted
