@ui @type:IntegrationTests @meeting_workspace
Feature: Use recording history and transcription jobs through the desktop UI
  Saved recordings and jobs are local fixtures.
  Transcription uses a deterministic worker rather than a native model.

  Scenario: Explain an empty library without presenting selection-dependent actions
    Given the isolated recorder UI is open with "no model" fixtures
    Then the library explains that no recordings are available
    And playback, transcript viewing, and job actions require an existing entry
    When the user opens the transcription jobs view
    Then the UI explains that no transcription jobs exist
    And recording setup remains available

  Scenario: Report an externally missing recording without recreating it
    Given the isolated recorder UI is open with "missing recording" fixtures
    Then the recording history has 1 row
    When the user selects the first recording
    And the user presses "Open output folder"
    Then the UI message contains "The recording file is missing"

  Scenario: View the selected successful transcript
    Given the isolated recorder UI is open with "saved transcript" fixtures
    When the user opens the transcription jobs view
    Then the job list contains status "Completed"
    When the user selects the first transcription job
    And the user presses "View transcript"
    Then the transcript reader contains "UI fixture transcript."
    Then the recording status reads "Idle"

  Scenario: Cancel the selected running transcription while keeping capture available
    Given the isolated recorder UI is open with "running job" fixtures
    When the user opens the transcription jobs view
    Then the job list contains status "Running"
    When the user selects the first transcription job
    And the user cancels that transcription job
    Then the job list contains status "Cancelled"
    When the user confirms recording permission
    Then the "Start recording" button is enabled

  Scenario: Explicitly retry a saved recording
    Given the isolated recorder UI is open with "saved transcript" fixtures
    When the user selects the first recording
    And the user selects spoken language "Brazilian Portuguese"
    And the user presses "Transcribe again"
    And the user opens the transcription jobs view
    Then the transcription job list has 2 rows
    And the job list contains language "Brazilian Portuguese"

  Scenario Outline: Stop a job without promising pause or resume
    Given the isolated recorder UI is open with "controlled running job" fixtures
    When the user resizes the recorder to <width> by <height>
    When the user opens the transcription jobs view
    Then Pause is unavailable and its limitation is explained
    When the user stops the running job
    Then the job shows Stopping until its worker is released
    And Restart and Delete job are unavailable while release is pending
    When the worker is released
    Then the job shows Stopped
    And restarting creates a new attempt from the beginning
    And previous completed outputs remain available

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario Outline: Show coarse recognition progress and preserve phase distinctions
    Given the isolated recorder UI is open with "controlled running job" fixtures
    When the user resizes the recorder to <width> by <height>
    When the user opens the transcription jobs view
    And the worker reports recognition percentages 0, 37, and 84
    Then the job view shows those recognition estimates for that job
    And no elapsed-time estimate is substituted
    When recognition ends and output writing starts
    Then the job shows indeterminate writing progress rather than Completed
    When both final output files are published
    Then the job shows Completed

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario Outline: Show indeterminate progress when the worker has no reporter
    Given the isolated recorder UI is open with "worker without progress" fixtures
    When the user opens the transcription jobs view
    And the job enters "<phase>" without a percentage
    Then job progress is indeterminate and the current phase is identified
    And no fabricated percentage or completion is shown

    Examples:
      | phase       |
      | Preparing   |
      | Recognizing |
      | Writing     |

  Scenario: Copy a completed transcript through an explicit user action
    Given the isolated recorder UI is open with "saved transcript" fixtures
    When the user views the completed transcript and requests Copy
    Then the clipboard adapter receives "UI fixture transcript."
    And copying does not change recording or job state

  Scenario: Delete only terminal job history and retain the selected transcript
    Given the isolated recorder UI is open with "saved transcript" fixtures
    When the user opens the transcription jobs view and deletes a completed job
    Then the confirmation says audio and completed outputs will be kept
    When the user confirms job-history deletion
    Then only that job-history entry disappears
    And the recording and its completed transcript remain available in the library

  Scenario: Keep queued job choices visible when new-job settings change
    Given the isolated recorder UI is open with "queued model-pinned job" fixtures
    And that queued job uses Base original and English
    When the user selects Small Q5_1 and Brazilian Portuguese for new jobs
    Then the queued job's details still show Base original and English
    And only new jobs use Small Q5_1 and Brazilian Portuguese

  Scenario: Finish capture but defer new transcription while model selection is pending
    Given the isolated recorder UI is open with "queued model-pinned job" fixtures
    And model selection verification is held by the fixture
    When the user selects Small Q5_1 while verification is pending
    Then new transcription actions are unavailable until selection completes
    And recording remains available during selection
    When the user records and saves audio during pending selection
    Then the audio is saved without queuing against the previous model
    When model selection verification is released
    Then the saved recording is queued with Small Q5_1
