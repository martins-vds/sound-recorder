@requirements @v1
Feature: Install and operate a private offline Windows recorder
  Recording and transcription require no account or cloud service.
  Network access is reserved for explicitly requested model installation or updates.

  Scenario: Install as a standard Windows user
    Given a Windows 11 x64 computer
    And the user has no administrator rights
    When the user installs and launches the desktop app
    Then installation and normal use do not require elevation
    And recording and transcription do not require a dedicated GPU
    And the initial user interface is in English

  Scenario: Complete initial model setup through an explicit download
    Given the user has internet access
    When the user explicitly requests a model download
    Then the app may download the model
    And the app checks compatibility and package integrity before activation
    And the request contains no recording audio or transcript content

  @meeting_workspace
  Scenario: Complete setup without connecting the target computer
    Given the installer and a compatible model package have been transferred from another computer
    And the target computer has no network connection
    When the user installs the app and imports the local model package
    Then the app checks compatibility and package integrity before activation
    When the user selects the verified imported model for new jobs
    Then recording and local transcription become available without network access

  Scenario: Reject an unusable model package
    When an imported or downloaded model package fails compatibility or integrity checks
    Then the app reports an actionable error
    And the package is not activated
    And any previously working model remains available

  Scenario: Preserve the working model when an update fails
    Given a working transcription model is installed
    When an explicitly requested model update fails
    Then the previous working model remains usable
    And the update failure is clearly reported

  Scenario: Operate with the network disconnected
    Given a compatible model is installed
    And all network connections are disabled
    When the user records a conversation and requests transcription
    Then recording and transcription complete locally
    And no account, cloud endpoint, or internet connection is required
    And there is no cloud fallback

  Scenario: Make no unsolicited network requests
    Given the user has not requested model installation or an update
    When the app starts, records, transcribes, or remains idle
    Then the app makes no network requests
    And the app sends no telemetry
    And the app performs no background update checks

  Scenario: Keep conversation content local
    Then audio, transcripts, recording history, and recovery files remain in local storage
    And diagnostics contain no audio or transcript content
    And version one does not provide app-managed encryption or password protection
    And the app does not claim to control external backup or synchronization tools

  Scenario: Reject a network output destination
    When the user selects a network share or mapped network drive as the output destination
    Then the destination is rejected with an actionable explanation
    And the app does not silently substitute another destination

  Scenario: Warn about a known synchronized folder
    When the user selects a folder known to be cloud-synchronized
    Then the app warns that an external service may upload files from that folder
    And the app does not claim to detect every third-party synchronization tool

  Scenario: Use the app with a keyboard and screen reader
    When the user navigates recording, setup, history, and transcription controls
    Then all controls are usable with a keyboard
    And controls have screen-reader labels
    And recording, paused, interrupted, and job statuses are available to the screen reader

  Scenario: Keep initial platform scope explicit
    Then Windows versions other than Windows 11 are outside version-one support
    And ARM64 is outside version-one support
    And compressed audio output and separate source tracks are outside version-one scope
