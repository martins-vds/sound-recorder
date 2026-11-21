@ui @type:IntegrationTests
Feature: Manage local model setup through the desktop UI
  The package-selection adapter supplies a fixture after the user clicks Import.
  Package validation is real; no model inference or network request is performed.

  Scenario: Import an explicitly selected compatible package
    Given the isolated recorder UI is open with "valid package" fixtures
    Then the model status contains "No compatible model"
    When the user presses "Import model package..."
    Then the model status contains "Active model: ui-fixture"
    And the UI message contains "verified and activated locally"

  Scenario: Preserve the active model after an invalid import
    Given the isolated recorder UI is open with "invalid package" fixtures
    Then the model status contains "Active model: ui-fixture"
    When the user presses "Import model package..."
    Then the UI message contains "not activated"
    And the UI message contains "SHA-256"
    And the model status contains "Active model: ui-fixture"

  Scenario: Explain an invalid download URL without installing a model
    Given the isolated recorder UI is open with "no model" fixtures
    When the user enters "file:///model.zip" as the model package URL
    And the user presses "Download selected package"
    Then the UI message contains "Enter an HTTP or HTTPS URL"
    And the model status contains "No compatible model"
