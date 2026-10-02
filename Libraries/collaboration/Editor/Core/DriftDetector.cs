using System;
using System.Collections.Generic;

namespace TeamCreate;

/// <summary>What the host should do about one member after a ping that carried a scene digest.</summary>
public enum DriftVerdict
{
    /// <summary>Nothing: the member agrees with the host, edits are in flight, the evidence is too thin, or a recent resync is still settling.</summary>
    None,

    /// <summary>The member's scene has silently differed from the host's across consecutive pings: send it a fresh snapshot.</summary>
    Resync,

    /// <summary>Resyncs have not held. The host should tell the user rather than send another snapshot.</summary>
    Persistent
}

/// <summary>
/// Decides when a member's scene has drifted from the host's, from the digest each ping carries. Pure and clock-free (the caller passes
/// the time) so every rule can be tested without waiting.
///
/// A difference in digests only means drift when nothing is in flight, so the comparison happens only when the member has seen the
/// host's latest revision, and a single mismatch is never enough: a ping can cross an edit on the wire. Two mismatches in a row at equal
/// revisions mean the member really holds something else. A member that keeps drifting is limited to one resync per
/// <see cref="MinSecondsBetweenResyncs"/> seconds, and after <see cref="PersistentResyncCount"/> resyncs within
/// <see cref="PersistentWindowSeconds"/> seconds the verdict becomes <see cref="DriftVerdict.Persistent"/> instead of looping.
/// </summary>
public sealed class DriftDetector
{
    /// <summary>Mismatches in a row, at equal revisions, before a resync. Two, so one ping that crossed an edit cannot trigger it.</summary>
    public const int MismatchesToResync = 2;

    /// <summary>The least time between two resyncs of one member.</summary>
    public const double MinSecondsBetweenResyncs = 10;

    /// <summary>Resyncs within the window after which more are pointless and the user is told instead.</summary>
    public const int PersistentResyncCount = 5;

    /// <summary>The window, in seconds, that <see cref="PersistentResyncCount"/> is counted over.</summary>
    public const double PersistentWindowSeconds = 300;

    private sealed class MemberState
    {
        public string Scene;

        public int Streak;

        public readonly List<double> Resyncs = new List<double>();
    }

    private readonly Dictionary<string, MemberState> members = new Dictionary<string, MemberState>( StringComparer.Ordinal );

    /// <summary>
    /// Judges one ping. <paramref name="clientRevision"/> is the last revision the member had applied and <paramref name="hostRevision"/>
    /// the host's current one; the digests are the member's confirmed state and the host's, of <paramref name="scene"/>.
    /// <paramref name="nowSeconds"/> is any monotonic clock in seconds.
    /// </summary>
    public DriftVerdict ShouldResync( string memberKey, string scene, long clientRevision, string clientDigest, long hostRevision, string hostDigest, double nowSeconds )
    {
        if ( memberKey == null )
        {
            return DriftVerdict.None;
        }
        if ( !members.TryGetValue( memberKey, out MemberState state ) )
        {
            state = new MemberState();
            members[memberKey] = state;
        }

        // Without both digests nothing can be compared, and a gap in the evidence breaks "in a row": the safe reading is that the
        // member is not known to be drifting.
        if ( string.IsNullOrEmpty( clientDigest ) || string.IsNullOrEmpty( hostDigest ) )
        {
            state.Streak = 0;
            return DriftVerdict.None;
        }

        // A streak belongs to one scene: two mismatches about different scenes are not the same drift confirmed twice.
        if ( !string.Equals( state.Scene, scene, StringComparison.Ordinal ) )
        {
            state.Scene = scene;
            state.Streak = 0;
        }

        // A member that has not yet seen the host's latest revision is behind, or has edits in flight, and a difference is then
        // expected. Only equal revisions make the digests comparable.
        if ( clientRevision != hostRevision )
        {
            state.Streak = 0;
            return DriftVerdict.None;
        }

        if ( string.Equals( clientDigest, hostDigest, StringComparison.Ordinal ) )
        {
            state.Streak = 0;
            return DriftVerdict.None;
        }

        state.Streak++;
        if ( state.Streak < MismatchesToResync )
        {
            return DriftVerdict.None;
        }

        state.Resyncs.RemoveAll( at => nowSeconds - at > PersistentWindowSeconds );
        if ( state.Resyncs.Count >= PersistentResyncCount )
        {
            // Starting the count again keeps the verdict from being repeated on every ping; it is returned again after two more
            // mismatches, which is as often as anyone needs the notice refreshed.
            state.Streak = 0;
            return DriftVerdict.Persistent;
        }

        // Within the cap the streak is kept: the member is still drifting, so the first mismatch after the cap lifts resyncs at once.
        if ( state.Resyncs.Count > 0 && nowSeconds - state.Resyncs[state.Resyncs.Count - 1] < MinSecondsBetweenResyncs )
        {
            return DriftVerdict.None;
        }

        state.Resyncs.Add( nowSeconds );
        state.Streak = 0;
        return DriftVerdict.Resync;
    }

    /// <summary>Drops everything remembered about a member, for when it leaves, so a new member never inherits a stranger's history.</summary>
    public void Forget( string memberKey )
    {
        if ( memberKey != null )
        {
            members.Remove( memberKey );
        }
    }
}
