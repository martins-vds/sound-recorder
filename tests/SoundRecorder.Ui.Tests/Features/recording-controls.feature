@ui @type:IntegrationTests @meeting_workspace
Feature: Operate recording controls through the desktop UI
  These scenarios exercise the real window with deterministic capture adapters.
  They do not establish real-device capture quality or release performance.

  Scenario: Require fresh recording permission on launch
    Given the isolated recorder UI is open with "no model" fixtures
    Then the recording status reads "Idle"
    And the captured duration reads "00:00:00"
    And recording permission is unchecked
    And Pause, Resume, and Stop are not offered as idle capture actions
    When the user presses "Start recording"
    Then recording setup and the permission confirmation are visible
    And the recording status reads "Idle"
    And no recording file or transcription job is created

  Scenario Outline: Enable only devices used by the selected audio source
    Given the isolated recorder UI is open with "no model" fixtures
    When the user expands recording setup
    And the user selects audio source "<source>"
    Then the microphone selector is <microphone>
    And the playback selector is <playback>
    And the source explanation contains "<explanation>"

    Examples:
      | source                     | microphone | playback | explanation                       |
      | Microphone only            | enabled    | disabled | mono                              |
      | System audio only          | disabled   | enabled  | all sound                         |
      | Microphone and system audio | enabled   | enabled  | all sound                         |

  Scenario: Pause and resume one recording through the controls
    Given the isolated recorder UI is open with "no model" fixtures
    When the user confirms recording permission
    Then the "Start recording" button is enabled
    When the user presses "Start recording"
    Then the recording status reads "Recording"
    And Start recording and Resume are not offered during active capture
    And the "Pause" button is enabled
    And the "Stop" button is enabled
    When the user presses "Pause"
    Then the recording status reads "Paused"
    And Pause and Start recording are not offered during paused capture
    And the "Resume" button is enabled
    And the paused captured duration does not advance
    When the user presses "Resume"
    Then the recording status reads "Recording"
    When the user presses "Stop"
    Then the recording status reads "Idle"
    And recording permission is unchecked
    And recording cannot begin again without fresh permission confirmation
    And the UI message contains "Install a compatible transcription model"
    And the recording history has 1 row

  Scenario: Explain an empty destination without starting capture
    Given the isolated recorder UI is open with "no model" fixtures
    When the user enters "" as the local destination
    And the user confirms recording permission
    And the user presses "Start recording"
    Then the UI message contains "Select a writable local destination folder"
    And the recording status reads "Idle"
    And the recording history has 0 rows
