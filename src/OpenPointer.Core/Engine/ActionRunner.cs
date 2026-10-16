using System.Diagnostics;
using OpenPointer.Config;
using OpenPointer.Platform;

namespace OpenPointer.Engine;

/// <summary>Device operations that actions can trigger, implemented by the device manager.</summary>
public interface IDeviceControl
{
    Task<int?> GetDpiAsync(ConnectedMouse mouse);
    Task SetDpiAsync(ConnectedMouse mouse, int dpi, bool persist);
    IReadOnlyList<int> DpiPresets(ConnectedMouse mouse);
    Task ToggleWheelModeAsync(ConnectedMouse mouse);
    Task SwitchHostAsync(ConnectedMouse mouse, int host);
}

/// <summary>Draws the Actions Ring; implemented by the UI.</summary>
public interface IRingPresenter
{
    void Show(ActionRingAction ring);
    void Highlight(int index);

    /// <summary>Aim position as a fraction of the ring's reach (-1..1 on each axis).</summary>
    void Aim(double x, double y);
    void Hide();
}

/// <summary>
/// Executes actions with press/release semantics. A press remembers what it started so the release
/// ends the same action even if the focused app (and therefore the profile) changed in between.
/// </summary>
public sealed class ActionRunner
{
    private readonly IDeviceControl _devices;
    private readonly Dictionary<(ConnectedMouse, ushort), Held> _held = [];
    private readonly object _sync = new();

    private abstract class Held;

    private sealed class HeldKeys(IReadOnlyList<ushort> keys) : Held
    {
        public IReadOnlyList<ushort> Keys { get; } = keys;
        public Repeater? Repeat { get; set; }
    }

    private sealed class HeldRepeat(Repeater repeat) : Held
    {
        public Repeater Repeat { get; } = repeat;
    }

    /// <summary>Re-runs an action at the user's keyboard repeat rate while a button is held.</summary>
    internal sealed class Repeater : IDisposable
    {
        private readonly object _sync = new();
        private readonly Action _tick;
        private readonly Timer _timer;
        private bool _stopped;

        public Repeater(Action tick)
        {
            _tick = tick;
            var (delay, interval) = KeyboardRepeatTiming();
            _timer = new Timer(_ => Tick(), null, delay, interval);
        }

        private void Tick()
        {
            lock (_sync)
            {
                if (_stopped) return;
                try { _tick(); } catch (Exception ex) { Log.Error("Repeat", ex); }
            }
        }

        /// <summary>Stops repeating; once this returns no further tick runs.</summary>
        public void Dispose()
        {
            lock (_sync) _stopped = true;
            _timer.Dispose();
        }

        /// <summary>Windows keyboard settings: delay 0-3 (250-1000 ms), speed 0-31 (~2.5-30 per second).</summary>
        internal static (int DelayMs, int IntervalMs) KeyboardRepeatTiming()
        {
            var delay = Platform.NativeMethods.SystemParametersInfoW(Platform.NativeMethods.SPI_GETKEYBOARDDELAY, 0, out var d, 0) ? d : 1;
            var speed = Platform.NativeMethods.SystemParametersInfoW(Platform.NativeMethods.SPI_GETKEYBOARDSPEED, 0, out var s, 0) ? s : 31;
            var perSecond = 2.5 + Math.Min(speed, 31u) * (27.5 / 31.0);
            return ((int)(Math.Min(delay, 3u) + 1) * 250, (int)Math.Round(1000 / perSecond));
        }
    }

    private sealed class HeldMouse(MouseButtonKind button) : Held
    {
        public MouseButtonKind Button { get; } = button;
    }

    private sealed class HeldGesture(GestureAction gesture) : Held
    {
        public GestureAction Gesture { get; } = gesture;
        public int Dx;
        public int Dy;
    }

    private sealed class HeldRing(ActionRingAction ring, double scale) : Held
    {
        public ActionRingAction Ring { get; } = ring;

        /// <summary>Converts device counts to counts at 1000 DPI, so the ring feels the same at any DPI.</summary>
        public double Scale { get; } = scale;
        public long Started { get; } = Environment.TickCount64;
        public double X;
        public double Y;
        public int Selected = -1;
    }

    /// <summary>Movement right after the press is the button going down, not aiming.</summary>
    private const int RingSettleMs = 120;

    /// <summary>A release this soon after the press is a click: it closes the ring without running anything.</summary>
    private const int RingClickMs = 200;

    private sealed class HeldDpiShift(int restore) : Held
    {
        public int Restore { get; } = restore;
    }

    public ActionRunner(IDeviceControl devices) => _devices = devices;

    /// <summary>Set by the UI so rings can be drawn; without it ring actions are ignored.</summary>
    public IRingPresenter? Ring { get; set; }

    /// <summary>Raised when an action fails, for user-visible error reporting.</summary>
    public event Action<string>? Failed;

    public void Press(ConnectedMouse mouse, ushort cid, ActionSpec action)
    {
        Held? held = null;
        try
        {
            switch (action)
            {
                case NativeAction:
                    // The control is diverted but this context wants native behavior (e.g. focus changed
                    // faster than diversion could be updated). Emulate what the hardware would send.
                    var native = NativeButtonFor(cid);
                    if (native != null)
                    {
                        InputSynth.MouseButton(native.Value, down: true);
                        held = new HeldMouse(native.Value);
                    }

                    break;
                case KeysAction keys:
                    var codes = KeyChord.Parse(keys.Keys);
                    InputSynth.KeyDown(codes);
                    var heldKeys = new HeldKeys(codes);
                    if (keys.ShouldRepeat)
                    {
                        // Like typematic repeat: modifiers stay down, the main key re-fires.
                        var main = codes.Where(c => !KeyChord.IsModifier(c)).ToList();
                        if (main.Count > 0)
                            heldKeys.Repeat = new Repeater(() =>
                            {
                                InputSynth.KeyUp(main);
                                InputSynth.KeyDown(main);
                            });
                    }

                    held = heldKeys;
                    break;
                case ScrollAction { Repeat: true } scroll:
                    InputSynth.Scroll(scroll.Amount, scroll.Horizontal);
                    held = new HeldRepeat(new Repeater(() => InputSynth.Scroll(scroll.Amount, scroll.Horizontal)));
                    break;
                case MouseAction m:
                    InputSynth.MouseButton(m.Button, down: true);
                    held = new HeldMouse(m.Button);
                    break;
                case GestureAction gesture:
                    held = new HeldGesture(gesture);
                    break;
                case ActionRingAction ring when Ring != null && ring.Items.Count > 0:
                    Ring.Show(ring);
                    held = new HeldRing(ring, mouse.Dpi?.Current is > 0 and var dpi ? 1000.0 / dpi : 1);
                    break;
                case DpiAction { Mode: DpiMode.Shift } shift:
                    held = StartDpiShift(mouse, shift.Value);
                    break;
                default:
                    Fire(mouse, action);
                    break;
            }
        }
        catch (Exception ex)
        {
            Report(action, ex);
        }

        if (held != null)
            lock (_sync) _held[(mouse, cid)] = held;
    }

    public void Release(ConnectedMouse mouse, ushort cid)
    {
        Held? held;
        lock (_sync)
        {
            if (!_held.Remove((mouse, cid), out held)) return;
        }

        try
        {
            switch (held)
            {
                case HeldKeys keys:
                    keys.Repeat?.Dispose();
                    InputSynth.KeyUp(keys.Keys);
                    break;
                case HeldRepeat repeat:
                    repeat.Repeat.Dispose();
                    break;
                case HeldMouse m:
                    InputSynth.MouseButton(m.Button, down: false);
                    break;
                case HeldGesture g:
                    var picked = PickGesture(g);
                    if (picked != null) Fire(mouse, picked);
                    break;
                case HeldDpiShift shift:
                    _ = _devices.SetDpiAsync(mouse, shift.Restore, persist: false);
                    break;
                case HeldRing ring:
                    Ring?.Hide();
                    if (ring.Selected >= 0 && Environment.TickCount64 - ring.Started >= RingClickMs) Fire(mouse, ring.Ring.Items[ring.Selected].Action);
                    break;
            }
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
        }
    }

    /// <summary>Accumulates raw movement for gestures in progress on this device.</summary>
    public void Move(ConnectedMouse mouse, int dx, int dy)
    {
        lock (_sync)
        {
            foreach (var ((owner, _), held) in _held)
            {
                if (owner != mouse) continue;
                if (held is HeldGesture g)
                {
                    g.Dx += dx;
                    g.Dy += dy;
                }
                else if (held is HeldRing r)
                {
                    if (Environment.TickCount64 - r.Started < RingSettleMs) continue;
                    (r.X, r.Y) = r.Ring.Aim(r.X, r.Y, dx * r.Scale, dy * r.Scale);
                    var reach = (double)Math.Max(r.Ring.Reach, 1);
                    Ring?.Aim(r.X / reach, r.Y / reach);
                    var pick = r.Ring.Pick(r.X, r.Y, r.Selected);
                    if (pick != r.Selected)
                    {
                        r.Selected = pick;
                        Ring?.Highlight(pick);
                    }
                }
            }
        }
    }

    /// <summary>Releases everything held for a device (it disconnected or went to sleep).</summary>
    public void ReleaseAll(ConnectedMouse mouse)
    {
        List<ushort> cids;
        lock (_sync) cids = _held.Keys.Where(k => k.Item1 == mouse).Select(k => k.Item2).ToList();
        foreach (var cid in cids) Release(mouse, cid);
    }

    internal static ActionSpec? PickGesture(GestureAction g, int dx, int dy)
    {
        var threshold = Math.Max(1, g.Threshold);
        if (Math.Abs(dx) < threshold && Math.Abs(dy) < threshold) return g.Tap;
        if (Math.Abs(dx) >= Math.Abs(dy)) return dx > 0 ? g.Right : g.Left;
        return dy > 0 ? g.Down : g.Up;
    }

    private static ActionSpec? PickGesture(HeldGesture g) => PickGesture(g.Gesture, g.Dx, g.Dy);

    private Held? StartDpiShift(ConnectedMouse mouse, int value)
    {
        var restore = mouse.Dpi?.Current;
        if (restore == null) return null;
        _ = _devices.SetDpiAsync(mouse, value, persist: false);
        return new HeldDpiShift(restore.Value);
    }

    /// <summary>Runs a one-shot action (press and release together).</summary>
    public void Fire(ConnectedMouse mouse, ActionSpec action)
    {
        try
        {
            switch (action)
            {
                case NativeAction or NoneAction:
                    break;
                case KeysAction keys:
                    InputSynth.Tap(keys.Keys);
                    break;
                case MouseAction m:
                    InputSynth.MouseButton(m.Button, down: true);
                    InputSynth.MouseButton(m.Button, down: false);
                    break;
                case ScrollAction s:
                    InputSynth.Scroll(s.Amount, s.Horizontal);
                    break;
                case LaunchAction launch:
                    Launch(launch);
                    break;
                case TextAction text:
                    InputSynth.TypeText(text.Text);
                    break;
                case MacroAction macro:
                    _ = Task.Run(() => RunMacroAsync(mouse, macro));
                    break;
                case GestureAction g:
                    if (g.Tap != null) Fire(mouse, g.Tap);
                    break;
                case DpiAction dpi:
                    _ = ChangeDpiAsync(mouse, dpi);
                    break;
                case WheelModeAction:
                    _ = _devices.ToggleWheelModeAsync(mouse);
                    break;
                case SwitchHostAction host:
                    _ = _devices.SwitchHostAsync(mouse, host.Host);
                    break;
            }
        }
        catch (Exception ex)
        {
            Report(action, ex);
        }
    }

    private async Task RunMacroAsync(ConnectedMouse mouse, MacroAction macro)
    {
        foreach (var step in macro.Steps)
        {
            if (step.Action is not null and not MacroAction) Fire(mouse, step.Action);
            if (step.DelayMs > 0) await Task.Delay(step.DelayMs).ConfigureAwait(false);
        }
    }

    private async Task ChangeDpiAsync(ConnectedMouse mouse, DpiAction action)
    {
        try
        {
            if (action.Mode == DpiMode.Set)
            {
                await _devices.SetDpiAsync(mouse, action.Value, persist: true).ConfigureAwait(false);
                return;
            }

            var presets = action.Values is { Count: > 0 } ? action.Values : _devices.DpiPresets(mouse);
            if (presets.Count == 0) return;
            var current = await _devices.GetDpiAsync(mouse).ConfigureAwait(false) ?? presets[0];
            var next = presets.FirstOrDefault(p => p > current, presets[0]);
            await _devices.SetDpiAsync(mouse, next, persist: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Report(action, ex);
        }
    }

    private static void Launch(LaunchAction launch)
    {
        var target = Environment.ExpandEnvironmentVariables(launch.Target);
        // Start menu apps (including Microsoft Store apps) launch through the shell by app ID,
        // which keeps working when the app updates and its install folder changes.
        var info = Platform.InstalledApps.IsAppsFolderTarget(target)
            ? new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true }
            : new ProcessStartInfo { FileName = target, Arguments = launch.Arguments ?? "", UseShellExecute = true };
        if (!string.IsNullOrWhiteSpace(launch.WorkingDirectory))
            info.WorkingDirectory = Environment.ExpandEnvironmentVariables(launch.WorkingDirectory);
        Process.Start(info)?.Dispose();
    }

    private static MouseButtonKind? NativeButtonFor(ushort cid) => cid switch
    {
        0x52 => MouseButtonKind.Middle,
        0x53 => MouseButtonKind.Back,
        0x56 => MouseButtonKind.Forward,
        _ => null,
    };

    private void Report(ActionSpec action, Exception ex)
    {
        Log.Error($"Action \"{action.Describe()}\" failed", ex);
        Failed?.Invoke($"{action.Describe()}: {ex.Message}");
    }
}
