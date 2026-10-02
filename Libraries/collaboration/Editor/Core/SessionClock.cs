using System;
using System.Diagnostics;

namespace TeamCreate;

/// <summary>
/// The sender's clock for presence stamps: milliseconds since this object was created (a session starts one), read from the monotonic
/// <see cref="Stopwatch"/> counter. Wall-clock time is deliberately not used: it can step (a time sync, a daylight change) and two machines'
/// wall clocks disagree by seconds, whereas a receiver only needs the DIFFERENCES between one sender's stamps and a rough constant offset.
/// </summary>
public sealed class SessionClock
{
    /// <summary>The smallest value reported. A stamp of exactly 0 is how an old peer's missing field reads, so a real stamp is never 0.</summary>
    public const double MinimumMs = 0.001;

    private readonly long startTicks = Stopwatch.GetTimestamp();

    /// <summary>Milliseconds since the clock started: positive, finite, never decreasing.</summary>
    public double NowMs => Math.Max( MinimumMs, (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency );
}
