using System;

namespace TeamCreate;

/// <summary>
/// How often an editor talks to the host when nothing has changed. Every message an editor sends is also a message the relay receives, and a free shared
/// relay is billed per message, so sending ten identical presence packets a second is the difference between a session that costs almost nothing and one
/// that uses a day's allowance in hours. The host drops a member after 15 seconds of silence (SessionServer), so every interval here stays far inside that.
/// </summary>
public static class SendCadence
{
    /// <summary>The host's own silence limit is 15 seconds; an editor never stays quiet for more than this.</summary>
    public static readonly TimeSpan MaxSilence = TimeSpan.FromSeconds(4);

    /// <summary>The fastest presence is sent while the camera or selection is changing: ten a second, as before.</summary>
    public static readonly TimeSpan PresenceMinimum = TimeSpan.FromMilliseconds(100);

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

    /// <summary>The ceiling while every member is moving: presence at its minimum interval plus the heartbeat.</summary>
    public static double MessagesPerSecondMoving(int peers)
    {
        return peers * (1.0 / PresenceMinimum.TotalSeconds + 1.0 / PingInterval.TotalSeconds);
    }

    /// <summary>Cloudflare bills incoming WebSocket messages at 20 to 1 against its request allowance.</summary>
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
