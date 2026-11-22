@ui @type:IntegrationTests @meeting_workspace
Feature: Access desktop controls and close active work explicitly
  Tests use the Windows accessibility tree in an interactive desktop session.
  Notification-area integration and real power interruptions are outside this fixture suite.

  Scenario Outline: Keep model setup usable at supported window sizes
    Given the isolated recorder UI is open with "no model" fixtures
    When the user resizes the recorder to <width> by <height>
    And the user opens local transcription models and the download catalog
    Then the downloaded model selector and catalog download actions fit inside the client area
    And capture remains idle until recording permission is confirmed

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario: Expose input labels and live status regions to automation
    Given the isolated recorder UI is open with "no model" fixtures
    When the user expands recording setup
    And the user opens local transcription models and the download catalog
    Then these input controls have accessible names
      | automation id     | accessible name                |
      | SourceCombo       | Audio source                   |
      | MicrophoneCombo   | Microphone                     |
      | LanguageCombo     | Spoken language                |
      | TitleBox          | Optional recording title       |
      | DestinationBox    | Local destination folder       |
      | PermissionCheckBox | Permission to record confirmed |
      | DownloadedModelSelector | Downloaded models        |
      | ModelCatalogFilter | Catalog language support      |
    And recording status and messages are assertive live regions

  Scenario: Expose playback and progress without stealing focus
    Given the isolated recorder UI is open with "playable transcript and running job" fixtures
    When the user operates recording playback using the keyboard
    Then playback controls have accessible names and a readable position
    When a job reports progress
    Then the progress value and phase are exposed to automation
    And keyboard focus stays on the user's control

  Scenario: Contain keyboard focus in destructive confirmation
    Given the isolated recorder UI is open with "playable transcript" fixtures
    When the user opens Delete recording using the keyboard
    Then both deletion scopes and their consequences have accessible labels
    And Tab remains inside the confirmation dialog
    When the user presses Escape
    Then the dialog closes without deletion
    And focus returns to the recording's review

  Scenario: Start a recording using keyboard activation
    Given the isolated recorder UI is open with "no model" fixtures
    When the user tabs from the audio source selector
    Then the microphone selector has keyboard focus
    When the user confirms recording permission using the keyboard
    Then the "Start recording" button is enabled
    When the user activates "Start recording" using the keyboard
    Then the recording status reads "Recording"
    When the user presses "Stop"
    Then the recording status reads "Idle"

  Scenario: Keep capture active across minimization
    Given the isolated recorder UI is open with "no model" fixtures
    When the user confirms recording permission
    And the user presses "Start recording"
    Then the recording status reads "Recording"
    When the user minimizes and restores the recorder window
    Then the recording status reads "Recording"
    And the "Stop" button is enabled
    When the user presses "Stop"
    Then the recording status reads "Idle"

  Scenario: Cancel closing during active capture
    Given the isolated recorder UI is open with "no model" fixtures
    When the user confirms recording permission
    And the user presses "Start recording"
    Then the recording status reads "Recording"
    When the user closes the recorder window
    Then the "Active work" dialog contains "Keep running"
    When the user chooses "Cancel" in the "Active work" dialog
    Then the recording status reads "Recording"
    When the user presses "Stop"
    Then the recording status reads "Idle"

  Scenario: Explicitly stop active capture and exit
    Given the isolated recorder UI is open with "no model" fixtures
    When the user confirms recording permission
    And the user presses "Start recording"
    Then the recording status reads "Recording"
    When the user closes the recorder window
    Then the "Active work" dialog contains "stop active work and exit"
    When the user chooses "No" in the "Active work" dialog
    Then the isolated recorder process exits
