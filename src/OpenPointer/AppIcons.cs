using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenPointer.App;

/// <summary>Icons for Start menu apps (desktop and Microsoft Store) via the shell's image factory.</summary>
public static class AppIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The icon for a shell parsing name such as "shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App".</summary>
    public static ImageSource? For(string parsingName, int pixels = 32) => Cache.GetOrAdd($"{parsingName}|{pixels}", _ =>
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, 0, ref iid, out var factory) != 0 || factory == null) return null;
            try
            {
                if (factory.GetImage(new SIZE { cx = pixels, cy = pixels }, SIIGBF_ICONONLY, out var hbitmap) != 0) return null;
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
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (COMException)
        {
            return null;
        }
    });

    private const int SIIGBF_ICONONLY = 0x04;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out nint phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? item);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);
}
