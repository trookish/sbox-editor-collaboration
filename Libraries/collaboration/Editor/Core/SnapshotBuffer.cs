using System;

namespace TeamCreate;

/// <summary>
/// Where something is: position, rotation as a quaternion (X, Y, Z, W) and scale. Plain floats, no engine types, so the same value can be
/// filled from a presence packet, a drag packet, or a scene object. Scale defaults to 1 so that callers that only track a position and
/// rotation (a collaborator's camera) need not mention it.
/// </summary>
public readonly record struct Pose( float X, float Y, float Z, float Qx, float Qy, float Qz, float Qw, float Sx = 1f, float Sy = 1f, float Sz = 1f );

/// <summary>
/// Keeps a short history of timestamped poses for one remote thing (a collaborator's camera, or an object being dragged) and answers
/// "where was it at time t". The caller asks for a time slightly in the past, so that two samples bracket it and the answer is a blend between
/// them rather than the last raw packet, which is what makes remote motion look continuous at 30 packets a second.
/// </summary>
/// <remarks>
/// Times are the sender's own monotonic milliseconds, converted by <see cref="ClockOffset"/>; the buffer only compares them and never reads
/// a clock itself. Between two samples position and scale are interpolated linearly and rotation by normalised lerp on the short path. After
/// the newest sample the position alone continues along the velocity of the last two samples, for at most <see cref="MaxExtrapolationMs"/>,
/// and then holds: a collaborator who stopped (or whose packets stopped) must not drift away on a stale guess. Rotation and scale are only ever held
/// past the newest sample: they have no sensible velocity to project. The buffer never allocates after construction; it is sampled every
/// frame for each remote thing in view.
/// </remarks>
public sealed class SnapshotBuffer
{
    /// <summary>About a second of history at 30 packets a second; far more than the delay ever needs.</summary>
    public const int Capacity = 32;

    /// <summary>
    /// How long past the newest sample the position keeps moving. Longer than a few lost packets is a guess about what the person did
    /// next, and a wrong guess is more visible than a pause.
    /// </summary>
    public const double MaxExtrapolationMs = 120;

    private readonly double[] times = new double[Capacity];
    private readonly Pose[] poses = new Pose[Capacity];
    private int count;

    public int Count => count;

    /// <summary>
    /// Stores a sample, keeping the history sorted by time. A sample with the same timestamp as one already held replaces it; past
    /// <see cref="Capacity"/> the oldest is dropped. A non-finite timestamp or pose component is ignored (it is not stored and replaces nothing):
    /// one bad packet must not put NaN into the rendered transform.
    /// </summary>
    public void Add( double timeMs, Pose pose )
    {
        if ( !double.IsFinite( timeMs ) || !IsFinite( pose ) )
        {
            return;
        }

        // Packets nearly always arrive in order, so search from the newest end.
        int at = count;
        while ( at > 0 && times[at - 1] > timeMs )
        {
            at--;
        }
        if ( at > 0 && times[at - 1] == timeMs )
        {
            poses[at - 1] = pose;
            return;
        }

        if ( count == Capacity )
        {
            // Full: the oldest sample goes. If the new one is older than everything held, it is the oldest and is the one dropped.
            if ( at == 0 )
            {
                return;
            }
            Array.Copy( times, 1, times, 0, count - 1 );
            Array.Copy( poses, 1, poses, 0, count - 1 );
            count--;
            at--;
        }
        if ( at < count )
        {
            Array.Copy( times, at, times, at + 1, count - at );
            Array.Copy( poses, at, poses, at + 1, count - at );
        }
        times[at] = timeMs;
        poses[at] = pose;
        count++;
    }

    public void Clear()
    {
        count = 0;
    }

    /// <summary>
    /// The pose at <paramref name="renderTimeMs"/>. False when the buffer is empty or the time is not finite (nothing sensible to return);
    /// <paramref name="pose"/> is then the default value. Before the first sample the first pose is held, exactly on a sample that sample is returned
    /// unchanged, between two samples they are blended, and after the last the position is extrapolated for a limited time and then held.
    /// </summary>
    public bool TrySample( double renderTimeMs, out Pose pose )
    {
        pose = default;
        if ( count == 0 || !double.IsFinite( renderTimeMs ) )
        {
            return false;
        }
        if ( renderTimeMs <= times[0] )
        {
            pose = poses[0];
            return true;
        }

        // The render time is just behind the newest samples, so scan from the end for the last sample at or before it.
        int i = count - 1;
        while ( times[i] > renderTimeMs )
        {
            i--;
        }
        if ( times[i] == renderTimeMs )
        {
            pose = poses[i];
            return true;
        }
        pose = i == count - 1 ? Extrapolate( i, renderTimeMs ) : Blend( poses[i], poses[i + 1], (renderTimeMs - times[i]) / (times[i + 1] - times[i]) );
        return true;
    }

    /// <summary>Position only, along the velocity of the last two samples; everything else is held from the newest sample.</summary>
    private Pose Extrapolate( int last, double renderTimeMs )
    {
        var newest = poses[last];
        if ( last < 1 )
        {
            return newest;
        }
        var previous = poses[last - 1];
        // Strictly increasing times (a duplicate replaces) make the span positive.
        double k = Math.Min( renderTimeMs - times[last], MaxExtrapolationMs ) / (times[last] - times[last - 1]);
        double x = newest.X + (newest.X - previous.X) * k;
        double y = newest.Y + (newest.Y - previous.Y) * k;
        double z = newest.Z + (newest.Z - previous.Z) * k;
        // Two samples a hair apart in time can give an enormous velocity; if that overflows, hold rather than render garbage.
        if ( !IsRepresentable( x ) || !IsRepresentable( y ) || !IsRepresentable( z ) )
        {
            return newest;
        }
        return newest with { X = (float)x, Y = (float)y, Z = (float)z };
    }

    private static bool IsRepresentable( double v ) => double.IsFinite( v ) && Math.Abs( v ) <= float.MaxValue;

    private static Pose Blend( Pose a, Pose b, double u )
    {
        // q and -q are the same rotation. If the two quaternions point into opposite hemispheres, blending toward b directly would swing
        // the long way round (through zero, for a pair like identity and its negation), so blend toward -b instead.
        double dot = (double)a.Qx * b.Qx + (double)a.Qy * b.Qy + (double)a.Qz * b.Qz + (double)a.Qw * b.Qw;
        double sign = dot < 0 ? -1.0 : 1.0;
        double qx = a.Qx + (sign * b.Qx - a.Qx) * u;
        double qy = a.Qy + (sign * b.Qy - a.Qy) * u;
        double qz = a.Qz + (sign * b.Qz - a.Qz) * u;
        double qw = a.Qw + (sign * b.Qw - a.Qw) * u;
        double length = Math.Sqrt( qx * qx + qy * qy + qz * qz + qw * qw );
        if ( length > 1e-9 )
        {
            qx /= length;
            qy /= length;
            qz /= length;
            qw /= length;
        }
        else
        {
            // Unit quaternions on the same hemisphere never blend to nothing, so this is only reached with an invalid (zero-length) input.
            // Use the earlier rotation if it is a rotation at all, otherwise no rotation, rather than dividing by zero.
            double aLength = Math.Sqrt( (double)a.Qx * a.Qx + (double)a.Qy * a.Qy + (double)a.Qz * a.Qz + (double)a.Qw * a.Qw );
            if ( aLength > 1e-9 )
            {
                qx = a.Qx / aLength;
                qy = a.Qy / aLength;
                qz = a.Qz / aLength;
                qw = a.Qw / aLength;
            }
            else
            {
                qx = 0;
                qy = 0;
                qz = 0;
                qw = 1;
            }
        }
        return new Pose(
            (float)(a.X + ((double)b.X - a.X) * u), (float)(a.Y + ((double)b.Y - a.Y) * u), (float)(a.Z + ((double)b.Z - a.Z) * u),
            (float)qx, (float)qy, (float)qz, (float)qw,
            (float)(a.Sx + ((double)b.Sx - a.Sx) * u), (float)(a.Sy + ((double)b.Sy - a.Sy) * u), (float)(a.Sz + ((double)b.Sz - a.Sz) * u) );
    }

    private static bool IsFinite( Pose p )
    {
        return float.IsFinite( p.X ) && float.IsFinite( p.Y ) && float.IsFinite( p.Z )
            && float.IsFinite( p.Qx ) && float.IsFinite( p.Qy ) && float.IsFinite( p.Qz ) && float.IsFinite( p.Qw )
            && float.IsFinite( p.Sx ) && float.IsFinite( p.Sy ) && float.IsFinite( p.Sz );
    }
}
