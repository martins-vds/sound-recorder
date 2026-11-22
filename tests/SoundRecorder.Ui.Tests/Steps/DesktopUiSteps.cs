using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Reqnroll;

namespace SoundRecorder.Ui.Tests.Steps;

[Binding]
public sealed class DesktopUiSteps
{
    private Process? _process;
    private Task<string>? _hostErrors;
    private UIA3Automation? _automation;
    private Application? _application;
    private Window? _window;
    private DirectoryInfo? _directory;
    private string? _size;
    private string[]? _catalogDownloadedModels;
    private string? _catalogSelectedModel;
    private Dictionary<string, byte[]>? _beforeDeletion;
    private Dictionary<string, byte[]>? _beforeJobControl;
    private readonly List<int> _observedRecognitionEstimates = [];
    private double _playbackBeforeProgress;
    private int _libraryCountBeforeFilter;
    private Dictionary<string, string>? _captureSettings;

    [Given("the isolated recorder UI is open with {string} fixtures")]
    public void OpenRecorder(string fixtures)
    {
        _directory = Directory.CreateTempSubdirectory("sound-recorder-ui-");
        var executable = Path.Combine(AppContext.BaseDirectory, "UiHost", "SoundRecorder.Ui.TestHost.exe");
        _process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            ArgumentList = { _directory.FullName, fixtures },
        }) ?? throw new InvalidOperationException("The UI test host did not start.");
        _hostErrors = _process.StandardError.ReadToEndAsync();
        _automation = new UIA3Automation();
        _application = Application.Attach(_process.Id);
        WaitUntil(() =>
        {
            _window = _application.GetMainWindow(_automation, TimeSpan.FromMilliseconds(100));
            return _window is not null && _window.FindFirstDescendant(cf => cf.ByAutomationId("StartButton")) is not null;
        }, "The isolated recorder window did not appear.");
        WaitUntil(() => Element("StartButton").IsEnabled,
            "The UI did not finish loading local state.");
    }

    [Then("the recording status reads {string}")]
    public void RecordingStatus(string expected) =>
        WaitUntil(() => Element("StatusText").Name == expected, $"Expected recording status '{expected}'.");

    [Then("the library is to the left of the transcript reader")]
    public void LibraryBesideReader() =>
        Assert.True(Element("HistoryList").BoundingRectangle.Right <=
            Element("TranscriptScroll").BoundingRectangle.Left);

    [Then("the capture dock is below the library and reader")]
    public void CaptureBelowReader()
    {
        var dockTop = Math.Min(Element("StatusText").BoundingRectangle.Top,
            Element("StartButton").BoundingRectangle.Top);
        Assert.True(Element("HistoryList").BoundingRectangle.Bottom <= dockTop);
        Assert.True(Element("TranscriptScroll").BoundingRectangle.Bottom <= dockTop);
    }

    [Then("the library, reader, capture actions, and job navigation fit inside the client area")]
    public void WorkspaceControlsFit()
    {
        var bounds = _window!.BoundingRectangle;
        foreach (var item in new[] { Element("HistoryList"), Element("TranscriptScroll"),
                     Element("StartButton"), Button("View transcription jobs") })
        {
            var rectangle = item.BoundingRectangle;
            Assert.False(item.Properties.IsOffscreen.Value);
            Assert.True(rectangle.Width > 0 && rectangle.Height > 0);
            Assert.True(rectangle.Left >= bounds.Left && rectangle.Right <= bounds.Right &&
                rectangle.Top >= bounds.Top && rectangle.Bottom <= bounds.Bottom);
        }
    }

    [Then("only one recording and one transcript reader are selected")]
    public void SingleRecordingReview()
    {
        Assert.Single(Element("HistoryList").Patterns.Selection.Pattern.Selection.Value);
        Assert.Contains("UI fixture transcript.", Element("TranscriptText").Patterns.Value.Pattern.Value.Value);
    }

    [Then("the prototype variant switcher is absent")]
    public void NoPrototypeSwitcher()
    {
        Assert.Null(_window!.FindFirstDescendant(cf => cf.ByName("Previous variant")));
        Assert.Null(_window.FindFirstDescendant(cf => cf.ByName("Next variant")));
    }

    [Given("a recording is selected for review")]
    public void RecordingSelectedForReview()
    {
        SelectRecording();
        _libraryCountBeforeFilter = Element("HistoryList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Length;
    }

    [When("the user searches the library for an unmatched phrase")]
    public void SearchUnmatched() => Element("LibrarySearchBox").AsTextBox().Text = "no fixture can match this phrase";

    [Then("the library explains that no meetings match")]
    public void NoSearchMatches() =>
        WaitUntil(() => Element("LibraryMessageText").Name.Contains("No matching meetings", StringComparison.Ordinal),
            "The library did not explain the unmatched query.");

    [Then("the selected recording and its transcript remain in the reader")]
    public void ReviewRetained() =>
        Assert.Contains("UI fixture transcript.", Element("TranscriptText").Patterns.Value.Pattern.Value.Value);

    [Then("clearing the query restores the library results")]
    public void ClearLibraryQuery()
    {
        Element("LibrarySearchBox").AsTextBox().Text = string.Empty;
        HistoryCount(_libraryCountBeforeFilter);
    }

    [When("the user plays the selected recording and scrolls its transcript")]
    public void PlayAndRead()
    {
        Button("Play recording").Invoke();
        var scroll = Element("TranscriptScroll").Patterns.Scroll.Pattern;
        if (scroll.VerticallyScrollable.Value)
        {
            scroll.SetScrollPercent(-1, 10);
        }
    }

    [Then("the actual fixture WAV is the playback source")]
    public void ActualPlaybackSource() =>
        Assert.Equal(Path.Combine(_directory!.FullName, "fixture.wav"),
            Button("Play recording").Properties.HelpText.Value);

    [Then("playback controls stay visible with at least two complete transcript text lines")]
    public void PlayerAndTextVisible()
    {
        Assert.False(Button("Play recording").Properties.IsOffscreen.Value);
        Assert.False(Element("PlaybackPositionSlider").Properties.IsOffscreen.Value);
        var viewport = Element("TranscriptScroll").BoundingRectangle;
        var lines = Element("TranscriptText").Patterns.Text.Pattern.DocumentRange.GetBoundingRectangles();
        Assert.True(lines.Count(rectangle => rectangle.Top >= viewport.Top && rectangle.Bottom <= viewport.Bottom) >= 2,
            "The reader does not show two complete text lines while playback controls are visible.");
    }

    [Then("playback position advances without starting a transcription job")]
    public void PlaybackAdvancesWithoutTranscribing()
    {
        WaitUntil(() => Element("PlaybackPositionSlider").Patterns.RangeValue.Pattern.Value.Value > 0,
            "Recording playback position did not advance.");
        Button("View transcription jobs").Invoke();
        WaitUntil(() => _window!.FindFirstDescendant(cf => cf.ByAutomationId("JobsList")) is not null,
            "Transcription jobs view did not open.");
        JobCount(1);
    }

    [Then("the captured duration reads {string}")]
    public void CapturedDuration(string expected) =>
        Assert.Equal(expected, Element("DurationText").Name);

    [Then("recording permission is unchecked")]
    public void PermissionUnchecked()
    {
        ExpandRecordingSetup();
        Assert.False(Element("PermissionCheckBox").AsCheckBox().IsChecked);
    }

    [When("the user expands recording setup")]
    public void ExpandRecordingSetup()
    {
        var setup = Element("RecordingSetupExpander").Patterns.ExpandCollapse.Pattern;
        if (setup.ExpandCollapseState.Value == ExpandCollapseState.Collapsed)
        {
            setup.Expand();
        }
        WaitUntil(() => _window!.FindFirstDescendant(cf => cf.ByAutomationId("SourceCombo")) is not null,
            "Recording setup did not expand.");
    }

    [Then("Pause, Resume, and Stop are not offered as idle capture actions")]
    public void IdleActions()
    {
        foreach (var id in new[] { "PauseButton", "ResumeButton", "StopButton" })
        {
            var control = _window!.FindFirstDescendant(cf => cf.ByAutomationId(id));
            Assert.True(control is null || control.Properties.IsOffscreen.Value);
        }
    }

    [Then("recording setup and the permission confirmation are visible")]
    public void PermissionVisible() =>
        WaitUntil(() => !Element("PermissionCheckBox").Properties.IsOffscreen.Value,
            "Permission confirmation is not visible.");

    [Then("no recording file or transcription job is created")]
    public void NoRecordingCreated()
    {
        Assert.Empty(_directory!.GetFiles("*.wav"));
        Button("View transcription jobs").Invoke();
        WaitUntil(() => _window!.FindFirstDescendant(cf => cf.ByAutomationId("JobsList")) is not null,
            "Transcription jobs view did not open.");
        Assert.Empty(Element("JobsList").FindAllChildren(cf => cf.ByControlType(ControlType.DataItem)));
    }

    [Then("the {string} button is disabled")]
    public void ButtonDisabled(string name) =>
        WaitUntil(() => !Button(name).IsEnabled, $"Expected '{name}' to be disabled.");

    [Then("the {string} button is enabled")]
    public void ButtonEnabled(string name) =>
        WaitUntil(() => Button(name).IsEnabled, $"Expected '{name}' to be enabled.");

    [When(@"^the user resizes the recorder to (\d+) by (\d+)$")]
    public void ResizeRecorder(int width, int height)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Assert.NotEqual(IntPtr.Zero, previous);
        try
        {
            var dpi = GetDpiForWindow(new IntPtr(_window!.Properties.NativeWindowHandle.Value));
            Assert.NotEqual(0u, dpi);
            var scale = dpi / 96d;
            _window.Patterns.Transform.Pattern.Resize(width * scale, height * scale);
            _size = $"{width}x{height}";
            WaitUntil(() => Math.Abs(_window.BoundingRectangle.Width - width * scale) < 2 &&
                Math.Abs(_window.BoundingRectangle.Height - height * scale) < 2, "The recorder did not reach the requested size.",
                () => _window.BoundingRectangle.ToString());
            _window.Patterns.Transform.Pattern.Move(20, 20);
            _window.SetForeground();
            Thread.Sleep(300);
            CaptureLayout("recording");
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [When("the user scrolls to model setup")]
    public void ScrollToModel()
    {
        var scroll = Element("ContentScroll").Patterns.Scroll.Pattern;
        if (scroll.VerticallyScrollable.Value)
        {
            scroll.SetScrollPercent(-1, 50);
        }
    }

    [Then("the model package URL and download action fit inside the window")]
    public void ModelControlsFit()
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Assert.NotEqual(IntPtr.Zero, previous);
        try
        {
            CaptureLayout("model");
            var window = _window!.BoundingRectangle;
            foreach (var control in new AutomationElement[] { Element("ModelUrlBox"), Button("Download selected package") })
            {
                var bounds = control.BoundingRectangle;
                Assert.False(control.Properties.IsOffscreen.Value, $"{control.Name} is offscreen at {_size}.");
                Assert.False(bounds.IsEmpty, $"{control.Name} has no visible bounds.");
                Assert.True(bounds.Left >= window.Left && bounds.Right <= window.Right &&
                    bounds.Top >= window.Top && bounds.Bottom <= window.Bottom,
                    $"{control.Name} is clipped at {_size}: {bounds} outside {window}.");
            }
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    private void CaptureLayout(string section)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Assert.NotEqual(IntPtr.Zero, previous);
        try
        {
            var screenshots = Path.Combine(AppContext.BaseDirectory, "TestResults", "Layout");
            Directory.CreateDirectory(screenshots);
            _window!.CaptureToFile(Path.Combine(screenshots, $"{_size}-{section}.png"));
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    [When("the user confirms recording permission")]
    public void ConfirmPermission()
    {
        ExpandRecordingSetup();
        Element("PermissionCheckBox").AsCheckBox().IsChecked = true;
    }

    [When("the user presses {string}")]
    public void PressButton(string name)
    {
        if (name == "Import model package...")
        {
            OpenModels();
            var import = _window!.FindFirstDescendant(cf => cf.ByName("Import a previously downloaded package")) ??
                throw new InvalidOperationException("The package import section was not found.");
            import.Patterns.ExpandCollapse.Pattern.Expand();
        }
        Button(name).Invoke();
    }

    private void OpenModels()
    {
        Element("ModelsButton").AsButton().Invoke();
        WaitUntil(() => _window!.FindFirstDescendant(cf => cf.ByAutomationId("DownloadedModelSelector")) is not null,
            "Local model settings did not open.");
    }

    [Then("the downloaded model selector is empty")]
    [Then("the downloaded model selector remains empty")]
    public void DownloadedModelsEmpty()
    {
        OpenModels();
        Assert.Empty(Element("DownloadedModelSelector").AsComboBox().Items);
    }

    [Then("the downloaded model selector contains {string}")]
    public void DownloadedModelListed(string name) =>
        WaitUntil(() => Element("DownloadedModelSelector").AsComboBox().Items.Any(item => item.Text == name),
            $"The verified model '{name}' did not appear.");

    [Then("no model is selected automatically")]
    public void NoAutomaticModelSelection() =>
        Assert.Null(Element("DownloadedModelSelector").AsComboBox().SelectedItem);

    [When("the user selects downloaded model {string}")]
    public void SelectDownloadedModel(string name) =>
        Element("DownloadedModelSelector").AsComboBox().Select(name);

    [Then("the selected model is {string}")]
    [Then("the selected model remains {string}")]
    public void SelectedDownloadedModel(string name)
    {
        OpenModels();
        WaitUntil(() => Element("DownloadedModelSelector").AsComboBox().SelectedItem?.Text == name,
            $"The selected model is not '{name}'.", () =>
                $"Selected: {Element("DownloadedModelSelector").AsComboBox().SelectedItem?.Text}; " +
                $"Models: {string.Join(", ", Element("DownloadedModelSelector").AsComboBox().Items.Select(item => item.Text))}; " +
                $"Status: {Element("ModelStatusText").Name}; Message: {Element("MessageText").Name}");
    }

    [Then("the UI explains that the package was not registered")]
    public void PackageRegistrationFailed() => MessageContains("not registered");

    [When("the user opens local transcription models and the download catalog")]
    public void OpenModelCatalog()
    {
        OpenModels();
        Element("ModelCatalogExpander").Patterns.ExpandCollapse.Pattern.Expand();
    }

    [Then(@"the catalog shows (\d+) model families and (\d+) downloadable artifacts")]
    public void CatalogChoices(int families, int artifacts)
    {
        var encodings = Element("ModelCatalogList")
            .FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox).And(cf.ByName("Model encoding")));
        Assert.Equal(families, encodings.Length);
        var count = 0;
        foreach (var encoding in encodings)
        {
            var combo = encoding.AsComboBox();
            count += combo.Items.Length;
        }
        Assert.Equal(artifacts, count);
        var scroll = Element("ModelSettingsScroll").Patterns.Scroll.Pattern;
        if (scroll.VerticallyScrollable.Value)
        {
            scroll.SetScrollPercent(-1, 0);
        }
        Thread.Sleep(200);
        CaptureLayout("model-catalog");
    }

    [When("the user filters the model catalog by {string}")]
    public void FilterModelCatalog(string filter)
    {
        OpenModelCatalog();
        var downloaded = Element("DownloadedModelSelector").AsComboBox();
        _catalogDownloadedModels = downloaded.Items.Select(item => item.Text).ToArray();
        _catalogSelectedModel = downloaded.SelectedItem?.Text;
        Element("ModelCatalogFilter").AsComboBox().Select(filter);
    }

    [Then(@"the catalog shows (\d+) model families")]
    public void CatalogFamilies(int count) =>
        WaitUntil(() => Element("ModelCatalogList")
                .FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox).And(cf.ByName("Model encoding")))
                .Length == count,
            $"Expected {count} filtered model families.");

    [Then("the downloaded model selector and selected model remain unchanged")]
    public void ModelSelectionUnaffectedByFilter()
    {
        var downloaded = Element("DownloadedModelSelector").AsComboBox();
        Assert.Equal(_catalogDownloadedModels, downloaded.Items.Select(item => item.Text).ToArray());
        Assert.Equal(_catalogSelectedModel, downloaded.SelectedItem?.Text);
        NoModelNetworkRequest();
    }

    [Then("no network request has been made")]
    public void NoModelNetworkRequest() =>
        Assert.False(File.Exists(Path.Combine(_directory!.FullName, "network-requested.txt")));

    [Given("Base original is selected")]
    public void BaseModelSelected() => SelectedDownloadedModel("base original");

    private void RequestSmallModel()
    {
        OpenModelCatalog();
        Element("small").AsComboBox().Select("Q5_1");
        Button("Download small").Invoke();
    }

    [When("the user requests Small Q5_1 and the fixture transfers 1024 of 4096 bytes")]
    public void RequestControlledModel() => RequestSmallModel();

    [Then("model download progress shows 25 percent")]
    public void ModelTransferProgress()
    {
        WaitUntil(() => Element("ModelsButton").Name.Contains("25%", StringComparison.Ordinal),
            "The real 1024/4096-byte transfer estimate did not reach the header.");
        Assert.Equal(25d, Element("ModelDownloadProgress").Patterns.RangeValue.Pattern.Value.Value);
    }

    [Then("Small Q5_1 is not selectable yet")]
    [Then("the incomplete model is not selectable")]
    public void IncompleteModelNotListed() =>
        Assert.DoesNotContain(Element("DownloadedModelSelector").AsComboBox().Items, item => item.Text == "small Q5_1");

    [When("the user returns to the library")]
    public void ReturnToLibrary()
    {
        WaitUntil(() => _window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button)
            .And(cf.ByName("Back to meeting"))) is not null, "The model/job return action was not available.");
        Button("Back to meeting").Invoke();
    }

    [Then("the header still identifies Small Q5_1 and shows its download progress")]
    public void HeaderTransferProgress()
    {
        Assert.Contains("small Q5_1", Element("ModelsButton").Name);
        Assert.Contains("25%", Element("ModelsButton").Name);
        CaptureLayout("model-transfer-library");
    }

    [When("transfer and verification finish")]
    public void CompleteModelTransfer()
    {
        File.WriteAllText(Path.Combine(_directory!.FullName, "model-transfer-release.txt"), "release");
        File.WriteAllText(Path.Combine(_directory.FullName, "model-verification-release.txt"), "release");
    }

    [Then("Small Q5_1 is selectable")]
    public void VerifiedModelListed()
    {
        OpenModels();
        DownloadedModelListed("small Q5_1");
    }

    [Then("Base original remains selected")]
    public void BaseSelectionUnchanged() => SelectedDownloadedModel("base original");

    [When("the user requests a model download")]
    public void RequestUnknownLengthModel() => RequestSmallModel();

    [Then("download progress is indeterminate")]
    public void UnknownModelTransferLength()
    {
        WaitUntil(() => Element("ModelsButton").Name.Contains("Downloading", StringComparison.Ordinal) &&
            !Element("ModelsButton").Name.Contains('%'),
            "The unknown-length transfer should not invent a percentage.");
        Assert.Equal("Indeterminate", Element("ModelDownloadProgress").Properties.ItemStatus.Value);
    }

    [When("transfer finishes but verification has not completed")]
    public void ModelVerificationPending()
    {
        File.WriteAllText(Path.Combine(_directory!.FullName, "model-transfer-release.txt"), "release");
        WaitUntil(() => Element("ModelsButton").Name.Contains("Verifying", StringComparison.Ordinal),
            "The model verification phase was not reported.");
        Assert.Equal("Indeterminate", Element("ModelDownloadProgress").Properties.ItemStatus.Value);
        Element("ModelCatalogExpander").Patterns.ExpandCollapse.Pattern.Collapse();
        var scroll = Element("ModelSettingsScroll").Patterns.Scroll.Pattern;
        if (scroll.VerticallyScrollable.Value)
        {
            scroll.SetScrollPercent(-1, 0);
        }
        CaptureLayout("model-verifying");
    }

    [Then("the UI identifies verification and does not offer the model as downloaded")]
    public void UnverifiedModelUnavailable()
    {
        Assert.Contains("Verifying", Element("ModelsButton").Name);
        IncompleteModelNotListed();
    }

    [Given("capture and a model download are running")]
    public void CaptureDuringModelTransfer()
    {
        ConfirmPermission();
        PressButton("Start recording");
        RecordingStatus("Recording");
        Element("RecordingSetupExpander").Patterns.ExpandCollapse.Pattern.Collapse();
        RequestSmallModel();
        ModelTransferProgress();
    }

    [When("the user cancels the model download")]
    public void CancelModelTransfer() => Button("Cancel download").Invoke();

    [Then("only that download is cancelled")]
    public void OnlyModelTransferCancelled()
    {
        WaitUntil(() => !Element("ModelsButton").Name.Contains("Downloading", StringComparison.Ordinal),
            "The cancelled download remained active.");
        RecordingStatus("Recording");
    }

    [Then("capture and the selected model remain unchanged")]
    public void CaptureAndModelPreserved()
    {
        RecordingStatus("Recording");
        BaseSelectionUnchanged();
    }

    [When("the user selects Brazilian Portuguese for a new transcription")]
    public void SelectPortugueseForNewTranscription() => SelectLanguage("Brazilian Portuguese");

    [Then("the UI asks for a compatible multilingual downloaded model")]
    public void MultilingualModelRequired() => MessageContains("compatible multilingual downloaded model");

    [Then("the English-only model remains listed")]
    public void EnglishOnlyModelRetained()
    {
        OpenModels();
        DownloadedModelListed("ui-fixture");
    }

    [Then("recording remains available")]
    public void RecordingAvailableWithoutCompatibleModel()
    {
        ConfirmPermission();
        Assert.True(Button("Start recording").IsEnabled);
    }

    [Then("another model is not selected silently")]
    public void NoSilentModelReplacement() => SelectedDownloadedModel("ui-fixture");

    [When("the user operates recording playback using the keyboard")]
    public void KeyboardRecordingPlayback()
    {
        KeyboardButton("Play recording");
        WaitUntil(() => Element("PlaybackPositionSlider").Patterns.RangeValue.Pattern.Value.Value > 0,
            "Keyboard activation did not start real recording playback.");
        Element("PlaybackPositionSlider").Focus();
    }

    [Then("playback controls have accessible names and a readable position")]
    public void AccessiblePlaybackPosition()
    {
        Assert.Equal("Playback position", Element("PlaybackPositionSlider").Name);
        Assert.True(Element("PlaybackPositionSlider").Patterns.RangeValue.Pattern.Value.Value >= 0);
        Assert.NotNull(_window!.FindFirstDescendant(cf => cf.ByName("Pause recording playback")));
    }

    [When("a job reports progress")]
    public void ReportAccessibleJobProgress() => SendWorkerCommand("Recognizing", 37);

    [Then("the progress value and phase are exposed to automation")]
    public void AccessibleRecordingJobProgress()
    {
        var progress = _window!.FindFirstDescendant(cf => cf.ByAutomationId("RecordingJobProgress"));
        Assert.NotNull(progress);
        WaitUntil(() => Element("RecordingJobProgressText").Name == "Recognizing / 37% recognition estimate",
            "The active job's phase/estimate were not exposed while reading.");
        Assert.Equal(37d, progress.Patterns.RangeValue.Pattern.Value.Value);
    }

    [Then("keyboard focus stays on the user's control")]
    public void JobProgressPreservesKeyboardFocus() =>
        Assert.True(Element("PlaybackPositionSlider").Properties.HasKeyboardFocus.Value);

    [When("the user opens deletion for the selected entry")]
    public void OpenRetainedEntryDeletion() => OpenRecordingDeletion();

    [Then("audio-only deletion is unavailable")]
    public void AudioOnlyDeletionUnavailable()
    {
        var audio = Dialog("Delete recording artifacts?").FindFirstDescendant(cf => cf.ByAutomationId("AudioOnlyDeletionScope"));
        Assert.NotNull(audio);
        Assert.False(audio.IsEnabled);
    }

    [Then("all-artifacts deletion names the retained outputs and history")]
    public void RetainedArtifactsDeletionConsequences() => RecordingDeletionScopes();

    [When("the user confirms all-artifacts deletion")]
    public void ConfirmRetainedArtifactsDeletion()
    {
        if (!_application!.GetAllTopLevelWindows(_automation!).Any(window => window.Name == "Delete recording artifacts?"))
        {
            OpenRecordingDeletion();
        }
        var dialog = Dialog("Delete recording artifacts?");
        var all = dialog.FindFirstDescendant(cf => cf.ByAutomationId("AllArtifactsDeletionScope")) ??
            throw new InvalidOperationException("The all-artifacts scope was not found.");
        all.Patterns.SelectionItem.Pattern.Select();
        var confirm = dialog.FindFirstDescendant(cf => cf.ByAutomationId("DeleteArtifactsButton")) ??
            throw new InvalidOperationException("All-artifact confirmation was not found.");
        confirm.AsButton().Invoke();
    }

    [Then("the retained outputs and the entry are removed")]
    public void RetainedArtifactsRemoved()
    {
        MessageContains("The recording and all its generated artifacts were deleted.");
        HistoryCount(0);
        Assert.NotNull(_beforeDeletion);
        foreach (var path in _beforeDeletion.Keys.Where(path => Path.GetExtension(path) is ".txt" or ".srt"))
        {
            Assert.False(File.Exists(path));
        }
    }

    [Then("the UI reports the remaining failed artifact and a retry action")]
    public void PartialArtifactFailureReported()
    {
        MessageContains("Deletion was not completed.");
        Assert.Contains(".txt", Element("MessageText").Name);
        Assert.True(Button("Retry deletion").IsEnabled);
    }

    [Then("complete deletion is not announced")]
    public void CompleteDeletionNotAnnounced() =>
        Assert.DoesNotContain("The recording and all its generated artifacts were deleted.", Element("MessageText").Name);

    [Then("the unfinished library entry remains available")]
    public void UnfinishedLibraryEntryAvailable() => HistoryCount(1);

    [When("the user requests playback")]
    public void RequestRecordingPlayback() => Button("Play recording").Invoke();

    [Then("an actionable audio error is shown")]
    public void PlaybackAudioError() => MessageContains("The recording could not be played:");

    [Then("retained transcript review remains available")]
    public void UnusableAudioTranscriptAvailable() => TranscriptReaderContains("UI fixture transcript.");

    [Then("no alternative audio or synthetic playback source is substituted")]
    public void NoSubstitutePlaybackAudio()
    {
        Assert.Equal("Not a WAV file", File.ReadAllText(Path.Combine(_directory!.FullName, "fixture.wav")));
        Assert.True(string.IsNullOrEmpty(Button("Play recording").Properties.HelpText.Value));
        Assert.Single(_directory.GetFiles("*.wav"));
    }

    [When("the user plays the selected recording")]
    public void PlaySelectedRecording()
    {
        Button("Play recording").Invoke();
        WaitUntil(() => Element("PlaybackPositionSlider").Patterns.RangeValue.Pattern.Value.Value > 0,
            "Playback position did not begin advancing.");
        _playbackBeforeProgress = Element("PlaybackPositionSlider").Patterns.RangeValue.Pattern.Value.Value;
    }

    [When("that job reports another recognition estimate")]
    public void ReportProgressDuringPlayback()
    {
        SendWorkerCommand("Recognizing", 37);
        Thread.Sleep(350);
    }

    [Then("playback continues from its existing position")]
    public void PlaybackPositionPreserved() =>
        WaitUntil(() => Element("PlaybackPositionSlider").Patterns.RangeValue.Pattern.Value.Value > _playbackBeforeProgress,
            "An unrelated progress refresh reset or stopped playback.");

    [Then("the visible completed transcript is identified as the previous output")]
    public void PriorCompletedOutputIdentified() =>
        Assert.NotNull(_window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text)
            .And(cf.ByName("Previously completed transcript"))));

    [When("the user plays the selected recording and opens model management")]
    public void PlayAndLeaveReview()
    {
        PlaySelectedRecording();
        OpenModels();
    }

    [Then("playback is paused")]
    public void PlaybackPaused()
    {
        ReturnToLibrary();
        var position = Element("PlaybackPositionSlider").Patterns.RangeValue.Pattern.Value.Value;
        Thread.Sleep(350);
        Assert.Equal(position, Element("PlaybackPositionSlider").Patterns.RangeValue.Pattern.Value.Value, precision: 2);
    }

    [When("the user returns and selects a different recording")]
    public void SelectAnotherPlayableRecording()
    {
        var other = Element("HistoryList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
            .First(row => row.Name.Contains("Other recording", StringComparison.Ordinal));
        other.Patterns.SelectionItem.Pattern.Select();
    }

    [Then("playback uses the newly selected WAV from its beginning")]
    public void NewRecordingPlaybackStartsAtZero() =>
        Assert.Equal(0d, Element("PlaybackPositionSlider").Patterns.RangeValue.Pattern.Value.Value);

    [Then("it does not start automatically")]
    public void NewRecordingDoesNotAutoplay()
    {
        Assert.False(Button("Pause recording playback").IsEnabled);
        Button("Play recording").Invoke();
        WaitUntil(() => Button("Play recording").Properties.HelpText.Value == Path.Combine(_directory!.FullName, "other.wav"),
            "Explicit playback did not use the newly selected WAV.");
    }

    private AutomationElement[] QueuedJobRows() =>
        Element("JobsList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
            .Where(row => row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Any(text => text.Name == "Queued"))
            .ToArray();

    [Given("that queued job uses Base original and English")]
    public void QueuedBaseModelAndEnglish()
    {
        OpenJobs();
        JobContains("Queued");
        var queued = Assert.Single(QueuedJobRows());
        Assert.Contains(queued.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)), text => text.Name == "base original");
        Assert.Contains(queued.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)), text => text.Name == "English");
    }

    [When("the user selects Small Q5_1 and Brazilian Portuguese for new jobs")]
    public void ChangeNewJobChoices()
    {
        OpenModels();
        SelectDownloadedModel("small Q5_1");
        WaitUntil(() => Element("ModelsButton").Name.Contains("small Q5_1", StringComparison.Ordinal),
            "The new selected model was not persisted.");
        SelectLanguage("Brazilian Portuguese");
        Element("RecordingSetupExpander").Patterns.ExpandCollapse.Pattern.Collapse();
    }

    [Then("the queued job's details still show Base original and English")]
    public void QueuedChoicesRemainPinned() => QueuedBaseModelAndEnglish();

    [Then("only new jobs use Small Q5_1 and Brazilian Portuguese")]
    public void NewJobsUseNewChoices()
    {
        Button("Back to meeting").Invoke();
        Button("Transcribe again").Invoke();
        OpenJobs();
        JobCount(3);
        var queued = QueuedJobRows();
        Assert.Equal(2, queued.Length);
        Assert.Contains(queued, row => row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Any(text => text.Name == "small Q5_1") && row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Any(text => text.Name == "Brazilian Portuguese"));
        Assert.Contains(queued, row => row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Any(text => text.Name == "base original") && row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Any(text => text.Name == "English"));
    }

    [Given("model selection verification is held by the fixture")]
    public void HoldModelSelection() =>
        File.WriteAllText(Path.Combine(_directory!.FullName, "hold-model-selection.txt"), "hold");

    [When("the user selects Small Q5_1 while verification is pending")]
    public void SelectPendingModel()
    {
        OpenModels();
        SelectDownloadedModel("small Q5_1");
        ReturnToLibrary();
    }

    [Then("new transcription actions are unavailable until selection completes")]
    public void PendingSelectionGatesTranscription() => Assert.False(Button("Transcribe again").IsEnabled);

    [Then("recording remains available during selection")]
    public void CaptureAvailableDuringSelection() => Assert.True(Button("Start recording").IsEnabled);

    [When("the user records and saves audio during pending selection")]
    public void SaveDuringPendingSelection()
    {
        ConfirmPermission();
        PressButton("Start recording");
        RecordingStatus("Recording");
        PressButton("Stop");
        RecordingStatus("Idle");
    }

    [Then("the audio is saved without queuing against the previous model")]
    public void PendingSelectionKeepsSavedAudio()
    {
        Assert.Contains("waits for the pending model selection", Element("MessageText").Name);
        HistoryCount(2);
        Assert.Equal(2, _directory!.GetFiles("*.wav").Length);
        OpenJobs();
        JobCount(2);
    }

    [When("model selection verification is released")]
    public void ReleasePendingSelection() =>
        File.WriteAllText(Path.Combine(_directory!.FullName, "release-model-selection.txt"), "release");

    [Then("the saved recording is queued with Small Q5_1")]
    public void DeferredRecordingUsesSelectedModel()
    {
        JobCount(3);
        var container = Element("JobsList").Patterns.ItemContainer.Pattern;
        AutomationElement? item = null;
        for (var index = 0; index < 3; index++)
        {
            item = container.FindItemByProperty(item, null, null);
        }
        Assert.NotNull(item);
        if (item.Patterns.VirtualizedItem.IsSupported)
        {
            item.Patterns.VirtualizedItem.Pattern.Realize();
        }
        item.AsListBoxItem().Select();
        item.Patterns.ScrollItem.Pattern.ScrollIntoView();
        JobContains("small Q5_1");
    }

    [When("the user opens Delete recording")]
    public void OpenRecordingDeletion()
    {
        SnapshotDeletionArtifacts();
        var delete = _window!.FindFirstDescendant(cf => cf.ByAutomationId("DeleteRecordingButton"));
        Assert.NotNull(delete);
        delete.AsButton().Invoke();
    }

    private void SnapshotDeletionArtifacts() =>
        _beforeDeletion = _directory!.GetFiles("*", SearchOption.AllDirectories)
            .Where(file => file.Extension is ".wav" or ".txt" or ".srt" or ".json")
            .ToDictionary(file => file.FullName, file => File.ReadAllBytes(file.FullName));

    [When("the user opens Delete recording using the keyboard")]
    public void OpenDeletionWithKeyboard()
    {
        SnapshotDeletionArtifacts();
        KeyboardButton("Delete recording...");
    }

    [Then("both deletion scopes and their consequences have accessible labels")]
    public void AccessibleDeletionScopes() => RecordingDeletionScopes();

    [Then("Tab remains inside the confirmation dialog")]
    public void DeletionFocusIsContained()
    {
        var dialog = Dialog("Delete recording artifacts?");
        for (var index = 0; index < 8; index++)
        {
            Keyboard.Type(VirtualKeyShort.TAB);
            WaitUntil(() => dialog.FindAllDescendants()
                .Any(element => element.Properties.HasKeyboardFocus.Value),
                "Tab moved focus outside the owned deletion dialog.");
        }
    }

    [When("the user presses Escape")]
    public void KeyboardEscapeDeletion() => EscapeRecordingDeletion();

    [Then("the dialog closes without deletion")]
    public void KeyboardDeletionCancelled() => RecordingDeletionCancelled();

    [Then("focus returns to the recording's review")]
    public void KeyboardReviewFocusRestored() => RecordingReviewFocusRestored();

    [Then("the dialog offers audio-only and all-artifacts scopes and explains their consequences")]
    public void RecordingDeletionScopes()
    {
        var dialog = Dialog("Delete recording artifacts?");
        Assert.NotNull(dialog.FindFirstDescendant(cf => cf.ByAutomationId("AudioOnlyDeletionScope")));
        Assert.NotNull(dialog.FindFirstDescendant(cf => cf.ByAutomationId("AllArtifactsDeletionScope")));
        var text = string.Join(" ", dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Select(element => element.Name));
        Assert.Contains("Keep the transcript, subtitles, and library/job history", text);
        Assert.Contains("Downloaded models are kept", text);
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        Assert.NotEqual(IntPtr.Zero, previous);
        try
        {
            var screenshots = Path.Combine(AppContext.BaseDirectory, "TestResults", "Layout");
            Directory.CreateDirectory(screenshots);
            dialog.CaptureToFile(Path.Combine(screenshots, $"{_size}-delete-confirmation.png"));
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    [Then("the default scope is audio only")]
    public void DefaultRecordingDeletionScope()
    {
        var radio = Dialog("Delete recording artifacts?")
            .FindFirstDescendant(cf => cf.ByAutomationId("AudioOnlyDeletionScope")) ??
            throw new InvalidOperationException("The audio-only deletion scope was not found.");
        Assert.True(radio.Patterns.SelectionItem.Pattern.IsSelected.Value);
    }

    [When("the user dismisses the dialog with Escape")]
    public void EscapeRecordingDeletion()
    {
        var keep = Dialog("Delete recording artifacts?")
            .FindFirstDescendant(cf => cf.ByAutomationId("KeepRecordingButton")) ??
            throw new InvalidOperationException("The safe deletion-dismissal action was not found.");
        PressKey(keep, VirtualKeyShort.ESCAPE);
    }

    [Then("audio, transcript, subtitles, library entry, and job history remain unchanged")]
    public void RecordingDeletionCancelled()
    {
        Assert.NotNull(_beforeDeletion);
        foreach (var (path, bytes) in _beforeDeletion)
        {
            Assert.True(File.Exists(path));
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        HistoryCount(1);
    }

    [Then("keyboard focus returns to the recording's review")]
    public void RecordingReviewFocusRestored() =>
        WaitUntil(() => Element("DeleteRecordingButton").Properties.HasKeyboardFocus.Value,
            "Cancelling deletion did not restore focus to the recording review.");

    [When("the user confirms audio-only deletion")]
    public void ConfirmAudioDeletion()
    {
        OpenRecordingDeletion();
        var confirm = Dialog("Delete recording artifacts?")
            .FindFirstDescendant(cf => cf.ByAutomationId("DeleteArtifactsButton")) ??
            throw new InvalidOperationException("The deletion confirmation was not found.");
        confirm.AsButton().Invoke();
    }

    [Then("the library entry remains selected and explains that audio was deleted")]
    public void AudioDeletedLibraryEntry()
    {
        MessageContains("Audio deleted.");
        HistoryCount(1);
        Assert.Single(Element("HistoryList").Patterns.Selection.Pattern.Selection.Value);
        Assert.NotNull(_window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text)
            .And(cf.ByName("English / 00:00:06 / Audio deleted. Transcripts retained."))));
    }

    [Then("its transcript and subtitle outputs remain viewable")]
    public void RetainedDeletionOutputs()
    {
        WaitUntil(() => Element("TranscriptText").Patterns.Value.Pattern.Value.Value
            .Contains("UI fixture transcript.", StringComparison.Ordinal),
            "The retained transcript is not available.");
        Assert.NotNull(_beforeDeletion);
        var outputs = _beforeDeletion.Where(pair => Path.GetExtension(pair.Key) is ".txt" or ".srt").ToArray();
        Assert.NotEmpty(outputs);
        foreach (var (path, bytes) in outputs)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        Assert.False(File.Exists(Path.Combine(_directory!.FullName, "fixture.wav")));
        CaptureLayout("audio-deleted");
    }

    [Then("playback and Transcribe again are unavailable")]
    public void RemovedAudioActionsUnavailable()
    {
        Assert.Null(_window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Play recording"))));
        Assert.False(Button("Transcribe again").IsEnabled);
    }

    [Then("Open output folder remains available for retained outputs")]
    public void RevealRetainedOutputs()
    {
        Assert.True(Button("Open output folder").IsEnabled);
        Button("Open output folder").Invoke();
        var revealed = Path.Combine(_directory!.FullName, "revealed-recording-path.txt");
        WaitUntil(() => File.Exists(revealed), "The retained output could not be revealed.");
        var path = File.ReadAllText(revealed);
        Assert.Contains(path, _beforeDeletion!.Keys.Where(item => Path.GetExtension(item) is ".txt" or ".srt"));
    }

    [When("the user confirms deletion of all artifacts for the selected recording")]
    public void ConfirmAllRecordingArtifacts()
    {
        Button("Play recording").Invoke();
        OpenRecordingDeletion();
        var dialog = Dialog("Delete recording artifacts?");
        var all = dialog.FindFirstDescendant(cf => cf.ByAutomationId("AllArtifactsDeletionScope")) ??
            throw new InvalidOperationException("The all-artifacts scope was not found.");
        all.Patterns.SelectionItem.Pattern.Select();
        var confirm = dialog.FindFirstDescendant(cf => cf.ByAutomationId("DeleteArtifactsButton")) ??
            throw new InvalidOperationException("The deletion confirmation was not found.");
        confirm.AsButton().Invoke();
    }

    [Then("that recording and its job history disappear from the workspace")]
    public void AllRecordingArtifactsRemoved()
    {
        MessageContains("The recording and all its generated artifacts were deleted.");
        HistoryCount(1);
        Assert.NotNull(_beforeDeletion);
        var targetFiles = _beforeDeletion.Keys.Where(path =>
            Path.GetFileName(path).StartsWith("fixture", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.True(targetFiles.Length >= 5, "The fixture must include audio and at least two TXT/SRT pairs.");
        foreach (var path in targetFiles)
        {
            Assert.False(File.Exists(path), $"Owned artifact was retained: {path}");
        }
        Button("View transcription jobs").Invoke();
        JobCount(1);
    }

    [Then("a remaining recording is selected or the empty-library state is shown")]
    public void RemainingRecordingSelected()
    {
        Assert.Single(Element("HistoryList").Patterns.Selection.Pattern.Selection.Value);
        Assert.Contains("Other recording", Element("HistoryList").Patterns.Selection.Pattern.Selection.Value[0].Name);
    }

    [Then("downloaded models and other recordings remain unchanged")]
    public void UnrelatedDeletionArtifactsUnchanged()
    {
        Assert.NotNull(_beforeDeletion);
        var protectedFiles = _beforeDeletion.Where(pair =>
            pair.Key.StartsWith(Path.Combine(_directory!.FullName, "models") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(pair.Key).StartsWith("other", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.NotEmpty(protectedFiles);
        foreach (var (path, bytes) in protectedFiles)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        SelectedDownloadedModel("ui-fixture");
    }

    [When("the user selects audio source {string}")]
    public void SelectSource(string name)
    {
        ExpandRecordingSetup();
        Element("SourceCombo").AsComboBox().Select(name);
    }

    [Then(@"^the microphone selector is (enabled|disabled)$")]
    public void MicrophoneEnabled(string state) =>
        Assert.Equal(state == "enabled", Element("MicrophoneCombo").IsEnabled);

    [Then(@"^the playback selector is (enabled|disabled)$")]
    public void PlaybackEnabled(string state) =>
        Assert.Equal(state == "enabled", Element("PlaybackCombo").IsEnabled);

    [Then("the source explanation contains {string}")]
    public void SourceExplanation(string text) =>
        Assert.Contains(text, Element("SystemAudioScopeText").Name, StringComparison.OrdinalIgnoreCase);

    [When("the user enters {string} as the local destination")]
    public void EnterDestination(string text)
    {
        ExpandRecordingSetup();
        Element("DestinationBox").AsTextBox().Text = text;
        Element("PermissionCheckBox").Focus();
    }

    [Then("the UI message contains {string}")]
    public void MessageContains(string expected) =>
        WaitUntil(() => _window!.FindFirstDescendant(cf => cf.ByAutomationId("MessageText"))
                ?.Name.Contains(expected, StringComparison.Ordinal) == true,
            $"Expected UI message containing '{expected}'.", () =>
            {
                CaptureLayout("message-failure");
                var message = _window!.FindFirstDescendant(cf => cf.ByAutomationId("MessageText"))?.Name;
                var windows = _application!.GetAllTopLevelWindows(_automation!).Select(window => window.Name);
                return $"Message: '{message}'; windows: {string.Join(", ", windows)}";
            });

    [Then("the model status contains {string}")]
    public void ModelStatus(string expected) =>
        WaitUntil(() => Element("ModelStatusText").Name.Contains(expected, StringComparison.Ordinal),
            $"Expected model status containing '{expected}'.");

    [When("the user enters {string} as the model package URL")]
    public void EnterModelUrl(string text) => Element("ModelUrlBox").AsTextBox().Text = text;

    [When("the user opens the {string} tab")]
    public void OpenTab(string name) =>
        (_window?.FindFirstDescendant(cf => cf.ByControlType(ControlType.TabItem).And(cf.ByName(name))) ??
         throw new InvalidOperationException($"Tab '{name}' was not found."))
        .Patterns.SelectionItem.Pattern.Select();

    [When("the user selects the first recording")]
    public void SelectRecording() => SelectFirstRow("HistoryList");

    [When("the user selects the first transcription job")]
    public void SelectJob() => SelectFirstRow("JobsList");

    [When("the user opens the transcription jobs view")]
    [When("the user opens transcription jobs")]
    public void OpenJobs()
    {
        Button("View transcription jobs").Invoke();
        WaitUntil(() => _window!.FindFirstDescendant(cf => cf.ByAutomationId("JobsList")) is not null,
            "The transcription jobs view did not open.");
    }

    [When("the user opens local transcription models")]
    public void OpenModelWorkspace() => OpenModels();

    [Then("model management occupies the reader work area")]
    public void ModelsUseReaderArea() =>
        Assert.True(Element("ModelSettingsScroll").BoundingRectangle.Left >= Element("HistoryList").BoundingRectangle.Right);

    [Then("the library and capture dock remain available")]
    public void PersistentWorkspaceAreasAvailable()
    {
        Assert.False(Element("HistoryList").Properties.IsOffscreen.Value);
        Assert.False(Element("StartButton").Properties.IsOffscreen.Value);
    }

    [Then("the job view occupies the reader work area")]
    public void JobsUseReaderArea() =>
        Assert.True(Element("JobsList").BoundingRectangle.Left >= Element("HistoryList").BoundingRectangle.Right);

    [Then("queued or active jobs precede completed job history")]
    public void ActiveJobsPrecedeHistory()
    {
        JobContains("Running");
        var rows = Element("JobsList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));
        var statuses = rows.Select(row => row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Select(text => text.Name).First(name => name is "Running" or "Queued" or "Completed")).ToArray();
        Assert.Contains("Completed", statuses);
        Assert.True(statuses[0] is "Running" or "Queued", "Active work must precede completed job history.");
    }

    private Dictionary<string, string> CaptureSettings() => new()
    {
        ["Microphone"] = Element("MicrophoneCombo").AsComboBox().SelectedItem?.Text ?? string.Empty,
        ["Language"] = Element("LanguageCombo").AsComboBox().SelectedItem?.Text ?? string.Empty,
        ["Destination"] = Element("DestinationBox").AsTextBox().Text,
        ["Title"] = Element("TitleBox").AsTextBox().Text,
    };

    [Given("a microphone, language, destination, and title are configured for a new recording")]
    public void ConfigureFutureCapture()
    {
        ExpandRecordingSetup();
        Element("MicrophoneCombo").AsComboBox().Select("Fixture microphone");
        Element("LanguageCombo").AsComboBox().Select("Brazilian Portuguese");
        Element("DestinationBox").AsTextBox().Text = _directory!.FullName;
        Element("TitleBox").AsTextBox().Text = "Upcoming meeting";
        _captureSettings = CaptureSettings();
    }

    [When("the user selects another library recording")]
    public void SelectOtherLibraryRecording() => SelectAnotherPlayableRecording();

    [Then("that recording's title, captured duration, and completed transcript are shown in the reader")]
    public void SelectedLibraryMetadataDisplayed()
    {
        Assert.NotNull(_window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("Other recording"))));
        Assert.NotNull(_window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("English / 00:00:01"))));
        TranscriptReaderContains("UI fixture transcript.");
    }

    [Then("the new recording's capture setup remains unchanged")]
    public void FutureCaptureUnchanged() => Assert.Equal(_captureSettings, CaptureSettings());

    [When("the user searches the library for {string}")]
    public void SearchLibrary(string query)
    {
        _beforeJobControl = _directory!.GetFiles("*", SearchOption.AllDirectories)
            .Where(file => file.Extension is ".wav" or ".txt" or ".srt" or ".json")
            .ToDictionary(file => file.FullName, file => File.ReadAllBytes(file.FullName));
        Element("LibrarySearchBox").AsTextBox().Text = query;
    }

    [Then("only library entries matching a title or completed transcript phrase are shown")]
    public void SearchMatchesTitlesAndTranscripts()
    {
        HistoryCount(2);
        var names = Element("HistoryList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
            .Select(row => row.Name).ToArray();
        Assert.Contains(names, name => name.Contains("Design handover", StringComparison.Ordinal));
        Assert.Contains(names, name => name.Contains("Other recording", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("Budget discussion", StringComparison.Ordinal));
    }

    [Then("the query does not modify any recording or job")]
    public void SearchIsReadOnly() => EarlierJobOutputsRetained();

    [When("the user confirms recording permission and starts capture")]
    public void StartCaptureFromReader()
    {
        ConfirmPermission();
        PressButton("Start recording");
        RecordingStatus("Recording");
        Element("RecordingSetupExpander").Patterns.ExpandCollapse.Pattern.Collapse();
    }

    [When("the user scrolls the transcript reader")]
    public void ScrollTranscriptReader()
    {
        var scroll = Element("TranscriptScroll").Patterns.Scroll.Pattern;
        Assert.True(scroll.VerticallyScrollable.Value);
        scroll.SetScrollPercent(-1, 80);
    }

    [Then("recording status, captured duration, Pause, and Stop remain visible and operable")]
    public void CaptureDockRemainsOperable()
    {
        foreach (var control in new[] { Element("StatusText"), Element("DurationText"), Button("Pause"), Button("Stop") })
        {
            Assert.False(control.Properties.IsOffscreen.Value);
            Assert.True(control.IsEnabled);
        }
    }

    [Then("scrolling the reader does not pause or stop capture")]
    public void ScrollingPreservesCapture() => RecordingStatus("Recording");

    [When("the user cancels that transcription job")]
    public void CancelSelectedJob() => Button("Cancel selected job").Invoke();

    [Then("Pause is unavailable and its limitation is explained")]
    public void JobPauseUnavailable()
    {
        Assert.NotNull(_window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text)
            .And(cf.ByName("Pause is unavailable. Stop/Cancel keep completed outputs; Delete removes only job history."))));
        Assert.Null(_window.FindFirstDescendant(cf => cf.ByName("Pause transcription")));
    }

    private AutomationElement RunningJobRow()
    {
        AutomationElement? row = null;
        WaitUntil(() =>
        {
            row = Element("JobsList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)
                .Or(cf.ByControlType(ControlType.DataItem))).FirstOrDefault(item =>
                    item.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Any(text => text.Name == "Running"));
            return row is not null;
        }, "A running job row was not available.");
        return row ?? throw new InvalidOperationException("The running job row disappeared.");
    }

    [When("the user stops the running job")]
    public void StopRunningJob()
    {
        _beforeJobControl = _directory!.GetFiles().Where(file => file.Extension is ".txt" or ".srt")
            .ToDictionary(file => file.FullName, file => File.ReadAllBytes(file.FullName));
        RunningJobRow().Patterns.SelectionItem.Pattern.Select();
        var stop = _window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Stop selected job")));
        Assert.NotNull(stop);
        stop.AsButton().Invoke();
    }

    [Then("the job shows Stopping until its worker is released")]
    public void JobStopping()
    {
        JobContains("Stopping");
        Assert.True(File.Exists(Path.Combine(_directory!.FullName, "worker-cancel-pending.txt")));
        CaptureLayout("job-stopping");
    }

    [Then("Restart and Delete job are unavailable while release is pending")]
    public void PendingJobActionsDisabled()
    {
        Assert.False(Button("Restart selected job").IsEnabled);
        Assert.False(Button("Delete job history...").IsEnabled);
    }

    [When("the worker is released")]
    public void ReleaseFixtureWorker() =>
        File.WriteAllText(Path.Combine(_directory!.FullName, "worker-release.txt"), "release");

    [Then("the job shows Stopped")]
    public void JobStopped() => JobContains("Stopped");

    [Then("restarting creates a new attempt from the beginning")]
    public void RestartStoppedJob()
    {
        var before = Element("JobsList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Length;
        WaitUntil(() => Button("Restart selected job").IsEnabled, "Restart was not enabled after worker release.");
        Button("Restart selected job").Invoke();
        JobCount(before + 1);
        JobContains("Running");
    }

    [Then("previous completed outputs remain available")]
    public void EarlierJobOutputsRetained()
    {
        Assert.NotNull(_beforeJobControl);
        Assert.NotEmpty(_beforeJobControl);
        foreach (var (path, bytes) in _beforeJobControl)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    private void SendWorkerCommand(string action, int? percent = null)
    {
        var temporary = Path.Combine(_directory!.FullName, "worker-command.pending");
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(new { Action = action, Percent = percent }));
        File.Move(temporary, Path.Combine(_directory.FullName, "worker-command.json"));
    }

    private AutomationElement SelectedJobControl(string id)
    {
        AutomationElement? control = null;
        WaitUntil(() =>
        {
            var selected = Assert.Single(Element("JobsList").Patterns.Selection.Pattern.Selection.Value);
            control = selected.FindFirstDescendant(cf => cf.ByAutomationId(id));
            return control is not null;
        }, $"Selected job control '{id}' did not become available.");
        return control!;
    }

    [When(@"the worker reports recognition percentages (\d+), (\d+), and (\d+)")]
    public void ReportRecognitionEstimates(int first, int second, int third)
    {
        RunningJobRow().Patterns.SelectionItem.Pattern.Select();
        var progress = _window!.FindFirstDescendant(cf => cf.ByAutomationId("JobProgressBar"));
        Assert.NotNull(progress);
        foreach (var percent in new[] { first, second, third })
        {
            SendWorkerCommand("Recognizing", percent);
            WaitUntil(() => SelectedJobControl("JobProgressText").Name == $"Recognizing / {percent}% recognition estimate",
                "Recognition progress did not reach the selected job.");
            _observedRecognitionEstimates.Add((int)SelectedJobControl("JobProgressBar").Patterns.RangeValue.Pattern.Value.Value);
        }
        var gauge = SelectedJobControl("JobProgressBar");
        var viewport = Element("JobsList").BoundingRectangle;
        Assert.False(gauge.Properties.IsOffscreen.Value);
        Assert.True(gauge.BoundingRectangle.Top >= viewport.Top && gauge.BoundingRectangle.Bottom <= viewport.Bottom,
            "Recognition progress must actually be visible within the job list.");
        CaptureLayout("job-recognizing");
    }

    [Then("the job view shows those recognition estimates for that job")]
    public void RecognitionEstimatesShown() => Assert.Equal(new[] { 0, 37, 84 }, _observedRecognitionEstimates);

    [Then("no elapsed-time estimate is substituted")]
    public void NoElapsedJobEstimate() =>
        Assert.Equal("Recognizing / 84% recognition estimate", SelectedJobControl("JobProgressText").Name);

    [When("recognition ends and output writing starts")]
    public void FinishFixtureRecognition()
    {
        SendWorkerCommand("Complete");
        WaitUntil(() => SelectedJobControl("JobProgressText").Name == "Writing / progress unavailable",
            "The job did not enter output writing.");
    }

    [Then("the job shows indeterminate writing progress rather than Completed")]
    public void IndeterminateWriting()
    {
        Assert.Equal("Indeterminate", SelectedJobControl("JobProgressBar").Properties.ItemStatus.Value);
        var selected = Assert.Single(Element("JobsList").Patterns.Selection.Pattern.Selection.Value);
        Assert.Contains(selected.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)), text => text.Name == "Running");
        CaptureLayout("job-writing");
    }

    [When("both final output files are published")]
    public void PublishFixtureOutputs() =>
        File.WriteAllText(Path.Combine(_directory!.FullName, "worker-publish.txt"), "publish");

    [Then("the job shows Completed")]
    public void SelectedJobCompleted()
    {
        WaitUntil(() => Assert.Single(Element("JobsList").Patterns.Selection.Pattern.Selection.Value)
            .FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Any(text => text.Name == "Completed"),
            "The selected job did not complete after output publication.");
    }

    [When("the user opens the transcription jobs view and deletes a completed job")]
    public void DeleteCompletedJobHistory()
    {
        _beforeJobControl = _directory!.GetFiles().Where(file => file.Extension is ".wav" or ".txt" or ".srt")
            .ToDictionary(file => file.FullName, file => File.ReadAllBytes(file.FullName));
        OpenJobs();
        SelectJob();
        Button("Delete job history...").Invoke();
    }

    [Then("the confirmation says audio and completed outputs will be kept")]
    public void JobHistoryDeletionConsequences() =>
        DialogContains("Delete transcription job history?", "Audio and completed outputs will be kept");

    [When("the user confirms job-history deletion")]
    public void ConfirmJobHistoryDeletion() =>
        ChooseDialogButton("Yes", "Delete transcription job history?");

    [Then("only that job-history entry disappears")]
    public void JobHistoryRemoved()
    {
        MessageContains("Job history deleted.");
        JobCount(0);
        EarlierJobOutputsRetained();
    }

    [Then("the recording and its completed transcript remain available in the library")]
    public void RecordingRetainedAfterJobDeletion()
    {
        Button("Back to meeting").Invoke();
        HistoryCount(1);
        TranscriptReaderContains("UI fixture transcript.");
    }

    [When("the job enters {string} without a percentage")]
    public void WorkerPhaseWithoutEstimate(string phase)
    {
        RunningJobRow().Patterns.SelectionItem.Pattern.Select();
        SendWorkerCommand(phase == "Writing" ? "Complete" : phase);
        WaitUntil(() => SelectedJobControl("JobProgressText").Name == $"{phase} / progress unavailable",
            $"The job did not identify phase '{phase}' without an estimate.");
    }

    [Then("job progress is indeterminate and the current phase is identified")]
    public void ProgressWithoutEstimate() =>
        Assert.Equal("Indeterminate", SelectedJobControl("JobProgressBar").Properties.ItemStatus.Value);

    [Then("no fabricated percentage or completion is shown")]
    public void NoFabricatedJobProgress()
    {
        Assert.DoesNotContain("%", SelectedJobControl("JobProgressText").Name);
        var row = Assert.Single(Element("JobsList").Patterns.Selection.Pattern.Selection.Value);
        Assert.DoesNotContain(row.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)), text => text.Name == "Completed");
    }

    [When("the user views the completed transcript and requests Copy")]
    public void CopyCompletedTranscript()
    {
        _beforeJobControl = _directory!.GetFiles().Where(file => file.Extension is ".wav" or ".txt" or ".srt")
            .ToDictionary(file => file.FullName, file => File.ReadAllBytes(file.FullName));
        Button("Copy transcript").Invoke();
    }

    [Then("the clipboard adapter receives {string}")]
    public void ClipboardReceivesTranscript(string text)
    {
        var clipboard = Path.Combine(_directory!.FullName, "clipboard-transcript.txt");
        WaitUntil(() => File.Exists(clipboard), "The explicit Copy action did not reach the clipboard adapter.");
        var actual = File.ReadAllText(clipboard);
        Assert.Contains(text, actual);
        var source = Assert.Single(_beforeJobControl!, pair => Path.GetExtension(pair.Key) == ".txt");
        Assert.Equal(System.Text.Encoding.UTF8.GetString(source.Value).TrimStart('\uFEFF'), actual);
    }

    [Then("copying does not change recording or job state")]
    public void CopyPreservesJobState()
    {
        EarlierJobOutputsRetained();
        HistoryCount(1);
        RecordingStatus("Idle");
        OpenJobs();
        JobCount(1);
        JobContains("Completed");
    }

    [Then("the library explains that no recordings are available")]
    public void EmptyLibraryExplained() => Assert.Equal("No recordings yet.", Element("LibraryMessageText").Name);

    [Then("playback, transcript viewing, and job actions require an existing entry")]
    public void EmptySelectionActionsUnavailable()
    {
        Assert.Null(_window!.FindFirstDescendant(cf => cf.ByName("Play recording")));
        Assert.False(Button("Copy transcript").IsEnabled);
        Assert.False(Button("Transcribe again").IsEnabled);
        OpenJobs();
        Assert.False(Button("View transcript").IsEnabled);
        Assert.False(Button("Cancel selected job").IsEnabled);
        Assert.False(Button("Stop selected job").IsEnabled);
        Assert.False(Button("Restart selected job").IsEnabled);
        Assert.False(Button("Delete job history...").IsEnabled);
    }

    [Then("the UI explains that no transcription jobs exist")]
    public void EmptyJobsExplained() =>
        Assert.NotNull(_window!.FindFirstDescendant(cf => cf.ByControlType(ControlType.Text).And(cf.ByName("No transcription jobs yet."))));

    [Then("recording setup remains available")]
    public void SetupRemainsAvailable()
    {
        Assert.True(Button("Start recording").IsEnabled);
        ExpandRecordingSetup();
        PermissionVisible();
    }

    [Then("the downloaded model selector and catalog download actions fit inside the client area")]
    public void CatalogActionsFit()
    {
        var bounds = _window!.BoundingRectangle;
        var selector = Element("DownloadedModelSelector");
        Assert.True(selector.BoundingRectangle.Left >= bounds.Left && selector.BoundingRectangle.Right <= bounds.Right);
        var downloads = Element("ModelCatalogList").FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .Where(button => button.Name.StartsWith("Download ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(12, downloads.Length);
        foreach (var button in downloads)
        {
            button.Focus();
            WaitUntil(() => !button.Properties.IsOffscreen.Value, "A catalog action could not be brought into view.");
            Assert.True(button.BoundingRectangle.Left >= bounds.Left && button.BoundingRectangle.Right <= bounds.Right);
        }
    }

    [Then("capture remains idle until recording permission is confirmed")]
    public void ModelBrowsingDoesNotCapture()
    {
        RecordingStatus("Idle");
        Assert.Empty(_directory!.GetFiles("*.wav"));
    }

    [Then("the transcript reader contains {string}")]
    public void TranscriptReaderContains(string text) =>
        WaitUntil(() => _window!.FindFirstDescendant(cf => cf.ByAutomationId("TranscriptText")) is { } reader &&
            reader.Patterns.Value.Pattern.Value.Value.Contains(text, StringComparison.Ordinal),
            "The completed transcript is not in the reader.");

    [When("the user selects spoken language {string}")]
    public void SelectLanguage(string name)
    {
        ExpandRecordingSetup();
        Element("LanguageCombo").AsComboBox().Select(name);
    }

    [Then("Start recording and Resume are not offered during active capture")]
    public void ActiveActions()
    {
        Assert.False(Button("Pause").Properties.IsOffscreen.Value);
        Assert.Null(_window!.FindFirstDescendant(cf => cf.ByAutomationId("StartButton")));
        Assert.Null(_window.FindFirstDescendant(cf => cf.ByAutomationId("ResumeButton")));
    }

    [Then("Pause and Start recording are not offered during paused capture")]
    public void PausedActions()
    {
        Assert.False(Button("Resume").Properties.IsOffscreen.Value);
        Assert.Null(_window!.FindFirstDescendant(cf => cf.ByAutomationId("PauseButton")));
        Assert.Null(_window.FindFirstDescendant(cf => cf.ByAutomationId("StartButton")));
    }

    [Then("recording cannot begin again without fresh permission confirmation")]
    public void FreshPermissionRequired() => PermissionUnchecked();

    [Then(@"the transcription job list has (\d+) rows?")]
    public void JobCount(int count) =>
        WaitUntil(() => CountJobItems() == count,
            $"Expected {count} transcription job rows.", () =>
                $"Actual {CountJobItems()}; " +
                $"{Element("ModelsButton").Name}; {Element("MessageText").Name}");

    private int CountJobItems()
    {
        var container = Element("JobsList").Patterns.ItemContainer.Pattern;
        AutomationElement? item = null;
        var count = 0;
        while ((item = container.FindItemByProperty(item, null, null)) is not null)
        {
            count++;
        }
        return count;
    }

    [Then("the job list contains status {string}")]
    [Then("the job list contains language {string}")]
    public void JobContains(string value) =>
        WaitUntil(() => Element("JobsList").FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Any(cell => cell.Name == value), $"Expected job list cell '{value}'.");

    [Then("the {string} dialog contains {string}")]
    public void DialogContains(string title, string text) =>
        WaitUntil(() => Dialog(title).FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Any(item => item.Name.Contains(text, StringComparison.Ordinal)),
            $"Expected '{title}' dialog containing '{text}'.");

    [When("the user chooses {string} in the {string} dialog")]
    public void ChooseDialogButton(string name, string title) =>
        (Dialog(title).FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name))) ??
         throw new InvalidOperationException($"Dialog button '{name}' was not found."))
        .AsButton().Invoke();

    [Then("these input controls have accessible names")]
    public void AccessibleNames(DataTable controls)
    {
        foreach (var row in controls.Rows)
        {
            var control = Element(row["automation id"]);
            Assert.Equal(row["accessible name"], control.Name);
            Assert.True(control.Properties.IsKeyboardFocusable.Value);
        }
    }

    [Then("recording status and messages are assertive live regions")]
    public void AssertiveLiveRegions()
    {
        Assert.Equal("Assertive", Element("StatusText").Properties.LiveSetting.Value.ToString());
        Assert.Equal("Assertive", Element("MessageText").Properties.LiveSetting.Value.ToString());
    }

    [When("the user tabs from the audio source selector")]
    public void TabFromSource()
    {
        ExpandRecordingSetup();
        PressKey(Element("SourceCombo"), VirtualKeyShort.TAB);
    }

    [Then("the microphone selector has keyboard focus")]
    public void MicrophoneFocused() =>
        WaitUntil(() => Element("MicrophoneCombo").Properties.HasKeyboardFocus.Value,
            "Tab did not move keyboard focus to the microphone selector.", () =>
                string.Join(", ", _window!.FindAllDescendants().Where(control => control.Properties.HasKeyboardFocus.Value)
                    .Select(control => $"{control.Properties.AutomationId.Value}: {control.Name}")));

    [When("the user confirms recording permission using the keyboard")]
    public void KeyboardPermission() => PressKey(Element("PermissionCheckBox"), VirtualKeyShort.SPACE);

    [When("the user activates {string} using the keyboard")]
    public void KeyboardButton(string name) => PressKey(Button(name), VirtualKeyShort.RETURN);

    private void PressKey(AutomationElement control, VirtualKeyShort key)
    {
        control.Focus();
        WaitUntil(() => control.Properties.HasKeyboardFocus.Value, "The target did not receive keyboard focus.");
        Keyboard.Type(key);
    }

    [When("the user minimizes and restores the recorder window")]
    public void MinimizeAndRestore()
    {
        var pattern = _window!.Patterns.Window.Pattern;
        pattern.SetWindowVisualState(WindowVisualState.Minimized);
        WaitUntil(() => pattern.WindowVisualState.Value == WindowVisualState.Minimized,
            "The recorder window did not minimize.");
        ChooseDialogButton("Restore recorder", "Fixture notification area");
        WaitUntil(() => pattern.WindowVisualState.Value == WindowVisualState.Normal,
            "The recorder window did not restore.");
        WaitUntil(() => _window.FindFirstDescendant(cf => cf.ByAutomationId("StatusText")) is not null,
            "The restored recorder UI did not reappear.");
    }

    [When("the user closes the recorder window")]
    public void CloseWindow() => _window!.Patterns.Window.Pattern.Close();

    [Then("the isolated recorder process exits")]
    public void ProcessExits()
    {
        var process = _process ?? throw new InvalidOperationException("The owned UI process is unavailable.");
        Assert.True(process.WaitForExit(10_000), "The isolated recorder did not exit after explicit confirmation.");
        Assert.Equal(0, process.ExitCode);
    }

    private void SelectFirstRow(string list)
    {
        WaitUntil(() => Element(list).FindFirstChild(cf => cf.ByControlType(ControlType.DataItem)
            .Or(cf.ByControlType(ControlType.ListItem))) is not null,
            $"No row was available in '{list}'.");
        var row = Element(list).FindFirstChild(cf => cf.ByControlType(ControlType.DataItem)
            .Or(cf.ByControlType(ControlType.ListItem))) ??
            throw new InvalidOperationException($"The row in '{list}' disappeared before selection.");
        row.Patterns.SelectionItem.Pattern.Select();
    }

    private Window Dialog(string title)
    {
        Window? dialog = null;
        WaitUntil(() =>
        {
            dialog = _window?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Window).And(cf.ByName(title)))
                ?.AsWindow() ?? _application?.GetAllTopLevelWindows(_automation!)
                .FirstOrDefault(window => window.Name == title);
            return dialog is not null;
        }, $"Dialog '{title}' did not appear.", () =>
            string.Join(", ", _application!.GetAllTopLevelWindows(_automation!).Select(window => window.Name)));
        return dialog!;
    }

    [Then(@"the recording history has (\d+) rows?")]
    public void HistoryCount(int count) =>
        WaitUntil(() => Element("HistoryList").FindAllChildren(cf => cf.ByControlType(ControlType.DataItem)
            .Or(cf.ByControlType(ControlType.ListItem))).Length == count,
            $"Expected {count} recording history rows.");

    [Then("the paused captured duration does not advance")]
    public void PausedDuration()
    {
        Thread.Sleep(300);
        var duration = Element("DurationText").Name;
        Thread.Sleep(1_100);
        Assert.Equal(duration, Element("DurationText").Name);
    }

    private AutomationElement Element(string id)
    {
        AutomationElement? element = null;
        WaitUntil(() =>
        {
            element = _window?.FindFirstDescendant(cf => cf.ByAutomationId(id));
            return element is not null;
        }, $"UI element '{id}' did not become available.");
        return element!;
    }

    private Button Button(string name) =>
        _window?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name)))?.AsButton() ??
        throw new InvalidOperationException($"Button '{name}' was not found.");

    private void WaitUntil(Func<bool> condition, string failure, Func<string>? diagnostics = null)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            if (_process?.HasExited == true)
            {
                Assert.Fail($"UI test host exited with code {_process.ExitCode}. {failure} {_hostErrors?.GetAwaiter().GetResult()}");
            }
            if (condition())
            {
                return;
            }
            if (timer.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail(diagnostics is null ? failure : $"{failure} Diagnostics: {diagnostics()}");
            }
            Thread.Sleep(50);
        }
    }

    [AfterScenario]
    public void CloseRecorder()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                if (!_process.WaitForExit(10_000))
                {
                    throw new TimeoutException("The owned UI test host did not exit.");
                }
            }
        }
        finally
        {
            _automation?.Dispose();
            _application?.Dispose();
            _process?.Dispose();
            _directory?.Delete(recursive: true);
        }
    }
}
