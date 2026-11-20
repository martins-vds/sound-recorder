@requirements @v1
Feature: Preserve captured audio and report interruptions honestly
  Failures must not silently switch devices, destinations, or processing services.
  An incomplete recording must not be presented as complete.

  Scenario Outline: Block capture when a prerequisite is unavailable
    Given "<prerequisite>" is unavailable
    When the user attempts to start recording
    Then capture does not start
    And the app shows an actionable explanation
    And the app does not silently choose another device or folder

    Examples:
      | prerequisite                       |
      | Windows microphone permission      |
      | the selected microphone            |
      | the selected playback device       |
      | write access to the output folder  |

  Scenario: Warn about insufficient space for maximum duration
    Given the destination has insufficient free space for four hours in the selected audio format
    When the user attempts to start recording
    Then the app warns about insufficient space for the maximum duration
    And a shorter recording may proceed only after explicit user confirmation
    And a non-writable destination still blocks recording

  Scenario Outline: Stop capture safely on a source or storage failure
    Given a recording is active or paused
    When "<failure>" occurs
    Then the app ends the recording and marks it interrupted
    And the app preserves all recoverable captured audio
    And the app displays an actionable error
    And the app does not silently switch devices or destination folders
    And the app does not present the recording as complete

    Examples:
      | failure                             |
      | a selected device is disconnected   |
      | writing captured audio fails        |
      | the destination runs out of space   |

  Scenario Outline: Recover after an unexpected interruption
    Given audio was captured to healthy writable storage
    When the process is forcibly terminated during "<phase>"
    And the app is launched again
    Then the app offers recovery of the available audio
    And recovered audio omits at most the final 5 seconds captured before termination
    And the recovered recording is identified as recovered rather than complete
    And transcription of the recovered recording requires an explicit request

    Examples:
      | phase                    |
      | active audio capture     |
      | paused recording         |
      | audio-file finalization  |

  Scenario: Recover after power loss
    Given captured audio has been periodically persisted to healthy storage
    When power is lost and the computer is restarted
    Then the app offers recovery on its next launch
    And recovered audio omits at most the final 5 seconds captured before power loss
    And the recording is not presented as complete

  Scenario: Report the limits of recovery
    Given storage failure prevents recovery of captured audio
    When the app attempts recovery
    Then the app reports what could not be recovered
    And the app does not promise the five-second loss limit for failed storage
    And the app does not report recovery as successful when no audio was recovered

  Scenario Outline: Handle explicit Windows lifecycle interruptions
    Given a recording is active or paused
    When Windows is explicitly "<action>"
    Then the app finalizes available audio when possible or offers recovery on next launch
    And the recording is identified as interrupted or recovered
    And capture does not silently resume afterward

    Examples:
      | action      |
      | suspended   |
      | shut down   |

  Scenario: Keep work running when minimized
    Given a recording or transcription job is active
    When the user minimizes the window
    Then recording and transcription continue
    And Windows notification-area status visibly indicates ongoing work
    And an active recording remains visibly identifiable as recording

  Scenario: Confirm closing during active work
    Given a recording, paused recording, or transcription job is active
    When the user closes the main window
    Then the app offers to keep running or stop work and exit
    When the user chooses to keep running
    Then ongoing work continues with notification-area status

  Scenario: Stop work and exit explicitly
    Given a recording or transcription job is active
    When the user confirms stopping work and exiting
    Then available captured audio is finalized before exit when storage permits
    And the running transcription job is safely canceled without changing its audio
    And no new final transcript files are produced by the canceled job
    And jobs already queued but not started are preserved for the next launch
    And exit does not enqueue a new automatic transcription job for the recording being finalized

  Scenario: Avoid unintended recording
    When the app starts
    Then recording does not start automatically
    And version one does not provide global recording hotkeys
