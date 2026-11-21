@ui @type:IntegrationTests
Feature: Use recording history and transcription jobs through the desktop UI
  Saved recordings and jobs are local fixtures.
  Transcription uses a deterministic worker rather than a native model.

  Scenario: Explain actions that need a selected recording or job
    Given the isolated recorder UI is open with "no model" fixtures
    When the user presses "Transcribe again"
    Then the UI message contains "Select a recording or job"
    When the user opens the "Transcription jobs" tab
    And the user presses "Cancel selected job"
    Then the UI message contains "Select a transcription job"
    When the user presses "View transcript"
    Then the UI message contains "Select a successful transcription job"

  Scenario: Report an externally missing recording without recreating it
    Given the isolated recorder UI is open with "missing recording" fixtures
    Then the recording history has 1 row
    When the user selects the first recording
    And the user presses "Open output folder"
    Then the UI message contains "The recording file is missing"

  Scenario: View the selected successful transcript
    Given the isolated recorder UI is open with "saved transcript" fixtures
    When the user opens the "Transcription jobs" tab
    Then the job list contains status "Succeeded"
    When the user selects the first transcription job
    And the user presses "View transcript"
    Then the "Transcript" dialog contains "UI fixture transcript."
    When the user chooses "Cancel" in the "Transcript" dialog
    Then the recording status reads "Idle"

  Scenario: Cancel the selected running transcription while keeping capture available
    Given the isolated recorder UI is open with "running job" fixtures
    When the user opens the "Transcription jobs" tab
    Then the job list contains status "Running"
    When the user selects the first transcription job
    And the user presses "Cancel selected job"
    Then the job list contains status "Canceled"
    When the user confirms recording permission
    Then the "Start recording" button is enabled

  Scenario: Explicitly retry a saved recording
    Given the isolated recorder UI is open with "saved transcript" fixtures
    When the user selects the first recording
    And the user selects spoken language "Brazilian Portuguese"
    And the user presses "Transcribe again"
    And the user opens the "Transcription jobs" tab
    Then the transcription job list has 2 rows
    And the job list contains language "Brazilian Portuguese"
