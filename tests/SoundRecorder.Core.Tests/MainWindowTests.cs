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
            await context.ViewModel.StopJobCommand.ExecuteAsync(null);
            Assert.Contains("running transcription job", context.ViewModel.Message);
            await context.ViewModel.DeleteSelectedJobHistoryAsync();
            Assert.Contains("terminal transcription job", context.ViewModel.Message);
            await context.ViewModel.DeleteRecordingAsync(false);
            Assert.Contains("Select a recording", context.ViewModel.Message);
            await context.ViewModel.RetryRecordingDeletionCommand.ExecuteAsync(null);
            Assert.Contains("unfinished deletion", context.ViewModel.Message);
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
                }
            }
            else
            {
                await UntilAsync(() => context.ViewModel.Message.Contains("could not be read", StringComparison.Ordinal));
                Assert.Null(context.ViewModel.Transcript);
                Assert.False(File.Exists(path));
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
            context.PrepareRecording();
            await context.ViewModel.StartRecordingAsync(false);
            var saved = await context.ViewModel.StopAsync(enqueueTranscription: true);
            Assert.Same(context.Engine.Result, saved);
            Assert.Same(saved, Assert.Single(context.ViewModel.Recordings));
            Assert.Empty(context.Queue.Jobs);
            Assert.True(context.ViewModel.IsIdle);
            Assert.False(context.ViewModel.PermissionConfirmed);
            if (!succeeds) context.Models.Error = new IOException("Selection verification failed.");
            release.SetResult();
            if (succeeds)
            {
                await UntilAsync(() => context.Queue.Jobs.Count == 1);
                Assert.Same(saved, Assert.Single(context.Queue.Jobs).Recording);
            }
            else
            {
                await UntilAsync(() => context.ViewModel.Message.Contains("explicit retry", StringComparison.Ordinal));
                Assert.Empty(context.Queue.Jobs);
                Assert.Same(saved, Assert.Single(context.ViewModel.Recordings));
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
            }
            else
            {
                context.Models.CurrentPackage = context.Models.Package with { Languages = ["English"] };
                context.ViewModel.Language = "Brazilian Portuguese";
                Assert.Contains("compatible multilingual downloaded model", context.ViewModel.Message);
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
            viewModel.SearchText = "unmatched";
            await UntilAsync(() => viewModel.FilteredRecordings.Count == 0);
            Assert.Contains("No matching meetings", viewModel.LibraryMessage);
            Assert.Same(selected, viewModel.SelectedRecording);
            File.Delete(recording.AudioPath);
            viewModel.SearchText = Path.GetFileNameWithoutExtension(recording.AudioPath);
            await UntilAsync(() => viewModel.LibraryMessage.Contains("may be incomplete", StringComparison.Ordinal));
            Assert.False(File.Exists(recording.AudioPath));
            viewModel.SearchText = string.Empty;
            await UntilAsync(() => viewModel.FilteredRecordings.Count == 2);
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
                viewModel.SearchText = "history unavailable";
                await UntilAsync(() => viewModel.Message.Contains("Library search could not read", StringComparison.Ordinal));
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
            Assert.False(context.ViewModel.IsBusy);
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
            Assert.Equal(context.Models.Package.Id, context.Models.SelectedModelId);
            Assert.Equal("Recording", context.ViewModel.Status);
            if (outcome == "complete")
            {
                Assert.Contains(context.ViewModel.DownloadedModels, model => model.Id == entry.Key);
            }
            else
            {
                Assert.DoesNotContain(context.ViewModel.DownloadedModels, model => model.Id == entry.Key);
                Assert.Contains(outcome == "cancel" ? "cancelled" : "failed",
                    context.ViewModel.ModelDownloadStatus, StringComparison.OrdinalIgnoreCase);
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
            Assert.True(viewModel.SelectedRecording!.AudioDeleted);
            var retained = await library.ReadTranscriptAsync(recording.Id, job.Id, CancellationToken.None);
            Assert.True(retained.Text == expectedText, string.Join(" ", retained.Issues.Select(issue => issue.Message)));
            await UntilAsync(() => viewModel.Transcript == expectedText && viewModel.CanCopyTranscript);
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
            window.RefreshStatus();
            Assert.False(environment.PreventSleep.Last());
            Assert.Equal("Sound Recorder", environment.Texts.Last());
            Assert.Empty(environment.Balloons);
            context.PrepareRecording();
            await context.ViewModel.StartRecordingAsync(false);
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
            window.RefreshStatus();
            Assert.True(environment.PreventSleep.Last());
            Assert.Single(environment.Balloons);
            await context.ViewModel.StopAsync(false);
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
        context.PrepareRecording();
        await context.ViewModel.StartRecordingAsync(false);
        window.Close();
        Assert.Equal("Recording", context.ViewModel.Status);
        Assert.Equal(0, environment.Disposals);
        var prompt = Assert.Single(dialogs.Prompts);
        Assert.Equal("Keep running in the notification area, or stop active work and exit?", prompt.Message);
        Assert.Equal("Active work", prompt.Title);
        Assert.Equal(MessageBoxButton.YesNoCancel, prompt.Buttons);
        Assert.Equal(MessageBoxImage.Question, prompt.Image);
        Assert.Equal(MessageBoxResult.Yes, prompt.Default);
        await context.ViewModel.StopAsync(false);
        await window.RequestExitAsync();
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
