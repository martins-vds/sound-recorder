using System.Diagnostics;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Reqnroll;
using SoundRecorder.Infrastructure.Persistence;
using SoundRecorder.Infrastructure.Transcription;
using static SoundRecorder.Core.Tests.TranscriptionJobManagementTests;

namespace SoundRecorder.Core.Tests.Steps;

[Binding]
[Scope(Feature = "Manage distinct transcription attempts and recognition progress")]
public sealed class TranscriptionJobManagementSteps
{
    private readonly string _directory = Path.Combine(Environment.CurrentDirectory, "artifacts", $"job-management-{Guid.NewGuid():N}");
    private readonly Models _models = new();
    private readonly Repository _repository = new();
    private ControlledTranscriber _worker = new();
    private TranscriptionQueue _queue = null!;
    private JsonRecordingHistory _history = null!;
    private SavedRecording _recording = null!;
    private TranscriptionJob _selected = null!;
    private TranscriptionJob? _queued;
    private TranscriptionJob? _new;
    private GatedWriter? _blockedWriter;
    private readonly ConcurrentQueue<int?> _estimates = new();
    private readonly Dictionary<string, byte[]> _original = [];
    private Stopwatch? _stop;
    private Exception? _error;
    private bool _realWorker;
    private TranscriptionJob? _earlier;

    [BeforeScenario]
    public async Task Setup()
    {
        Directory.CreateDirectory(_directory);
        var audio = Path.Combine(_directory, "recording.wav");
        await File.WriteAllBytesAsync(audio, Encoding.UTF8.GetBytes("disposable captured audio"));
        _recording = new(Guid.NewGuid(), audio, "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        _history = new(Path.Combine(_directory, "history.json"));
        await _history.AddAsync(_recording, default);
        _original[audio] = await File.ReadAllBytesAsync(audio);
        CreateQueue();
    }

    private void CreateQueue(ITranscriptWriter? writer = null, ILocalTranscriber? transcriber = null,
        ITranscriptionModelStore? models = null) =>
        _queue = new(transcriber ?? _worker, writer ?? new AtomicTranscriptWriter(), _repository,
            models: models ?? _models, history: _history);

    private sealed class GatedWriter : ITranscriptWriter
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Release { get; init; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TranscriptOutputs> WriteAsync(TranscriptDocument document, CancellationToken token)
        {
            Calls++;
            await Release.Task.WaitAsync(token);
            return await new AtomicTranscriptWriter().WriteAsync(document, token);
        }
    }

    private async Task Start()
    {
        await _queue.EnqueueAsync(_recording, default);
        await _worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _selected = _queue.Jobs.Last();
    }

    private async Task SeedEarlier()
    {
        var outputs = await new AtomicTranscriptWriter().WriteAsync(
            new(_recording, _recording.Language, [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "earlier transcript")]), default);
        _earlier = new(Guid.NewGuid(), _recording, DateTimeOffset.UtcNow.AddMinutes(-1),
            TranscriptionJobStatus.Succeeded, outputs, null);
        await _history.RegisterOutputsAsync(_recording.Id, new(_earlier.Id, outputs), default);
        _repository.Stored = [_earlier];
        foreach (var path in new[] { outputs.TextPath, outputs.SubtitlePath })
            _original[path] = await File.ReadAllBytesAsync(path);
    }

    [Given("a job was queued with Base original and English")]
    public async Task QueuedChoice()
    {
        _models.SelectedModelId = "Tiny original";
        await StartPortuguese();
        _models.SelectedModelId = "Base original";
        await _queue.EnqueueAsync(_recording, default);
        _queued = _queue.Jobs.Last();
    }

    private async Task StartPortuguese()
    {
        await _queue.EnqueueAsync(_recording with { Language = "Brazilian Portuguese" }, default);
        await _worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _selected = _queue.Jobs.First();
    }

    [Given("another job is running with Tiny original and Brazilian Portuguese")]
    public void RunningChoice()
    {
        Assert.Equal(TranscriptionJobStatus.Running, _selected.Status);
        Assert.Equal("Tiny original", _selected.ModelId);
        Assert.Equal("Brazilian Portuguese", _selected.Language);
    }

    [When("the user selects Small Q5_1 and Brazilian Portuguese for new jobs")]
    public void ChangeSelection() => _models.SelectedModelId = "Small Q5_1";

    [Then("the queued job retains Base original and English")]
    public void QueuedRetained()
    {
        Assert.Equal("Base original", _queued!.ModelId);
        Assert.Equal("English", _queued.Language);
    }

    [Then("the running job retains Tiny original and Brazilian Portuguese")]
    public void RunningRetained() => RunningChoice();

    [When("the user requests another transcription")]
    public async Task NewAttempt() => _new = await _queue.RetryAsync(_recording with { Language = "Brazilian Portuguese" }, default);

    [Then("only the new job snapshots Small Q5_1 and Brazilian Portuguese")]
    public void NewChoice()
    {
        Assert.Equal("Small Q5_1", _new!.ModelId);
        Assert.Equal("Brazilian Portuguese", _new.Language);
        QueuedRetained();
        RunningRetained();
    }

    [Given("a queued job retains a model that has become unavailable")]
    public async Task MissingChoice()
    {
        await SeedEarlier();
        await _queue.RestoreAsync(default);
        await Start();
        await _queue.EnqueueAsync(_recording, default);
        _queued = _queue.Jobs.Last();
        _models.Missing = true;
    }

    [Given("a different compatible model is currently selected")]
    public void DifferentSelected() => _models.SelectedModelId = "Different compatible model";

    [When("the queued job attempts to start")]
    public async Task AttemptMissing()
    {
        _worker.Release.TrySetResult();
        await _queue.WaitForIdleAsync(default);
    }

    [Then("that job reports an actionable missing-model failure")]
    public void MissingFailure()
    {
        Assert.Equal(TranscriptionJobStatus.Failed, _queued!.Status);
        Assert.Contains("model", _queued.Error);
        Assert.Contains("new attempt", _queued.Error);
    }

    [Then("the different selected model is not substituted")]
    public void NoSubstitution()
    {
        Assert.Equal(1, _worker.Calls);
        Assert.Equal("base-original", _queued!.ModelId);
    }

    [Then("its recording and earlier completed outputs remain unchanged")]
    [Then("the WAV and all earlier successful outputs remain unchanged")]
    public async Task ArtifactsUnchanged()
    {
        foreach (var (path, bytes) in _original) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Given("a controlled owned worker is processing a job")]
    public void ControlledOwnedWorker() => _realWorker = true;

    [When("it reports recognition percentages 0, 37, and 84 through progress messages")]
    public async Task RealProgress()
    {
        Assert.True(_realWorker);
        var script = """
            $r=[Console]::ReadLine()|ConvertFrom-Json
            foreach($p in 0,37,84){
              @{Kind='Progress';JobId=$r.JobId;Progress=@{Phase=1;Percent=$p}}|ConvertTo-Json -Compress|%{[Console]::WriteLine($_)}
              Start-Sleep -Milliseconds 100
            }
            Start-Sleep -Seconds 30
            """;
        var transcriber = new WhisperProcessTranscriber("controlled", _models,
            _ => WindowsTranscriptionWorker.Start("controlled", WindowsProcessJob.Create, start =>
            {
                start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                start.ArgumentList.Clear();
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-EncodedCommand");
                start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
                return Process.Start(start);
            }));
        CreateQueue(transcriber: transcriber);
        _queue.ProgressChanged += (_, value) =>
        {
            if (value.Progress.Phase == TranscriptionPhase.Recognizing)
                _estimates.Enqueue(value.Progress.Percent);
        };
        await _queue.EnqueueAsync(_recording, default);
        _selected = _queue.Jobs.Last();
        await WaitUntilAsync(() => _estimates.Count == 3 || _selected.Status == TranscriptionJobStatus.Failed);
    }

    [Then("the public job observer receives those estimates for that job")]
    public void Estimates() => Assert.Equal(new int?[] { 0, 37, 84 }, _estimates);

    [Then("the job remains running")]
    [Then("no progress message is parsed as a final transcript result")]
    [Then("the job is not marked successful")]
    public void NotSuccessful()
    {
        Assert.Equal(TranscriptionJobStatus.Running, _selected.Status);
        Assert.Null(_selected.Outputs);
    }

    [Then("no final TXT or SRT output is published yet")]
    [Then("no new final outputs from the stopped attempt are published")]
    [Then("no stopped-attempt output is published")]
    public void NoOutput()
    {
        Assert.Null(_selected.Outputs);
        foreach (var path in Directory.GetFiles(_directory, "*.txt").Concat(Directory.GetFiles(_directory, "*.srt")))
            Assert.Contains(path, _original.Keys);
    }

    [Given("a running job has reported 100 percent recognition progress")]
    public async Task Hundred()
    {
        _blockedWriter = new() { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        CreateQueue(_blockedWriter);
        await Start();
        _worker.Reporter!.Report(new(TranscriptionPhase.Recognizing, 100));
        Assert.Equal(100, _selected.Progress!.Percent);
    }

    [Given("processing or output publication is still unfinished")]
    public void Unfinished() => NotSuccessful();

    [When("processing finishes and both unique TXT and SRT files are published")]
    public async Task FinishPublication()
    {
        _worker.Release.TrySetResult();
        _blockedWriter!.Release!.TrySetResult();
        await _queue.WaitForIdleAsync(default);
        var pair = _selected.Outputs!;
        Assert.True(File.Exists(pair.TextPath));
        Assert.True(File.Exists(pair.SubtitlePath));
    }

    [Then("the job is marked successful")]
    public void Successful() => Assert.Equal(TranscriptionJobStatus.Succeeded, _selected.Status);

    [Given("a running job is in phase {string} without percentage information")]
    public async Task Phase(string phase)
    {
        if (phase == "Writing")
        {
            _blockedWriter = new() { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            CreateQueue(_blockedWriter);
        }
        await Start();
        if (phase == "Writing")
        {
            _worker.Release.TrySetResult();
            await WaitUntilAsync(() => _blockedWriter!.Calls == 1);
        }
        else if (phase == "Recognizing") _worker.Reporter!.Report(new(TranscriptionPhase.Recognizing));
    }

    [When("its public progress state is observed")]
    public void Observe() => Assert.NotNull(_selected.Progress);

    [Then("the phase is {string} and its percentage is unavailable")]
    public void Indeterminate(string phase)
    {
        Assert.Equal(Enum.Parse<TranscriptionPhase>(phase), _selected.Progress!.Phase);
        Assert.Null(_selected.Progress.Percent);
    }

    [Then("elapsed time is not converted into fabricated progress")]
    public async Task NoFabrication()
    {
        await Task.Delay(30);
        Assert.Null(_selected.Progress!.Percent);
    }

    private TaskCompletionSource? _ownedWorkerRelease;

    [Given("a transcription job is running on the reference PC")]
    public async Task OwnedRunning()
    {
        _realWorker = true;
        await SeedEarlier();
        var logger = new StartedLogger();
        _ownedWorkerRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var transcriber = new WhisperProcessTranscriber(
            Path.Combine(AppContext.BaseDirectory, "SoundRecorder.ProcessTestHost.exe"),
            new HangModels(), executable =>
                new ControlledReleaseWorker(WindowsTranscriptionWorker.Start(executable), _ownedWorkerRelease.Task), logger);
        CreateQueue(transcriber: transcriber, models: new HangModels());
        await _queue.RestoreAsync(default);
        // This is controlled real-process evidence, not native inference/reference-PC acceptance.
        _models.SelectedModelId = "hang";
        await _queue.EnqueueAsync(_recording, default);
        _selected = _queue.Jobs.Last();
        await logger.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ControlledReleaseWorker(ITranscriptionWorker worker, Task release) : ITranscriptionWorker
    {
        public int Id => worker.Id;
        public TextWriter Input => worker.Input;
        public TextReader Output => worker.Output;
        public Task<int> WaitForExitAsync(CancellationToken token) => worker.WaitForExitAsync(token);
        public void Abort() => worker.Abort();
        public async ValueTask DisposeAsync()
        {
            await release;
            await worker.DisposeAsync();
        }
    }

    private sealed class HangModels : ITranscriptionModelStore
    {
        public string? SelectedModelId => "hang";
        public bool HasCompatibleModel(string language) => true;
        public string? GetModelPath(string language) => "hang";
        public string? GetModelPath(string language, string? pinnedModelId) => "hang";
    }

    private sealed class StartedLogger : ILogger<WhisperProcessTranscriber>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (id.Id == 700) Started.TrySetResult();
        }
    }

    [Given("another job is queued")]
    public async Task AnotherQueued()
    {
        await _queue.EnqueueAsync(_recording, default);
        _queued = _queue.Jobs.Last();
    }

    [When("the user stops the running job")]
    public async Task Stop()
    {
        _stop = Stopwatch.StartNew();
        await _queue.StopAsync(_selected.Id, default);
    }

    [Then("the stop request is acknowledged within 1 second")]
    public void Acknowledged() => Assert.True(_stop!.Elapsed < TimeSpan.FromSeconds(1));

    [Then("restart and history deletion wait until its worker is released")]
    public async Task Gated()
    {
        Assert.True(_selected.IsTerminationPending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _queue.DeleteJobAsync(_selected.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _queue.RetryAsync(_recording, default));
    }

    [Then("its worker is released within 5 seconds")]
    public async Task Released()
    {
        _ownedWorkerRelease!.TrySetResult();
        await WaitUntilAsync(() => !_selected.IsTerminationPending);
        Assert.True(_stop!.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Then("the stopped job is marked interrupted with a user-stop reason")]
    public void Stopped()
    {
        Assert.Equal(TranscriptionJobStatus.Interrupted, _selected.Status);
        Assert.Equal("User requested Stop.", _selected.InterruptionReason);
    }

    [Then("the next queued job can start")]
    public async Task NextStarts() => await WaitUntilAsync(() => _queued!.Status != TranscriptionJobStatus.Queued);

    [Given("a job was stopped during recognition")]
    public async Task StoppedRecognition()
    {
        await SeedEarlier();
        await _queue.RestoreAsync(default);
        await Start();
        _worker.Reporter!.Report(new(TranscriptionPhase.Recognizing, 37));
        await _queue.StopAsync(_selected.Id, default);
        await _queue.WaitForIdleAsync(default);
    }

    [Given("a compatible downloaded model and language are selected")]
    public void RestartSelection() => _models.SelectedModelId = "Small Q5_1";

    [When("the user requests restart")]
    public async Task Restart()
    {
        _new = await _queue.RetryAsync(_recording with { Language = "Brazilian Portuguese" }, default);
        await WaitUntilAsync(() => _worker.Calls == 2);
    }

    [Then("a new job with a different ID is queued for the same recording")]
    public void Distinct()
    {
        Assert.NotEqual(_selected.Id, _new!.Id);
        Assert.Equal(_selected.Recording.Id, _new.Recording.Id);
    }

    [Then("it snapshots the then-selected model and language")]
    public void RestartChoice()
    {
        Assert.Equal("Small Q5_1", _new!.ModelId);
        Assert.Equal("Brazilian Portuguese", _new.Language);
    }

    [Then("it begins at the start of the saved audio")]
    public void FromBeginning()
    {
        Assert.Equal(_recording.AudioPath, _worker.Request!.Recording.AudioPath);
        Assert.Equal(_new!.Id, _worker.Request.JobId);
    }

    [Then("the stopped attempt and all earlier successful outputs remain in history")]
    public async Task EarlierRetained()
    {
        Assert.Contains(_queue.Jobs, job => job.Id == _selected.Id && job.Status == TranscriptionJobStatus.Interrupted);
        Assert.Equal(3, _queue.Jobs.Count);
        Assert.Contains(_queue.Jobs, job => job.Id == _earlier!.Id && job.Status == TranscriptionJobStatus.Succeeded);
        var history = Assert.Single(await _history.LoadAsync(default));
        Assert.Contains(history.TranscriptOutputs, owned => owned.JobId == _earlier!.Id);
        await ArtifactsUnchanged();
    }

    private IProgress<TranscriptionProgress>? _oldReporter;
    private TranscriptionProgress? _newProgress;

    [Given("one attempt has been stopped and a new attempt is running")]
    public async Task Replaced()
    {
        _worker.IgnoreCancellation = true;
        await Start();
        _oldReporter = _worker.Reporter;
        await _queue.StopAsync(_selected.Id, default);
        _worker.Release.TrySetResult();
        await _queue.WaitForIdleAsync(default);
        _worker = new();
        // Retain public jobs by restoring their persisted history, not by reaching inside a worker.
        CreateQueue();
        await _queue.RestoreAsync(default);
        _new = await _queue.RetryAsync(_recording, default);
        await _worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _worker.Reporter!.Report(new(TranscriptionPhase.Recognizing, 12));
        _newProgress = _new.Progress;
    }

    [When("delayed progress or a result arrives for the stopped attempt")]
    public void Delayed() => _oldReporter!.Report(new(TranscriptionPhase.Recognizing, 99));

    [Then("the new attempt's state and progress remain unchanged")]
    public void ReplacementUnchanged()
    {
        Assert.Equal(TranscriptionJobStatus.Running, _new!.Status);
        Assert.Equal(_newProgress, _new.Progress);
    }

    [Then("the terminated attempt is not reported successful")]
    public void OldNotSuccessful() => Assert.Equal(TranscriptionJobStatus.Interrupted, _selected.Status);

    [Given("a recording has a completed job and another queued attempt")]
    public async Task CompletedAndQueued()
    {
        _worker.Release.TrySetResult();
        await Start();
        await _queue.WaitForIdleAsync(default);
        Assert.Equal(TranscriptionJobStatus.Succeeded, _selected.Status);
        foreach (var path in new[] { _selected.Outputs!.TextPath, _selected.Outputs.SubtitlePath })
            _original[path] = await File.ReadAllBytesAsync(path);
        _worker = new();
        CreateQueue();
        await _queue.RestoreAsync(default);
        await _queue.EnqueueAsync(_recording with { Id = _recording.Id }, default);
        await _worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _queue.EnqueueAsync(_recording, default);
        _queued = _queue.Jobs.Last();
    }

    [When("the user deletes the completed job's history")]
    public async Task DeleteCompleted() => await _queue.DeleteJobAsync(_selected.Id, default);

    [Then("only that job-history entry is removed")]
    public void OnlyHistory()
    {
        Assert.DoesNotContain(_queue.Jobs, job => job.Id == _selected.Id);
        Assert.Equal(2, _queue.Jobs.Count);
        Assert.DoesNotContain(_repository.Stored, job => job.Id == _selected.Id);
    }

    [Then("its TXT and SRT pair remains owned by the recording")]
    [Then("later full recording deletion can still find that output pair")]
    public async Task IndependentOwnership()
    {
        var entry = Assert.Single(await _history.LoadAsync(default));
        Assert.Contains(entry.TranscriptOutputs, output => output.JobId == _selected.Id && output.Outputs == _selected.Outputs);
        await ArtifactsUnchanged();
    }

    [Then("the recording and queued attempt remain unchanged")]
    public void QueueUnchanged()
    {
        Assert.Equal(TranscriptionJobStatus.Queued, _queued!.Status);
        Assert.Equal(_recording, _queued.Recording);
    }

    [Given("a transcription job is running")]
    public async Task Running() => await Start();

    [When("deletion of that job's history is requested without stopping or cancelling it")]
    public async Task DeleteActive() => _error = await Record.ExceptionAsync(() => _queue.DeleteJobAsync(_selected.Id, default));

    [Then("the request reports that the job must be stopped or cancelled first")]
    public void RejectActive()
    {
        Assert.IsType<InvalidOperationException>(_error);
        Assert.Contains("Stop or cancel", _error!.Message);
    }

    [Then("the active job remains manageable and its history is retained")]
    public async Task Manageable()
    {
        Assert.Contains(_queue.Jobs, job => job.Id == _selected.Id);
        await _queue.CancelAsync(_selected.Id, default);
        await _queue.WaitForIdleAsync(default);
        Assert.Equal(TranscriptionJobStatus.Canceled, _selected.Status);
    }

    [AfterScenario]
    public async Task Cleanup()
    {
        try
        {
            _ownedWorkerRelease?.TrySetResult();
            foreach (var job in _queue.Jobs.Where(job => job.Status is TranscriptionJobStatus.Queued or TranscriptionJobStatus.Running))
                await _queue.CancelAsync(job.Id, default);
            _worker.Release.TrySetResult();
            _blockedWriter?.Release?.TrySetResult();
            await _queue.WaitForIdleAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { Directory.Delete(_directory, true); }
    }
}
