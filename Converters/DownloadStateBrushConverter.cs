using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using BigFileDownloader.Models;

namespace BigFileDownloader.Converters;

public sealed class DownloadStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is DownloadState state
            ? state switch
            {
                DownloadState.Completed => new SolidColorBrush(Color.FromRgb(37, 132, 93)),
                DownloadState.Failed => new SolidColorBrush(Color.FromRgb(196, 61, 75)),
                DownloadState.DeletionFailed => new SolidColorBrush(Color.FromRgb(196, 61, 75)),
                DownloadState.Paused => new SolidColorBrush(Color.FromRgb(181, 103, 0)),
                DownloadState.Pausing => new SolidColorBrush(Color.FromRgb(181, 103, 0)),
                DownloadState.Downloading => new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                DownloadState.Merging => new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                DownloadState.Deleting => new SolidColorBrush(Color.FromRgb(181, 103, 0)),
                DownloadState.Inspecting => new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                _ => new SolidColorBrush(Color.FromRgb(104, 113, 125))
            }
            : Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
