using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenPointer.App.Views;
using OpenPointer.Config;
using OpenPointer.Engine;
using OpenPointer.Platform;

namespace OpenPointer.App;

/// <summary>
/// Renders each page of the settings window to PNG for the README:
///   OpenPointer.exe --capture-docs docs/screenshots
/// Runs beside a normal instance with a passive device manager, so the mouse is never reconfigured.
/// </summary>
public static class DocsCapture
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = new ConfigStore();
        config.Load();
        ThemeManager.Apply(config.Current.General.Theme);
        var devices = new DeviceManager(config, passive: true);
        Services.Initialize(config, devices, new FocusTracker());
        Draft.Initialize();
        devices.Start();

        for (var i = 0; i < 40 && devices.Mice.Count == 0; i++) await Task.Delay(250);
        await Task.Delay(800);

        var window = new MainWindow { Width = 1080, Height = 720, ShowActivated = false, Left = -20000, Top = 0 };
        window.Show();
        foreach (var (page, file) in new[] { ("Buttons", "buttons"), ("Pointer", "pointer"), ("Apps", "applications"), ("Settings", "settings") })
        {
            window.Navigate(page);
            await Task.Delay(500);
            Save(window, Path.Combine(directory, file + ".png"));
        }

        window.Close();
        devices.Dispose();
        Application.Current.Shutdown();
    }

    private static void Save(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        const double scale = 2;
        var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * scale), (int)(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var surface = new DrawingVisual();
        using (var dc = surface.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("Background"), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }

        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
