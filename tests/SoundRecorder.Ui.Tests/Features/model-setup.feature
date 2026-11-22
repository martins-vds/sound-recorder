@ui @type:IntegrationTests @meeting_workspace
Feature: Manage local model setup through the desktop UI
  The package-selection adapter supplies a fixture after the user clicks Import.
  Package validation is real; no model inference or network request is performed.

  Scenario: Import an explicitly selected compatible package
    Given the isolated recorder UI is open with "valid package" fixtures
    Then the downloaded model selector is empty
    When the user presses "Import model package..."
    Then the downloaded model selector contains "ui-fixture"
    And no model is selected automatically
    When the user selects downloaded model "ui-fixture"
    Then the selected model is "ui-fixture"

  Scenario: Preserve the active model after an invalid import
    Given the isolated recorder UI is open with "invalid package" fixtures
    Then the selected model is "ui-fixture"
    When the user presses "Import model package..."
    Then the UI explains that the package was not registered
    And the UI message contains "SHA-256"
    And the selected model remains "ui-fixture"

  Scenario Outline: Browse the full catalog without starting a download
    Given the isolated recorder UI is open with "no model" fixtures
    When the user resizes the recorder to <width> by <height>
    When the user opens local transcription models and the download catalog
    Then the catalog shows 12 model families and 72 downloadable artifacts
    And the downloaded model selector remains empty
    And no network request has been made

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario Outline: Filter available downloads without changing downloaded selection
    Given the isolated recorder UI is open with "model catalog" fixtures
    When the user filters the model catalog by "<filter>"
    Then the catalog shows <families> model families
    And the downloaded model selector and selected model remain unchanged

    Examples:
      | filter       | families |
      | All models   | 12       |
      | Multilingual | 8        |
      | English only | 4        |

  Scenario Outline: Show transfer progress outside the model view without selecting the download
    Given the isolated recorder UI is open with "controlled model download" fixtures
    And Base original is selected
    When the user resizes the recorder to <width> by <height>
    When the user requests Small Q5_1 and the fixture transfers 1024 of 4096 bytes
    Then model download progress shows 25 percent
    And Small Q5_1 is not selectable yet
    When the user returns to the library
    Then the header still identifies Small Q5_1 and shows its download progress
    When transfer and verification finish
    Then Small Q5_1 is selectable
    And Base original remains selected

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario Outline: Keep verification and unknown transfer progress honest
    Given the isolated recorder UI is open with "unknown download length" fixtures
    When the user resizes the recorder to <width> by <height>
    When the user requests a model download
    Then download progress is indeterminate
    When transfer finishes but verification has not completed
    Then the UI identifies verification and does not offer the model as downloaded

    Examples:
      | width | height |
      | 760   | 560    |
      | 980   | 720    |

  Scenario: Cancel a download without cancelling capture
    Given the isolated recorder UI is open with "controlled model download" fixtures
    And capture and a model download are running
    When the user cancels the model download
    Then only that download is cancelled
    And the incomplete model is not selectable
    And capture and the selected model remain unchanged

  Scenario: Keep an English-only choice while explaining incompatible transcription
    Given the isolated recorder UI is open with "English-only downloaded model" fixtures
    When the user selects Brazilian Portuguese for a new transcription
    Then the UI asks for a compatible multilingual downloaded model
    And the English-only model remains listed
    And recording remains available
    And another model is not selected silently
