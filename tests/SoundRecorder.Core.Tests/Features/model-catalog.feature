@requirements @meeting_workspace @planned
Feature: Choose verified downloaded models and explicitly download other models
  The catalog is pinned to the Whisper.net 1.9.1 downloader.
  All network and package fixtures are controlled; tests do not download model binaries.

  Scenario: Offer the full built-in model download catalog
    When the user browses the built-in model download catalog
    Then it contains exactly these model families and language capabilities
      | model          | language capability |
      | tiny           | Multilingual        |
      | tiny.en        | English only        |
      | base           | Multilingual        |
      | base.en        | English only        |
      | small          | Multilingual        |
      | small.en       | English only        |
      | medium         | Multilingual        |
      | medium.en      | English only        |
      | large-v1       | Multilingual        |
      | large-v2       | Multilingual        |
      | large-v3       | Multilingual        |
      | large-v3-turbo | Multilingual        |
    And each family offers exactly these separate downloadable encodings
      | encoding       |
      | NoQuantization |
      | Q4_0           |
      | Q4_1           |
      | Q5_0           |
      | Q5_1           |
      | Q8_0           |
    And the catalog offers 72 distinct model artifacts
    And VAD models and platform encoder bundles are not transcription-model choices
    And browsing the catalog makes no network request

  Scenario: Offer only complete verified local models for selection
    Given a verified downloaded Base original model exists
    And a Small model is still downloading
    And a Medium model failed integrity verification
    When the user opens downloaded model selection
    Then only the Base original model is selectable
    And incomplete or unverified artifacts are not offered as ready models

  Scenario: Keep different encodings as distinct downloaded choices
    Given Base original and Base Q5_1 are both verified and downloaded
    When the user selects Base Q5_1
    Then new transcription requests use the Base Q5_1 artifact identity
    And the Base original artifact remains available

  Scenario: Report actual transfer progress and verify before registration
    Given the selected model is a verified Tiny model
    And an explicitly requested model download has a total length of 4096 bytes
    When 1024 bytes have been transferred
    Then download progress reports 25 percent
    And the downloading model is not selectable
    When all bytes have arrived but verification is unfinished
    Then the model is still not selectable
    When compatibility and expected SHA-256 verification succeed
    Then the new model becomes a downloaded choice
    And Tiny remains the selected model until the user explicitly changes selection

  Scenario: Show indeterminate download progress when total size is unavailable
    Given an explicitly requested model download has no known total byte count
    When bytes are transferred
    Then transferred bytes are observable
    And a total percentage is unavailable rather than fabricated
    And completion still requires compatibility and integrity verification

  Scenario Outline: Preserve the selected model when a download cannot finish
    Given a verified downloaded Base model is selected
    When an explicitly requested download encounters "<failure>"
    Then the failure is reported with a recovery action
    And the incomplete artifact is not registered as a downloaded model
    And Base remains selectable and selected
    And only owned download staging files are cleaned

    Examples:
      | failure                |
      | interrupted transfer   |
      | SHA-256 mismatch       |
      | incompatible model     |

  Scenario: Cancel a model download without affecting recording or selection
    Given a verified downloaded model is selected
    And capture and a model download are active
    When the user cancels the model download
    Then only that download is cancelled
    And its incomplete artifact is not selectable
    And capture and the selected model remain unchanged

  Scenario: Select a downloaded model without network access
    Given Base original and Small Q5_1 have been verified and downloaded
    And the network is disconnected
    When the user selects Small Q5_1 and restarts the app
    Then Small Q5_1 remains the selected model
    And both downloaded artifacts remain available
    And no network request is required

  Scenario: Keep English-only models available without using them for Portuguese
    Given a downloaded Tiny.en model is selected
    When a Brazilian Portuguese recording is saved
    Then its audio is retained without a new transcription job
    And the user is told to choose a compatible downloaded model
    And Tiny.en remains a valid English-only downloaded choice
    And no multilingual model is selected silently

  Scenario: Preserve inventory and selection when a selected artifact disappears
    Given a verified downloaded model was selected
    And its model file is externally removed
    When the app reloads the model inventory
    Then that artifact is reported unavailable for use
    And another downloaded model is not selected silently
    And recording remains available
