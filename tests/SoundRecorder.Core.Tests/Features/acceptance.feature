# These are agreed requirements, not evidence that an implementation has passed.
# Candidate engine/model selection and benchmark fixture preparation remain future work.
@requirements @v1 @release_acceptance @ignore
Feature: Verify recording and offline transcription quality
  Release acceptance requires repeatable measurements, not assumed model capability.

  Background:
    Given the reference PC has an Intel Core i5-1240P CPU
    And the reference PC has 16 GB of RAM and SSD storage
    And the reference PC runs Windows 11 x64 while plugged into power
    And inference uses the CPU without GPU acceleration
    And there is no competing heavy workload
    And a compatible transcription model is already installed

  Scenario: Prepare reproducible release evidence
    Given a candidate application and transcription model
    When release acceptance is evaluated
    Then a versioned test corpus is available with permission for its use
    And each language and acoustic condition has at least 30 minutes of audio
    And spoken samples have human-verified reference transcripts
    And silence samples and four-hour endurance samples are available
    And evidence records the corpus version, app version, engine, and model identity
    And evidence records the text-normalization rules and reference-machine configuration
    And evidence records measured results rather than inferred or estimated results
    And acceptance is not claimed before the required evidence exists and passes

  Scenario Outline: Meet the offline transcription speed target
    Given a 30-minute "<language>" benchmark recording from the versioned corpus
    And no other recording is in progress
    And network connections are disabled
    When a transcription job starts
    Then both final TXT and SRT outputs are saved within 30 minutes of job start
    And the measurement includes any model loading performed after job start
    And the measurement excludes queue wait and model installation

    Examples:
      | language             |
      | English              |
      | Brazilian Portuguese |

  Scenario Outline: Meet language-specific accuracy thresholds
    Given the fixed "<condition>" corpus for "<language>"
    When the candidate transcribes the corpus locally
    Then aggregate word error rate is at most <maximum_percent> percent for that corpus
    And case and punctuation are normalized consistently in reference and generated text
    And word error rate is calculated as "100 * (substitutions + deletions + insertions) / reference word count"
    And passing one language or condition does not compensate for failing another

    Examples:
      | language             | condition            | maximum_percent |
      | English              | clear speech         | 15              |
      | Brazilian Portuguese | clear speech         | 15              |
      | English              | computer-call audio  | 25              |
      | Brazilian Portuguese | computer-call audio  | 25              |

  Scenario: Avoid invented speech during silence
    Given a silence-only sample from the versioned corpus
    When it is transcribed in each supported language
    Then no transcript text segments or SRT cues are produced
    And empty final TXT and SRT files may represent the successful empty transcript

  Scenario Outline: Record the full supported duration
    Given healthy writable storage with sufficient free space
    And a deterministic continuous test signal for "<source>"
    When the app records four hours of captured duration
    Then the resulting WAV file is playable through its full captured duration
    And no capture-caused gaps or truncated audio appear before the normal duration limit
    And the app saves normally at the four-hour limit

    Examples:
      | source                |
      | microphone only       |
      | system audio only     |
      | microphone and system |

  Scenario: Prioritize capture during concurrent transcription
    Given a transcription job is processing a benchmark recording
    When the user records a deterministic continuous test signal
    Then the saved recording has no capture-caused gaps or truncated audio
    And transcription continues or yields resources without corrupting its output
    And the real-time transcription speed target does not apply during concurrent capture

  Scenario: Verify responsiveness and recovery separately from recognition speed
    When release acceptance is evaluated on healthy writable storage
    Then evidence verifies recording indication within 1 second of successful Start
    And evidence verifies cancellation acknowledgement within 1 second
    And evidence verifies transcription worker release within 5 seconds of cancellation
    And evidence verifies at most 5 seconds of captured-audio loss after forced termination
    And forced-termination recovery is tested during capture, pause, and finalization
    And controlled power-loss recovery is verified separately on a dedicated test machine
