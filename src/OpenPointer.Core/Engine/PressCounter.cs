using OpenPointer.Config;

namespace OpenPointer.Engine;

/// <summary>What a <see cref="PressCounter"/> decided: start an action now, end it, or tap it.</summary>
public abstract record PressOutcome
{
    /// <summary>The button is still down: start the action; <see cref="End"/> follows on release.</summary>
    public sealed record Hold(ActionSpec Action, int Count) : PressOutcome;

    /// <summary>The held action ends.</summary>
    public sealed record End : PressOutcome;

    /// <summary>The presses are over and the button is up: run the action as a press and release.</summary>
    public sealed record Tap(ActionSpec Action, int Count) : PressOutcome;
}

/// <summary>
/// Turns presses of one button into single, double and triple presses. A press waits for the next
/// one only when a later press count has an action, so buttons without them respond instantly.
/// Holding the button past the wait starts that count's action as a hold, so the Actions Ring,
/// gestures and held keys still work. Not thread-safe: call it from one thread, including the
/// timer callbacks, which the owner schedules.
/// </summary>
public sealed class PressCounter
{
    private ActionSpec?[] _presses = [];
    private int _count;
    private bool _down;
    private bool _holding;

    /// <summary>Bumped on every press and release, so a timer from an earlier one does nothing.</summary>
    public int Generation { get; private set; }

    /// <summary>A press sequence is in progress (waiting for more presses, or holding).</summary>
    public bool Busy => _count > 0 || _holding;

    /// <param name="presses">Actions for 1..n presses, from <see cref="ContextMatcher.ResolvePresses"/>; used only when no sequence is in progress.</param>
    /// <param name="wait">Set when the owner must call <see cref="Expire"/> with <see cref="Generation"/> after the multi-press delay.</param>
    public PressOutcome? Down(ActionSpec?[] presses, out bool wait)
    {
        wait = false;
        if (_count == 0) _presses = presses;
        _count++;
        _down = true;
        Generation++;

        // Run straight away when no further press could change the outcome.
        if (_count >= LastConfigured())
        {
            var count = _count;
            var action = ActionFor(count);
            _count = 0;
            _holding = true;
            return new PressOutcome.Hold(action, count);
        }

        wait = true;
        return null;
    }

    public PressOutcome? Up(out bool wait)
    {
        wait = false;
        _down = false;
        Generation++;
        if (_holding)
        {
            _holding = false;
            return new PressOutcome.End();
        }

        // Released before the delay: wait for the next press.
        wait = _count > 0;
        return null;
    }

    /// <summary>The multi-press delay passed with no further press.</summary>
    public PressOutcome? Expire(int generation)
    {
        if (generation != Generation || _count == 0) return null;
        var action = ActionFor(_count);
        var count = _count;
        _count = 0;
        if (!_down) return new PressOutcome.Tap(action, count);
        _holding = true;
        return new PressOutcome.Hold(action, count);
    }

    private int LastConfigured()
    {
        for (var i = _presses.Length - 1; i > 0; i--)
            if (_presses[i] != null) return i + 1;
        return 1;
    }

    /// <summary>A count with no action of its own (double press when only triple is set) falls back to the single press.</summary>
    private ActionSpec ActionFor(int count) =>
        (count - 1 < _presses.Length ? _presses[count - 1] : null) ?? _presses.FirstOrDefault() ?? new NativeAction();
}
