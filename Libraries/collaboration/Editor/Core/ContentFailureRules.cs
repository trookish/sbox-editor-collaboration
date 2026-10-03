using System;
using System.Collections.Generic;

namespace TeamCreate;

/// <summary>What the editor does with a scene packet that the tracked-scene routing did not admit.</summary>
public enum RouteFailureAction
{
    /// <summary>The packet names no valid scene: the host is not speaking this protocol, so the session stays fail-closed.</summary>
    Fail,

    /// <summary>The scene was closed (or never opened) here. Reopening it subscribes again and brings it up to date.</summary>
    IgnoreClosed,

    /// <summary>The scene is open but cannot take the packet now (for example it is playing): resynchronize it once it can.</summary>
    ResyncWhenRoutable
}

/// <summary>What the scan does when a scene's file on disk changed outside a collaboration save.</summary>
public enum DiskStampAction
{
    None,

    /// <summary>The disk version was backed up; the open, live scene stays authoritative and the new stamp is adopted.</summary>
    KeepLiveScene,

    /// <summary>The disk version could not be backed up: only this scene stops synchronizing, so nothing is overwritten unseen.</summary>
    PauseScene
}

/// <summary>
/// The content failures around the apply path, each answered by healing one scene instead of ending the session (see
/// docs/FAILURE-MODES.md for the full audit). Pure: the editor glue supplies the facts and carries out the answer.
/// </summary>
public static class ContentFailureRules
{
    public static RouteFailureAction ClassifyRouteFailure( bool sceneKeyValid, bool tracked, bool targetOpen )
    {
        if ( !sceneKeyValid )
        {
            return RouteFailureAction.Fail;
        }
        return tracked && targetOpen ? RouteFailureAction.ResyncWhenRoutable : RouteFailureAction.IgnoreClosed;
    }

    /// <summary>
    /// Whether a completed connection may send its hello. Playing, or having no scene open, only pauses source synchronization
    /// (<see cref="PlaySpectateRules"/>): the connection, roster and presence carry on and scene packets are held. Only a runtime
    /// that a hotload interrupted (<see cref="PlaySpectateDecision.FailStop"/>) or a teardown in progress still refuses.
    /// </summary>
    public static bool MayPromoteConnection( PlaySpectateDecision decision, bool canPromoteConnection )
    {
        return canPromoteConnection && decision != PlaySpectateDecision.FailStop;
    }

    /// <summary>
    /// The play-mode buffer is full. Every held packet is discarded (applying some of them and not others would leave the scene
    /// in a state nobody had), and the scenes they and <paramref name="overflowing"/> belong to are returned: each is
    /// resynchronized from the host when play ends.
    /// </summary>
    public static HashSet<string> DrainOverflow( HeldPacketBuffer buffer, Packet overflowing )
    {
        HashSet<string> scenes = new HashSet<string>( StringComparer.Ordinal );
        while ( buffer.TryTake( out Packet packet ) )
        {
            if ( !string.IsNullOrEmpty( packet?.Scene ) )
            {
                scenes.Add( packet.Scene );
            }
        }
        if ( !string.IsNullOrEmpty( overflowing?.Scene ) )
        {
            scenes.Add( overflowing.Scene );
        }
        return scenes;
    }

    public static DiskStampAction OnDiskStampMismatch( bool flagged, bool backupWritten )
    {
        if ( !flagged )
        {
            return DiskStampAction.None;
        }
        return backupWritten ? DiskStampAction.KeepLiveScene : DiskStampAction.PauseScene;
    }
}
