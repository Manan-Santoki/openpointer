using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HidSharp;

namespace OpenPointer.Platform;

/// <summary>A standard mouse exposed by Windows, regardless of brand or connection.</summary>
public sealed record WindowsMouseInfo(string DevicePath, string Name, int ButtonCount, string Connection,
    int? VendorId = null, int? ProductId = null, int ButtonMask = -1, Guid? ContainerId = null, bool IsTouchpad = false)
{
    internal bool IsVisible(Config.GeneralSettings settings) => !IsTouchpad || settings.DeveloperMode && settings.ShowTouchpads;

    internal bool MatchesLogitechDevice(int productId, Guid? containerId) => VendorId == Hidpp.HidppTransport.VendorLogitech &&
        ProductId == productId && (ContainerId == null || containerId == null || ContainerId == containerId);

    public IReadOnlyList<string> Buttons
    {
        get
        {
            var mask = ButtonMask >= 0 ? ButtonMask : (1 << Math.Clamp(ButtonCount, 0, 5)) - 1;
            var buttons = new List<string>();
            if ((mask & 4) != 0) buttons.Add(Config.ButtonNames.Middle);
            if ((mask & 16) != 0) buttons.Add(Config.ButtonNames.Forward);
            if ((mask & 8) != 0) buttons.Add(Config.ButtonNames.Back);
            return buttons;
        }
    }
}

/// <summary>Enumerates Windows mouse devices without opening their protected input collections.</summary>
public static class WindowsMouseDiscovery
{
    public static IReadOnlyList<WindowsMouseInfo> Enumerate()
    {
        var entrySize = (uint)Marshal.SizeOf<RawDevice>();
        // Hotplug can change the count between the two calls.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint count = 0;
            if (GetRawInputDeviceList(null, ref count, entrySize) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (count == 0) return [];
            var devices = new RawDevice[count];
            var read = GetRawInputDeviceList(devices, ref count, entrySize);
            if (read == uint.MaxValue)
            {
                if (Marshal.GetLastWin32Error() == 122) continue;
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new List<WindowsMouseInfo>();
            foreach (var device in devices.Take((int)read).Where(d => d.Type == 0))
            {
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                var size = info.Size;
                if (GetRawInputDeviceInfo(device.Handle, 0x2000000B, ref info, ref size) == uint.MaxValue) continue;
                if (DeviceName(device.Handle) is not { } path) continue;
                result.Add(Describe(path, (int)info.Buttons) with { ButtonMask = ReadButtonMask(device.Handle) });
            }

            var mouseInterfaces = result.Select(m => Hidpp.HidppTransport.InterfaceKey(m.DevicePath)).ToHashSet();
            var touchpads = new HashSet<string>();
            foreach (var device in DeviceList.Local.GetHidDevices())
            {
                var key = Hidpp.HidppTransport.InterfaceKey(device.DevicePath);
                if (!mouseInterfaces.Contains(key)) continue;
                try
                {
                    // A precision touchpad also exposes a legacy mouse collection. Match its
                    // sibling Touch Pad TLC (Digitizers page 0x0D, usage 0x05), not its name or
                    // button count: a genuine two-button USB/Bluetooth mouse must remain visible.
                    if (device.GetReportDescriptor().DeviceItems.Any(i => i.Usages.GetAllValues().Contains(0x000D0005u)))
                        touchpads.Add(key);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception) { }
            }
            return result.DistinctBy(m => m.DevicePath, StringComparer.OrdinalIgnoreCase).Select(m =>
                touchpads.Contains(Hidpp.HidppTransport.InterfaceKey(m.DevicePath))
                    ? m with { IsTouchpad = true, Name = m.Name == "Built-in pointing device" ? "Built-in touchpad" : m.Name }
                    : m).ToList();
        }

        throw new Win32Exception(122, "Mouse devices kept changing during enumeration.");
    }

    /// <summary>The interface path of a raw input device, as used for <see cref="WindowsMouseInfo.DevicePath"/>.</summary>
    internal static string? DeviceName(nint device)
    {
        uint chars = 0;
        if (GetRawInputDeviceName(device, 0x20000007, null, ref chars) == uint.MaxValue || chars == 0) return null;
        var path = new StringBuilder((int)chars);
        return GetRawInputDeviceName(device, 0x20000007, path, ref chars) == uint.MaxValue ? null : path.ToString();
    }

    private static WindowsMouseInfo Describe(string path, int buttons)
    {
        var name = "Windows mouse";
        var haveReportedName = false;
        var bluetooth = path.Contains("BTH", StringComparison.OrdinalIgnoreCase);
        var usb = path.Contains("VID_", StringComparison.OrdinalIgnoreCase);
        var builtIn = false;
        var instance = ReadInterfaceInstance(path);
        if (instance != null && CM_Locate_DevNodeW(out var node, instance, 0) == 0)
        {
            // Bluetooth names and the bus live on a parent of the HID mouse collection.
            for (var depth = 0; depth < 5; depth++)
            {
                var id = new StringBuilder(1024);
                var nodeId = "";
                if (CM_Get_Device_IDW(node, id, id.Capacity, 0) == 0)
                {
                    nodeId = id.ToString();
                    // Above the device sit host controllers ("PCI Device") whose names are not the mouse's.
                    if (nodeId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) { builtIn |= !usb && !bluetooth; break; }
                    bluetooth |= nodeId.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);
                    usb |= nodeId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase);
                    builtIn |= nodeId.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase);
                }

                var reported = ReadProperty(node, new(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 4));
                var friendly = ReadProperty(node, new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14));
                if (!haveReportedName && !string.IsNullOrWhiteSpace(reported)) { name = reported; haveReportedName = true; }
                var remoteBluetoothDevice = nodeId.StartsWith("BTHLE\\DEV_", StringComparison.OrdinalIgnoreCase) ||
                    nodeId.StartsWith("BTHENUM\\DEV_", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(friendly) && (!haveReportedName || remoteBluetoothDevice)) name = friendly;
                if (CM_Get_Parent(out var parent, node, 0) != 0) break;
                node = parent;
            }
        }

        if (name == "Windows mouse" && builtIn) name = "Built-in pointing device";
        return new(path, name, buttons, bluetooth ? "Bluetooth" : usb ? "USB / wireless receiver" : builtIn ? "Built-in" : "Windows mouse",
            ReadId(path, "VID"), ReadId(path, "PID"), ContainerId: ReadContainerId(path));
    }

    internal static Guid? ReadContainerId(string path)
    {
        var instance = ReadInterfaceInstance(path);
        if (instance == null || CM_Locate_DevNodeW(out var node, instance, 0) != 0) return null;
        var key = new PropertyKey(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
        var data = new byte[16];
        uint size = 16;
        return CM_Get_DevNode_PropertyW(node, ref key, out var type, data, ref size, 0) == 0 && type == 13 && size == 16
            ? new Guid(data) : null;
    }

    internal static int? ReadId(string path, string kind)
    {
        // Bluetooth HID identifiers can include a four-digit vendor-source prefix.
        var match = Regex.Match(path, kind + @"[_&](?:[0-9a-f]{4})?([0-9a-f]{4})(?![0-9a-f])", RegexOptions.IgnoreCase);
        return match.Success ? Convert.ToInt32(match.Groups[1].Value, 16) : null;
    }

    private static int ReadButtonMask(nint device)
    {
        // Prefer actual HID button usages to the mouse driver's advertised maximum button count.
        // This also handles sparse reports (e.g. middle + forward, with no back button).
        uint size = 0;
        if (GetPreparsedData(device, 0x20000005, 0, ref size) == uint.MaxValue || size == 0) return -1;
        var data = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetPreparsedData(device, 0x20000005, data, ref size) == uint.MaxValue ||
                HidP_GetCaps(data, out var caps) != 0x00110000) return -1;
            var count = caps.InputButtonCaps;
            if (count == 0) return 0;
            var buttons = new ButtonCaps[count];
            if (HidP_GetButtonCaps(0, buttons, ref count, data) != 0x00110000) return -1;
            var mask = 0;
            foreach (var button in buttons.Take(count).Where(b => b.UsagePage == 9))
            {
                var min = (int)button.UsageMin;
                var max = button.IsRange != 0 ? button.UsageMax : min;
                for (var usage = Math.Max(min, 1); usage <= Math.Min(max, 5); usage++) mask |= 1 << (usage - 1);
            }
            return mask;
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    private static string? ReadInterfaceInstance(string path)
    {
        var key = new PropertyKey(new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
        var buffer = new byte[4096];
        uint size = (uint)buffer.Length;
        return CM_Get_Device_Interface_PropertyW(path, ref key, out var type, buffer, ref size, 0) == 0 && type == 18
            ? Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0') : null;
    }

    private static string? ReadProperty(uint node, PropertyKey key)
    {
        var buffer = new byte[4096];
        uint size = (uint)buffer.Length;
        return CM_Get_DevNode_PropertyW(node, ref key, out var type, buffer, ref size, 0) == 0 && type == 18
            ? Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0') : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawDevice { public nint Handle; public uint Type; }

    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct DeviceInfo
    {
        public uint Size, Type, Id, Buttons, SampleRate;
        public int HorizontalWheel;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HidCaps { [FieldOffset(46)] public ushort InputButtonCaps; }

    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct ButtonCaps
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(56)] public ushort UsageMin;
        [FieldOffset(58)] public ushort UsageMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, uint id)
    {
        public readonly Guid Format = format;
        public readonly uint Id = id;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList([Out] RawDevice[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(nint device, uint command, ref DeviceInfo info, ref uint size);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceName(nint device, uint command, StringBuilder? name, ref uint size);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    private static extern uint GetPreparsedData(nint device, uint command, nint buffer, ref uint size);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(nint data, out HidCaps caps);
    [DllImport("hid.dll")]
    private static extern int HidP_GetButtonCaps(int reportType, [Out] ButtonCaps[] caps, ref ushort count, nint data);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_PropertyW(string path, ref PropertyKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string instance, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(uint node, ref PropertyKey key, out uint type, [Out] byte[] buffer, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
}
