using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Lampa.Desktop.Services;

/// <summary>
/// Precision touchpads emit a burst of WM_MOUSEWHEEL messages, each already
/// quantized to ±120. WPF then scrolls by "3 lines" per message, so a small
/// finger movement jumps the page. Convert those bursts into small pixel steps.
/// </summary>
internal static class WheelScroll
{
    private static readonly DependencyProperty LastUtcProperty =
        DependencyProperty.RegisterAttached("LastUtc", typeof(DateTime), typeof(WheelScroll));

    public static void Handle(MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        if (e.OriginalSource is not DependencyObject origin) return;
        var viewer = FindScrollable(origin);
        if (viewer is null) return;

        var last = viewer.GetValue(LastUtcProperty) is DateTime stored ? stored : DateTime.MinValue;
        var now = DateTime.UtcNow;
        viewer.SetValue(LastUtcProperty, now);
        var burst = (now - last).TotalMilliseconds < 80;
        var scale = burst ? 0.16 : 0.40;
        viewer.ScrollToVerticalOffset(Math.Clamp(viewer.VerticalOffset - e.Delta * scale, 0, viewer.ScrollableHeight));
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollable(DependencyObject? start)
    {
        for (var current = start; current != null; current = Parent(current))
        {
            if (current is ScrollViewer viewer && viewer.ScrollableHeight > 0)
                return viewer;
        }
        return null;
    }

    private static DependencyObject? Parent(DependencyObject current) =>
        current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
}
