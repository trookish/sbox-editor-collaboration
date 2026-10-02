using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace TeamCreate;

/// <summary>What the live scene should become for one object, and what (if anything) to tell the user about it.</summary>
/// <param name="NewLocal">The state the live scene must hold for the object; null means the object must not exist locally. Never aliases any input.</param>
/// <param name="Notice">A formal sentence for the user, or null when nothing needs saying.</param>
public sealed record RebaseResult( ObjectState NewLocal, string Notice );

/// <summary>
/// The client half of the host's property-level merge. The host answers concurrent edits by combining them; this decides what
/// the editor's live scene must become when such a result arrives while the user holds an edit the host has not seen yet.
/// Without it a half-finished drag made the host's change fail the whole session, and a merged receipt was never applied, so
/// the next scan sent the pre-merge local state back and reverted the other person's change.
/// </summary>
/// <remarks>
/// Pure and engine-independent: every decision lives here so the editor glue stays a loop that captures the scene, calls these
/// functions and writes the answer back. After either call the editor must treat the host's state as the confirmed one
/// (<c>Known</c>), whatever <see cref="RebaseResult.NewLocal"/> says: any difference between the two is the user's unsent
/// edit, which the next scan sends on top of the host's state.
/// </remarks>
public static class ChangeRebase
{
    // Names arrive from another machine and are quoted inside a sentence shown in the dock.
    private const int MaxNameLength = 60;

    private const int MaxNoticeLength = 300;

    /// <summary>
    /// Another collaborator's change (<paramref name="hostAfter"/>, null = deleted) reached this editor. <paramref name="known"/> is the
    /// last state the host confirmed for the id and <paramref name="local"/> what the live scene holds now; they differ exactly when
    /// the user has an edit the host has not seen. The user's edit is kept on top of the host's change.
    /// </summary>
    public static RebaseResult RebaseOntoHostChange( string id, ObjectState known, ObjectState local, ObjectState hostAfter )
    {
        // No unsent edit: the scene simply follows the host.
        if ( StateRules.Equal( local, known ) )
        {
            return new RebaseResult( Clone( hostAfter ), null );
        }

        if ( hostAfter is null )
        {
            // The object is gone for everybody. Nothing to report when the user had deleted it too.
            if ( local is null )
            {
                return new RebaseResult( null, null );
            }
            return new RebaseResult( null, "Your unsent change to \"" + NameOf( id, local, known ) + "\" was discarded because the object was deleted by another collaborator." );
        }

        if ( local is null )
        {
            // The user's deletion has not gone out yet. It goes out next and, being the later edit, wins.
            return new RebaseResult( null, null );
        }

        if ( known is null || !PropertyMerge.TryMerge( hostAfter, known, local, out ObjectState merged, out List<string> clashed ) )
        {
            return new RebaseResult( Clone( hostAfter ), CouldNotCombine( id, local, hostAfter ) );
        }
        string notice = clashed.Count == 0
            ? null
            : "Your unsent change to \"" + NameOf( id, local, hostAfter ) + "\" replaced another collaborator's edit to: " + string.Join( ", ", clashed.Select( Clean ) ) + ".";
        return new RebaseResult( merged, notice );
    }

    /// <summary>
    /// The same as <see cref="RebaseOntoHostChange"/> for a change as it arrives on the wire. The follower's <paramref name="known"/> is
    /// expected to equal the host's <paramref name="hostBefore"/>; when it does not (a bookkeeping gap, never the user's fault) the
    /// session must not fail, because the host is the authority. An object the user has not touched then simply follows the host, and
    /// an edited one is merged using the host's <paramref name="hostBefore"/> as the common ancestor. A creation (hostBefore null)
    /// has no ancestor to compare against and uses <paramref name="known"/> as it is.
    /// </summary>
    public static RebaseResult RebaseOntoRemoteChange( string id, ObjectState known, ObjectState local, ObjectState hostBefore, ObjectState hostAfter )
    {
        ObjectState ancestor = known;
        if ( hostBefore is not null && !StateRules.Equal( known, hostBefore ) )
        {
            if ( StateRules.Equal( local, known ) )
            {
                return new RebaseResult( Clone( hostAfter ), null );
            }
            ancestor = hostBefore;
        }
        return RebaseOntoHostChange( id, ancestor, local, hostAfter );
    }

    /// <summary>
    /// The host answered this editor's own edit. <paramref name="sentAfter"/> is what the editor sent for the id (null when it sent a
    /// deletion or nothing), <paramref name="applied"/> what the host actually kept (null = the object is gone). When they differ the
    /// host combined the edit with somebody else's, and the scene must take the host's result, with whatever the user did after
    /// sending merged on top.
    /// </summary>
    public static RebaseResult RebaseOntoOwnReceipt( string id, ObjectState sentAfter, ObjectState local, ObjectState applied )
    {
        if ( applied is null )
        {
            // The host's merge removed the object. An edit made after sending has nothing left to apply to.
            bool lostEdit = local is not null && !StateRules.Equal( local, sentAfter );
            return new RebaseResult( null, lostEdit ? "Your unsent change to \"" + NameOf( id, local, sentAfter ) + "\" was discarded because the object was deleted by another collaborator." : null );
        }

        // Nothing to apply: the scene already shows the host's result (this also covers a receipt whose sent edit is unknown), or the
        // host kept exactly what was sent and whatever the user did since stays for the next scan to send.
        if ( StateRules.Equal( local, applied ) )
        {
            return new RebaseResult( Clone( applied ), null );
        }
        if ( StateRules.Equal( applied, sentAfter ) )
        {
            return new RebaseResult( Clone( local ), null );
        }

        // The host's result differs from what was sent. If the user has done nothing since, the scene takes it.
        if ( StateRules.Equal( local, sentAfter ) )
        {
            return new RebaseResult( Clone( applied ), null );
        }

        if ( local is null )
        {
            // Deleted after sending: that deletion has not gone out yet and, being later, wins.
            return new RebaseResult( null, null );
        }

        // The user edited further while the receipt was in flight.
        if ( sentAfter is null || !PropertyMerge.TryMerge( applied, sentAfter, local, out ObjectState merged, out List<string> clashed ) )
        {
            return new RebaseResult( Clone( applied ), CouldNotCombine( id, local, applied ) );
        }
        string notice = clashed.Count == 0
            ? null
            : "Your unsent change to \"" + NameOf( id, local, applied ) + "\" replaced another collaborator's edit to: " + string.Join( ", ", clashed.Select( Clean ) ) + ".";
        return new RebaseResult( merged, notice );
    }

    /// <summary>
    /// Whether an own receipt needs <see cref="RebaseOntoOwnReceipt"/> at all. The host sets <paramref name="hostSaysMerged"/> when it
    /// combined something; independently, an applied state that differs from the one sent means the same. When the sent edit is not
    /// known (<paramref name="sentKnown"/> false: the receipt outlived the pending packet, for example after a reconnect) only the
    /// host's flag can say, because there is nothing to compare with. When nothing is merged the editor does no extra work.
    /// </summary>
    public static bool NeedsOwnReceiptRebase( bool hostSaysMerged, bool sentKnown, ObjectState sentAfter, ObjectState applied )
    {
        return hostSaysMerged || ( sentKnown && !StateRules.Equal( applied, sentAfter ) );
    }

    /// <summary>
    /// Joins the sentences gathered while processing one packet into the single line the dock shows: a space between them, empty ones
    /// dropped, control characters replaced, at most 300 characters (the last three are an ellipsis when it was cut). Null when there is nothing to say.
    /// </summary>
    public static string JoinNotices( params string[] notices )
    {
        string joined = string.Join( " ", ( notices ?? Array.Empty<string>() ).Where( x => !string.IsNullOrWhiteSpace( x ) ).Select( Clean ) ).TrimEnd();
        if ( joined.Length == 0 )
        {
            return null;
        }
        if ( joined.Length <= MaxNoticeLength )
        {
            return joined;
        }
        return joined.Substring( 0, MaxNoticeLength - 3 ) + "...";
    }

    private static string CouldNotCombine( string id, ObjectState local, ObjectState theirs )
    {
        return "Your unsent change to \"" + NameOf( id, local, theirs ) + "\" could not be combined with another collaborator's edit and was replaced by theirs.";
    }

    // The name a person recognises: the first non-null state's Name, else the id. Sanitised and cut because it came from another machine.
    private static string NameOf( string id, params ObjectState[] states )
    {
        foreach ( ObjectState state in states )
        {
            if ( state?.Data?["Name"] is JsonValue value && value.TryGetValue( out string name ) && !string.IsNullOrWhiteSpace( name ) )
            {
                return Cut( Clean( name ), MaxNameLength );
            }
            if ( state is not null )
            {
                break;
            }
        }
        return Cut( Clean( id ?? string.Empty ), MaxNameLength );
    }

    private static string Clean( string text )
    {
        if ( string.IsNullOrEmpty( text ) )
        {
            return text ?? string.Empty;
        }
        StringBuilder builder = new StringBuilder( text.Length );
        foreach ( char c in text )
        {
            builder.Append( char.IsControl( c ) ? ' ' : c );
        }
        return builder.ToString();
    }

    // Cuts at `max` UTF-16 units without leaving half of a surrogate pair behind.
    private static string Cut( string text, int max )
    {
        if ( text.Length <= max )
        {
            return text;
        }
        int length = char.IsHighSurrogate( text[max - 1] ) ? max - 1 : max;
        return text.Substring( 0, length );
    }

    private static ObjectState Clone( ObjectState state )
    {
        return state is null ? null : new ObjectState( state.Id, state.Parent, (JsonObject)state.Data?.DeepClone() );
    }
}
