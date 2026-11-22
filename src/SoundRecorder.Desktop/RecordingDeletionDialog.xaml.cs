using System.Windows;

namespace SoundRecorder.Desktop;

public partial class RecordingDeletionDialog : Window
{
    public RecordingDeletionDialog(string recordingTitle, bool audioDeleted)
    {
        InitializeComponent();
        RecordingTitleText.Text = recordingTitle;
        AudioOnlyDeletionScope.IsEnabled = !audioDeleted;
        AudioOnlyDeletionScope.IsChecked = !audioDeleted;
        AllArtifactsDeletionScope.IsChecked = audioDeleted;
        Loaded += (_, _) =>
        {
            if (audioDeleted)
            {
                AllArtifactsDeletionScope.Focus();
            }
            else
            {
                AudioOnlyDeletionScope.Focus();
            }
        };
    }

    public bool DeleteAllArtifacts => AllArtifactsDeletionScope.IsChecked == true;

    private void ScopeChanged(object sender, RoutedEventArgs args) =>
        DeleteArtifactsButton.Content = DeleteAllArtifacts ? "Delete all artifacts" : "Delete audio only";

    private void ConfirmDeletion(object sender, RoutedEventArgs args) => DialogResult = true;
}
