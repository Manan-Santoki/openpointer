using System.Collections.Concurrent;
using OpenPointer.Config;
using OpenPointer.Hidpp;
using OpenPointer.Platform;

namespace OpenPointer.Engine;

/// <summary>
/// Owns HID++ connections and the shared Windows mouse input backend: finds mice across brands,
/// switches diversion when the focused app changes, and turns device events into actions.
/// </summary>
public sealed class DeviceManager : IDeviceControl, IDisposable
{
    private const int LowBatteryPercent = 15;
    private static readonly TimeSpan BatteryInterval = TimeSpan.FromMinutes(2);

    private readonly ConfigStore _config;
    private readonly List<HidppTransport> _transports = [];
    private readonly ConcurrentDictionary<(HidppTransport, byte), ConnectedMouse> _mice = new();
    private readonly ConcurrentDictionary<string, ConnectedMouse> _windowsMice = new(StringComparer.OrdinalIgnoreCase);
    private sealed record TransportIdentity(Guid? ContainerId, IReadOnlyDictionary<byte, string> PairedMice);
    private readonly ConcurrentDictionary<HidppTransport, TransportIdentity> _identities = new();
    private readonly ConcurrentDictionary<(HidppTransport, byte), SemaphoreSlim> _probeLocks = new();
    private readonly BlockingCollection<Action> _actionQueue = new();
    private readonly Thread _actionThread;
    private WindowsMouseHook? _windowsHook;
    private readonly SeenButtonStore _seenButtons = new();

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
        _actionThread = new Thread(ActionLoop) { IsBackground = true, Name = "OpenPointer actions" };
        _actionThread.Start();
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
            if (value)
            {
                _windowsHook?.Cancel();
                foreach (var mouse in Mice) QueueAction(() => CancelActions(mouse));
            }
            _ = ReapplyAllAsync(settingsChanged: false);
            StateChanged?.Invoke();
        }
    }

    private volatile bool _paused;

    /// <summary>The focused context profiles are applied for.</summary>
    public FocusContext Focus => _focus;

    public IReadOnlyList<ConnectedMouse> Mice => _mice.Values.Concat(_windowsMice.Values)
        .Where(m => m.WindowsDevice?.IsVisible(_config.Current.General) != false)
        .OrderBy(m => m.IsStandardMouse).ThenBy(m => m.Name).ToList();

    public bool WindowsRemappingAvailable => _windowsHook != null;

    /// <summary>Raised (on a worker thread) when a device appears, disappears or changes state.</summary>
    public event Action? StateChanged;

    public event Action<ConnectedMouse>? LowBattery;

    public void Start()
    {
        if (!_passive)
        {
            _router = new WindowsMouseRouter(ResolveWindowsButton, OnWindowsInput);
            var hook = new WindowsMouseHook(_router,
                (path, button) => QueueAction(() => OnWindowsButtonSeen(path, button)));
            try
            {
                hook.Start();
                _windowsHook = hook;
                Log.Info($"Windows mouse remapping started (shared standard button mappings; per-mouse button detection {(hook.ObservesDevices ? "on" : "off")})");
            }
            catch (Exception ex) { hook.Dispose(); Log.Error("Starting Windows mouse remapping", ex); }
            StartTouchpad();
        }
        _ = Task.Run(MonitorLoopAsync);
    }

    private WindowsMouseRouter? _router;
    private TouchpadMonitor? _touchpad;
    private readonly TouchpadRecognizer _recognizer = new();
    private ConnectedMouse? _touchpadMouse;
    private (int X, int Y) _touchCursor;
    private WindowsTouchpadSettings _touchpadSettings = WindowsTouchpadSettings.Default;
    private long _touchpadSettingsRead = long.MinValue / 2;

    /// <summary>The precision touchpad's physical size, once its first report arrives.</summary>
    public (double Width, double Height)? TouchpadSize { get; private set; }

    public bool HasTouchpad => _touchpadMouse != null;

    /// <summary>Raised on the touchpad thread for every frame; the Touchpad page draws the fingers.</summary>
    public event Action<TouchFrame>? TouchpadFrame;

    /// <summary>Raised when a touchpad gesture runs its action.</summary>
    public event Action<string>? TouchpadGestureFired;

    /// <summary>The user's Windows touchpad gesture settings, re-read at most every few seconds.</summary>
    public WindowsTouchpadSettings TouchpadSettings
    {
        get
        {
            if (Environment.TickCount64 - _touchpadSettingsRead > 3000)
            {
                _touchpadSettings = WindowsTouchpadSettings.Read();
                _touchpadSettingsRead = Environment.TickCount64;
            }
            return _touchpadSettings;
        }
    }

    private void StartTouchpad()
    {
        var monitor = new TouchpadMonitor();
        monitor.DeviceFound += (path, _, size) =>
        {
            TouchpadSize = size;
            if (size is { Width: > 0, Height: > 0 } s) _recognizer.Aspect = s.Width / s.Height;
            _touchpadMouse = new ConnectedMouse(new WindowsMouseInfo(path, "Touchpad", 0, "Built-in", IsTouchpad: true));
            Log.Info($"Precision touchpad found{(size is { } m ? $" ({m.Width:F0} x {m.Height:F0} mm)" : "")}");
            StateChanged?.Invoke();
        };
        monitor.Frame += frame =>
        {
            TouchpadFrame?.Invoke(frame);
            _recognizer.Feed(frame.Contacts, frame.Tick, frame.ButtonDown);
            if (_pointerHeld) _router?.HoldPointerUntil(Environment.TickCount64 + 250);
        };
        _recognizer.TouchStarted += () =>
        {
            _touchId++;
            GetCursorPos(out var cursor);
            _touchCursor = (cursor.X, cursor.Y);
            var settings = TouchpadSettings;
            _recognizer.CornersNeedHold = settings.CornersNeedHold;
            _recognizer.Active = ActiveGestures(settings);
        };
        _recognizer.PointerLock += on =>
        {
            _pointerHeld = on;
            if (_router == null) return;
            _router.HoldPointerUntil(on ? Environment.TickCount64 + 250 : 0);
            // The finger moved the pointer before the gesture was recognized; put it back.
            if (on) SetCursorPos(_touchCursor.X, _touchCursor.Y);
        };
        _recognizer.Fired += key =>
        {
            var touch = _touchId;
            QueueAction(() => OnTouchpadGesture(key, touch));
        };
        try
        {
            monitor.Start();
            _touchpad = monitor;
        }
        catch (Exception ex) { monitor.Dispose(); Log.Error("Starting touchpad input", ex); }
    }

    /// <summary>Gestures with an action in the focused app that Windows doesn't already use.</summary>
    private HashSet<string> ActiveGestures(WindowsTouchpadSettings settings)
    {
        var config = _config.Current;
        if (_paused || !config.General.DeveloperMode || !config.General.ShowTouchpads) return [];
        var context = FocusTracker.Snapshot();
        return TouchpadGestures.All.Where(g => settings.Owner(g.Key) == null &&
            ContextMatcher.ResolveButton(config, context, g.Key) is not (NativeAction or NoneAction)).Select(g => g.Key).ToHashSet();
    }

    private int _touchId;
    private volatile bool _pointerHeld;
    private (int Touch, Dictionary<string, (int Count, long Last)> Keys) _firedInTouch = (-1, []);

    /// <summary>Fastest a repeating gesture runs its action, and the most times one touch may run it.</summary>
    internal const int RepeatIntervalMs = 80, MaxRepeatsPerTouch = 40;

    /// <summary>Whether a gesture may run its action again in the same touch.</summary>
    internal static bool MayRepeat(ActionSpec action, int count, long sinceLast) =>
        count == 0 || action is (KeysAction or ScrollAction) && count < MaxRepeatsPerTouch && sinceLast >= RepeatIntervalMs;

    private void OnTouchpadGesture(string key, int touch)
    {
        if (_touchpadMouse is not { } mouse || TouchpadSettings.Owner(key) != null) return;
        var action = ContextMatcher.ResolveButton(_config.Current, FocusTracker.Snapshot(), key);
        if (action is NativeAction or NoneAction) return;
        // Edge slides and circles repeat; that suits keys (volume, brightness) and scrolling, but an
        // app, macro or typed text runs once per touch.
        if (_firedInTouch.Touch != touch) _firedInTouch = (touch, []);
        var now = Environment.TickCount64;
        var (count, last) = _firedInTouch.Keys.GetValueOrDefault(key);
        if (!MayRepeat(action, count, now - last)) return;
        _firedInTouch.Keys[key] = (count + 1, now);
        LogPress(TouchpadGestures.Find(key) is { } gesture ? $"Touchpad {gesture.Group.ToLowerInvariant()}: {gesture.Label.ToLowerInvariant()}" : key, 1, action);
        Runner.Fire(mouse, action);
        TouchpadGestureFired?.Invoke(key);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private WindowsButtonBinding? ResolveWindowsButton(ushort cid)
    {
        if (_paused || _stop.IsCancellationRequested) return null;
        var button = ButtonNames.FromCid(cid);
        var mouse = _windowsMice.Values.FirstOrDefault(m => m.Online && m.WindowsDevice!.IsVisible(_config.Current.General) && m.Buttons.Contains(button));
        if (mouse == null) return null;
        return WindowsButtonBinding.Resolve(mouse, _config.Current, FocusTracker.Snapshot(), cid);
    }

    private void OnWindowsButtonSeen(string path, string button)
    {
        if (!_windowsMice.TryGetValue(path, out var mouse) || mouse.SeenButtons is not { } seen || seen.Contains(button)) return;
        mouse.SeenButtons = new HashSet<string>(seen) { button };
        _seenButtons.Add(mouse.WindowsDevice!, button);
        Log.Info($"{mouse.Name}: {ButtonNames.Label(button)} pressed for the first time");
        StateChanged?.Invoke();
    }

    private void OnWindowsInput(WindowsMouseEvent input) => QueueAction(() =>
    {
        switch (input)
        {
            case WindowsMouseEvent.Down down:
                if (down.Mouse.Online) OnButtonDown(down.Mouse, down.Cid, down.Binding.Presses);
                break;
            case WindowsMouseEvent.Up up: OnButtonUp(up.Mouse, up.Cid); break;
            case WindowsMouseEvent.Move move: Runner.Move(move.Mouse, move.Dx, move.Dy); break;
            case WindowsMouseEvent.Cancel cancel: CancelActions(cancel.Mouse); break;
        }
    });

    private void QueueAction(Action action)
    {
        try { _actionQueue.TryAdd(action); }
        catch (InvalidOperationException) { } // Shutdown raced an input notification.
    }

    private void CancelActions(ConnectedMouse mouse)
    {
        foreach (var key in _counters.Keys.Where(k => k.Item1 == mouse).ToArray())
        {
            _counters[key].Reset();
            _counters.Remove(key);
        }
        Runner.ReleaseAll(mouse, cancel: true);
    }

    private void ScanWindowsMice()
    {
        if (_stop.IsCancellationRequested) return;
        IReadOnlyList<WindowsMouseInfo> snapshot;
        try { snapshot = WindowsMouseDiscovery.Enumerate(); }
        catch (System.ComponentModel.Win32Exception ex) { Log.Error("Windows mouse discovery", ex); return; }
        var found = snapshot.Where(info => !IsHandledByHidpp(info)).Select(WithReceiverName).ToList();
        var paths = found.Select(m => m.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var key in _windowsMice.Keys.Where(p => !paths.Contains(p)))
        {
            if (!_windowsMice.TryRemove(key, out var removed)) continue;
            removed.Online = false;
            _windowsHook?.Cancel(removed);
            QueueAction(() => CancelActions(removed));
            changed = true;
        }
        foreach (var info in found)
        {
            if (_stop.IsCancellationRequested) return;
            if (_windowsMice.TryGetValue(info.DevicePath, out var existing))
            {
                if (existing.WindowsDevice != info)
                {
                    existing.WindowsDevice = info;
                    Log.Info($"Identified Windows mouse as {info.Name} ({info.Connection})");
                    changed = true;
                }
                continue;
            }
            // Without raw input, presses can't be attributed to a mouse, so every reported button is shown.
            var mouse = new ConnectedMouse(info) { SeenButtons = _windowsHook?.ObservesDevices == true ? _seenButtons.For(info) : null };
            if (!_windowsMice.TryAdd(info.DevicePath, mouse)) continue;
            var reported = info.Buttons.Count == 0 ? "none" : string.Join(", ", info.Buttons);
            var waiting = mouse.UnconfirmedButtons.Count == 0 ? "" : $"; {string.Join(", ", mouse.UnconfirmedButtons)} shown after the first press";
            Log.Info($"Connected {info.Name} via Windows mouse input ({info.Connection}, extra buttons reported: {reported}{waiting})");
            changed = true;
        }
        if (changed) StateChanged?.Invoke();
    }

    private bool MatchesTransport(WindowsMouseInfo info, HidppTransport transport) =>
        info.MatchesLogitechDevice(transport.ProductId, _identities.GetValueOrDefault(transport)?.ContainerId);

    private bool IsHandledByHidpp(WindowsMouseInfo info) =>
        _mice.Values.Any(m => MatchesTransport(info, m.Device!.Device.Transport));

    private WindowsMouseInfo WithReceiverName(WindowsMouseInfo info)
    {
        var receiver = _identities.FirstOrDefault(p => p.Key.IsReceiver && MatchesTransport(info, p.Key));
        if (receiver.Value == null) return info;
        var names = receiver.Value.PairedMice.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return names.Length == 0 ? info : info with
        {
            Name = string.Join(", ", names),
            Connection = receiver.Key.ProductId == 0xC548 ? "Logi Bolt receiver" : info.Connection,
        };
    }

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
                ScanWindowsMice();
                await ScanAsync().ConfigureAwait(false);

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
        HashSet<string> existing;
        lock (_transportSync) existing = _transports.Select(t => t.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var opened = HidppTransport.OpenAll(Log.Info, existing);
        foreach (var transport in opened)
        {
            lock (_transportSync)
            {
                if (_stop.IsCancellationRequested) { transport.Dispose(); continue; }
                _transports.Add(transport);
            }
            transport.Closed += () => OnTransportClosed(transport);
            transport.Notification += message => OnNotification(transport, message);
            await DeviceDiscovery.EnableReceiverNotificationsAsync(transport).ConfigureAwait(false);
            _identities[transport] = new(WindowsMouseDiscovery.ReadContainerId(transport.DevicePath),
                await DeviceDiscovery.ReadReceiverMouseNamesAsync(transport).ConfigureAwait(false));
        }

        // An opened receiver is not evidence that its mouse answered the first probe. Retry
        // missing/offline slots so startup races and missed notifications do not strand it.
        HidppTransport[] active;
        lock (_transportSync) active = _transports.ToArray();
        foreach (var transport in active)
        {
            var paired = _identities.GetValueOrDefault(transport)?.PairedMice;
            var slots = paired is { Count: > 0 } ? paired.Keys : DeviceDiscovery.SlotsOf(transport);
            foreach (var slot in slots)
            {
                if (_stop.IsCancellationRequested) break;
                if (_mice.TryGetValue((transport, slot), out var mouse))
                {
                    if (!mouse.Online) await OnLinkChangedAsync(transport, slot, true).ConfigureAwait(false);
                }
                else await TryAddAsync(transport, slot).ConfigureAwait(false);
            }
        }

        if (opened.Count > 0) { ScanWindowsMice(); StateChanged?.Invoke(); }
    }

    private async Task TryAddAsync(HidppTransport transport, byte slot)
    {
        var gate = _probeLocks.GetOrAdd((transport, slot), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_stop.IsCancellationRequested && !_mice.ContainsKey((transport, slot)))
                await ProbeMouseAsync(transport, slot).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task ProbeMouseAsync(HidppTransport transport, byte slot)
    {
        try
        {
            var device = await DeviceDiscovery.ProbeSlotAsync(transport, slot, Log.Info).ConfigureAwait(false);
            if (device == null || _stop.IsCancellationRequested) return;
            var mouse = new MouseDevice(device);
            await mouse.InitializeAsync().ConfigureAwait(false);
            if (_stop.IsCancellationRequested) return;
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
            ScanWindowsMice();
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error($"Setting up device at slot {slot}", ex);
        }
    }

    private void Wire(ConnectedMouse mouse)
    {
        var device = mouse.Device!;
        device.ButtonDown += cid => QueueAction(() => OnButtonDown(mouse, cid));
        device.ButtonUp += cid => QueueAction(() => OnButtonUp(mouse, cid));
        device.RawMove += (dx, dy) => Runner.Move(mouse, dx, dy);
        device.ThumbWheel += (rotation, phase) => QueueAction(() => OnThumbWheel(mouse, rotation, phase));
    }

    private void OnButtonDown(ConnectedMouse mouse, ushort cid, ActionSpec?[]? resolved = null)
    {
        if (_stop.IsCancellationRequested || !mouse.Online) return;
        if (_paused && resolved == null)
        {
            Runner.Press(mouse, cid, new NativeAction());
            return;
        }

        var context = FocusTracker.Snapshot();
        var button = ButtonNames.FromCid(cid);
        var presses = resolved ?? ContextMatcher.ResolvePresses(_config.Current, context, button);
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
            QueueAction(() => Run(mouse, cid, counter.Expire(generation)));
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
            mouse.Device!.Device.HandleNotification(message);
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
            QueueAction(() => CancelActions(mouse));
            mouse.Device!.ReleaseAll();
            Log.Info($"{mouse.Name} went offline");
            StateChanged?.Invoke();
            return;
        }

        // The device forgets temporary diversion when it sleeps or power-cycles, so redo everything.
        await Task.Delay(500).ConfigureAwait(false);
        try
        {
            await mouse.Device!.InitializeAsync().ConfigureAwait(false);
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
            if (_mice.TryRemove(key, out var mouse)) QueueAction(() => CancelActions(mouse));
        }

        lock (_transportSync) _transports.Remove(transport);
        _identities.TryRemove(transport, out _);
        transport.Dispose();
        StateChanged?.Invoke();
        Rescan();
    }

    /// <summary>Reads state and pushes config to a freshly connected mouse.</summary>
    private async Task ConfigureAsync(ConnectedMouse mouse)
    {
        var device = mouse.Device!;
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
        var device = mouse.Device!;

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
            foreach (var control in mouse.Device!.Controls.Where(c => c.IsDivertable && c.Cid is not (0x50 or 0x51)))
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
        var battery = await Safe(mouse.Device!.GetBatteryAsync).ConfigureAwait(false);
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
        if (mouse.Device == null) return null;
        mouse.Dpi = await Safe(() => mouse.Device.GetDpiAsync()).ConfigureAwait(false) ?? mouse.Dpi;
        return mouse.Dpi?.Current;
    }

    public Task SetDpiAsync(ConnectedMouse mouse, int dpi, bool persist) => SetDpiAsync(mouse, dpi, persist, notify: true);

    /// <param name="notify">False for live previews, so the UI is not rebuilt mid-drag.</param>
    public async Task SetDpiAsync(ConnectedMouse mouse, int dpi, bool persist, bool notify)
    {
        if (mouse.Device == null) throw new NotSupportedException("Hardware DPI is not available for this mouse. Use its DPI button or Windows pointer settings.");
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
        await mouse.Device!.SetDpiAsync(snapped).ConfigureAwait(false);
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
        if (mouse.Device == null) throw new NotSupportedException("This mouse does not expose wheel mode control.");
        var state = await Safe(mouse.Device.GetSmartShiftAsync).ConfigureAwait(false);
        if (state == null) return;
        var next = state.Mode == WheelRatchetMode.Ratchet ? WheelRatchetMode.Freespin : WheelRatchetMode.Ratchet;
        await mouse.Device.SetSmartShiftAsync(next, null).ConfigureAwait(false);
        mouse.SmartShift = state with { Mode = next };
        StateChanged?.Invoke();
    }

    public Task SwitchHostAsync(ConnectedMouse mouse, int host) => mouse.Device?.SwitchHostAsync(host - 1)
        ?? Task.FromException(new NotSupportedException("Use the mouse's physical Bluetooth channel switch."));

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
                try { await mouse.Device!.SetDivertAsync(cid, false).ConfigureAwait(false); } catch { }
            }

            if (mouse.AppliedThumb is { Diverted: true } thumb)
            {
                try { await mouse.Device!.SetThumbWheelAsync(false, thumb.Inverted).ConfigureAwait(false); } catch { }
            }
        }).ToArray();
        Task.WaitAll(tasks, TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _windowsHook?.Dispose();
        _windowsHook = null;
        _touchpad?.Dispose();
        _touchpad = null;
        foreach (var mouse in Mice) QueueAction(() => CancelActions(mouse));
        RestoreDevices();
        _actionQueue.CompleteAdding();
        if (Thread.CurrentThread != _actionThread) _actionThread.Join(TimeSpan.FromSeconds(3));
        lock (_transportSync)
        {
            foreach (var t in _transports) t.Dispose();
            _transports.Clear();
        }
    }
}
