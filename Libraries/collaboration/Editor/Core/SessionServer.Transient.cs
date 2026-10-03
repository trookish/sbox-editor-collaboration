using System;
using System.Linq;

namespace TeamCreate;

/// <summary>
/// The host's part of the live-drag channel (<see cref="TransientLimits"/>). A transient is relayed to every OTHER member and is nothing else: it is
/// never journaled, never changes a revision or a scene, is never kept for a member who joins later, and is checked for nothing but its shape, so
/// that a hostile or runaway member costs the others no more than the rate limit allows.
/// </summary>
public sealed partial class SessionServer
{
    private readonly TransientRateLimit transientLimit = new TransientRateLimit();

    private readonly System.Diagnostics.Stopwatch transientClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Transients relayed to at least one member, for the host's diagnostics and tests.</summary>
    public int TransientsRelayed { get; private set; }

    /// <summary>Transients dropped (rate limit, invalid content or an unknown scene), for the host's diagnostics and tests.</summary>
    public int TransientsDropped { get; private set; }

    private bool HandleTransientPacket( Member member, Packet packet )
    {
        if ( packet.Kind != TransientLimits.Kind )
        {
            return false;
        }
        // Only an admitted member gets here (the pump handles the handshake before any other kind), so the sender needs no further proof. The rate
        // limit comes first because it is the cheapest check and the one that bounds what a flood can cost.
        if ( !transientLimit.Allow( member.Peer, transientClock.Elapsed.TotalMilliseconds )
            || TransientCodec.Validate( packet, out _ ) != null
            || !authority.Scenes.ContainsKey( packet.Scene ) )
        {
            TransientsDropped++;
            return true;
        }
        // packet.Peer is already the admitted peer id (the pump overwrites it), so a member cannot pose as another.
        foreach ( Member target in members.Where( m => m != member && m.Peer != null ).ToList() )
        {
            SendQuietly( target, packet );
        }
        TransientsRelayed++;
        return true;
    }
}
