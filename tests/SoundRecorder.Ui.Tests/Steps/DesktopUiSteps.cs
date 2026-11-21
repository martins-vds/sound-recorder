using System.Diagnostics;
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
    private UIA3Automation? _automation;
    private Application? _application;
    private Window? _window;
    private DirectoryInfo? _directory;

    [Given("the isolated recorder UI is open with {string} fixtures")]
    public void OpenRecorder(string fixtures)
    {
        _directory = Directory.CreateTempSubdirectory("sound-recorder-ui-");
        var executable = Path.Combine(AppContext.BaseDirectory, "UiHost", "SoundRecorder.Ui.TestHost.exe");
        _process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            ArgumentList = { _directory.FullName, fixtures },
        }) ?? throw new InvalidOperationException("The UI test host did not start.");
        _automation = new UIA3Automation();
        _application = Application.Attach(_process.Id);
        WaitUntil(() =>
        {
            _window = _application.GetMainWindow(_automation, TimeSpan.FromMilliseconds(100));
            return _window is not null && _window.FindFirstDescendant(cf => cf.ByAutomationId("SourceCombo")) is not null;
        }, "The isolated recorder window did not appear.");
        WaitUntil(() => Element("SourceCombo").AsComboBox().SelectedItem is not null,
            "The UI did not finish loading local state.");
    }

    [Then("the recording status reads {string}")]
    public void RecordingStatus(string expected) =>
        WaitUntil(() => Element("StatusText").Name == expected, $"Expected recording status '{expected}'.");

    [Then("the captured duration reads {string}")]
    public void CapturedDuration(string expected) =>
        Assert.Equal(expected, Element("DurationText").Name);

    [Then("recording permission is unchecked")]
    public void PermissionUnchecked() =>
        Assert.False(Element("PermissionCheckBox").AsCheckBox().IsChecked);

    [Then("the {string} button is disabled")]
    public void ButtonDisabled(string name) =>
        WaitUntil(() => !Button(name).IsEnabled, $"Expected '{name}' to be disabled.");

    [Then("the {string} button is enabled")]
    public void ButtonEnabled(string name) =>
        WaitUntil(() => Button(name).IsEnabled, $"Expected '{name}' to be enabled.");

    [When("the user confirms recording permission")]
    public void ConfirmPermission() =>
        Element("PermissionCheckBox").AsCheckBox().IsChecked = true;

    [When("the user presses {string}")]
    public void PressButton(string name) => Button(name).Invoke();

    [When("the user selects audio source {string}")]
    public void SelectSource(string name) => Element("SourceCombo").AsComboBox().Select(name);

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
        Element("DestinationBox").AsTextBox().Text = text;
        Element("PermissionCheckBox").Focus();
    }

    [Then("the UI message contains {string}")]
    public void MessageContains(string expected) =>
        WaitUntil(() => Element("MessageText").Name.Contains(expected, StringComparison.Ordinal),
            $"Expected UI message containing '{expected}'.");

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

    [When("the user selects spoken language {string}")]
    public void SelectLanguage(string name) => Element("LanguageCombo").AsComboBox().Select(name);

    [Then(@"the transcription job list has (\d+) rows?")]
    public void JobCount(int count) =>
        WaitUntil(() => Element("JobsList").FindAllChildren(cf => cf.ByControlType(ControlType.DataItem)).Length == count,
            $"Expected {count} transcription job rows.");

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
    public void TabFromSource() => PressKey(Element("SourceCombo"), VirtualKeyShort.TAB);

    [Then("the microphone selector has keyboard focus")]
    public void MicrophoneFocused() =>
        WaitUntil(() => Element("MicrophoneCombo").Properties.HasKeyboardFocus.Value,
            "Tab did not move keyboard focus to the microphone selector.");

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
    public void ProcessExits() =>
        WaitUntil(() => _process!.HasExited, "The isolated recorder did not exit after explicit confirmation.");

    private void SelectFirstRow(string list)
    {
        WaitUntil(() => Element(list).FindFirstChild(cf => cf.ByControlType(ControlType.DataItem)) is not null,
            $"No row was available in '{list}'.");
        var row = Element(list).FindFirstChild(cf => cf.ByControlType(ControlType.DataItem)) ??
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
        WaitUntil(() => Element("HistoryList").FindAllChildren(cf => cf.ByControlType(ControlType.DataItem)).Length == count,
            $"Expected {count} recording history rows.");

    [Then("the paused captured duration does not advance")]
    public void PausedDuration()
    {
        Thread.Sleep(300);
        var duration = Element("DurationText").Name;
        Thread.Sleep(1_100);
        Assert.Equal(duration, Element("DurationText").Name);
    }

    private AutomationElement Element(string id) =>
        _window?.FindFirstDescendant(cf => cf.ByAutomationId(id)) ??
        throw new InvalidOperationException($"UI element '{id}' was not found.");

    private Button Button(string name) =>
        _window?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName(name)))?.AsButton() ??
        throw new InvalidOperationException($"Button '{name}' was not found.");

    private void WaitUntil(Func<bool> condition, string failure, Func<string>? diagnostics = null)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (_process?.HasExited == true)
            {
                Assert.Fail($"UI test host exited with code {_process.ExitCode}. {failure}");
            }
            if (timer.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail(diagnostics is null ? failure : $"{failure} Owned windows: {diagnostics()}");
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
