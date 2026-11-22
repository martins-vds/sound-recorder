@requirements @meeting_workspace
Feature: Review recordings and manage their owned artifacts
  A library entry can outlive its audio.
  Artifacts belong to a recording independently of transcription job history.
  Storage and playback fixtures never use a user's recordings or destination.

  @type:IntegrationTests
  Scenario: Search recording titles and completed transcript content locally
    Given the library contains recordings "Planning" and "Review"
    And only the completed transcript for "Review" contains "handover"
    When the user searches the library for "handover"
    Then only "Review" is returned
    And recording files, transcript files, and job states remain unchanged
    And no network request is made

  @type:IntegrationTests
  Scenario: Play and seek saved audio on its captured-audio timeline
    Given a disposable valid WAV recording with a completed transcript
    And the WAV includes no audio from paused capture intervals
    When the user plays that recording and seeks to 4 seconds
    Then playback reads that recording's WAV
    And its position is 4 seconds on the saved-audio timeline
    And the completed transcript remains available for review
    And no transcription job is started by playback

  @type:IntegrationTests
  Scenario: Report externally missing audio while retaining transcript review
    Given a library entry has an audio file and a completed TXT and SRT pair
    And an external operation removes only its audio file
    When the user requests playback
    Then the missing audio is reported explicitly
    And the completed transcript and subtitles remain available
    And the audio is not recreated or replaced

  @type:IntegrationTests
  Scenario: Delete audio only across several completed transcription attempts
    Given a disposable recording has two completed transcription jobs and two unique TXT and SRT pairs
    When the user confirms deletion of only its audio
    Then the WAV is removed
    And both TXT and SRT pairs and their content remain unchanged
    And the library entry and both job-history entries remain
    And playback and requests for new transcription are unavailable for that entry

  @type:IntegrationTests
  Scenario: Delete all owned artifacts without deleting the output folder
    Given a disposable recording has two unique TXT and SRT pairs
    And another recording and an unrelated file share its output folder
    And a downloaded model is available
    When the user confirms deletion of the recording and all its artifacts
    Then only that recording's WAV and both TXT and SRT pairs are removed
    And its library and job-history entries are removed
    And the output folder, unrelated file, other recording, and downloaded model remain unchanged

  @type:IntegrationTests
  Scenario: Retain artifact ownership after deleting job history
    Given a disposable recording has a completed transcription job with a TXT and SRT pair
    When the user deletes only that job's history
    Then its completed outputs remain associated with the library entry
    When the user confirms deletion of the recording and all its artifacts
    Then the WAV and that job's TXT and SRT pair are removed
    And no orphan output from the deleted job history remains

  @type:IntegrationTests
  Scenario: Release related work before removing audio
    Given a recording is being played and transcribed
    And another recording session is capturing audio
    When the user confirms deletion of the saved recording's audio
    Then related transcription is cancelled and its worker is released before file removal
    And the playback file handle is closed before file removal
    And the unrelated recording session keeps capturing
    And late progress or results from the cancelled attempt do not recreate artifacts

  @type:IntegrationTests
  Scenario: Refuse deletion while a related worker cannot be released
    Given a related transcription worker cannot be released within the existing worker-release deadline
    When the user confirms deletion of the recording's audio
    Then deletion reports an actionable worker-release failure
    And no recording artifact or library entry is removed
    And the operation is not reported as successful

  @type:IntegrationTests
  Scenario: Report a partial deletion and retry only the remaining owned artifacts
    Given a disposable recording has a WAV and a completed TXT and SRT pair
    And deleting the WAV succeeds but the TXT cannot be deleted
    When the user confirms deletion of all its artifacts
    Then the library records the audio removal and the unfinished requested deletion
    And the remaining failed artifact is identified with an actionable error
    And complete deletion is not reported
    When the file restriction is removed and the user retries that deletion
    Then the remaining owned artifacts and associated history are removed
    And already removed audio is not recreated

  @type:IntegrationTests
  Scenario: Recover an unfinished deletion without silently deleting more files
    Given confirmed artifact deletion was interrupted after removing only the WAV
    When the app reloads the disposable library storage
    Then the unfinished deletion and remaining artifacts are visible
    And retained transcripts remain available
    And no further artifact is removed until the user explicitly retries

  @type:IntegrationTests
  Scenario: Preserve legacy recording history during migration
    Given disposable legacy history contains a recording with completed job outputs
    When the app loads and saves the migrated library
    Then the recording and job IDs, paths, language, captured duration, and statuses are preserved
    And existing audio and output bytes remain unchanged
    And the output pair is associated with its library entry
    And no unknown historical model choice is fabricated

  @type:IntegrationTests
  Scenario: Reject an artifact reference that does not belong to the recording
    Given a recording's artifact metadata includes an unrelated file reference
    When deletion of all recording artifacts is requested
    Then ownership validation reports an actionable failure
    And the unrelated file is not removed
    And a successful complete deletion is not reported
