using OpenPointer.Hidpp;

// Diagnostics for the HID++ layer. Logi Options+ must not be running, or it will fight over the device.
//   openpointer-cli dump              identity, features, controls, DPI, SmartShift, battery
//   openpointer-cli listen [cid...]   divert controls (hex CIDs, default: all divertable) and print events
//   openpointer-cli dpi <value>       set DPI

var command = args.Length > 0 ? args[0] : "dump";
if (Environment.GetEnvironmentVariable("OPENPOINTER_TRACE") == "1") HidppTransport.Trace = Console.WriteLine;
var transports = HidppTransport.OpenAll(Console.WriteLine);
if (transports.Count == 0)
{
    Console.WriteLine("No Logitech HID++ endpoints found.");
    return 1;
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
