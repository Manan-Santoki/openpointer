using System.Collections.Concurrent;

namespace OpenPointer.Hidpp;

/// <summary>HID++ 2.0 feature identifiers used by OpenPointer.</summary>
public static class FeatureId
{
    public const ushort Root = 0x0000;
    public const ushort FeatureSet = 0x0001;
    public const ushort DeviceInformation = 0x0003;
    public const ushort DeviceNameType = 0x0005;
    public const ushort BatteryStatus = 0x1000;
    public const ushort UnifiedBattery = 0x1004;
    public const ushort ChangeHost = 0x1814;
    public const ushort ReprogControlsV4 = 0x1B04;
    public const ushort SmartShift = 0x2110;
    public const ushort SmartShiftEnhanced = 0x2111;
    public const ushort HiResWheel = 0x2121;
    public const ushort ThumbWheel = 0x2150;
    public const ushort AdjustableDpi = 0x2201;
}

/// <summary>A notification from a HID++ 2.0 feature, already resolved to its feature ID.</summary>
public readonly record struct FeatureEvent(ushort FeatureId, int EventId, byte[] Params);

/// <summary>
/// One HID++ 2.0 device behind a transport (a receiver slot 1-6, or 0xFF for a direct connection).
/// Resolves feature IDs to the device-specific feature indexes and routes notifications.
/// </summary>
public sealed class HidppDevice
{
    /// <summary>Software ID stamped on our requests so replies can be told apart from notifications (which use 0).</summary>
    public const int SoftwareId = 0x0A;

    private readonly ConcurrentDictionary<ushort, byte> _indexByFeature = new();
    private readonly ConcurrentDictionary<byte, ushort> _featureByIndex = new();
    private readonly ConcurrentDictionary<ushort, bool> _missing = new();

    public HidppDevice(HidppTransport transport, byte deviceIndex)
    {
        Transport = transport;
        DeviceIndex = deviceIndex;
    }

    public HidppTransport Transport { get; }
    public byte DeviceIndex { get; }

    public event Action<FeatureEvent>? FeatureNotification;

    /// <summary>
    /// Pings the device. Returns the protocol version (e.g. 4.5), or null when nothing answers at
    /// this index (empty receiver slot, device asleep, or a HID++ 1.0-only device).
    /// </summary>
    public async Task<Version?> PingAsync(TimeSpan? timeout = null)
    {
        const byte marker = 0x5A;
        try
        {
            var reply = await Transport.RequestAsync(
                HidppMessage.Long(DeviceIndex, 0x00, 1, SoftwareId, [0, 0, marker]), timeout).ConfigureAwait(false);
            var p = reply.Params;
            return p[2] == marker ? new Version(p[0], p[1]) : null;
        }
        catch (HidppException ex) when (ex.ErrorCode == 0x01)
        {
            // HID++ 1.0 "invalid sub-id" reply to a ping means the device speaks protocol 1.0 only.
            return new Version(1, 0);
        }
        catch (HidppException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Returns the device's index for a feature, or null if it does not implement it.</summary>
    public async Task<byte?> GetFeatureIndexAsync(ushort featureId)
    {
        if (featureId == FeatureId.Root) return 0;
        if (_indexByFeature.TryGetValue(featureId, out var cached)) return cached;
        if (_missing.ContainsKey(featureId)) return null;

        var reply = await Transport.RequestAsync(
            HidppMessage.Long(DeviceIndex, 0x00, 0, SoftwareId, [(byte)(featureId >> 8), (byte)featureId])).ConfigureAwait(false);
        var index = reply.Params[0];
        if (index == 0)
        {
            _missing[featureId] = true;
            return null;
        }

        _indexByFeature[featureId] = index;
        _featureByIndex[index] = featureId;
        return index;
    }

    public async Task<bool> HasFeatureAsync(ushort featureId) => await GetFeatureIndexAsync(featureId).ConfigureAwait(false) != null;

    /// <summary>Calls a feature function and returns the 16 reply parameter bytes.</summary>
    public async Task<byte[]> CallAsync(ushort featureId, int function, params byte[] args)
    {
        var index = await GetFeatureIndexAsync(featureId).ConfigureAwait(false)
                    ?? throw new HidppException($"Device does not support feature {featureId:X4}.");
        var reply = await Transport.RequestAsync(HidppMessage.Long(DeviceIndex, index, function, SoftwareId, args)).ConfigureAwait(false);
        return reply.Params.ToArray();
    }

    /// <summary>Lists every feature the device implements (ID per index), for diagnostics.</summary>
    public async Task<List<(byte Index, ushort FeatureId, byte Version)>> ListFeaturesAsync()
    {
        var list = new List<(byte, ushort, byte)>();
        var setIndex = await GetFeatureIndexAsync(FeatureId.FeatureSet).ConfigureAwait(false);
        if (setIndex == null) return list;
        var count = (await CallAsync(FeatureId.FeatureSet, 0).ConfigureAwait(false))[0];
        for (var i = 1; i <= count; i++)
        {
            var p = await CallAsync(FeatureId.FeatureSet, 1, (byte)i).ConfigureAwait(false);
            var id = (ushort)((p[0] << 8) | p[1]);
            list.Add(((byte)i, id, p[3]));
            _indexByFeature.TryAdd(id, (byte)i);
            _featureByIndex.TryAdd((byte)i, id);
        }

        return list;
    }

    /// <summary>Called by the owner for notifications addressed to this device index.</summary>
    internal void HandleNotification(HidppMessage message)
    {
        if (!message.IsLong || message.SoftwareId != 0) return;
        if (!_featureByIndex.TryGetValue(message.FeatureIndex, out var featureId)) return;
        FeatureNotification?.Invoke(new FeatureEvent(featureId, message.Function, message.Params.ToArray()));
    }

    /// <summary>Forgets cached feature indexes (firmware can change after an update).</summary>
    public void ResetFeatureCache()
    {
        _indexByFeature.Clear();
        _featureByIndex.Clear();
        _missing.Clear();
    }
}
