namespace OpenPointer.Hidpp;

/// <summary>Finds HID++ 2.0 devices on every Logitech endpoint and routes notifications to them.</summary>
public static class DeviceDiscovery
{
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
