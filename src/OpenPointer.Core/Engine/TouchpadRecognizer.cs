using OpenPointer.Config;
using OpenPointer.Platform;

namespace OpenPointer.Engine;

/// <summary>
/// Turns precision touchpad frames into OpenPointer gestures. Only gestures in <see cref="Active"/>
/// are recognized, so an unassigned corner or edge behaves exactly as Windows makes it.
/// One touch, from the first finger down to the last finger up, produces at most one kind of gesture.
/// </summary>
internal sealed class TouchpadRecognizer
{
    // Zones and distances are in pad heights, so they feel the same on wide and narrow pads.
    internal const double CornerX = TouchpadGestures.CornerX, CornerY = TouchpadGestures.CornerY, EdgeBand = TouchpadGestures.EdgeBand, EdgeKeep = 0.16, EdgeStep = 0.08, SwipeIn = 0.25,
        Still = 0.03, MultiSwipe = 0.2;
    internal const int CircleTurns = 2;
    /// <summary>
    /// A circle must span at least this many pad heights each way (about 15 mm on a typical pad) and
    /// be roughly round; tiny jiggles and flat zigzags turn just as much but are not circles.
    /// </summary>
    internal const double MinCircle = 0.2, MaxCircleStretch = 2.5, HeadingStep = 0.012;
    internal const int TapMs = 250, HoldMs = 450, SwipeInMs = 350;

    private enum Mode { Undecided, Edge, Circle, Done, Ignored }

    private Mode _mode;
    private long _start;
    private int _maxFingers;
    private (double X, double Y) _first, _last, _anchor, _centroidStart, _centroid;
    private double _heading, _turn;
    private bool _hasHeading, _locked;
    private (double MinX, double MinY, double MaxX, double MaxY) _box;
    private string? _edge;

    /// <summary>Gesture keys with an action in the focused app; nothing else is recognized.</summary>
    public IReadOnlySet<string> Active { get; set; } = new HashSet<string>();

    public bool CornersNeedHold { get; set; } = true;

    /// <summary>Pad width over height; X is scaled by it so circles and distances are round.</summary>
    public double Aspect { get; set; } = 1.6;

    /// <summary>Raised with a gesture key each time it fires (repeating gestures fire several times).</summary>
    public event Action<string>? Fired;

    /// <summary>Raised when a one-finger gesture takes over, so the pointer stops following the finger.</summary>
    public event Action<bool>? PointerLock;

    /// <summary>Raised when a new touch begins.</summary>
    public event Action? TouchStarted;

    /// <param name="buttonDown">The pad is physically pressed: a click, never a gesture (a thumb resting in a corner to click).</param>
    public void Feed(IReadOnlyList<TouchContact> contacts, long tick, bool buttonDown = false)
    {
        var touching = contacts.Where(c => c.Touching).ToList();
        if (touching.Count == 0)
        {
            if (_maxFingers > 0) End(tick);
            return;
        }

        var points = touching.Select(c => (X: c.X * Aspect, c.Y)).ToList();
        var centroid = (X: points.Average(p => p.X), Y: points.Average(p => p.Y));
        if (_maxFingers == 0)
        {
            _start = tick;
            _mode = Mode.Undecided;
            _first = _last = _anchor = points[0];
            _centroidStart = _centroid = centroid;
            _turn = 0;
            _box = (points[0].X, points[0].Y, points[0].X, points[0].Y);
            _hasHeading = false;
            _edge = null;
            TouchStarted?.Invoke();
        }

        // Palms and accidental contacts never trigger anything.
        if (touching.Any(c => !c.Confident) || buttonDown && !_locked) _mode = Mode.Ignored;
        if (touching.Count > _maxFingers)
        {
            _maxFingers = touching.Count;
            _centroidStart = centroid;
            if (_maxFingers > 1 && _mode is Mode.Edge or Mode.Circle) _mode = Mode.Ignored;
        }
        _centroid = centroid;
        if (_mode is Mode.Done or Mode.Ignored) return;

        if (_maxFingers == 1) OneFinger(points[0], tick);
        else if (_maxFingers is 3 or 4) MultiFinger(tick);
    }

    private void OneFinger((double X, double Y) point, long tick)
    {
        var moved = Distance(point, _first);
        var step = Distance(point, _last);
        _box = (Math.Min(_box.MinX, point.X), Math.Min(_box.MinY, point.Y), Math.Max(_box.MaxX, point.X), Math.Max(_box.MaxY, point.Y));
        // Ignore sensor jitter: only steps of about a millimetre change the heading.
        if (step > HeadingStep)
        {
            var heading = Math.Atan2(point.Y - _last.Y, point.X - _last.X);
            if (_hasHeading) _turn += Wrap(heading - _heading);
            _heading = heading;
            _hasHeading = true;
            _last = point;
        }

        if (_mode == Mode.Undecided)
        {
            if (Corner(_first) is { } corner && CornersNeedHold && moved < Still && tick - _start >= HoldMs)
            {
                Fire(corner);
                _mode = Mode.Done;
                return;
            }

            if (SwipeInEdge(_first) is { } from && tick - _start <= SwipeInMs && Inward(from, point) >= SwipeIn)
            {
                Lock(true);
                Fire(TouchpadGestures.Key($"swipe-in-{from}"));
                _mode = Mode.Done;
                return;
            }

            _edge ??= SlideEdge(_first, point);
            if (_edge != null) _mode = Mode.Edge;
            else if (moved > Still && Math.Abs(_turn) > Math.PI / 2 && HasCircles) _mode = Mode.Circle;
        }

        if (_mode == Mode.Edge) Slide(point);
        if (_mode == Mode.Circle) Circle();
    }

    private void Slide((double X, double Y) point)
    {
        var vertical = _edge is "left" or "right";
        var depth = _edge switch { "left" => point.X, "right" => Aspect - point.X, "top" => point.Y, _ => 1 - point.Y };
        if (depth > EdgeKeep)
        {
            // Left the edge: a normal pointer move after all, unless it already fired.
            _mode = _locked ? Mode.Done : Mode.Undecided;
            if (!_locked) _edge = null;
            return;
        }

        var along = vertical ? point.Y - _anchor.Y : point.X - _anchor.X;
        while (Math.Abs(along) >= EdgeStep)
        {
            var direction = vertical ? (along > 0 ? "down" : "up") : (along > 0 ? "right" : "left");
            var key = TouchpadGestures.Key($"edge-{_edge}-{direction}");
            if (!Active.Contains(key)) return;
            Lock(true);
            Fire(key);
            var sign = Math.Sign(along);
            _anchor = vertical ? (_anchor.X, _anchor.Y + sign * EdgeStep) : (_anchor.X + sign * EdgeStep, _anchor.Y);
            along -= sign * EdgeStep;
        }
    }

    private void Circle()
    {
        // Turns of the finger's heading; screen Y grows downward, so positive is clockwise. The first
        // action needs two full circles so a curvy pointer move never triggers it; then one per turn.
        var needed = 2 * Math.PI * (_locked ? 0.92 : CircleTurns - 0.08);
        while (Math.Abs(_turn) >= needed)
        {
            var width = _box.MaxX - _box.MinX;
            var height = _box.MaxY - _box.MinY;
            if (Math.Min(width, height) < MinCircle || Math.Max(width, height) > MaxCircleStretch * Math.Min(width, height))
            {
                // Turned a lot without drawing a real circle: start counting again from here.
                _turn = 0;
                _box = (_last.X, _last.Y, _last.X, _last.Y);
                return;
            }

            var key = TouchpadGestures.Key(_turn > 0 ? "circle-cw" : "circle-ccw");
            if (!Active.Contains(key)) { _mode = Mode.Ignored; return; }
            _turn -= Math.Sign(_turn) * 2 * Math.PI * (_locked ? 1 : CircleTurns);
            Lock(true);
            Fire(key);
            _box = (_last.X, _last.Y, _last.X, _last.Y); // each further turn must be a real circle too
            needed = 2 * Math.PI * 0.92;
        }
    }

    private void MultiFinger(long tick)
    {
        var dx = _centroid.X - _centroidStart.X;
        var dy = _centroid.Y - _centroidStart.Y;
        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) < MultiSwipe) return;
        var direction = Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? "right" : "left") : (dy > 0 ? "down" : "up");
        Fire(TouchpadGestures.Key($"{_maxFingers}-{direction}"));
        _mode = Mode.Done;
    }

    private void End(long tick)
    {
        var quick = tick - _start <= TapMs;
        if (_mode == Mode.Undecided && quick)
        {
            if (_maxFingers == 1 && !CornersNeedHold && Corner(_first) is { } corner && Distance(_last, _first) < Still) Fire(corner);
            else if (_maxFingers >= 3 && Distance(_centroid, _centroidStart) < Still * 2) Fire(TouchpadGestures.Key($"{Math.Min(_maxFingers, 5)}-tap"));
        }

        if (_locked) Lock(false);
        _maxFingers = 0;
        _mode = Mode.Undecided;
    }

    private string? Corner((double X, double Y) p)
    {
        var x = p.X / Aspect;
        var horizontal = x < CornerX ? "left" : x > 1 - CornerX ? "right" : null;
        var vertical = p.Y < CornerY ? "top" : p.Y > 1 - CornerY ? "bottom" : null;
        if (horizontal == null || vertical == null) return null;
        var key = TouchpadGestures.Key($"corner-{vertical}-{horizontal}");
        return Active.Contains(key) ? key : null;
    }

    /// <summary>The edge a touch starting here could swipe in from, if that swipe is assigned.</summary>
    private string? SwipeInEdge((double X, double Y) p)
    {
        foreach (var edge in EdgesAt(p))
            if (Active.Contains(TouchpadGestures.Key($"swipe-in-{edge}"))) return edge;
        return null;
    }

    private double Inward(string edge, (double X, double Y) p) => edge switch
    {
        "left" => p.X - _first.X,
        "right" => _first.X - p.X,
        "top" => p.Y - _first.Y,
        _ => _first.Y - p.Y,
    };

    /// <summary>The edge a touch is sliding along: it started in that edge's band and moves parallel to it.</summary>
    private string? SlideEdge((double X, double Y) start, (double X, double Y) p)
    {
        var dx = Math.Abs(p.X - start.X);
        var dy = Math.Abs(p.Y - start.Y);
        if (Math.Max(dx, dy) < Still) return null;
        foreach (var edge in EdgesAt(start))
        {
            var vertical = edge is "left" or "right";
            if ((vertical ? dy > dx * 1.5 : dx > dy * 1.5) && EdgeAssigned(edge)) return edge;
        }
        return null;
    }

    private IEnumerable<string> EdgesAt((double X, double Y) p)
    {
        if (p.X < EdgeBand) yield return "left";
        if (p.X > Aspect - EdgeBand) yield return "right";
        if (p.Y < EdgeBand) yield return "top";
        if (p.Y > 1 - EdgeBand) yield return "bottom";
    }

    private bool EdgeAssigned(string edge) => Active.Any(k => k.StartsWith(TouchpadGestures.Key($"edge-{edge}-"), StringComparison.Ordinal));

    private bool HasCircles => Active.Contains(TouchpadGestures.Key("circle-cw")) || Active.Contains(TouchpadGestures.Key("circle-ccw"));

    private void Fire(string key)
    {
        if (Active.Contains(key)) Fired?.Invoke(key);
    }

    private void Lock(bool on)
    {
        if (_locked == on) return;
        _locked = on;
        PointerLock?.Invoke(on);
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static double Wrap(double angle) => angle > Math.PI ? angle - 2 * Math.PI : angle < -Math.PI ? angle + 2 * Math.PI : angle;
}
