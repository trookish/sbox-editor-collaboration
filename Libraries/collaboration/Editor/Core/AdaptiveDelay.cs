using System;

namespace TeamCreate;

/// <summary>
/// How far behind live to render remote motion. Rendering behind live gives the interpolation two samples to blend between; the delay has to be
/// at least as long as the network's irregularity or the newest sample is not there yet and the picture stalls. Longer than that only adds lag.
/// </summary>
/// <remarks>
/// The measure of irregularity is the gap between consecutive packet arrivals over the last <see cref="Window"/> packets. The target is
/// <c>p95(gap) + 0.5 * mean(gap)</c>: the 95th-percentile gap is how long the picture can wait for the next packet on all but one in twenty
/// arrivals, and half a mean gap is added as margin so that the sample after the render time has normally arrived. The target is clamped to
/// <see cref="FloorMs"/>..<see cref="CeilingMs"/>. With a 32-gap window the nearest-rank p95 is the 31st smallest value, so a single late
/// packet barely moves the target (only the mean); jitter that repeats does.
///
/// The delay follows that target asymmetrically. When the network gets worse the delay rises at once, because a late packet must not freeze
/// the picture. When the network improves, or a single packet was merely lost, the delay falls by at most <see cref="MaxFallMsPerSecond"/> of
/// elapsed time: the render clock then runs at 0.5% slower than real time while it settles, which no one can see, instead of lurching
/// forward, which everyone can.
///
/// Before any gap is observed the delay is <see cref="StartMs"/>. The floor (50 ms) would be too short for a first, possibly jittery, packet; 80 ms is
/// inside the 60 to 80 ms typical range and costs a few seconds of mild lag at worst: the delay then walks down to the real target at the fall rate.
/// </remarks>
public sealed class AdaptiveDelay
{
    public const double FloorMs = 50;
    public const double CeilingMs = 150;
    public const double MaxFallMsPerSecond = 5;

    /// <summary>The delay used until the first gap has been observed, and the value the delay falls from afterwards.</summary>
    public const double StartMs = 80;

    /// <summary>How many of the most recent arrival gaps take part: about one second at 30 packets a second.</summary>
    public const int Window = 32;

    private readonly double[] gaps = new double[Window];
    private readonly double[] sorted = new double[Window];
    private int count;
    private int next;
    private double targetMs;
    private double currentMs = StartMs;
    private double lastMs;
    private bool hasLast;

    /// <summary>
    /// Records the time between two consecutive packet arrivals, read at local time <paramref name="nowMs"/>. A negative or non-finite gap
    /// is ignored (a clock that stepped backwards, or a bad reading, says nothing about the network).
    /// </summary>
    public void Observe( double arrivalGapMs, double nowMs )
    {
        if ( !double.IsFinite( arrivalGapMs ) || arrivalGapMs < 0 )
        {
            return;
        }
        // Settle the delay up to now against the old target first, so that time already elapsed is not charged against the new one.
        Follow( nowMs );
        gaps[next] = arrivalGapMs;
        next = (next + 1) % Window;
        if ( count < Window )
        {
            count++;
        }
        targetMs = ComputeTarget();
        Follow( nowMs );
    }

    /// <summary>The delay to render behind live at local time <paramref name="nowMs"/>. Reading it more or less often does not change the result.</summary>
    public double DelayMs( double nowMs )
    {
        Follow( nowMs );
        return currentMs;
    }

    /// <summary>
    /// Moves the current delay toward the target for the time elapsed since the last reading: up at once, down at the limited rate. A reading
    /// earlier than one already seen adds no time, so calls that arrive slightly out of order can never make the delay fall faster than its limit.
    /// </summary>
    private void Follow( double nowMs )
    {
        double elapsedMs = 0;
        if ( double.IsFinite( nowMs ) )
        {
            if ( hasLast && nowMs > lastMs )
            {
                elapsedMs = nowMs - lastMs;
            }
            if ( !hasLast || nowMs > lastMs )
            {
                lastMs = nowMs;
                hasLast = true;
            }
        }
        if ( count == 0 )
        {
            return;
        }
        if ( targetMs >= currentMs )
        {
            currentMs = targetMs;
        }
        else
        {
            currentMs = Math.Max( targetMs, currentMs - MaxFallMsPerSecond * elapsedMs / 1000.0 );
        }
    }

    private double ComputeTarget()
    {
        Array.Copy( gaps, sorted, count );
        Array.Sort( sorted, 0, count );
        // Nearest rank: the smallest value with at least 95% of the window at or below it, i.e. the ceil(0.95 * n)-th smallest. Integer
        // arithmetic, so that 20 values give exactly the 19th rather than whatever 0.95 * 20 rounds to.
        int rank = (95 * count + 99) / 100;
        double p95 = sorted[rank - 1];
        double sum = 0;
        for ( int i = 0; i < count; i++ )
        {
            sum += gaps[i];
        }
        double mean = sum / count;
        // A sum that overflows is +infinity and clamps to the ceiling, which is the right answer for such a gap.
        return Math.Clamp( p95 + 0.5 * mean, FloorMs, CeilingMs );
    }
}
