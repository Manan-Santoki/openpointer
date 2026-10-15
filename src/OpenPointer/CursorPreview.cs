using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Image = System.Windows.Controls.Image;

namespace OpenPointer.App;

/// <summary>
/// Renders cursor files (.cur and animated .ani) and Windows' built-in cursors to images through
/// Win32, exactly as Windows draws them. Animated cursors play at their own frame rate.
/// </summary>
public static class CursorPreview
{
    // Built-in cursors for roles that the default scheme leaves empty.
    private static readonly Dictionary<string, int> SystemIds = new()
    {
        ["Arrow"] = 32512, ["IBeam"] = 32513, ["Wait"] = 32514, ["Crosshair"] = 32515, ["UpArrow"] = 32516,
        ["SizeNWSE"] = 32642, ["SizeNESW"] = 32643, ["SizeWE"] = 32644, ["SizeNS"] = 32645, ["SizeAll"] = 32646,
        ["No"] = 32648, ["Hand"] = 32649, ["AppStarting"] = 32650, ["Help"] = 32651, ["Pin"] = 32671, ["Person"] = 32672, ["NWPen"] = 32631,
    };

    /// <summary>
    /// An image showing a cursor at <paramref name="sizeDip"/>, painted on <paramref name="background"/>
    /// (cursors carry alpha that GDI cannot blend onto a transparent surface).
    /// </summary>
    public static Image Create(string role, string? file, double sizeDip, System.Windows.Media.Color background, double dpiScale)
    {
        var pixels = (int)Math.Round(sizeDip * dpiScale);
        var image = new Image { Width = sizeDip, Height = sizeDip, SnapsToDevicePixels = true };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(image, System.Windows.Media.BitmapScalingMode.HighQuality);

        nint handle;
        var owned = true;
        if (!string.IsNullOrEmpty(file) && File.Exists(file))
        {
            handle = LoadImage(0, file, IMAGE_CURSOR, pixels, pixels, LR_LOADFROMFILE);
        }
        else
        {
            handle = SystemIds.TryGetValue(role, out var id) ? LoadCursor(0, id) : 0;
            owned = false; // shared system cursor
        }

        if (handle == 0) return image;

        var (frames, rateMs) = file != null && file.EndsWith(".ani", StringComparison.OrdinalIgnoreCase) ? AniInfo(file) : (1, 0);
        var bitmaps = new List<BitmapSource>();
        for (var step = 0; step < Math.Min(frames, 64); step++)
        {
            var frame = Render(handle, pixels, background, step);
            if (frame == null) break;
            bitmaps.Add(frame);
        }

        if (owned) DestroyCursor(handle);
        if (bitmaps.Count == 0) return image;
        image.Source = bitmaps[0];

        if (bitmaps.Count > 1 && rateMs > 0)
        {
            var index = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(rateMs) };
            timer.Tick += (_, _) => image.Source = bitmaps[index = (index + 1) % bitmaps.Count];
            image.Loaded += (_, _) => timer.Start();
            image.Unloaded += (_, _) => timer.Stop();
        }

        return image;
    }

    private static BitmapSource? Render(nint cursor, int pixels, System.Windows.Media.Color background, int step)
    {
        using var bitmap = new Bitmap(pixels, pixels);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.FromArgb(255, background.R, background.G, background.B));
            var hdc = g.GetHdc();
            try
            {
                if (!DrawIconEx(hdc, 0, 0, cursor, pixels, pixels, step, 0, DI_NORMAL)) return null;
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        var hbitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hbitmap);
        }
    }

    /// <summary>Reads the step count and default rate from an .ani file's "anih" header.</summary>
    internal static (int Steps, int RateMs) AniInfo(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            for (var i = 12; i + 44 <= data.Length; i++)
            {
                if (data[i] != 'a' || data[i + 1] != 'n' || data[i + 2] != 'i' || data[i + 3] != 'h') continue;
                var header = i + 8;
                var steps = BitConverter.ToInt32(data, header + 8);
                var jiffies = BitConverter.ToInt32(data, header + 28); // 1/60 s
                return (Math.Max(1, steps), Math.Max(16, jiffies * 1000 / 60));
            }
        }
        catch (IOException) { }

        return (1, 0);
    }

    private const int IMAGE_CURSOR = 2;
    private const uint LR_LOADFROMFILE = 0x10;
    private const int DI_NORMAL = 3;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string name, int type, int cx, int cy, uint load);

    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, int id);
    [DllImport("user32.dll")] private static extern bool DestroyCursor(nint cursor);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint hdc, int x, int y, nint icon, int cx, int cy, int step, nint brush, int flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
}
