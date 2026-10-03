using System;
using System.Collections.Generic;
using System.Linq;

namespace TeamCreate;

/// <summary>What a scene does after applying the host's state to it threw in this editor.</summary>
public enum ApplyRecoveryDecision
{
    /// <summary>Back the scene up, hold this editor's edits and ask the host for a fresh snapshot (a re-<c>subscribe</c>).</summary>
    Resync,

    /// <summary>The fresh snapshot failed too: apply it object by object and leave out the objects this editor cannot show.</summary>
    SkipObjects,

    /// <summary>Too many resyncs in a short time: stop synchronizing this scene until it is reopened. The session continues.</summary>
    Pause
}

/// <summary>
/// The per-scene policy that replaces "an apply threw, so the session ends". Applying another person's change can throw for
/// reasons that have nothing to do with the session's integrity: an engine exception on an unusual component, a parent that
/// the engine refuses, GUID trouble. Before this, any of them reached <c>Fail</c> and everybody's session for this editor ended.
/// Now the scene asks the host for its state again, then isolates what still fails, then, if it keeps failing, pauses only that
/// scene. Pure and engine-independent: the editor glue captures, applies through a delegate and writes the answer back.
/// </summary>
/// <remarks>
/// Integrity failures are not routed here and stay fail-closed: a malformed host graph (<see cref="StateRules.ValidateGraph"/>),
/// decryption or authentication errors, a changed host session, a fatal packet. Those mean the session itself cannot be trusted.
/// </remarks>
public sealed class ApplyRecovery
{
    public const int MaxResyncs = 3;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds( 60 );

    private const int MaxReasonLength = 120;

    private readonly Queue<DateTime> resyncs = new Queue<DateTime>();

    /// <summary>A fresh snapshot has been requested and not applied yet.</summary>
    public bool Resyncing { get; private set; }

    /// <summary>The scene repeatedly failed to apply; nothing is sent for it until it is reopened.</summary>
    public bool Paused { get; private set; }

    /// <summary>Local edits stay in the live scene and are not sent: they are replayed on top of the fresh snapshot, or kept locally while paused.</summary>
    public bool HoldsOutgoing => Resyncing || Paused;

    /// <summary>Accepted and settings packets are applied to the editor only when neither resyncing (the snapshot will contain them) nor paused.</summary>
    public bool AppliesIncoming => !Resyncing && !Paused;

    /// <summary>
    /// The decision for one failed apply at <paramref name="now"/>. <paramref name="snapshot"/> is true when what failed was a
    /// snapshot; it is the fresh one this scene asked for exactly when the scene is resyncing, and only then is it isolated per
    /// object (an unrequested snapshot, such as a drift resync, is first retried as a whole). Isolation does not consume the
    /// resync budget; each resync does, and the fourth within <see cref="Window"/> pauses the scene.
    /// </summary>
    public ApplyRecoveryDecision Failed( DateTime now, bool snapshot )
    {
        if ( Paused )
        {
            return ApplyRecoveryDecision.Pause;
        }
        if ( snapshot && Resyncing )
        {
            return ApplyRecoveryDecision.SkipObjects;
        }
        while ( resyncs.Count > 0 && now - resyncs.Peek() >= Window )
        {
            resyncs.Dequeue();
        }
        if ( resyncs.Count >= MaxResyncs )
        {
            Pause();
            return ApplyRecoveryDecision.Pause;
        }
        resyncs.Enqueue( now );
        Resyncing = true;
        return ApplyRecoveryDecision.Resync;
    }

    /// <summary>A resync that is not caused by a failed apply (the play-mode buffer overflowed, a reopened scene with an unanswered edit). Not counted.</summary>
    public void Requested()
    {
        if ( !Paused )
        {
            Resyncing = true;
        }
    }

    public void Succeeded()
    {
        Resyncing = false;
    }

    public void Pause()
    {
        Resyncing = false;
        Paused = true;
    }

    public void Reopen()
    {
        Resyncing = false;
        Paused = false;
        resyncs.Clear();
    }

    /// <summary>
    /// Makes a scan see every skipped object exactly as the host last confirmed it: present with the host's JSON when the host has
    /// it, absent when it does not. A skipped object is one this editor cannot show or capture, so whatever the live scene holds
    /// for it (nothing, or a stale local copy) is never this person's change and must never be sent, least of all as a deletion.
    /// </summary>
    public static Dictionary<string, ObjectState> PreserveSkipped( IReadOnlyDictionary<string, ObjectState> known, Dictionary<string, ObjectState> live, IEnumerable<string> skipped )
    {
        foreach ( string id in skipped )
        {
            if ( known.TryGetValue( id, out ObjectState state ) )
            {
                live[id] = state;
            }
            else
            {
                live.Remove( id );
            }
        }
        return live;
    }

    /// <summary>
    /// Applies <paramref name="target"/> (a fresh snapshot that failed as a whole) in parts, and returns the state the editor now
    /// shows. <paramref name="tryApply"/> brings the live scene to a whole candidate state and returns the exception it threw, or
    /// null. Objects that still fail on their own are recorded in <paramref name="skipped"/> with a short reason; their local
    /// version, if the editor had one, stays as it was; descendants of an object that cannot be shown are skipped with it. Parts
    /// are bisected (a failing group is halved), so a scene with one bad object costs a logarithmic number of applies, not one
    /// per object.
    /// </summary>
    public static Dictionary<string, ObjectState> IsolateObjects( IReadOnlyDictionary<string, ObjectState> target, IReadOnlyDictionary<string, ObjectState> live, Func<Dictionary<string, ObjectState>, Exception> tryApply, IDictionary<string, string> skipped )
    {
        // The starting point is what the editor already shows of the host's objects: an object the host deleted is dropped here,
        // and an object kept from the live scene keeps its local version until its host version applies.
        Dictionary<string, ObjectState> shown = new Dictionary<string, ObjectState>();
        foreach ( KeyValuePair<string, ObjectState> pair in live )
        {
            if ( target.ContainsKey( pair.Key ) )
            {
                shown[pair.Key] = pair.Value;
            }
        }
        PruneOrphans( shown );
        Exception basis = tryApply( shown );
        if ( basis != null )
        {
            // The host's deletions cannot be carried out in this editor. Those objects stay, recorded as skipped so the scan never
            // sends them back as creations, and the parts below no longer try to delete them.
            foreach ( KeyValuePair<string, ObjectState> pair in live )
            {
                if ( !target.ContainsKey( pair.Key ) )
                {
                    shown[pair.Key] = pair.Value;
                    skipped[pair.Key] = ShortReason( basis.Message );
                }
            }
            PruneOrphans( shown );
            basis = tryApply( shown );
            if ( basis != null )
            {
                // Nothing can be changed in this scene at all; every object the host has differently is left as it is.
                foreach ( ObjectState state in target.Values )
                {
                    if ( !shown.TryGetValue( state.Id, out ObjectState current ) || !StateRules.Equal( current, state ) )
                    {
                        skipped[state.Id] = ShortReason( basis.Message );
                    }
                }
                return shown;
            }
        }

        List<ObjectState> order = TopologicalOrder( target );
        int index = 0;
        int chunk = Math.Max( 1, order.Count );
        while ( index < order.Count )
        {
            int take = Math.Min( chunk, order.Count - index );
            Dictionary<string, ObjectState> candidate = new Dictionary<string, ObjectState>( shown );
            bool any = false;
            for ( int k = index; k < index + take; k++ )
            {
                ObjectState state = order[k];
                if ( !string.IsNullOrEmpty( state.Parent ) && !candidate.ContainsKey( state.Parent ) )
                {
                    // Parents come first, so a parent missing here was itself left out and its object cannot be shown either.
                    skipped[state.Id] = ParentReason;
                    candidate.Remove( state.Id );
                    continue;
                }
                candidate[state.Id] = state;
                any = true;
            }
            Exception error = any ? tryApply( candidate ) : null;
            if ( error == null )
            {
                if ( any )
                {
                    shown = candidate;
                }
                index += take;
                chunk = take * 2;
            }
            else if ( take == 1 )
            {
                skipped[order[index].Id] = ShortReason( error.Message );
                index++;
            }
            else
            {
                chunk = Math.Max( 1, take / 2 );
            }
        }
        return shown;
    }

    private const string ParentReason = "its parent object could not be shown";

    /// <summary>
    /// The state to write after a fresh snapshot was applied (<paramref name="shown"/> is what the editor shows of it): every edit the
    /// person made while the scene was held (<paramref name="held"/> against <paramref name="heldBase"/>, the confirmed state it was
    /// captured against) is merged onto the host's fresh state with the existing rebase (<see cref="ChangeRebase.RebaseOntoHostChange"/>),
    /// in the order the held scene lists the objects. The next scan then sends them as ordinary edits. An edit to a skipped object is
    /// never replayed: this editor cannot show that object, so its edit is not this person's to send.
    /// </summary>
    public static Dictionary<string, ObjectState> ReplayHeld( IReadOnlyDictionary<string, ObjectState> heldBase, IReadOnlyDictionary<string, ObjectState> held, IReadOnlyDictionary<string, ObjectState> fresh, IReadOnlyDictionary<string, ObjectState> shown, ISet<string> skipped, List<string> notices )
    {
        Dictionary<string, ObjectState> result = new Dictionary<string, ObjectState>( shown );
        foreach ( string id in held.Keys.Concat( heldBase.Keys.Where( x => !held.ContainsKey( x ) ) ) )
        {
            ObjectState baseState = heldBase.GetValueOrDefault( id );
            ObjectState local = held.GetValueOrDefault( id );
            if ( StateRules.Equal( baseState, local ) || skipped.Contains( id ) )
            {
                continue;
            }
            ObjectState hostState = fresh.GetValueOrDefault( id );
            if ( baseState is null && hostState is null )
            {
                // Created here while held and unknown to the host: an ordinary creation for the next scan to send. (The rebase
                // below reads "absent on the host" as "deleted by another collaborator", which is right only for known objects.)
                result[id] = local;
                continue;
            }
            RebaseResult rebased = ChangeRebase.RebaseOntoHostChange( id, baseState, local, hostState );
            notices.Add( rebased.Notice );
            if ( rebased.NewLocal is null )
            {
                result.Remove( id );
            }
            else
            {
                result[id] = rebased.NewLocal;
            }
        }
        return result;
    }

    /// <summary>A copy of <paramref name="current"/> with the entries of <paramref name="prior"/> put back (null = the object was absent).</summary>
    public static Dictionary<string, ObjectState> WithPrior( IReadOnlyDictionary<string, ObjectState> current, IReadOnlyDictionary<string, ObjectState> prior )
    {
        Dictionary<string, ObjectState> result = new Dictionary<string, ObjectState>( current );
        foreach ( KeyValuePair<string, ObjectState> pair in prior )
        {
            if ( pair.Value is null )
            {
                result.Remove( pair.Key );
            }
            else
            {
                result[pair.Key] = pair.Value;
            }
        }
        return result;
    }

    /// <summary>Records what the host accepted into the confirmed state without touching the editor (a paused scene still follows the host on paper).</summary>
    public static void Record( Dictionary<string, ObjectState> known, IEnumerable<Change> changes )
    {
        foreach ( Change change in changes ?? Enumerable.Empty<Change>() )
        {
            if ( change.After is null )
            {
                known.Remove( change.Id );
            }
            else
            {
                known[change.Id] = change.After;
            }
        }
    }

    /// <summary>
    /// Whether the live scene must be written to the recovery folder before the host's snapshot replaces it. Unsent scene settings
    /// count as much as unsent objects: the backup is the engine's whole scene document (<c>Scene.Serialize</c> writes the scene's
    /// <c>Properties</c> next to its <c>GameObjects</c>), so one file holds both.
    /// </summary>
    public static bool NeedsBackup( bool unsentObjects, bool unsentSettings )
    {
        return unsentObjects || unsentSettings;
    }

    /// <summary>The first line of an engine message, bounded, for a sentence shown to the user.</summary>
    public static string ShortReason( string message )
    {
        string reason = ( message ?? "" ).Split( '\n' )[0].Trim();
        if ( reason.Length == 0 )
        {
            return "unknown engine failure";
        }
        return reason.Length > MaxReasonLength ? reason.Substring( 0, MaxReasonLength ) : reason;
    }

    public static string SkippedNotice( string name, string reason )
    {
        return "\"" + name + "\" could not be shown in your editor (" + reason + "). It is kept unchanged for everyone else.";
    }

    public static string PausedNotice( string scene )
    {
        return "Synchronization of \"" + scene + "\" is paused because it repeatedly failed to apply. Your changes to this scene are kept locally. Reopen the scene to try again.";
    }

    // Parents before children, otherwise in the snapshot's order. An object whose parent is not in the snapshot comes last, where
    // the isolation loop leaves it out.
    private static List<ObjectState> TopologicalOrder( IReadOnlyDictionary<string, ObjectState> target )
    {
        List<ObjectState> order = new List<ObjectState>( target.Count );
        HashSet<string> placed = new HashSet<string>( StringComparer.Ordinal );
        List<ObjectState> remaining = target.Values.ToList();
        bool progress = true;
        while ( remaining.Count > 0 && progress )
        {
            progress = false;
            List<ObjectState> next = new List<ObjectState>();
            foreach ( ObjectState state in remaining )
            {
                if ( string.IsNullOrEmpty( state.Parent ) || placed.Contains( state.Parent ) )
                {
                    order.Add( state );
                    placed.Add( state.Id );
                    progress = true;
                }
                else
                {
                    next.Add( state );
                }
            }
            remaining = next;
        }
        order.AddRange( remaining );
        return order;
    }

    // Removes entries whose parent chain is not complete, so a starting state handed to the engine is always a valid graph.
    private static void PruneOrphans( Dictionary<string, ObjectState> states )
    {
        bool removed = true;
        while ( removed )
        {
            removed = false;
            foreach ( ObjectState state in states.Values.ToList() )
            {
                if ( !string.IsNullOrEmpty( state.Parent ) && !states.ContainsKey( state.Parent ) )
                {
                    states.Remove( state.Id );
                    removed = true;
                }
            }
        }
    }
}
