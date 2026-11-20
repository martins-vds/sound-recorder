using CommunityToolkit.Mvvm.Input;
using SoundRecorder.Desktop;

namespace SoundRecorder.Core.Tests;

public sealed class MainViewModelTests
{
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
}
