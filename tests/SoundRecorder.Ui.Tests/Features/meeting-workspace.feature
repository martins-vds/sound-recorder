@ui @type:IntegrationTests @meeting_workspace @planned
Feature: Use the approved meeting workspace
  These scenarios exercise native WPF, not the browser prototype.
  Automated layout checks supplement the recurring manual comparison with prototype B.

  Scenario Outline: Keep B's work areas usable at supported window sizes
    Given the isolated recorder UI is open with "meeting workspace" fixtures
    When the user resizes the recorder to <width> by <height>
    Then the library is to the left of the transcript reader
    And the capture dock is below the library and reader
    And the library, reader, capture actions, and job navigation fit inside the client area
    And only one recording and one transcript reader are selected
    And the prototype variant switcher is absent

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario: Select a recording without changing capture setup
    Given the isolated recorder UI is open with "meeting workspace" fixtures
    And a microphone, language, destination, and title are configured for a new recording
    When the user selects another library recording
    Then that recording's title, captured duration, and completed transcript are shown in the reader
    And the new recording's capture setup remains unchanged

  Scenario: Search titles and completed transcript phrases
    Given the isolated recorder UI is open with "searchable meetings" fixtures
    When the user searches the library for "handover"
    Then only library entries matching a title or completed transcript phrase are shown
    And the query does not modify any recording or job

  Scenario: Explain a filter with no matches while retaining the current review
    Given the isolated recorder UI is open with "meeting workspace" fixtures
    And a recording is selected for review
    When the user searches the library for an unmatched phrase
    Then the library explains that no meetings match
    And the selected recording and its transcript remain in the reader
    And clearing the query restores the library results

  Scenario: Keep capture controls visible while reviewing a long transcript
    Given the isolated recorder UI is open with "long transcript" fixtures
    When the user confirms recording permission and starts capture
    And the user scrolls the transcript reader
    Then recording status, captured duration, Pause, and Stop remain visible and operable
    And scrolling the reader does not pause or stop capture

  Scenario: Use model and job views without leaving the workspace
    Given the isolated recorder UI is open with "meeting workspace" fixtures
    When the user opens local transcription models
    Then model management occupies the reader work area
    And the library and capture dock remain available
    When the user opens transcription jobs
    Then the job view occupies the reader work area
    And queued or active jobs precede completed job history
