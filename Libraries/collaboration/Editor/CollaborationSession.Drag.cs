using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace TeamCreate;

/// <summary>
/// Live drag, the editor's side (the channel is described in <c>Core/TransientDrag.cs</c>). Sending: the scan notices which objects this person is
/// changing, and every 33 ms the live transform of those objects is read straight from the engine and sent as a transient, so the packet rate does
/// not depend on the 50 ms scan. Receiving: poses from other editors are blended by <see cref="TransientReceiver"/> and written to the live objects
/// every frame with plain property writes, which the engine records no undo entry for (the committed state still arrives as an accepted packet and
/// replaces whatever was drawn). The scan reads such an object with its transform taken from Known (<see cref="TransientEcho"/>), so a drawn pose is
/// never sent back as an edit.
/// </summary>
public sealed partial class CollaborationSession
{
    private readonly Dictionary<string, TransientCadence> dragSenders = new Dictionary<string, TransientCadence>( StringComparer.Ordinal );

    private readonly TransientReceiver dragReceiver = new TransientReceiver();

    // Set from the host's welcome: an older host answers a kind it does not know with a fatal error, so nothing is sent to one.
    private bool hostTakesTransients;

    private readonly List<(string Scene, string Id, Pose Pose)> dragWrites = new List<(string Scene, string Id, Pose Pose)>();

    private void ClearDrag()
    {
        dragSenders.Clear();
        dragReceiver.Clear();
    }

    private bool IsLocalDragging( string scene, string id )
    {
        return dragSenders.TryGetValue( scene, out TransientCadence cadence ) && cadence.IsMoving( id, presenceClock.NowMs );
    }

    /// <summary>The scan found these local changes: the objects whose transform changed are the ones being dragged.</summary>
    private void NoteLocalDrag( string scene, List<Change> changes )
    {
        if ( !hostTakesTransients || changes == null )
        {
            return;
        }
        foreach ( Change change in changes )
        {
            if ( change.Before == null || change.After == null || !TransientEcho.TransformDiffers( change.Before, change.After ) )
            {
                continue;
            }
            if ( !dragSenders.TryGetValue( scene, out TransientCadence cadence ) )
            {
                cadence = new TransientCadence();
                dragSenders[scene] = cadence;
            }
            if ( TransientEcho.TryReadPose( change.Before, out Pose before ) )
            {
                // Baseline first: only movement from here on is sent. A second note of the same object leaves the baseline where it is.
                cadence.Update( change.Id, before, presenceClock.NowMs );
            }
        }
    }

    /// <summary>The scan's capture, with the transform of every object drawn from a remote drag put back to what the host confirmed.</summary>
    private void MaskTransientEcho( string scene, Tracked tracked, Dictionary<string, ObjectState> live )
    {
        if ( dragReceiver.ActiveCount == 0 )
        {
            return;
        }
        double now = presenceClock.NowMs;
        foreach ( string id in live.Keys.ToList() )
        {
            if ( dragReceiver.IsActive( scene, id, now ) && tracked.Known.TryGetValue( id, out ObjectState known ) )
            {
                live[id] = TransientEcho.CopyTransform( live[id], known );
            }
        }
    }

    /// <summary>Every 33 ms, from the presence tick: sends the live pose of the objects this person is dragging, and the final pose when the drag ends.</summary>
    private void TickDragSend()
    {
        if ( !hostTakesTransients || wire == null || dragSenders.Count == 0 )
        {
            return;
        }
        double now = presenceClock.NowMs;
        bool mouseDown = continuousEdits > 0 || Editor.Application.MouseButtons.HasFlag( MouseButtons.Left );
        foreach ( string scene in dragSenders.Keys.ToList() )
        {
            TransientCadence cadence = dragSenders[scene];
            Tracked tracked = TrackedFor( scene );
            if ( tracked == null || !tracked.Session.Scene.IsValid() || !SceneEditorSession.All.Contains( tracked.Session ) )
            {
                dragSenders.Remove( scene );
                continue;
            }
            foreach ( string id in cadence.Ids.ToList() )
            {
                GameObject go = Guid.TryParse( id, out Guid guid ) ? tracked.Session.Scene.Directory.FindByGuid( guid ) : null;
                if ( go == null || !go.IsValid() )
                {
                    continue;
                }
                Vector3 position = go.LocalPosition;
                Rotation rotation = go.LocalRotation;
                Vector3 scale = go.LocalScale;
                cadence.Update( id, new Pose( position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w, scale.x, scale.y, scale.z ), now );
            }
            if ( mouseDown )
            {
                SendTransient( scene, cadence.TakeDue( now ), final: false );
            }
            else
            {
                // The button is up: the edit that follows carries the committed state; this is the exact last pose and the end of the stream.
                SendTransient( scene, cadence.TakeFinal(), final: true );
                dragSenders.Remove( scene );
            }
        }
    }

    private void SendTransient( string scene, List<TransientItem> items, bool final )
    {
        if ( items.Count == 0 )
        {
            return;
        }
        Packet packet = TransientCodec.Build( scene, Peer, presenceClock.NowMs, items, final );
        wire.Send( packet );
    }

    private void ReceiveTransient( Packet p )
    {
        if ( p.Peer == Peer || TransientCodec.Validate( p, out List<TransientItem> items ) != null )
        {
            return;
        }
        Tracked tracked = TrackedFor( p.Scene );
        if ( tracked == null || !tracked.Ready || tracked.Recovery.Resyncing || tracked.Recovery.Paused || tracked.Session.IsPlaying )
        {
            return;
        }
        dragReceiver.OnPacket( p.Peer, p.Scene, p.SentMs, items, p.Final, presenceClock.NowMs, id => IsLocalDragging( p.Scene, id ) );
    }

    /// <summary>An accepted packet from another editor arrived: the authoritative state replaces the drawn pose, and the shown capture is read without it.</summary>
    private void NoteAcceptedForDrag( Packet p, Tracked tracked, Dictionary<string, ObjectState> shown )
    {
        double now = presenceClock.NowMs;
        foreach ( Change change in p.Changes )
        {
            if ( shown.TryGetValue( change.Id, out ObjectState live ) && dragReceiver.IsActive( p.Scene, change.Id, now ) && tracked.Known.TryGetValue( change.Id, out ObjectState known ) )
            {
                shown[change.Id] = TransientEcho.CopyTransform( live, known );
            }
            dragReceiver.OnAccepted( p.Scene, change.Id, p.Final, now );
        }
    }

    /// <summary>Every frame: draws the other editors' drags from interpolated samples.</summary>
    private void DrawRemoteDrags()
    {
        if ( dragReceiver.ActiveCount == 0 )
        {
            return;
        }
        double now = presenceClock.NowMs;
        try
        {
            dragWrites.Clear();
            dragReceiver.Sample( now, dragWrites );
            foreach ( var write in dragWrites )
            {
                WriteLiveTransform( write.Scene, write.Id, write.Pose );
            }
            foreach ( var expired in dragReceiver.TakeExpired( now ) )
            {
                // Never confirmed by an accepted edit: the object goes back to what the host holds.
                Tracked tracked = TrackedFor( expired.Scene );
                if ( tracked != null && tracked.Known.TryGetValue( expired.Id, out ObjectState known ) && TransientEcho.TryReadPose( known, out Pose pose ) )
                {
                    WriteLiveTransform( expired.Scene, expired.Id, pose );
                }
            }
        }
        catch ( Exception )
        {
            // A drawn drag is only a picture; nothing about it may end the session.
            dragReceiver.Clear();
        }
    }

    private void WriteLiveTransform( string scene, string id, Pose pose )
    {
        Tracked tracked = TrackedFor( scene );
        if ( tracked == null || !tracked.Session.Scene.IsValid() || !SceneEditorSession.All.Contains( tracked.Session ) || tracked.Session.IsPlaying )
        {
            return;
        }
        GameObject go = Guid.TryParse( id, out Guid guid ) ? tracked.Session.Scene.Directory.FindByGuid( guid ) : null;
        if ( go == null || !go.IsValid() )
        {
            return;
        }
        go.LocalPosition = new Vector3( pose.X, pose.Y, pose.Z );
        go.LocalRotation = new Rotation( pose.Qx, pose.Qy, pose.Qz, pose.Qw );
        go.LocalScale = new Vector3( pose.Sx, pose.Sy, pose.Sz );
    }
}
