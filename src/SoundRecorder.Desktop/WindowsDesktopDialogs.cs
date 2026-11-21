using System.Windows;

namespace SoundRecorder.Desktop;

/// <summary>Windows dialogs used only in response to an explicit application action.</summary>
public sealed class WindowsDesktopDialogs : IDesktopDialogs
{
    private readonly Func<Microsoft.Win32.CommonDialog, bool?> _showDialog;
    private readonly Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult, MessageBoxResult> _showMessage;

    public WindowsDesktopDialogs()
        : this(ShowDialog, System.Windows.MessageBox.Show)
    {
    }

    /// <summary>Configures real common dialogs without requiring a native modal loop from its caller.</summary>
    public WindowsDesktopDialogs(
        Func<Microsoft.Win32.CommonDialog, bool?> showDialog,
        Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult, MessageBoxResult> showMessage)
    {
        _showDialog = showDialog;
        _showMessage = showMessage;
    }

    private static bool? ShowDialog(Microsoft.Win32.CommonDialog dialog) => dialog.ShowDialog();

    public string? ChooseDestination()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog();
        return _showDialog(dialog) == true ? dialog.FolderName : null;
    }

    public string? ChooseModelPackage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Sound Recorder model package (*.zip)|*.zip",
        };
        return _showDialog(dialog) == true ? dialog.FileName : null;
    }

    public MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        _showMessage(message, title, buttons, image, defaultResult);
}
