namespace OpenPointer.Hidpp;

/// <summary>Finds HID++ 2.0 devices on every Logitech endpoint and routes notifications to them.</summary>
public static class DeviceDiscovery
{
    /// <summary>Reads paired mouse identities from a Bolt receiver even when HID++ pings fail.
    /// These are read-only pairing-information queries; they never pair or unpair a device.</summary>
    public static async Task<IReadOnlyDictionary<byte, string>> ReadReceiverMouseNamesAsync(HidppTransport transport)
    {
        var names = new Dictionary<byte, string>();
        if (transport.ProductId != 0xC548) return names;
        for (byte slot = 1; slot <= 6; slot++)
        {
            try
            {
                var info = await transport.RequestAsync(HidppMessage.Short(0xFF, 0x83, 0xB5, [(byte)(0x50 | slot)]),
                    TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                if (info.Params.Length < 2 || (info.Params[1] & 0x0F) != 2) continue;
                var name = await transport.RequestAsync(HidppMessage.Short(0xFF, 0x83, 0xB5, [(byte)(0x60 | slot), 1]),
                    TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                if (ParseReceiverMouseName(slot, info.Params, name.Params) is { } model) names[slot] = model;
            }
            catch (Exception ex) when (ex is HidppException or TimeoutException) { }
        }
        return names;
    }

    internal static string? ParseReceiverMouseName(byte slot, ReadOnlySpan<byte> info, ReadOnlySpan<byte> name)
    {
        if (info.Length < 2 || info[0] != (0x50 | slot) || (info[1] & 0x0F) != 2 ||
            name.Length < 3 || name[0] != (0x60 | slot) || name[1] != 1 || name[2] == 0) return null;
        var length = Math.Min(name[2], name.Length - 3);
        var text = System.Text.Encoding.UTF8.GetString(name.Slice(3, length)).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// Probes receiver slots 1-6 (or index 0xFF for direct connections) on a transport and returns
    /// the HID++ 2.0 devices that answer. Notifications on the transport are wired to the devices.
    /// </summary>
    public static async Task<List<HidppDevice>> ProbeAsync(HidppTransport transport, Action<string>? log = null)
    {
        var found = new List<HidppDevice>();
        foreach (var index in SlotsOf(transport))
        {
            var device = await ProbeSlotAsync(transport, index, log).ConfigureAwait(false);
            if (device != null) found.Add(device);
        }

        transport.Notification += message =>
        {
            foreach (var device in found)
                if (device.DeviceIndex == message.DeviceIndex)
                    device.HandleNotification(message);
        };
        return found;
    }

    public static IEnumerable<byte> SlotsOf(HidppTransport transport) =>
        transport.IsReceiver ? Enumerable.Range(1, 6).Select(i => (byte)i) : [HidppMessage.ReceiverIndex];

    /// <summary>Returns the HID++ 2.0 device at one index, or null if the slot is empty, asleep or HID++ 1.0.</summary>
    public static async Task<HidppDevice?> ProbeSlotAsync(HidppTransport transport, byte index, Action<string>? log = null)
    {
        var device = new HidppDevice(transport, index);
        // Sleeping devices take a while to answer; empty slots fail fast with an error.
        var version = await device.PingAsync(TimeSpan.FromMilliseconds(2500)).ConfigureAwait(false);
        if (version == null) return null;
        if (version.Major < 2)
        {
            log?.Invoke($"PID {transport.ProductId:X4} slot {index}: HID++ {version} (unsupported)");
            return null;
        }

        log?.Invoke($"PID {transport.ProductId:X4} slot {index}: HID++ {version}");
        return device;
    }

    /// <summary>
    /// Turns on receiver notifications for device arrival and removal (HID++ 1.0 register 0x00).
    /// Harmless if the receiver refuses.
    /// </summary>
    public static async Task EnableReceiverNotificationsAsync(HidppTransport transport)
    {
        if (!transport.IsReceiver) return;
        try
        {
            // 0x000100 wireless notifications | 0x000800 software present.
            await transport.RequestAsync(HidppMessage.Short(HidppMessage.ReceiverIndex, 0x80, 0x00, [0x00, 0x09, 0x00]),
                TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HidppException or TimeoutException)
        {
        }
    }

    /// <summary>
    /// True for a receiver notification that says a paired device linked or unlinked
    /// ([10, slot, 41, flags, ...]); bit 6 of the flags means the link is down.
    /// </summary>
    public static bool TryParseConnection(HidppMessage message, out byte slot, out bool connected)
    {
        slot = message.DeviceIndex;
        connected = false;
        if (message.ReportId != HidppMessage.ShortReportId || message.FeatureIndex != 0x41) return false;
        connected = (message.Raw[4] & 0x40) == 0;
        return true;
    }
}
