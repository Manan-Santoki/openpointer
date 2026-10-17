using System.Text.RegularExpressions;
using HidSharp;

namespace OpenPointer.Hidpp;

/// <summary>
/// One HID++ endpoint: a receiver interface or a directly connected device. Windows exposes the
/// short (0x10) and long (0x11) HID++ reports as separate HID collections, so both are opened and
/// their input is merged. Requests are serialized and matched to replies; everything else is
/// surfaced as a notification.
/// </summary>
public sealed class HidppTransport : IDisposable
{
    public const int VendorLogitech = 0x046D;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(2000);

    private readonly HidStream _long;
    private readonly HidStream? _short;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly object _pendingSync = new();
    private Pending? _pending;
    private volatile bool _disposed;

    private sealed record Pending(byte DeviceIndex, byte Match2, byte Match3, TaskCompletionSource<HidppMessage> Completion);

    private HidppTransport(HidDevice longDevice, HidStream longStream, HidStream? shortStream)
    {
        Device = longDevice;
        _long = longStream;
        _short = shortStream;
        _long.ReadTimeout = Timeout.Infinite;
        if (_short != null) _short.ReadTimeout = Timeout.Infinite;
        StartReader(_long, HidppMessage.LongLength);
        if (_short != null) StartReader(_short, HidppMessage.ShortLength);
    }

    public HidDevice Device { get; }
    public int ProductId => Device.ProductID;
    public string DevicePath => Device.DevicePath;

    /// <summary>True for receivers (Bolt, Unifying, Lightspeed...) that address paired devices by index 1-6.</summary>
    public bool IsReceiver => KnownReceivers.Contains(Device.ProductID);

    /// <summary>Raised on the reader thread for every message that is not a reply to a request.</summary>
    public event Action<HidppMessage>? Notification;

    /// <summary>Optional raw traffic log for diagnostics.</summary>
    public static Action<string>? Trace { get; set; }

    /// <summary>Raised once when the underlying HID device disappears.</summary>
    public event Action? Closed;

    public static readonly HashSet<int> KnownReceivers =
    [
        0xC52B, 0xC532, // Unifying
        0xC548,         // Bolt
        0xC52F, 0xC534, 0xC535, 0xC537, // Nano
        0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, // Lightspeed
    ];

    /// <summary>Finds every Logitech HID++ endpoint currently attached.</summary>
    public static List<HidppTransport> OpenAll(Action<string>? log = null, ISet<string>? excludedPaths = null)
    {
        var candidates = DeviceList.Local.GetHidDevices(VendorLogitech).ToList();
        var longs = candidates.Where(d => SafeLength(d, input: true) == HidppMessage.LongLength &&
                                          SafeLength(d, input: false) == HidppMessage.LongLength &&
                                          IsVendorCollection(d)).ToList();
        var shorts = candidates.Where(d => SafeLength(d, input: true) == HidppMessage.ShortLength &&
                                           SafeLength(d, input: false) == HidppMessage.ShortLength &&
                                           IsVendorCollection(d)).ToList();

        var result = new List<HidppTransport>();
        foreach (var longDevice in longs)
        {
            if (excludedPaths?.Contains(longDevice.DevicePath) == true) continue;
            if (!longDevice.TryOpen(out var longStream))
            {
                log?.Invoke($"Could not open {longDevice.DevicePath}");
                continue;
            }

            var key = InterfaceKey(longDevice.DevicePath);
            var shortDevice = shorts.FirstOrDefault(s => InterfaceKey(s.DevicePath) == key);
            HidStream? shortStream = null;
            if (shortDevice != null && !shortDevice.TryOpen(out shortStream))
            {
                log?.Invoke($"Could not open short collection {shortDevice.DevicePath}");
                shortStream = null;
            }

            log?.Invoke($"Opened HID++ endpoint PID {longDevice.ProductID:X4} (short collection: {(shortStream != null ? "yes" : "no")})");
            result.Add(new HidppTransport(longDevice, longStream, shortStream));
        }

        return result;
    }

    /// <summary>
    /// Groups the per-collection device paths of one HID interface: the collection number and the
    /// final instance counter differ between collections, the rest of the path is shared.
    /// </summary>
    internal static string InterfaceKey(string devicePath)
    {
        var parts = devicePath.ToLowerInvariant().Split('#');
        if (parts.Length < 3) return devicePath.ToLowerInvariant();
        var hardware = Regex.Replace(parts[1], @"&col[0-9a-f]+", "");
        var instance = parts[2];
        var lastAmp = instance.LastIndexOf('&');
        if (lastAmp > 0) instance = instance[..lastAmp];
        return hardware + "#" + instance;
    }

    private static int SafeLength(HidDevice device, bool input)
    {
        try { return input ? device.GetMaxInputReportLength() : device.GetMaxOutputReportLength(); }
        catch { return -1; }
    }

    private static bool IsVendorCollection(HidDevice device)
    {
        try
        {
            var descriptor = device.GetReportDescriptor();
            return descriptor.DeviceItems.Any(item => item.Usages.GetAllValues().Any(u => (u >> 16) >= 0xFF00));
        }
        catch
        {
            // Some stacks refuse to hand out descriptors; fall back to the report-length heuristic.
            return true;
        }
    }

    public async Task<HidppMessage> RequestAsync(HidppMessage request, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<HidppMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingSync)
                _pending = new Pending(request.DeviceIndex, request.FeatureIndex, request.FunctionAndSoftwareId, completion);

            Trace?.Invoke($"-> {request}");
            Write(request);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout ?? DefaultTimeout);
            try
            {
                return await completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No reply from device {request.DeviceIndex:X2} to {request}.");
            }
        }
        finally
        {
            lock (_pendingSync) _pending = null;
            _requestLock.Release();
        }
    }

    /// <summary>Sends a report without waiting for a reply.</summary>
    public void Write(HidppMessage message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (message.IsLong)
        {
            _long.Write(message.Raw);
        }
        else if (_short != null)
        {
            _short.Write(message.Raw);
        }
        else
        {
            throw new HidppException("This endpoint has no short HID++ collection.");
        }
    }

    private void StartReader(HidStream stream, int length)
    {
        var thread = new Thread(() => ReadLoop(stream, length))
        {
            IsBackground = true,
            Name = $"HID++ reader {length}",
        };
        thread.Start();
    }

    private void ReadLoop(HidStream stream, int length)
    {
        var buffer = new byte[Math.Max(length, stream.Device.GetMaxInputReportLength())];
        while (!_disposed)
        {
            int read;
            try
            {
                read = stream.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception) when (_disposed)
            {
                return;
            }
            catch (Exception)
            {
                OnClosed();
                return;
            }

            if (read < 4) continue;
            var copy = buffer.AsSpan(0, read).ToArray();
            if (copy[0] != HidppMessage.ShortReportId && copy[0] != HidppMessage.LongReportId) continue;
            Dispatch(new HidppMessage(copy));
        }
    }

    private void Dispatch(HidppMessage message)
    {
        Trace?.Invoke($"<- {message}");
        lock (_pendingSync)
        {
            var pending = _pending;
            if (pending != null && message.DeviceIndex == pending.DeviceIndex)
            {
                var raw = message.Raw;
                if (raw[2] == pending.Match2 && raw[3] == pending.Match3)
                {
                    pending.Completion.TrySetResult(message);
                    return;
                }

                // HID++ 2.0 error: [11, dev, FF, featureIndex, func|sw, code]
                // HID++ 1.0 error: [10, dev, 8F, subId, address, code]
                var isError = (raw[2] == HidppMessage.Hidpp20ErrorIndex && message.IsLong) ||
                              raw[2] == HidppMessage.Hidpp10ErrorSubId;
                if (isError && raw.Length >= 6 && raw[3] == pending.Match2 && raw[4] == pending.Match3)
                {
                    pending.Completion.TrySetException(new HidppException(
                        $"Device {raw[1]:X2} returned HID++ {(raw[2] == 0x8F ? "1.0" : "2.0")} error {raw[5]:X2}", raw[5]));
                    return;
                }
            }
        }

        try { Notification?.Invoke(message); }
        catch { /* never let a subscriber kill the reader */ }
    }

    private int _closedRaised;

    private void OnClosed()
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) != 0) return;
        lock (_pendingSync) _pending?.Completion.TrySetException(new HidppException("Device was disconnected."));
        try { Closed?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _long.Dispose(); } catch { }
        try { _short?.Dispose(); } catch { }
        lock (_pendingSync) _pending?.Completion.TrySetException(new ObjectDisposedException(nameof(HidppTransport)));
    }
}
