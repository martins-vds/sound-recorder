using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using SoundRecorder.Core;

namespace SoundRecorder.Desktop;

public sealed class RecordingFileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || value == DependencyProperty.UnsetValue)
        {
            return string.Empty;
        }
        return FormatRecordingName(value);
    }

    private static string FormatRecordingName(object value) => value switch
        {
            SavedRecording recording => recording.Title ?? Path.GetFileNameWithoutExtension(recording.AudioPath),
            string path => Path.GetFileNameWithoutExtension(path),
            _ => throw new ArgumentException("A recording filename requires a recording or path.", nameof(value)),
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Recording names are read-only.");
}
