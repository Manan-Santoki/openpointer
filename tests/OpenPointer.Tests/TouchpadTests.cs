using OpenPointer.Config;
using OpenPointer.Engine;
using OpenPointer.Platform;

namespace OpenPointer.Tests;

public class TouchpadRecognizerTests
{
    private static (TouchpadRecognizer Recognizer, List<string> Fired, List<bool> Locks) Create(params string[] active)
    {
        var recognizer = new TouchpadRecognizer { Aspect = 1.6, Active = active.Select(TouchpadGestures.Key).ToHashSet(), CornersNeedHold = false };
        var fired = new List<string>();
        var locks = new List<bool>();
        recognizer.Fired += key => fired.Add(key[TouchpadGestures.Prefix.Length..]);
        recognizer.PointerLock += locks.Add;
        return (recognizer, fired, locks);
    }

    /// <summary>Plays one touch: each step is the positions of the fingers on the pad (0..1).</summary>
    private static void Play(TouchpadRecognizer recognizer, IEnumerable<(double X, double Y)[]> steps, int msPerStep = 10, bool palm = false)
    {
        long tick = 1000;
        foreach (var fingers in steps)
        {
            recognizer.Feed(fingers.Select((p, i) => new TouchContact(i, p.X, p.Y, true, !palm)).ToList(), tick);
            tick += msPerStep;
        }
        recognizer.Feed([], tick);
    }

    private static IEnumerable<(double X, double Y)[]> Line((double X, double Y) from, (double X, double Y) to, int steps = 20) =>
        Enumerable.Range(0, steps + 1).Select(i => new[] { (from.X + (to.X - from.X) * i / steps, from.Y + (to.Y - from.Y) * i / steps) });

    [Fact]
    public void CornerTapFiresOnlyForAnAssignedCorner()
    {
        var (recognizer, fired, _) = Create("corner-top-right");
        Play(recognizer, [[(0.98, 0.03)], [(0.98, 0.03)]]);
        Play(recognizer, [[(0.02, 0.03)], [(0.02, 0.03)]]);
        Assert.Equal(["corner-top-right"], fired);
    }

    [Fact]
    public void WithTapToClickCornersNeedAHoldInstead()
    {
        var (recognizer, fired, _) = Create("corner-bottom-left");
        recognizer.CornersNeedHold = true;
        Play(recognizer, [[(0.02, 0.97)], [(0.02, 0.97)]]);
        Assert.Empty(fired);
        Play(recognizer, Enumerable.Repeat(new[] { (0.02, 0.97) }, 60), msPerStep: 10);
        Assert.Equal(["corner-bottom-left"], fired);
    }

    [Fact]
    public void EdgeSlideRepeatsPerStepAndHoldsThePointer()
    {
        var (recognizer, fired, locks) = Create("edge-right-down", "edge-right-up");
        Play(recognizer, Line((0.99, 0.1), (0.99, 0.6)));
        Assert.True(fired.Count >= 5);
        Assert.All(fired, key => Assert.Equal("edge-right-down", key));
        Assert.Equal([true, false], locks);
    }

    [Fact]
    public void MovingAwayFromTheEdgeIsAnOrdinaryPointerMove()
    {
        var (recognizer, fired, locks) = Create("edge-left-down");
        Play(recognizer, Line((0.02, 0.3), (0.6, 0.5)));
        Assert.Empty(fired);
        Assert.Empty(locks);
    }

    [Fact]
    public void QuickFlickFromAnEdgeIsASwipeIn()
    {
        var (recognizer, fired, _) = Create("swipe-in-left");
        Play(recognizer, Line((0.01, 0.5), (0.35, 0.5), 10));
        Assert.Equal(["swipe-in-left"], fired);
    }

    [Theory]
    [InlineData(true, "circle-cw")]
    [InlineData(false, "circle-ccw")]
    public void CirclesNeedTwoTurnsThenFireOncePerTurn(bool clockwise, string expected)
    {
        var (recognizer, fired, locks) = Create("circle-cw", "circle-ccw");
        var points = Enumerable.Range(0, 72 * 3 + 1).Select(i =>
        {
            var angle = (clockwise ? 1 : -1) * i * 2 * Math.PI / 72;
            return new[] { (0.5 + 0.15 * Math.Cos(angle) / 1.6, 0.5 + 0.15 * Math.Sin(angle)) };
        });
        Play(recognizer, points);
        Assert.Equal([expected, expected], fired); // after two turns, then the third
        Assert.Equal([true, false], locks);

        fired.Clear();
        Play(recognizer, points.Take(72 + 60)); // under two turns: an ordinary curvy move
        Assert.Empty(fired);
    }

    [Theory]
    [InlineData(0.04, 1.0)] // a tiny jiggle that turns a lot
    [InlineData(0.15, 0.2)] // a flat zigzag: wide but squashed
    public void SmallOrFlatLoopsAreNotCircles(double radius, double squash)
    {
        var (recognizer, fired, locks) = Create("circle-cw", "circle-ccw");
        Play(recognizer, Enumerable.Range(0, 72 * 4).Select(i =>
        {
            var angle = i * 2 * Math.PI / 72;
            return new[] { (0.5 + radius * Math.Cos(angle) / 1.6, 0.5 + radius * squash * Math.Sin(angle)) };
        }));
        Assert.Empty(fired);
        Assert.Empty(locks);
    }

    [Fact]
    public void MultiFingerSwipesAndTaps()
    {
        var (recognizer, fired, _) = Create("3-up", "4-tap", "5-tap");
        Play(recognizer, Enumerable.Range(0, 20).Select(i => new[] { (0.4, 0.8 - i * 0.03), (0.5, 0.8 - i * 0.03), (0.6, 0.8 - i * 0.03) }));
        Play(recognizer, [[(0.3, 0.5), (0.4, 0.5), (0.5, 0.5), (0.6, 0.5)], [(0.3, 0.5), (0.4, 0.5), (0.5, 0.5), (0.6, 0.5)]]);
        Play(recognizer, [[(0.2, 0.5), (0.3, 0.5), (0.4, 0.5), (0.5, 0.5), (0.6, 0.5)]]);
        Assert.Equal(["3-up", "4-tap", "5-tap"], fired);
    }

    [Fact]
    public void PalmsNeverTrigger()
    {
        var (recognizer, fired, _) = Create("corner-top-left", "edge-left-down");
        Play(recognizer, [[(0.02, 0.03)], [(0.02, 0.03)]], palm: true);
        Play(recognizer, Line((0.01, 0.1), (0.01, 0.8)), palm: true);
        Assert.Empty(fired);
    }
}

public class WindowsTouchpadSettingsTests
{
    [Fact]
    public void WindowsOwnedGesturesAreLockedAndFreeOnesAreNot()
    {
        var settings = new WindowsTouchpadSettings(TapToClick: true, ThreeSwipe: 1, FourSwipe: 0, ThreeTap: 2, FourTap: 0);
        Assert.Equal("Switch apps and show desktop", settings.Owner(TouchpadGestures.Key("3-left")));
        Assert.Equal("Action Center", settings.Owner(TouchpadGestures.Key("3-tap")));
        Assert.Null(settings.Owner(TouchpadGestures.Key("4-up")));
        Assert.Null(settings.Owner(TouchpadGestures.Key("4-tap")));
        Assert.Null(settings.Owner(TouchpadGestures.Key("5-tap")));
        Assert.StartsWith("Play/pause", (settings with { FourTap = 3 }).Owner(TouchpadGestures.Key("5-tap")));
        Assert.Null(settings.Owner(TouchpadGestures.Key("corner-top-left")));
        Assert.True(settings.CornersNeedHold);
    }

    [Fact]
    public void GestureKeysAreUniqueAndLiveInProfilesLikeButtons()
    {
        Assert.Equal(TouchpadGestures.All.Count, TouchpadGestures.All.Select(g => g.Key).Distinct().Count());
        var config = AppConfig.CreateDefault();
        config.DefaultProfile.Buttons[TouchpadGestures.Key("circle-cw")] = new KeysAction("volumeup");
        var roundTrip = ConfigStore.Parse(ConfigStore.Serialize(config));
        Assert.Equal(new KeysAction("volumeup"), ContextMatcher.ResolveButton(roundTrip, FocusContext.Unknown, TouchpadGestures.Key("circle-cw")));
    }
}

public class RunawayGuardTests
{
    [Fact]
    public void TheSameAppOrPageOpensAtMostOncePerSecond()
    {
        var target = $"test-{Guid.NewGuid():N}";
        Assert.True(ActionRunner.LaunchAllowed(target, 10_000));
        Assert.False(ActionRunner.LaunchAllowed(target, 10_400));
        Assert.False(ActionRunner.LaunchAllowed(target.ToUpperInvariant(), 10_900));
        Assert.True(ActionRunner.LaunchAllowed(target, 11_000));
        Assert.True(ActionRunner.LaunchAllowed(target + "-other", 11_001));
    }

    [Fact]
    public void OnlyKeysAndScrollingRepeatWithinOneTouchAndThenSlowlyAndBoundedly()
    {
        var launch = new LaunchAction("explorer.exe");
        var volume = new KeysAction("volume_up");
        Assert.True(DeviceManager.MayRepeat(launch, 0, 0));
        Assert.False(DeviceManager.MayRepeat(launch, 1, 10_000));
        Assert.False(DeviceManager.MayRepeat(new MacroAction([new(launch)]), 1, 10_000));
        Assert.False(DeviceManager.MayRepeat(new TextAction("hi"), 1, 10_000));
        Assert.True(DeviceManager.MayRepeat(volume, 1, DeviceManager.RepeatIntervalMs));
        Assert.False(DeviceManager.MayRepeat(volume, 1, DeviceManager.RepeatIntervalMs - 1));
        Assert.False(DeviceManager.MayRepeat(volume, DeviceManager.MaxRepeatsPerTouch, 10_000));
    }

    [Fact]
    public void AFrozenPointerFreesItselfWhenTouchpadInputStops()
    {
        var router = new WindowsMouseRouter(_ => null, _ => { });
        router.HoldPointerUntil(Environment.TickCount64 + 10_000);
        Assert.True(router.Process(0x0200, 0, 5, 5));
        router.HoldPointerUntil(Environment.TickCount64 - 1);
        Assert.False(router.Process(0x0200, 0, 5, 5));
        router.HoldPointerUntil(Environment.TickCount64 + 10_000);
        Assert.False(router.Process(0x0200, 0, 5, 5, flags: 1)); // injected moves always pass
    }

    [Fact]
    public void PressingThePadIsAClickNotACornerGesture()
    {
        var recognizer = new TouchpadRecognizer { Active = new HashSet<string> { TouchpadGestures.Key("corner-bottom-right") }, CornersNeedHold = true };
        var fired = new List<string>();
        recognizer.Fired += fired.Add;
        for (var t = 0; t < 1000; t += 10) recognizer.Feed([new TouchContact(0, 0.97, 0.97, true, true)], t, buttonDown: t > 100);
        recognizer.Feed([], 1000);
        Assert.Empty(fired);
    }
}
