using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;
using Sandbox.Internal;

namespace TeamCreate;

/// <summary>The floating window that holds a <see cref="SpectatorView"/> and says who is being watched. Closing it only closes the view; the run goes on.</summary>
public sealed class SpectatorWindow : Widget
{
    private readonly SpectatorView view;

    private readonly Label caption;

    public event Action Closed;

    public SpectatorWindow( PlayMirror mirror ) : base( null )
    {
        WindowFlags = WindowFlags.Dialog;
        WindowTitle = "Spectating";
        DeleteOnClose = true;
        MinimumSize = new Vector2( 640, 420 );
        Layout = Layout.Column();
        Layout.Margin = 4;
        Layout.Spacing = 4;
        caption = Layout.Add( new Label( "", this ) { WordWrap = true } );
        view = Layout.Add( new SpectatorView( mirror, this ), 1 );
    }

    public SpectatorView View => view;

    public void SetCaption( string title, string text )
    {
        WindowTitle = title;
        caption.Text = text;
    }

    protected override void OnClosed()
    {
        base.OnClosed();
        view.Release();
        Closed?.Invoke();
    }
}

/// <summary>
/// The editor side of shared play for one session: the packets, the player's sampling and the spectator's window, around the pure pieces in Core. Created only
/// when this computer opted in (<see cref="SharedPlaySetting"/>) and the host advertised <see cref="PlayRelayPolicy.Feature"/>; absent otherwise, so a session
/// without it behaves as it always did. It speaks only collaboration packets on the connection the session already has. It never starts, joins or changes
/// the game's own networking, and it never starts or stops the game: it watches whether a run exists.
/// </summary>
public sealed class SharedPlayClient : IDisposable
{
    public const double SampleIntervalMs = 50;

    public enum PlayRole
    {
        None,
        Requested,
        Player,
        Spectator
    }

    private readonly Action<Packet> send;

    private readonly string me;

    private readonly Action<string> notice;

    private readonly PlayMirror mirror = new PlayMirror();

    private readonly PlayCapture capture = new PlayCapture();

    private PlaySampler sampler = new PlaySampler();

    private SpectatorWindow window;

    private double lastSampleMs;

    private double lastKeyAskMs = double.NegativeInfinity;

    private bool refusedForThisRun;

    private bool disposed;

    public PlayRole Role { get; private set; }

    public string RunId { get; private set; }

    public string PlayerName { get; private set; }

    public string PlayerPeer { get; private set; }

    /// <summary>True while another member's run is the one on screen. The session reads it to pause source synchronization.</summary>
    public bool SpectatingRun => Role == PlayRole.Spectator;

    public SharedPlayClient( Action<Packet> send, string myPeer, Action<string> notice )
    {
        this.send = send ?? throw new ArgumentNullException( nameof( send ) );
        me = myPeer ?? throw new ArgumentNullException( nameof( myPeer ) );
        this.notice = notice ?? ( _ => { } );
        send( new Packet { Kind = "play-optin" } );
    }

    /// <summary>The sentence the dock shows while this editor is held back by somebody else's run. Null otherwise.</summary>
    public string EditsPausedNotice => SpectatingRun ? "Edits are paused while " + ( PlayerName ?? "someone" ) + " is playing." : null;

    /// <summary>Handles a shared play packet; false when the packet is not one, so the session processes it as usual.</summary>
    public bool Handle( Packet p )
    {
        if ( disposed || p?.Kind is not ( "play-start" or "play-stop" or "play-frame" or "play-key" or "play-refused" ) )
        {
            return false;
        }
        switch ( p.Kind )
        {
        case "play-start":
            Begin( p );
            break;
        case "play-stop":
            if ( p.RuntimeRunId == RunId ) End( p.Peer == me ? null : ( PlayerName ?? "The player" ) + " stopped playing." );
            break;
        case "play-frame":
            if ( Role == PlayRole.Spectator && p.RuntimeRunId == RunId ) Apply( p );
            break;
        case "play-key":
            // The host asks the player for a full keyframe (a spectator joined, or one fell behind).
            if ( Role == PlayRole.Player && p.RuntimeRunId == RunId ) sampler.RequestKeyframe();
            break;
        case "play-refused":
            // A refusal answers a request of this editor's own; a spectator that asked (it pressed Play while watching) keeps watching.
            if ( Role == PlayRole.Requested ) Role = PlayRole.None;
            refusedForThisRun = true;
            if ( !string.IsNullOrEmpty( p.Notice ) ) notice( p.Notice );
            break;
        }
        return true;
    }

    private void Begin( Packet p )
    {
        ReleaseWindow();
        RunId = p.RuntimeRunId;
        PlayerPeer = p.Peer;
        PlayerName = string.IsNullOrWhiteSpace( p.Name ) ? "A collaborator" : p.Name;
        mirror.Reset();
        if ( p.Peer == me )
        {
            Role = PlayRole.Player;
            sampler = new PlaySampler();
            lastSampleMs = double.NegativeInfinity;
            return;
        }
        Role = PlayRole.Spectator;
        window = new SpectatorWindow( mirror );
        window.SetCaption( "Spectating " + PlayerName, PlayerName + " is playing. Right-drag to look, W A S D to move, Q and E down and up, Shift faster, Ctrl slower, mouse wheel changes speed. You see a copy of the scene; no game code runs here." );
        window.Closed += OnWindowClosed;
        window.Show();
        notice( PlayerName + " started playing. Spectator view opened." );
    }

    private void OnWindowClosed()
    {
        window = null;
    }

    private void Apply( Packet p )
    {
        if ( !PlayFrame.TryParse( p.Play, out PlayFrame frame, out string error ) )
        {
            // A frame the receiver refuses whole is treated like a lost one: ask for a keyframe, keep what is shown.
            AskForKeyframe();
            return;
        }
        if ( mirror.Apply( frame ) == PlayApplyResult.NeedsKeyframe )
        {
            AskForKeyframe();
        }
    }

    private void AskForKeyframe()
    {
        double now = Environment.TickCount64;
        if ( now - lastKeyAskMs < PlayRelayPolicy.KeyframeAskIntervalMs || RunId == null )
        {
            return;
        }
        lastKeyAskMs = now;
        send( new Packet { Kind = "play-key", RuntimeRunId = RunId } );
    }

    private void End( string message )
    {
        ReleaseWindow();
        mirror.Reset();
        Role = PlayRole.None;
        RunId = null;
        PlayerName = null;
        PlayerPeer = null;
        if ( message != null ) notice( message );
    }

    private void ReleaseWindow()
    {
        if ( window != null )
        {
            window.Closed -= OnWindowClosed;
            var w = window;
            window = null;
            if ( w.IsValid() )
            {
                w.View.Release();
                w.Close();
            }
        }
    }

    /// <summary>
    /// Called every editor frame while connected. Watches the editor's own game session: when one appears this person asks to be the player, when it
    /// ends the run ends; while they are the player it samples the runtime scene and sends frames.
    /// </summary>
    public void Tick()
    {
        if ( disposed )
        {
            return;
        }
        GameEditorSession game = SceneEditorSession.All.OfType<GameEditorSession>().FirstOrDefault();
        if ( game == null )
        {
            refusedForThisRun = false;
            if ( Role == PlayRole.Player || Role == PlayRole.Requested )
            {
                if ( RunId != null ) send( new Packet { Kind = "play-stop", RuntimeRunId = RunId } );
                End( null );
            }
            return;
        }
        if ( ( Role == PlayRole.None || Role == PlayRole.Spectator ) && !refusedForThisRun )
        {
            string scene = SceneAdapter.PathOf( game.Parent );
            if ( PlayRoster.IsPlayableScene( scene ) )
            {
                if ( Role == PlayRole.None ) Role = PlayRole.Requested;
                send( new Packet { Kind = "play-start", Scene = scene } );
            }
            else
            {
                refusedForThisRun = true;
                notice( "This scene is not saved under Assets/scenes, so it cannot be shared. Your run is local and is not shown to the others." );
            }
            return;
        }
        if ( Role != PlayRole.Player || RunId == null )
        {
            return;
        }
        double now = Environment.TickCount64;
        if ( now - lastSampleMs < SampleIntervalMs )
        {
            return;
        }
        lastSampleMs = now;
        try
        {
            foreach ( PlayFrame frame in sampler.Sample( capture.Capture( game ), now ) )
            {
                var packet = new Packet { Kind = "play-frame", RuntimeRunId = RunId, RuntimeSequence = frame.Seq, Play = frame.ToJson() };
                send( packet );
            }
        }
        catch ( Exception error )
        {
            // A sample that fails (a scene mid-teardown) skips this tick and asks for a keyframe so the next one is whole; it never ends the session.
            sampler.RequestKeyframe();
            GlobalGameNamespace.Log.Warning( "Shared play sample skipped: " + CollaborationDiagnostics.Bounded( error.Message ) );
        }
    }

    public void Dispose()
    {
        if ( disposed )
        {
            return;
        }
        disposed = true;
        ReleaseWindow();
        mirror.Reset();
        Role = PlayRole.None;
    }
}
