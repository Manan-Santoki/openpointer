using System.Runtime.InteropServices;
using OpenPointer.Config;
using static OpenPointer.Platform.NativeMethods;

namespace OpenPointer.Platform;

/// <summary>Synthesizes keyboard and mouse input with SendInput.</summary>
public static class InputSynth
{
    /// <summary>Tags our own events so hooks can recognise them.</summary>
    public static readonly nint Signature = 0x4F50; // "OP"

    private static readonly HashSet<ushort> ExtendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // page up/down, end, home, arrows
        0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0xA3, 0xA5, // print, insert, delete, win, apps, numpad /, rctrl, ralt
    ];

    public static void KeyDown(IEnumerable<ushort> keys) => Send(keys.Select(k => Key(k, up: false)).ToArray());

    public static void KeyUp(IEnumerable<ushort> keys) => Send(keys.Reverse().Select(k => Key(k, up: true)).ToArray());

    public static void Tap(string chord)
    {
        var keys = KeyChord.Parse(chord);
        KeyDown(keys);
        KeyUp(keys);
    }

    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            inputs.Add(Unicode(ch, up: false));
            inputs.Add(Unicode(ch, up: true));
        }

        Send(inputs.ToArray());
    }

    public static void MouseButton(MouseButtonKind button, bool down)
    {
        var (flags, data) = (button, down) switch
        {
            (MouseButtonKind.Left, true) => (MOUSEEVENTF_LEFTDOWN, 0u),
            (MouseButtonKind.Left, false) => (MOUSEEVENTF_LEFTUP, 0u),
            (MouseButtonKind.Right, true) => (MOUSEEVENTF_RIGHTDOWN, 0u),
            (MouseButtonKind.Right, false) => (MOUSEEVENTF_RIGHTUP, 0u),
            (MouseButtonKind.Middle, true) => (MOUSEEVENTF_MIDDLEDOWN, 0u),
            (MouseButtonKind.Middle, false) => (MOUSEEVENTF_MIDDLEUP, 0u),
            (MouseButtonKind.Back, true) => (MOUSEEVENTF_XDOWN, XBUTTON1),
            (MouseButtonKind.Back, false) => (MOUSEEVENTF_XUP, XBUTTON1),
            (MouseButtonKind.Forward, true) => (MOUSEEVENTF_XDOWN, XBUTTON2),
            _ => (MOUSEEVENTF_XUP, XBUTTON2),
        };
        Send([Mouse(flags, (int)data)]);
    }

    /// <summary>Scrolls by notches; positive is up (vertical) or right (horizontal).</summary>
    public static void Scroll(int notches, bool horizontal)
    {
        const int WheelDelta = 120;
        Send([Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, notches * WheelDelta)]);
    }

    private static INPUT Key(ushort vk, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0;
        if (ExtendedKeys.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKeyW(vk, 0), dwFlags = flags, dwExtraInfo = Signature },
            },
        };
    }

    private static INPUT Unicode(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), dwExtraInfo = Signature },
        },
    };

    private static INPUT Mouse(uint flags, int data) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data, dwExtraInfo = Signature } },
    };

    private static void Send(INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
