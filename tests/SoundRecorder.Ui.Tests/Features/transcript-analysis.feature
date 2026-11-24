Feature: Experimental local transcript analysis controls
  Saved transcript analysis remains separate from the recording workflow.

  Scenario Outline: Restore the exact transcript discussion without inference on restart
    Given the isolated recorder UI is open with "saved transcript" fixtures
    When the user resizes the recorder to <width> by <height>
    And the user opens experimental local transcript analysis
    And the user selects local lookup coverage
    And the user types the analysis question "Who is mentioned?"
    And the user presses "Ask selected transcript"
    Then the experimental analysis presents its exact fixture evidence
    When the user restarts the isolated recorder with its existing state
    And the user opens experimental local transcript analysis
    Then the saved discussion identifies "Who is mentioned?" with local coverage
    And the experimental analysis presents its exact fixture evidence
    And the capture dock is below the library and reader

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario Outline: Model setup remains separate and explicit
    Given the isolated recorder UI is open with "saved transcript" fixtures
    When the user resizes the recorder to <width> by <height>
    And the user opens experimental local transcript analysis
    And the user opens analysis model setup
    Then the analysis profile shows both roles, licenses, bytes, and local storage
    And the "Download pinned profile..." button is enabled

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario Outline: Inspect an explicitly requested candidate and its original evidence
    Given the isolated recorder UI is open with "saved transcript" fixtures
    When the user resizes the recorder to <width> by <height>
    And the user opens experimental local transcript analysis
    Then the "Generate experimental summary" button is enabled
    When the user presses "Generate experimental summary"
    Then the experimental analysis presents its exact fixture evidence
    And the capture dock is below the library and reader

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario Outline: Analysis requires a successful saved transcript
    Given the isolated recorder UI is open with "no model" fixtures
    When the user resizes the recorder to <width> by <height>
    And the user opens experimental local transcript analysis
    Then the "Generate experimental summary" button is disabled
    And the "Ask selected transcript" button is disabled
    And experimental analysis has no target

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario Outline: Cancellation does not claim output before native release
    Given the isolated recorder UI is open with "saved transcript" fixtures
    When the user resizes the recorder to <width> by <height>
    And the user opens experimental local transcript analysis
    And the user types the analysis question "UI fixture wait"
    And the user presses "Ask selected transcript"
    Then experimental analysis is busy
    When the user presses "Cancel analysis"
    Then experimental analysis reports "Cancelled"

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario Outline: Analysis failures are actionable rather than answers
    Given the isolated recorder UI is open with "saved transcript" fixtures
    When the user resizes the recorder to <width> by <height>
    And the user opens experimental local transcript analysis
    And the user types the analysis question "UI fixture failure"
    And the user presses "Ask selected transcript"
    Then experimental analysis reports "Analysis failed"

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |
