using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using FileDetective.Core;

namespace FileDetective.App;

/// <summary>
/// Attached property that fills a <see cref="TextBlock"/> with the plain and highlighted pieces of a hit name
/// (PLAN.md 8.4). Rows are recycled by the virtualizing list, so the pieces are rebuilt whenever the value changes.
/// </summary>
internal static class Highlight
{
    private static readonly Brush HighlightBrush = CreateBrush();

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IReadOnlyList<TextSegment>), typeof(Highlight), new PropertyMetadata(null, OnSegmentsChanged));

    public static IReadOnlyList<TextSegment>? GetSegments(DependencyObject element)
        => (IReadOnlyList<TextSegment>?)element.GetValue(SegmentsProperty);

    public static void SetSegments(DependencyObject element, IReadOnlyList<TextSegment>? value)
        => element.SetValue(SegmentsProperty, value);

    private static Brush CreateBrush()
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x5C, 0xAD));
        brush.Freeze();
        return brush;
    }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<TextSegment> segments)
        {
            return;
        }

        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.Highlighted)
            {
                run.FontWeight = FontWeights.SemiBold;
                run.Foreground = HighlightBrush;
            }

            block.Inlines.Add(run);
        }
    }
}
