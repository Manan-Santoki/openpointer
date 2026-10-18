using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using OpenPointer.Config;
using OpenPointer.Platform;

namespace OpenPointer.App.Views;

/// <summary>
/// The touchpad version of the Buttons page: a live drawing of the user's pad with its gesture
/// zones, and an action picker per gesture. Gestures Windows already uses are shown but locked.
/// </summary>
internal static class TouchpadView
{
    private const double PadWidth = 520;

    public static IEnumerable<UIElement> Create(AppConfig config, Profile profile, bool isDefault, Action<string, ActionSpec?> assign)
    {
        var devices = Services.Devices;
        var windows = devices.TouchpadSettings;
        var size = devices.TouchpadSize ?? (117, 72);
        var height = PadWidth * size.Height / size.Width;
        var pad = new PadDrawing(PadWidth, height);

        bool Assigned(string key) => windows.Owner(key) == null &&
            (profile.Buttons.GetValueOrDefault(key) ?? config.DefaultProfile.Buttons.GetValueOrDefault(key)) is not (null or NativeAction or NoneAction);
        foreach (var gesture in TouchpadGestures.All.Where(g => Assigned(g.Key))) pad.Mark(gesture.Key, true);

        var drawing = new Border { Style = Ui.Style("Card"), Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 8), Child = pad.Root };
        yield return drawing;
        var legend = Ui.Text(devices.HasTouchpad
            ? $"Your touchpad{(devices.TouchpadSize is { } mm ? $", {mm.Width:F0} × {mm.Height:F0} mm" : "")}. Touch it to see your fingers; shaded zones have a gesture."
            : "Touch your touchpad once and it appears here. Shaded zones have a gesture.", "Caption");
        legend.TextWrapping = TextWrapping.Wrap;
        legend.Margin = new Thickness(2, 0, 0, 24);
        yield return legend;

        foreach (var group in TouchpadGestures.All.GroupBy(g => g.Group))
        {
            yield return Ui.Section(group.Key, GroupHint(group.Key, windows));
            var rows = new List<UIElement>();
            foreach (var gesture in group)
            {
                var owner = windows.Owner(gesture.Key);
                profile.Buttons.TryGetValue(gesture.Key, out var own);
                var effective = own ?? (isDefault ? new NativeAction() : ContextMatcher.ResolveButton(config, new FocusContext("\0", "", ""), gesture.Key));
                var caption = owner != null ? $"Used by Windows: {owner}"
                    : !isDefault ? (own != null ? $"Custom for {profile.Name}" : "Same as All applications")
                    : gesture.Hint;
                FrameworkElement control;
                if (owner != null)
                {
                    control = Ui.Text("Windows", "MutedText");
                    control.ToolTip = "Set this gesture to Nothing in Windows touchpad settings to use it here.";
                }
                else
                {
                    control = ButtonsPage.SelectButton(effective is NativeAction ? "Off" : ActionCatalog.Label(effective), anchor =>
                        ActionMenu.ShowDropdown(anchor, effective, picked => assign(gesture.Key, picked),
                            inheritLabel: !isDefault && own != null ? "Use All applications setting" : null, nativeLabel: "Off"));
                }

                var row = Ui.Row(gesture.Label, caption, control);
                row.Background = Brushes.Transparent;
                if (owner != null) row.Opacity = 0.6;
                var key = gesture.Key;
                row.MouseEnter += (_, _) => pad.Highlight(key, true);
                row.MouseLeave += (_, _) => pad.Highlight(key, false);
                rows.Add(row);
            }
            yield return Ui.Card(rows);
        }

        if (TouchpadGestures.All.Any(g => windows.Owner(g.Key) != null))
        {
            var open = Ui.Button("Open Windows touchpad settings", () =>
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:devices-touchpad") { UseShellExecute = true }));
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.Margin = new Thickness(0, 0, 0, 12);
            yield return open;
        }

        // Live fingers and gesture flashes, for as long as the drawing is on screen.
        void OnFrame(TouchFrame frame) => pad.Root.Dispatcher.BeginInvoke(() => pad.ShowFingers(frame.Contacts));
        void OnGesture(string key) => pad.Root.Dispatcher.BeginInvoke(() => pad.Flash(key));
        pad.Root.Loaded += (_, _) =>
        {
            devices.TouchpadFrame += OnFrame;
            devices.TouchpadGestureFired += OnGesture;
        };
        pad.Root.Unloaded += (_, _) =>
        {
            devices.TouchpadFrame -= OnFrame;
            devices.TouchpadGestureFired -= OnGesture;
        };
    }

    private static string? GroupHint(string group, WindowsTouchpadSettings windows) => group switch
    {
        TouchpadGestures.Corners => windows.CornersNeedHold
            ? "Touch and hold a corner for half a second. Tap to click is on in Windows, so a quick tap still clicks."
            : "Tap a corner with one finger.",
        TouchpadGestures.EdgeSlides => "Start at the very edge and slide along it. Shortcuts and media keys repeat as you slide, which suits volume or brightness; apps and macros run once. The pointer stays put while the gesture runs.",
        TouchpadGestures.EdgeSwipes => "Start at the very edge and flick toward the middle.",
        TouchpadGestures.Circles => "Circle twice with one finger anywhere on the pad. Keep circling to repeat shortcuts and media keys once per turn; apps and macros run once.",
        TouchpadGestures.FiveFingers => "Windows treats five fingers as its four-finger tap, so this is free when four-finger taps are set to Nothing.",
        _ => "Available when Windows has this gesture set to Nothing.",
    };

    /// <summary>The pad outline, its zones and the fingers on it.</summary>
    private sealed class PadDrawing
    {
        private readonly double _width, _height;
        private readonly Canvas _canvas;
        private readonly Dictionary<string, List<Shape>> _zones = [];
        private readonly HashSet<string> _marked = [];
        private readonly List<Ellipse> _fingers = [];
        private readonly TextBlock _flash;
        private readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };

        public FrameworkElement Root { get; }

        public PadDrawing(double width, double height)
        {
            _width = width;
            _height = height;
            _canvas = new Canvas { Width = width, Height = height, ClipToBounds = true };
            var outline = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), Child = _canvas };
            outline.SetResourceReference(Border.BackgroundProperty, "Muted");
            outline.SetResourceReference(Border.BorderBrushProperty, "Input");
            outline.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 14, 14);

            var band = TouchpadGestures.EdgeBand * height;
            var cornerW = TouchpadGestures.CornerX * width;
            var cornerH = TouchpadGestures.CornerY * height;
            foreach (var edge in new[] { "left", "right", "top", "bottom" })
            {
                var rect = edge switch
                {
                    "left" => new Rect(0, 0, band, height),
                    "right" => new Rect(width - band, 0, band, height),
                    "top" => new Rect(0, 0, width, band),
                    _ => new Rect(0, height - band, width, band),
                };
                var shape = Zone(rect);
                foreach (var key in TouchpadGestures.All.Where(g => g.Key.Contains($"edge-{edge}-") || g.Key.EndsWith($"swipe-in-{edge}")))
                    Add(key.Key, shape);
            }
            foreach (var (v, h) in new[] { ("top", "left"), ("top", "right"), ("bottom", "left"), ("bottom", "right") })
            {
                var shape = Zone(new Rect(h == "left" ? 0 : width - cornerW, v == "top" ? 0 : height - cornerH, cornerW, cornerH));
                Add(TouchpadGestures.Key($"corner-{v}-{h}"), shape);
            }
            var circle = new Ellipse { Width = height * 0.5, Height = height * 0.5, StrokeThickness = 1.5, StrokeDashArray = [3, 3], Opacity = 0 };
            circle.SetResourceReference(Shape.StrokeProperty, "Ring");
            Canvas.SetLeft(circle, width / 2 - height * 0.25);
            Canvas.SetTop(circle, height * 0.25);
            _canvas.Children.Add(circle);
            Add(TouchpadGestures.Key("circle-cw"), circle);
            Add(TouchpadGestures.Key("circle-ccw"), circle);
            var whole = Zone(new Rect(0, 0, width, height));
            foreach (var key in TouchpadGestures.All.Where(g => char.IsDigit(g.Key[TouchpadGestures.Prefix.Length])))
                Add(key.Key, whole);

            _flash = new TextBlock { Style = Ui.Style("Label"), FontSize = 13, Opacity = 0, TextAlignment = TextAlignment.Center, Width = width };
            Canvas.SetTop(_flash, height / 2 - 9);
            _canvas.Children.Add(_flash);
            _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); _flash.Opacity = 0; Refresh(); };
            Root = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Children = { outline } };
        }

        private Rectangle Zone(Rect rect)
        {
            var shape = new Rectangle { Width = rect.Width, Height = rect.Height, Opacity = 0, IsHitTestVisible = false };
            shape.SetResourceReference(Shape.FillProperty, "Ring");
            Canvas.SetLeft(shape, rect.X);
            Canvas.SetTop(shape, rect.Y);
            _canvas.Children.Add(shape);
            return shape;
        }

        private void Add(string key, Shape shape)
        {
            if (!_zones.TryGetValue(key, out var list)) _zones[key] = list = [];
            list.Add(shape);
        }

        public void Mark(string key, bool on)
        {
            if (on) _marked.Add(key); else _marked.Remove(key);
            Refresh();
        }

        private string? _highlighted;

        public void Highlight(string key, bool on)
        {
            _highlighted = on ? key : _highlighted == key ? null : _highlighted;
            Refresh();
        }

        /// <summary>Shades assigned zones lightly and the hovered or just-fired one strongly.</summary>
        private void Refresh(string? fired = null)
        {
            var strong = fired ?? _highlighted;
            foreach (var (key, shapes) in _zones)
                foreach (var shape in shapes) shape.Opacity = 0;
            foreach (var (key, shapes) in _zones)
            {
                var level = key == strong ? (shapes[0] is Ellipse ? 1 : 0.45) : _marked.Contains(key) ? (shapes[0] is Ellipse ? 0.5 : 0.16) : 0;
                if (char.IsDigit(key[TouchpadGestures.Prefix.Length]) && key != strong) level = 0; // the whole pad would hide everything else
                foreach (var shape in shapes) shape.Opacity = Math.Max(shape.Opacity, level);
            }
        }

        public void Flash(string key)
        {
            _flash.Text = TouchpadGestures.Find(key) is { } g ? $"{g.Group}: {g.Label}" : key;
            _flash.Opacity = 1;
            Refresh(key);
            _flashTimer.Stop();
            _flashTimer.Start();
        }

        public void ShowFingers(IReadOnlyList<TouchContact> contacts)
        {
            var touching = contacts.Where(c => c.Touching).ToList();
            while (_fingers.Count < touching.Count)
            {
                var dot = new Ellipse { Width = 22, Height = 22, IsHitTestVisible = false, StrokeThickness = 2 };
                dot.SetResourceReference(Shape.FillProperty, "Primary");
                dot.SetResourceReference(Shape.StrokeProperty, "Background");
                _canvas.Children.Add(dot);
                _fingers.Add(dot);
            }
            for (var i = 0; i < _fingers.Count; i++)
            {
                var dot = _fingers[i];
                if (i >= touching.Count) { dot.Visibility = Visibility.Collapsed; continue; }
                dot.Visibility = Visibility.Visible;
                dot.Opacity = touching[i].Confident ? 0.9 : 0.35;
                Canvas.SetLeft(dot, touching[i].X * _width - 11);
                Canvas.SetTop(dot, touching[i].Y * _height - 11);
            }
        }
    }
}
