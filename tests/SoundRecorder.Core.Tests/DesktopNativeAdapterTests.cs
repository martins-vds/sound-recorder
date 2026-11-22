using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SoundRecorder.Desktop;
using SoundRecorder.Core;
using SoundRecorder.Infrastructure.Audio;
using SoundRecorder.Infrastructure.Persistence;

namespace SoundRecorder.Core.Tests;

[Collection("Desktop environment")]
public sealed class DesktopNativeAdapterTests
{
    [Fact]
    public void JobRow_PublicRefreshPublishesSnapshotLabelsAndIndependentDeletionRestartAvailability()
    {
        using var context = new DesktopTestContext();
        var recording = WritePlaybackWave(context, "row-contract.wav") with { Title = "First meeting & agenda_2026" };
        var id = Guid.NewGuid();
        var row = new TranscriptionJobViewModel(new(id, recording, DateTimeOffset.UnixEpoch,
            TranscriptionJobStatus.Succeeded, new("meeting.txt", "meeting.srt"), null));
        Assert.Equal("First meeting & agenda_2026", row.Title);
        Assert.Equal("English", row.Language);
        Assert.Empty(row.ModelName);
        Assert.True(row.CanDelete);
        Assert.True(row.CanRestart);
        var title = row.Title;
        var language = row.Language;
        var model = row.ModelName;
        var canDelete = row.CanDelete;
        var canRestart = row.CanRestart;
        row.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(row.Title): title = row.Title; break;
                case nameof(row.Language): language = row.Language; break;
                case nameof(row.ModelName): model = row.ModelName; break;
                case nameof(row.CanDelete): canDelete = row.CanDelete; break;
                case nameof(row.CanRestart): canRestart = row.CanRestart; break;
            }
        };
        var corrected = recording with { Title = "Corrected decisões_2026", Language = "Brazilian Portuguese" };
        row.Refresh(new(id, corrected, DateTimeOffset.UnixEpoch, TranscriptionJobStatus.Running, null, null),
            "small Q5_1", audioAvailable: true);
        Assert.Equal("Corrected decisões_2026", title);
        Assert.Equal("Brazilian Portuguese", language);
        Assert.Equal("small Q5_1", model);
        Assert.False(canDelete);
        Assert.False(canRestart);
        row.Refresh(new(id, corrected, DateTimeOffset.UnixEpoch, TranscriptionJobStatus.Failed, null,
            "The saved audio requires an explicit retry."), "small Q5_1", audioAvailable: false);
        Assert.True(canDelete);
        Assert.False(canRestart);
        row.Refresh(row.Job, "small Q5_1", audioAvailable: true);
        Assert.True(canDelete);
        Assert.True(canRestart);
        Assert.Equal(id, row.Id);
        Assert.True(File.Exists(recording.AudioPath));
    }

    [Fact]
    public void NativeProcessFailureSubscription_RetainsItsDiagnosticOwnerOnlyUntilUnsubscription()
    {
        var events = new WindowsProcessFailureEvents();
        var owner = SubscribeDiagnosticOwner(events);
        try
        {
            Collect();
            Assert.True(IsDiagnosticOwnerAlive(owner));
            UnsubscribeDiagnosticOwner(events, owner);
            Collect();
            Assert.False(IsDiagnosticOwnerAlive(owner));
        }
        finally
        {
            RemoveOwnedNativeDiagnosticSubscription(owner);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<ProcessDiagnosticOwner> SubscribeDiagnosticOwner(IProcessFailureEvents events)
    {
        var owner = new ProcessDiagnosticOwner();
        events.UnhandledException += owner.OnFailure;
        return new(owner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void UnsubscribeDiagnosticOwner(IProcessFailureEvents events,
        WeakReference<ProcessDiagnosticOwner> owner)
    {
        if (owner.TryGetTarget(out var target)) events.UnhandledException -= target.OnFailure;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsDiagnosticOwnerAlive(WeakReference<ProcessDiagnosticOwner> owner) =>
        owner.TryGetTarget(out _);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemoveOwnedNativeDiagnosticSubscription(WeakReference<ProcessDiagnosticOwner> owner)
    {
        if (!owner.TryGetTarget(out var target)) return;
        AppDomain.CurrentDomain.UnhandledException -= target.OnFailure;
        AppDomain.CurrentDomain.UnhandledException -= target.OnFailure;
    }

    private sealed class ProcessDiagnosticOwner
    {
        public void OnFailure(object sender, UnhandledExceptionEventArgs args) { }
    }

    [Fact]
    public void RecordingNameAdapter_UnresolvedOrLegacyNamesNeverRenameOrExposeTheRecordingDirectory()
    {
        using var context = new DesktopTestContext();
        var recording = WritePlaybackWave(context, "legacy_ação.wav");
        var named = recording with { Title = "Budget & decisões_2026" };
        var converter = new RecordingFileNameConverter();
        var culture = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        Assert.Equal("Budget & decisões_2026", converter.Convert(named, typeof(string), null!, culture));
        Assert.Equal("legacy_ação", converter.Convert(recording, typeof(string), null!, culture));
        Assert.Equal("legacy_ação", converter.Convert(recording.AudioPath, typeof(string), null!, culture));
        Assert.Equal(string.Empty, converter.Convert(null!, typeof(string), null!, culture));
        Assert.Equal(string.Empty, converter.Convert(DependencyProperty.UnsetValue, typeof(string), null!, culture));
        var invalid = Assert.Throws<ArgumentException>(() =>
            converter.Convert(recording.Id, typeof(string), null!, culture));
        Assert.Equal("value", invalid.ParamName);
        Assert.StartsWith("A recording filename requires a recording or path.", invalid.Message);
        var readOnly = Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack("An attempted replacement title", typeof(SavedRecording), null!, culture));
        Assert.Equal("Recording names are read-only.", readOnly.Message);
        Assert.Equal("Budget & decisões_2026", named.Title);
        Assert.True(File.Exists(recording.AudioPath));
        Assert.False(File.Exists(Path.Combine(context.Directory, "An attempted replacement title.wav")));
        using var exclusive = File.Open(recording.AudioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public Task StaleCopyActivation_AfterLeavingTheReviewedMeetingRejectsPrivateContentWithoutClipboardEffects() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var recording = WritePlaybackWave(context, "copy-origin.wav");
            var textPath = Path.ChangeExtension(recording.AudioPath, ".txt");
            await File.WriteAllTextAsync(textPath, "Private content from the previously reviewed meeting.");
            context.ViewModel.SelectedJob = context.Job(recording,
                new(textPath, Path.ChangeExtension(recording.AudioPath, ".srt")));
            var environment = new DesktopEnvironment();
            var logger = new DesktopLogger<MainWindow>();
            var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(), environment, logger);
            try
            {
                await context.ViewModel.ViewTranscriptCommand.ExecuteAsync(null);
                await RenderAsync(window);
                var copy = Elements<System.Windows.Controls.Button>((DependencyObject)window.Content)
                    .Single(button => Equals(button.Content, "Copy transcript"));
                Assert.True(copy.IsEnabled);
                context.ViewModel.SelectedRecording = WritePlaybackWave(context, "new-copy-selection.wav");
                copy.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Empty(environment.Copied);
                Assert.Equal("Select an available completed transcript before copying.", context.ViewModel.Message);
                Assert.DoesNotContain("Private content from the previously reviewed meeting.",
                    context.ViewModel.Transcript ?? string.Empty, StringComparison.Ordinal);
                Assert.False(context.ViewModel.CanCopyTranscript);
                Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning &&
                    entry.Message == "Transcript copying requested without an available completed transcript");
                Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("Private content", StringComparison.Ordinal) ||
                    entry.Message.Contains(context.Directory, StringComparison.Ordinal));
                Assert.Equal("Private content from the previously reviewed meeting.", await File.ReadAllTextAsync(textPath));
            }
            finally
            {
                await window.RequestExitAsync();
            }
        });

    [Fact]
    public Task StaleJobDeletionActivation_AfterClearingSelectionDoesNotPromptOrRemoveSavedHistory() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var recording = WritePlaybackWave(context, "stale-job-delete.wav");
            var history = new JsonRecordingHistory(Path.Combine(context.Directory, "stale-job-recordings.json"));
            var repository = new JsonTranscriptionJobRepository(Path.Combine(context.Directory, "stale-job-history.json"));
            await history.AddAsync(recording, CancellationToken.None);
            var job = context.Job(recording);
            await repository.SaveAsync([job], CancellationToken.None);
            var queue = new TranscriptionQueue(context.Transcriber, new DesktopWriter(), repository, history: history);
            await queue.RestoreAsync(CancellationToken.None);
            var library = new RecordingLibrary(history, repository, cancelRecordingAsync: queue.CancelRecordingAsync,
                deleteJobAsync: queue.DeleteJobAsync, releasePlaybackAsync: (_, _) => Task.CompletedTask);
            var viewModel = new MainViewModel(context.Recorder, context.Engine, queue, context.Models, history,
                new RecoveryManager(context.Recovery), context, context.Logger, library);
            var dialogs = new DesktopDialogs { Answer = MessageBoxResult.Yes };
            var window = new MainWindow(viewModel, context.Engine, dialogs, new DesktopEnvironment(),
                new DesktopLogger<MainWindow>());
            try
            {
                viewModel.Refresh();
                viewModel.ShowJobsCommand.Execute(null);
                await RenderAsync(window);
                var jobs = Assert.IsType<System.Windows.Controls.ListBox>(window.FindName("JobsList"));
                jobs.SelectedIndex = 0;
                await RenderAsync(window);
                var delete = JobButton(window, "Delete job history...");
                Assert.True(delete.IsEnabled);
                viewModel.SelectedJobRow = null;
                delete.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.Empty(dialogs.Prompts);
                Assert.Equal("Select a terminal transcription job whose worker has been released.", viewModel.Message);
                Assert.Equal(job.Id, Assert.Single(await repository.LoadAsync(CancellationToken.None)).Id);
                Assert.True(File.Exists(recording.AudioPath));
                Assert.Equal(recording.Id, Assert.Single(await history.LoadAsync(CancellationToken.None)).Id);
            }
            finally
            {
                await window.RequestExitAsync();
            }
        });

    [Fact]
    public Task StaleRecordingDeletionActivation_AfterLosingSelectionKeepsTheOwnedAudioAndHistory() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var recording = WritePlaybackWave(context, "stale-recording-delete.wav");
            var history = new JsonRecordingHistory(Path.Combine(context.Directory, "stale-recordings.json"));
            var repository = new JsonTranscriptionJobRepository(Path.Combine(context.Directory, "stale-recording-jobs.json"));
            await history.AddAsync(recording, CancellationToken.None);
            var queue = new TranscriptionQueue(context.Transcriber, new DesktopWriter(), repository, history: history);
            var library = new RecordingLibrary(history, repository, cancelRecordingAsync: queue.CancelRecordingAsync,
                deleteJobAsync: queue.DeleteJobAsync, releasePlaybackAsync: (_, _) => Task.CompletedTask);
            var viewModel = new MainViewModel(context.Recorder, context.Engine, queue, context.Models, history,
                new RecoveryManager(context.Recovery), context, context.Logger, library);
            var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
                new DesktopLogger<MainWindow>());
            var failures = new List<Exception>();
            DispatcherUnhandledExceptionEventHandler failure = (_, args) =>
            {
                failures.Add(args.Exception);
                args.Handled = true;
            };
            window.Dispatcher.UnhandledException += failure;
            try
            {
                viewModel.SelectedRecording = recording;
                await RenderAsync(window);
                var delete = Assert.IsType<System.Windows.Controls.Button>(window.FindName("DeleteRecordingButton"));
                Assert.True(delete.IsEnabled);
                viewModel.SelectedRecording = null;
                delete.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await RenderAsync(window);
                Assert.Empty(failures);
                Assert.Empty(window.OwnedWindows.OfType<RecordingDeletionDialog>());
                Assert.Equal("Select a recording, or wait for its current deletion to finish.", viewModel.Message);
                Assert.False(viewModel.IsDeletingRecording);
                Assert.True(File.Exists(recording.AudioPath));
                Assert.Equal(recording.Id, Assert.Single(await history.LoadAsync(CancellationToken.None)).Id);
                using var exclusive = File.Open(recording.AudioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            finally
            {
                window.Dispatcher.UnhandledException -= failure;
                await window.RequestExitAsync();
            }
        });

    [Fact]
    public Task StaleRecordingDeletionActivation_WhilePlaybackReleaseIsHeldCannotChangeTheConfirmedScope() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var recording = WritePlaybackWave(context, "busy-recording-delete.wav");
            var text = Path.ChangeExtension(recording.AudioPath, ".txt");
            await File.WriteAllTextAsync(text, "Keep the confirmed audio-only deletion transcript.");
            var history = new JsonRecordingHistory(Path.Combine(context.Directory, "busy-recordings.json"));
            var repository = new JsonTranscriptionJobRepository(Path.Combine(context.Directory, "busy-recording-jobs.json"));
            await history.AddAsync(recording, CancellationToken.None);
            var releaseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePermit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCount = 0;
            var queue = new TranscriptionQueue(context.Transcriber, new DesktopWriter(), repository, history: history);
            var library = new RecordingLibrary(history, repository, cancelRecordingAsync: queue.CancelRecordingAsync,
                deleteJobAsync: queue.DeleteJobAsync, releasePlaybackAsync: async (_, token) =>
                {
                    Interlocked.Increment(ref releaseCount);
                    releaseStarted.TrySetResult();
                    await releasePermit.Task.WaitAsync(token);
                });
            var viewModel = new MainViewModel(context.Recorder, context.Engine, queue, context.Models, history,
                new RecoveryManager(context.Recovery), context, context.Logger, library);
            var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(), new DesktopEnvironment(),
                new DesktopLogger<MainWindow>());
            var failures = new List<Exception>();
            DispatcherUnhandledExceptionEventHandler failure = (_, args) =>
            {
                failures.Add(args.Exception);
                args.Handled = true;
            };
            window.Dispatcher.UnhandledException += failure;
            var unexpectedDialog = false;
            var modalGuard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            modalGuard.Tick += (_, _) =>
            {
                foreach (var dialog in window.OwnedWindows.OfType<RecordingDeletionDialog>().ToArray())
                {
                    unexpectedDialog = true;
                    dialog.Close();
                }
            };
            Task? deletion = null;
            try
            {
                viewModel.SelectedRecording = recording;
                await RenderAsync(window);
                var delete = Assert.IsType<System.Windows.Controls.Button>(window.FindName("DeleteRecordingButton"));
                Assert.True(delete.IsEnabled);
                deletion = viewModel.DeleteRecordingAsync(false);
                await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(viewModel.IsDeletingRecording);
                Assert.Same(recording, viewModel.SelectedRecording);
                modalGuard.Start();
                delete.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await RenderAsync(window);
                Assert.False(delete.IsEnabled);
                Assert.False(unexpectedDialog);
                Assert.Empty(failures);
                Assert.True(File.Exists(recording.AudioPath));
                Assert.Equal(RecordingDeletionScope.AudioOnly,
                    Assert.Single(await history.LoadAsync(CancellationToken.None)).PendingDeletion!.Scope);
                releasePermit.TrySetResult();
                await deletion.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(1, releaseCount);
                Assert.False(File.Exists(recording.AudioPath));
                Assert.Equal("Keep the confirmed audio-only deletion transcript.", await File.ReadAllTextAsync(text));
                Assert.True(Assert.Single(await history.LoadAsync(CancellationToken.None)).AudioDeleted);
            }
            finally
            {
                modalGuard.Stop();
                releasePermit.TrySetResult();
                if (deletion is not null) await deletion.WaitAsync(TimeSpan.FromSeconds(3));
                window.Dispatcher.UnhandledException -= failure;
                await window.RequestExitAsync();
            }
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task JobControls_RecognitionAndTerminationRefreshTheExistingVisibleRow(bool stop) =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            context.Engine.Result = context.Engine.Result with { Title = "Budget & priorities_2026" };
            context.Transcriber.Block = true;
            context.Transcriber.CancellationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await context.Queue.EnqueueAsync(context.Engine.Result, CancellationToken.None);
            await context.Transcriber.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await context.Queue.EnqueueAsync(context.Recording(Guid.NewGuid()), CancellationToken.None);
            var environment = new DesktopEnvironment();
            var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(), environment,
                new DesktopLogger<MainWindow>());
            try
            {
                context.ViewModel.Refresh();
                context.ViewModel.ShowJobsCommand.Execute(null);
                var jobs = Assert.IsType<System.Windows.Controls.ListBox>(window.FindName("JobsList"));
                await RenderAsync(window);
                jobs.SelectedIndex = 0;
                await RenderAsync(window);
                var row = Assert.IsType<System.Windows.Controls.ListBoxItem>(jobs.ItemContainerGenerator.ContainerFromIndex(0));
                var bar = Assert.Single(Elements<System.Windows.Controls.ProgressBar>(row));
                var progressText = Elements<System.Windows.Controls.TextBlock>(row)
                    .Single(text => text.Name == "JobProgressText");
                Assert.Equal(Visibility.Visible, ((FrameworkElement)bar.Parent).Visibility);
                Assert.Contains(Elements<System.Windows.Controls.TextBlock>(row), text => text.Text == "Budget & priorities_2026");
                Assert.Contains(Elements<System.Windows.Controls.TextBlock>(row), text => text.Text == "English");
                Assert.Contains(Elements<System.Windows.Controls.TextBlock>(row), text => text.Text == "Running");
                Assert.True(bar.IsIndeterminate);
                Assert.Equal("Indeterminate", AutomationProperties.GetItemStatus(bar));
                Assert.Equal("Preparing / progress unavailable", progressText.Text);
                Assert.True(JobButton(window, "Cancel selected job").IsEnabled);
                Assert.True(JobButton(window, "Stop selected job").IsEnabled);
                Assert.False(JobButton(window, "Restart selected job").IsEnabled);
                context.Transcriber.Progress!.Report(new(TranscriptionPhase.Recognizing, 37));
                context.ViewModel.Refresh();
                await RenderAsync(window);
                Assert.Same(row, jobs.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.False(bar.IsIndeterminate);
                Assert.Equal(37d, bar.Value);
                Assert.Equal("Recognition estimate", AutomationProperties.GetItemStatus(bar));
                Assert.Equal("Recognizing / 37% recognition estimate", progressText.Text);
                context.Transcriber.Progress.Report(new(TranscriptionPhase.Writing));
                context.ViewModel.Refresh();
                await RenderAsync(window);
                Assert.True(bar.IsIndeterminate);
                Assert.Equal("Indeterminate", AutomationProperties.GetItemStatus(bar));
                Assert.Equal("Writing / progress unavailable", progressText.Text);
                var ending = stop ? context.ViewModel.StopJobCommand.ExecuteAsync(null) :
                    context.ViewModel.CancelJobCommand.ExecuteAsync(null);
                await context.Transcriber.CancellationRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
                context.ViewModel.Refresh();
                await RenderAsync(window);
                Assert.Contains(Elements<System.Windows.Controls.TextBlock>(row), text => text.Text == (stop ? "Stopping" : "Cancelling"));
                Assert.False(JobButton(window, "Cancel selected job").IsEnabled);
                Assert.False(JobButton(window, "Stop selected job").IsEnabled);
                Assert.False(JobButton(window, "Restart selected job").IsEnabled);
                context.Transcriber.CancellationGate.SetResult();
                await ending;
                context.Transcriber.Release.SetResult();
                await context.Queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                context.ViewModel.Refresh();
                await RenderAsync(window);
                Assert.Contains(Elements<System.Windows.Controls.TextBlock>(row), text => text.Text == (stop ? "Stopped" : "Cancelled"));
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)bar.Parent).Visibility);
                Assert.False(JobButton(window, "Cancel selected job").IsEnabled);
                Assert.False(JobButton(window, "Stop selected job").IsEnabled);
                Assert.True(JobButton(window, "Restart selected job").IsEnabled);
                context.ViewModel.Recordings.Add(context.Engine.Result with { AudioDeleted = true });
                context.ViewModel.Refresh();
                await RenderAsync(window);
                Assert.False(JobButton(window, "Restart selected job").IsEnabled);
            }
            finally
            {
                context.Transcriber.CancellationGate.TrySetResult();
                context.Transcriber.Release.TrySetResult();
                await context.Queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                await window.RequestExitAsync();
            }
        });

    [Theory]
    [InlineData(TranscriptionJobStatus.Succeeded, true, true, "Completed")]
    [InlineData(TranscriptionJobStatus.Succeeded, false, false, "Completed")]
    [InlineData(TranscriptionJobStatus.Failed, true, false, "Failed")]
    [InlineData(TranscriptionJobStatus.Canceled, false, false, "Cancelled")]
    public Task JobControls_RestoredAttemptsOfferOnlySuccessfullyPublishedTranscripts(
        TranscriptionJobStatus status, bool outputs, bool canView, string label) =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            context.Repository.Items.Add(new(Guid.NewGuid(), context.Engine.Result,
                DateTimeOffset.UnixEpoch, status, outputs ? new("attempt.txt", "attempt.srt") : null,
                status == TranscriptionJobStatus.Failed ? "Owned attempt could not finish." : null,
                modelId: "Small Q5_1_English"));
            await context.Queue.RestoreAsync(CancellationToken.None);
            var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(),
                new DesktopEnvironment(), new DesktopLogger<MainWindow>());
            try
            {
                context.ViewModel.Refresh();
                context.ViewModel.ShowJobsCommand.Execute(null);
                var jobs = Assert.IsType<System.Windows.Controls.ListBox>(window.FindName("JobsList"));
                await RenderAsync(window);
                jobs.SelectedIndex = 0;
                await RenderAsync(window);
                var row = Assert.IsType<System.Windows.Controls.ListBoxItem>(jobs.ItemContainerGenerator.ContainerFromIndex(0));
                var texts = Elements<System.Windows.Controls.TextBlock>(row).Select(text => text.Text).ToArray();
                Assert.Contains(label, texts);
                Assert.Contains("Small Q5_1_English", texts);
                if (status == TranscriptionJobStatus.Failed) Assert.Contains("Owned attempt could not finish.", texts);
                Assert.False(JobButton(window, "Cancel selected job").IsEnabled);
                Assert.False(JobButton(window, "Stop selected job").IsEnabled);
                Assert.True(JobButton(window, "Restart selected job").IsEnabled);
                var view = Elements<System.Windows.Controls.Button>((DependencyObject)window.Content)
                    .Single(button => Equals(button.Content, "View transcript"));
                Assert.Equal(canView, view.IsEnabled);
            }
            finally
            {
                await window.RequestExitAsync();
            }
        });

    [Fact]
    public Task ModelCatalog_VisibleFamiliesExplainLanguageCompatibilityBeforeDownload() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(),
                new DesktopEnvironment(), new DesktopLogger<MainWindow>());
            try
            {
                await context.ViewModel.InitializeCommand.ExecuteAsync(null);
                context.ViewModel.ShowModelsCommand.Execute(null);
                Assert.IsType<System.Windows.Controls.Expander>(window.FindName("ModelCatalogExpander")).IsExpanded = true;
                await RenderAsync(window);
                var catalog = Assert.IsType<System.Windows.Controls.ItemsControl>(window.FindName("ModelCatalogList"));
                var text = Elements<System.Windows.Controls.TextBlock>(catalog).Select(item => item.Text).ToArray();
                Assert.Contains("English", text);
                Assert.Contains("English, Brazilian Portuguese", text);
            }
            finally
            {
                await window.RequestExitAsync();
            }
        });

    [Fact]
    public Task JobModelLabel_ResolvesRegisteredCatalogIdentityWithoutChangingThePinnedChoiceOrSelection() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var recording = WritePlaybackWave(context, "pinned-model.wav");
            var entry = SoundRecorder.Infrastructure.Models.TranscriptionModelCatalog.Entries
                .Single(model => model.Family == "small" && model.Encoding.ToString() == "Q5_1");
            context.History.Items.Add(recording);
            var job = new TranscriptionJob(Guid.NewGuid(), recording, DateTimeOffset.UnixEpoch,
                TranscriptionJobStatus.Failed, null, "Model is unavailable.", entry.Key);
            context.Repository.Items.Add(job);
            var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(),
                new DesktopEnvironment(), new DesktopLogger<MainWindow>());
            try
            {
                await context.ViewModel.InitializeCommand.ExecuteAsync(null);
                context.ViewModel.ShowJobsCommand.Execute(null);
                await RenderAsync(window);
                var jobs = Assert.IsType<System.Windows.Controls.ListBox>(window.FindName("JobsList"));
                var row = Assert.IsType<System.Windows.Controls.ListBoxItem>(jobs.ItemContainerGenerator.ContainerFromIndex(0));
                var label = Elements<System.Windows.Controls.TextBlock>(row)
                    .Single(text => text.Text == "whisper.net:small:Q5_1");
                await context.ViewModel.DownloadCatalogModelCommand.ExecuteAsync(entry);
                window.RefreshStatus();
                await RenderAsync(window);
                Assert.Equal("small Q5_1", label.Text);
                Assert.Equal("whisper.net:small:Q5_1", Assert.Single(context.Queue.Jobs).ModelId);
                Assert.Null(context.Models.SelectedModelId);
                Assert.Empty(context.Models.Downloads);
                Assert.Single(context.Models.DownloadedModels);
            }
            finally
            {
                await window.RequestExitAsync();
            }
        });

    [Fact]
    public Task SelectedModelAccessibility_AnnouncesTheLiteralEncodingNameWithoutAccessKeyEscaping() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(),
                new DesktopEnvironment(), new DesktopLogger<MainWindow>());
            try
            {
                await context.ViewModel.InitializeCommand.ExecuteAsync(null);
                var entry = SoundRecorder.Infrastructure.Models.TranscriptionModelCatalog.Entries
                    .Single(model => model.Family == "small" && model.Encoding.ToString() == "Q5_1");
                await context.ViewModel.DownloadCatalogModelCommand.ExecuteAsync(entry);
                context.ViewModel.SelectedModel = Assert.Single(context.ViewModel.DownloadedModels);
                await WaitUntilAsync(() => context.ViewModel.ModelHeader == "small Q5_1");
                await RenderAsync(window);
                var button = Assert.IsType<System.Windows.Controls.Button>(window.FindName("ModelsButton"));
                Assert.Equal("small Q5_1", new ButtonAutomationPeer(button).GetName());
                Assert.Empty(context.Models.Downloads);
            }
            finally
            {
                await window.RequestExitAsync();
            }
        });

    private static System.Windows.Controls.Button JobButton(MainWindow window, string name) =>
        Elements<System.Windows.Controls.Button>((DependencyObject)window.Content)
            .Single(button => AutomationProperties.GetName(button) == name);

    private static IEnumerable<T> Elements<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Elements<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static async Task RenderAsync(MainWindow window)
    {
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        content.Measure(new System.Windows.Size(980, 720));
        content.Arrange(new Rect(0, 0, 980, 720));
        content.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        content.UpdateLayout();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public Task RecordingDeletion_PublicConfirmationPreservesIntentAndOwnedArtifacts(
        bool audioDeleted, bool allArtifacts, bool confirm) =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var recording = WritePlaybackWave(context, "confirm-delete.wav");
            var textPath = Path.ChangeExtension(recording.AudioPath, ".txt");
            var subtitlePath = Path.ChangeExtension(recording.AudioPath, ".srt");
            await File.WriteAllTextAsync(textPath, "Previously completed local transcript.");
            await File.WriteAllTextAsync(subtitlePath, "1\r\n00:00:00,000 --> 00:00:01,000\r\nRetained\r\n");
            recording = recording with
            {
                Title = "Keep & delete_accurately",
                AudioDeleted = audioDeleted,
                TranscriptOutputs = [new(Guid.NewGuid(), new(textPath, subtitlePath))],
            };
            if (audioDeleted) File.Delete(recording.AudioPath);
            var history = new JsonRecordingHistory(Path.Combine(context.Directory, "history.json"));
            await history.AddAsync(recording, CancellationToken.None);
            var other = WritePlaybackWave(context, "unrelated-confirm-delete.wav");
            await history.AddAsync(other, CancellationToken.None);
            var factory = new AppLoggerFactory();
            var window = new MainWindow(context.Directory, factory, () => new DesktopDialogs(),
                () => new DesktopEnvironment(), _ => new MainWindowTests.DesktopLifecycleEvents(),
                context, (_, _, _) => context.Transcriber);
            var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
            var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            DispatcherUnhandledExceptionEventHandler failure = (_, args) =>
            {
                handled.TrySetException(args.Exception);
                args.Handled = true;
            };
            window.Dispatcher.UnhandledException += failure;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            var started = Stopwatch.StartNew();
            timer.Tick += (_, _) =>
            {
                var dialog = PresentationSource.CurrentSources
                    .Cast<PresentationSource>()
                    .Select(source => source.RootVisual)
                    .OfType<RecordingDeletionDialog>()
                    .FirstOrDefault(candidate => candidate.Dispatcher == window.Dispatcher && candidate.IsVisible);
                if (dialog is null)
                {
                    if (started.Elapsed > TimeSpan.FromSeconds(2))
                    {
                        timer.Stop();
                        handled.TrySetException(new Xunit.Sdk.XunitException("Recording confirmation was not shown."));
                    }
                    return;
                }
                timer.Stop();
                try
                {
                    Assert.Same(window, dialog.Owner);
                    Assert.Equal("Keep & delete_accurately",
                        Assert.IsType<System.Windows.Controls.TextBlock>(dialog.FindName("RecordingTitleText")).Text);
                    var audio = Assert.IsType<System.Windows.Controls.RadioButton>(dialog.FindName("AudioOnlyDeletionScope"));
                    var all = Assert.IsType<System.Windows.Controls.RadioButton>(dialog.FindName("AllArtifactsDeletionScope"));
                    Assert.Equal(!audioDeleted, audio.IsEnabled);
                    Assert.Equal(!audioDeleted, audio.IsChecked);
                    Assert.Equal(audioDeleted, all.IsChecked);
                    Assert.True(audioDeleted ? all.IsKeyboardFocused : audio.IsKeyboardFocused);
                    all.IsChecked = allArtifacts;
                    audio.IsChecked = !allArtifacts;
                    var deletion = Assert.IsType<System.Windows.Controls.Button>(dialog.FindName("DeleteArtifactsButton"));
                    Assert.Equal(allArtifacts ? "Delete all artifacts" : "Delete audio only", deletion.Content);
                    if (confirm) deletion.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    else dialog.Close();
                    handled.TrySetResult();
                }
                catch (Exception exception)
                {
                    dialog.Close();
                    handled.TrySetException(exception);
                }
            };
            try
            {
                window.Show();
                await WaitUntilAsync(() => factory.Logger.Entries.Any(entry => entry.Id == 201));
                viewModel.SelectedRecording = viewModel.Recordings.Single(item => item.Id == recording.Id);
                await RenderAsync(window);
                timer.Start();
                var delete = Assert.IsType<System.Windows.Controls.Button>(window.FindName("DeleteRecordingButton"));
                Assert.True(delete.IsEnabled);
                delete.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await handled.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (confirm)
                {
                    await WaitUntilAsync(() => viewModel.Message.StartsWith("Audio deleted.", StringComparison.Ordinal) ||
                        viewModel.Message.StartsWith("The recording and", StringComparison.Ordinal) ||
                        viewModel.Message.StartsWith("Deletion", StringComparison.Ordinal));
                    Assert.Equal(allArtifacts ? "The recording and all its generated artifacts were deleted." :
                        "Audio deleted. Transcripts, subtitles, and history were kept.", viewModel.Message);
                }
                var persisted = await history.LoadAsync(CancellationToken.None);
                if (confirm && allArtifacts)
                {
                    Assert.Equal(other.Id, Assert.Single(persisted).Id);
                    Assert.False(File.Exists(textPath));
                    Assert.False(File.Exists(subtitlePath));
                }
                else
                {
                    Assert.Equal(confirm || audioDeleted, persisted.Single(item => item.Id == recording.Id).AudioDeleted);
                    Assert.Contains(persisted, item => item.Id == other.Id && !item.AudioDeleted);
                    Assert.Equal("Previously completed local transcript.", await File.ReadAllTextAsync(textPath));
                    Assert.True(File.Exists(subtitlePath));
                }
                Assert.Equal(!audioDeleted && !confirm, File.Exists(recording.AudioPath));
                Assert.True(File.Exists(other.AudioPath));
                if (confirm)
                {
                    if (allArtifacts)
                        Assert.True(Assert.IsType<System.Windows.Controls.ListBox>(window.FindName("HistoryList")).IsKeyboardFocused);
                    else
                        Assert.True(delete.IsKeyboardFocused);
                }
            }
            finally
            {
                timer.Stop();
                window.Dispatcher.UnhandledException -= failure;
                foreach (var owned in window.OwnedWindows.OfType<Window>().ToArray()) owned.Close();
                await window.RequestExitAsync();
            }
        });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Theory]
    [InlineData(MessageBoxResult.No)]
    [InlineData(MessageBoxResult.Yes)]
    public Task JobHistoryConfirmation_ExplainsRetainedArtifactsAndHonorsTheSafeDefault(MessageBoxResult choice) =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var recording = WritePlaybackWave(context, "job-history.wav");
            var text = Path.ChangeExtension(recording.AudioPath, ".txt");
            var subtitle = Path.ChangeExtension(recording.AudioPath, ".srt");
            await File.WriteAllTextAsync(text, "Keep completed evidence.");
            await File.WriteAllTextAsync(subtitle, "Keep subtitles.");
            var job = context.Job(recording, new(text, subtitle));
            recording = recording with { TranscriptOutputs = [new(job.Id, job.Outputs!)] };
            var history = new JsonRecordingHistory(Path.Combine(context.Directory, "job-history-recordings.json"));
            var repository = new JsonTranscriptionJobRepository(Path.Combine(context.Directory, "job-history-jobs.json"));
            await history.AddAsync(recording, CancellationToken.None);
            await repository.SaveAsync([job], CancellationToken.None);
            var queue = new TranscriptionQueue(context.Transcriber, new DesktopWriter(), repository, history: history);
            await queue.RestoreAsync(CancellationToken.None);
            var library = new RecordingLibrary(history, repository,
                cancelRecordingAsync: queue.CancelRecordingAsync, deleteJobAsync: queue.DeleteJobAsync,
                releasePlaybackAsync: (_, _) => Task.CompletedTask);
            var viewModel = new MainViewModel(context.Recorder, context.Engine, queue, context.Models, history,
                new RecoveryManager(context.Recovery), context, context.Logger, library);
            var dialogs = new DesktopDialogs { Answer = choice };
            var window = new MainWindow(viewModel, context.Engine, dialogs, new DesktopEnvironment(),
                new DesktopLogger<MainWindow>());
            try
            {
                viewModel.Refresh();
                viewModel.ShowJobsCommand.Execute(null);
                var jobs = Assert.IsType<System.Windows.Controls.ListBox>(window.FindName("JobsList"));
                await RenderAsync(window);
                jobs.SelectedIndex = 0;
                await RenderAsync(window);
                var delete = JobButton(window, "Delete job history...");
                Assert.True(delete.IsEnabled);
                delete.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var prompt = Assert.Single(dialogs.Prompts);
                Assert.Equal("Audio and completed outputs will be kept. Delete only this job-history entry?", prompt.Message);
                Assert.Equal("Delete transcription job history?", prompt.Title);
                Assert.Equal(MessageBoxButton.YesNo, prompt.Buttons);
                Assert.Equal(MessageBoxImage.Warning, prompt.Image);
                Assert.Equal(MessageBoxResult.No, prompt.Default);
                if (choice == MessageBoxResult.Yes) await WaitUntilAsync(() => viewModel.JobRows.Count == 0);
                Assert.Equal(choice == MessageBoxResult.Yes ? 0 : 1, (await repository.LoadAsync(CancellationToken.None)).Count);
                Assert.Single(await history.LoadAsync(CancellationToken.None));
                Assert.True(File.Exists(recording.AudioPath));
                Assert.Equal("Keep completed evidence.", await File.ReadAllTextAsync(text));
                Assert.Equal("Keep subtitles.", await File.ReadAllTextAsync(subtitle));
            }
            finally
            {
                await window.RequestExitAsync();
            }
        });

    [Fact]
    public Task ClosedWindow_ReleasesRefreshTimerAndIgnoresLateNativeSignals() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var timer = new DispatcherTimer();
            var environment = new DesktopEnvironment();
            var events = new MainWindowTests.DesktopLifecycleEvents();
            var presentations = 0;
            var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(), environment,
                new DesktopLogger<MainWindow>(), lifecycleEvents: events, timer: timer,
                presentation: new(() => presentations++, () => { }, () => true));
            await window.LoadAsync();
            Assert.True(timer.IsEnabled);
            environment.RequestOpen();
            Assert.Equal(1, presentations);
            var beforeClose = context.ViewModel.Recordings.ToArray();
            await window.RequestExitAsync();
            try
            {
                Assert.False(timer.IsEnabled);
                environment.RequestOpen();
                events.RaiseCompletion(context.Recording(Guid.NewGuid()));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.Equal(1, presentations);
                Assert.Equal(beforeClose, context.ViewModel.Recordings);
                Assert.Equal(1, environment.Disposals);
            }
            finally
            {
                timer.Stop();
            }
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ConfirmedExit_CancelsSavedCaptureQueueingBeforeWaitingForAnExistingWorkerToRelease(bool explicitExit) =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var saved = WritePlaybackWave(context, "saved-before-exit.wav");
            context.Engine.Result = saved;
            var existing = WritePlaybackWave(context, "existing-worker-at-exit.wav");
            var model = await context.Models.RegisterAsync("existing.zip", CancellationToken.None);
            context.Models.CurrentPackage = context.Models.Package;
            using var worker = new HeldCancellationTranscriber();
            var queue = new TranscriptionQueue(worker, new DesktopWriter(), context.Repository);
            var recorder = new RecorderApplication(context.Engine, queue, context.Models, context.History);
            var viewModel = new MainViewModel(recorder, context.Engine, queue, context.Models, context.History,
                new RecoveryManager(context.Recovery), context, context.Logger);
            viewModel.DestinationFolder = context.Directory;
            var releaseSelection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Models.Selecting = token => releaseSelection.Task.WaitAsync(token);
            var environment = new DesktopEnvironment();
            var dialogs = new DesktopDialogs { Answer = explicitExit ? MessageBoxResult.Yes : MessageBoxResult.No };
            var window = new MainWindow(viewModel, context.Engine, dialogs, environment,
                new DesktopLogger<MainWindow>());
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            Task? exit = null;
            try
            {
                await viewModel.InitializeCommand.ExecuteAsync(null);
                await queue.EnqueueAsync(existing, CancellationToken.None);
                await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                viewModel.SelectedModel = model;
                await WaitUntilAsync(() => viewModel.IsSelectingModel);
                viewModel.PermissionConfirmed = true;
                viewModel.SelectedMicrophoneId = "microphone";
                await viewModel.StartRecordingAsync(false);
                Assert.Same(saved, await viewModel.StopAsync(enqueueTranscription: true));
                Assert.Same(saved, Assert.Single(viewModel.Recordings));
                Assert.Single(queue.Jobs);
                Assert.True(viewModel.HasPendingViewWork);
                viewModel.Refresh();
                Assert.True(viewModel.HasActiveWork());
                Assert.True(viewModel.IsIdle);
                Assert.Equal(TranscriptionJobStatus.Running, Assert.Single(viewModel.Jobs).Status);
                if (explicitExit) exit = window.RequestExitAsync();
                else window.Close();
                await worker.CancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.False(closed.Task.IsCompleted);
                Assert.Equal(0, environment.Disposals);
                releaseSelection.TrySetResult();
                await WaitUntilAsync(() => !viewModel.HasPendingViewWork);
                Assert.DoesNotContain(queue.Jobs, job => job.Recording.Id == saved.Id);
                Assert.True(File.Exists(saved.AudioPath));
                Assert.Same(saved, Assert.Single(viewModel.Recordings));
                worker.ReleaseCancellation.Set();
                if (exit is not null) await exit.WaitAsync(TimeSpan.FromSeconds(3));
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(1, environment.Disposals);
                Assert.Single(dialogs.Prompts);
            }
            finally
            {
                releaseSelection.TrySetResult();
                worker.ReleaseCancellation.Set();
                worker.Finish.TrySetResult();
                await queue.WaitForIdleAsync(CancellationToken.None);
                if (exit is not null) await exit;
                if (!closed.Task.IsCompleted) await window.RequestExitAsync();
            }
        });

    [Fact]
    public Task NativeClose_WithPendingModelVerificationPublishesOneClosingEventAndReleasesOwnedServicesOnce() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var model = await context.Models.RegisterAsync("existing.zip", CancellationToken.None);
            var verification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Models.Selecting = async token =>
            {
                using var registration = token.Register(() => cancellation.TrySetResult());
                await verification.Task;
                token.ThrowIfCancellationRequested();
            };
            var environment = new DesktopEnvironment();
            var capture = new DesktopLifetime();
            var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(), environment,
                new DesktopLogger<MainWindow>(), captureLifetime: capture);
            var closingCalls = 0;
            window.Closing += (_, _) => closingCalls++;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            try
            {
                await context.ViewModel.InitializeCommand.ExecuteAsync(null);
                context.ViewModel.SelectedModel = model;
                await WaitUntilAsync(() => context.ViewModel.IsSelectingModel);
                window.Close();
                await cancellation.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Equal(0, closingCalls);
                Assert.Equal(0, capture.Disposals);
                Assert.Equal(0, environment.Disposals);
                verification.TrySetResult();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.SystemIdle);
                Assert.Equal(1, closingCalls);
                Assert.Equal(1, capture.Disposals);
                Assert.Equal(1, environment.Disposals);
                Assert.False(context.ViewModel.IsSelectingModel);
            }
            finally
            {
                verification.TrySetResult();
                if (!closed.Task.IsCompleted) await window.RequestExitAsync();
            }
        });

    [Fact]
    public Task ClosedWindow_StopsOwnedPlaybackAndReleasesTheSavedAudioHandle() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var recording = WritePlaybackWave(context, "close-playing.wav");
            var device = new PlaybackDevice();
            using var playback = new WindowsRecordingPlayback(() => device);
            var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models,
                context.History, new RecoveryManager(context.Recovery), context, context.Logger, playback: playback);
            var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(),
                new DesktopEnvironment(), new DesktopLogger<MainWindow>());
            try
            {
                viewModel.SelectedRecording = recording;
                viewModel.PlayRecordingCommand.Execute(null);
                Assert.True(playback.IsPlaying);
                Assert.Throws<IOException>(() => File.Open(recording.AudioPath,
                    FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                await window.RequestExitAsync();
                Assert.False(playback.IsPlaying);
                Assert.Equal(1, device.Disposals);
                using var exclusive = File.Open(recording.AudioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            finally
            {
                await window.RequestExitAsync();
            }
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Composition_DeletionReleasesOnlyTheMatchingPlaybackBeforeRemovingOwnedAudio(bool matching) =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var root = Path.Combine(context.Directory, "playback-composition");
            Directory.CreateDirectory(root);
            var target = WritePlaybackWave(context, "composition-delete.wav");
            var other = WritePlaybackWave(context, "composition-kept.wav");
            var history = new JsonRecordingHistory(Path.Combine(root, "history.json"));
            await history.AddAsync(target, CancellationToken.None);
            await history.AddAsync(other, CancellationToken.None);
            await new JsonTranscriptionJobRepository(Path.Combine(root, "jobs.json"))
                .SaveAsync([], CancellationToken.None);
            var stoppedWhileTargetExisted = new List<bool>();
            var output = new PlaybackDevice
            {
                Stopping = () => stoppedWhileTargetExisted.Add(File.Exists(target.AudioPath)),
            };
            using var playback = new WindowsRecordingPlayback(() => output);
            var window = new MainWindow(root, new AppLoggerFactory(), () => new DesktopDialogs(),
                () => new DesktopEnvironment(), _ => new MainWindowTests.DesktopLifecycleEvents(),
                context, (_, _, _) => context.Transcriber, playback);
            try
            {
                var viewModel = Assert.IsType<MainViewModel>(window.DataContext);
                await viewModel.InitializeCommand.ExecuteAsync(null);
                viewModel.SelectedRecording = viewModel.Recordings.Single(recording => recording.Id == target.Id);
                await RenderAsync(window);
                playback.Open(matching ? target : other);
                playback.Play();
                Assert.True(playback.IsPlaying);
                await viewModel.DeleteRecordingAsync(false);
                Assert.False(File.Exists(target.AudioPath));
                Assert.True(File.Exists(other.AudioPath));
                Assert.Equal(matching, stoppedWhileTargetExisted.Any(existed => existed));
                Assert.True((await history.LoadAsync(CancellationToken.None))
                    .Single(recording => recording.Id == target.Id).AudioDeleted);
                Assert.False((await history.LoadAsync(CancellationToken.None))
                    .Single(recording => recording.Id == other.Id).AudioDeleted);
                using var exclusive = File.Open(other.AudioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            finally
            {
                await window.RequestExitAsync();
            }
        });

    [Fact]
    public Task ClosedWindow_IsNotRetainedByItsLongerLivedNativeAdaptersOrViewModel() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var environment = new DesktopEnvironment();
            var events = new MainWindowTests.DesktopLifecycleEvents();
            var timer = new DispatcherTimer();
            var reference = ClosedWindowReference(context, environment, events, timer);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Collect();
            Assert.False(IsWindowAlive(reference));
            GC.KeepAlive(context);
            GC.KeepAlive(environment);
            GC.KeepAlive(events);
            GC.KeepAlive(timer);
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<MainWindow> ClosedWindowReference(DesktopTestContext context,
        DesktopEnvironment environment, MainWindowTests.DesktopLifecycleEvents events, DispatcherTimer timer)
    {
        var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(), environment,
            new DesktopLogger<MainWindow>(), lifecycleEvents: events, timer: timer);
        window.Close();
        return new(window);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsWindowAlive(WeakReference<MainWindow> reference) => reference.TryGetTarget(out _);

    [Fact]
    public Task CanceledWindowClosing_KeepsOwnedServicesAndDoesNotAuthorizeTheNextActiveExit() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var environment = new DesktopEnvironment();
            var capture = new DesktopLifetime();
            var timer = new DispatcherTimer();
            var dialogs = new DesktopDialogs { Answer = MessageBoxResult.Cancel };
            var window = new MainWindow(context.ViewModel, context.Engine, dialogs, environment,
                new DesktopLogger<MainWindow>(), captureLifetime: capture, timer: timer);
            var vetoOnce = true;
            window.Closing += (_, args) =>
            {
                if (vetoOnce)
                {
                    vetoOnce = false;
                    args.Cancel = true;
                }
            };
            try
            {
                await window.LoadAsync();
                window.Close();
                Assert.Equal(0, environment.Disposals);
                Assert.Equal(0, capture.Disposals);
                Assert.True(timer.IsEnabled);
                dialogs.Prompts.Clear();
                context.PrepareRecording();
                await context.ViewModel.StartRecordingAsync(false);
                window.Close();
                Assert.Single(dialogs.Prompts);
                Assert.Equal("Recording", context.ViewModel.Status);
                Assert.Equal(0, environment.Disposals);
                await context.ViewModel.StopAsync(false);
                await window.RequestExitAsync();
                Assert.False(timer.IsEnabled);
                Assert.Equal(1, environment.Disposals);
                Assert.Equal(1, capture.Disposals);
            }
            finally
            {
                timer.Stop();
                if (!context.ViewModel.IsIdle) await context.ViewModel.StopAsync(false);
                await window.RequestExitAsync();
            }
        });

    [Theory]
    [InlineData("none")]
    [InlineData("missing")]
    [InlineData("subtitle")]
    [InlineData("text")]
    public Task RevealRetainedOutput_UsesTheNewestAvailableOwnedFileOrExplainsItsAbsence(string available) =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var olderText = Path.Combine(context.Directory, "older.txt");
            var newerText = Path.Combine(context.Directory, "newer.txt");
            var olderSubtitle = Path.Combine(context.Directory, "older.srt");
            var newerSubtitle = Path.Combine(context.Directory, "newer.srt");
            if (available is "subtitle" or "text")
            {
                await File.WriteAllTextAsync(olderText, "An earlier successful attempt.");
                await File.WriteAllTextAsync(newerSubtitle, "Newer subtitle output.");
            }
            if (available == "text") await File.WriteAllTextAsync(newerText, "Newer transcript output.");
            var recording = context.Engine.Result with
            {
                AudioDeleted = true,
                Title = "A private recording title",
                TranscriptOutputs = available == "none" ? [] :
                    [new(Guid.NewGuid(), new(olderText, olderSubtitle)), new(Guid.NewGuid(), new(newerText, newerSubtitle))],
            };
            var environment = new DesktopEnvironment();
            var logger = new DesktopLogger<MainWindow>();
            var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(),
                environment, logger);
            try
            {
                window.OpenOutputFolder(recording);
                if (available is "subtitle" or "text")
                {
                    Assert.Equal(available == "text" ? newerText : newerSubtitle, Assert.Single(environment.Opened));
                }
                else
                {
                    Assert.Empty(environment.Opened);
                    Assert.Equal("No retained output file is available. Check the recording's output files.",
                        context.ViewModel.Message);
                    Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning &&
                        entry.Message.StartsWith("Retained output files unavailable for recording ", StringComparison.Ordinal));
                    Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("A private recording title", StringComparison.Ordinal));
                    Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(context.Directory, StringComparison.Ordinal));
                }
            }
            finally
            {
                await window.RequestExitAsync();
            }
        });

    [Fact]
    public Task LibraryNavigation_SelectingHistoryOrViewingACompletedJobReturnsToTheReaderWithoutCopying() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var recording = WritePlaybackWave(context, "read-selected.wav");
            var text = Path.Combine(context.Directory, "selected-completed.txt");
            await File.WriteAllTextAsync(text, "The selected successful transcript.");
            var job = context.Job(recording, new(text, Path.Combine(context.Directory, "selected-completed.srt")));
            context.Repository.Items.Add(job);
            await context.Queue.RestoreAsync(CancellationToken.None);
            context.ViewModel.Recordings.Add(recording);
            var otherRecording = WritePlaybackWave(context, "other-history.wav");
            context.ViewModel.Recordings.Add(otherRecording);
            var environment = new DesktopEnvironment();
            var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(), environment,
                new DesktopLogger<MainWindow>());
            try
            {
                context.ViewModel.Refresh();
                context.ViewModel.SelectedRecording = recording;
                await RenderAsync(window);
                context.ViewModel.ShowJobsCommand.Execute(null);
                var jobs = Assert.IsType<System.Windows.Controls.ListBox>(window.FindName("JobsList"));
                await RenderAsync(window);
                jobs.SelectedIndex = 0;
                await RenderAsync(window);
                var view = Elements<System.Windows.Controls.Button>((DependencyObject)window.Content)
                    .Single(button => Equals(button.Content, "View transcript"));
                Assert.True(view.IsEnabled);
                view.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                await WaitUntilAsync(() => context.ViewModel.IsLibraryView &&
                    context.ViewModel.Transcript == "The selected successful transcript.");
                await RenderAsync(window);
                Assert.True(context.ViewModel.IsLibraryView);
                Assert.Empty(environment.Copied);
                context.ViewModel.ShowModelsCommand.Execute(null);
                var history = Assert.IsType<System.Windows.Controls.ListBox>(window.FindName("HistoryList"));
                history.SelectedItem = otherRecording;
                await RenderAsync(window);
                Assert.True(context.ViewModel.IsLibraryView);
                Assert.Equal(otherRecording.Id, context.ViewModel.SelectedRecording?.Id);
                Assert.Empty(environment.Copied);
                Assert.Equal("The selected successful transcript.", await File.ReadAllTextAsync(text));
            }
            finally
            {
                await window.RequestExitAsync();
            }
        });

    [Fact]
    public Task JobFailure_RefreshesTheVisibleErrorAndKeepsRetrySeparateFromTranscriptReview() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var recording = WritePlaybackWave(context, "failure.wav");
            var worker = new DeferredFailureTranscriber();
            var queue = new TranscriptionQueue(worker, new DesktopWriter(), context.Repository);
            var viewModel = new MainViewModel(context.Recorder, context.Engine, queue, context.Models,
                context.History, new RecoveryManager(context.Recovery), context, context.Logger);
            await queue.EnqueueAsync(recording, CancellationToken.None);
            await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(),
                new DesktopEnvironment(), new DesktopLogger<MainWindow>());
            try
            {
                viewModel.Refresh();
                viewModel.ShowJobsCommand.Execute(null);
                var jobs = Assert.IsType<System.Windows.Controls.ListBox>(window.FindName("JobsList"));
                await RenderAsync(window);
                jobs.SelectedIndex = 0;
                await RenderAsync(window);
                var row = Assert.IsType<System.Windows.Controls.ListBoxItem>(jobs.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.DoesNotContain(Elements<System.Windows.Controls.TextBlock>(row), text => text.Text == "Worker could not read saved audio.");
                worker.Release.SetResult();
                await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                viewModel.Refresh();
                await RenderAsync(window);
                Assert.Same(row, jobs.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.Contains(Elements<System.Windows.Controls.TextBlock>(row), text => text.Text == "Failed");
                Assert.Contains(Elements<System.Windows.Controls.TextBlock>(row), text => text.Text == "Worker could not read saved audio.");
                Assert.True(JobButton(window, "Restart selected job").IsEnabled);
                Assert.False(JobButton(window, "Stop selected job").IsEnabled);
                Assert.False(JobButton(window, "Cancel selected job").IsEnabled);
                Assert.False(Elements<System.Windows.Controls.Button>((DependencyObject)window.Content)
                    .Single(button => Equals(button.Content, "View transcript")).IsEnabled);
            }
            finally
            {
                worker.Release.TrySetResult();
                await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                await window.RequestExitAsync();
            }
        });

    private sealed class DeferredFailureTranscriber : ILocalTranscriber
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TranscriptDocument> TranscribeAsync(SavedRecording recording, CancellationToken token)
        {
            Started.SetResult();
            await Release.Task.WaitAsync(token);
            throw new IOException("Worker could not read saved audio.");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task TerminalHistoryCheckpoint_ShowsFinishingAndKeepsDestructiveActionsUnavailableUntilSaved(bool succeeds) =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var recording = WritePlaybackWave(context, "terminal-checkpoint.wav");
            var history = new JsonRecordingHistory(Path.Combine(context.Directory, "checkpoint-recordings.json"));
            await history.AddAsync(recording, CancellationToken.None);
            var repository = new HeldTerminalRepository(
                new JsonTranscriptionJobRepository(Path.Combine(context.Directory, "checkpoint-jobs.json")));
            var failedWorker = new DeferredFailureTranscriber();
            context.Transcriber.Block = true;
            context.Transcriber.Segments = [new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "Controlled checkpoint content.")];
            var queue = new TranscriptionQueue(succeeds ? context.Transcriber : failedWorker,
                new SoundRecorder.Infrastructure.Transcription.AtomicTranscriptWriter(), repository, history: history);
            var library = new RecordingLibrary(history, repository, cancelRecordingAsync: queue.CancelRecordingAsync,
                deleteJobAsync: queue.DeleteJobAsync, releasePlaybackAsync: (_, _) => Task.CompletedTask);
            var viewModel = new MainViewModel(context.Recorder, context.Engine, queue, context.Models, history,
                new RecoveryManager(context.Recovery), context, context.Logger, library);
            var window = new MainWindow(viewModel, context.Engine, new DesktopDialogs(),
                new DesktopEnvironment(), new DesktopLogger<MainWindow>());
            try
            {
                await window.LoadAsync();
                await queue.EnqueueAsync(recording, CancellationToken.None);
                await (succeeds ? context.Transcriber.Started.Task : failedWorker.Started.Task)
                    .WaitAsync(TimeSpan.FromSeconds(2));
                window.RefreshStatus();
                viewModel.ShowJobsCommand.Execute(null);
                await RenderAsync(window);
                var jobs = Assert.IsType<System.Windows.Controls.ListBox>(window.FindName("JobsList"));
                jobs.SelectedIndex = 0;
                await RenderAsync(window);
                var row = Assert.IsType<System.Windows.Controls.ListBoxItem>(jobs.ItemContainerGenerator.ContainerFromIndex(0));
                var view = Elements<System.Windows.Controls.Button>((DependencyObject)window.Content)
                    .Single(button => Equals(button.Content, "View transcript"));
                Assert.False(view.IsEnabled);
                if (succeeds) context.Transcriber.Release.SetResult();
                else failedWorker.Release.SetResult();
                await repository.TerminalSaving.Task.WaitAsync(TimeSpan.FromSeconds(2));
                window.RefreshStatus();
                await RenderAsync(window);
                Assert.Contains(Elements<System.Windows.Controls.TextBlock>(row), text => text.Text == "Finishing");
                Assert.Equal(succeeds, view.IsEnabled);
                Assert.Equal(Visibility.Visible,
                    ((FrameworkElement)Assert.Single(Elements<System.Windows.Controls.ProgressBar>(row)).Parent).Visibility);
                Assert.False(JobButton(window, "Cancel selected job").IsEnabled);
                Assert.False(JobButton(window, "Stop selected job").IsEnabled);
                Assert.False(JobButton(window, "Restart selected job").IsEnabled);
                Assert.False(JobButton(window, "Delete job history...").IsEnabled);
                if (!succeeds)
                    Assert.Contains(Elements<System.Windows.Controls.TextBlock>(row), text => text.Text == "Worker could not read saved audio.");
                repository.Permit.SetResult();
                await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                window.RefreshStatus();
                await RenderAsync(window);
                Assert.Contains(Elements<System.Windows.Controls.TextBlock>(row), text => text.Text == (succeeds ? "Completed" : "Failed"));
                Assert.True(JobButton(window, "Restart selected job").IsEnabled);
                Assert.True(JobButton(window, "Delete job history...").IsEnabled);
                if (succeeds)
                {
                    var outputs = Assert.IsType<TranscriptOutputs>(Assert.Single(queue.Jobs).Outputs);
                    Assert.Contains("Controlled checkpoint content.", await File.ReadAllTextAsync(outputs.TextPath));
                    Assert.True(File.Exists(outputs.SubtitlePath));
                }
            }
            finally
            {
                context.Transcriber.Release.TrySetResult();
                failedWorker.Release.TrySetResult();
                repository.Permit.TrySetResult();
                await queue.WaitForIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                await window.RequestExitAsync();
            }
        });

    private sealed class HeldTerminalRepository(ITranscriptionJobRepository inner) : ITranscriptionJobRepository
    {
        public TaskCompletionSource TerminalSaving { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Permit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<TranscriptionJob>> LoadAsync(CancellationToken token) => inner.LoadAsync(token);
        public async Task SaveAsync(IReadOnlyList<TranscriptionJob> jobs, CancellationToken token)
        {
            if (jobs.Any(job => job.Status is not (TranscriptionJobStatus.Queued or TranscriptionJobStatus.Running)))
            {
                TerminalSaving.TrySetResult();
                await Permit.Task.WaitAsync(token);
            }
            await inner.SaveAsync(jobs, token);
        }
    }

    [Fact]
    public Task StartWithoutPermission_OpensCaptureSetupAndFocusesTheFreshConfirmationWithoutRecording() =>
        MainWindowTests.OnStaAsync(async () =>
        {
            using var context = new DesktopTestContext();
            var window = new MainWindow(context.ViewModel, context.Engine, new DesktopDialogs(),
                new DesktopEnvironment(), new DesktopLogger<MainWindow>());
            try
            {
                window.Show();
                await WaitUntilAsync(() => context.ViewModel.InitializationCompleted);
                var start = Assert.IsType<System.Windows.Controls.Button>(window.FindName("StartButton"));
                var permission = Assert.IsType<System.Windows.Controls.CheckBox>(window.FindName("PermissionCheckBox"));
                var invocation = Assert.IsAssignableFrom<IInvokeProvider>(
                    new ButtonAutomationPeer(start).GetPattern(PatternInterface.Invoke));
                invocation.Invoke();
                await WaitUntilAsync(() => permission.IsKeyboardFocused);
                Assert.True(Assert.IsType<System.Windows.Controls.Expander>(window.FindName("RecordingSetupExpander")).IsExpanded);
                Assert.False(context.ViewModel.PermissionConfirmed);
                Assert.Null(context.Engine.Request);
                Assert.Equal("Idle", context.ViewModel.Status);
            }
            finally
            {
                await window.RequestExitAsync();
            }
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordingPlayback_UnavailableSelectionReportsErrorWithoutOpeningAnotherSource(bool selected)
    {
        using var context = new DesktopTestContext();
        using var playback = new WindowsRecordingPlayback();
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models,
            context.History, new RecoveryManager(context.Recovery), context, context.Logger, playback: playback);
        if (selected) viewModel.SelectedRecording = context.Engine.Result;
        viewModel.PlayRecordingCommand.Execute(null);
        Assert.Contains(selected ? "could not be played" : "Select an available recording", viewModel.Message);
        Assert.Null(playback.SourcePath);
        Assert.False(playback.IsPlaying);
        viewModel.PlaybackVolume = .5;
        Assert.Equal(.5, playback.Volume);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RecordingPlayback_InvalidVolumeLeavesThePreviousLevel(double value)
    {
        using var playback = new WindowsRecordingPlayback();
        playback.Volume = .5;
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => playback.Volume = value);
        Assert.StartsWith("Playback volume must be between zero and one.", error.Message);
        Assert.Equal(.5, playback.Volume);
    }

    [Fact]
    public void RecordingPlayback_SeekLimitsMuteAndReopenKeepOnlyTheOwnedAudioOpen()
    {
        using var context = new DesktopTestContext();
        var first = WritePlaybackWave(context, "first.wav");
        var second = WritePlaybackWave(context, "second.wav");
        var outputs = new List<PlaybackDevice>();
        using var playback = new WindowsRecordingPlayback(() =>
        {
            var device = new PlaybackDevice();
            outputs.Add(device);
            return device;
        });
        playback.Open(first);
        playback.Play();
        playback.Volume = 0;
        Assert.Equal(0f, outputs[0].Volume);
        playback.Volume = 1;
        Assert.Equal(1f, outputs[0].Volume);
        foreach (var invalid in new[] { TimeSpan.FromTicks(-1), TimeSpan.FromSeconds(7) })
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() => playback.Position = invalid);
            Assert.StartsWith("Seek must remain within the saved audio.", error.Message);
            Assert.True(playback.IsPlaying);
            Assert.Equal(0, outputs[0].Disposals);
        }
        playback.Position = TimeSpan.Zero;
        Assert.True(playback.IsPlaying);
        Assert.Equal(TimeSpan.Zero, playback.Position);
        playback.Position = TimeSpan.FromSeconds(6);
        Assert.Equal(TimeSpan.FromSeconds(6), playback.Position);
        Assert.All(outputs.Take(2), output => Assert.Equal(1, output.Disposals));
        outputs[2].PlayedBytes = 96_000;
        Assert.Equal(TimeSpan.FromSeconds(6), playback.Position);
        var previousDeviceFailure = outputs[2].PendingFailure(new IOException("Previous device stopped late"));
        playback.Open(second);
        Assert.Equal(1, outputs[2].Disposals);
        Assert.False(playback.IsPlaying);
        Assert.Equal(TimeSpan.Zero, playback.Position);
        Assert.Equal(second.AudioPath, playback.SourcePath);
        using (File.Open(first.AudioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        playback.Play();
        previousDeviceFailure();
        Assert.Null(playback.LastError);
        outputs[3].Fail(new IOException("Disconnected"));
        Assert.Equal("The audio device could not continue playback: Disconnected", playback.LastError);
        outputs[3].Finish();
        Assert.Equal("The audio device could not continue playback: Disconnected", playback.LastError);
        playback.Play();
        Assert.Null(playback.LastError);
        playback.Dispose();
        Assert.Equal(1, outputs[3].Disposals);
        Assert.Null(playback.SourcePath);
        Assert.Equal(TimeSpan.Zero, playback.Position);
        Assert.Equal(TimeSpan.Zero, playback.Duration);
        using var exclusive = File.Open(second.AudioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void RecordingPlayback_FailedDeviceInitializationReleasesOutputAndCanRetry()
    {
        using var context = new DesktopTestContext();
        var recording = WritePlaybackWave(context, "initialization.wav");
        var error = new IOException("Device initialization failed");
        var failed = new PlaybackDevice { InitializationError = error };
        var working = new PlaybackDevice();
        var attempts = 0;
        using var playback = new WindowsRecordingPlayback(() => attempts++ == 0 ? failed : working);
        playback.Open(recording);
        Assert.Same(error, Assert.Throws<IOException>(playback.Play));
        Assert.Equal(1, failed.Disposals);
        Assert.False(playback.IsPlaying);
        playback.Play();
        Assert.True(playback.IsPlaying);
        Assert.Equal(2, attempts);
        playback.Close();
        Assert.Equal(1, working.Disposals);
        using var exclusive = File.Open(recording.AudioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void RecordingPlayback_DeletedAudioAndUnopenedCommandsExplainWhyPlaybackIsUnavailable()
    {
        using var context = new DesktopTestContext();
        var recording = WritePlaybackWave(context, "deleted.wav");
        using var playback = new WindowsRecordingPlayback();
        playback.Open(recording);
        Assert.Equal("The recording's audio has been deleted.",
            Assert.Throws<InvalidOperationException>(() => playback.Open(recording with { AudioDeleted = true })).Message);
        Assert.Null(playback.RecordingId);
        Assert.Null(playback.SourcePath);
        Assert.Equal("Open a recording before playback.", Assert.Throws<InvalidOperationException>(playback.Play).Message);
        Assert.Equal("Open a recording before seeking.",
            Assert.Throws<InvalidOperationException>(() => playback.Position = TimeSpan.Zero).Message);
        Assert.Null(Record.Exception(playback.Pause));
        using var exclusive = File.Open(recording.AudioPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void RecordingPlayback_DeviceWithoutPositionReportingFailsExplicitly()
    {
        using var context = new DesktopTestContext();
        var recording = WritePlaybackWave(context, "position.wav");
        using var playback = new WindowsRecordingPlayback(() => new PositionlessPlaybackDevice());
        playback.Open(recording);
        playback.Play();
        Assert.Equal("The playback device cannot report its audio position.",
            Assert.Throws<NotSupportedException>(() => playback.Position).Message);
    }

    private static SavedRecording WritePlaybackWave(DesktopTestContext context, string name)
    {
        var path = Path.Combine(context.Directory, name);
        using var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(48_000, 16, 1));
        writer.Write(new byte[6 * 96_000], 0, 6 * 96_000);
        return new(Guid.NewGuid(), path, "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(6));
    }

    [Fact]
    public void RecordingPlayback_DevicePositionFailureIsReportedWithoutBreakingRefresh()
    {
        using var context = new DesktopTestContext();
        var path = Path.Combine(context.Directory, "playback-error.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(48_000, 16, 1)))
        {
            writer.Write(new byte[96_000], 0, 96_000);
        }
        var device = new PlaybackDevice();
        using var playback = new WindowsRecordingPlayback(() => device);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models,
            context.History, new RecoveryManager(context.Recovery), context, context.Logger, playback: playback);
        viewModel.SelectedRecording = new SavedRecording(Guid.NewGuid(), path, "English",
            RecordingOutcome.Complete, TimeSpan.FromSeconds(1));
        viewModel.PlayRecordingCommand.Execute(null);
        device.PositionError = new IOException("Audio device disconnected");

        Assert.Null(Record.Exception(viewModel.Refresh));
        Assert.Contains("Audio device disconnected", viewModel.Message);
        Assert.False(viewModel.IsPlaying);
    }

    [Fact]
    public void RecordingPlayback_UserPlayOpensOnlyTheSelectedRecording()
    {
        using var context = new DesktopTestContext();
        var path = Path.Combine(context.Directory, "selected-playback.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(48_000, 16, 1)))
        {
            writer.Write(new byte[6 * 96_000], 0, 6 * 96_000);
        }
        using var playback = new WindowsRecordingPlayback(() => new PlaybackDevice());
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models,
            context.History, new RecoveryManager(context.Recovery), context, context.Logger, playback: playback);
        var recording = new SavedRecording(Guid.NewGuid(), path, "English", RecordingOutcome.Complete,
            TimeSpan.FromSeconds(6));
        viewModel.SelectedRecording = recording;
        Assert.Null(Record.Exception(() => viewModel.PlayRecordingCommand.Execute(null)));
        Assert.Equal(recording.Id, playback.RecordingId);
        Assert.True(playback.IsPlaying);
    }

    [Fact]
    public void RecordingPlayback_LeavingReviewPausesWithoutLosingPosition()
    {
        using var context = new DesktopTestContext();
        var path = Path.Combine(context.Directory, "view-playback.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(48_000, 16, 1)))
        {
            writer.Write(new byte[6 * 96_000], 0, 6 * 96_000);
        }
        var device = new PlaybackDevice();
        using var playback = new WindowsRecordingPlayback(() => device);
        var viewModel = new MainViewModel(context.Recorder, context.Engine, context.Queue, context.Models,
            context.History, new RecoveryManager(context.Recovery), context, context.Logger,
            playback: playback);
        var recording = new SavedRecording(Guid.NewGuid(), path, "English",
            RecordingOutcome.Complete, TimeSpan.FromSeconds(6));
        viewModel.SelectedRecording = recording;
        playback.Open(recording);
        playback.Play();
        device.PlayedBytes = 96_000;
        viewModel.ShowModelsCommand.Execute(null);
        Assert.False(playback.IsPlaying);
        Assert.Equal(TimeSpan.FromSeconds(1), playback.Position);
    }

    [Fact]
    public void RecordingPlayback_UsesDevicePositionAndRetainsSeekOnResume()
    {
        using var context = new DesktopTestContext();
        var path = Path.Combine(context.Directory, "playback-controls.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(48_000, 16, 1)))
        {
            writer.Write(new byte[6 * 96_000], 0, 6 * 96_000);
        }
        var outputs = new List<PlaybackDevice>();
        using var playback = new WindowsRecordingPlayback(() =>
        {
            var output = new PlaybackDevice();
            outputs.Add(output);
            return output;
        });
        playback.Open(new(Guid.NewGuid(), path, "English", RecordingOutcome.Complete, TimeSpan.FromSeconds(6)));
        playback.Volume = .5;
        playback.Play();
        outputs[0].PlayedBytes = 96_000;
        Assert.True(playback.IsPlaying);
        Assert.Equal(TimeSpan.FromSeconds(1), playback.Position);
        Assert.Equal(.5f, outputs[0].Volume);
        playback.Pause();
        Assert.False(playback.IsPlaying);
        playback.Position = TimeSpan.FromSeconds(4);
        playback.Play();
        Assert.Equal(2, outputs.Count);
        Assert.Equal(TimeSpan.FromSeconds(4), playback.Position);
        outputs[1].Fail(new IOException("Output unavailable"));
        Assert.Contains("Output unavailable", playback.LastError);
        playback.Close();
        Assert.False(playback.IsPlaying);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private sealed class PlaybackDevice : NAudio.Wave.IWavePlayer, NAudio.Wave.IWavePosition
    {
        public Exception? InitializationError { get; set; }
        public int Disposals { get; private set; }
        public Exception? PositionError { get; set; }
        public long PlayedBytes { get; set; }
        public float Volume { get; set; }
        public NAudio.Wave.PlaybackState PlaybackState { get; private set; }
        public NAudio.Wave.WaveFormat OutputWaveFormat { get; private set; } = new(48_000, 16, 1);
        public event EventHandler<NAudio.Wave.StoppedEventArgs>? PlaybackStopped;
        public void Init(NAudio.Wave.IWaveProvider provider)
        {
            if (InitializationError is { } error) throw error;
            OutputWaveFormat = provider.WaveFormat;
        }
        public void Play() => PlaybackState = NAudio.Wave.PlaybackState.Playing;
        public void Pause() => PlaybackState = NAudio.Wave.PlaybackState.Paused;
        public Action? Stopping { get; set; }
        public void Stop()
        {
            Stopping?.Invoke();
            PlaybackState = NAudio.Wave.PlaybackState.Stopped;
        }
        public long GetPosition() => PositionError is { } error ? throw error : PlayedBytes;
        public void Dispose() { Disposals++; Stop(); }
        public void Finish()
        {
            Stop();
            PlaybackStopped?.Invoke(this, new(null));
        }
        public Action PendingFailure(Exception exception)
        {
            var callback = PlaybackStopped;
            return () => callback?.Invoke(this, new(exception));
        }
        public void Fail(Exception exception)
        {
            Stop();
            PlaybackStopped?.Invoke(this, new(exception));
        }
    }

    private sealed class PositionlessPlaybackDevice : NAudio.Wave.IWavePlayer
    {
        public float Volume { get; set; }
        public NAudio.Wave.WaveFormat OutputWaveFormat { get; private set; } = new(48_000, 16, 1);
        public NAudio.Wave.PlaybackState PlaybackState { get; private set; }
        public event EventHandler<NAudio.Wave.StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(NAudio.Wave.IWaveProvider provider) => OutputWaveFormat = provider.WaveFormat;
        public void Play() => PlaybackState = NAudio.Wave.PlaybackState.Playing;
        public void Pause() => PlaybackState = NAudio.Wave.PlaybackState.Paused;
        public void Stop() => PlaybackState = NAudio.Wave.PlaybackState.Stopped;
        public void Dispose() => Stop();
    }

    [Fact]
    public void RecordingPlayback_OpensSeeksAndReleasesTheActualWave()
    {
        using var context = new DesktopTestContext();
        var path = Path.Combine(context.Directory, "playback.wav");
        using (var writer = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(48_000, 16, 1)))
        {
            writer.Write(new byte[6 * 96_000], 0, 6 * 96_000);
        }
        var recording = new SavedRecording(Guid.NewGuid(), path, "English",
            RecordingOutcome.Complete, TimeSpan.FromSeconds(6));
        using var playback = new WindowsRecordingPlayback();
        Assert.Null(Record.Exception(() => playback.Open(recording)));
        Assert.Equal(TimeSpan.FromSeconds(6), playback.Duration);
        playback.Position = TimeSpan.FromSeconds(4);
        Assert.Equal(TimeSpan.FromSeconds(4), playback.Position);
        playback.Close();
        Assert.Null(playback.RecordingId);
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("power")]
    [InlineData("session")]
    [InlineData("capture")]
    public void LifecycleEventAdapter_SubscribeAndUnsubscribe_OwnsAndReleasesListener(string signal)
    {
        using var context = new DesktopTestContext();
        using var engine = new WindowsRecordingEngine(context.Directory);
        var events = new WindowsDesktopLifecycleEvents(engine);
        Action<NativeListener> subscribe = signal switch
        {
            "power" => listener => events.PowerModeChanged += listener.Power,
            "session" => listener => events.SessionEnding += listener.Session,
            _ => listener => events.RecordingCompleted += listener.Completed,
        };
        Action<NativeListener> unsubscribe = signal switch
        {
            "power" => listener => events.PowerModeChanged -= listener.Power,
            "session" => listener => events.SessionEnding -= listener.Session,
            _ => listener => events.RecordingCompleted -= listener.Completed,
        };
        var reference = Subscribe(subscribe);
        try
        {
            Collect();
            Assert.True(IsAlive(reference));
            Unsubscribe(reference, unsubscribe);
            Collect();
            Assert.False(IsAlive(reference));
        }
        finally
        {
            if (reference.TryGetTarget(out var listener))
            {
                SystemEvents.PowerModeChanged -= listener.Power;
                SystemEvents.SessionEnding -= listener.Session;
                engine.RecordingCompleted -= listener.Completed;
            }
        }
    }

    [Fact]
    public Task NotificationEventAdapter_SubscribeAndUnsubscribe_OwnsAndReleasesListener() =>
        MainWindowTests.OnStaAsync(() =>
        {
            using var native = new NotifyIcon();
            using var adapter = new WindowsNotificationArea(native);
            var reference = Subscribe(listener => adapter.DoubleClick += listener.DoubleClick);
            try
            {
                Collect();
                Assert.True(IsAlive(reference));
                Unsubscribe(reference, listener => adapter.DoubleClick -= listener.DoubleClick);
                Collect();
                Assert.False(IsAlive(reference));
            }
            finally
            {
                if (reference.TryGetTarget(out var listener)) native.DoubleClick -= listener.DoubleClick;
            }
            Assert.False(native.Visible);
            return Task.CompletedTask;
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<NativeListener> Subscribe(Action<NativeListener> subscribe)
    {
        var listener = new NativeListener();
        subscribe(listener);
        return new(listener);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Unsubscribe(WeakReference<NativeListener> reference, Action<NativeListener> unsubscribe)
    {
        Assert.True(reference.TryGetTarget(out var listener));
        unsubscribe(listener);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<NativeListener> reference) => reference.TryGetTarget(out _);

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class NativeListener
    {
        public void Power(object sender, PowerModeChangedEventArgs args) { }
        public void Session(object sender, SessionEndingEventArgs args) { }
        public void Completed(object? sender, SavedRecording recording) { }
        public void DoubleClick(object? sender, EventArgs args) { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public Task ChooseDestination_DialogResult_ReturnsOnlyConfirmedSingleFolder(bool? answer) =>
        MainWindowTests.OnStaAsync(() =>
        {
            var dialogs = new WindowsDesktopDialogs(dialog =>
            {
                var folder = Assert.IsType<OpenFolderDialog>(dialog);
                Assert.False(folder.Multiselect);
                folder.FolderName = @"C:\Selected destination";
                return answer;
            }, (_, _, _, _, _) => throw new InvalidOperationException());
            Assert.Equal(answer == true ? @"C:\Selected destination" : null, dialogs.ChooseDestination());
            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public Task ChooseModelPackage_DialogResult_ReturnsOnlyConfirmedZipPackage(bool? answer) =>
        MainWindowTests.OnStaAsync(() =>
        {
            var dialogs = new WindowsDesktopDialogs(dialog =>
            {
                var file = Assert.IsType<Microsoft.Win32.OpenFileDialog>(dialog);
                Assert.Equal("Sound Recorder model package (*.zip)|*.zip", file.Filter);
                file.FileName = @"C:\Selected model.zip";
                return answer;
            }, (_, _, _, _, _) => throw new InvalidOperationException());
            Assert.Equal(answer == true ? @"C:\Selected model.zip" : null, dialogs.ChooseModelPackage());
            return Task.CompletedTask;
        });

    [Fact]
    public void Show_DialogConfiguration_ForwardsQuestionAndChoiceUnchanged()
    {
        var dialogs = new WindowsDesktopDialogs(_ => throw new InvalidOperationException(),
            (text, title, buttons, image, defaultResult) =>
            {
                Assert.Equal("Stop work?", text);
                Assert.Equal("Confirm exit", title);
                Assert.Equal(MessageBoxButton.YesNoCancel, buttons);
                Assert.Equal(MessageBoxImage.Question, image);
                Assert.Equal(MessageBoxResult.No, defaultResult);
                return MessageBoxResult.Cancel;
            });
        Assert.Equal(MessageBoxResult.Cancel, dialogs.Show("Stop work?", "Confirm exit",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No));
    }

    [Fact]
    public Task Environment_NotificationActions_ConfigureAndRouteWithoutNativeDesktopEffects() =>
        MainWindowTests.OnStaAsync(() =>
        {
            var icon = new DesktopNotificationArea();
            var wake = new List<WindowsDesktopEnvironment.ExecutionState>();
            var copies = new List<string>();
            var processes = new List<ProcessStartInfo>();
            using (var environment = new WindowsDesktopEnvironment(
                icon, flags => { wake.Add(flags); return flags; }, copies.Add,
                info => { processes.Add(info); return null; }))
            {
                Assert.Same(SystemIcons.Application, icon.Icon);
                Assert.Equal("Sound Recorder", icon.Text);
                Assert.True(icon.Visible);
                Assert.NotNull(icon.ContextMenuStrip);
                Assert.Equal(["Open Sound Recorder", "Exit"], icon.ContextMenuStrip.Items
                    .Cast<ToolStripItem>().Select(item => item.Text));
                var opened = 0;
                var exited = 0;
                environment.OpenRequested += (_, _) => opened++;
                environment.ExitRequested += (_, _) => exited++;
                icon.RaiseDoubleClick();
                icon.ContextMenuStrip.Items[0].PerformClick();
                icon.ContextMenuStrip.Items[1].PerformClick();
                Assert.Equal(2, opened);
                Assert.Equal(1, exited);
                environment.SetNotificationText("Sound Recorder - recording");
                Assert.Equal("Sound Recorder - recording", icon.Text);
                environment.ShowBalloonTip(5000, "Limit", "Saved at four hours", ToolTipIcon.Warning);
                Assert.Equal((5000, "Limit", "Saved at four hours", ToolTipIcon.Warning),
                    Assert.Single(icon.Balloons));
                environment.PreventIdleSleep(true);
                environment.PreventIdleSleep(false);
                Assert.Equal(new[]
                {
                    WindowsDesktopEnvironment.ExecutionState.Continuous |
                    WindowsDesktopEnvironment.ExecutionState.SystemRequired,
                    WindowsDesktopEnvironment.ExecutionState.Continuous,
                }, wake);
                environment.CopyTranscript("Local content");
                Assert.Equal("Local content", Assert.Single(copies));
                environment.OpenRecordingFolder(@"C:\Audio\meeting one.wav");
                var process = Assert.Single(processes);
                Assert.Equal("explorer.exe", process.FileName);
                Assert.Equal("/select,\"C:\\Audio\\meeting one.wav\"", process.Arguments);
                Assert.True(process.UseShellExecute);
            }
            Assert.Equal(1, icon.Disposals);
            return Task.CompletedTask;
        });

    [Fact]
    public Task WindowsNotificationArea_UnshownIcon_KeepsNativeStateWithoutPublishingIt() =>
        MainWindowTests.OnStaAsync(() =>
        {
            using var icon = new WindowsNotificationArea();
            using var menu = new ContextMenuStrip();
            Assert.False(icon.Visible);
            icon.Text = "Sound Recorder";
            icon.Icon = SystemIcons.Application;
            icon.ContextMenuStrip = menu;
            Assert.Equal("Sound Recorder", icon.Text);
            Assert.Same(SystemIcons.Application, icon.Icon);
            Assert.Same(menu, icon.ContextMenuStrip);
            icon.Visible = false;
            Assert.False(icon.Visible);
            return Task.CompletedTask;
        });

    [Fact]
    public void ApplicationDiagnostics_IsolatedApplicationRoot_WritesInformationAndFlushesOnExit()
    {
        using var context = new DesktopTestContext();
        var lifecycle = new ApplicationLifecycle(new DesktopDialogs(),
            _ => throw new InvalidOperationException(), context.Directory);
        lifecycle.InitializeDesktop();
        var logger = lifecycle.LoggerFactory!.CreateLogger("diagnostic-test");
        logger.LogDebug("Debug is below the application minimum");
        logger.LogInformation("Local operation completed");
        lifecycle.CompleteExit(0);
        Assert.StartsWith(Path.Combine(context.Directory, "SoundRecorder", "logs"),
            lifecycle.LogFilePath);
        var text = File.ReadAllText(lifecycle.LogFilePath!);
        Assert.Contains("Application starting", text);
        Assert.Contains("Local operation completed", text);
        Assert.Contains("Application exited with code 0", text);
        Assert.DoesNotContain("Debug is below", text);
    }

    internal static Task VerifyApplicationTaskFailureLifetimeAsync(WpfApplicationHarness applicationHarness) =>
        applicationHarness.InvokeAsync(async harness =>
        {
            harness.Reset();
            var app = harness.Application;
            var observations = new System.Collections.Concurrent.ConcurrentQueue<bool>();
            var marker = "Controlled background failure " + Guid.NewGuid().ToString("N");
            EventHandler<UnobservedTaskExceptionEventArgs> observer = (_, args) =>
            {
                if (args.Exception.InnerExceptions.Any(error => error.Message.StartsWith(marker, StringComparison.Ordinal)))
                {
                    observations.Enqueue(args.Observed);
                    args.SetObserved();
                }
            };
            try
            {
                await app.InitializeAsync([]);
                TaskScheduler.UnobservedTaskException += observer;
                AbandonFailedTask(marker + " while open");
                Collect();
                await WaitUntilAsync(() => observations.Count == 1);
                Assert.True(Assert.Single(observations));
                Assert.Contains(harness.Factory.Logger.Exceptions, exception =>
                    exception is AggregateException aggregate &&
                    aggregate.InnerExceptions.Any(error => error.Message == marker + " while open"));
                app.CompleteExit(0);
                var logged = harness.Factory.Logger.Exceptions.Count;
                AbandonFailedTask(marker + " after exit");
                Collect();
                await WaitUntilAsync(() => observations.Count == 2);
                Assert.Equal(new[] { true, false }, observations.ToArray());
                Assert.Equal(logged, harness.Factory.Logger.Exceptions.Count);
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= observer;
                app.CompleteExit(0);
            }
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonFailedTask(string message) =>
        _ = Task.FromException(new IOException(message));
}

internal sealed class HeldCancellationTranscriber : IJobTranscriber, IDisposable
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CancellationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ManualResetEventSlim ReleaseCancellation { get; } = new(false);

    public Task<TranscriptDocument> TranscribeAsync(TranscriptionRequest request,
        IProgress<TranscriptionProgress>? progress, CancellationToken token) =>
        TranscribeAsync(request.Recording, token);

    public async Task<TranscriptDocument> TranscribeAsync(SavedRecording recording, CancellationToken token)
    {
        using var registration = token.Register(() =>
        {
            CancellationStarted.TrySetResult();
            if (!ReleaseCancellation.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The owned worker cancellation was not released.");
            Finish.TrySetResult();
        });
        Started.TrySetResult();
        await Finish.Task;
        token.ThrowIfCancellationRequested();
        return new(recording, recording.Language, []);
    }

    public void Dispose()
    {
        ReleaseCancellation.Set();
        ReleaseCancellation.Dispose();
    }
}

internal sealed class DesktopNotificationArea : INotificationArea
{
    public Icon? Icon { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool Visible { get; set; }
    public ContextMenuStrip? ContextMenuStrip { get; set; }
    public event EventHandler? DoubleClick;
    public int Disposals { get; private set; }
    public List<(int, string, string, ToolTipIcon)> Balloons { get; } = [];
    public void ShowBalloonTip(int milliseconds, string title, string message, ToolTipIcon icon) =>
        Balloons.Add((milliseconds, title, message, icon));
    public void Dispose() => Disposals++;
    public void RaiseDoubleClick() => DoubleClick?.Invoke(this, EventArgs.Empty);
}
