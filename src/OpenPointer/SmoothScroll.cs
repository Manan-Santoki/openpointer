using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OpenPointer.App;

/// <summary>
/// Replaces WPF's default wheel handling (a fixed 48 px jump per notch) with a gentler, eased scroll
/// for every ScrollViewer in the app. High-resolution wheels send small deltas and stay proportional.
/// </summary>
public static class SmoothScroll
{
    /// <summary>Pixels per standard 120-unit wheel notch.</summary>
    private const double PixelsPerNotch = 34;

    /// <summary>Fraction of the remaining distance covered each frame.</summary>
    private const double Easing = 0.22;

    private static readonly Dictionary<ScrollViewer, double> Targets = [];
    private static bool _rendering;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel), handledEventsToo: false);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || viewer.ScrollableHeight <= 0) return;

        // Let the innermost scrollable viewer take the wheel; outer viewers see the bubbling event only.
        if (e.OriginalSource is DependencyObject source && InnerScrollable(source, viewer) is { } inner && inner != viewer) return;

        e.Handled = true;
        var start = Targets.TryGetValue(viewer, out var pending) ? pending : viewer.VerticalOffset;
        var target = Math.Clamp(start - e.Delta / 120.0 * PixelsPerNotch, 0, viewer.ScrollableHeight);
        Targets[viewer] = target;
        if (!_rendering)
        {
            _rendering = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private static ScrollViewer? InnerScrollable(DependencyObject source, ScrollViewer outer)
    {
        for (var node = source; node != null && node != outer; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
            if (node is ScrollViewer { ScrollableHeight: > 0 } viewer)
                return viewer;
        return outer;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        foreach (var (viewer, target) in Targets.ToList())
        {
            var current = viewer.VerticalOffset;
            var distance = target - current;
            if (Math.Abs(distance) < 0.5 || !viewer.IsLoaded)
            {
                if (viewer.IsLoaded) viewer.ScrollToVerticalOffset(target);
                Targets.Remove(viewer);
                continue;
            }

            viewer.ScrollToVerticalOffset(current + distance * Easing);
        }

        if (Targets.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }
}
