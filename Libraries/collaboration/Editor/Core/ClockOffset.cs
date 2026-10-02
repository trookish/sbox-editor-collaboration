using System;

namespace TeamCreate;

/// <summary>
/// Estimates the difference between a sender's clock and ours from the packets it stamps with its own monotonic milliseconds.
/// </summary>
/// <remarks>
/// Neither clock is known to the other, so the only measurable quantity is <c>receivedLocalMs - senderMs</c>: the true clock offset plus
/// however long that packet spent on the network. The packet that spent the least time is the one whose value is closest to the true offset,
/// so the estimate is the minimum over the most recent observations. Taking the minimum rather than the mean means one slow packet never
/// shifts the estimate; the cost is a constant bias of the smallest one-way delay, which moves every frame by the same amount and is invisible.
/// A window (not "the minimum ever") lets the estimate follow the two clocks drifting apart. One instance per sender: a sender that restarts has a
/// new clock, so the caller replaces its instance.
/// </remarks>
public sealed class ClockOffset
{
    /// <summary>How many of the most recent observations take part in the minimum: about two seconds at 30 packets a second.</summary>
    public const int Window = 64;

    private readonly double[] samples = new double[Window];
    private int count;
    private int next;
    private double offsetMs;

    /// <summary>True once at least one finite observation has been made; before that <see cref="OffsetMs"/> is 0.</summary>
    public bool HasEstimate => count > 0;

    /// <summary>The estimated <c>local - sender</c> clock difference in milliseconds (0 until <see cref="HasEstimate"/>).</summary>
    public double OffsetMs => offsetMs;

    /// <summary>
    /// Records one packet: the sender's stamp and the local time it arrived. A pair that does not give a finite difference is ignored, which
    /// covers every NaN or infinite input (a NaN or an infinity in either value makes the difference NaN or infinite) and two huge finite values that overflow.
    /// </summary>
    public void Observe( double senderMs, double receivedLocalMs )
    {
        double difference = receivedLocalMs - senderMs;
        if ( !double.IsFinite( difference ) )
        {
            return;
        }
        samples[next] = difference;
        next = (next + 1) % Window;
        if ( count < Window )
        {
            count++;
        }
        double min = double.MaxValue;
        for ( int i = 0; i < count; i++ )
        {
            if ( samples[i] < min )
            {
                min = samples[i];
            }
        }
        offsetMs = min;
    }

    /// <summary>The local time corresponding to a sender timestamp.</summary>
    public double ToLocalMs( double senderMs ) => senderMs + OffsetMs;
}
