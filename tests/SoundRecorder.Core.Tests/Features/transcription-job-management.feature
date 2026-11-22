@requirements @meeting_workspace
Feature: Manage distinct transcription attempts and recognition progress
  A job snapshots its model and language when queued.
  Progress is optional and is not a signal that final outputs have been saved.
  Existing cancellation, FIFO, and unique-output requirements remain authoritative.

  Scenario: Preserve queued and running model and language choices
    Given a job was queued with Base original and English
    And another job is running with Tiny original and Brazilian Portuguese
    When the user selects Small Q5_1 and Brazilian Portuguese for new jobs
    Then the queued job retains Base original and English
    And the running job retains Tiny original and Brazilian Portuguese
    When the user requests another transcription
    Then only the new job snapshots Small Q5_1 and Brazilian Portuguese

  Scenario: Fail explicitly if a queued job's pinned model is unavailable
    Given a queued job retains a model that has become unavailable
    And a different compatible model is currently selected
    When the queued job attempts to start
    Then that job reports an actionable missing-model failure
    And the different selected model is not substituted
    And its recording and earlier completed outputs remain unchanged

  @type:IntegrationTests
  Scenario: Transport coarse worker recognition progress without treating it as a result
    Given a controlled owned worker is processing a job
    When it reports recognition percentages 0, 37, and 84 through progress messages
    Then the public job observer receives those estimates for that job
    And the job remains running
    And no progress message is parsed as a final transcript result
    And no final TXT or SRT output is published yet

  Scenario: Do not mark success from a recognition percentage
    Given a running job has reported 100 percent recognition progress
    And processing or output publication is still unfinished
    Then the job is not marked successful
    When processing finishes and both unique TXT and SRT files are published
    Then the job is marked successful

  Scenario Outline: Keep an unreported phase indeterminate
    Given a running job is in phase "<phase>" without percentage information
    When its public progress state is observed
    Then the phase is "<phase>" and its percentage is unavailable
    And elapsed time is not converted into fabricated progress
    And the job is not marked successful

    Examples:
      | phase       |
      | Preparing   |
      | Recognizing |
      | Writing     |

  @type:IntegrationTests
  Scenario: Stop one running job and release its owned worker
    Given a transcription job is running on the reference PC
    And another job is queued
    When the user stops the running job
    Then the stop request is acknowledged within 1 second
    And restart and history deletion wait until its worker is released
    And its worker is released within 5 seconds
    And the stopped job is marked interrupted with a user-stop reason
    And the next queued job can start
    And the WAV and all earlier successful outputs remain unchanged
    And no new final outputs from the stopped attempt are published

  Scenario: Restart a stopped job as a separate attempt from the beginning
    Given a job was stopped during recognition
    And a compatible downloaded model and language are selected
    When the user requests restart
    Then a new job with a different ID is queued for the same recording
    And it snapshots the then-selected model and language
    And it begins at the start of the saved audio
    And the stopped attempt and all earlier successful outputs remain in history

  Scenario: Ignore late progress and results from a terminated attempt
    Given one attempt has been stopped and a new attempt is running
    When delayed progress or a result arrives for the stopped attempt
    Then the new attempt's state and progress remain unchanged
    And no stopped-attempt output is published
    And the terminated attempt is not reported successful

  Scenario: Delete one terminal job without deleting other attempts or outputs
    Given a recording has a completed job and another queued attempt
    When the user deletes the completed job's history
    Then only that job-history entry is removed
    And its TXT and SRT pair remains owned by the recording
    And the recording and queued attempt remain unchanged
    And later full recording deletion can still find that output pair

  Scenario: Reject history deletion before active work is terminated
    Given a transcription job is running
    When deletion of that job's history is requested without stopping or cancelling it
    Then the request reports that the job must be stopped or cancelled first
    And the active job remains manageable and its history is retained
