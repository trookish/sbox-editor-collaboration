using System;
using System.Collections.Generic;
using System.Linq;

namespace TeamCreate;

/// <summary>
/// When the local editor sends the pose of an object being dragged. Each selected object is observed every frame; a pose goes out at most every 33 ms
/// per object (the presence floor, <see cref="SendCadence.PresenceMinimum"/>) and only when it differs from the last one sent by at least 0.001 in
/// position or scale or 0.0005 in rotation, so an object that is held still sends nothing and an idle session sends nothing at all.
/// </summary>
/// <remarks>
/// The first observation of an object is only a baseline: selecting an object, or pressing the mouse on it without moving it, sends nothing. The
/// receivers already hold that pose (it is the state the host confirmed). When more objects are due than one packet carries
/// (<see cref="TransientLimits.MaxItems"/>) the ones that moved most recently go first and the others are due again on the next tick.
/// </remarks>
public sealed class TransientCadence
{
    public const double MinIntervalMs = 33;

    public const float PositionThreshold = 0.001f;

    public const float RotationThreshold = 0.0005f;

    /// <summary>An object counts as being dragged by this editor for this long after it last moved.</summary>
    public const double MovingQuietMs = 250;

    private sealed class State
    {
        public Pose Observed;
        public double ChangedAtMs;
        public bool HasSent;
        public Pose Sent;
        public double SentAtMs = double.NegativeInfinity;
    }

    private readonly Dictionary<string, State> states = new Dictionary<string, State>( StringComparer.Ordinal );

    /// <summary>Whether two poses differ enough to be worth a packet. A quaternion and its negation are the same rotation and do not differ.</summary>
    public static bool Differs( Pose a, Pose b )
    {
        if ( Math.Abs( a.X - b.X ) >= PositionThreshold || Math.Abs( a.Y - b.Y ) >= PositionThreshold || Math.Abs( a.Z - b.Z ) >= PositionThreshold )
        {
            return true;
        }
        if ( Math.Abs( a.Sx - b.Sx ) >= PositionThreshold || Math.Abs( a.Sy - b.Sy ) >= PositionThreshold || Math.Abs( a.Sz - b.Sz ) >= PositionThreshold )
        {
            return true;
        }
        float same = Math.Max( Math.Max( Math.Abs( a.Qx - b.Qx ), Math.Abs( a.Qy - b.Qy ) ), Math.Max( Math.Abs( a.Qz - b.Qz ), Math.Abs( a.Qw - b.Qw ) ) );
        float opposite = Math.Max( Math.Max( Math.Abs( a.Qx + b.Qx ), Math.Abs( a.Qy + b.Qy ) ), Math.Max( Math.Abs( a.Qz + b.Qz ), Math.Abs( a.Qw + b.Qw ) ) );
        return Math.Min( same, opposite ) >= RotationThreshold;
    }

    /// <summary>Records where an object is now. A pose that is not finite is ignored.</summary>
    public void Update( string id, Pose pose, double nowMs )
    {
        if ( string.IsNullOrEmpty( id ) || !IsFinite( pose ) )
        {
            return;
        }
        if ( !states.TryGetValue( id, out State state ) )
        {
            states[id] = new State { Observed = pose, ChangedAtMs = double.NegativeInfinity, HasSent = true, Sent = pose };
            return;
        }
        if ( Differs( state.Observed, pose ) )
        {
            state.ChangedAtMs = nowMs;
        }
        state.Observed = pose;
    }

    /// <summary>
    /// The objects to send at <paramref name="nowMs"/>, newest-moved first and at most <paramref name="max"/>. Those returned count as sent; those that
    /// did not fit stay due.
    /// </summary>
    public List<TransientItem> TakeDue( double nowMs, int max = TransientLimits.MaxItems )
    {
        var due = states.Where( pair => nowMs - pair.Value.SentAtMs >= MinIntervalMs && Differs( pair.Value.Sent, pair.Value.Observed ) )
            .OrderByDescending( pair => pair.Value.ChangedAtMs )
            .Take( max )
            .ToList();
        var items = new List<TransientItem>( due.Count );
        foreach ( var pair in due )
        {
            pair.Value.Sent = pair.Value.Observed;
            pair.Value.SentAtMs = nowMs;
            pair.Value.HasSent = true;
            items.Add( new TransientItem( pair.Key, pair.Value.Observed ) );
        }
        return items;
    }

    /// <summary>
    /// The drag is over: the exact last pose of every object that was sent at all, so the receivers end on it, and all state is forgotten. An object
    /// that was only selected and never moved sends nothing, not even this.
    /// </summary>
    public List<TransientItem> TakeFinal()
    {
        var items = states.Where( pair => double.IsFinite( pair.Value.SentAtMs ) || Differs( pair.Value.Sent, pair.Value.Observed ) )
            .OrderByDescending( pair => pair.Value.ChangedAtMs )
            .Take( TransientLimits.MaxItems )
            .Select( pair => new TransientItem( pair.Key, pair.Value.Observed ) )
            .ToList();
        states.Clear();
        return items;
    }

    /// <summary>True while this editor itself is moving the object (or did so a moment ago): a remote transient for it is then dropped, local wins.</summary>
    public bool IsMoving( string id, double nowMs )
    {
        return id != null && states.TryGetValue( id, out State state ) && nowMs - state.ChangedAtMs < MovingQuietMs;
    }

    public void Clear() => states.Clear();

    private static bool IsFinite( Pose p )
    {
        return float.IsFinite( p.X ) && float.IsFinite( p.Y ) && float.IsFinite( p.Z )
            && float.IsFinite( p.Qx ) && float.IsFinite( p.Qy ) && float.IsFinite( p.Qz ) && float.IsFinite( p.Qw )
            && float.IsFinite( p.Sx ) && float.IsFinite( p.Sy ) && float.IsFinite( p.Sz );
    }
}
