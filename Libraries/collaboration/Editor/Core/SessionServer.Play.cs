using System;
using System.Collections.Generic;
using System.Linq;

namespace TeamCreate;

/// <summary>
/// Shared play on the host (one run, many spectators). The host is a relay and a referee, nothing more: it keeps the <see cref="PlayRoster"/>, tells
/// the opted-in members who plays, and forwards the player's frames to the spectators. It never reads a frame's content (every spectator checks it
/// again) and never touches the game's networking: these are collaboration packets on the collaboration connection.
/// </summary>
public sealed partial class SessionServer
{
    private readonly PlayRoster playRoster = new PlayRoster();

    private readonly HashSet<Member> playOptIn = new HashSet<Member>();

    private readonly Dictionary<Member, bool> playBehind = new Dictionary<Member, bool>();

    private readonly System.Diagnostics.Stopwatch playClock = System.Diagnostics.Stopwatch.StartNew();

    private double lastKeyframeAskMs = double.NegativeInfinity;

    /// <summary>
    /// The host's own setting. Off (the default) the host does not advertise shared play, ignores its messages without an error, and behaves exactly as
    /// before. On, it relays; a guest still takes part only after it sends <c>play-optin</c>.
    /// </summary>
    public bool SharedPlayEnabled { get; set; }

    /// <summary>The peer id of the member playing, or null. For the host's own dock.</summary>
    public string PlayingPeer => playRoster.Player;

    /// <summary>The queued backlog of a peer's connection, or null to read the wire's own counters. Tests replace it to simulate a slow spectator.</summary>
    public Func<string, (long Bytes, int Messages)> PlayBacklogProbe { get; set; }

    private bool HandlePlayPacket( Member member, Packet packet )
    {
        if ( packet.Kind is not ( "play-optin" or "play-start" or "play-stop" or "play-frame" or "play-key" ) )
        {
            return false;
        }
        if ( !SharedPlayEnabled )
        {
            return true;
        }
        switch ( packet.Kind )
        {
        case "play-optin":
            playOptIn.Add( member );
            if ( playRoster.IsPlaying )
            {
                SendQuietly( member, StartPacket() );
                AskForKeyframe();
            }
            break;
        case "play-start":
            if ( playOptIn.Contains( member ) )
            {
                StartRun( member, packet );
            }
            break;
        case "play-stop":
        {
            string run = playRoster.RunId;
            if ( playRoster.Stop( member.Peer, packet.RuntimeRunId ) )
            {
                BroadcastPlay( StopPacket( member.Peer, run ), except: null );
            }
            break;
        }
        case "play-frame":
            if ( playOptIn.Contains( member ) && packet.Play != null && playRoster.IsPlayer( member.Peer, packet.RuntimeRunId ) )
            {
                RelayFrame( member, packet );
            }
            break;
        case "play-key":
            if ( playOptIn.Contains( member ) && playRoster.IsPlaying )
            {
                AskForKeyframe();
            }
            break;
        }
        return true;
    }

    private void StartRun( Member member, Packet request )
    {
        PlayStartResult result = playRoster.TryStart( member.Peer, member.Name, request.Scene, () => Guid.NewGuid().ToString( "N" ) );
        switch ( result.Verdict )
        {
        case PlayStartVerdict.Started:
            foreach ( Member behind in playBehind.Keys.ToList() ) playBehind[behind] = false;
            BroadcastPlay( StartPacket(), except: null );
            break;
        case PlayStartVerdict.Refused:
            SendQuietly( member, new Packet { Kind = "play-refused", Notice = result.Notice, Peer = result.Player } );
            break;
        default:
            SendQuietly( member, new Packet { Kind = "play-refused", Notice = "The play request was not valid. Save the scene under Assets/scenes and try again. Your run is local and is not shown to the others." } );
            break;
        }
    }

    private Packet StartPacket() => new Packet
    {
        Kind = "play-start", Peer = playRoster.Player, Name = playRoster.PlayerName, Scene = playRoster.Scene, RuntimeRunId = playRoster.RunId,
    };

    private static Packet StopPacket( string peer, string run ) => new Packet { Kind = "play-stop", Peer = peer, RuntimeRunId = run };

    private void BroadcastPlay( Packet packet, Member except )
    {
        foreach ( Member target in playOptIn.Where( m => m != except && members.Contains( m ) ).ToList() )
        {
            SendQuietly( target, packet );
        }
    }

    /// <summary>A play message must never cost a member its session or the host a broadcast: a failed send is left for the pump's own failure handling.</summary>
    private static void SendQuietly( Member target, Packet packet )
    {
        try
        {
            target.Wire.Send( packet );
        }
        catch ( Exception )
        {
        }
    }

    private void RelayFrame( Member player, Packet frame )
    {
        bool isKey = frame.Play["k"] is System.Text.Json.Nodes.JsonValue k && k.TryGetValue( out bool key ) && key;
        foreach ( Member spectator in playOptIn.Where( m => m != player && members.Contains( m ) ).ToList() )
        {
            (long bytes, int messages) = PlayBacklogProbe != null ? PlayBacklogProbe( spectator.Peer ) : (spectator.Wire.PendingBytes, spectator.Wire.PendingMessages);
            bool wasBehind = playBehind.TryGetValue( spectator, out bool b ) && b;
            bool send = PlayRelayPolicy.ShouldSend( wasBehind, isKey, bytes, messages, out bool nowBehind );
            playBehind[spectator] = nowBehind;
            if ( nowBehind && !wasBehind )
            {
                AskForKeyframe();
            }
            if ( send )
            {
                SendQuietly( spectator, frame );
            }
        }
    }

    // Spectators that ask at the same moment (a late joiner, a gap on two screens) cost the player one keyframe, not one each.
    private void AskForKeyframe()
    {
        double now = playClock.Elapsed.TotalMilliseconds;
        if ( !playRoster.IsPlaying || now - lastKeyframeAskMs < PlayRelayPolicy.KeyframeAskIntervalMs )
        {
            return;
        }
        Member player = members.FirstOrDefault( m => m.Peer == playRoster.Player );
        if ( player == null )
        {
            return;
        }
        lastKeyframeAskMs = now;
        SendQuietly( player, new Packet { Kind = "play-key", RuntimeRunId = playRoster.RunId } );
    }

    /// <summary>A member left or dropped: it no longer relays, and when it was the player the run ends for everyone.</summary>
    private void ForgetPlayMember( Member member )
    {
        playOptIn.Remove( member );
        playBehind.Remove( member );
        if ( member.Peer == null )
        {
            return;
        }
        string run = playRoster.RunId;
        if ( playRoster.PeerLeft( member.Peer ) )
        {
            BroadcastPlay( StopPacket( member.Peer, run ), except: null );
        }
    }
}
