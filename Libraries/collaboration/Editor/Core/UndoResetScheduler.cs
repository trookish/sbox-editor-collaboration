using System;

namespace TeamCreate;

/// <summary>
/// Decides when the native undo history is reset after remote changes arrive. The engine's undo entries snapshot whole editor state, so after a
/// remote change is applied the history has to be reset (<c>UndoSystem.Initialize()</c>) or a later Ctrl+Z could restore a snapshot that
/// contains none of it and silently revert other people's work. That reset is a full snapshot, and a remote drag arrives at about 20 packets a
/// second, so doing it per packet costs a full reset each time. The scene is still applied at once; only the history reset is coalesced here:
/// at most one per <see cref="IntervalMs"/> while changes keep arriving, and one more when they stop, so the final state is always reset exactly once.
///
/// Hazard, by design and bounded: between a remote apply and the coalesced reset (at most <see cref="IntervalMs"/>) a native Ctrl+Z could restore
/// a stale snapshot. The library cannot see the keystroke, but every undo it can see goes through <c>CollaborationSession.Undo</c> and
/// <c>TryRedo</c>, and those call <see cref="Flush"/> first, so a reset still pending is run before any undo or redo acts.
///
/// Time is injected as milliseconds from any monotonic clock the caller owns, which keeps this pure. A reading that goes backwards or is not a
/// finite number never throws and never leaves a reset stuck.
/// </summary>
public sealed class UndoResetScheduler
{
    /// <summary>The shortest gap between two runs while requests keep coming, and the quiet gap after which a held request runs.</summary>
    public const double IntervalMs = 250;

    private bool pending;

    private double lastRequestMs;

    private double? lastRunMs;

    /// <summary>True while a requested reset has not run yet.</summary>
    public bool Pending => pending;

    /// <summary>Marks a reset as needed because a remote change was applied at <paramref name="nowMs"/>.</summary>
    public void Request( double nowMs )
    {
        pending = true;
        // An unusable reading counts as "long ago", so the quiet-gap rule releases the request instead of waiting on a number that never arrives.
        lastRequestMs = double.IsFinite( nowMs ) ? nowMs : double.NegativeInfinity;
    }

    /// <summary>
    /// True when the caller should run the reset now (which also consumes the request). A request runs at once if nothing ran in the last
    /// <see cref="IntervalMs"/>; otherwise it is held and runs when that interval ends, or when <see cref="IntervalMs"/> have passed since the
    /// last request, whichever comes first. The second rule is what releases the final request after a clock that stepped backwards.
    /// </summary>
    public bool ShouldRunNow( double nowMs )
    {
        if ( !pending ) return false;
        if ( !double.IsFinite( nowMs ) )
        {
            // Better one reset too many than a pending reset that never runs.
            pending = false;
            lastRunMs = null;
            return true;
        }
        bool intervalOver = lastRunMs == null || nowMs - lastRunMs.Value >= IntervalMs;
        bool quiet = nowMs - lastRequestMs >= IntervalMs;
        if ( !intervalOver && !quiet ) return false;
        pending = false;
        lastRunMs = nowMs;
        return true;
    }

    /// <summary>
    /// For an undo or redo about to act: true when a reset was pending, which the caller must run first. Clears the request and forgets the last
    /// run, so the next request after the undo is not held back by an interval that belonged to the state before it.
    /// </summary>
    public bool Flush()
    {
        bool was = pending;
        pending = false;
        if ( was ) lastRunMs = null;
        return was;
    }
}
