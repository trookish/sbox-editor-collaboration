using System;

namespace TeamCreate;

/// <summary>
/// How many people one session holds. There is exactly one number, <see cref="MaxParticipants"/>, counted with the host: the host's editor
/// plus up to three guests. The host's server enforces it at admission (the refused guest is told why), and the relay, which cannot see
/// who is admitted, is limited to the same number so it never accepts a guest the host would turn away.
/// </summary>
public static class SessionLimits
{
    /// <summary>Participants in one session, the host included.</summary>
    public const int MaxParticipants = 4;

    /// <summary>
    /// Sockets the host's server will hold at once, admitted or not. This is a separate and larger bound on purpose: a socket that has not
    /// yet said hello is not a participant, and if it were counted against the participant limit a stranger could fill the session with
    /// silent connections. Past this, a connection is closed unanswered; below it, an extra guest still receives a readable refusal.
    /// </summary>
    public const int MaxConnections = 16;

    /// <summary>Null when one more guest may be admitted while <paramref name="otherAdmittedGuests"/> are already in; otherwise the message shown to the refused guest.</summary>
    public static string AdmitGuest( int otherAdmittedGuests )
    {
        // Host + the guests already in + this one.
        if ( 1 + Math.Max( 0, otherAdmittedGuests ) + 1 > MaxParticipants )
            return "This session already has " + MaxParticipants + " participants.";
        return null;
    }
}
