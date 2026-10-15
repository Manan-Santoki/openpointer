using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using OpenPointer.Config;
using OpenPointer.Engine;

namespace OpenPointer.App;

/// <summary>
/// The Actions Ring: a click-through, non-activating overlay at the cursor. Items sit on a circle,
/// item 0 at the top, clockwise; the one in the direction of movement is highlighted.
/// </summary>
public sealed class RingOverlay : IRingPresenter
{
    private const double Size = 340;
    private const double Radius = 118;

    private Window? _window;
    private Canvas? _canvas;
    private readonly List<Border> _items = [];

    public void Show(ActionRingAction ring) => Application.Current.Dispatcher.BeginInvoke(() => ShowCore(ring));

    public void Highlight(int index) => Application.Current.Dispatcher.BeginInvoke(() => HighlightCore(index));

    public void Hide() => Application.Current.Dispatcher.BeginInvoke(() => _window?.Hide());

    private void ShowCore(ActionRingAction ring)
    {
        EnsureWindow();
        _canvas!.Children.Clear();
        _items.Clear();

        var hub = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1) };
        hub.SetResourceReference(Border.BackgroundProperty, "Background");
        hub.SetResourceReference(Border.BorderBrushProperty, "Border");
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Border.BackgroundProperty, "MutedForeground");
        hub.Child = dot;
        Canvas.SetLeft(hub, Size / 2 - 18);
        Canvas.SetTop(hub, Size / 2 - 18);
        _canvas.Children.Add(hub);

        for (var i = 0; i < ring.Items.Count; i++)
        {
            var angle = 2 * Math.PI * i / ring.Items.Count; // 0 = up, clockwise
            var label = Ui.Text(ring.Items[i].Label, "Label", 12);
            var pill = new Border
            {
                Child = label,
                Padding = new Thickness(12, 7, 12, 7),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.35, Color = Colors.Black },
            };
            pill.SetResourceReference(Border.BackgroundProperty, "Background");
            pill.SetResourceReference(Border.BorderBrushProperty, "Border");
            pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var x = Size / 2 + Radius * Math.Sin(angle) - pill.DesiredSize.Width / 2;
            var y = Size / 2 - Radius * Math.Cos(angle) - pill.DesiredSize.Height / 2;
            Canvas.SetLeft(pill, x);
            Canvas.SetTop(pill, y);
            _canvas.Children.Add(pill);
            _items.Add(pill);
        }

        GetCursorPos(out var cursor);
        var scale = DpiAt(cursor) / 96.0;
        _window!.Left = cursor.X / scale - Size / 2;
        _window.Top = cursor.Y / scale - Size / 2;
        _window.Show();
    }

    private void HighlightCore(int index)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var pill = _items[i];
            var on = i == index;
            pill.SetResourceReference(Border.BackgroundProperty, on ? "Primary" : "Background");
            pill.SetResourceReference(Border.BorderBrushProperty, on ? "Primary" : "Border");
            ((TextBlock)pill.Child).SetResourceReference(TextBlock.ForegroundProperty, on ? "PrimaryForeground" : "Foreground");
        }
    }

    private void EnsureWindow()
    {
        if (_window != null) return;
        _canvas = new Canvas { Width = Size, Height = Size };
        _window = new Window
        {
            Width = Size,
            Height = Size,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Content = _canvas,
            UseLayoutRounding = true,
        };
        TextOptions.SetTextFormattingMode(_window, TextFormattingMode.Display);
        _window.SourceInitialized += (_, _) =>
        {
            // Never take focus or clicks: the user's app must stay in front.
            var handle = new WindowInteropHelper(_window).Handle;
            var style = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
        };
    }

    private static double DpiAt(POINT point)
    {
        var monitor = MonitorFromPoint(point, 2);
        return GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? x : 96;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(POINT point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hWnd, int index, int value);
}
