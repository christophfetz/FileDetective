using System.Windows;
using System.Windows.Controls;
using FileDetective.Core;

namespace FileDetective.App;

/// <summary>
/// Scrolling area of the screenshot history. The rows (day headers and rows of previews) have fixed heights
/// (<see cref="HistoryRowMap"/>), so only the rows in view and one more above and below exist as elements; the others are
/// not created at all, which keeps 5,000 and more images smooth. Each row is a <see cref="ContentControl"/> whose content
/// is an item that an implicit data template of the window turns into the header or the previews. Rows that scroll out
/// go to a pool and are filled again.
/// </summary>
internal sealed class HistoryGridView : ScrollViewer
{
    /// <summary>Rows kept above and below the view.</summary>
    private const int Buffer = 1;

    private readonly Canvas _canvas = new();
    private readonly Dictionary<int, ContentControl> _realized = [];
    private readonly Stack<ContentControl> _pool = new();
    private HistoryRowMap _map = new([], 1, 1);
    private Func<HistoryRow, object>? _content;

    public HistoryGridView()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Focusable = false;
        Content = _canvas;
        ScrollChanged += (_, _) => Refresh();
        SizeChanged += (_, _) => Refresh();
    }

    /// <summary>A row got its element (it came into view or the layout was set again).</summary>
    public event Action<HistoryRow>? RowRealized;

    /// <summary>A row lost its element (it scrolled out of view or the layout was replaced).</summary>
    public event Action<HistoryRow>? RowReleased;

    public HistoryRowMap Map => _map;

    /// <summary>The height of the view; what can be shown at once.</summary>
    public double ViewHeight => ViewportHeight > 0 ? ViewportHeight : ActualHeight;

    /// <summary>
    /// Shows a new list of rows. The scroll position is <paramref name="offset"/> (the caller worked it out from an anchor
    /// of the old layout, so the user keeps his place). All rows that were on screen are released and the rows in view
    /// are created again.
    /// </summary>
    public void SetLayout(HistoryRowMap map, Func<HistoryRow, object> content, double offset)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(content);
        ReleaseAll();
        _map = map;
        _content = content;
        _canvas.Height = Math.Max(0, map.TotalHeight);
        ScrollToVerticalOffset(Math.Clamp(offset, 0, Math.Max(0, map.TotalHeight - ViewHeight)));
        UpdateLayout();
        Refresh();
    }

    /// <summary>Scrolls so that the row is in view (no movement if it is).</summary>
    public void Reveal(int row)
    {
        if (row < 0 || row >= _map.Rows.Count)
        {
            return;
        }

        double target = _map.OffsetToReveal(row, VerticalOffset, ViewHeight);
        if (Math.Abs(target - VerticalOffset) > 0.5)
        {
            ScrollToVerticalOffset(target);
        }
    }

    /// <summary>The rows that have an element now.</summary>
    public IEnumerable<int> RealizedRows => _realized.Keys;

    private void ReleaseAll()
    {
        foreach (var (index, element) in _realized.ToList())
        {
            Release(index, element);
        }
    }

    private void Release(int index, ContentControl element)
    {
        _realized.Remove(index);
        _canvas.Children.Remove(element);
        var row = _map.Rows[index];
        element.Content = null;
        _pool.Push(element);
        RowReleased?.Invoke(row);
    }

    private void Refresh()
    {
        if (_content is null || _map.Rows.Count == 0)
        {
            return;
        }

        double width = Math.Max(0, ViewportWidth > 0 ? ViewportWidth : ActualWidth);
        _canvas.Width = width;
        var (first, last) = _map.Visible(VerticalOffset, ViewHeight);
        first = Math.Max(0, first - Buffer);
        last = Math.Min(_map.Rows.Count - 1, last + Buffer);

        foreach (var (index, element) in _realized.ToList())
        {
            if (index < first || index > last)
            {
                Release(index, element);
            }
        }

        for (int i = first; i <= last; i++)
        {
            if (!_realized.TryGetValue(i, out var element))
            {
                element = _pool.Count > 0 ? _pool.Pop() : new ContentControl { Focusable = false, IsTabStop = false };
                element.Content = _content(_map.Rows[i]);
                _realized[i] = element;
                _canvas.Children.Add(element);
                Canvas.SetTop(element, _map.Top(i));
                element.Height = _map.HeightOf(i);
                RowRealized?.Invoke(_map.Rows[i]);
            }

            element.Width = width;
        }
    }
}
