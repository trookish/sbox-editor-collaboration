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

    /// <summary>
    /// What the session does with an exception thrown by something the person did (save, undo, redo, the play toggle, a dock button).
    /// Almost all of these are about one action: a full disk, a scene the engine would not save, an undo with nothing to undo. They are
    /// reported and the session continues, because ending a collaboration over them throws away a working connection. Only the
    /// integrity failures docs/FAILURE-MODES.md keeps fail-closed still end it: a frame that does not authenticate or a peer that cannot be
    /// authenticated (<see cref="System.Security.Cryptography.CryptographicException"/>, <see cref="System.Security.Authentication.AuthenticationException"/>),
    /// a relay that refused the route (<see cref="RelayRefusedException"/>), and an editor out of memory. A wrapper (an aggregate, a reflection
    /// invocation, or any exception with an inner one) is judged by what it wraps, to a bounded depth.
    /// </summary>
    public static ActionFailureAction ClassifyActionFailure( Exception error )
    {
        return IsIntegrityFailure( error, 0 ) ? ActionFailureAction.Fail : ActionFailureAction.Notice;
    }

    private const int MaxWrapperDepth = 8;

    private static bool IsIntegrityFailure( Exception error, int depth )
    {
        if ( error == null || depth > MaxWrapperDepth )
        {
            return false;
        }
        if ( error is System.Security.Cryptography.CryptographicException || error is System.Security.Authentication.AuthenticationException
            || error is RelayRefusedException || error is OutOfMemoryException )
        {
            return true;
        }
        if ( error is AggregateException aggregate )
        {
            foreach ( Exception inner in aggregate.InnerExceptions )
            {
                if ( IsIntegrityFailure( inner, depth + 1 ) )
                {
                    return true;
                }
            }
            return false;
        }
        return IsIntegrityFailure( error.InnerException, depth + 1 );
    }

    /// <summary>
    /// The notice for an action that failed without ending the session: <c>The action could not be completed: &lt;reason&gt;. The session continues.</c>
    /// The reason is the first line of the message, bounded (<see cref="ApplyRecovery.ShortReason"/>), so a stack trace or an engine dump never
    /// reaches the dock. An <see cref="ActionRefusal"/> is already a sentence written for the person and is shown as it is.
    /// </summary>
    public static string ActionFailureNotice( Exception error )
    {
        if ( error is ActionRefusal && !string.IsNullOrWhiteSpace( error.Message ) )
        {
            return error.Message;
        }
        string reason = string.IsNullOrWhiteSpace( error?.Message ) ? string.Empty : ApplyRecovery.ShortReason( error.Message ).TrimEnd( '.', ' ' );
        if ( reason.Length == 0 )
        {
            reason = "an unexpected error occurred";
        }
        return "The action could not be completed: " + reason + ". The session continues.";
    }
}

/// <summary>What <see cref="ContentFailureRules.ClassifyActionFailure"/> decided about an exception from a user action.</summary>
public enum ActionFailureAction
{
    /// <summary>Show a notice; the session continues.</summary>
    Notice,

    /// <summary>An integrity failure: the session ends, fail-closed.</summary>
    Fail
}
