using OpenPointer.Hidpp;

// Diagnostics for the HID++ layer. Logi Options+ must not be running, or it will fight over the device.
//   openpointer-cli dump              identity, features, controls, DPI, SmartShift, battery
//   openpointer-cli listen [cid...]   divert controls (hex CIDs, default: all divertable) and print events
//   openpointer-cli dpi <value>       set DPI

//   openpointer-cli touchpad [secs]   print precision touchpad contacts (0..1 from the top-left)

var command = args.Length > 0 ? args[0] : "dump";
if (command == "touchpad-replay")
{
    // Replays a "touchpad" recording through the gesture recognizer with every gesture assigned.
    var invariant = System.Globalization.CultureInfo.InvariantCulture;
    var recognizer = new OpenPointer.Engine.TouchpadRecognizer
    {
        Aspect = args.Length > 2 ? double.Parse(args[2], invariant) : 117 / 72.0,
        Active = OpenPointer.Config.TouchpadGestures.All.Select(g => g.Key).ToHashSet(),
    };
    long at = 0;
    recognizer.Fired += key => Console.WriteLine($"{at,6} {key}");
    foreach (var line in File.ReadLines(args[1]))
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, @"^\s*(\d+)\s*(.*)$");
        if (!match.Success) continue;
        at = long.Parse(match.Groups[1].Value);
        var contacts = System.Text.RegularExpressions.Regex.Matches(match.Groups[2].Value, @"(\d):([\d.]+),([\d.]+)( palm)?")
            .Select(m => new OpenPointer.Platform.TouchContact(int.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value, invariant),
                double.Parse(m.Groups[3].Value, invariant), true, !m.Groups[4].Success)).ToList();
        recognizer.Feed(contacts, at, match.Groups[2].Value.Contains("[click]"));
    }
    return 0;
}
if (command == "touchpad")
{
    using var touchpad = new OpenPointer.Platform.TouchpadMonitor();
    touchpad.DeviceFound += (path, contacts, size) =>
        Console.WriteLine($"Touchpad {path}: {contacts} contacts, {(size is { } s ? $"{s.Width:F0} x {s.Height:F0} mm" : "size not reported")}");
    var last = "";
    touchpad.Frame += frame =>
    {
        var line = string.Join("  ", frame.Contacts.Where(c => c.Touching)
            .Select(c => $"{c.Id}:{c.X:F2},{c.Y:F2}{(c.Confident ? "" : " palm")}")) + (frame.ButtonDown ? "  [click]" : "");
        if (line == last) return;
        last = line;
        Console.WriteLine($"{frame.Tick % 100000,6} {line}");
    };
    touchpad.Start();
    var seconds = args.Length > 1 ? int.Parse(args[1]) : 0;
    Console.WriteLine(seconds > 0 ? $"Recording for {seconds} s." : "Touch the pad. Press Enter to stop.");
    if (seconds > 0) Thread.Sleep(seconds * 1000); else Console.ReadLine();
    return 0;
}
var windowsMice = OpenPointer.Platform.WindowsMouseDiscovery.Enumerate();
Console.WriteLine($"Windows mice: {windowsMice.Count}");
foreach (var mouse in windowsMice)
{
    Console.WriteLine($"  {mouse.Name}: {mouse.Connection}, {mouse.ButtonCount} buttons, remappable: {string.Join(", ", mouse.Buttons)}");
    Console.WriteLine($"    {mouse.DevicePath}");
}
if (command == "devices") return 0;
if (Environment.GetEnvironmentVariable("OPENPOINTER_TRACE") == "1") HidppTransport.Trace = Console.WriteLine;
var transports = HidppTransport.OpenAll(Console.WriteLine);
if (transports.Count == 0)
{
    Console.WriteLine("No Logitech HID++ endpoints found. Standard mice use Windows button remapping in the app.");
    return command == "dump" && windowsMice.Count > 0 ? 0 : 1;
}

var mice = new List<MouseDevice>();
foreach (var transport in transports)
{
    foreach (var device in await DeviceDiscovery.ProbeAsync(transport, Console.WriteLine))
    {
        var mouse = new MouseDevice(device);
        await mouse.InitializeAsync();
        Console.WriteLine($"  -> {mouse.Name} (type {mouse.DeviceType}, protocol {mouse.Protocol})");
        if (mouse.IsMouseLike) mice.Add(mouse);
    }
}

if (mice.Count == 0)
{
    Console.WriteLine("No HID++ 2.0 mouse found.");
    return 1;
}

var target = mice[0];
switch (command)
{
    case "dump":
        foreach (var (index, id, version) in await target.Device.ListFeaturesAsync())
            Console.WriteLine($"  feature [{index:X2}] {id:X4} v{version}");
        foreach (var c in target.Controls)
            Console.WriteLine($"  control CID {c.Cid:X4} task {c.TaskId:X4} flags [{c.Flags}]");
        Console.WriteLine($"  battery    {await target.GetBatteryAsync()}");
        var dpi = await target.GetDpiAsync();
        Console.WriteLine($"  dpi        {dpi?.Current} (default {dpi?.Default}, {dpi?.Supported.Count} steps {dpi?.Supported.FirstOrDefault()}-{dpi?.Supported.LastOrDefault()})");
        Console.WriteLine($"  smartshift {await target.GetSmartShiftAsync()}");
        Console.WriteLine($"  wheel      0x{await target.GetWheelModeAsync():X2}");
        Console.WriteLine($"  thumbwheel {await target.GetThumbWheelAsync()}");
        Console.WriteLine($"  hosts      {await target.GetHostsAsync()}");
        break;

    case "dpi":
        await target.SetDpiAsync(int.Parse(args[1]));
        Console.WriteLine($"DPI now {(await target.GetDpiAsync())?.Current}");
        break;

    case "listen":
        var cids = args.Length > 1
            ? args.Skip(1).Select(a => Convert.ToUInt16(a, 16)).ToList()
            : target.Controls.Where(c => c.IsDivertable).Select(c => c.Cid).ToList();
        target.ButtonDown += cid => Console.WriteLine($"down {cid:X4}");
        target.ButtonUp += cid => Console.WriteLine($"up   {cid:X4}");
        target.RawMove += (dx, dy) => Console.WriteLine($"move {dx,5} {dy,5}");
        target.ThumbWheel += (r, phase) => Console.WriteLine($"thumb {r,4} {phase}");
        foreach (var cid in cids) await target.SetDivertAsync(cid, true, rawXY: cid == 0xC3);
        await target.SetThumbWheelAsync(true, false);
        Console.WriteLine($"Diverted {string.Join(", ", cids.Select(c => c.ToString("X4")))} and the thumb wheel. Press Enter to restore.");
        Console.ReadLine();
        foreach (var cid in cids) await target.SetDivertAsync(cid, false);
        await target.SetThumbWheelAsync(false, false);
        break;
}

foreach (var t in transports) t.Dispose();
return 0;
