using System;

namespace TeamCreate;

/// <summary>
/// When the host forwards a play frame to a spectator, as a rule with no I/O so it is tested directly. A spectator whose connection cannot keep up
/// must not make the host queue without bound (the wire's queue holds 256 packets, and one that overflows would have failed the member's whole
/// session), and must not receive deltas that no longer extend what it holds. So: over the limit it is "behind" and gets nothing; once behind it
/// receives nothing until a keyframe arrives while its queue has drained, and then deltas resume. The keyframe restores the state; skipping to it is
/// cheaper and safer than replaying what was missed.
/// </summary>
public static class PlayRelayPolicy
{
    /// <summary>The welcome feature a host advertises when it relays play, so a guest never sends play messages to a host that would answer with a fatal error.</summary>
    public const string Feature = "shared-play";

    public const long MaxPendingBytes = 1024 * 1024;

    public const int MaxPendingMessages = 128;

    /// <summary>A behind spectator is sent a keyframe only once its queue is down to this much, so the keyframe is not itself queued behind a backlog.</summary>
    public const long ResumeBytes = 256 * 1024;

    public const int ResumeMessages = 32;

    /// <summary>The player is asked for a keyframe at most this often, however many spectators ask.</summary>
    public const double KeyframeAskIntervalMs = 1000;

    public static bool ShouldSend( bool behind, bool isKeyframe, long pendingBytes, int pendingMessages, out bool nowBehind )
    {
        if ( pendingBytes > MaxPendingBytes || pendingMessages > MaxPendingMessages )
        {
            nowBehind = true;
            return false;
        }
        if ( behind )
        {
            if ( isKeyframe && pendingBytes <= ResumeBytes && pendingMessages <= ResumeMessages )
            {
                nowBehind = false;
                return true;
            }
            nowBehind = true;
            return false;
        }
        nowBehind = false;
        return true;
    }
}
