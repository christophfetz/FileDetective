using System.ComponentModel;
using System.Windows.Media;
using FileDetective.App.Interop;
using FileDetective.Core;

namespace FileDetective.App.ViewModels;

/// <summary>A day header of the history grid.</summary>
internal sealed class HistoryHeaderItem(string text)
{
    public string Text { get; } = text;
}

/// <summary>A row of previews of the history grid.</summary>
internal sealed class HistoryTileRowItem(IReadOnlyList<HistoryTileItem> tiles)
{
    public IReadOnlyList<HistoryTileItem> Tiles { get; } = tiles;
}

/// <summary>
/// One preview tile. It lives as long as its image is in the list, but holds the preview only while its row is on
/// screen (<see cref="IsRealized"/>), so thousands of images do not hold thousands of bitmaps.
/// </summary>
internal sealed class HistoryTileItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private bool _selected;

    public HistoryTileItem(HistoryImage image)
    {
        Image = image;
        Icon = FileIcons.For(image.Name, false);
        TimeText = image.LastWriteUtc.ToLocalTime().ToString("t");
        ToolTipText = image.Name + Environment.NewLine + image.LastWriteUtc.ToLocalTime().ToString("g");
        CloudText = image.IsCloud ? Strings.Get("Hist_CloudOnly") : string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The image; replaced when the directory reports a change (new text, new time).</summary>
    public HistoryImage Image { get; set; }

    public string TimeText { get; }

    public string ToolTipText { get; }

    /// <summary>"Only in the cloud" for the placeholder of a cloud file, else empty.</summary>
    public string CloudText { get; }

    /// <summary>The file type icon, shown while there is no preview and as the placeholder of a cloud file.</summary>
    public ImageSource? Icon { get; }

    /// <summary>The row is on screen: a preview may be loaded and kept. Read on the loading thread.</summary>
    public volatile bool IsRealized;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!ReferenceEquals(_thumbnail, value))
            {
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}
