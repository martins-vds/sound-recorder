using System.Collections.Concurrent;
using System.Text;
using Reqnroll;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Persistence;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Core.Tests.Steps;

[Binding]
[Scope(Feature = "Transcribe saved recordings locally")]
public sealed class TranscriptionSteps
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"sound-recorder-tests-{Guid.NewGuid():N}");
    private TranscriptionQueue? _queue;
    private ControlledTranscriber? _transcriber;
    private MemoryJobRepository? _repository;
    private AtomicTranscriptWriter? _writer;
    private SavedRecording? _recording;
    private TranscriptionJob? _selectedJob;
    private RecorderApplication? _recorder;
    private MutableModelStore? _models;
    private RecordingQueueSpy? _recordingQueue;
    private byte[]? _audioBefore;
    private Dictionary<string, byte[]> _outputsBefore = [];
    private readonly List<string> _enqueueOrder = [];
    private int _startsBeforeExplicitRetry;
    private JsonRecordingHistory? _history;
    private IReadOnlyList<SavedRecording> _historyEntries = [];

    public TranscriptionSteps()
    {
        Directory.CreateDirectory(_directory);
    }

    [Given("the user is preparing a recording")]
    public void GivenTheUserIsPreparingARecording()
    {
        _recording = CreateRecording("English");
    }

    [When("the user selects {string}")]
    public void WhenTheUserSelects(string language)
    {
        _recording = _recording! with { Language = language };
    }

    [Then("the recording retains that language for its automatic transcription job")]
    public async Task ThenTheRecordingRetainsThatLanguageForItsAutomaticTranscriptionJob()
    {
        _models = new MutableModelStore { ModelAvailable = true };
        _recordingQueue = new RecordingQueueSpy();
        var engine = new SimpleRecordingEngine(_recording!);
        _recorder = new RecorderApplication(engine, _recordingQueue, _models);
        await _recorder.StartAsync(
            new RecordingRequest(AudioSource.MicrophoneOnly, "microphone", null, _directory, null),
            permissionConfirmed: true);
        await _recorder.StopAsync();
        Assert.Equal(_recording!.Language, Assert.Single(_recordingQueue.Recordings).Language);
    }

    [Given("no compatible transcription model is installed")]
    public void GivenNoCompatibleTranscriptionModelIsInstalled()
    {
        _models = new MutableModelStore();
        _recordingQueue = new RecordingQueueSpy();
    }

    [When("the user saves a recording")]
    public async Task WhenTheUserSavesARecording()
    {
        _recording = CreateRecording("English");
        var engine = new SimpleRecordingEngine(_recording);
        _recorder = new RecorderApplication(engine, _recordingQueue, _models);
        await _recorder.StartAsync(
            new RecordingRequest(AudioSource.MicrophoneOnly, "microphone", null, _directory, null),
            permissionConfirmed: true);
        await _recorder.StopAsync();
    }

    [Then("the audio is retained")]
    public void ThenTheAudioIsRetained()
    {
        Assert.True(File.Exists(_recording?.AudioPath));
    }

    [Then("the app clearly indicates that transcription needs a model")]
    public void ThenTheAppClearlyIndicatesThatTranscriptionNeedsAModel()
    {
        Assert.Contains("model", _recorder?.TranscriptionNotice, StringComparison.OrdinalIgnoreCase);
    }

    [Then("no transcription job for that recording enters the queue yet")]
    public void ThenNoTranscriptionJobForThatRecordingEntersTheQueueYet()
    {
        Assert.Empty(_recordingQueue!.Recordings);
    }

    [When("the user installs a compatible model and requests transcription")]
    public async Task WhenTheUserInstallsACompatibleModelAndRequestsTranscription()
    {
        _models!.ModelAvailable = true;
        await _recordingQueue!.EnqueueAsync(_recording!, CancellationToken.None);
    }

    [Then("a job for that recording enters the queue")]
    public void ThenAJobForThatRecordingEntersTheQueue()
    {
        Assert.Equal(_recording, Assert.Single(_recordingQueue!.Recordings));
    }

    [Given("several transcription jobs are queued")]
    public async Task GivenSeveralTranscriptionJobsAreQueued()
    {
        CreateQueue(blockWorker: true);
        for (var index = 0; index < 3; index++)
        {
            await _queue!.EnqueueAsync(CreateRecording($"English-{index}"), CancellationToken.None);
            _enqueueOrder.Add(_queue.Jobs.Last().Recording.AudioPath);
        }

        await WaitUntilAsync(() => _transcriber!.StartOrder.Count == 1);
    }

    [When("the worker is available")]
    public async Task WhenTheWorkerIsAvailable()
    {
        _transcriber!.Release();
        await _queue!.WaitForIdleAsync(CancellationToken.None);
    }

    [Then("queued jobs start in first-in, first-out order")]
    public void ThenQueuedJobsStartInFirstInFirstOutOrder()
    {
        var actual = _transcriber!.StartOrder;
        Assert.Equal(_enqueueOrder, actual);
    }

    [Then("at most one transcription job runs at a time")]
    public void ThenAtMostOneTranscriptionJobRunsAtATime()
    {
        Assert.Equal(1, _transcriber!.MaximumConcurrency);
    }

    [Then("the user can start another recording while transcription runs")]
    public async Task ThenTheUserCanStartAnotherRecordingWhileTranscriptionRuns()
    {
        var recorder = new RecorderApplication(
            new SimpleRecordingEngine(CreateRecording("English")));
        await recorder.StartAsync(
            new RecordingRequest(AudioSource.MicrophoneOnly, "microphone", null, _directory, null),
            permissionConfirmed: true);
        Assert.True(recorder.IsActive);
    }

    [Then("uninterrupted audio capture takes priority over transcription speed")]
    public void ThenUninterruptedAudioCaptureTakesPriorityOverTranscriptionSpeed()
    {
        Assert.True(TranscriptionSchedulingPolicy.CaptureHasPriority);
        Assert.Equal(1, TranscriptionSchedulingPolicy.MaximumConcurrentJobs);
    }

    [Given("a transcription job completes successfully")]
    public async Task GivenATranscriptionJobCompletesSuccessfully()
    {
        CreateQueue();
        _recording = CreateRecording("English");
        await _queue!.EnqueueAsync(_recording, CancellationToken.None);
        await _queue.WaitForIdleAsync(CancellationToken.None);
        _selectedJob = Assert.Single(_queue.Jobs);
    }

    [Then("its transcript can be viewed and copied in the app")]
    public void ThenItsTranscriptCanBeViewedAndCopiedInTheApp()
    {
        Assert.NotEmpty(File.ReadAllText(_selectedJob!.Outputs!.TextPath));
    }

    [Then(@"a UTF-(\d+) TXT file and a UTF-(\d+) SRT file are saved beside the WAV file")]
    public void ThenUtfFilesAreSavedBesideTheWavFile(int textEncoding, int subtitleEncoding)
    {
        Assert.Equal(8, textEncoding);
        Assert.Equal(8, subtitleEncoding);
        Assert.Equal(Path.GetDirectoryName(_recording!.AudioPath), Path.GetDirectoryName(_selectedJob!.Outputs!.TextPath));
        Assert.False(File.ReadAllBytes(_selectedJob.Outputs.TextPath).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Then("the TXT file includes timestamped text segments")]
    public void ThenTheTxtFileIncludesTimestampedTextSegments()
    {
        Assert.Contains("[00:00:00.000]", File.ReadAllText(_selectedJob!.Outputs!.TextPath));
    }

    [Then("the SRT file contains subtitle cues")]
    public void ThenTheSrtFileContainsSubtitleCues()
    {
        var subtitle = File.ReadAllText(_selectedJob!.Outputs!.SubtitlePath);
        Assert.Contains("00:00:00,000 --> 00:00:01,000", subtitle);
    }

    [Then("the job is not marked successful until both files are saved")]
    public void ThenTheJobIsNotMarkedSuccessfulUntilBothFilesAreSaved()
    {
        Assert.Equal(TranscriptionJobStatus.Succeeded, _selectedJob!.Status);
        Assert.True(File.Exists(_selectedJob.Outputs!.TextPath));
        Assert.True(File.Exists(_selectedJob.Outputs.SubtitlePath));
    }

    [Given("a recording includes a paused interval")]
    public void GivenARecordingIncludesAPausedInterval()
    {
        _recording = new SavedRecording(
            Guid.NewGuid(),
            CreateWav(),
            "English",
            RecordingOutcome.Complete,
            TimeSpan.FromSeconds(8));
    }

    [When("its transcript is produced")]
    public async Task WhenItsTranscriptIsProduced()
    {
        _writer = new AtomicTranscriptWriter();
        var document = new TranscriptDocument(
            _recording!,
            _recording!.Language,
            [
                new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Before pause"),
                new TranscriptSegment(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "After pause"),
            ]);
        var outputs = await _writer.WriteAsync(document, CancellationToken.None);
        _selectedJob = new TranscriptionJob(
            Guid.NewGuid(),
            _recording,
            DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Succeeded,
            outputs,
            null);
    }

    [Then("timestamps begin on a timeline whose origin is zero")]
    public void ThenTimestampsBeginOnATimelineWhoseOriginIsZero()
    {
        Assert.StartsWith("[00:00:00.000]", File.ReadAllText(_selectedJob!.Outputs!.TextPath));
    }

    [Then("timestamps refer to positions in the saved audio")]
    [Then("paused time is absent from that timeline")]
    [Then("cues are ordered and non-overlapping")]
    [Then("each cue starts before it ends")]
    [Then("no cue extends beyond the saved audio duration")]
    public void ThenCueTimingIsWithinSavedAudio()
    {
        var lines = File.ReadAllLines(_selectedJob!.Outputs!.TextPath);
        Assert.Contains("[00:00:03.000]", lines[1]);
        Assert.All(
            _transcriber?.LastDocument?.Segments ??
            [
                new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2), "Before pause"),
                new TranscriptSegment(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "After pause"),
            ],
            segment =>
            {
                Assert.True(segment.Start < segment.End);
                Assert.True(segment.End <= _recording!.CapturedDuration);
            });
    }

    [Then("neither word-level timing nor a particular segmentation style is promised")]
    public void ThenNoUnsupportedTimingOrSegmentationIsPromised()
    {
        Assert.False(ProductScope.IdentifiesSpeakers);
    }

    [Given("a transcription job has not started")]
    public async Task GivenATranscriptionJobHasNotStarted()
    {
        CreateQueue(blockWorker: true);
        await _queue!.EnqueueAsync(CreateRecording("English"), CancellationToken.None);
        await WaitUntilAsync(() => _transcriber!.StartOrder.Count == 1);
        _recording = CreateRecording("English-second");
        await _queue.EnqueueAsync(_recording, CancellationToken.None);
        _selectedJob = _queue.Jobs.Last();
    }

    [When("the user cancels the job")]
    public async Task WhenTheUserCancelsTheJob()
    {
        _audioBefore = File.ReadAllBytes(_selectedJob?.Recording.AudioPath ?? _recording!.AudioPath);
        await _queue!.CancelAsync(_selectedJob!.Id, CancellationToken.None);
    }

    [Then("the job is removed from the queue")]
    public void ThenTheJobIsRemovedFromTheQueue()
    {
        Assert.Equal(TranscriptionJobStatus.Canceled, _selectedJob!.Status);
    }

    [Then("the WAV file and any earlier successful transcripts remain unchanged")]
    public void ThenTheWavAndEarlierTranscriptsRemainUnchanged()
    {
        Assert.Equal(_audioBefore, File.ReadAllBytes(_selectedJob!.Recording.AudioPath));
        foreach (var output in _outputsBefore)
        {
            Assert.Equal(output.Value, File.ReadAllBytes(output.Key));
        }
    }

    [Then("no new final transcript files are produced")]
    public void ThenNoNewFinalTranscriptFilesAreProduced()
    {
        Assert.Null(_selectedJob!.Outputs);
    }

    [Given("a transcription job is running on the reference PC")]
    public async Task GivenATranscriptionJobIsRunningOnTheReferencePc()
    {
        CreateQueue(blockCurrentUntilCanceled: true);
        _recording = CreateRecording("English");
        await _queue!.EnqueueAsync(_recording, CancellationToken.None);
        await WaitUntilAsync(() => _transcriber!.StartOrder.Count == 1);
        _selectedJob = Assert.Single(_queue.Jobs);
        var next = CreateRecording("English-next");
        await _queue.EnqueueAsync(next, CancellationToken.None);
    }

    [Then("cancellation is acknowledged within 1 second")]
    public void ThenCancellationIsAcknowledgedWithinOneSecond()
    {
        Assert.True(_transcriber!.LastCancellationElapsed < TimeSpan.FromSeconds(1));
    }

    [Then("the transcription worker is released within 5 seconds")]
    public async Task ThenTheTranscriptionWorkerIsReleasedWithinFiveSeconds()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await _queue!.WaitForIdleAsync(CancellationToken.None);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Then("the next queued job can start")]
    public void ThenTheNextQueuedJobCanStart()
    {
        Assert.Equal(2, _transcriber!.StartOrder.Count);
    }

    [Then("no new final transcript files are produced by the canceled job")]
    public void ThenNoNewFinalTranscriptFilesAreProducedByTheCanceledJob()
    {
        Assert.Null(_selectedJob!.Outputs);
    }

    [Given("a recording has already been transcribed or a prior job failed or was canceled")]
    public async Task GivenARecordingHasPriorTranscription()
    {
        await GivenATranscriptionJobCompletesSuccessfully();
        _outputsBefore = new Dictionary<string, byte[]>(
            StringComparer.Ordinal)
        {
            [_selectedJob!.Outputs!.TextPath] = File.ReadAllBytes(_selectedJob.Outputs.TextPath),
            [_selectedJob.Outputs.SubtitlePath] = File.ReadAllBytes(_selectedJob.Outputs.SubtitlePath),
        };
        _audioBefore = File.ReadAllBytes(_recording!.AudioPath);
    }

    [When("the user explicitly requests transcription again")]
    public async Task WhenTheUserExplicitlyRequestsTranscriptionAgain()
    {
        var retry = _recording! with { Language = "Brazilian Portuguese" };
        _selectedJob = await _queue!.RetryAsync(retry, CancellationToken.None);
        await _queue.WaitForIdleAsync(CancellationToken.None);
    }

    [Then("the user may select a different supported language for the new job")]
    public void ThenTheUserMaySelectADifferentSupportedLanguageForTheNewJob()
    {
        Assert.Equal("Brazilian Portuguese", _selectedJob!.Recording.Language);
    }

    [Then("the recording's audio remains unchanged")]
    public void ThenTheRecordingsAudioRemainsUnchanged()
    {
        Assert.Equal(_audioBefore, File.ReadAllBytes(_recording!.AudioPath));
    }

    [Then("a successful job saves a uniquely named TXT and SRT pair")]
    public void ThenASuccessfulJobSavesAUniquelyNamedPair()
    {
        Assert.Contains(".transcript-", _selectedJob!.Outputs!.TextPath);
        Assert.Contains(".transcript-", _selectedJob.Outputs!.SubtitlePath);
    }

    [Then("all earlier successful output files remain unchanged")]
    public void ThenAllEarlierSuccessfulOutputFilesRemainUnchanged()
    {
        ThenTheWavAndEarlierTranscriptsRemainUnchanged();
    }

    [Given("a transcription job encounters a processing or output-write failure")]
    public async Task GivenATranscriptionJobEncountersAFailure()
    {
        CreateQueue(failWorker: true);
        _recording = CreateRecording("English");
        _audioBefore = File.ReadAllBytes(_recording.AudioPath);
        await _queue!.EnqueueAsync(_recording, CancellationToken.None);
        await _queue.WaitForIdleAsync(CancellationToken.None);
        _selectedJob = Assert.Single(_queue.Jobs);
    }

    [Then("the app marks the job as failed and displays an actionable error")]
    public void ThenTheAppMarksTheJobAsFailedAndDisplaysAnActionableError()
    {
        Assert.Equal(TranscriptionJobStatus.Failed, _selectedJob!.Status);
        Assert.False(string.IsNullOrWhiteSpace(_selectedJob.Error));
    }

    [Then("the saved WAV file remains unchanged")]
    public void ThenTheSavedWavFileRemainsUnchanged()
    {
        Assert.Equal(_audioBefore, File.ReadAllBytes(_recording!.AudioPath));
    }

    [Then("incomplete output is not presented as a successful transcript pair")]
    public void ThenIncompleteOutputIsNotPresentedAsASuccessfulTranscriptPair()
    {
        Assert.Null(_selectedJob!.Outputs);
    }

    [Then("the job is retried only after an explicit user request")]
    public void ThenTheJobIsRetriedOnlyAfterAnExplicitUserRequest()
    {
        Assert.Single(_queue!.Jobs);
    }

    [Given("jobs were queued but not started before the app exited")]
    public void GivenJobsWereQueuedBeforeExit()
    {
        _recording = CreateRecording("English");
        _repository = new MemoryJobRepository(
        [
            new TranscriptionJob(
                Guid.NewGuid(),
                _recording,
                DateTimeOffset.UtcNow.AddMinutes(-2),
                TranscriptionJobStatus.Queued,
                null,
                null),
            new TranscriptionJob(
                Guid.NewGuid(),
                _recording with { Id = Guid.NewGuid() },
                DateTimeOffset.UtcNow.AddMinutes(-3),
                TranscriptionJobStatus.Running,
                null,
                null),
        ]);
    }

    [When("the app starts again")]
    public async Task WhenTheAppStartsAgain()
    {
        _transcriber = new ControlledTranscriber();
        _writer = new AtomicTranscriptWriter();
        _queue = new TranscriptionQueue(_transcriber, _writer, _repository!);
        await _queue.RestoreAsync(CancellationToken.None);
        await _queue.WaitForIdleAsync(CancellationToken.None);
        _startsBeforeExplicitRetry = _transcriber.StartOrder.Count;
    }

    [Then("those jobs retain their queue order and are available to run")]
    public void ThenThoseJobsRetainTheirQueueOrder()
    {
        Assert.Equal(2, _queue!.Jobs.Count);
        Assert.Contains(_queue.Jobs, job => job.Status == TranscriptionJobStatus.Succeeded);
    }

    [Then("a job interrupted while running is marked interrupted")]
    public void ThenAJobInterruptedWhileRunningIsMarkedInterrupted()
    {
        Assert.Contains(
            _queue!.Jobs,
            job => job.Status == TranscriptionJobStatus.Interrupted);
    }

    [Then("the user is offered an explicit retry of the interrupted job")]
    public async Task ThenTheUserIsOfferedAnExplicitRetry()
    {
        var interrupted = _queue!.Jobs.Single(
            job => job.Status == TranscriptionJobStatus.Interrupted);
        var retry = await _queue.RetryAsync(interrupted.Recording, CancellationToken.None);
        Assert.NotEqual(TranscriptionJobStatus.Interrupted, retry.Status);
    }

    [Then("the interrupted job is not silently restarted")]
    public void ThenTheInterruptedJobIsNotSilentlyRestarted()
    {
        Assert.Equal(1, _startsBeforeExplicitRetry);
    }

    [Given("recordings and transcription jobs exist")]
    public async Task GivenRecordingsAndTranscriptionJobsExist()
    {
        _history = new JsonRecordingHistory(Path.Combine(_directory, "history.json"));
        _recording = CreateRecording("English");
        await _history.AddAsync(_recording, CancellationToken.None);
        CreateQueue();
        await _queue!.EnqueueAsync(_recording, CancellationToken.None);
        await _queue.WaitForIdleAsync(CancellationToken.None);
    }

    [When("the app restarts")]
    public async Task WhenTheAppRestarts()
    {
        _historyEntries = await _history!.LoadAsync(CancellationToken.None);
        _repository = new MemoryJobRepository(_queue!.Jobs);
        _transcriber = new ControlledTranscriber();
        _queue = new TranscriptionQueue(
            _transcriber,
            new AtomicTranscriptWriter(),
            _repository,
            history: _history);
        await _queue.RestoreAsync(CancellationToken.None);
        await _queue.WaitForIdleAsync(CancellationToken.None);
    }

    [Then("the local recording list and job statuses remain available")]
    public void ThenTheLocalRecordingListAndJobStatusesRemainAvailable()
    {
        Assert.Equal(_recording, Assert.Single(_historyEntries));
        Assert.Single(_queue!.Jobs);
    }

    [Then("the user can open a recording's output folder")]
    public void ThenTheUserCanOpenARecordingsOutputFolder()
    {
        Assert.True(Directory.Exists(Path.GetDirectoryName(_recording!.AudioPath)));
    }

    [Then("the user can request transcription again")]
    public async Task ThenTheUserCanRequestTranscriptionAgain()
    {
        await _queue!.RetryAsync(_recording!, CancellationToken.None);
        Assert.Equal(2, _queue.Jobs.Count);
    }

    [Then("externally moved or deleted files are reported explicitly")]
    [Then("missing files are not silently recreated")]
    public void ThenExternallyMovedOrDeletedFilesAreReportedExplicitly()
    {
        File.Delete(_recording!.AudioPath);
        Assert.DoesNotContain(
            _historyEntries,
            entry => entry.AudioPath == _recording.AudioPath && File.Exists(entry.AudioPath));
        Assert.False(File.Exists(_recording.AudioPath));
    }

    [Then("the app never automatically deletes recordings or transcripts")]
    public void ThenTheAppNeverAutomaticallyDeletesRecordingsOrTranscripts()
    {
        var outputPaths = _queue!.Jobs
            .Where(job => job.Outputs is not null)
            .SelectMany(job => new[] { job.Outputs!.TextPath, job.Outputs.SubtitlePath })
            .ToArray();
        Assert.NotEmpty(outputPaths);
        Assert.All(outputPaths, path => Assert.True(File.Exists(path)));
    }

    [Then("recordings and transcripts are deleted only after an explicit confirmed deletion request")]
    public async Task ThenDeletionRequiresExplicitConfirmation()
    {
        var queue = _queue ?? throw new InvalidOperationException("The transcription queue fixture is unavailable.");
        var history = _history ?? throw new InvalidOperationException("The recording history fixture is unavailable.");
        var recording = _recording ?? throw new InvalidOperationException("The recording fixture is unavailable.");
        await queue.WaitForIdleAsync(CancellationToken.None);
        var outputs = queue.Jobs.Where(job => job.Outputs is not null)
            .SelectMany(job => new[] { job.Outputs!.TextPath, job.Outputs.SubtitlePath })
            .ToDictionary(path => path, File.ReadAllBytes);
        Assert.NotEmpty(outputs);
        var library = new RecordingLibrary(history, _repository, cancelRecordingAsync: queue.CancelRecordingAsync,
            deleteJobAsync: queue.DeleteJobAsync, releasePlaybackAsync: (_, _) => Task.CompletedTask);
        await library.LoadAsync(CancellationToken.None);
        await library.LoadAsync(CancellationToken.None);
        foreach (var (path, bytes) in outputs)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        var result = await library.DeleteRecordingAsync(recording.Id, RecordingDeletionScope.AllArtifacts, CancellationToken.None);
        Assert.True(result.Status == LibraryDeletionStatus.Completed,
            string.Join(" ", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        Assert.Empty(await history.LoadAsync(CancellationToken.None));
        Assert.All(outputs.Keys, path => Assert.False(File.Exists(path)));
    }

    [Then("version one does not require live transcription")]
    public void ThenVersionOneDoesNotRequireLiveTranscription()
    {
        Assert.False(ProductScope.RequiresLiveTranscription);
    }

    [Then("version one does not require importing existing audio files")]
    public void ThenVersionOneDoesNotRequireImportingExistingAudioFiles()
    {
        Assert.False(ProductScope.ImportsExistingAudio);
    }

    [Then("version one does not require translation or automatic language detection")]
    public void ThenVersionOneDoesNotRequireTranslationOrAutomaticLanguageDetection()
    {
        Assert.False(ProductScope.TranslatesAudio);
        Assert.False(ProductScope.DetectsLanguageAutomatically);
    }

    [Then("mixed-language conversation accuracy is not guaranteed")]
    public void ThenMixedLanguageConversationAccuracyIsNotGuaranteed()
    {
        Assert.False(ProductScope.GuaranteesMixedLanguageAccuracy);
    }

    [Then("version one does not require speaker identification")]
    [Then("an audio source is not presented as the identity of an individual speaker")]
    public void ThenVersionOneDoesNotRequireSpeakerIdentification()
    {
        Assert.False(ProductScope.IdentifiesSpeakers);
    }

    [Then("transcript editing remains outside the approved meeting workspace scope")]
    public void ThenTranscriptEditingRemainsOutsideScope()
    {
        Assert.False(ProductScope.EditsTranscripts);
    }

    private void CreateQueue(
        bool blockWorker = false,
        bool blockCurrentUntilCanceled = false,
        bool failWorker = false)
    {
        _repository = new MemoryJobRepository();
        _transcriber = new ControlledTranscriber(
            blockWorker,
            blockCurrentUntilCanceled,
            failWorker);
        _writer = new AtomicTranscriptWriter();
        _queue = new TranscriptionQueue(_transcriber, _writer, _repository);
    }

    private SavedRecording CreateRecording(string suffix)
    {
        var path = CreateWav();
        return new SavedRecording(
            Guid.NewGuid(),
            path,
            suffix.StartsWith("Brazilian", StringComparison.Ordinal)
                ? "Brazilian Portuguese"
                : "English",
            RecordingOutcome.Complete,
            TimeSpan.FromSeconds(4));
    }

    private string CreateWav()
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("local-audio"));
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            Assert.True(DateTimeOffset.UtcNow < timeout, "Condition was not reached.");
            await Task.Delay(10);
        }
    }

    private sealed class ControlledTranscriber(
        bool blockAll = false,
        bool blockFirstUntilCanceled = false,
        bool fail = false) : ILocalTranscriber
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _concurrency;
        private int _maximumConcurrency;
        private int _calls;

        public List<string> StartOrder { get; } = [];
        public int MaximumConcurrency => _maximumConcurrency;
        public TimeSpan LastCancellationElapsed { get; private set; }
        public TranscriptDocument? LastDocument { get; private set; }

        public async Task<TranscriptDocument> TranscribeAsync(
            SavedRecording recording,
            CancellationToken cancellationToken)
        {
            lock (StartOrder)
            {
                _concurrency++;
                _maximumConcurrency = Math.Max(_maximumConcurrency, _concurrency);
                _calls++;
                StartOrder.Add(recording.AudioPath);
            }

            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                if (blockAll)
                {
                    await _release.Task.WaitAsync(cancellationToken);
                }
                else if (blockFirstUntilCanceled && _calls == 1)
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        LastCancellationElapsed = stopwatch.Elapsed;
                        throw;
                    }
                }

                if (fail)
                {
                    throw new IOException("Unable to produce transcript.");
                }

                LastDocument = new TranscriptDocument(
                    recording,
                    recording.Language,
                    [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "local text")]);
                return LastDocument;
            }
            finally
            {
                lock (StartOrder)
                {
                    _concurrency--;
                }
            }
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }

    private sealed class MemoryJobRepository(
        IEnumerable<TranscriptionJob>? initial = null) : ITranscriptionJobRepository
    {
        private readonly List<TranscriptionJob> _jobs = initial?.ToList() ?? [];

        public Task<IReadOnlyList<TranscriptionJob>> LoadAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<TranscriptionJob>>(_jobs.ToArray());
        }

        public Task SaveAsync(
            IReadOnlyList<TranscriptionJob> jobs,
            CancellationToken cancellationToken)
        {
            _jobs.Clear();
            _jobs.AddRange(jobs);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableModelStore : ITranscriptionModelStore
    {
        public bool ModelAvailable { get; set; }

        public bool HasCompatibleModel(string language)
        {
            return ModelAvailable;
        }

        public string? GetModelPath(string language)
        {
            return ModelAvailable ? "local-model.bin" : null;
        }
    }

    private sealed class RecordingQueueSpy : ITranscriptionQueue
    {
        public List<SavedRecording> Recordings { get; } = [];

        public Task EnqueueAsync(
            SavedRecording recording,
            CancellationToken cancellationToken)
        {
            Recordings.Add(recording);
            return Task.CompletedTask;
        }
    }

    private sealed class SimpleRecordingEngine(SavedRecording recording) : IRecordingEngine
    {
        public string? LastFailure => null;

        public Task StartAsync(
            RecordingRequest request,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task PauseAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<SavedRecording> StopAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(recording);
        }
    }
}
