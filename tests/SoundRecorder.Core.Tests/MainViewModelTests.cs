using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging.Abstractions;
using SoundRecorder.Core;
using SoundRecorder.Desktop;
using SoundRecorder.Infrastructure.Audio;
using SoundRecorder.Infrastructure.Models;
using SoundRecorder.Infrastructure.Persistence;

namespace SoundRecorder.Core.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task Refresh_WhenJobsCollectionResets_PreservesSelectedJob()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var recording = new SavedRecording(
            Guid.NewGuid(),
            Path.Combine(directory, "recording.wav"),
            "English",
            RecordingOutcome.Complete,
            TimeSpan.FromSeconds(1));
        var outputs = new TranscriptOutputs(
            Path.Combine(directory, "recording.txt"),
            Path.Combine(directory, "recording.srt"));
        var job = new TranscriptionJob(
            Guid.NewGuid(),
            recording,
            DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Succeeded,
            outputs,
            null);
        var repository = new JobRepository(job);
        var queue = new TranscriptionQueue(
            new UnusedTranscriber(),
            new UnusedWriter(),
            repository);
        await queue.RestoreAsync(CancellationToken.None);
        var recordingEngine = new WindowsRecordingEngine(directory);
        var modelManager = new LocalTranscriptionModelManager(
            Path.Combine(directory, "models"),
            new HttpClient());
        var viewModel = new MainViewModel(
            new RecorderApplication(recordingEngine, queue, modelManager),
            recordingEngine,
            queue,
            modelManager,
            new JsonRecordingHistory(Path.Combine(directory, "history.json")),
            new RecoveryManager(new EmptyRecoveryStorage()),
            new WindowsAudioDeviceCatalog(),
            NullLogger<MainViewModel>.Instance);
        viewModel.Refresh();
        viewModel.SelectedJob = job;
        viewModel.Jobs.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                viewModel.SelectedJob = null;
            }
        };

        viewModel.Refresh();

        Assert.Same(job, viewModel.SelectedJob);
    }

    [Fact]
    public void ViewModelExposesAsyncCommandsForLongRunningOperations()
    {
        var members = typeof(MainViewModel)
            .GetProperties()
            .Where(property =>
                typeof(IAsyncRelayCommand).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(nameof(MainViewModel.InitializeCommand), members);
        Assert.Contains(nameof(MainViewModel.ImportModelCommand), members);
        Assert.Contains(nameof(MainViewModel.DownloadModelCommand), members);
        Assert.Contains(nameof(MainViewModel.StartCommand), members);
        Assert.Contains(nameof(MainViewModel.StopCommand), members);
        Assert.Contains(nameof(MainViewModel.CancelJobCommand), members);
        Assert.Contains(nameof(MainViewModel.RetryTranscriptionCommand), members);
    }

    [Fact]
    public async Task ViewTranscriptCommand_EmptyTranscript_ProvidesVisibleExplanation()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var textPath = Path.Combine(directory, "recording.txt");
        await File.WriteAllTextAsync(textPath, string.Empty);
        var recording = new SavedRecording(
            Guid.NewGuid(),
            Path.Combine(directory, "recording.wav"),
            "English",
            RecordingOutcome.Complete,
            TimeSpan.FromSeconds(1));
        var job = new TranscriptionJob(
            Guid.NewGuid(),
            recording,
            DateTimeOffset.UtcNow,
            TranscriptionJobStatus.Succeeded,
            new TranscriptOutputs(textPath, Path.Combine(directory, "recording.srt")),
            null);
        var viewModel = await CreateViewModelAsync(directory, job);
        viewModel.SelectedJob = job;

        await viewModel.ViewTranscriptCommand.ExecuteAsync(null);

        Assert.Equal(
            "No speech was recognized in this recording.",
            viewModel.Transcript);
    }

    private static async Task<MainViewModel> CreateViewModelAsync(
        string directory,
        TranscriptionJob job)
    {
        var repository = new JobRepository(job);
        var queue = new TranscriptionQueue(
            new UnusedTranscriber(),
            new UnusedWriter(),
            repository);
        await queue.RestoreAsync(CancellationToken.None);
        var recordingEngine = new WindowsRecordingEngine(directory);
        var modelManager = new LocalTranscriptionModelManager(
            Path.Combine(directory, "models"),
            new HttpClient());
        return new MainViewModel(
            new RecorderApplication(recordingEngine, queue, modelManager),
            recordingEngine,
            queue,
            modelManager,
            new JsonRecordingHistory(Path.Combine(directory, "history.json")),
            new RecoveryManager(new EmptyRecoveryStorage()),
            new WindowsAudioDeviceCatalog(),
            NullLogger<MainViewModel>.Instance);
    }

    private sealed class JobRepository(TranscriptionJob job) : ITranscriptionJobRepository
    {
        public Task<IReadOnlyList<TranscriptionJob>> LoadAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<TranscriptionJob>>([job]);
        }

        public Task SaveAsync(
            IReadOnlyList<TranscriptionJob> jobs,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyRecoveryStorage : IRecoveryStorage
    {
        public Task<IReadOnlyList<RecoveryCandidate>> FindAsync(
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<RecoveryCandidate>>([]);
        }

        public Task<SavedRecording> RecoverAsync(
            RecoveryCandidate candidate,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task DiscardAsync(
            RecoveryCandidate candidate,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedTranscriber : ILocalTranscriber
    {
        public Task<TranscriptDocument> TranscribeAsync(
            SavedRecording recording,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class UnusedWriter : ITranscriptWriter
    {
        public Task<TranscriptOutputs> WriteAsync(
            TranscriptDocument document,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
