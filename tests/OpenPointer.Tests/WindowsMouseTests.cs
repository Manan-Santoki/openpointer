using OpenPointer.Config;
using OpenPointer.Engine;
using OpenPointer.Platform;

namespace OpenPointer.Tests;

public class WindowsMouseCapabilityTests
{
    [Theory]
    [InlineData(0, "")]
    [InlineData(2, "")]
    [InlineData(3, "middle")]
    [InlineData(4, "middle,back")]
    [InlineData(5, "middle,forward,back")]
    [InlineData(16, "middle,forward,back")]
    public void ShowsOnlyAvailableStandardButtons(int count, string expected)
    {
        var mouse = new ConnectedMouse(new WindowsMouseInfo("mouse", "Mouse", count, "Bluetooth"));
        Assert.Equal(expected, string.Join(",", mouse.Buttons));
        Assert.All(mouse.Buttons, button => Assert.True(mouse.SupportsGestures(button)));
        Assert.False(mouse.SupportsGestures(ButtonNames.Gesture));
    }

    [Fact]
    public void HidUsagesOverrideAnInflatedDriverCountAndAllowSparseButtons()
    {
        var threeButtons = new WindowsMouseInfo("lenovo", "Lenovo 600 Bluetooth Silent Mouse", 5, "Bluetooth", ButtonMask: 7);
        Assert.Equal([ButtonNames.Middle], threeButtons.Buttons);
        var sparse = threeButtons with { ButtonMask = 1 | 2 | 4 | 16 };
        Assert.Equal([ButtonNames.Middle, ButtonNames.Forward], sparse.Buttons);
        Assert.Empty((threeButtons with { ButtonMask = 3 }).Buttons);
    }

    [Theory]
    [InlineData(@"\\?\HID#VID_046D&PID_C548&MI_01", "VID", 0x046D)]
    [InlineData(@"\\?\HID#VID_046D&PID_C548&MI_01", "PID", 0xC548)]
    [InlineData(@"\\?\HID#Dev_VID&020017EF_PID&6099", "VID", 0x17EF)]
    [InlineData(@"\\?\HID#Dev_VID&020017EF_PID&6099", "PID", 0x6099)]
    [InlineData(@"\\?\HID#ELAN06FA&Col01", "VID", null)]
    public void ReadsUsbAndBluetoothHardwareIds(string path, string kind, int? expected) =>
        Assert.Equal(expected, WindowsMouseDiscovery.ReadId(path, kind));

    [Fact]
    public void StandardMouseSupportsSoftwareActionsButNotHardwareCommands()
    {
        var mouse = StandardMouse();
        Assert.True(mouse.SupportsAction(new KeysAction("ctrl+c")));
        Assert.True(mouse.SupportsAction(new MouseAction(MouseButtonKind.Middle)));
        Assert.True(mouse.SupportsAction(new ScrollAction(1)));
        Assert.True(mouse.SupportsAction(new LaunchAction("explorer.exe")));
        Assert.True(mouse.SupportsAction(new TextAction("hello")));
        Assert.True(mouse.SupportsAction(GestureAction.WindowsDefaults));
        Assert.True(mouse.SupportsAction(ActionRingAction.Defaults));
        Assert.True(mouse.SupportsAction(new MacroAction([new(new KeysAction("ctrl+c"))])));
        Assert.False(mouse.SupportsAction(new DpiAction(DpiMode.Cycle)));
        Assert.False(mouse.SupportsAction(new WheelModeAction()));
        Assert.False(mouse.SupportsAction(new SwitchHostAction(2)));
        Assert.False(mouse.SupportsAction(new GestureAction { Up = new DpiAction(DpiMode.Set) }));
        Assert.False(mouse.SupportsAction(new ActionRingAction { Items = [new("DPI", new DpiAction(DpiMode.Cycle))] }));
        Assert.False(mouse.SupportsAction(new MacroAction([new(new WheelModeAction())])));
    }

    [Fact]
    public void IdenticalReceiverModelsAreMatchedByTheirPhysicalContainer()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var mouse = new WindowsMouseInfo("receiver", "Mouse", 5, "USB", 0x046D, 0xC548, ContainerId: first);
        Assert.True(mouse.MatchesLogitechDevice(0xC548, first));
        Assert.False(mouse.MatchesLogitechDevice(0xC548, second));
        Assert.False(mouse.MatchesLogitechDevice(0xC52B, first));
        Assert.False((mouse with { VendorId = 0x17EF }).MatchesLogitechDevice(0xC548, first));
    }

    [Fact]
    public void TouchpadsRequireBothDeveloperModeAndExplicitOptIn()
    {
        var touchpad = new WindowsMouseInfo("touchpad", "Touchpad", 2, "Built-in", IsTouchpad: true);
        var settings = new GeneralSettings();
        Assert.False(touchpad.IsVisible(settings));
        settings.ShowTouchpads = true;
        Assert.False(touchpad.IsVisible(settings));
        settings.DeveloperMode = true;
        Assert.True(touchpad.IsVisible(settings));
        settings.ShowTouchpads = false;
        Assert.False(touchpad.IsVisible(settings));
        Assert.True((touchpad with { IsTouchpad = false, Connection = "Bluetooth" }).IsVisible(settings));
    }

    [Fact]
    public void DeveloperDeviceSettingsPersistWithoutEnablingThemForExistingConfigs()
    {
        var old = ConfigStore.Parse("{}");
        Assert.False(old.General.DeveloperMode);
        Assert.False(old.General.ShowTouchpads);
        old.General.DeveloperMode = old.General.ShowTouchpads = true;
        var roundTrip = ConfigStore.Parse(ConfigStore.Serialize(old));
        Assert.True(roundTrip.General.DeveloperMode);
        Assert.True(roundTrip.General.ShowTouchpads);
    }

    internal static ConnectedMouse StandardMouse() => new(new WindowsMouseInfo("lenovo", "Lenovo 600 Bluetooth Silent Mouse", 3, "Bluetooth"));
}

public class ReceiverIdentityTests
{
    [Fact]
    public void ReadsTheMouseModelFromBoltPairingInformation()
    {
        // Captured from the receiver while the paired mouse did not answer a HID++ ping.
        byte[] info = [0x52, 0x42, 0x2A, 0xB0, 0x5A, 0xBD, 0xEE, 0x58, 1, 0, 0x80, 0x14, 1, 0, 0, 0];
        byte[] name = [0x62, 1, 11, .. System.Text.Encoding.UTF8.GetBytes("Logi M650 L"), 0, 0];
        Assert.Equal("Logi M650 L", OpenPointer.Hidpp.DeviceDiscovery.ParseReceiverMouseName(2, info, name));
        Assert.Null(OpenPointer.Hidpp.DeviceDiscovery.ParseReceiverMouseName(1, info, name));
        info[1] = 0x41; // Keyboard paired to the same receiver must not become a mouse.
        Assert.Null(OpenPointer.Hidpp.DeviceDiscovery.ParseReceiverMouseName(2, info, name));
    }

    [Fact]
    public void BadOrEmptyNamesAreNotDisplayed()
    {
        Assert.Null(OpenPointer.Hidpp.DeviceDiscovery.ParseReceiverMouseName(2, [0x52, 0x42], []));
        Assert.Null(OpenPointer.Hidpp.DeviceDiscovery.ParseReceiverMouseName(2, [0x52, 0x42], [0x62, 1, 0]));
        Assert.Null(OpenPointer.Hidpp.DeviceDiscovery.ParseReceiverMouseName(2, [0x52, 0x42], [0x62, 2, 4, 65, 66]));
    }
}

public class WindowsMouseBindingTests
{
    private static readonly FocusContext Chrome = new("chrome.exe", "Chrome_WidgetWin_1", "Chrome");
    private static readonly ushort Middle = ButtonNames.ToCid(ButtonNames.Middle)!.Value;

    [Fact]
    public void NativeAndUnavailableControlsAreNotIntercepted()
    {
        var mouse = WindowsMouseCapabilityTests.StandardMouse();
        var config = AppConfig.CreateDefault();
        Assert.Null(WindowsButtonBinding.Resolve(mouse, config, Chrome, Middle));
        config.DefaultProfile.Buttons[ButtonNames.Back] = new KeysAction("ctrl+c");
        Assert.Null(WindowsButtonBinding.Resolve(mouse, config, Chrome, ButtonNames.ToCid(ButtonNames.Back)!.Value));
        config.DefaultProfile.Buttons[ButtonNames.Middle] = new DpiAction(DpiMode.Cycle);
        Assert.Null(WindowsButtonBinding.Resolve(mouse, config, Chrome, Middle));
        mouse.Online = false;
        config.DefaultProfile.Buttons[ButtonNames.Middle] = new KeysAction("ctrl+c");
        Assert.Null(WindowsButtonBinding.Resolve(mouse, config, Chrome, Middle));
    }

    [Fact]
    public void PerAppActionsAndInheritedMultiPressesWorkOnAThreeButtonMouse()
    {
        var config = AppConfig.CreateDefault();
        config.DefaultProfile.Buttons["middle:2"] = new KeysAction("ctrl+v");
        config.Profiles.Add(new Profile { Name = "Chrome", Match = ["chrome.exe"], Buttons = new() { ["middle"] = new KeysAction("ctrl+c") } });
        var binding = WindowsButtonBinding.Resolve(WindowsMouseCapabilityTests.StandardMouse(), config, Chrome, Middle);
        Assert.NotNull(binding);
        Assert.Equal(new KeysAction("ctrl+c"), binding.Presses[0]);
        Assert.Equal(new KeysAction("ctrl+v"), binding.Presses[1]);
        config.Profiles[^1].Buttons["middle:2"] = new NativeAction();
        Assert.Null(WindowsButtonBinding.Resolve(binding.Mouse, config, Chrome, Middle)!.Presses[1]);
    }

    [Fact]
    public void LaterPressActionCanInterceptANativeSinglePress()
    {
        var config = AppConfig.CreateDefault();
        config.DefaultProfile.Buttons["middle:2"] = new KeysAction("ctrl+c");
        var binding = WindowsButtonBinding.Resolve(WindowsMouseCapabilityTests.StandardMouse(), config, Chrome, Middle);
        Assert.NotNull(binding);
        Assert.IsType<NativeAction>(binding.Presses[0]);
        Assert.Equal(new KeysAction("ctrl+c"), binding.Presses[1]);
    }
}

public class WindowsMouseRouterTests
{
    private static WindowsButtonBinding Binding(ActionSpec? action = null) =>
        new(WindowsMouseCapabilityTests.StandardMouse(), [action ?? new KeysAction("ctrl+c"), null, null]);

    [Fact]
    public void NativeInputPrimaryClicksAndWheelPassThrough()
    {
        var events = new List<WindowsMouseEvent>();
        var router = new WindowsMouseRouter(_ => null, events.Add);
        foreach (var message in new[] { 0x0200, 0x0201, 0x0202, 0x0204, 0x0205, 0x0207, 0x0208, 0x020A, 0x020E })
            Assert.False(router.Process(message, 0, 100, 100));
        Assert.Empty(events);
    }

    [Theory]
    [InlineData(1u, 0)]
    [InlineData(2u, 0)]
    [InlineData(0u, 0x4F50)]
    public void GeneratedInputIsNeverRemapped(uint flags, int extraInfo)
    {
        var events = new List<WindowsMouseEvent>();
        var router = new WindowsMouseRouter(_ => Binding(), events.Add);
        Assert.False(router.Process(0x0207, 0, 0, 0, flags, extraInfo));
        Assert.False(router.Process(0x0208, 0, 0, 0, flags, extraInfo));
        Assert.Empty(events);
    }

    [Theory]
    [InlineData(0x0207, 0x0208, 0u, "middle")]
    [InlineData(0x020B, 0x020C, 0x10000u, "back")]
    [InlineData(0x020B, 0x020C, 0x20000u, "forward")]
    public void RemapsPressAndReleaseExactlyOnce(int down, int up, uint data, string button)
    {
        var events = new List<WindowsMouseEvent>();
        var router = new WindowsMouseRouter(_ => Binding(), events.Add);
        Assert.True(router.Process(down, data, 0, 0));
        Assert.True(router.Process(down, data, 0, 0)); // Duplicate down must not start another action.
        Assert.True(router.Process(up, data, 0, 0));
        Assert.False(router.Process(up, data, 0, 0));
        Assert.Equal(ButtonNames.ToCid(button), Assert.IsType<WindowsMouseEvent.Down>(events[0]).Cid);
        Assert.IsType<WindowsMouseEvent.Up>(events[1]);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void ReleaseKeepsOriginalBindingWhenFocusOrPauseChanges()
    {
        var events = new List<WindowsMouseEvent>();
        WindowsButtonBinding? binding = Binding();
        var original = binding.Mouse;
        var router = new WindowsMouseRouter(_ => binding, events.Add);
        Assert.True(router.Process(0x0207, 0, 0, 0));
        binding = null;
        Assert.True(router.Process(0x0208, 0, 0, 0));
        Assert.Same(original, Assert.IsType<WindowsMouseEvent.Up>(events[1]).Mouse);
        Assert.False(router.Process(0x0207, 0, 0, 0));
    }

    [Fact]
    public void ChangingProfileWhileANativeButtonIsDownDoesNotSwallowItsRelease()
    {
        var events = new List<WindowsMouseEvent>();
        WindowsButtonBinding? binding = null;
        var router = new WindowsMouseRouter(_ => binding, events.Add);
        Assert.False(router.Process(0x0207, 0, 0, 0));
        binding = Binding();
        Assert.False(router.Process(0x0208, 0, 0, 0));
        Assert.Empty(events);
    }

    [Fact]
    public void GesturesCaptureMovementAndCancelStopsFreezingThePointer()
    {
        var events = new List<WindowsMouseEvent>();
        var binding = Binding(GestureAction.WindowsDefaults);
        var router = new WindowsMouseRouter(_ => binding, events.Add);
        Assert.True(router.Process(0x0207, 0, 100, 100));
        Assert.True(router.Process(0x0200, 0, 112, 93));
        var move = Assert.IsType<WindowsMouseEvent.Move>(events[1]);
        Assert.Equal((12, -7), (move.Dx, move.Dy));
        router.Cancel(binding.Mouse);
        Assert.IsType<WindowsMouseEvent.Cancel>(events[^1]);
        Assert.False(router.Process(0x0200, 0, 125, 110));
        Assert.True(router.Process(0x0208, 0, 125, 110));
        Assert.Equal(3, events.Count); // A cancelled press never fires a release action.
    }

    [Fact]
    public void CancelledCounterCannotFireAnOldTimer()
    {
        var counter = new PressCounter();
        counter.Down([new KeysAction("ctrl+c"), new KeysAction("ctrl+v")], out _);
        counter.Up(out _);
        var generation = counter.Generation;
        counter.Reset();
        Assert.False(counter.Busy);
        Assert.Null(counter.Expire(generation));
    }
}

public class WindowsMouseActionTests
{
    [Fact]
    public void StandardMouseUsesExistingGestureEngineAndCancellationDoesNotFireIt()
    {
        var control = new DeviceControl();
        var runner = new ActionRunner(control);
        var mouse = WindowsMouseCapabilityTests.StandardMouse();
        var cid = ButtonNames.ToCid(ButtonNames.Middle)!.Value;
        var gesture = new GestureAction { Tap = new SwitchHostAction(1), Right = new SwitchHostAction(2) };
        runner.Press(mouse, cid, gesture);
        runner.ReleaseAll(mouse, cancel: true);
        Assert.Empty(control.Hosts);
        runner.Press(mouse, cid, gesture);
        runner.Move(mouse, 80, 10);
        runner.Release(mouse, cid);
        Assert.Equal([2], control.Hosts);
    }

    [Fact]
    public void CancellingARingClosesTheOverlay()
    {
        var presenter = new RingPresenter();
        var runner = new ActionRunner(new DeviceControl()) { Ring = presenter };
        var mouse = WindowsMouseCapabilityTests.StandardMouse();
        runner.Press(mouse, 0x52, ActionRingAction.Defaults);
        Assert.True(presenter.Visible);
        runner.ReleaseAll(mouse, cancel: true);
        Assert.False(presenter.Visible);
    }

    private sealed class DeviceControl : IDeviceControl
    {
        public List<int> Hosts { get; } = [];
        public Task<int?> GetDpiAsync(ConnectedMouse mouse) => Task.FromResult<int?>(null);
        public Task SetDpiAsync(ConnectedMouse mouse, int dpi, bool persist) => Task.CompletedTask;
        public IReadOnlyList<int> DpiPresets(ConnectedMouse mouse) => [];
        public Task ToggleWheelModeAsync(ConnectedMouse mouse) => Task.CompletedTask;
        public Task SwitchHostAsync(ConnectedMouse mouse, int host) { Hosts.Add(host); return Task.CompletedTask; }
    }

    private sealed class RingPresenter : IRingPresenter
    {
        public bool Visible { get; private set; }
        public void Show(ActionRingAction ring) => Visible = true;
        public void Hide() => Visible = false;
        public void Highlight(int index) { }
        public void Aim(double x, double y) { }
    }
}
