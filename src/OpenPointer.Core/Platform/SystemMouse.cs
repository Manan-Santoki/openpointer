using System.Runtime.InteropServices;

namespace OpenPointer.Platform;

/// <summary>Windows-wide mouse settings that Options+ also exposes (scroll speed).</summary>
public static partial class SystemMouse
{
    private const uint SPI_GETWHEELSCROLLLINES = 0x0068;
    private const uint SPI_SETWHEELSCROLLLINES = 0x0069;
    private const uint SPI_GETWHEELSCROLLCHARS = 0x006C;
    private const uint SPI_SETWHEELSCROLLCHARS = 0x006D;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    /// <summary>Lines scrolled per wheel notch (Windows default 3).</summary>
    public static int ScrollLines
    {
        get => SystemParametersInfoW(SPI_GETWHEELSCROLLLINES, 0, out var lines, 0) ? (int)lines : 3;
        set => SetParameter(SPI_SETWHEELSCROLLLINES, (uint)Math.Clamp(value, 1, 100));
    }

    /// <summary>Characters scrolled per horizontal notch (Windows default 3).</summary>
    public static int ScrollChars
    {
        get => SystemParametersInfoW(SPI_GETWHEELSCROLLCHARS, 0, out var chars, 0) ? (int)chars : 3;
        set => SetParameter(SPI_SETWHEELSCROLLCHARS, (uint)Math.Clamp(value, 1, 100));
    }

    private static void SetParameter(uint action, uint value) =>
        SystemParametersInfoSet(action, value, 0, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint param, out uint value, uint winIni);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoSet(uint action, uint param, nint value, uint winIni);
}
