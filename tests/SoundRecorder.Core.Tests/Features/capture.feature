@requirements @v1
Feature: Capture conversations to audio files
  A Windows user can record microphone audio, system audio, or both
  without relying on an internet connection.

  Scenario Outline: Select the audio source before starting
    Given no recording is active
    When the user selects "<source>"
    Then the user must select "<devices>" before starting
    And the selection remains fixed for the recording

    Examples:
      | source                | devices                           |
      | microphone only       | a microphone                      |
      | system audio only     | a playback device                 |
      | microphone and system | a microphone and a playback device |

  Scenario: Explain the scope of system audio
    When the user selects a source that includes system audio
    Then the app explains that all sound from the selected playback device may be captured
    And the explanation includes notifications and other applications
    And the app does not promise capture of only a particular call or application

  Scenario: Require recording permission confirmation
    Given the selected devices and destination are available
    When the user requests a new recording
    Then the app requires confirmation that the user has permission to record
    And no audio is captured before confirmation
    And declining leaves the recorder idle
    And the app does not present confirmation as a guarantee of legal compliance

  Scenario: Start a recording
    Given the user has confirmed permission to record
    And the selected devices and destination are available
    When recording starts successfully
    Then a visible recording indicator appears within 1 second on the reference PC
    And the app displays the captured duration
    And only one recording can be active

  Scenario: Monitor audio levels
    Given audio devices have been selected
    When the user prepares to record or is recording
    Then a level meter is available for each selected source
    And combined capture shows microphone and system-audio levels separately
    And the app recommends headphones for combined capture
    And the app does not guarantee echo cancellation

  Scenario: Pause and resume
    Given a recording is active
    When the user pauses the recording
    Then the app visibly indicates that recording is paused
    And paused time is excluded from the saved audio and captured duration
    When the user resumes the recording
    Then capture continues in the same recording
    And the app visibly indicates that recording is active

  Scenario Outline: Save the agreed audio format
    Given a recording uses "<source>"
    When the user stops the recording
    Then the app saves a 48000 Hz 16-bit PCM WAV file
    And the saved audio has "<channels>"
    And no separate source-track files are required

    Examples:
      | source                | channels |
      | microphone only       | mono     |
      | system audio only     | stereo   |
      | microphone and system | stereo   |

  Scenario: Mix combined sources
    Given microphone and system-audio capture is selected
    When the recording is saved
    Then microphone audio is mixed into both stereo channels
    And the saved audio includes the selected playback device's system audio

  Scenario: Save without overwriting existing files
    Given the user has selected a writable local destination folder
    And the user may have supplied an optional recording title
    When a recording is saved
    Then its WAV filename includes a timestamp
    And the optional title is included when supplied
    And the filename is unique within the destination folder
    And no existing file is overwritten

  Scenario: Enforce the captured-duration limit
    Given a recording has reached 3 hours and 55 minutes of captured duration
    Then the app warns that the four-hour limit is approaching
    When the recording reaches 4 hours of captured duration
    Then capture stops automatically
    And the app saves the recording normally
    And paused time does not count toward either threshold

  Scenario: Keep an active or paused recording awake
    Given a recording is active or paused
    Then the app prevents Windows idle sleep
    But the app does not prevent an explicit user or operating-system suspension or shutdown
