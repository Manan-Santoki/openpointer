using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenPointer.App;

/// <summary>Small factory helpers so code-built UI uses the same styles as XAML.</summary>
public static class Ui
{
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public static TextBlock Text(string text, string style = "Text", double? size = null)
    {
        var block = new TextBlock { Text = text, Style = Style(style) };
        if (size != null) block.FontSize = size.Value;
        return block;
    }

    public static TextBlock Icon(string glyph, double size = 14, string brush = "Foreground")
    {
        var block = new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)Application.Current.FindResource("Icons"),
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }

    public static Border Divider() => new() { Style = Style("Divider") };

    /// <summary>A labelled settings row: title and description on the left, control on the right.</summary>
    public static Grid Row(string title, string? description, UIElement? control)
    {
        var grid = new Grid { Style = Style("Row") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 12, 16, 12) };
        left.Children.Add(Text(title, "Label"));
        if (!string.IsNullOrEmpty(description))
        {
            var caption = Text(description, "Caption");
            caption.Margin = new Thickness(0, 2, 0, 0);
            caption.TextWrapping = TextWrapping.Wrap;
            caption.TextTrimming = TextTrimming.None;
            left.Children.Add(caption);
        }

        grid.Children.Add(left);
        if (control is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(element, 1);
            grid.Children.Add(element);
        }

        return grid;
    }

    /// <summary>A bordered card that stacks rows with dividers between them.</summary>
    public static Border Card(IEnumerable<UIElement> rows)
    {
        var stack = new StackPanel();
        var first = true;
        foreach (var row in rows)
        {
            if (!first) stack.Children.Add(Divider());
            stack.Children.Add(row);
            first = false;
        }

        return new Border { Style = Style("Card"), Child = stack, Margin = new Thickness(0, 0, 0, 24) };
    }

    public static StackPanel Section(string title, string? description = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(Text(title, "Heading"));
        if (description != null)
        {
            var caption = Text(description, "MutedText");
            caption.Margin = new Thickness(0, 2, 0, 0);
            caption.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(caption);
        }

        return panel;
    }

    public static CheckBox Switch(bool isOn, Action<bool> changed)
    {
        var toggle = new CheckBox { Style = Style("Switch"), IsChecked = isOn };
        toggle.Click += (_, _) => changed(toggle.IsChecked == true);
        return toggle;
    }

    /// <summary>An outline button, or the named style ("PrimaryButton", "GhostButton", ...).</summary>
    public static Button Button(string text, Action click, string? style = null)
    {
        var button = new Button { Content = text };
        if (style != null) button.Style = Style(style);
        button.Click += (_, _) => click();
        return button;
    }

    public static MenuItem MenuItem(string header, Action? click = null, bool isChecked = false, string? hint = null)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked, InputGestureText = hint ?? "" };
        if (click != null) item.Click += (_, e) => { e.Handled = true; click(); };
        return item;
    }

    /// <summary>A non-interactive menu row for labels and status.</summary>
    public static MenuItem MenuLabel(UIElement content) => new()
    {
        Header = content,
        IsHitTestVisible = false,
        Focusable = false,
    };

    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The shell icon of an executable, or null.</summary>
    public static ImageSource? ExecutableIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return IconCache.GetOrAdd(path!, p =>
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(p);
                if (icon == null) return null;
                var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
        });
    }
}
