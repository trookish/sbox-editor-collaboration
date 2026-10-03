using System;
using System.Text;

namespace TeamCreate;

public enum PlayStartVerdict
{
    /// <summary>The request became the run; the host broadcasts it.</summary>
    Started,

    /// <summary>Someone else is playing. The requester keeps its own, local run and is told so.</summary>
    Refused,

    /// <summary>The request itself was malformed (a scene outside the project, no peer). It is dropped without a run.</summary>
    Invalid
}

public sealed record PlayStartResult( PlayStartVerdict Verdict, string RunId, string Notice, string Player );

/// <summary>
/// Who is playing, kept by the host and nowhere else. At most ONE run exists at a time; the host reads requests in the order they arrive, so two
/// people pressing Play together are ordered by that read and the second is refused, with no tie to break. The roster is pure state: the
/// session server turns its verdicts into packets, and every rule here is tested without a socket.
/// </summary>
/// <remarks>
/// A refused person's own Play still works in the engine, as a purely local run; only the collaboration part is refused, and the notice says so
/// because the person would otherwise believe the others can see them. A run belongs to the connection that started it: when that peer leaves
/// or its connection drops the run ends for everyone, because nobody is left to sample it. A reconnecting player is a new request with a new run
/// id, so frames of the old run can never be mistaken for the new one.
/// </remarks>
public sealed class PlayRoster
{
    /// <summary>The same bound a scene name gets everywhere else on the wire (a project path is short).</summary>
    public const int MaxSceneLength = 260;

    public const int MaxNameLength = 64;

    public bool IsPlaying => Player != null;

    public string Player { get; private set; }

    public string PlayerName { get; private set; }

    public string RunId { get; private set; }

    public string Scene { get; private set; }

    /// <summary>The formal sentence a refused person reads. <paramref name="name"/> is cleaned first: it came from a peer and is shown to people.</summary>
    public static string AlreadyPlayingNotice( string name ) =>
        CleanName( name ) + " is already playing. Wait until they stop, or ask them to stop. Your run is local and is not shown to the others.";

    /// <summary>Control characters removed, bounded, and a blank name replaced by a neutral one, so a name can never break a sentence or a line.</summary>
    public static string CleanName( string name )
    {
        var builder = new StringBuilder();
        foreach ( char c in name ?? string.Empty )
        {
            if ( !char.IsControl( c ) )
            {
                builder.Append( c );
            }
            if ( builder.Length >= MaxNameLength )
            {
                break;
            }
        }
        string cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? "A collaborator" : cleaned;
    }

    /// <summary>The scene must be a project scene, by the rule a subscribe is held to: under scenes/, no parent hops, bounded.</summary>
    public static bool IsPlayableScene( string scene ) =>
        !string.IsNullOrWhiteSpace( scene ) && scene.Length <= MaxSceneLength && scene.StartsWith( "scenes/", StringComparison.Ordinal ) && !scene.Contains( ".." ) && scene.IndexOf( '\\' ) < 0;

    public PlayStartResult TryStart( string peer, string name, string scene, Func<string> newRunId )
    {
        if ( string.IsNullOrEmpty( peer ) || !IsPlayableScene( scene ) )
        {
            return new PlayStartResult( PlayStartVerdict.Invalid, null, null, Player );
        }
        if ( Player != null && !string.Equals( Player, peer, StringComparison.Ordinal ) )
        {
            return new PlayStartResult( PlayStartVerdict.Refused, null, AlreadyPlayingNotice( PlayerName ), Player );
        }
        Player = peer;
        PlayerName = CleanName( name );
        Scene = scene;
        RunId = newRunId();
        return new PlayStartResult( PlayStartVerdict.Started, RunId, null, Player );
    }

    /// <summary>True when the run ended. Only the player may end it, and only by naming the run that is current.</summary>
    public bool Stop( string peer, string runId )
    {
        if ( !IsPlayer( peer, runId ) )
        {
            return false;
        }
        Clear();
        return true;
    }

    /// <summary>True when the leaver was the player, so the host must tell everyone the run is over.</summary>
    public bool PeerLeft( string peer )
    {
        if ( Player == null || !string.Equals( Player, peer, StringComparison.Ordinal ) )
        {
            return false;
        }
        Clear();
        return true;
    }

    public bool IsPlayer( string peer, string runId ) =>
        Player != null && peer != null && runId != null && string.Equals( Player, peer, StringComparison.Ordinal ) && string.Equals( RunId, runId, StringComparison.Ordinal );

    private void Clear()
    {
        Player = null;
        PlayerName = null;
        RunId = null;
        Scene = null;
    }
}
