using System;

namespace TeamCreate;

/// <summary>
/// How often an editor talks to the host. The host runs the relay itself, so there is no shared request quota to protect and the cadence is chosen for
/// smooth remote motion: while the camera or selection is changing, presence goes out every 33 ms (about 30 a second), enough for the receiver to blend
/// between samples. The 33 ms floor applies ONLY while something changes; an unchanged presence is still sent only as a refresh every two seconds, and a
/// heartbeat covers the rest, so an idle session stays nearly silent. The host drops a member after 15 seconds of silence (SessionServer), so every
/// interval here stays far inside that. The billing helpers below remain for a relay that IS metered (a Cloudflare Worker used as a shared relay): they
/// describe that deployment and no longer decide the default.
/// </summary>
public static class SendCadence
{
    /// <summary>The host's own silence limit is 15 seconds; an editor never stays quiet for more than this.</summary>
    public static readonly TimeSpan MaxSilence = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The fastest presence is sent while the camera or selection is changing: about 30 a second, so that remote markers can be drawn from interpolated
    /// samples. Was 100 ms while a shared relay was billed per message; a host-run relay has no such quota. Four people all moving cost the host roughly
    /// 70 KB/s of upload (see the PresenceSync tests), which a home connection carries easily.
    /// </summary>
    public static readonly TimeSpan PresenceMinimum = TimeSpan.FromMilliseconds(33);

    /// <summary>Unchanged presence is repeated at this interval, so a collaborator who arrives late learns where everyone is.</summary>
    public static readonly TimeSpan PresenceRefresh = TimeSpan.FromSeconds(2);

    /// <summary>The liveness heartbeat.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(2);

    public static bool ShouldSendPresence(bool changed, TimeSpan sinceLastSend, bool isEditing)
    {
        if (sinceLastSend >= PresenceRefresh)
        {
            return true;
        }
        return changed && sinceLastSend >= PresenceMinimum;
    }

    public static bool ShouldSendPing(TimeSpan sinceLastSend)
    {
        return sinceLastSend >= PingInterval;
    }

    /// <summary>Messages per second an idle session sends the relay: every member's refresh and heartbeat.</summary>
    public static double MessagesPerSecondIdle(int peers)
    {
        return peers * (1.0 / PresenceRefresh.TotalSeconds + 1.0 / PingInterval.TotalSeconds);
    }

    /// <summary>The ceiling while every member is moving: presence at its minimum interval plus the heartbeat. Counts messages SENT to the host, not its fan-out.</summary>
    public static double MessagesPerSecondMoving(int peers)
    {
        return peers * (1.0 / PresenceMinimum.TotalSeconds + 1.0 / PingInterval.TotalSeconds);
    }

    /// <summary>
    /// Cloudflare bills incoming WebSocket messages at 20 to 1 against its request allowance. Applies only to a metered relay; at the moving rate
    /// above, a shared free allowance would not last, which is why a shared relay is not the default.
    /// </summary>
    public static double BilledRequestsPerHour(double messagesPerSecond)
    {
        return messagesPerSecond * 3600.0 / 20.0;
    }

    public const double FreeRequestsPerDay = 100000.0;

    public static double FreeHoursPerDay(double messagesPerSecond)
    {
        return FreeRequestsPerDay / BilledRequestsPerHour(messagesPerSecond);
    }
}
