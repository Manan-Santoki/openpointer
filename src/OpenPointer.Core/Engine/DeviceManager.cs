using System.Collections.Concurrent;
using OpenPointer.Config;
using OpenPointer.Hidpp;
using OpenPointer.Platform;

namespace OpenPointer.Engine;

/// <summary>
/// Owns every HID++ connection: finds mice, keeps them configured across sleep and reconnects,
/// switches diversion when the focused app changes, and turns device events into actions.
/// </summary>
public sealed class DeviceManager : IDeviceControl, IDisposable
{
    private const int LowBatteryPercent = 15;
    private static readonly TimeSpan BatteryInterval = TimeSpan.FromMinutes(2);

    private readonly ConfigStore _config;
    private readonly List<HidppTransport> _transports = [];
    private readonly ConcurrentDictionary<(HidppTransport, byte), ConnectedMouse> _mice = new();
    private readonly BlockingCollection<Action> _actionQueue = new();

    /// <summary>Double and triple press tracking per button; used only on the action thread.</summary>
    private readonly Dictionary<(ConnectedMouse, ushort), PressCounter> _counters = [];
    private readonly SemaphoreSlim _scanSignal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _transportSync = new();
    private FocusContext _focus = FocusContext.Unknown;
    private readonly bool _passive;

    /// <param name="passive">Read device state only; never divert controls or change settings
    /// (used for documentation captures next to a running instance).</param>
    public DeviceManager(ConfigStore config, bool passive = false)
    {
        _config = config;
        _passive = passive;
        Runner = new ActionRunner(this);
        _config.Changed += config => _ = ReapplyAllAsync(settingsChanged: true);
        new Thread(ActionLoop) { IsBackground = true, Name = "OpenPointer actions" }.Start();
    }

    public ActionRunner Runner { get; }

    /// <summary>When paused every control is handed back to the device; nothing is remapped.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            _ = ReapplyAllAsync(settingsChanged: false);
            StateChanged?.Invoke();
        }
    }

    private volatile bool _paused;

    /// <summary>The focused context profiles are applied for.</summary>
    public FocusContext Focus => _focus;

    public IReadOnlyList<ConnectedMouse> Mice => _mice.Values.OrderBy(m => m.Name).ToList();

    /// <summary>Raised (on a worker thread) when a device appears, disappears or changes state.</summary>
    public event Action? StateChanged;

    public event Action<ConnectedMouse>? LowBattery;

    public void Start() => _ = Task.Run(MonitorLoopAsync);

    /// <summary>Called by the UI when the foreground window changes.</summary>
    public void OnFocusChanged(FocusContext context)
    {
        _focus = context;
        _ = ReapplyAllAsync(settingsChanged: false);
    }

    /// <summary>Re-applies device settings and the focused app's profile, e.g. after discarding a preview.</summary>
    public void Reapply() => _ = ReapplyAllAsync(settingsChanged: true);

    /// <summary>Asks the monitor loop to look for devices now.</summary>
    public void Rescan() => _scanSignal.Release();

    private async Task MonitorLoopAsync()
    {
        var lastBattery = DateTime.MinValue;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                bool needScan;
                lock (_transportSync) needScan = _transports.Count == 0;
                if (needScan) await ScanAsync().ConfigureAwait(false);

                if (DateTime.UtcNow - lastBattery > BatteryInterval)
                {
                    lastBattery = DateTime.UtcNow;
                    foreach (var mouse in _mice.Values.Where(m => m.Online)) await RefreshBatteryAsync(mouse).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Device monitor", ex);
            }

            try { await _scanSignal.WaitAsync(TimeSpan.FromSeconds(5), _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ScanAsync()
    {
        var opened = HidppTransport.OpenAll(Log.Info);
        foreach (var transport in opened)
        {
            lock (_transportSync) _transports.Add(transport);
            transport.Closed += () => OnTransportClosed(transport);
            transport.Notification += message => OnNotification(transport, message);
            await DeviceDiscovery.EnableReceiverNotificationsAsync(transport).ConfigureAwait(false);
            foreach (var slot in DeviceDiscovery.SlotsOf(transport))
                await TryAddAsync(transport, slot).ConfigureAwait(false);
        }

        if (opened.Count > 0) StateChanged?.Invoke();
    }

    private async Task TryAddAsync(HidppTransport transport, byte slot)
    {
        try
        {
            var device = await DeviceDiscovery.ProbeSlotAsync(transport, slot, Log.Info).ConfigureAwait(false);
            if (device == null) return;
            var mouse = new MouseDevice(device);
            await mouse.InitializeAsync().ConfigureAwait(false);
            if (!mouse.IsMouseLike)
            {
                Log.Info($"Ignoring {mouse.Name} (not a pointing device)");
                return;
            }

            var connected = new ConnectedMouse(mouse);
            Wire(connected);
            _mice[(transport, slot)] = connected;
            Log.Info($"Connected {mouse.Name} on PID {transport.ProductId:X4} slot {slot}");
            await ConfigureAsync(connected).ConfigureAwait(false);
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error($"Setting up device at slot {slot}", ex);
        }
    }

    private void Wire(ConnectedMouse mouse)
    {
        var device = mouse.Device;
        device.ButtonDown += cid => _actionQueue.Add(() => OnButtonDown(mouse, cid));
        device.ButtonUp += cid => _actionQueue.Add(() => OnButtonUp(mouse, cid));
        device.RawMove += (dx, dy) => Runner.Move(mouse, dx, dy);
        device.ThumbWheel += (rotation, phase) => _actionQueue.Add(() => OnThumbWheel(mouse, rotation, phase));
    }

    private void OnButtonDown(ConnectedMouse mouse, ushort cid)
    {
        if (_paused)
        {
            Runner.Press(mouse, cid, new NativeAction());
            return;
        }

        var context = FocusTracker.Snapshot();
        var button = ButtonNames.FromCid(cid);
        var presses = ContextMatcher.ResolvePresses(_config.Current, context, button);
        var counter = Counter(mouse, cid);
        if (!counter.Busy && presses.Skip(1).All(p => p == null))
        {
            LogPress(button, 1, presses[0]!);
            Runner.Press(mouse, cid, presses[0]!);
            return;
        }

        var outcome = counter.Down(presses, out var wait);
        if (wait) ScheduleExpiry(mouse, cid, counter);
        Run(mouse, cid, outcome);
    }

    private void OnButtonUp(ConnectedMouse mouse, ushort cid)
    {
        if (!_counters.TryGetValue((mouse, cid), out var counter) || !counter.Busy)
        {
            Runner.Release(mouse, cid);
            return;
        }

        var outcome = counter.Up(out var wait);
        if (wait) ScheduleExpiry(mouse, cid, counter);
        Run(mouse, cid, outcome);
    }

    private PressCounter Counter(ConnectedMouse mouse, ushort cid)
    {
        if (!_counters.TryGetValue((mouse, cid), out var counter)) _counters[(mouse, cid)] = counter = new PressCounter();
        return counter;
    }

    /// <summary>Calls <see cref="PressCounter.Expire"/> on the action thread once the multi-press delay passes.</summary>
    private void ScheduleExpiry(ConnectedMouse mouse, ushort cid, PressCounter counter)
    {
        var generation = counter.Generation;
        var delay = Math.Clamp(_config.Current.General.MultiPressMs, 150, 800);
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            if (!_actionQueue.IsAddingCompleted) _actionQueue.TryAdd(() => Run(mouse, cid, counter.Expire(generation)));
        }, TaskScheduler.Default);
    }

    private void Run(ConnectedMouse mouse, ushort cid, PressOutcome? outcome)
    {
        switch (outcome)
        {
            case PressOutcome.Hold hold:
                LogPress(ButtonNames.FromCid(cid), hold.Count, hold.Action);
                Runner.Press(mouse, cid, hold.Action);
                break;
            case PressOutcome.Tap tap:
                LogPress(ButtonNames.FromCid(cid), tap.Count, tap.Action);
                Runner.Press(mouse, cid, tap.Action);
                Runner.Release(mouse, cid);
                break;
            case PressOutcome.End:
                Runner.Release(mouse, cid);
                break;
        }
    }

    private void LogPress(string button, int count, ActionSpec action)
    {
        var context = FocusTracker.Snapshot();
        var profile = ContextMatcher.FindAppProfile(_config.Current, context)?.Name ?? "All applications";
        var times = count switch { 1 => "pressed", 2 => "pressed twice", _ => $"pressed {count} times" };
        Log.Info($"{button} {times} in {context.DisplayName} [{context.ProcessName}/{context.WindowClass}] -> {profile}: {action.Describe()}");
    }

    private void OnThumbWheel(ConnectedMouse mouse, int rotation, ThumbWheelPhase phase)
    {
        var (left, right) = ContextMatcher.ResolveThumbWheel(_config.Current, FocusTracker.Snapshot());
        if (phase == ThumbWheelPhase.Start) mouse.ThumbAccumulator = 0;
        mouse.ThumbAccumulator += rotation;
        const int step = 15;
        while (Math.Abs(mouse.ThumbAccumulator) >= step)
        {
            var positive = mouse.ThumbAccumulator > 0;
            mouse.ThumbAccumulator -= positive ? step : -step;
            var action = positive ? right : left;
            if (action != null) Runner.Fire(mouse, action);
            else InputSynth.Scroll(positive ? 1 : -1, horizontal: true);
        }
    }

    private void ActionLoop()
    {
        foreach (var work in _actionQueue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { Log.Error("Action", ex); }
        }
    }

    private void OnNotification(HidppTransport transport, HidppMessage message)
    {
        if (DeviceDiscovery.TryParseConnection(message, out var slot, out var linked))
        {
            _ = Task.Run(() => OnLinkChangedAsync(transport, slot, linked));
            return;
        }

        if (_mice.TryGetValue((transport, message.DeviceIndex), out var mouse))
            mouse.Device.Device.HandleNotification(message);
    }

    private async Task OnLinkChangedAsync(HidppTransport transport, byte slot, bool linked)
    {
        if (!_mice.TryGetValue((transport, slot), out var mouse))
        {
            if (linked)
            {
                await Task.Delay(500).ConfigureAwait(false);
                await TryAddAsync(transport, slot).ConfigureAwait(false);
            }

            return;
        }

        if (!linked)
        {
            if (!mouse.Online) return;
            mouse.Online = false;
            Runner.ReleaseAll(mouse);
            mouse.Device.ReleaseAll();
            Log.Info($"{mouse.Name} went offline");
            StateChanged?.Invoke();
            return;
        }

        // The device forgets temporary diversion when it sleeps or power-cycles, so redo everything.
        await Task.Delay(500).ConfigureAwait(false);
        try
        {
            await mouse.Device.InitializeAsync().ConfigureAwait(false);
            mouse.Online = true;
            mouse.AppliedDivert.Clear();
            mouse.AppliedThumb = null;
            await ConfigureAsync(mouse).ConfigureAwait(false);
            Log.Info($"{mouse.Name} is back online");
        }
        catch (Exception ex)
        {
            Log.Error($"Reconnecting {mouse.Name}", ex);
        }

        StateChanged?.Invoke();
    }

    private void OnTransportClosed(HidppTransport transport)
    {
        Log.Info($"Receiver/device PID {transport.ProductId:X4} was removed");
        foreach (var key in _mice.Keys.Where(k => k.Item1 == transport).ToList())
        {
            if (_mice.TryRemove(key, out var mouse)) Runner.ReleaseAll(mouse);
        }

        lock (_transportSync) _transports.Remove(transport);
        transport.Dispose();
        StateChanged?.Invoke();
        Rescan();
    }

    /// <summary>Reads state and pushes config to a freshly connected mouse.</summary>
    private async Task ConfigureAsync(ConnectedMouse mouse)
    {
        var device = mouse.Device;
        mouse.Battery = await Safe(device.GetBatteryAsync).ConfigureAwait(false);
        mouse.Dpi = await Safe(() => device.GetDpiAsync()).ConfigureAwait(false);
        mouse.SmartShift = await Safe(device.GetSmartShiftAsync).ConfigureAwait(false);
        mouse.Hosts = await Safe(device.GetHostsAsync).ConfigureAwait(false);
        mouse.WheelFlags = await Safe(device.GetWheelModeAsync).ConfigureAwait(false);
        mouse.HasThumbWheel = await device.Device.HasFeatureAsync(FeatureId.ThumbWheel).ConfigureAwait(false);
        await ApplySettingsAsync(mouse).ConfigureAwait(false);
        await ApplyContextAsync(mouse).ConfigureAwait(false);
        CheckBattery(mouse);
    }

    private async Task ReapplyAllAsync(bool settingsChanged)
    {
        foreach (var mouse in _mice.Values.Where(m => m.Online))
        {
            try
            {
                if (settingsChanged) await ApplySettingsAsync(mouse).ConfigureAwait(false);
                await ApplyContextAsync(mouse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error($"Applying settings to {mouse.Name}", ex);
            }
        }

        if (settingsChanged) StateChanged?.Invoke();
    }

    private async Task ApplySettingsAsync(ConnectedMouse mouse)
    {
        if (_passive) return;
        var settings = _config.Current.SettingsFor(mouse.Name);
        var device = mouse.Device;

        if (mouse.SmartShift != null && (settings.SmartShiftEnabled != null || settings.SmartShiftThreshold != null))
        {
            var enabled = settings.SmartShiftEnabled ?? mouse.SmartShift.Enabled;
            var threshold = enabled ? Math.Clamp(settings.SmartShiftThreshold ?? 10, 1, 50) : 0xFF;
            if (threshold != mouse.SmartShift.AutoDisengage)
            {
                await device.SetSmartShiftAsync(WheelRatchetMode.Ratchet, threshold).ConfigureAwait(false);
                mouse.SmartShift = await Safe(device.GetSmartShiftAsync).ConfigureAwait(false);
            }
        }

        if (mouse.SmartShift != null && settings.WheelMode is "ratchet" or "freespin")
        {
            var mode = settings.WheelMode == "ratchet" ? WheelRatchetMode.Ratchet : WheelRatchetMode.Freespin;
            if (mouse.SmartShift.Mode != mode)
            {
                await device.SetSmartShiftAsync(mode, null).ConfigureAwait(false);
                mouse.SmartShift = mouse.SmartShift with { Mode = mode };
            }
        }

        if (mouse.WheelFlags is { } flags && (settings.InvertScroll != null || settings.SmoothScroll != null))
        {
            var wanted = flags;
            if (settings.InvertScroll is { } invert) wanted = invert ? wanted | 0x04 : wanted & ~0x04;
            if (settings.SmoothScroll is { } smooth) wanted = smooth ? wanted | 0x02 : wanted & ~0x02;
            if (wanted != flags)
            {
                await device.SetWheelModeAsync(wanted).ConfigureAwait(false);
                mouse.WheelFlags = wanted;
            }
        }
    }

    /// <summary>Diverts exactly the controls the focused app remaps, and applies per-app DPI.</summary>
    private async Task ApplyContextAsync(ConnectedMouse mouse)
    {
        if (_passive) return;
        await mouse.ApplyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var config = _config.Current;
            var context = _focus;
            foreach (var control in mouse.Device.Controls.Where(c => c.IsDivertable && c.Cid is not (0x50 or 0x51)))
            {
                var presses = _paused ? new ActionSpec?[] { new NativeAction() } : ContextMatcher.ResolvePresses(config, context, ButtonNames.FromCid(control.Cid));
                var action = presses[0];
                var wanted = (Divert: presses.Any(p => p is not null and not NativeAction), Raw: action is GestureAction or ActionRingAction && control.SupportsRawXY);
                if (control.Flags.HasFlag(ControlFlags.Virtual)) wanted = (false, false);
                var known = mouse.AppliedDivert.TryGetValue(control.Cid, out var applied);
                if (known && applied == wanted) continue;
                if (!known && !wanted.Divert)
                {
                    // Unknown state at startup (a previous run may have crashed): force native.
                    await mouse.Device.SetDivertAsync(control.Cid, false).ConfigureAwait(false);
                    mouse.AppliedDivert[control.Cid] = wanted;
                    continue;
                }

                await mouse.Device.SetDivertAsync(control.Cid, wanted.Divert, wanted.Raw).ConfigureAwait(false);
                mouse.AppliedDivert[control.Cid] = wanted;
                Log.Info($"{ButtonNames.FromCid(control.Cid)} {(wanted.Divert ? "remapped" : "native")} for {context.DisplayName}");
            }

            if (mouse.HasThumbWheel)
            {
                var (left, right) = ContextMatcher.ResolveThumbWheel(config, context);
                var thumb = (Diverted: !_paused && (left != null || right != null),
                             Inverted: config.SettingsFor(mouse.Name).InvertThumbWheel ?? false);
                if (mouse.AppliedThumb != thumb)
                {
                    await mouse.Device.SetThumbWheelAsync(thumb.Diverted, thumb.Inverted).ConfigureAwait(false);
                    mouse.AppliedThumb = thumb;
                }
            }

            var dpi = ContextMatcher.ResolveDpi(config, context) ?? config.SettingsFor(mouse.Name).Dpi;
            if (dpi is { } target && mouse.Dpi != null && Snap(mouse, target) != mouse.Dpi.Current)
                await SetDpiCoreAsync(mouse, target).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HidppException or TimeoutException)
        {
            Log.Warn($"Could not apply profile to {mouse.Name}: {ex.Message}");
        }
        finally
        {
            mouse.ApplyLock.Release();
        }
    }

    private async Task RefreshBatteryAsync(ConnectedMouse mouse)
    {
        var battery = await Safe(mouse.Device.GetBatteryAsync).ConfigureAwait(false);
        if (battery == null || battery == mouse.Battery) return;
        mouse.Battery = battery;
        CheckBattery(mouse);
        StateChanged?.Invoke();
    }

    private void CheckBattery(ConnectedMouse mouse)
    {
        if (mouse.Battery is not { } battery) return;
        if (battery.Charging || battery.Percent > LowBatteryPercent + 5)
        {
            mouse.LowBatteryNotified = false;
        }
        else if (battery.Percent <= LowBatteryPercent && !mouse.LowBatteryNotified)
        {
            mouse.LowBatteryNotified = true;
            LowBattery?.Invoke(mouse);
        }
    }

    // IDeviceControl

    public async Task<int?> GetDpiAsync(ConnectedMouse mouse)
    {
        mouse.Dpi = await Safe(() => mouse.Device.GetDpiAsync()).ConfigureAwait(false) ?? mouse.Dpi;
        return mouse.Dpi?.Current;
    }

    public Task SetDpiAsync(ConnectedMouse mouse, int dpi, bool persist) => SetDpiAsync(mouse, dpi, persist, notify: true);

    /// <param name="notify">False for live previews, so the UI is not rebuilt mid-drag.</param>
    public async Task SetDpiAsync(ConnectedMouse mouse, int dpi, bool persist, bool notify)
    {
        await SetDpiCoreAsync(mouse, dpi).ConfigureAwait(false);
        if (persist)
        {
            var snapped = Snap(mouse, dpi);
            _config.Update(c =>
            {
                if (!c.Devices.TryGetValue(mouse.Name, out var settings)) c.Devices[mouse.Name] = settings = new DeviceSettings();
                settings.Dpi = snapped;
            });
        }

        if (notify) StateChanged?.Invoke();
    }

    private async Task SetDpiCoreAsync(ConnectedMouse mouse, int dpi)
    {
        var snapped = Snap(mouse, dpi);
        await mouse.Device.SetDpiAsync(snapped).ConfigureAwait(false);
        if (mouse.Dpi != null) mouse.Dpi = mouse.Dpi with { Current = snapped };
    }

    private static int Snap(ConnectedMouse mouse, int dpi)
    {
        var supported = mouse.Dpi?.Supported;
        if (supported == null || supported.Count == 0) return dpi;
        return supported.MinBy(s => Math.Abs(s - dpi));
    }

    public IReadOnlyList<int> DpiPresets(ConnectedMouse mouse)
    {
        var configured = _config.Current.SettingsFor(mouse.Name).DpiPresets;
        if (configured is { Count: > 0 }) return configured;
        var supported = mouse.Dpi?.Supported ?? [];
        if (supported.Count == 0) return [];
        int[] standard = [400, 800, 1000, 1200, 1600, 2000, 2400, 3200, 4000, 6400, 8000];
        return standard.Where(v => v >= supported[0] && v <= supported[^1]).Select(v => Snap(mouse, v)).Distinct().ToList();
    }

    public async Task SetSmartShiftAsync(ConnectedMouse mouse, bool enabled, int threshold)
    {
        _config.Update(c =>
        {
            if (!c.Devices.TryGetValue(mouse.Name, out var settings)) c.Devices[mouse.Name] = settings = new DeviceSettings();
            settings.SmartShiftEnabled = enabled;
            settings.SmartShiftThreshold = threshold;
        });
        await Task.CompletedTask;
    }

    public async Task ToggleWheelModeAsync(ConnectedMouse mouse)
    {
        var state = await Safe(mouse.Device.GetSmartShiftAsync).ConfigureAwait(false);
        if (state == null) return;
        var next = state.Mode == WheelRatchetMode.Ratchet ? WheelRatchetMode.Freespin : WheelRatchetMode.Ratchet;
        await mouse.Device.SetSmartShiftAsync(next, null).ConfigureAwait(false);
        mouse.SmartShift = state with { Mode = next };
        StateChanged?.Invoke();
    }

    public Task SwitchHostAsync(ConnectedMouse mouse, int host) => mouse.Device.SwitchHostAsync(host - 1);

    private static async Task<T?> Safe<T>(Func<Task<T?>> read) where T : class
    {
        try { return await read().ConfigureAwait(false); }
        catch (Exception ex) when (ex is HidppException or TimeoutException) { return null; }
    }

    private static async Task<int?> Safe(Func<Task<int?>> read)
    {
        try { return await read().ConfigureAwait(false); }
        catch (Exception ex) when (ex is HidppException or TimeoutException) { return null; }
    }

    /// <summary>Hands every control back to the device so buttons keep working after we exit.</summary>
    public void RestoreDevices()
    {
        if (_passive) return;
        var tasks = _mice.Values.Where(m => m.Online).Select(async mouse =>
        {
            foreach (var (cid, state) in mouse.AppliedDivert)
            {
                if (!state.Divert) continue;
                try { await mouse.Device.SetDivertAsync(cid, false).ConfigureAwait(false); } catch { }
            }

            if (mouse.AppliedThumb is { Diverted: true } thumb)
            {
                try { await mouse.Device.SetThumbWheelAsync(false, thumb.Inverted).ConfigureAwait(false); } catch { }
            }
        }).ToArray();
        Task.WaitAll(tasks, TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        _stop.Cancel();
        RestoreDevices();
        _actionQueue.CompleteAdding();
        lock (_transportSync)
        {
            foreach (var t in _transports) t.Dispose();
            _transports.Clear();
        }
    }
}
