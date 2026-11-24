using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Threading;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using SoundRecorder.Core;
using SoundRecorder.Desktop;
using SoundRecorder.Infrastructure.Persistence;
using SoundRecorder.Infrastructure.Transcription;

namespace SoundRecorder.Core.Tests;

[Collection("Desktop environment")]
public sealed class MainWindowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SearchText_NonCancellableOldSnapshot_CannotReplaceNewResultsEvenWhenQueryMatchesAgain(bool repeatQuery) =>
        OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var alpha = context.Recording(Guid.NewGuid()) with { Title = "Alpha" };
            var beta = context.Recording(Guid.NewGuid()) with { Title = "Beta" };
            foreach (var recording in new[] { alpha, beta })
                await File.WriteAllBytesAsync(recording.AudioPath, [1, 2, 3]);
            context.History.Items.AddRange([alpha, beta]);
            var library = new DeferredSearchLibrary(new RecordingLibrary(context.History));
            var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models,
                context.History, new RecoveryManager(context.Recovery), context, context.Logger, library);
            viewModel.Recordings.Add(alpha);
            viewModel.Recordings.Add(beta);
            try
            {
                viewModel.SearchText = "Alpha";
                await library.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                viewModel.SearchText = "Beta";
                await UntilAsync(() => viewModel.FilteredRecordings.Count == 1 &&
                    viewModel.FilteredRecordings[0].Id == beta.Id);
                if (repeatQuery)
                {
                    var added = context.Recording(Guid.NewGuid()) with { Title = "Alpha follow-up" };
                    await File.WriteAllBytesAsync(added.AudioPath, [4, 5, 6]);
                    context.History.Items.Add(added);
                    viewModel.SearchText = "Alpha";
                    await UntilAsync(() => viewModel.FilteredRecordings.Count == 2);
                }
                var latest = viewModel.FilteredRecordings.Select(recording => recording.Id).ToArray();
                library.Release.SetResult();
                await UntilAsync(() => !viewModel.HasPendingViewWork);

                Assert.Equal(latest, viewModel.FilteredRecordings.Select(recording => recording.Id));
                Assert.Equal(string.Empty, viewModel.LibraryMessage);
                Assert.Empty(context.Logger.Errors);
                Assert.Empty(context.Queue.Jobs);
            }
            finally
            {
                library.Release.TrySetResult();
                await viewModel.PrepareViewCloseAsync();
            }
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task MeetingWorkspace_StopDistinguishesKnownWorkerErrorsWithoutWeakeningPhysicalRelease(bool knownFailure) => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        context.Transcriber.Block = true;
        context.Transcriber.CancellationError = knownFailure
            ? new AggregateException(new IOException("Worker cancellation release failed."),
                new UnauthorizedAccessException("Worker handle unavailable."))
            : new AggregateException(new IOException("Worker cancellation release failed."),
                new NullReferenceException("Unexpected worker defect."));
        context.Transcriber.CancellationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await context.Queue.EnqueueAsync(context.Engine.Result, CancellationToken.None);
        await context.Transcriber.Started.Task;
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            context.ViewModel.Refresh();
            context.ViewModel.SelectedJobRow = Assert.Single(context.ViewModel.JobRows);
            var failure = await Record.ExceptionAsync(() => context.ViewModel.StopJobCommand.ExecuteAsync(null));
            if (knownFailure)
            {
                Assert.Null(failure);
                Assert.Contains("could not be stopped", context.ViewModel.Message);
                Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Error &&
                    entry.Message.Contains(context.ViewModel.SelectedJob!.Id.ToString(), StringComparison.Ordinal));
            }
            else
            {
                Assert.IsType<AggregateException>(failure);
            }
        }
        finally
        {
            context.Transcriber.CancellationError = null;
            context.Transcriber.CancellationGate.TrySetResult();
            try { await context.Queue.WaitForIdleAsync(CancellationToken.None); }
            catch (AggregateException) { }
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_DeferredQueuePersistenceFailureKeepsSavedAudioAndDoesNotStartProcessing() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Engine.Result;
        await File.WriteAllBytesAsync(recording.AudioPath, [1, 2, 3]);
        var history = new JsonRecordingHistory(Path.Combine(context.Directory, "deferred-history.json"));
        var path = Path.Combine(context.Directory, "deferred-jobs.json");
        var repository = new JsonTranscriptionJobRepository(path);
        await repository.SaveAsync([], CancellationToken.None);
        var queue = new TranscriptionQueue(context.Transcriber, new AtomicTranscriptWriter(), repository, history: history);
        var recorder = new RecorderApplication(context.Engine, queue, context.Models, history);
        var viewModel = new MainViewModel(recorder, context.Engine, queue, context.Models, history,
            new RecoveryManager(context.Recovery), context, context.Logger);
        viewModel.DestinationFolder = context.Directory;
        var model = await context.Models.RegisterAsync("existing.zip", CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Models.Selecting = token => release.Task.WaitAsync(token);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            await viewModel.InitializeCommand.ExecuteAsync(null);
            viewModel.SelectedModel = model;
            await UntilAsync(() => viewModel.IsSelectingModel);
            viewModel.PermissionConfirmed = true;
            viewModel.SelectedMicrophoneId = "microphone";
            await viewModel.StartRecordingAsync(false);
            await viewModel.StopAsync(enqueueTranscription: true);
            using (var blocked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                release.SetResult();
                await UntilAsync(() => !viewModel.HasPendingViewWork);
                Assert.Contains("could not be queued", viewModel.Message);
                Assert.Equal(recording.Id, Assert.Single(viewModel.Recordings).Id);
                Assert.True(File.Exists(recording.AudioPath));
                Assert.False(context.Transcriber.Started.Task.IsCompleted);
                Assert.DoesNotContain(queue.Jobs, job => job.Status == TranscriptionJobStatus.Running);
                Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Error &&
                    entry.Message.Contains(recording.Id.ToString(), StringComparison.Ordinal));
            }
            Assert.Empty(await repository.LoadAsync(CancellationToken.None));
        }
        finally
        {
            release.TrySetResult();
            await queue.CancelRecordingAsync(recording.Id, CancellationToken.None);
            await queue.WaitForIdleAsync(CancellationToken.None);
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_ExitCancelsAndDrainsCatalogTransferWithoutReplacingTheSelectedModel() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        await context.Models.RegisterAsync("existing.zip", CancellationToken.None);
        context.Models.CurrentPackage = context.Models.Package;
        context.Models.CatalogGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        var entry = SoundRecorder.Infrastructure.Models.TranscriptionModelCatalog.Entries.Single(
            model => model.Key == "whisper.net:small:Q5_1");
        var download = context.ViewModel.DownloadCatalogModelCommand.ExecuteAsync(entry);
        try
        {
            await UntilAsync(() => context.ViewModel.ModelDownloadStatus == "Downloading");
            Assert.True(context.ViewModel.HasPendingViewWork);
            await window.RequestExitAsync();
            Assert.True(download.IsCompleted);
            Assert.False(context.ViewModel.HasPendingViewWork);
            Assert.Equal(context.Models.Package.Id, context.Models.SelectedModelId);
            Assert.DoesNotContain(context.Models.DownloadedModels, model => model.Id == entry.Key);
            Assert.Contains("cancelled", context.ViewModel.ModelDownloadStatus, StringComparison.OrdinalIgnoreCase);
            Assert.False(context.Models.CatalogGate.Task.IsCompleted);
        }
        finally
        {
            context.Models.CatalogGate.TrySetResult();
            await download;
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_JobHistoryBoundaryFailureLeavesReviewAndArtifactsAvailableForExplicitRetry() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Engine.Result;
        await File.WriteAllBytesAsync(recording.AudioPath, [1, 2, 3]);
        var history = new JsonRecordingHistory(Path.Combine(context.Directory, "job-boundary-history.json"));
        var repository = new JsonTranscriptionJobRepository(Path.Combine(context.Directory, "job-boundary-jobs.json"));
        var outputs = await new AtomicTranscriptWriter().WriteAsync(new(recording, recording.Language,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Completed before history became unavailable.")]), CancellationToken.None);
        var job = context.Job(recording, outputs);
        await history.AddAsync(recording, CancellationToken.None);
        await repository.SaveAsync([job], CancellationToken.None);
        var queue = new TranscriptionQueue(context.Transcriber, new AtomicTranscriptWriter(), repository, history: history);
        var localLibrary = new RecordingLibrary(history, repository, deleteJobAsync: queue.DeleteJobAsync);
        var library = new JobHistoryBoundary(localLibrary) { Failure = new IOException("History service unavailable.") };
        var viewModel = new MainViewModel(context.Recorder, context.Engine, queue, context.Models, history,
            new RecoveryManager(context.Recovery), context, context.Logger, library);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            await viewModel.InitializeCommand.ExecuteAsync(null);
            await UntilAsync(() => viewModel.CanCopyTranscript);
            viewModel.SelectedJobRow = Assert.Single(viewModel.JobRows);
            await viewModel.DeleteSelectedJobHistoryAsync();
            Assert.Equal(job.Id, Assert.Single(viewModel.Jobs).Id);
            Assert.Equal(job.Id, viewModel.SelectedJob!.Id);
            Assert.True(viewModel.CanCopyTranscript);
            Assert.Contains("Completed before history became unavailable.", viewModel.Transcript);
            Assert.True(File.Exists(recording.AudioPath));
            Assert.True(File.Exists(outputs.TextPath));
            Assert.True(File.Exists(outputs.SubtitlePath));
            Assert.Contains("could not be deleted", viewModel.Message);
            Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Error &&
                entry.Message.Contains(job.Id.ToString(), StringComparison.Ordinal) &&
                entry.Message.Contains(nameof(IOException), StringComparison.Ordinal));
            library.Failure = null;
            await viewModel.DeleteSelectedJobHistoryAsync();
            Assert.Empty(viewModel.Jobs);
            Assert.Empty(viewModel.JobRows);
            Assert.Contains("Completed before history became unavailable.", viewModel.Transcript);
            Assert.True(File.Exists(outputs.TextPath));
            Assert.True(File.Exists(outputs.SubtitlePath));
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_PublicResourceReleaseCancelsSearchAndClosesItsActualAudioHandle() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Engine.Result;
        using (var wave = new NAudio.Wave.WaveFileWriter(recording.AudioPath, new NAudio.Wave.WaveFormat(48_000, 16, 1)))
        {
            wave.Write(new byte[96_000], 0, 96_000);
        }
        context.History.Items.Add(recording);
        context.History.LoadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = context.History.LoadGate;
        using var playback = new WindowsRecordingPlayback(() => new WorkspacePlaybackDevice());
        var library = new RecordingLibrary(context.History);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models, context.History,
            new RecoveryManager(context.Recovery), context, context.Logger, library, playback);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            viewModel.SelectedRecording = recording;
            viewModel.PlayRecordingCommand.Execute(null);
            Assert.Equal(recording.AudioPath, viewModel.PlaybackSourcePath);
            viewModel.SearchText = "pending search";
            await context.History.LoadStarted.Task;
            Assert.True(viewModel.HasPendingViewWork);
            viewModel.ReleaseViewResources();
            await UntilAsync(() => !viewModel.HasPendingViewWork);
            Assert.False(release.Task.IsCompleted);
            Assert.Null(playback.RecordingId);
            using var exclusive = File.Open(recording.AudioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            release.TrySetResult();
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_ChangingMeetingClearsPrivateTextWhileItsNewTranscriptIsLoading() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var first = context.Engine.Result with { Title = "First meeting" };
        var second = context.Recording(Guid.NewGuid()) with { Title = "Second meeting" };
        var writer = new AtomicTranscriptWriter();
        var firstOutputs = await writer.WriteAsync(new(first, first.Language,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "First private decision.")]), CancellationToken.None);
        var secondOutputs = await writer.WriteAsync(new(second, second.Language,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Second private decision.")]), CancellationToken.None);
        first = first with { TranscriptOutputs = [new(Guid.NewGuid(), firstOutputs)] };
        second = second with { TranscriptOutputs = [new(Guid.NewGuid(), secondOutputs)] };
        context.History.Items.AddRange([first, second]);
        var library = new RecordingLibrary(context.History);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models, context.History,
            new RecoveryManager(context.Recovery), context, context.Logger, library);
        viewModel.Recordings.Add(first);
        viewModel.Recordings.Add(second);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            viewModel.SelectedRecording = first;
            await UntilAsync(() => viewModel.CanCopyTranscript);
            Assert.Contains("First private decision.", viewModel.Transcript);
            context.History.LoadGate = release;
            viewModel.SelectedRecording = second;
            Assert.True(viewModel.HasPendingViewWork);
            Assert.Equal(second.Id, viewModel.SelectedRecording!.Id);
            Assert.False(viewModel.CanCopyTranscript);
            Assert.Null(viewModel.CompletedTranscriptJobId);
            Assert.DoesNotContain("First private decision.", viewModel.Transcript ?? string.Empty);
            release.SetResult();
            await UntilAsync(() => viewModel.CanCopyTranscript);
            Assert.Contains("Second private decision.", viewModel.Transcript);
            Assert.DoesNotContain("First private decision.", viewModel.Transcript);
        }
        finally
        {
            release.TrySetResult();
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_EnglishRequiresACompatibleChoiceWithoutReplacingALanguageRestrictedPackage() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            await context.ViewModel.InitializeCommand.ExecuteAsync(null);
            context.Models.CurrentPackage = context.Models.Package with { Languages = ["Brazilian Portuguese"] };
            context.ViewModel.Language = "Brazilian Portuguese";
            context.ViewModel.Language = "English";
            Assert.Contains("compatible downloaded model for English", context.ViewModel.Message);
            Assert.Equal(context.Models.Package.Id, context.Models.SelectedModelId);
            Assert.Equal(["Brazilian Portuguese"], context.Models.CurrentPackage.Languages);
            Assert.True(context.ViewModel.StartCommand.CanExecute(null));
            Assert.Empty(context.Queue.Jobs);
            Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("English", StringComparison.Ordinal));
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_MissingOwnedArtifactsRemainExplicitWithoutPromotingACompletedReview() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Engine.Result;
        var outputs = await new AtomicTranscriptWriter().WriteAsync(new(recording, recording.Language,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Saved before the files went missing.")]), CancellationToken.None);
        var jobId = Guid.NewGuid();
        var history = new JsonRecordingHistory(Path.Combine(context.Directory, "missing-artifacts-history.json"));
        await history.AddAsync(recording with { TranscriptOutputs = [new(jobId, outputs)] }, CancellationToken.None);
        File.Delete(outputs.TextPath);
        var library = new RecordingLibrary(history);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models, history,
            new RecoveryManager(context.Recovery), context, context.Logger, library);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            await viewModel.InitializeCommand.ExecuteAsync(null);
            await UntilAsync(() => !viewModel.HasPendingViewWork);
            Assert.Contains("retained transcript artifact is unavailable", viewModel.Message);
            Assert.Null(viewModel.Transcript);
            Assert.False(viewModel.CanCopyTranscript);
            Assert.Null(viewModel.CompletedTranscriptJobId);
            Assert.True(File.Exists(outputs.SubtitlePath));
            Assert.False(File.Exists(recording.AudioPath));
            Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("Library", StringComparison.Ordinal));
            Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains(recording.Id.ToString(), StringComparison.Ordinal));
            Assert.DoesNotContain(context.Logger.Entries, entry => entry.Message.Contains(recording.AudioPath,
                StringComparison.Ordinal) || entry.Message.Contains(outputs.TextPath, StringComparison.Ordinal));
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_FullDeletionDisablesDuplicateRequestsUntilOwnedWorkerAndAudioAreReleased() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Engine.Result;
        using (var wave = new NAudio.Wave.WaveFileWriter(recording.AudioPath, new NAudio.Wave.WaveFormat(48_000, 16, 1)))
        {
            wave.Write(new byte[96_000], 0, 96_000);
        }
        var history = new JsonRecordingHistory(Path.Combine(context.Directory, "held-deletion-history.json"));
        var repository = new JsonTranscriptionJobRepository(Path.Combine(context.Directory, "held-deletion-jobs.json"));
        await history.AddAsync(recording, CancellationToken.None);
        var outputs = await new AtomicTranscriptWriter().WriteAsync(new(recording, recording.Language,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Retained before explicit deletion.")]), CancellationToken.None);
        var completed = context.Job(recording, outputs);
        await repository.SaveAsync([completed], CancellationToken.None);
        context.Transcriber.Block = true;
        context.Transcriber.CancellationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new TranscriptionQueue(context.Transcriber, new AtomicTranscriptWriter(), repository, history: history);
        using var playback = new WindowsRecordingPlayback(() => new WorkspacePlaybackDevice());
        var dispatcher = Dispatcher.CurrentDispatcher;
        var library = new RecordingLibrary(history, repository, cancelRecordingAsync: queue.CancelRecordingAsync,
            deleteJobAsync: queue.DeleteJobAsync, releasePlaybackAsync: (_, token) =>
                dispatcher.InvokeAsync(playback.Close, DispatcherPriority.Normal, token).Task);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, queue, context.Models, history,
            new RecoveryManager(context.Recovery), context, context.Logger, library, playback);
        var environment = new DesktopEnvironment();
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs { Answer = MessageBoxResult.Yes }, environment,
            new DesktopLogger<MainWindow>());
        Task? deleting = null;
        try
        {
            await viewModel.InitializeCommand.ExecuteAsync(null);
            viewModel.PlayRecordingCommand.Execute(null);
            Assert.True(viewModel.IsPlaying);
            await queue.EnqueueAsync(recording, CancellationToken.None);
            await context.Transcriber.Started.Task;
            viewModel.Refresh();
            deleting = viewModel.DeleteRecordingAsync(true);
            await context.Transcriber.CancellationRequested.Task;
            Assert.True(viewModel.IsDeletingRecording);
            Assert.False(viewModel.CanDeleteRecording);
            Assert.True(viewModel.StartCommand.CanExecute(null));
            await viewModel.DeleteRecordingAsync(true);
            Assert.True(File.Exists(recording.AudioPath));
            Assert.Equal(2, queue.Jobs.Count);
            var exiting = window.RequestExitAsync();
            Assert.False(exiting.IsCompleted);
            Assert.Equal(0, environment.Disposals);
            context.Transcriber.CancellationGate.SetResult();
            await deleting;
            await exiting;
            Assert.Equal(1, environment.Disposals);
            Assert.Empty(viewModel.Jobs);
            Assert.Empty(viewModel.JobRows);
            Assert.Empty(viewModel.Recordings);
            Assert.Empty(queue.Jobs);
            Assert.False(viewModel.IsDeletingRecording);
            Assert.False(viewModel.IsPlaying);
            Assert.Null(viewModel.PlaybackSourcePath);
            Assert.Equal(0, viewModel.PlaybackPositionSeconds);
            Assert.Equal(0, viewModel.PlaybackDurationSeconds);
            Assert.False(File.Exists(recording.AudioPath));
            Assert.False(File.Exists(outputs.TextPath));
            Assert.False(File.Exists(outputs.SubtitlePath));
        }
        finally
        {
            context.Transcriber.CancellationGate.TrySetResult();
            if (deleting is not null) await deleting;
            await queue.WaitForIdleAsync(CancellationToken.None);
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_EmptySearchUpdatesWhenMatchingRecoveredAudioIsAdded() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var history = new JsonRecordingHistory(Path.Combine(context.Directory, "empty-search-history.json"));
        var library = new RecordingLibrary(history);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models, history,
            new RecoveryManager(context.Recovery), context, context.Logger, library);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            await viewModel.InitializeCommand.ExecuteAsync(null);
            viewModel.SearchText = "Budget";
            await UntilAsync(() => !viewModel.HasPendingViewWork);
            Assert.Empty(viewModel.FilteredRecordings);
            Assert.Contains("No recordings", viewModel.LibraryMessage);
            var recovered = context.Engine.Result with { Title = "Budget", Outcome = RecordingOutcome.Recovered };
            await File.WriteAllBytesAsync(recovered.AudioPath, [1, 2, 3]);
            context.Recovery.Result = recovered;
            await viewModel.RecoverAsync(context.Recovery.Candidate);
            Assert.Equal("Budget", viewModel.SearchText);
            Assert.Equal(recovered.Id, Assert.Single(viewModel.FilteredRecordings).Id);
            Assert.Equal(string.Empty, viewModel.LibraryMessage);
            Assert.Null(viewModel.SelectedRecording);
            Assert.Empty(context.Queue.Jobs);
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_CloseRetainsSavedAudioWhenModelCommitWinsCancellation() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var model = await context.Models.RegisterAsync("existing.zip", CancellationToken.None);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken selectionToken = default;
        context.Models.Selecting = token =>
        {
            selectionToken = token;
            selectionStarted.SetResult();
            return releaseCommit.Task;
        };
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            await context.ViewModel.InitializeCommand.ExecuteAsync(null);
            context.ViewModel.SelectedModel = model;
            await selectionStarted.Task;
            context.PrepareRecording();
            await context.ViewModel.StartRecordingAsync(false);
            var saved = await context.ViewModel.StopAsync(enqueueTranscription: true);
            Assert.Same(saved, Assert.Single(context.ViewModel.Recordings));
            Assert.Empty(context.Queue.Jobs);
            var exiting = window.RequestExitAsync();
            Assert.True(selectionToken.IsCancellationRequested);
            Assert.False(exiting.IsCompleted);
            releaseCommit.SetResult();
            await exiting;
            Assert.Empty(context.Queue.Jobs);
            Assert.Same(saved, Assert.Single(context.ViewModel.Recordings));
            Assert.Contains("saved for later transcription while the workspace is closing", context.ViewModel.Message);
            Assert.False(context.ViewModel.HasPendingViewWork);
        }
        finally
        {
            releaseCommit.TrySetResult();
            await window.RequestExitAsync();
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task MeetingWorkspace_TransferAndVerificationNeverOfferAnIncompleteModelChoice(bool lengthKnown) => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        await context.Models.RegisterAsync("existing.zip", CancellationToken.None);
        context.Models.CurrentPackage = context.Models.Package;
        context.Models.CatalogTotalBytes = lengthKnown ? 4096 : null;
        context.Models.CatalogGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Models.VerificationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        var entry = SoundRecorder.Infrastructure.Models.TranscriptionModelCatalog.Entries.Single(
            model => model.Key == "whisper.net:small:Q5_1");
        var download = context.ViewModel.DownloadCatalogModelCommand.ExecuteAsync(entry);
        try
        {
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(980, 720));
            content.Arrange(new Rect(0, 0, 980, 720));
            content.UpdateLayout();
            await UntilAsync(() => context.ViewModel.ModelDownloadStatus == "Downloading");
            var progress = Assert.IsType<System.Windows.Controls.ProgressBar>(window.FindName("ModelDownloadProgress"));
            Assert.Equal(lengthKnown ? 25 : 0, progress.Value);
            Assert.Equal(!lengthKnown, progress.IsIndeterminate);
            if (!lengthKnown)
            {
                Assert.DoesNotContain("%", context.ViewModel.ModelHeader);
                Assert.Equal("Indeterminate", System.Windows.Automation.AutomationProperties.GetItemStatus(progress));
            }
            context.Models.CatalogGate.SetResult();
            await UntilAsync(() => context.ViewModel.ModelDownloadStatus == "Verifying");
            Assert.True(context.ViewModel.IsDownloadingModel);
            Assert.True(progress.IsIndeterminate);
            Assert.Equal("Indeterminate", System.Windows.Automation.AutomationProperties.GetItemStatus(progress));
            Assert.DoesNotContain(context.Models.DownloadedModels, model => model.Id == entry.Key);
            Assert.Equal(context.Models.Package.Id, context.Models.SelectedModelId);
            context.Models.VerificationGate.SetResult();
            await download;
            Assert.Contains(context.ViewModel.DownloadedModels, model => model.Id == entry.Key);
            Assert.Equal(context.Models.Package.Id, context.Models.SelectedModelId);
            Assert.False(context.ViewModel.IsDownloadingModel);
            var completedStatus = context.ViewModel.ModelDownloadStatus;
            var selectedHeader = context.ViewModel.ModelHeader;
            context.Models.CatalogProgress!.Report(new(ModelDownloadPhase.Downloading, 1, 2));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal(completedStatus, context.ViewModel.ModelDownloadStatus);
            Assert.Equal(selectedHeader, context.ViewModel.ModelHeader);
            Assert.False(context.ViewModel.IsDownloadingModel);
        }
        finally
        {
            context.Models.CatalogGate.TrySetResult();
            context.Models.VerificationGate.TrySetResult();
            await download;
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_FailedTerminalCheckpointOffersAnExplicitPersistenceRetry() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        context.Repository.TerminalSaveError = new IOException("Private history checkpoint is unavailable.");
        await context.Queue.EnqueueAsync(context.Engine.Result, CancellationToken.None);
        await UntilAsync(() => Assert.Single(context.Queue.Jobs).HasTerminalCheckpointFailure);
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            context.ViewModel.Refresh();
            var row = Assert.Single(context.ViewModel.JobRows);
            context.ViewModel.SelectedJobRow = row;
            context.ViewModel.ShowJobsCommand.Execute(null);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(980, 720));
            content.Arrange(new Rect(0, 0, 980, 720));
            content.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal(TranscriptionJobStatus.Succeeded, row.Job.Status);
            Assert.True(row.Job.HasTerminalCheckpointFailure);
            Assert.True(row.Job.IsTerminationPending);
            Assert.False(row.CanDelete);
            var cancel = Assert.IsType<System.Windows.Controls.Button>(FindButton(content, "Cancel"));
            Assert.True(cancel.IsEnabled);
            var command = Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(cancel.Command);
            var failure = await Record.ExceptionAsync(() => command.ExecuteAsync(cancel.CommandParameter));
            Assert.Null(failure);
            Assert.True(row.Job.HasTerminalCheckpointFailure);
            Assert.True(row.Job.IsTerminationPending);
            Assert.False(row.CanDelete);
            Assert.Contains("could not be cancelled", context.ViewModel.Message);
            Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Error &&
                entry.Message.Contains(row.Id.ToString(), StringComparison.Ordinal) &&
                entry.Message.Contains(nameof(IOException), StringComparison.Ordinal));
            Assert.DoesNotContain(context.Logger.Entries, entry => entry.Message.Contains("Private history checkpoint",
                StringComparison.Ordinal));
            context.Repository.TerminalSaveError = null;
            await command.ExecuteAsync(cancel.CommandParameter);
            Assert.False(row.Job.HasTerminalCheckpointFailure);
            Assert.False(row.Job.IsTerminationPending);
            Assert.Equal(TranscriptionJobStatus.Succeeded, row.Job.Status);
            Assert.True(row.CanDelete);
            Assert.DoesNotContain("could not be cancelled", context.ViewModel.Message);
            Assert.Same(row.Job, Assert.Single(context.Queue.Jobs));
            Assert.Null(context.Engine.Request);
        }
        finally
        {
            context.Repository.TerminalSaveError = null;
            await context.Queue.CancelAsync(Assert.Single(context.Queue.Jobs).Id, CancellationToken.None);
            await context.Queue.WaitForIdleAsync(CancellationToken.None);
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_ExitWaitsForATerminalJobCheckpointBeforeDisposingOwnedServices() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        context.Repository.TerminalSaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Transcriber.Block = true;
        await context.Queue.EnqueueAsync(context.Engine.Result, CancellationToken.None);
        await context.Transcriber.Started.Task;
        var environment = new DesktopEnvironment();
        var lifetime = new DesktopLifetime();
        var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs { Answer = MessageBoxResult.Yes }, environment,
            new DesktopLogger<MainWindow>(), lifetime);
        try
        {
            context.Transcriber.Release.SetResult();
            await context.Repository.TerminalSaveStarted.Task;
            context.ViewModel.Refresh();
            var row = Assert.Single(context.ViewModel.JobRows);
            Assert.Equal(TranscriptionJobStatus.Succeeded, row.Job.Status);
            Assert.True(row.Job.IsTerminationPending);
            Assert.False(row.CanDelete);
            Assert.True(context.ViewModel.HasActiveWork());
            var exiting = window.RequestExitAsync();
            Assert.False(exiting.IsCompleted);
            Assert.Equal(0, environment.Disposals);
            Assert.Equal(0, lifetime.Disposals);
            context.Repository.TerminalSaveGate.SetResult();
            await exiting;
            await context.Queue.WaitForIdleAsync(CancellationToken.None);
            Assert.False(row.Job.IsTerminationPending);
            Assert.Equal(1, environment.Disposals);
            Assert.Equal(1, lifetime.Disposals);
        }
        finally
        {
            context.Repository.TerminalSaveGate.TrySetResult();
            await context.Queue.WaitForIdleAsync(CancellationToken.None);
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_ChangingSearchCancelsSupersededLocalReadsWithoutChangingReview() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var first = context.Engine.Result with { Title = "Alpha meeting" };
        var second = context.Recording(Guid.NewGuid()) with { Title = "Beta meeting" };
        await File.WriteAllBytesAsync(first.AudioPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(second.AudioPath, [4, 5, 6]);
        context.History.Items.AddRange([first, second]);
        context.History.LoadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedRead = context.History.LoadGate;
        var library = new RecordingLibrary(context.History);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models, context.History,
            new RecoveryManager(context.Recovery), context, context.Logger, library);
        viewModel.Recordings.Add(first);
        viewModel.Recordings.Add(second);
        viewModel.SelectedRecording = first;
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            viewModel.SearchText = "Alpha";
            await context.History.LoadStarted.Task;
            Assert.True(viewModel.HasPendingViewWork);
            context.History.LoadGate = null;
            viewModel.SearchText = "Beta";
            await UntilAsync(() => !viewModel.HasPendingViewWork);
            Assert.Equal(second.Id, Assert.Single(viewModel.FilteredRecordings).Id);
            Assert.Equal(first.Id, viewModel.SelectedRecording!.Id);
            Assert.Equal(string.Empty, viewModel.LibraryMessage);
            Assert.False(blockedRead.Task.IsCompleted);
            Assert.Empty(context.Logger.Errors);
            blockedRead.SetResult();
            await UntilAsync(() => !viewModel.HasPendingViewWork);
            Assert.Equal(second.Id, Assert.Single(viewModel.FilteredRecordings).Id);
            Assert.Equal(first.Id, viewModel.SelectedRecording!.Id);
        }
        finally
        {
            blockedRead.TrySetResult();
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_UnreadableDeletionMetadataIsRecoverableAndNeverReportedAsDeleted() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Engine.Result;
        await File.WriteAllBytesAsync(recording.AudioPath, [1, 2, 3]);
        var path = Path.Combine(context.Directory, "unavailable-deletion-history.json");
        var history = new JsonRecordingHistory(path);
        await history.AddAsync(recording, CancellationToken.None);
        var library = new RecordingLibrary(history, context.Repository, cancelRecordingAsync: context.Queue.CancelRecordingAsync,
            deleteJobAsync: context.Queue.DeleteJobAsync, releasePlaybackAsync: (_, _) => Task.CompletedTask);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models, history,
            new RecoveryManager(context.Recovery), context, context.Logger, library);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            await viewModel.InitializeCommand.ExecuteAsync(null);
            using (var blockedMetadata = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await viewModel.DeleteRecordingAsync(false);
                Assert.Contains("Deletion could not finish", viewModel.Message);
                Assert.False(viewModel.IsDeletingRecording);
                Assert.True(viewModel.CanDeleteRecording);
                Assert.Equal(recording.Id, viewModel.SelectedRecording!.Id);
                Assert.False(viewModel.SelectedRecording.AudioDeleted);
                Assert.True(File.Exists(recording.AudioPath));
                Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Error &&
                    entry.Message.Contains(recording.Id.ToString(), StringComparison.Ordinal) &&
                    entry.Message.Contains(nameof(IOException), StringComparison.Ordinal));
                Assert.DoesNotContain(context.Logger.Entries, entry => entry.Message.Contains(recording.AudioPath,
                    StringComparison.Ordinal));
            }
            await viewModel.DeleteRecordingAsync(false);
            Assert.False(File.Exists(recording.AudioPath));
            Assert.True(viewModel.SelectedRecording!.AudioDeleted);
            Assert.False(viewModel.IsDeletingRecording);
            Assert.Contains("Audio deleted", viewModel.Message);
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_ChangingReviewReportsAPlaybackReleaseFailureWithoutBreakingTheNewMeeting() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var first = context.Engine.Result with { Title = "Previous meeting" };
        var next = context.Recording(Guid.NewGuid()) with { Title = "Next meeting" };
        foreach (var recording in new[] { first, next })
        {
            using var wave = new NAudio.Wave.WaveFileWriter(recording.AudioPath, new NAudio.Wave.WaveFormat(48_000, 16, 1));
            wave.Write(new byte[96_000], 0, 96_000);
        }
        var devices = new List<WorkspacePlaybackDevice>();
        using var playback = new WindowsRecordingPlayback(() =>
        {
            var device = new WorkspacePlaybackDevice();
            devices.Add(device);
            return device;
        });
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models, context.History,
            new RecoveryManager(context.Recovery), context, context.Logger, playback: playback);
        viewModel.Recordings.Add(first);
        viewModel.Recordings.Add(next);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(980, 720));
            content.Arrange(new Rect(0, 0, 980, 720));
            content.UpdateLayout();
            viewModel.SelectedRecording = first;
            viewModel.PlayRecordingCommand.Execute(null);
            var oldDevice = Assert.Single(devices);
            oldDevice.DisposeError = new IOException("Previous audio device release failed.");
            var error = Record.Exception(() => viewModel.SelectedRecording = next);
            Assert.Null(error);
            Assert.True(oldDevice.Disposed);
            Assert.Null(viewModel.PlaybackSourcePath);
            Assert.False(viewModel.IsPlaying);
            Assert.Equal(next.Id, viewModel.SelectedRecording!.Id);
            Assert.Contains("Previous audio device release failed", viewModel.Message);
            content.UpdateLayout();
            await UntilAsync(() => VisualTexts(Assert.IsAssignableFrom<DependencyObject>(window.FindName("ReaderPane")))
                .Contains("Next meeting"));
            using (var releasedAudio = File.Open(first.AudioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                viewModel.PlayRecordingCommand.Execute(null);
                Assert.Equal(next.AudioPath, viewModel.PlaybackSourcePath);
                Assert.True(viewModel.IsPlaying);
                Assert.Equal(2, devices.Count);
            }
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_ReloadingTheSameMeetingCannotPublishAnOlderInFlightTranscript() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Engine.Result;
        var writer = new AtomicTranscriptWriter();
        var oldOutput = await writer.WriteAsync(new(recording, recording.Language,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Superseded published decision.")]), CancellationToken.None);
        var latestOutput = await writer.WriteAsync(new(recording, recording.Language,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Latest approved decision.")]), CancellationToken.None);
        var oldJob = Guid.NewGuid();
        var latestJob = Guid.NewGuid();
        var initial = recording with { TranscriptOutputs = [new(oldJob, oldOutput)] };
        var updated = recording with { TranscriptOutputs = [new(oldJob, oldOutput), new(latestJob, latestOutput)] };
        context.History.Items.Add(initial);
        context.History.LoadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedRead = context.History.LoadGate;
        var library = new RecordingLibrary(context.History);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models, context.History,
            new RecoveryManager(context.Recovery), context, context.Logger, library);
        viewModel.Recordings.Add(initial);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            viewModel.SelectedRecording = initial;
            await context.History.LoadStarted.Task;
            context.History.LoadGate = null;
            context.History.Items[0] = updated;
            viewModel.Recordings[0] = updated;
            viewModel.SelectedRecording = null;
            viewModel.SelectedRecording = updated;
            await UntilAsync(() => viewModel.CompletedTranscriptJobId == latestJob);
            Assert.Contains("Latest approved decision.", viewModel.Transcript);
            blockedRead.SetResult();
            await UntilAsync(() => !viewModel.HasPendingViewWork);
            Assert.Equal(latestJob, viewModel.CompletedTranscriptJobId);
            Assert.Contains("Latest approved decision.", viewModel.Transcript);
            Assert.DoesNotContain("Superseded published decision.", viewModel.Transcript);
        }
        finally
        {
            blockedRead.TrySetResult();
            await window.RequestExitAsync();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MeetingWorkspace_PlaybackRefreshPreservesPositionAndPauseFailureReleasesSavedAudio(bool releaseFails) =>
        OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Engine.Result;
        using (var wave = new NAudio.Wave.WaveFileWriter(recording.AudioPath, new NAudio.Wave.WaveFormat(48_000, 16, 1)))
        {
            wave.Write(new byte[3 * 96_000], 0, 3 * 96_000);
        }
        var devices = new List<WorkspacePlaybackDevice>();
        using var playback = new WindowsRecordingPlayback(() =>
        {
            var device = new WorkspacePlaybackDevice();
            devices.Add(device);
            return device;
        });
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models, context.History,
            new RecoveryManager(context.Recovery), context, context.Logger, playback: playback);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            Assert.False(viewModel.HasPlayableRecording);
            viewModel.PlayRecordingCommand.Execute(null);
            Assert.Contains("Select an available recording", viewModel.Message);
            Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("Playback", StringComparison.Ordinal));
            viewModel.SelectedRecording = recording;
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(980, 720));
            content.Arrange(new Rect(0, 0, 980, 720));
            content.UpdateLayout();
            var playbackPanel = LogicalElements(content).OfType<System.Windows.Controls.Border>().Single(element =>
                System.Windows.Automation.AutomationProperties.GetName(element) == "Recording playback");
            Assert.Equal(Visibility.Visible, playbackPanel.Visibility);
            viewModel.PlaybackVolume = 0.4;
            var playbackChanges = new List<string?>();
            viewModel.PropertyChanged += (_, args) => playbackChanges.Add(args.PropertyName);
            viewModel.PlayRecordingCommand.Execute(null);
            var device = Assert.Single(devices);
            device.PositionBytes = 120_000;
            viewModel.Refresh();
            Assert.Equal(1.25, viewModel.PlaybackPositionSeconds);
            Assert.Equal(3, viewModel.PlaybackDurationSeconds);
            Assert.Equal(0.4f, device.Volume);
            Assert.False(device.Disposed);
            Assert.True(viewModel.IsPlaying);
            Assert.Equal(recording.AudioPath, viewModel.PlaybackSourcePath);
            Assert.Contains(nameof(MainViewModel.PlaybackSourcePath), playbackChanges);
            playbackChanges.Clear();
            viewModel.PlayRecordingCommand.Execute(null);
            Assert.Single(devices);
            device.Fail(new IOException("Device cannot continue saved audio."));
            viewModel.Refresh();
            Assert.False(viewModel.IsPlaying);
            Assert.Contains("Device cannot continue saved audio", viewModel.Message);
            var reportedErrors = context.Logger.Entries.Count(entry => entry.Level == LogLevel.Error);
            Assert.Equal(1, reportedErrors);
            viewModel.Refresh();
            Assert.Equal(reportedErrors, context.Logger.Entries.Count(entry => entry.Level == LogLevel.Error));
            viewModel.PlayRecordingCommand.Execute(null);
            viewModel.PlaybackPositionSeconds = 2;
            Assert.True(device.Disposed);
            Assert.Equal(2, devices.Count);
            var resumed = devices.Last();
            resumed.PauseError = new IOException("Device cannot pause the private meeting.");
            resumed.DisposeError = releaseFails ? new UnauthorizedAccessException("Device release failed.") : null;
            viewModel.PausePlaybackCommand.Execute(null);
            Assert.True(resumed.Disposed);
            Assert.False(viewModel.IsPlaying);
            Assert.Equal(0, viewModel.PlaybackPositionSeconds);
            Assert.Equal(0, viewModel.PlaybackDurationSeconds);
            Assert.Null(viewModel.PlaybackSourcePath);
            Assert.Equal(recording.Id, viewModel.SelectedRecording!.Id);
            Assert.Contains("Device cannot pause", viewModel.Message);
            Assert.StartsWith("The recording could not be played", viewModel.Message);
            Assert.Equal(releaseFails, viewModel.Message.Contains("handle could not be released", StringComparison.Ordinal));
            if (releaseFails)
            {
                Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Error &&
                    entry.Message.Contains(nameof(UnauthorizedAccessException), StringComparison.Ordinal));
            }
            Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Error &&
                entry.Message.Contains(nameof(IOException), StringComparison.Ordinal));
            Assert.DoesNotContain(context.Logger.Entries, entry => entry.Message.Contains(recording.AudioPath,
                StringComparison.Ordinal) || entry.Message.Contains("private meeting", StringComparison.Ordinal));
            using var exclusivelyReopened = File.Open(recording.AudioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            viewModel.SelectedRecording = null;
            Assert.Contains(nameof(MainViewModel.PlaybackSourcePath), playbackChanges);
            content.UpdateLayout();
            await UntilAsync(() => playbackPanel.Visibility == Visibility.Collapsed);
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MeetingWorkspace_ACompletedReadCannotOverwriteAnotherMeetingOrAnExitedWorkspace(bool close) =>
        OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var first = context.Engine.Result;
        var second = context.Recording(Guid.NewGuid());
        var firstOutputs = await new AtomicTranscriptWriter().WriteAsync(new(first, first.Language,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "First meeting private decision.")]), CancellationToken.None);
        var secondOutputs = await new AtomicTranscriptWriter().WriteAsync(new(second, second.Language,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Second meeting private decision.")]), CancellationToken.None);
        var firstJob = Guid.NewGuid();
        var secondJob = Guid.NewGuid();
        first = first with { TranscriptOutputs = [new(firstJob, firstOutputs)] };
        second = second with { TranscriptOutputs = [new(secondJob, secondOutputs)] };
        context.History.Items.AddRange([first, second]);
        context.History.LoadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedRead = context.History.LoadGate;
        var library = new RecordingLibrary(context.History);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models, context.History,
            new RecoveryManager(context.Recovery), context, context.Logger, library);
        viewModel.Recordings.Add(first);
        viewModel.Recordings.Add(second);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            viewModel.SelectedRecording = first;
            await context.History.LoadStarted.Task;
            Assert.True(viewModel.HasPendingViewWork);
            Assert.False(viewModel.CanCopyTranscript);
            context.History.LoadGate = null;
            if (close)
            {
                var exiting = window.RequestExitAsync();
                Assert.False(exiting.IsCompleted);
                blockedRead.SetResult();
                await exiting;
                Assert.Null(viewModel.Transcript);
                Assert.False(viewModel.CanCopyTranscript);
                Assert.Null(viewModel.CompletedTranscriptJobId);
                var lateRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                context.History.LoadGate = lateRead;
                try
                {
                    viewModel.SearchText = "private decision";
                    viewModel.SelectedRecording = second;
                    Assert.False(viewModel.HasPendingViewWork);
                    Assert.Null(viewModel.Transcript);
                    Assert.False(viewModel.CanCopyTranscript);
                }
                finally
                {
                    lateRead.TrySetResult();
                }
            }
            else
            {
                viewModel.SelectedRecording = second;
                await UntilAsync(() => viewModel.CanCopyTranscript);
                Assert.Equal(secondJob, viewModel.CompletedTranscriptJobId);
                Assert.Contains("Second meeting private decision.", viewModel.Transcript);
                blockedRead.SetResult();
                await UntilAsync(() => !viewModel.HasPendingViewWork);
                Assert.Equal(second.Id, viewModel.SelectedRecording!.Id);
                Assert.Equal(secondJob, viewModel.CompletedTranscriptJobId);
                Assert.Contains("Second meeting private decision.", viewModel.Transcript);
                Assert.DoesNotContain("First meeting private decision.", viewModel.Transcript);
            }
            Assert.False(viewModel.HasPendingViewWork);
        }
        finally
        {
            blockedRead.TrySetResult();
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_RetainedLatestTranscriptOutlivesItsJobWhileOlderJobReviewStaysExact() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Engine.Result;
        await File.WriteAllBytesAsync(recording.AudioPath, [1, 2, 3]);
        var history = new JsonRecordingHistory(Path.Combine(context.Directory, "attempt-history.json"));
        var repository = new JsonTranscriptionJobRepository(Path.Combine(context.Directory, "attempt-jobs.json"));
        await history.AddAsync(recording, CancellationToken.None);
        var seed = new TranscriptionQueue(context.Transcriber, new AtomicTranscriptWriter(), repository, history: history);
        context.Transcriber.Segments = [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Earlier approved decision.")];
        await seed.EnqueueAsync(recording, CancellationToken.None);
        await seed.WaitForIdleAsync(CancellationToken.None);
        var earlier = Assert.Single(seed.Jobs);
        context.Transcriber.Segments = [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Latest revised decision.")];
        await seed.RetryAsync(recording, CancellationToken.None);
        await seed.WaitForIdleAsync(CancellationToken.None);
        var latest = seed.Jobs.Last();
        var queue = new TranscriptionQueue(context.Transcriber, new AtomicTranscriptWriter(), repository, history: history);
        var library = new RecordingLibrary(history, repository, deleteJobAsync: queue.DeleteJobAsync,
            cancelRecordingAsync: queue.CancelRecordingAsync, releasePlaybackAsync: (_, _) => Task.CompletedTask);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, queue, context.Models, history,
            new RecoveryManager(context.Recovery), context, context.Logger, library);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            await viewModel.InitializeCommand.ExecuteAsync(null);
            await UntilAsync(() => viewModel.CompletedTranscriptJobId == latest.Id);
            Assert.Contains("Latest revised decision.", viewModel.Transcript);
            viewModel.SelectedJobRow = viewModel.JobRows.Single(row => row.Id == latest.Id);
            await viewModel.DeleteSelectedJobHistoryAsync();
            Assert.Equal(earlier.Id, Assert.Single(queue.Jobs).Id);
            Assert.Null(viewModel.SelectedJobRow);
            viewModel.SelectedRecording = null;
            viewModel.SelectedRecording = Assert.Single(viewModel.Recordings);
            await UntilAsync(() => viewModel.CanCopyTranscript);
            Assert.Equal(latest.Id, viewModel.CompletedTranscriptJobId);
            Assert.Contains("Latest revised decision.", viewModel.Transcript);
            Assert.DoesNotContain("Earlier approved decision.", viewModel.Transcript);
            viewModel.SelectedJobRow = Assert.Single(viewModel.JobRows);
            await viewModel.ViewTranscriptCommand.ExecuteAsync(null);
            Assert.Equal(earlier.Id, viewModel.CompletedTranscriptJobId);
            Assert.Contains("Earlier approved decision.", viewModel.Transcript);
            Assert.DoesNotContain("Latest revised decision.", viewModel.Transcript);
            Assert.True(File.Exists(recording.AudioPath));
            Assert.Contains("Latest revised decision.", await File.ReadAllTextAsync(latest.Outputs!.TextPath));
            Assert.Contains("Earlier approved decision.", await File.ReadAllTextAsync(earlier.Outputs!.TextPath));
            using (var unavailableHistory = File.Open(Path.Combine(context.Directory, "attempt-history.json"),
                       FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await viewModel.ViewTranscriptCommand.ExecuteAsync(null);
                Assert.Null(viewModel.Transcript);
                Assert.False(viewModel.CanCopyTranscript);
                Assert.Null(viewModel.CompletedTranscriptJobId);
                Assert.Contains("retained transcript could not be read", viewModel.Message);
            }
            await viewModel.DeleteRecordingAsync(false);
            Assert.True(viewModel.SelectedRecording!.AudioDeleted);
            viewModel.SelectedRecording = null;
            Assert.Null(viewModel.SelectedJob);
            Assert.Null(viewModel.SelectedJobRow);
            viewModel.SelectedJobRow = Assert.Single(viewModel.JobRows);
            await viewModel.ViewTranscriptCommand.ExecuteAsync(null);
            Assert.NotNull(viewModel.SelectedRecording);
            Assert.True(viewModel.SelectedRecording!.AudioDeleted);
            Assert.Contains("Audio deleted", viewModel.SelectedRecordingInformation);
            Assert.Equal(earlier.Id, viewModel.CompletedTranscriptJobId);
            Assert.Contains("Earlier approved decision.", viewModel.Transcript);
            await viewModel.RetryTranscriptionCommand.ExecuteAsync(null);
            Assert.Contains("Audio is deleted", viewModel.Message);
            Assert.Equal(earlier.Id, Assert.Single(queue.Jobs).Id);
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MeetingWorkspace_ActiveAttemptsLeadHistoryAndProgressBelongsToTheReviewedRecording(bool downloadedModel) => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Recording(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")) with { Title = "Reviewed meeting" };
        var other = context.Recording(Guid.Parse("00000000-0000-0000-0000-000000000001")) with { Title = "Other meeting" };
        await File.WriteAllBytesAsync(recording.AudioPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(other.AudioPath, [4, 5, 6]);
        var history = new JsonRecordingHistory(Path.Combine(context.Directory, "ordered-history.json"));
        var repository = new JsonTranscriptionJobRepository(Path.Combine(context.Directory, "ordered-jobs.json"));
        var outputs = await new AtomicTranscriptWriter().WriteAsync(new(recording, recording.Language,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Previously published decision.")]), CancellationToken.None);
        var model = downloadedModel ? await context.Models.DownloadAsync(
            SoundRecorder.Infrastructure.Models.TranscriptionModelCatalog.Entries.Single(entry => entry.Key == "whisper.net:small:Q5_1"),
            new Progress<ModelDownloadProgress>(), CancellationToken.None) : null;
        var completed = new TranscriptionJob(Guid.NewGuid(), recording, DateTimeOffset.UnixEpoch,
            TranscriptionJobStatus.Succeeded, outputs, null, model?.Id ?? "historical-model");
        await history.AddAsync(recording, CancellationToken.None);
        await history.AddAsync(other, CancellationToken.None);
        await repository.SaveAsync([completed], CancellationToken.None);
        context.Transcriber.Block = true;
        var queue = new TranscriptionQueue(context.Transcriber, new AtomicTranscriptWriter(), repository, history: history);
        var library = new RecordingLibrary(history, repository, cancelRecordingAsync: queue.CancelRecordingAsync,
            deleteJobAsync: queue.DeleteJobAsync);
        var recorder = new RecorderApplication(context.Engine, queue, context.Models, history);
        var viewModel = new MainViewModel(recorder, context.Engine, queue, context.Models, history,
            new RecoveryManager(context.Recovery), context, context.Logger, library);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            await viewModel.InitializeCommand.ExecuteAsync(null);
            await UntilAsync(() => viewModel.CanCopyTranscript);
            Assert.Contains("Previously published decision.", viewModel.Transcript);
            Assert.Equal(completed.Id, viewModel.CompletedTranscriptJobId);
            await queue.EnqueueAsync(recording, CancellationToken.None);
            await context.Transcriber.Started.Task;
            await queue.EnqueueAsync(other, CancellationToken.None);
            viewModel.Refresh();
            var running = viewModel.JobRows.Single(row => row.Job.Status == TranscriptionJobStatus.Running);
            var queued = viewModel.JobRows.Single(row => row.Job.Status == TranscriptionJobStatus.Queued);
            var published = viewModel.JobRows.Single(row => row.Id == completed.Id);
            Assert.Equal([running, queued, published], viewModel.JobRows);
            Assert.Equal(model?.DisplayName ?? "historical-model", published.ModelName);
            Assert.Same(running, viewModel.SelectedRecordingJobProgress);
            Assert.Equal("Previously completed transcript", viewModel.TranscriptReviewLabel);
            viewModel.SelectedJobRow = running;
            await viewModel.StopJobCommand.ExecuteAsync(null);
            await UntilAsync(() => queue.Jobs.Any(job => job.Recording.Id == other.Id &&
                job.Status == TranscriptionJobStatus.Running));
            viewModel.Refresh();
            Assert.Same(running, viewModel.SelectedJobRow);
            Assert.Equal([queued, published, running], viewModel.JobRows);
            Assert.Null(viewModel.SelectedRecordingJobProgress);
            Assert.Equal("Completed transcript", viewModel.TranscriptReviewLabel);
            Assert.Contains("Previously published decision.", viewModel.Transcript);
            Assert.True(viewModel.CanDeleteSelectedJob);
            viewModel.SelectedRecording = viewModel.Recordings.Single(item => item.Id == other.Id);
            Assert.Same(queued, viewModel.SelectedRecordingJobProgress);
            Assert.Null(viewModel.CompletedTranscriptJobId);
            Assert.Equal(string.Empty, viewModel.TranscriptReviewLabel);
            Assert.Equal(running.Id, viewModel.SelectedJob!.Id);
            await viewModel.DeleteSelectedJobHistoryAsync();
            Assert.True(viewModel.Message.Contains("Job history deleted.", StringComparison.Ordinal),
                $"{viewModel.Message} Other attempt: {queued.Job.Status} / {queued.Job.Error}; target pending: {running.Job.IsTerminationPending}.");
            Assert.True(queued.Job.Status == TranscriptionJobStatus.Running, queued.Job.Error);
            Assert.Equal([queued, published], viewModel.JobRows);
            Assert.Null(viewModel.SelectedJobRow);
            Assert.Null(viewModel.SelectedJob);
            Assert.Equal(2, queue.Jobs.Count);
            Assert.Contains("completed outputs were kept", viewModel.Message);
            Assert.Contains("Previously published decision.", await File.ReadAllTextAsync(outputs.TextPath));
            viewModel.SelectedJobRow = published;
            viewModel.Language = "Brazilian Portuguese";
            await viewModel.RetranscribeSelectedRecordingCommand.ExecuteAsync(null);
            var requested = queue.Jobs.Single(job => job.Status == TranscriptionJobStatus.Queued);
            Assert.Equal(other.Id, requested.Recording.Id);
            Assert.Equal("Brazilian Portuguese", requested.Language);
            Assert.Equal("English", queued.Job.Language);
            Assert.Equal(completed.Id, viewModel.SelectedJob!.Id);
            Assert.NotEqual(completed.Id, requested.Id);
        }
        finally
        {
            await queue.CancelRecordingAsync(recording.Id, CancellationToken.None);
            await queue.CancelRecordingAsync(other.Id, CancellationToken.None);
            await queue.WaitForIdleAsync(CancellationToken.None);
            await window.RequestExitAsync();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MeetingWorkspace_PartialRecordingDeletionCanBeRetriedWithoutDeletingAnotherMeeting(bool allArtifacts) =>
        OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Recording(Guid.Parse("00000000-0000-0000-0000-000000000001")) with { Title = "Blocked meeting" };
        var other = context.Recording(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")) with { Title = "Other meeting" };
        await File.WriteAllBytesAsync(recording.AudioPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(other.AudioPath, [4, 5, 6]);
        var history = new JsonRecordingHistory(Path.Combine(context.Directory, "retry-history.json"));
        await history.AddAsync(recording, CancellationToken.None);
        await history.AddAsync(other, CancellationToken.None);
        var library = new RecordingLibrary(history, context.Repository, cancelRecordingAsync: context.Queue.CancelRecordingAsync,
            deleteJobAsync: context.Queue.DeleteJobAsync, releasePlaybackAsync: (_, _) => Task.CompletedTask);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models, history,
            new RecoveryManager(context.Recovery), context, context.Logger, library);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            await viewModel.InitializeCommand.ExecuteAsync(null);
            viewModel.SelectedRecording = viewModel.Recordings.Single(item => item.Id == recording.Id);
            using (var locked = File.Open(recording.AudioPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                await viewModel.DeleteRecordingAsync(allArtifacts);
                Assert.Equal(recording.Id, viewModel.SelectedRecording!.Id);
                Assert.True(viewModel.HasPendingDeletion);
                Assert.False(viewModel.IsDeletingRecording);
                Assert.True(viewModel.CanDeleteRecording);
                Assert.False(viewModel.CanRetranscribeSelectedRecording);
                Assert.Equal(allArtifacts ? RecordingDeletionScope.AllArtifacts : RecordingDeletionScope.AudioOnly,
                    viewModel.SelectedRecording.PendingDeletion!.Scope);
                Assert.Contains("Retry deletion", viewModel.Message);
                Assert.Contains(recording.AudioPath, viewModel.Message);
                Assert.Contains("Deletion incomplete", viewModel.SelectedRecordingInformation);
                Assert.True(File.Exists(recording.AudioPath));
                Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Warning &&
                    entry.Message.Contains("Recording deletion", StringComparison.Ordinal));
            }
            await viewModel.RetryRecordingDeletionCommand.ExecuteAsync(null);
            Assert.False(File.Exists(recording.AudioPath));
            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(other.AudioPath));
            Assert.False(viewModel.HasPendingDeletion);
            if (allArtifacts)
            {
                Assert.Equal(other.Id, Assert.Single(viewModel.Recordings).Id);
                Assert.Equal(other.Id, viewModel.SelectedRecording!.Id);
                Assert.Contains("all its generated artifacts", viewModel.Message);
            }
            else
            {
                Assert.Equal(2, viewModel.Recordings.Count);
                Assert.Equal(recording.Id, viewModel.SelectedRecording!.Id);
                Assert.True(viewModel.SelectedRecording.AudioDeleted);
                Assert.Contains("history were kept", viewModel.Message);
            }
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Theory]
    [InlineData(TranscriptionJobStatus.Failed)]
    [InlineData(TranscriptionJobStatus.Canceled)]
    [InlineData(TranscriptionJobStatus.Running)]
    public Task MeetingWorkspace_UnsuccessfulJobResidualsAreNotCompletedTranscripts(TranscriptionJobStatus status) =>
        OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var path = Path.Combine(context.Directory, "attempt.txt");
        await File.WriteAllTextAsync(path, "Unpublished attempt residual.");
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            var publishedPath = Path.Combine(context.Directory, "published.txt");
            await File.WriteAllTextAsync(publishedPath, "An earlier successful transcript.");
            var published = context.Job(context.Engine.Result, new(publishedPath, Path.ChangeExtension(publishedPath, ".srt")));
            context.ViewModel.SelectedJob = published;
            await context.ViewModel.ViewTranscriptCommand.ExecuteAsync(null);
            Assert.Equal("An earlier successful transcript.", context.ViewModel.Transcript);
            Assert.True(context.ViewModel.CanCopyTranscript);
            Assert.Equal(published.Id, context.ViewModel.CompletedTranscriptJobId);
            context.ViewModel.SelectedJob = new TranscriptionJob(Guid.NewGuid(), context.Engine.Result,
                DateTimeOffset.UtcNow, status, new(path, Path.ChangeExtension(path, ".srt")), "Attempt did not publish");
            await context.ViewModel.ViewTranscriptCommand.ExecuteAsync(null);
            Assert.Null(context.ViewModel.Transcript);
            Assert.False(context.ViewModel.CanCopyTranscript);
            Assert.Null(context.ViewModel.CompletedTranscriptJobId);
            Assert.Contains("successful transcription job", context.ViewModel.Message);
            Assert.Equal("Unpublished attempt residual.", await File.ReadAllTextAsync(path));
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_CatalogFiltersOfferCompatibleDownloadChoicesWithoutActivatingThem() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            await context.ViewModel.InitializeCommand.ExecuteAsync(null);
            context.ViewModel.ShowModelsCommand.Execute(null);
            var models = Assert.IsType<ScrollViewer>(window.FindName("ModelSettingsScroll"));
            Assert.Equal(Visibility.Visible, models.Visibility);
            var filter = Assert.IsType<System.Windows.Controls.ComboBox>(window.FindName("ModelCatalogFilter"));
            Assert.Equal(3, filter.Items.Count);
            Assert.Equal("All models", filter.SelectedItem);
            Assert.Equal(12, context.ViewModel.CatalogFamilies.Count);
            Assert.Equal(72, context.ViewModel.CatalogFamilies.Sum(family => family.Variants.Count));
            filter.SelectedIndex = 1;
            Assert.Equal(8, context.ViewModel.CatalogFamilies.Count);
            Assert.All(context.ViewModel.CatalogFamilies, family =>
            {
                Assert.Equal(6, family.Variants.Count);
                Assert.All(family.Variants, variant => Assert.Contains("Brazilian Portuguese", variant.Languages));
            });
            filter.SelectedIndex = 2;
            Assert.Equal(4, context.ViewModel.CatalogFamilies.Count);
            Assert.All(context.ViewModel.CatalogFamilies, family => Assert.All(family.Variants,
                variant => Assert.Equal(["English"], variant.Languages)));
            filter.SelectedIndex = 0;
            Assert.Equal(12, context.ViewModel.CatalogFamilies.Count);
            Assert.Equal(72, context.ViewModel.CatalogFamilies.Sum(family => family.Variants.Count));
            Assert.Empty(context.Models.DownloadedModels);
            Assert.Null(context.Models.SelectedModelId);
            context.ViewModel.ShowJobsCommand.Execute(null);
            Assert.Equal(Visibility.Collapsed, models.Visibility);
            Assert.True(context.ViewModel.IsJobsView);
            Assert.False(context.ViewModel.IsLibraryView);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(980, 720));
            content.Arrange(new Rect(0, 0, 980, 720));
            content.UpdateLayout();
            Assert.Contains("No transcription jobs yet.", VisualTexts(content));
            await context.Queue.EnqueueAsync(context.Engine.Result, CancellationToken.None);
            await context.Queue.WaitForIdleAsync(CancellationToken.None);
            context.ViewModel.Refresh();
            await UntilAsync(() => !VisualTexts(content).Contains("No transcription jobs yet."));
            Assert.Single(context.ViewModel.JobRows);
            context.ViewModel.ShowLibraryCommand.Execute(null);
            Assert.False(context.ViewModel.IsJobsView);
            Assert.True(context.ViewModel.IsLibraryView);
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_JobReviewDoesNotReadAReplacementAfterPartialDeletion() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Engine.Result;
        await File.WriteAllBytesAsync(recording.AudioPath, [1, 2, 3, 4]);
        context.Transcriber.Segments = [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Original completed transcript.")];
        var history = new JsonRecordingHistory(Path.Combine(context.Directory, "replacement-history.json"));
        var repository = new JsonTranscriptionJobRepository(Path.Combine(context.Directory, "replacement-jobs.json"));
        await history.AddAsync(recording, CancellationToken.None);
        var queue = new TranscriptionQueue(context.Transcriber, new AtomicTranscriptWriter(), repository, history: history);
        await queue.EnqueueAsync(recording, CancellationToken.None);
        await queue.WaitForIdleAsync(CancellationToken.None);
        var job = Assert.Single(queue.Jobs);
        var outputs = Assert.IsType<TranscriptOutputs>(job.Outputs);
        var library = new RecordingLibrary(history, repository, cancelRecordingAsync: queue.CancelRecordingAsync,
            deleteJobAsync: queue.DeleteJobAsync, releasePlaybackAsync: (_, _) => Task.CompletedTask);
        using var blockedSubtitle = File.Open(outputs.SubtitlePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var deletion = await library.DeleteRecordingAsync(recording.Id, RecordingDeletionScope.AllArtifacts, CancellationToken.None);
        Assert.Equal(LibraryDeletionStatus.Incomplete, deletion.Status);
        Assert.False(File.Exists(outputs.TextPath));
        await File.WriteAllTextAsync(outputs.TextPath, "Unowned replacement text.");
        var viewModel = new MainViewModel(context.Recorder, context.Engine, queue, context.Models, history,
            new RecoveryManager(context.Recovery), context, context.Logger, library);
        viewModel.Recordings.Add(Assert.Single(await history.LoadAsync(CancellationToken.None)));
        viewModel.Refresh();
        viewModel.SelectedJobRow = Assert.Single(viewModel.JobRows);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            await viewModel.ViewTranscriptCommand.ExecuteAsync(null);
            Assert.Null(viewModel.Transcript);
            Assert.False(viewModel.CanCopyTranscript);
            Assert.DoesNotContain("Unowned replacement", viewModel.Message);
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_UnavailableSearchAndHistoryActionsAreExplicit() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            context.ViewModel.SearchText = "query";
            Assert.Contains("search is unavailable", context.ViewModel.Message);
            Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("search", StringComparison.OrdinalIgnoreCase));
            await context.ViewModel.StopJobCommand.ExecuteAsync(null);
            Assert.Contains("running transcription job", context.ViewModel.Message);
            Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("Stop", StringComparison.Ordinal));
            await context.ViewModel.DeleteSelectedJobHistoryAsync();
            Assert.Contains("terminal transcription job", context.ViewModel.Message);
            Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("Job-history deletion", StringComparison.Ordinal));
            await context.ViewModel.DeleteRecordingAsync(false);
            Assert.Contains("Select a recording", context.ViewModel.Message);
            context.ViewModel.SelectedRecording = context.Engine.Result;
            Assert.False(context.ViewModel.CanDeleteRecording);
            var unavailableDelete = Assert.IsType<System.Windows.Controls.Button>(window.FindName("DeleteRecordingButton"));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.False(unavailableDelete.IsEnabled);
            Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("Recording deletion", StringComparison.Ordinal));
            await context.ViewModel.RetryRecordingDeletionCommand.ExecuteAsync(null);
            Assert.Contains("unfinished deletion", context.ViewModel.Message);
            Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("Deletion retry", StringComparison.Ordinal));
            await context.ViewModel.DownloadCatalogModelCommand.ExecuteAsync(null);
            Assert.Contains("Select a catalog model", context.ViewModel.Message);
            Assert.False(context.ViewModel.IsDownloadingModel);
            Assert.Empty(context.Models.DownloadedModels);
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_LibraryDisplaysTitleOrSavedFilename() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        context.ViewModel.Recordings.Add(context.Engine.Result with { Title = "Named meeting" });
        context.ViewModel.Recordings.Add(context.Engine.Result with
        {
            Id = Guid.NewGuid(),
            AudioPath = Path.Combine(context.Directory, "untitled-meeting.wav"),
        });
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(980, 720));
            content.Arrange(new Rect(0, 0, 980, 720));
            content.UpdateLayout();
            var history = Assert.IsType<System.Windows.Controls.ListBox>(window.FindName("HistoryList"));
            var texts = VisualTexts(history).ToArray();
            Assert.Contains("Named meeting", texts);
            Assert.Contains("untitled-meeting", texts);
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    private static IEnumerable<DependencyObject> LogicalElements(DependencyObject element)
    {
        yield return element;
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
        {
            foreach (var descendant in LogicalElements(child)) yield return descendant;
        }
    }

    private static IEnumerable<string> VisualTexts(DependencyObject element)
    {
        if (element is TextBlock text) yield return text.Text;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); index++)
        {
            foreach (var value in VisualTexts(System.Windows.Media.VisualTreeHelper.GetChild(element, index)))
            {
                yield return value;
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task MeetingWorkspace_LegacyReviewReadsActualOutputOrReportsMissingFile(bool available) => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var path = Path.Combine(context.Directory, "legacy-review.txt");
        if (available) await File.WriteAllTextAsync(path, "Legacy retained transcript.");
        var recording = context.Engine.Result;
        context.ViewModel.Recordings.Add(recording);
        context.ViewModel.Jobs.Add(context.Job(recording, new(path, Path.Combine(context.Directory, "legacy-review.srt"))));
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            context.ViewModel.SelectedRecording = recording;
            if (available)
            {
                await UntilAsync(() => context.ViewModel.Transcript == "Legacy retained transcript.");
                Assert.True(context.ViewModel.CanCopyTranscript);
                using (var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    context.ViewModel.SelectedJob = Assert.Single(context.ViewModel.Jobs);
                    await context.ViewModel.ViewTranscriptCommand.ExecuteAsync(null);
                    Assert.Null(context.ViewModel.Transcript);
                    Assert.Contains("completed transcript could not be read", context.ViewModel.Message);
                    Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Error &&
                        entry.Message.Contains(nameof(IOException), StringComparison.Ordinal));
                    Assert.DoesNotContain(context.Logger.Entries, entry => entry.Message.Contains(path, StringComparison.Ordinal));
                }
            }
            else
            {
                await UntilAsync(() => context.ViewModel.Message.Contains("could not be read", StringComparison.Ordinal));
                Assert.Null(context.ViewModel.Transcript);
                Assert.False(File.Exists(path));
                Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Error &&
                    entry.Message.Contains(recording.Id.ToString(), StringComparison.Ordinal) &&
                    entry.Message.Contains(nameof(FileNotFoundException), StringComparison.Ordinal));
                Assert.DoesNotContain(context.Logger.Entries, entry => entry.Message.Contains(path, StringComparison.Ordinal));
            }
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task MeetingWorkspace_SavedCaptureWaitsForSuccessfulModelSelection(bool succeeds) => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var model = await context.Models.RegisterAsync("existing.zip", CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Models.Selecting = token => release.Task.WaitAsync(token);
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            await context.ViewModel.InitializeCommand.ExecuteAsync(null);
            context.ViewModel.SelectedModel = model;
            await UntilAsync(() => context.ViewModel.IsSelectingModel);
            var selector = Assert.IsType<System.Windows.Controls.ComboBox>(window.FindName("DownloadedModelSelector"));
            Assert.False(selector.IsEnabled);
            Assert.True(context.ViewModel.HasPendingViewWork);
            context.ViewModel.SelectedRecording = context.Engine.Result;
            Assert.False(context.ViewModel.CanRetranscribeSelectedRecording);
            Assert.False(context.ViewModel.CanRestartSelectedJob);
            context.PrepareRecording();
            await context.ViewModel.StartRecordingAsync(false);
            var saved = await context.ViewModel.StopAsync(enqueueTranscription: true);
            Assert.Same(context.Engine.Result, saved);
            Assert.Same(saved, Assert.Single(context.ViewModel.Recordings));
            Assert.Empty(context.Queue.Jobs);
            Assert.True(context.ViewModel.IsIdle);
            Assert.False(context.ViewModel.PermissionConfirmed);
            Assert.Contains("waits for the pending model selection", context.ViewModel.Message);
            Task? compatibilityRetry = null;
            if (!succeeds)
            {
                compatibilityRetry = context.ViewModel.RetryTranscriptionCommand.ExecuteAsync(null);
                Assert.False(compatibilityRetry.IsCompleted);
                context.Models.Error = new IOException("Selection verification failed.");
            }
            release.SetResult();
            if (succeeds)
            {
                await UntilAsync(() => context.Queue.Jobs.Count == 1);
                Assert.Same(saved, Assert.Single(context.Queue.Jobs).Recording);
            }
            else
            {
                await UntilAsync(() => context.ViewModel.Message.Contains("explicit retry", StringComparison.Ordinal));
                await compatibilityRetry!;
                Assert.Empty(context.Queue.Jobs);
                Assert.Same(saved, Assert.Single(context.ViewModel.Recordings));
            }
            await UntilAsync(() => !context.ViewModel.HasPendingViewWork);
            Assert.True(selector.IsEnabled);
            Assert.True(context.ViewModel.CanRetranscribeSelectedRecording);
            Assert.False(context.ViewModel.CanRestartSelectedJob);
            if (succeeds)
            {
                context.Engine.Result = context.Recording(Guid.NewGuid());
                context.PrepareRecording();
                await context.ViewModel.StartRecordingAsync(false);
                await context.ViewModel.StopAsync(enqueueTranscription: true);
                Assert.Equal(2, context.Queue.Jobs.Count);
                Assert.DoesNotContain("waits for the pending model selection", context.ViewModel.Message);
            }
        }
        finally
        {
            release.TrySetResult();
            await window.RequestExitAsync();
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task MeetingWorkspace_ModelSelectionFailureOrIncompatibilityDoesNotSwitchSilently(bool failure) => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var registered = await context.Models.RegisterAsync("existing.zip", CancellationToken.None);
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            await context.ViewModel.InitializeCommand.ExecuteAsync(null);
            context.ViewModel.SelectedModel = registered;
            await UntilAsync(() => context.ViewModel.ModelHeader == registered.DisplayName);
            if (failure)
            {
                var entry = SoundRecorder.Infrastructure.Models.TranscriptionModelCatalog.Entries.Single(
                    item => item.Key == "whisper.net:small:Q5_1");
                var alternative = await context.Models.DownloadAsync(entry, new Progress<ModelDownloadProgress>(), CancellationToken.None);
                context.Models.Error = new IOException("Selected model is unavailable");
                context.ViewModel.SelectedModel = alternative;
                await UntilAsync(() => context.ViewModel.Message.Contains("could not be selected", StringComparison.Ordinal));
                Assert.Equal(registered.Id, context.Models.SelectedModelId);
                Assert.Equal(registered.Id, context.ViewModel.SelectedModel!.Id);
                Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Error &&
                    entry.Message.Contains("Downloaded model selection failed", StringComparison.Ordinal) &&
                    entry.Message.Contains(nameof(IOException), StringComparison.Ordinal));
                Assert.DoesNotContain(context.Logger.Entries, entry => entry.Message.Contains("Selected model is unavailable",
                    StringComparison.Ordinal));
            }
            else
            {
                context.Models.CurrentPackage = context.Models.Package with { Languages = ["English"] };
                context.ViewModel.Language = "Brazilian Portuguese";
                Assert.Contains("compatible multilingual downloaded model", context.ViewModel.Message);
                Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Warning &&
                    entry.Message.Contains("Brazilian Portuguese", StringComparison.Ordinal));
                Assert.Equal(registered.Id, context.Models.SelectedModelId);
                Assert.True(context.ViewModel.StartCommand.CanExecute(null));
                context.ViewModel.Language = "English";
                Assert.Contains("supports the new-job language", context.ViewModel.Message);
            }
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task MeetingWorkspace_JobProgressAndReleaseGatePreserveSelection(bool stop) => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        context.Transcriber.Block = true;
        context.Transcriber.CancellationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await context.Queue.EnqueueAsync(context.Engine.Result, CancellationToken.None);
        await context.Transcriber.Started.Task;
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            context.ViewModel.Refresh();
            var row = Assert.Single(context.ViewModel.JobRows);
            context.ViewModel.SelectedJobRow = row;
            context.Transcriber.Progress!.Report(new(TranscriptionPhase.Recognizing, 37));
            context.ViewModel.Refresh();
            Assert.Equal(37d, row.ProgressPercent);
            Assert.False(row.IsProgressIndeterminate);
            Assert.Equal("Recognizing / 37% recognition estimate", row.ProgressText);
            var ending = stop ? context.ViewModel.StopJobCommand.ExecuteAsync(null) :
                context.ViewModel.CancelJobCommand.ExecuteAsync(null);
            await context.Transcriber.CancellationRequested.Task;
            context.ViewModel.Refresh();
            Assert.Equal(stop ? "Stopping" : "Cancelling", row.Status);
            Assert.False(row.CanDelete);
            Assert.False(row.CanRestart);
            context.Transcriber.CancellationGate.SetResult();
            await ending;
            await context.Queue.WaitForIdleAsync(CancellationToken.None);
            context.ViewModel.Refresh();
            Assert.Same(row, context.ViewModel.SelectedJobRow);
            Assert.Equal(stop ? "Stopped" : "Cancelled", row.Status);
            Assert.True(row.CanRestart);
            Assert.True(row.CanDelete);
        }
        finally
        {
            context.Transcriber.CancellationGate.TrySetResult();
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_SearchReadsRetainedTextAndReportsMissingAudio() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Engine.Result;
        await File.WriteAllBytesAsync(recording.AudioPath, [1, 2, 3, 4]);
        var history = new JsonRecordingHistory(Path.Combine(context.Directory, "search-history.json"));
        var repository = new JsonTranscriptionJobRepository(Path.Combine(context.Directory, "search-jobs.json"));
        var jobId = Guid.NewGuid();
        var outputs = await new AtomicTranscriptWriter().WriteAsync(new TranscriptDocument(recording, recording.Language,
            [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Retained handover decisions.")]), CancellationToken.None);
        await history.AddAsync(recording with { TranscriptOutputs = [new(jobId, outputs)] }, CancellationToken.None);
        var other = recording with { Id = Guid.NewGuid(), AudioPath = Path.Combine(context.Directory, "other-search.wav"), Title = "Budget" };
        await File.WriteAllBytesAsync(other.AudioPath, [1, 2, 3, 4]);
        await history.AddAsync(other, CancellationToken.None);
        var library = new RecordingLibrary(history, repository);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models, history,
            new RecoveryManager(context.Recovery), context, context.Logger, library);
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
            new DesktopLogger<MainWindow>());
        try
        {
            await viewModel.InitializeCommand.ExecuteAsync(null);
            viewModel.SelectedRecording = viewModel.Recordings.Single(item => item.Id == recording.Id);
            viewModel.SearchText = "handover";
            await UntilAsync(() => viewModel.FilteredRecordings.Count == 1 && viewModel.LibraryMessage == string.Empty);
            var selected = viewModel.SelectedRecording;
            viewModel.SearchText = "Budget";
            await UntilAsync(() => viewModel.FilteredRecordings.Count == 1 && viewModel.FilteredRecordings[0].Id == other.Id);
            var recovered = context.Recording(Guid.NewGuid()) with { Title = "Budget follow-up", Outcome = RecordingOutcome.Recovered };
            await File.WriteAllBytesAsync(recovered.AudioPath, [7, 8, 9]);
            context.Recovery.Result = recovered;
            await viewModel.RecoverAsync(context.Recovery.Candidate);
            Assert.Equal("Budget", viewModel.SearchText);
            Assert.Equal(new[] { other.Id, recovered.Id }.Order(), viewModel.FilteredRecordings.Select(item => item.Id).Order());
            Assert.Same(selected, viewModel.SelectedRecording);
            Assert.Empty(context.Queue.Jobs);
            viewModel.SearchText = "unmatched";
            await UntilAsync(() => viewModel.FilteredRecordings.Count == 0);
            Assert.Contains("No matching meetings", viewModel.LibraryMessage);
            Assert.Same(selected, viewModel.SelectedRecording);
            File.Delete(recording.AudioPath);
            viewModel.SearchText = Path.GetFileNameWithoutExtension(recording.AudioPath);
            await UntilAsync(() => viewModel.LibraryMessage.Contains("may be incomplete", StringComparison.Ordinal));
            Assert.False(File.Exists(recording.AudioPath));
            viewModel.SearchText = string.Empty;
            await UntilAsync(() => viewModel.FilteredRecordings.Count == 3);
            Assert.Equal("Retained handover decisions.", (await library.ReadTranscriptAsync(recording.Id, jobId,
                CancellationToken.None)).Text!.Split(']', 2)[1].Trim());
            using (var lockedHistory = File.Open(Path.Combine(context.Directory, "search-history.json"),
                       FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var entry = viewModel.SelectedRecording;
                viewModel.SelectedRecording = null;
                viewModel.SelectedRecording = entry;
                await UntilAsync(() => viewModel.Message.Contains("retained transcript could not be read", StringComparison.Ordinal));
                Assert.Null(viewModel.Transcript);
                Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Error &&
                    entry.Message.Contains(recording.Id.ToString(), StringComparison.Ordinal) &&
                    entry.Message.Contains(nameof(IOException), StringComparison.Ordinal));
                viewModel.SearchText = "history unavailable";
                await UntilAsync(() => viewModel.Message.Contains("Library search could not read", StringComparison.Ordinal));
                Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Error &&
                    entry.Message.Contains(nameof(IOException), StringComparison.Ordinal) &&
                    entry.Message.Contains("search", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Theory]
    [InlineData("complete")]
    [InlineData("cancel")]
    [InlineData("fail")]
    public Task MeetingWorkspace_ModelDownloadKeepsCaptureAndSelectionIndependent(string outcome) => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        await context.Models.RegisterAsync("existing.zip", CancellationToken.None);
        context.Models.CurrentPackage = context.Models.Package;
        context.Models.CatalogGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        var entry = SoundRecorder.Infrastructure.Models.TranscriptionModelCatalog.Entries.Single(
            item => item.Key == "whisper.net:small:Q5_1");
        try
        {
            var download = context.ViewModel.DownloadCatalogModelCommand.ExecuteAsync(entry);
            await UntilAsync(() => context.ViewModel.ModelDownloadPercent == 25);
            Assert.True(context.ViewModel.IsDownloadingModel);
            Assert.True(context.ViewModel.HasPendingViewWork);
            Assert.False(context.ViewModel.IsBusy);
            Assert.False(context.ViewModel.IsModelDownloadIndeterminate);
            var progress = Assert.IsType<System.Windows.Controls.ProgressBar>(window.FindName("ModelDownloadProgress"));
            Assert.Equal(25, progress.Value);
            Assert.False(progress.IsIndeterminate);
            Assert.Equal("Transferred bytes estimate", System.Windows.Automation.AutomationProperties.GetItemStatus(progress));
            context.PrepareRecording();
            await context.ViewModel.StartCommand.ExecuteAsync(null);
            Assert.Equal("Recording", context.ViewModel.Status);
            context.ViewModel.ShowLibraryCommand.Execute(null);
            Assert.Contains("25%", context.ViewModel.ModelHeader);
            Assert.DoesNotContain(context.ViewModel.DownloadedModels, model => model.Id == entry.Key);
            if (outcome == "cancel")
            {
                context.ViewModel.CancelModelDownloadCommand.Execute(null);
            }
            else
            {
                context.Models.Error = outcome == "fail" ? new System.Net.Http.HttpRequestException("fixture unavailable") : null;
                context.Models.CatalogGate.SetResult();
            }
            await download;
            Assert.False(context.ViewModel.IsDownloadingModel);
            Assert.False(context.ViewModel.HasPendingViewWork);
            Assert.Equal(context.Models.Package.Id, context.Models.SelectedModelId);
            Assert.Equal(context.Models.Package.Id, context.ViewModel.SelectedModel!.Id);
            Assert.Equal(context.Models.Package.Id, context.ViewModel.ModelHeader);
            Assert.Equal("Recording", context.ViewModel.Status);
            if (outcome == "complete")
            {
                Assert.Contains(context.ViewModel.DownloadedModels, model => model.Id == entry.Key);
                Assert.Contains("Choose it from Downloaded models", context.ViewModel.ModelDownloadStatus);
                context.ViewModel.SelectedModel = context.ViewModel.DownloadedModels.Single(model => model.Id == entry.Key);
                await UntilAsync(() => !context.ViewModel.IsSelectingModel && context.Models.SelectedModelId == entry.Key);
                Assert.Equal(entry.Key, context.ViewModel.SelectedModel!.Id);
                Assert.Equal("small Q5_1", context.ViewModel.ModelHeader);
                var header = Assert.IsType<System.Windows.Controls.Button>(window.FindName("ModelsButton"));
                await UntilAsync(() => Equals("small Q5__1", header.Content));
                Assert.Empty(context.Models.Downloads);
            }
            else
            {
                Assert.DoesNotContain(context.ViewModel.DownloadedModels, model => model.Id == entry.Key);
                Assert.Contains(outcome == "cancel" ? "cancelled" : "failed",
                    context.ViewModel.ModelDownloadStatus, StringComparison.OrdinalIgnoreCase);
                if (outcome == "fail")
                {
                    Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Error &&
                        entry.Message.Contains(nameof(System.Net.Http.HttpRequestException), StringComparison.Ordinal));
                    Assert.Contains("fixture unavailable", context.ViewModel.Message);
                }
            }
        }
        finally
        {
            context.Models.CatalogGate.TrySetResult();
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_DeletingJobThenAudioRetainsReviewUntilFullDeletion() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var recording = context.Engine.Result;
        context.Transcriber.Segments = [new(TimeSpan.Zero, TimeSpan.FromSeconds(5), "Retained workspace transcript.")];
        using (var wave = new NAudio.Wave.WaveFileWriter(recording.AudioPath, new NAudio.Wave.WaveFormat(48_000, 16, 1)))
        {
            wave.Write(new byte[5 * 96_000], 0, 5 * 96_000);
        }
        var history = new JsonRecordingHistory(Path.Combine(context.Directory, "workspace-history.json"));
        var repository = new JsonTranscriptionJobRepository(Path.Combine(context.Directory, "workspace-jobs.json"));
        await history.AddAsync(recording, CancellationToken.None);
        var seedQueue = new TranscriptionQueue(context.Transcriber, new AtomicTranscriptWriter(), repository, history: history);
        await seedQueue.EnqueueAsync(recording, CancellationToken.None);
        await seedQueue.WaitForIdleAsync(CancellationToken.None);
        var job = Assert.Single(seedQueue.Jobs);
        var outputs = Assert.IsType<TranscriptOutputs>(job.Outputs);
        var expectedText = await File.ReadAllTextAsync(outputs.TextPath);
        Assert.Contains("Retained workspace transcript.", expectedText);
        var subtitles = await File.ReadAllBytesAsync(outputs.SubtitlePath);
        var queue = new TranscriptionQueue(context.Transcriber, new AtomicTranscriptWriter(), repository, history: history);
        using var playback = new WindowsRecordingPlayback();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var library = new RecordingLibrary(history, repository, cancelRecordingAsync: queue.CancelRecordingAsync,
            deleteJobAsync: queue.DeleteJobAsync, releasePlaybackAsync: (id, token) =>
                dispatcher.InvokeAsync(() => { if (playback.RecordingId == id) playback.Close(); },
                    DispatcherPriority.Normal, token).Task);
        var recorder = new RecorderApplication(context.Engine, queue, context.Models, history);
        var viewModel = new MainViewModel(recorder, context.Engine, queue, context.Models, history,
            new RecoveryManager(context.Recovery), context, context.Logger, library, playback);
        var environment = new DesktopEnvironment();
        var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs { Answer = MessageBoxResult.Yes }, environment,
            new DesktopLogger<MainWindow>());
        try
        {
            await viewModel.InitializeCommand.ExecuteAsync(null);
            Assert.Empty(context.Logger.Errors);
            Assert.Single(viewModel.Recordings);
            await UntilAsync(() => viewModel.Transcript == expectedText);
            viewModel.SelectedJobRow = Assert.Single(viewModel.JobRows);
            using (var lockedRepository = File.Open(Path.Combine(context.Directory, "workspace-jobs.json"),
                       FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await viewModel.DeleteSelectedJobHistoryAsync();
                Assert.Contains("was not deleted", viewModel.Message);
                Assert.Single(queue.Jobs);
            }
            Click(window, "Delete job...");
            await UntilAsync(() => queue.Jobs.Count == 0);
            Assert.Empty(queue.Jobs);
            Assert.Equal(expectedText, viewModel.Transcript);
            Assert.Equal(subtitles, await File.ReadAllBytesAsync(outputs.SubtitlePath));
            playback.Open(recording);
            await viewModel.DeleteRecordingAsync(false);
            Assert.False(File.Exists(recording.AudioPath));
            Assert.Null(playback.RecordingId);
            Assert.Null(viewModel.PlaybackSourcePath);
            Assert.True(viewModel.SelectedRecording!.AudioDeleted);
            Assert.False(viewModel.HasPlayableRecording);
            Assert.False(viewModel.IsDeletingRecording);
            Assert.True(viewModel.CanDeleteRecording);
            Assert.Contains("Audio deleted", viewModel.SelectedRecordingInformation);
            Assert.Contains("Transcripts", viewModel.Message);
            var retained = await library.ReadTranscriptAsync(recording.Id, job.Id, CancellationToken.None);
            Assert.True(retained.Text == expectedText, string.Join(" ", retained.Issues.Select(issue => issue.Message)));
            await UntilAsync(() => viewModel.Transcript == expectedText && viewModel.CanCopyTranscript);
            Assert.Equal(job.Id, viewModel.CompletedTranscriptJobId);
            Assert.Equal("Completed transcript", viewModel.TranscriptReviewLabel);
            Assert.False(viewModel.CanRetranscribeSelectedRecording);
            await viewModel.RetryTranscriptionCommand.ExecuteAsync(null);
            Assert.Empty(queue.Jobs);
            Assert.Contains("Audio is deleted", viewModel.Message);
            window.OpenOutputFolder(viewModel.SelectedRecording);
            Assert.Equal(outputs.TextPath, Assert.Single(environment.Opened));
            await viewModel.DeleteRecordingAsync(true);
            Assert.Empty(viewModel.Recordings);
            Assert.Null(viewModel.SelectedRecording);
            Assert.False(File.Exists(outputs.TextPath));
            Assert.False(File.Exists(outputs.SubtitlePath));
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_ClearingRecordingClearsTranscript() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        context.ViewModel.SelectedRecording = context.Engine.Result;
        context.ViewModel.Transcript = "Previously displayed transcript";
        context.ViewModel.SelectedRecording = null;
        Assert.Null(context.ViewModel.Transcript);
        await Task.CompletedTask;
    });

    [Fact]
    public Task MeetingWorkspace_StartCollectsPermissionWithoutCapturing() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new System.Windows.Size(980, 720));
            content.Arrange(new Rect(0, 0, 980, 720));
            content.UpdateLayout();
            Assert.True(context.ViewModel.StartCommand.CanExecute(null));
            await context.ViewModel.StartCommand.ExecuteAsync(null);
            content.UpdateLayout();
            var setup = Assert.IsType<Expander>(window.FindName("RecordingSetupExpander"));
            Assert.True(setup.IsExpanded);
            Assert.False(context.ViewModel.PermissionConfirmed);
            Assert.Equal("Idle", context.ViewModel.Status);
            Assert.Empty(context.ViewModel.Recordings);
            Assert.Contains("permission", context.ViewModel.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Information &&
                entry.Message.Contains("permission", StringComparison.Ordinal));
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task MeetingWorkspace_KeepsLibraryAndReaderAboveCaptureDock() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        try
        {
            await context.ViewModel.InitializeCommand.ExecuteAsync(null);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new System.Windows.Size(980, 720));
            content.Arrange(new Rect(0, 0, 980, 720));
            content.UpdateLayout();
            var library = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("LibraryPane"));
            var reader = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ReaderPane"));
            var dock = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("CaptureDock"));
            var libraryOrigin = library.TranslatePoint(new System.Windows.Point(), content);
            var readerOrigin = reader.TranslatePoint(new System.Windows.Point(), content);
            var dockOrigin = dock.TranslatePoint(new System.Windows.Point(), content);
            Assert.True(libraryOrigin.X + library.ActualWidth <= readerOrigin.X);
            Assert.True(readerOrigin.Y + reader.ActualHeight <= dockOrigin.Y,
                $"Reader bottom {readerOrigin.Y + reader.ActualHeight}, capture top {dockOrigin.Y}.");
            Assert.True(library.ActualWidth > 0 && reader.ActualWidth > 0 && dock.ActualHeight > 0);
            var emptyReader = VisualTexts(reader).ToArray();
            Assert.Contains("Select a recording", emptyReader);
            Assert.Contains("Your saved meetings and transcripts will appear here.", emptyReader);
            var libraryMessage = Assert.IsType<TextBlock>(window.FindName("LibraryMessageText"));
            Assert.Contains("No recordings", libraryMessage.Text);
            Assert.False(context.ViewModel.CanDeleteRecording);
            Assert.False(context.ViewModel.HasPlayableRecording);
            Assert.False(context.ViewModel.CanRetranscribeSelectedRecording);
            var recording = context.Engine.Result with { AudioPath = Path.Combine(context.Directory, "untitled-meeting.wav") };
            context.ViewModel.Recordings.Add(recording);
            var history = Assert.IsType<System.Windows.Controls.ListBox>(window.FindName("HistoryList"));
            await UntilAsync(() => history.Items.Count == 1);
            history.SelectedItem = recording;
            await UntilAsync(() => VisualTexts(reader).Contains("untitled-meeting"));
            Assert.Contains("English / 00:00:05", VisualTexts(reader));
            Assert.Equal(string.Empty, libraryMessage.Text);
            Assert.Equal(recording.Id, context.ViewModel.SelectedRecording!.Id);
        }
        finally
        {
            await window.RequestExitAsync();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ChooseDestination_Selection_ChangesOnlyConfirmedFolder(bool selected) => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var dialogs = new DesktopDialogs { Folder = selected ? context.Directory : null };
        var environment = new DesktopEnvironment();
        var window = CreateWindow(context, dialogs, environment);
        context.ViewModel.DestinationFolder = "original";
        Click(window, "Choose...");
        Assert.Equal(selected ? context.Directory : "original", context.ViewModel.DestinationFolder);
        Assert.Equal(1, dialogs.FolderRequests);
        Assert.Empty(dialogs.Prompts);
        await window.RequestExitAsync();
        Assert.Equal(1, environment.Disposals);
    });

    [Theory]
    [InlineData(null)]
    [InlineData("selected.zip")]
    public Task ImportModelAsync_Selection_InstallsOnlyExplicitPackage(string? package) => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var dialogs = new DesktopDialogs { Package = package };
        var window = CreateWindow(context, dialogs, new DesktopEnvironment());
        var commandNotifications = 0;
        ((INotifyPropertyChanged)context.ViewModel.ImportModelCommand).PropertyChanged += (_, _) =>
            commandNotifications++;
        await window.ImportModelAsync();
        Assert.Equal(package is null ? [] : new[] { package }, context.Models.Imports);
        Assert.Equal(1, dialogs.PackageRequests);
        Assert.Equal(package is not null, commandNotifications > 0);
        await window.RequestExitAsync();
    });

    [Theory]
    [InlineData(MessageBoxResult.OK)]
    [InlineData(MessageBoxResult.Cancel)]
    public Task ViewTranscriptAsync_UserChoice_CopiesOnlyOnExplicitApproval(MessageBoxResult answer) =>
        OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var dialogs = new DesktopDialogs { Answer = answer };
            var environment = new DesktopEnvironment();
            var window = CreateWindow(context, dialogs, environment);
            var path = Path.Combine(context.Directory, "transcript.txt");
            await File.WriteAllTextAsync(path, "Private conversation");
            context.ViewModel.SelectedJob = context.Job(context.Engine.Result, new(path, "captions.srt"));
            await window.ViewTranscriptAsync();
            var prompt = Assert.Single(dialogs.Prompts);
            Assert.Equal(("Private conversation", "Transcript", MessageBoxButton.OKCancel,
                MessageBoxImage.None, MessageBoxResult.None), prompt);
            Assert.Equal(answer == MessageBoxResult.OK ? ["Private conversation"] : Array.Empty<string>(),
                environment.Copied);
            await window.RequestExitAsync();
        });

    [Fact]
    public Task ViewTranscriptAsync_NoOutput_DoesNotOpenDialogOrCopy() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var dialogs = new DesktopDialogs();
        var environment = new DesktopEnvironment();
        var window = CreateWindow(context, dialogs, environment);
        await window.ViewTranscriptAsync();
        Assert.Empty(dialogs.Prompts);
        Assert.Empty(environment.Copied);
        Assert.Equal("Select a successful transcription job.", context.ViewModel.Message);
        await window.RequestExitAsync();
    });

    [Fact]
    public Task OpenOutputFolder_MissingOrExistingRecording_OnlyRevealsExistingFile() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var environment = new DesktopEnvironment();
        var window = CreateWindow(context, new DesktopDialogs(), environment);
        window.OpenOutputFolder(null);
        Assert.Empty(environment.Opened);
        Assert.Equal(string.Empty, context.ViewModel.Message);
        var recording = context.Engine.Result;
        window.OpenOutputFolder(recording);
        Assert.Equal("The recording file is missing: " + recording.AudioPath, context.ViewModel.Message);
        Assert.False(File.Exists(recording.AudioPath));
        await File.WriteAllBytesAsync(recording.AudioPath, [1]);
        context.ViewModel.Message = "Recording is available";
        window.OpenOutputFolder(recording);
        Assert.Equal(recording.AudioPath, Assert.Single(environment.Opened));
        Assert.Equal("Recording is available", context.ViewModel.Message);
        await window.RequestExitAsync();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LoadAsync_RecoveryChoice_RecoversOnlyAfterApproval(bool recover) => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var dialogs = new DesktopDialogs { Answer = recover ? MessageBoxResult.Yes : MessageBoxResult.No };
        var environment = new DesktopEnvironment();
        var logger = new DesktopLogger<MainWindow>();
        var window = new MainWindow(context.ViewModel, context.Engine, dialogs, environment, logger);
        await window.LoadAsync();
        var prompt = Assert.Single(dialogs.Prompts);
        Assert.Equal($"Recover available audio from the recording last saved at {context.Recovery.Candidate.LastSavedAt}?",
            prompt.Message);
        Assert.Equal("Recover interrupted recording", prompt.Title);
        Assert.Equal(MessageBoxButton.YesNo, prompt.Buttons);
        Assert.Equal(MessageBoxImage.Question, prompt.Image);
        Assert.Equal(recover, context.Recovery.Attempted is not null);
        Assert.Equal(recover ? 1 : 0, context.ViewModel.Recordings.Count);
        Assert.Empty(context.Queue.Jobs);
        Assert.Empty(logger.Errors);
        await window.RequestExitAsync();
    });

    [Fact]
    public Task LoadAsync_RecoveryReadFails_LogsAndLeavesWindowAvailable() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        context.Recovery.FindError = new IOException("unreadable recovery directory");
        var logger = new DesktopLogger<MainWindow>();
        var window = new MainWindow(context.ViewModel, context.Engine,
            new DesktopDialogs(), new DesktopEnvironment(), logger);
        await window.LoadAsync();
        Assert.Same(context.Recovery.FindError, Assert.Single(logger.Errors));
        Assert.StartsWith("Some local state could not be loaded. The app remains available; see ",
            context.ViewModel.Message);
        await window.RequestExitAsync();
    });

    [Fact]
    public Task RefreshStatus_DurationBoundaryAndNewSession_WarnsOncePerRecordingAndMaintainsWakeState() =>
        OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var environment = new DesktopEnvironment();
            var window = CreateWindow(context, new DesktopDialogs(), environment);
            await context.ViewModel.InitializeCommand.ExecuteAsync(null);
            var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            content.Measure(new Size(980, 720));
            content.Arrange(new Rect(0, 0, 980, 720));
            content.UpdateLayout();
            var start = Assert.IsType<System.Windows.Controls.Button>(window.FindName("StartButton"));
            var pause = Assert.IsType<System.Windows.Controls.Button>(window.FindName("PauseButton"));
            var resume = Assert.IsType<System.Windows.Controls.Button>(window.FindName("ResumeButton"));
            var stop = Assert.IsType<System.Windows.Controls.Button>(window.FindName("StopButton"));
            Assert.Equal(Visibility.Visible, start.Visibility);
            Assert.Equal(Visibility.Collapsed, stop.Visibility);
            window.RefreshStatus();
            Assert.False(environment.PreventSleep.Last());
            Assert.Equal("Sound Recorder", environment.Texts.Last());
            Assert.Empty(environment.Balloons);
            context.PrepareRecording();
            await context.ViewModel.StartRecordingAsync(false);
            await UntilAsync(() => pause.Visibility == Visibility.Visible);
            Assert.Equal(Visibility.Collapsed, start.Visibility);
            Assert.Equal(Visibility.Collapsed, resume.Visibility);
            Assert.Equal(Visibility.Visible, stop.Visibility);
            context.Engine.CapturedDuration = RecordingFormat.WarningCapturedDuration - TimeSpan.FromTicks(1);
            window.RefreshStatus();
            Assert.True(environment.PreventSleep.Last());
            Assert.Equal("Sound Recorder - recording", environment.Texts.Last());
            Assert.Empty(environment.Balloons);
            context.Engine.CapturedDuration = RecordingFormat.WarningCapturedDuration;
            window.RefreshStatus();
            Assert.Equal("03:55:00", context.ViewModel.CapturedDuration);
            Assert.Equal("The four-hour captured-duration limit is approaching.", context.ViewModel.Message);
            Assert.Equal((5000, "Recording limit approaching",
                "The recording will save normally at four hours of captured audio.", ToolTipIcon.Warning),
                Assert.Single(environment.Balloons));
            window.RefreshStatus();
            Assert.Single(environment.Balloons);
            await context.ViewModel.PauseCommand.ExecuteAsync(null);
            await UntilAsync(() => resume.Visibility == Visibility.Visible);
            Assert.Equal(Visibility.Collapsed, pause.Visibility);
            Assert.Equal(Visibility.Visible, stop.Visibility);
            window.RefreshStatus();
            Assert.True(environment.PreventSleep.Last());
            Assert.Single(environment.Balloons);
            await context.ViewModel.StopAsync(false);
            await UntilAsync(() => start.Visibility == Visibility.Visible);
            Assert.Equal(Visibility.Collapsed, pause.Visibility);
            Assert.Equal(Visibility.Collapsed, resume.Visibility);
            Assert.Equal(Visibility.Collapsed, stop.Visibility);
            window.RefreshStatus();
            Assert.False(environment.PreventSleep.Last());
            Assert.Single(environment.Balloons);
            context.PrepareRecording();
            await context.ViewModel.StartRecordingAsync(false);
            window.RefreshStatus();
            Assert.Equal(2, environment.Balloons.Count);
            await context.ViewModel.StopAsync(false);
            await window.RequestExitAsync();
        });

    [Fact]
    public Task RefreshStatus_FirstRefreshPausedAtLimit_WarnsWithoutRecordingTransition() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var environment = new DesktopEnvironment();
        var window = CreateWindow(context, new DesktopDialogs(), environment);
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        await context.ViewModel.PauseCommand.ExecuteAsync(null);
        context.Engine.CapturedDuration = RecordingFormat.WarningCapturedDuration;
        window.RefreshStatus();
        Assert.Single(environment.Balloons);
        Assert.True(environment.PreventSleep.Last());
        await context.ViewModel.StopAsync(false);
        await window.RequestExitAsync();
    });

    [Theory]
    [InlineData(MessageBoxResult.No)]
    [InlineData(MessageBoxResult.Yes)]
    public Task ConfirmShortRecordingAsync_UserChoice_ForwardsExplicitConsentOnly(MessageBoxResult answer) =>
        OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            context.PrepareRecording();
            var dialogs = new DesktopDialogs { Answer = answer };
            var window = CreateWindow(context, dialogs, new DesktopEnvironment());
            context.ViewModel.Message = "Insufficient space";
            await window.ConfirmShortRecordingAsync();
            Assert.False(context.ViewModel.NeedsShortRecordingConsent);
            var prompt = Assert.Single(dialogs.Prompts);
            Assert.Equal("Insufficient space\n\nProceed with a shorter recording when free space runs out?",
                prompt.Message);
            Assert.Equal("Insufficient space for maximum duration", prompt.Title);
            Assert.Equal(MessageBoxButton.YesNo, prompt.Buttons);
            Assert.Equal(MessageBoxImage.Warning, prompt.Image);
            Assert.Equal(answer == MessageBoxResult.Yes, context.Engine.Request is not null);
            if (context.Engine.Request is not null)
                Assert.True(context.Engine.Request.AllowShortRecordingWhenStorageIsLow);
            await context.ViewModel.StopAsync(false);
            await window.RequestExitAsync();
        });

    [Fact]
    public Task NeedsShortRecordingConsent_PropertyNotification_ConsumesPromptOnce() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var dialogs = new DesktopDialogs { Answer = MessageBoxResult.No };
        var window = CreateWindow(context, dialogs, new DesktopEnvironment());
        context.ViewModel.Message = "not a consent request";
        Assert.Empty(dialogs.Prompts);
        context.ViewModel.NeedsShortRecordingConsent = true;
        Assert.False(context.ViewModel.NeedsShortRecordingConsent);
        Assert.Single(dialogs.Prompts);
        Assert.Null(context.Engine.Request);
        await window.RequestExitAsync();
    });

    [Theory]
    [InlineData(MessageBoxResult.No)]
    [InlineData(MessageBoxResult.Yes)]
    public Task RequestExitAsync_ActiveWork_StopsOnlyAfterConfirmation(MessageBoxResult answer) =>
        OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var dialogs = new DesktopDialogs { Answer = answer };
            var environment = new DesktopEnvironment();
            var window = CreateWindow(context, dialogs, environment);
            context.PrepareRecording();
            await context.ViewModel.StartRecordingAsync(false);
            await window.RequestExitAsync();
            Assert.Equal(answer == MessageBoxResult.Yes, context.ViewModel.IsIdle);
            Assert.Equal(answer == MessageBoxResult.Yes ? 1 : 0, environment.Disposals);
            Assert.Empty(context.Queue.Jobs);
            var prompt = Assert.Single(dialogs.Prompts);
            Assert.Equal("Stop active recording or transcription work and exit? " +
                "Queued jobs and available captured audio will be preserved.", prompt.Message);
            Assert.Equal("Exit Sound Recorder", prompt.Title);
            Assert.Equal(MessageBoxButton.YesNo, prompt.Buttons);
            Assert.Equal(MessageBoxImage.Question, prompt.Image);
            if (answer != MessageBoxResult.Yes)
            {
                await context.ViewModel.StopAsync(false);
                await window.RequestExitAsync();
            }
        });

    [Theory]
    [InlineData(MessageBoxResult.Yes)]
    [InlineData(MessageBoxResult.Cancel)]
    public Task Close_KeepRunningOrCancel_DoesNotStopWork(MessageBoxResult answer) => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var dialogs = new DesktopDialogs { Answer = answer };
        var environment = new DesktopEnvironment();
        var window = CreateWindow(context, dialogs, environment);
        var model = await context.Models.RegisterAsync("existing.zip", CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken selectionToken = default;
        context.Models.Selecting = token =>
        {
            selectionToken = token;
            selectionStarted.SetResult();
            return release.Task.WaitAsync(token);
        };
        try
        {
            await context.ViewModel.InitializeCommand.ExecuteAsync(null);
            context.ViewModel.SelectedModel = model;
            await UntilAsync(() => context.ViewModel.IsSelectingModel);
            await selectionStarted.Task;
            context.PrepareRecording();
            await context.ViewModel.StartRecordingAsync(false);
            window.Close();
            Assert.Equal("Recording", context.ViewModel.Status);
            Assert.Equal(0, environment.Disposals);
            Assert.True(context.ViewModel.IsSelectingModel);
            Assert.False(selectionToken.IsCancellationRequested);
            var prompt = Assert.Single(dialogs.Prompts);
            Assert.Equal("Keep running in the notification area, or stop active work and exit?", prompt.Message);
            Assert.Equal("Active work", prompt.Title);
            Assert.Equal(MessageBoxButton.YesNoCancel, prompt.Buttons);
            Assert.Equal(MessageBoxImage.Question, prompt.Image);
            Assert.Equal(MessageBoxResult.Yes, prompt.Default);
            release.SetResult();
            await UntilAsync(() => !context.ViewModel.IsSelectingModel);
            Assert.Equal(model.Id, context.Models.SelectedModelId);
            Assert.Equal("Recording", context.ViewModel.Status);
            Assert.Empty(context.Queue.Jobs);
        }
        finally
        {
            release.TrySetResult();
            await UntilAsync(() => !context.ViewModel.IsSelectingModel);
            await context.ViewModel.StopAsync(false);
            await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task Close_Idle_DisposesOwnedServicesWithoutPrompt() => OnStaAsync(() =>
    {
        using var context = new DesktopTestContext();
        var dialogs = new DesktopDialogs();
        var environment = new DesktopEnvironment();
        var capture = new DesktopLifetime();
        var window = new MainWindow(context.ViewModel, context.Engine, dialogs, environment,
            new DesktopLogger<MainWindow>(), capture);
        window.Close();
        Assert.Empty(dialogs.Prompts);
        Assert.Equal(1, environment.Disposals);
        Assert.Equal(1, capture.Disposals);
        return Task.CompletedTask;
    });

    [Fact]
    public Task SuspendAsync_ActiveRecording_FinalizesWithoutQueuingOrResuming() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        await window.SuspendAsync();
        Assert.Equal("Windows is suspending. Available audio was finalized and work was interrupted.",
            context.ViewModel.Message);
        Assert.True(context.ViewModel.IsIdle);
        Assert.Same(context.Engine.Result, Assert.Single(context.ViewModel.Recordings));
        Assert.Empty(context.Queue.Jobs);
        await window.RequestExitAsync();
    });

    [Fact]
    public Task AcceptCompletedRecordingAsync_DurationLimit_RefreshesWindowOutcome() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var window = CreateWindow(context, new DesktopDialogs(), new DesktopEnvironment());
        await window.AcceptCompletedRecordingAsync(context.Engine.Result);
        Assert.Equal("Recording saved at the four-hour limit: " + context.Engine.Result.AudioPath,
            context.ViewModel.Message);
        await window.RequestExitAsync();
    });

    [Fact]
    public Task Close_DrainsPendingModelSelectionBeforeDisposing() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var model = await context.Models.RegisterAsync("existing.zip", CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Models.Selecting = async token =>
        {
            using var registration = token.Register(() => cancelled.TrySetResult());
            await release.Task;
            token.ThrowIfCancellationRequested();
        };
        var environment = new DesktopEnvironment();
        var window = CreateWindow(context, new DesktopDialogs(), environment);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            await context.ViewModel.InitializeCommand.ExecuteAsync(null);
            context.ViewModel.SelectedModel = model;
            await UntilAsync(() => context.ViewModel.IsSelectingModel);
            window.Close();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(closed.Task.IsCompleted);
            Assert.Equal(0, environment.Disposals);
            release.SetResult();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(context.ViewModel.IsSelectingModel);
            Assert.Equal(1, environment.Disposals);
        }
        finally
        {
            release.TrySetResult();
            await UntilAsync(() => !context.ViewModel.IsSelectingModel);
            if (!closed.Task.IsCompleted) await window.RequestExitAsync();
        }
    });

    [Fact]
    public Task Close_StopChoice_FinalizesBeforeRaisingClosingAndDisposing() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var dialogs = new DesktopDialogs { Answer = MessageBoxResult.No };
        var environment = new DesktopEnvironment();
        var window = CreateWindow(context, dialogs, environment);
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.SetResult();
        var closingCalls = 0;
        window.Closing += (_, _) => closingCalls++;
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(context.ViewModel.IsIdle);
        Assert.False(context.ViewModel.PermissionConfirmed);
        Assert.Same(context.Engine.Result, Assert.Single(context.ViewModel.Recordings));
        Assert.Empty(context.Queue.Jobs);
        Assert.Equal(1, closingCalls);
        Assert.Equal(1, environment.Disposals);
        Assert.Single(dialogs.Prompts);
    });

    [Fact]
    public Task Close_FinalizationRefreshFails_LogsAndCompletesExplicitExit() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var dialogs = new DesktopDialogs { Answer = MessageBoxResult.No };
        var environment = new DesktopEnvironment();
        var logger = new DesktopLogger<MainWindow>();
        var window = new MainWindow(context.ViewModel, context.Engine, dialogs, environment, logger);
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        context.History.LoadError = new IOException("history unreadable");
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.SetResult();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(context.History.LoadError, Assert.Single(logger.Errors));
        Assert.True(context.ViewModel.IsIdle);
        Assert.Equal(1, environment.Disposals);
    });

    [Fact]
    public Task NotificationAreaExit_Requested_RoutesToExitAndDisposesWindow() => OnStaAsync(() =>
    {
        using var context = new DesktopTestContext();
        var environment = new DesktopEnvironment();
        var window = CreateWindow(context, new DesktopDialogs(), environment);
        var closingCalls = 0;
        window.Closing += (_, _) => closingCalls++;
        environment.RequestExit();
        Assert.Equal(1, environment.Disposals);
        Assert.Equal(1, closingCalls);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Loaded_RoutedEvent_InitializesBeforePeriodicRefreshAndReportsReadiness() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var environment = new DesktopEnvironment();
        var logger = new DesktopLogger<MainWindow>();
        var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(), environment, logger);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        await UntilAsync(() => logger.Entries.Any(entry => entry.Id == 201));
        Assert.NotNull(context.Models.BundledDirectory);
        Assert.Equal("microphone", context.ViewModel.SelectedMicrophoneId);
        context.Engine.CapturedDuration = TimeSpan.FromSeconds(17);
        await UntilAsync(() => context.ViewModel.CapturedDuration == "00:00:17");
        Assert.Equal("Sound Recorder", environment.Texts.Last());
        Assert.False(environment.PreventSleep.Last());
        await window.RequestExitAsync();
    });

    [Theory]
    [InlineData("OneDrive")]
    [InlineData("OneDriveConsumer")]
    [InlineData("OneDriveCommercial")]
    [InlineData("Dropbox")]
    public Task ChooseDestination_KnownSynchronizedFolder_WarnsWithoutChangingTheSelection(string rootName) =>
        OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var previous = Environment.GetEnvironmentVariable(rootName);
            try
            {
                Environment.SetEnvironmentVariable(rootName, context.Directory);
                var window = CreateWindow(context, new DesktopDialogs { Folder = context.Directory },
                    new DesktopEnvironment());
                window.ChooseDestination();
                Assert.Equal(context.Directory, context.ViewModel.DestinationFolder);
                Assert.Equal(PrivacyPolicy.CloudSynchronizationWarning, context.ViewModel.Message);
                await window.RequestExitAsync();
            }
            finally
            {
                Environment.SetEnvironmentVariable(rootName, previous);
            }
        });

    [Fact]
    public Task ClickHandlers_ModelTranscriptAndHistory_RouteExplicitActions() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var environment = new DesktopEnvironment();
        var dialogs = new DesktopDialogs { Package = "selected.zip", Answer = MessageBoxResult.OK };
        var window = CreateWindow(context, dialogs, environment);
        Click(window, "Import model package...");
        await UntilAsync(() => context.ViewModel.Message ==
            "The model package was verified and registered locally. Choose it from Downloaded models.");
        Assert.Equal("selected.zip", Assert.Single(context.Models.Imports));
        var textPath = Path.Combine(context.Directory, "conversation.txt");
        await File.WriteAllTextAsync(textPath, "Local transcript");
        context.ViewModel.SelectedJob = context.Job(context.Engine.Result, new(textPath, "captions.srt"));
        Click(window, "View transcript");
        await UntilAsync(() => context.ViewModel.Transcript == "Local transcript");
        Assert.Empty(environment.Copied);
        Click(window, "Copy transcript");
        await UntilAsync(() => environment.Copied.Count == 1);
        Assert.Equal("Local transcript", Assert.Single(environment.Copied));
        context.ViewModel.Recordings.Add(context.Engine.Result);
        var history = (System.Windows.Controls.Primitives.Selector)window.FindName("HistoryList");
        history.SelectedItem = context.Engine.Result;
        Click(window, "Open output folder");
        Assert.Equal("The recording file is missing: " + context.Engine.Result.AudioPath,
            context.ViewModel.Message);
        await window.RequestExitAsync();
    });

    private sealed class JobHistoryBoundary(IRecordingLibrary library) : IRecordingLibrary
    {
        public IOException? Failure { get; set; }
        public Task<IReadOnlyList<RecordingLibraryEntry>> LoadAsync(CancellationToken token) => library.LoadAsync(token);
        public Task<IReadOnlyList<RecordingLibraryEntry>> SearchAsync(string query, CancellationToken token) => library.SearchAsync(query, token);
        public Task<TranscriptReadResult> ReadTranscriptAsync(Guid recordingId, Guid jobId, CancellationToken token) =>
            library.ReadTranscriptAsync(recordingId, jobId, token);
        public Task<LibraryDeletionResult> DeleteRecordingAsync(Guid id, RecordingDeletionScope scope, CancellationToken token) =>
            library.DeleteRecordingAsync(id, scope, token);
        public Task<LibraryDeletionResult> DeleteJobHistoryAsync(Guid id, CancellationToken token) =>
            Failure is { } failure ? Task.FromException<LibraryDeletionResult>(failure) : library.DeleteJobHistoryAsync(id, token);
    }

    private sealed class WorkspacePlaybackDevice : NAudio.Wave.IWavePlayer, NAudio.Wave.IWavePosition
    {
        public NAudio.Wave.PlaybackState PlaybackState { get; private set; }
        public NAudio.Wave.WaveFormat OutputWaveFormat { get; private set; } = null!;
        public float Volume { get; set; }
        public long PositionBytes { get; set; }
        public bool Disposed { get; private set; }
        public Exception? PauseError { get; set; }
        public Exception? DisposeError { get; set; }
        public event EventHandler<NAudio.Wave.StoppedEventArgs>? PlaybackStopped;
        public void Init(NAudio.Wave.IWaveProvider provider) => OutputWaveFormat = provider.WaveFormat;
        public long GetPosition() => PositionBytes;
        public void Play() => PlaybackState = NAudio.Wave.PlaybackState.Playing;
        public void Pause()
        {
            if (PauseError is not null) throw PauseError;
            PlaybackState = NAudio.Wave.PlaybackState.Paused;
        }
        public void Stop()
        {
            PlaybackState = NAudio.Wave.PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new(null));
        }
        public void Fail(Exception exception)
        {
            PlaybackState = NAudio.Wave.PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new(exception));
        }
        public void Dispose()
        {
            Disposed = true;
            if (DisposeError is not null) throw DisposeError;
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public Task LifecycleEvents_CompletionPowerAndSession_EndWorkThroughPublicSignals() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var events = new DesktopLifecycleEvents();
        var environment = new DesktopEnvironment();
        var shown = 0;
        var hidden = 0;
        var activated = 0;
        var timer = new DispatcherTimer();
        var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(), environment,
            new DesktopLogger<MainWindow>(), lifecycleEvents: events,
            presentation: new(() => shown++, () => hidden++, () => { activated++; return true; }),
            timer: timer);
        Assert.Equal(TimeSpan.FromMilliseconds(250), timer.Interval);
        environment.RequestOpen();
        Assert.Equal(1, shown);
        Assert.Equal(1, activated);
        Assert.Equal(WindowState.Normal, window.WindowState);
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        events.RaisePower(PowerModes.Resume);
        await Task.Delay(100);
        Assert.Equal("Recording", context.ViewModel.Status);
        events.RaisePower(PowerModes.Suspend);
        await UntilAsync(() => context.ViewModel.IsIdle);
        await UntilAsync(() => context.ViewModel.Message.StartsWith("Windows is suspending."));
        context.Engine.Result = context.Recording(Guid.NewGuid());
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        events.RaiseSessionEnding();
        await UntilAsync(() => context.ViewModel.IsIdle);
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        var completed = context.Recording(Guid.NewGuid());
        events.RaiseCompletion(completed);
        await UntilAsync(() => context.ViewModel.Message ==
            "Recording saved at the four-hour limit: " + completed.AudioPath);
        Assert.Contains(completed, context.ViewModel.Recordings);
        await window.RequestExitAsync();
        Assert.Equal(0, events.PowerSubscribers);
        Assert.Equal(0, events.SessionSubscribers);
    });

    [Fact]
    public Task Composition_LocalRoot_RestoresConfiguredFilesWithoutHardwareOrNetwork() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var root = Path.Combine(context.Directory, "application");
        Directory.CreateDirectory(root);
        var recording = context.Engine.Result;
        var job = context.Job(recording);
        await File.WriteAllTextAsync(Path.Combine(root, "history.json"), JsonSerializer.Serialize(new[] { recording }));
        await File.WriteAllTextAsync(Path.Combine(root, "jobs.json"), JsonSerializer.Serialize(new[] { job }));
        var logger = new AppLoggerFactory();
        var events = new DesktopLifecycleEvents();
        var transcriberCreated = false;
        var window = new MainWindow(root, logger, () => new DesktopDialogs(),
            () => new DesktopEnvironment(), _ => events, context, (worker, models, factory) =>
            {
                transcriberCreated = true;
                Assert.Equal(Path.Combine(AppContext.BaseDirectory, "SoundRecorder.Desktop.exe"), worker);
                Assert.Same(logger, factory);
                Assert.False(models.HasCompatibleModel("English"));
                return context.Transcriber;
            });
        var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
        Assert.True(transcriberCreated);
        Assert.NotNull(window.FindName("HistoryList"));
        Assert.True(Directory.Exists(Path.Combine(root, "capture")));
        Assert.True(Directory.Exists(Path.Combine(root, "models")));
        await viewModel.InitializeCommand.ExecuteAsync(null);
        Assert.Equal(recording, Assert.Single(viewModel.Recordings));
        Assert.Equal(job.Id, Assert.Single(viewModel.Jobs).Id);
        Assert.Equal("microphone", viewModel.SelectedMicrophoneId);
        Assert.StartsWith("Some recording artifacts are unavailable.", viewModel.Message);
        Assert.Contains(logger.Logger.Entries, entry => entry.Message == "Main window initializing");
        var checkpointPath = Path.Combine(root, "capture", "interrupted.capture.json");
        await File.WriteAllTextAsync(checkpointPath, JsonSerializer.Serialize(new
        {
            RecordingId = recording.Id,
            MicrophonePath = "partial.wav",
            SystemAudioPath = (string?)null,
            DestinationFolder = root,
            Title = (string?)null,
            Language = "English",
            Source = AudioSource.MicrophoneOnly,
            CapturedTicks = TimeSpan.FromSeconds(3).Ticks,
            LastSavedAt = DateTimeOffset.UnixEpoch,
        }));
        var candidate = Assert.Single(await viewModel.FindRecoveryCandidatesAsync());
        Assert.Equal(checkpointPath, candidate.CapturedAudioPath);
        Assert.Equal(recording.Id, candidate.RecordingId);
        await window.RequestExitAsync();
    });

    [Fact]
    public Task Composition_ServiceFailure_LogsInitializationFailureAndPreservesException() => OnStaAsync(() =>
    {
        using var context = new DesktopTestContext();
        var factory = new AppLoggerFactory();
        var error = new IOException("dialog service unavailable");
        var actual = Assert.Throws<IOException>(() => new MainWindow(context.Directory, factory,
            () => throw error, () => new DesktopEnvironment(), _ => new DesktopLifecycleEvents(), context));
        Assert.Same(error, actual);
        Assert.Contains(factory.Logger.Entries, entry => entry.Message == "Main window initialization failed");
        Assert.Same(error, factory.Logger.Exceptions.Last());
        return Task.CompletedTask;
    });

    [Fact]
    public Task Composition_LoggerCreationFails_RethrowsWithoutMaskingTheStartupFailure() => OnStaAsync(() =>
    {
        using var context = new DesktopTestContext();
        var error = new IOException("diagnostics unavailable");
        Assert.Same(error, Assert.Throws<IOException>(() =>
            new MainWindow(context.Directory, new DesktopFailedLoggerFactory(error),
                () => new DesktopDialogs(), () => new DesktopEnvironment(),
                _ => new DesktopLifecycleEvents(), context)));
        return Task.CompletedTask;
    });

    [Fact]
    public Task StateChanged_Minimized_HidesWithoutStoppingAndPublishesOngoingStatus() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var environment = new DesktopEnvironment();
        var hidden = 0;
        var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(),
            environment, new DesktopLogger<MainWindow>(),
            presentation: new(() => throw new InvalidOperationException("must not show"),
                () => hidden++, () => throw new InvalidOperationException("must not activate")));
        var stateChanges = 0;
        window.StateChanged += (_, _) => stateChanges++;
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        window.WindowState = WindowState.Minimized;
        window.UpdateWindowState();
        Assert.Equal(1, hidden);
        Assert.Equal((3000, "Sound Recorder is still running", "Recording", ToolTipIcon.Info),
            Assert.Single(environment.Balloons));
        Assert.Equal(1, stateChanges);
        Assert.Equal("Recording", context.ViewModel.Status);
        window.WindowState = WindowState.Normal;
        window.UpdateWindowState();
        Assert.Equal(1, hidden);
        Assert.Equal(2, stateChanges);
        await context.ViewModel.StopAsync(false);
        await window.RequestExitAsync();
    });

    [Fact]
    public Task Close_KeepRunning_UsesTheWindowHideActionAndDoesNotRaiseClosing() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        var hidden = 0;
        var environment = new DesktopEnvironment();
        var window = new MainWindow(context.ViewModel, context.Engine,
            new DesktopDialogs { Answer = MessageBoxResult.Yes }, environment, new DesktopLogger<MainWindow>(),
            presentation: new(() => { }, () => hidden++, () => true));
        var closingCalls = 0;
        window.Closing += (_, _) => closingCalls++;
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        window.Close();
        Assert.Equal(1, hidden);
        Assert.Equal(0, closingCalls);
        Assert.Equal(0, environment.Disposals);
        Assert.Equal("Recording", context.ViewModel.Status);
        await context.ViewModel.StopAsync(false);
        await window.RequestExitAsync();
    });

    [Fact]
    public Task LoadAsync_RecoveryFailure_StillStartsPeriodicStatusRefresh() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        context.Recovery.FindError = new IOException("read failed");
        var timer = new DispatcherTimer();
        var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(),
            new DesktopEnvironment(), new DesktopLogger<MainWindow>(), timer: timer);
        await window.LoadAsync();
        Assert.True(timer.IsEnabled);
        context.Engine.CapturedDuration = TimeSpan.FromSeconds(19);
        await UntilAsync(() => context.ViewModel.CapturedDuration == "00:00:19");
        await window.RequestExitAsync();
    });

    [Fact]
    public Task InitializeCommand_ReloadDuringCapture_RefreshesRecordingStateWithoutStartingAgain() => OnStaAsync(async () =>
    {
        using var context = new DesktopTestContext();
        context.PrepareRecording();
        await context.Recorder.StartAsync(new(AudioSource.MicrophoneOnly, "microphone", null,
            context.Directory, null, "English"), true);
        Assert.Equal("Idle", context.ViewModel.Status);
        await context.ViewModel.InitializeCommand.ExecuteAsync(null);
        Assert.Equal("Recording", context.ViewModel.Status);
        Assert.True(context.ViewModel.PauseCommand.CanExecute(null));
        await context.ViewModel.StopAsync(false);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Exit_RunningJobStillAcknowledgingCancellation_ClosesWithoutASecondConfirmation(bool explicitExit) =>
        OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            context.Transcriber.Block = true;
            context.Transcriber.CancellationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await context.Queue.EnqueueAsync(context.Engine.Result, CancellationToken.None);
            await context.Transcriber.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            context.ViewModel.Refresh();
            var dialogs = new DesktopDialogs();
            dialogs.Answers.Enqueue(explicitExit ? MessageBoxResult.Yes : MessageBoxResult.No);
            dialogs.Answers.Enqueue(MessageBoxResult.Cancel);
            var environment = new DesktopEnvironment();
            var window = CreateWindow(context, dialogs, environment);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                if (explicitExit) await window.RequestExitAsync();
                else window.Close();
                await context.Transcriber.CancellationRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await UntilAsync(() => closed);
                Assert.Single(dialogs.Prompts);
                Assert.Equal(1, environment.Disposals);
                Assert.Equal(TranscriptionJobStatus.Running, Assert.Single(context.Queue.Jobs).Status);
            }
            finally
            {
                context.Transcriber.CancellationGate.TrySetResult();
                context.Transcriber.Release.TrySetResult();
                await context.Queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                if (!closed) await window.RequestExitAsync();
            }
        });

    private sealed class DeferredSearchLibrary(IRecordingLibrary library) : IRecordingLibrary
    {
        private bool _first = true;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<RecordingLibraryEntry>> LoadAsync(CancellationToken token) => library.LoadAsync(token);
        public async Task<IReadOnlyList<RecordingLibraryEntry>> SearchAsync(string query, CancellationToken token)
        {
            var entries = await library.SearchAsync(query, token);
            if (_first)
            {
                _first = false;
                Started.SetResult();
                await Release.Task;
            }
            return entries;
        }
        public Task<TranscriptReadResult> ReadTranscriptAsync(Guid recordingId, Guid jobId, CancellationToken token) =>
            library.ReadTranscriptAsync(recordingId, jobId, token);
        public Task<LibraryDeletionResult> DeleteRecordingAsync(Guid id, RecordingDeletionScope scope, CancellationToken token) =>
            library.DeleteRecordingAsync(id, scope, token);
        public Task<LibraryDeletionResult> DeleteJobHistoryAsync(Guid id, CancellationToken token) =>
            library.DeleteJobHistoryAsync(id, token);
    }

    private static MainWindow CreateWindow(
        DesktopTestContext context, DesktopDialogs dialogs, DesktopEnvironment environment) =>
        new(context.ViewModel, context.Engine, dialogs, environment, new DesktopLogger<MainWindow>());

    private static void Click(MainWindow window, string label)
    {
        var button = FindButton((DependencyObject)window.Content, label);
        Assert.NotNull(button);
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    }

    internal sealed class DesktopFailedLoggerFactory(Exception error) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => throw error;
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public void Dispose() { }
    }

    internal sealed class DesktopLifecycleEvents : IDesktopLifecycleEvents
    {
        public event EventHandler<SavedRecording>? RecordingCompleted;
        private PowerModeChangedEventHandler? _power;
        private SessionEndingEventHandler? _session;
        public int PowerSubscribers => _power?.GetInvocationList().Length ?? 0;
        public int SessionSubscribers => _session?.GetInvocationList().Length ?? 0;
        public event PowerModeChangedEventHandler PowerModeChanged { add => _power += value; remove => _power -= value; }
        public event SessionEndingEventHandler SessionEnding { add => _session += value; remove => _session -= value; }
        public void RaisePower(PowerModes mode) => _power?.Invoke(this, new PowerModeChangedEventArgs(mode));
        public void RaiseSessionEnding() => _session?.Invoke(this, new SessionEndingEventArgs(SessionEndReasons.SystemShutdown));
        public void RaiseCompletion(SavedRecording recording) => RecordingCompleted?.Invoke(this, recording);
    }

    private static System.Windows.Controls.Button? FindButton(DependencyObject parent, string label)
    {
        if (parent is System.Windows.Controls.Button button && Equals(button.Content, label)) return button;
        return LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()
            .Select(child => FindButton(child, label)).FirstOrDefault(candidate => candidate is not null);
    }

    internal static Task OnStaAsync(Func<Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await action();
                    completed.SetResult();
                }
                catch (Exception exception)
                {
                    completed.SetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}

internal sealed class DesktopDialogs : IDesktopDialogs
{
    public string? Folder { get; set; }
    public string? Package { get; set; }
    public int FolderRequests { get; private set; }
    public int PackageRequests { get; private set; }
    public MessageBoxResult Answer { get; set; } = MessageBoxResult.No;
    public Queue<MessageBoxResult> Answers { get; } = [];
    public List<(string Message, string Title, MessageBoxButton Buttons, MessageBoxImage Image,
        MessageBoxResult Default)> Prompts { get; } = [];
    public string? ChooseDestination() { FolderRequests++; return Folder; }
    public string? ChooseModelPackage() { PackageRequests++; return Package; }
    public MessageBoxResult Show(string message, string title, MessageBoxButton buttons,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        Prompts.Add((message, title, buttons, image, defaultResult));
        return Answers.TryDequeue(out var answer) ? answer : Answer;
    }
}

internal sealed class DesktopEnvironment : IDesktopEnvironment
{
    public event EventHandler? OpenRequested;
    public event EventHandler? ExitRequested;
    public int Disposals { get; private set; }
    public List<string> Texts { get; } = [];
    public List<string> Copied { get; } = [];
    public List<string> Opened { get; } = [];
    public List<bool> PreventSleep { get; } = [];
    public List<(int Milliseconds, string Title, string Message, ToolTipIcon Icon)> Balloons { get; } = [];
    public void SetNotificationText(string text) => Texts.Add(text);
    public void ShowBalloonTip(int milliseconds, string title, string message, ToolTipIcon icon) =>
        Balloons.Add((milliseconds, title, message, icon));
    public void PreventIdleSleep(bool prevent) => PreventSleep.Add(prevent);
    public void CopyTranscript(string text) => Copied.Add(text);
    public void OpenRecordingFolder(string audioPath) => Opened.Add(audioPath);
    public void Dispose() => Disposals++;
    public void RequestOpen() => OpenRequested?.Invoke(this, EventArgs.Empty);
    public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);
}

internal sealed class DesktopLifetime : IDisposable
{
    public int Disposals { get; private set; }
    public void Dispose() => Disposals++;
}
