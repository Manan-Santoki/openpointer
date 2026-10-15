using OpenPointer.Config;
using OpenPointer.Engine;
using OpenPointer.Hidpp;

namespace OpenPointer.Tests;

public class ConfigTests
{
    [Fact]
    public void DefaultConfigRoundTripsThroughJson()
    {
        var json = ConfigStore.Serialize(AppConfig.CreateDefault());
        var parsed = ConfigStore.Parse(json);

        Assert.Equal(2, parsed.Profiles.Count);
        Assert.IsType<GestureAction>(parsed.DefaultProfile.Buttons[ButtonNames.Gesture]);
        Assert.Equal(new LaunchAction("explorer.exe"), parsed.Profiles[1].Buttons[ButtonNames.Forward]);
        Assert.Equal(json, ConfigStore.Serialize(parsed));
    }

    [Fact]
    public void ParsesHandWrittenConfigWithDiscriminatorNotFirst()
    {
        const string json = """
            {
              // comments are allowed
              "profiles": [
                { "name": "Default", "buttons": { "back": { "keys": "alt+left", "type": "keys" } } },
              ]
            }
            """;
        var config = ConfigStore.Parse(json);
        Assert.Equal(new KeysAction("alt+left"), config.DefaultProfile.Buttons["back"]);
        Assert.True(config.Devices.ContainsKey("*"));
    }

    [Fact]
    public void DeviceSettingsMergeSpecificOverGlobal()
    {
        var config = new AppConfig();
        config.Devices["*"] = new DeviceSettings { Dpi = 1000, InvertScroll = true };
        config.Devices["M650"] = new DeviceSettings { Dpi = 1600 };

        var merged = config.SettingsFor("M650");
        Assert.Equal(1600, merged.Dpi);
        Assert.True(merged.InvertScroll);
        Assert.Equal(1000, config.SettingsFor("Other").Dpi);
    }
}

public class ProfileTests
{
    private static readonly FocusContext Chrome = new("chrome.exe", "Chrome_WidgetWin_1", "New Tab");
    private static readonly FocusContext Desktop = new("explorer.exe", "Progman", "Program Manager");
    private static readonly FocusContext Explorer = new("explorer.exe", "CabinetWClass", "Downloads");

    private static AppConfig Config() => new()
    {
        Profiles =
        [
            new Profile { Name = "Default", Buttons = { ["back"] = new MouseAction(MouseButtonKind.Back) }, Dpi = 1000 },
            new Profile { Name = "Desktop", Match = [ContextMatcher.Desktop], Buttons = { ["back"] = new LaunchAction("calc.exe") } },
            new Profile { Name = "Chrome", Match = ["chrome"], Buttons = { ["forward"] = new KeysAction("ctrl+tab") }, Dpi = 1600 },
        ],
    };

    [Fact]
    public void DesktopIsNotTreatedAsExplorer()
    {
        var config = Config();
        Assert.Equal(new LaunchAction("calc.exe"), ContextMatcher.ResolveButton(config, Desktop, "back"));
        Assert.Equal(new MouseAction(MouseButtonKind.Back), ContextMatcher.ResolveButton(config, Explorer, "back"));
    }

    [Fact]
    public void AppProfileFallsBackToDefaultPerButton()
    {
        var config = Config();
        Assert.Equal(new KeysAction("ctrl+tab"), ContextMatcher.ResolveButton(config, Chrome, "forward"));
        Assert.Equal(new MouseAction(MouseButtonKind.Back), ContextMatcher.ResolveButton(config, Chrome, "back"));
        Assert.IsType<NativeAction>(ContextMatcher.ResolveButton(config, Explorer, "middle"));
    }

    [Fact]
    public void DisabledProfilesAreSkipped()
    {
        var config = Config();
        config.Profiles[2].Enabled = false;
        Assert.IsType<NativeAction>(ContextMatcher.ResolveButton(config, Chrome, "forward"));
    }

    [Fact]
    public void PerAppDpiFallsBackToDefaultProfile()
    {
        var config = Config();
        Assert.Equal(1600, ContextMatcher.ResolveDpi(config, Chrome));
        Assert.Equal(1000, ContextMatcher.ResolveDpi(config, Explorer));
    }
}

public class GestureTests
{
    private static readonly GestureAction Gesture = new()
    {
        Tap = new KeysAction("tap"), Up = new KeysAction("up"), Down = new KeysAction("down"),
        Left = new KeysAction("left"), Right = new KeysAction("right"), Threshold = 50,
    };

    [Theory]
    [InlineData(0, 0, "tap")]
    [InlineData(30, -40, "tap")]
    [InlineData(120, 10, "right")]
    [InlineData(-120, 60, "left")]
    [InlineData(10, 200, "down")]
    [InlineData(-20, -90, "up")]
    public void PicksDirectionByDominantAxis(int dx, int dy, string expected)
    {
        Assert.Equal(new KeysAction(expected), ActionRunner.PickGesture(Gesture, dx, dy));
    }
}

public class KeyChordTests
{
    [Fact]
    public void ModifiersArePressedFirst()
    {
        Assert.Equal([0x11, 0x10, (ushort)'T'], KeyChord.Parse("t+ctrl+shift"));
    }

    [Theory]
    [InlineData("win+tab")]
    [InlineData("ctrl+win+right")]
    [InlineData("media_play_pause")]
    [InlineData("f13")]
    public void KnownChordsParse(string chord) => Assert.True(KeyChord.TryParse(chord, out _));

    [Fact]
    public void UnknownKeyIsRejected() => Assert.False(KeyChord.TryParse("ctrl+banana", out _));

    [Fact]
    public void FormatsForDisplay() => Assert.Equal("Ctrl + Shift + T", KeyChord.Format("ctrl+shift+t"));
}

public class TransportTests
{
    [Fact]
    public void ShortAndLongCollectionsOfOneInterfaceShareAKey()
    {
        const string col1 = @"\\?\hid#vid_046d&pid_c548&mi_02&col01#8&3a285d73&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string col2 = @"\\?\hid#vid_046d&pid_c548&mi_02&col02#8&3a285d73&0&0001#{4d1e55b2-f16f-11cf-88cb-001111000030}";
        const string other = @"\\?\hid#vid_046d&pid_c548&mi_01&col04#8&2acad0bd&0&0003#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        Assert.Equal(HidppTransport.InterfaceKey(col1), HidppTransport.InterfaceKey(col2));
        Assert.NotEqual(HidppTransport.InterfaceKey(col1), HidppTransport.InterfaceKey(other));
    }

    [Fact]
    public void BuildsLongReports()
    {
        var message = HidppMessage.Long(2, 0x08, 3, 0x0A, [0x00, 0x53, 0x03]);
        Assert.Equal("110208 3A00530300000000000000000000000000".Replace(" ", ""), message.ToString());
        Assert.Equal(3, message.Function);
        Assert.Equal(0x0A, message.SoftwareId);
    }

    [Fact]
    public void ParsesReceiverConnectionNotifications()
    {
        Assert.True(DeviceDiscovery.TryParseConnection(new HidppMessage([0x10, 0x02, 0x41, 0x04, 0x61, 0x2E, 0x40]), out var slot, out var linked));
        Assert.Equal(2, slot);
        Assert.False(linked);
        Assert.True(DeviceDiscovery.TryParseConnection(new HidppMessage([0x10, 0x02, 0x41, 0x04, 0x21, 0x2E, 0x40]), out _, out linked));
        Assert.True(linked);
    }
}

public class RepeatTests
{
    [Theory]
    [InlineData("volume_up", true)]
    [InlineData("volume_down", true)]
    [InlineData("ctrl+z", true)]
    [InlineData("z", false)]
    [InlineData("alt+f4", false)]
    [InlineData("win+tab", false)]
    [InlineData("media_play_pause", false)]
    [InlineData("ctrl+plus", true)]
    public void PicksRepeatableChords(string chord, bool expected) => Assert.Equal(expected, KeyChord.IsRepeatable(chord));

    [Fact]
    public void ExplicitRepeatOverridesAutomatic()
    {
        Assert.False(new KeysAction("volume_up") { Repeat = false }.ShouldRepeat);
        Assert.True(new KeysAction("enter") { Repeat = true }.ShouldRepeat);
    }

    [Fact]
    public void RepeatTimingIsWithinKeyboardRange()
    {
        var (delay, interval) = ActionRunner.Repeater.KeyboardRepeatTiming();
        Assert.InRange(delay, 250, 1000);
        Assert.InRange(interval, 33, 400);
    }
}

public class RingTests
{
    private static ActionRingAction Ring(int count) => new()
    {
        Items = Enumerable.Range(0, count).Select(i => new RingItem($"{i}", new KeysAction("ctrl+c"))).ToList(),
        Threshold = 40,
    };

    [Theory]
    [InlineData(0, -100, 0)]   // up
    [InlineData(100, 0, 2)]    // right
    [InlineData(0, 100, 4)]    // down
    [InlineData(-100, 0, 6)]   // left
    [InlineData(80, -80, 1)]   // up-right
    [InlineData(-80, -80, 7)]  // up-left
    public void PicksItemClockwiseFromTop(int dx, int dy, int expected) => Assert.Equal(expected, Ring(8).Pick(dx, dy));

    [Fact]
    public void SmallMovementSelectsNothing() => Assert.Equal(-1, Ring(8).Pick(10, -20));

    [Fact]
    public void RingRoundTripsThroughConfig()
    {
        var config = new AppConfig { Profiles = [new Profile { Buttons = { ["back"] = ActionRingAction.Defaults } }] };
        var parsed = ConfigStore.Parse(ConfigStore.Serialize(config));
        var ring = Assert.IsType<ActionRingAction>(parsed.DefaultProfile.Buttons["back"]);
        Assert.Equal(8, ring.Items.Count);
        Assert.Equal(new KeysAction("ctrl+c"), ring.Items[0].Action);
    }
}

public class RingAimTests
{
    private static readonly ActionRingAction Ring = new()
    {
        Items = Enumerable.Range(0, 8).Select(i => new RingItem($"{i}", new NoneAction())).ToList(),
        Threshold = 60,
        Reach = 180,
    };

    [Fact]
    public void AimIsClampedSoReversingRespondsImmediately()
    {
        var (x, y) = Ring.Aim(0, 0, 1000, 0);   // big overshoot to the right
        Assert.Equal(180, x, 3);
        (x, y) = Ring.Aim(x, y, -360, 0);        // one ring-width back
        Assert.Equal(-180, x, 3);
        Assert.Equal(6, Ring.Pick(x, y));        // now pointing left
    }

    [Fact]
    public void SelectionHoldsNearSliceBorder()
    {
        // 25 degrees clockwise from up: past the 22.5 degree border, but within the 8 degree tolerance.
        var angle = 25 * Math.PI / 180;
        var (x, y) = (Math.Sin(angle) * 150, -Math.Cos(angle) * 150);
        Assert.Equal(1, Ring.Pick(x, y));
        Assert.Equal(0, Ring.Pick(x, y, current: 0));
    }
}
