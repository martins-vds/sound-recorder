using System.Windows;

namespace SoundRecorder.Desktop;

/// <summary>Explicit user choices that may display native modal dialogs.</summary>
public interface IDesktopDialogs
{
    string? ChooseDestination();

    string? ChooseModelPackage();

    MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage image = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None);
}
