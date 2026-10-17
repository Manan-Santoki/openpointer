using OpenPointer.Config;
using OpenPointer.Platform;

namespace OpenPointer.Engine;

internal sealed record WindowsButtonBinding(ConnectedMouse Mouse, ActionSpec?[] Presses)
{
    public bool CapturesMovement => Presses.Any(a => a is GestureAction or ActionRingAction);

    public static WindowsButtonBinding? Resolve(ConnectedMouse mouse, AppConfig config, FocusContext context, ushort cid)
    {
        var button = ButtonNames.FromCid(cid);
        if (!mouse.Online || !mouse.Buttons.Contains(button)) return null;
        var presses = ContextMatcher.ResolvePresses(config, context, button);
        for (var i = 0; i < presses.Length; i++)
            if (presses[i] is { } action && !mouse.SupportsAction(action)) presses[i] = i == 0 ? new NativeAction() : null;
        return presses.Any(a => a is not null and not NativeAction) ? new(mouse, presses) : null;
    }
}

internal abstract record WindowsMouseEvent(ConnectedMouse Mouse)
{
    public sealed record Down(WindowsButtonBinding Binding, ushort Cid) : WindowsMouseEvent(Binding.Mouse);
    public sealed record Up(ConnectedMouse Owner, ushort Cid) : WindowsMouseEvent(Owner);
    public sealed record Move(ConnectedMouse Owner, int Dx, int Dy) : WindowsMouseEvent(Owner);
    public sealed record Cancel(ConnectedMouse Owner) : WindowsMouseEvent(Owner);
}

/// <summary>
/// Owns intercepted press/release pairs. Windows' low-level hook has no device identity, so these
/// mappings intentionally apply to the shared Windows mouse stream, not one physical mouse.
/// </summary>
internal sealed class WindowsMouseRouter(Func<ushort, WindowsButtonBinding?> resolve, Action<WindowsMouseEvent> dispatch)
{
    private readonly Dictionary<ushort, WindowsButtonBinding?> _held = [];
    private int _anchorX, _anchorY;

    public bool Process(int message, uint data, int x, int y, uint flags = 0, nint extraInfo = 0)
    {
        // Never feed synthetic actions back into the remapper, including other apps' injected input.
        if ((flags & 3) != 0 || extraInfo == InputSynth.Signature) return false;
        if (message == 0x0200)
        {
            var owners = _held.Values.Where(b => b is { CapturesMovement: true }).Select(b => b!.Mouse).Distinct().ToArray();
            if (owners.Length == 0) return false;
            foreach (var owner in owners) dispatch(new WindowsMouseEvent.Move(owner, x - _anchorX, y - _anchorY));
            // Suppressed movement leaves the pointer at the anchor; each message is a delta from it.
            return true;
        }

        var cid = Button(message, data);
        if (cid == null) return false;
        var down = message is 0x0207 or 0x020B;
        if (!down)
        {
            if (!_held.Remove(cid.Value, out var binding)) return false;
            if (binding != null) dispatch(new WindowsMouseEvent.Up(binding.Mouse, cid.Value));
            return true;
        }

        if (_held.ContainsKey(cid.Value)) return true;
        var next = resolve(cid.Value);
        if (next == null) return false;
        if (next.CapturesMovement && !_held.Values.Any(b => b is { CapturesMovement: true }))
            (_anchorX, _anchorY) = (x, y);
        _held[cid.Value] = next;
        dispatch(new WindowsMouseEvent.Down(next, cid.Value));
        return true;
    }

    public void Cancel(ConnectedMouse? mouse = null)
    {
        var owners = _held.Values.Where(b => b != null && (mouse == null || b.Mouse == mouse)).Select(b => b!.Mouse).Distinct().ToArray();
        foreach (var cid in _held.Where(p => p.Value != null && owners.Contains(p.Value.Mouse)).Select(p => p.Key).ToArray())
            _held[cid] = null; // Still swallow the matching physical release, but stop capturing movement.
        foreach (var owner in owners) dispatch(new WindowsMouseEvent.Cancel(owner));
    }

    internal static ushort? Button(int message, uint data) => message switch
    {
        0x0207 or 0x0208 => ButtonNames.ToCid(ButtonNames.Middle),
        0x020B or 0x020C => (data >> 16) switch
        {
            1 => ButtonNames.ToCid(ButtonNames.Back),
            2 => ButtonNames.ToCid(ButtonNames.Forward),
            _ => null,
        },
        _ => null, // Primary clicks and scrolling always stay native.
    };
}
