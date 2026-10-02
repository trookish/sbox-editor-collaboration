using System.Text.Json.Nodes;
using TeamCreate;

/// <summary>
/// The client side of the host's property-level merge: what the live scene must become when the host's change arrives while
/// the user has an edit the host has not seen yet, and when the host answers the user's own edit with a merged result. Before
/// this existed a mid-drag object failed the whole session ("Concurrent edit on ...") and a merged receipt was never applied,
/// so the next scan sent the old local state back and reverted the other person's change.
/// </summary>
static class ChangeRebaseTests
{
    // ---- Builders -------------------------------------------------------------------------------

    static ObjectState Cube( string id, string name = "Cube", string position = "0,0,0", params JsonObject[] components )
    {
        var list = new JsonArray();
        foreach ( var component in components )
        {
            list.Add( component.DeepClone() );
        }
        return new ObjectState( id, "", new JsonObject
        {
            ["__guid"] = id,
            ["Name"] = name,
            ["Position"] = position,
            ["Rotation"] = "0,0,0,1",
            ["Scale"] = "1,1,1",
            ["Enabled"] = true,
            ["Tags"] = "",
            ["Components"] = list,
        } );
    }

    static JsonObject Renderer( string guid, string tint = "1,1,1,1" ) => new()
    {
        ["__guid"] = guid,
        ["__type"] = "Sandbox.ModelRenderer",
        ["Tint"] = tint,
    };

    static JsonObject Light( string guid, string radius = "512" ) => new()
    {
        ["__guid"] = guid,
        ["__type"] = "Sandbox.PointLight",
        ["Radius"] = radius,
    };

    static ObjectState With( ObjectState state, string field, JsonNode value )
    {
        var data = (JsonObject)state.Data.DeepClone();
        data[field] = value;
        return state with { Data = data };
    }

    static ObjectState WithComponents( ObjectState state, params JsonObject[] components )
    {
        var list = new JsonArray();
        foreach ( var component in components )
        {
            list.Add( component.DeepClone() );
        }
        return With( state, "Components", list );
    }

    static string Tint( ObjectState state ) => state.Data["Components"].AsArray()[0]["Tint"].GetValue<string>();

    const string IdA = "11111111-1111-1111-1111-111111111111";
    const string RenderGuid = "22222222-2222-2222-2222-222222222222";
    const string LightGuid = "33333333-3333-3333-3333-333333333333";

    // ---- Tests ----------------------------------------------------------------------------------

    public static void Run( Action<bool, string> assert )
    {
        HostChange( assert );
        HostChangeEdgeCases( assert );
        RemoteChange( assert );
        OwnReceipt( assert );
        Notices( assert );
    }

    static void HostChange( Action<bool, string> assert )
    {
        var known = WithComponents( Cube( IdA ), Renderer( RenderGuid ) );

        // No unsent local edit: the scene simply takes the host's state.
        var hostAfter = WithComponents( Cube( IdA, position: "5,0,0" ), Renderer( RenderGuid ) );
        var plain = ChangeRebase.RebaseOntoHostChange( IdA, known, known, hostAfter );
        assert( StateRules.Equal( plain.NewLocal, hostAfter ) && plain.Notice == null, "rebase_host_change_without_local_edit_takes_host_state" );

        // The user is mid-drag on Position while the host changed Tint: both survive and nobody is told anything.
        var hostTint = WithComponents( Cube( IdA ), Renderer( RenderGuid, "1,0,0,1" ) );
        var dragging = WithComponents( Cube( IdA, position: "9,0,0" ), Renderer( RenderGuid ) );
        var mid = ChangeRebase.RebaseOntoHostChange( IdA, known, dragging, hostTint );
        assert( mid.NewLocal != null && mid.NewLocal.Data["Position"].GetValue<string>() == "9,0,0" && Tint( mid.NewLocal ) == "1,0,0,1" && mid.Notice == null,
            "rebase_host_change_mid_drag_keeps_both_edits" );

        // Same property on both sides: the user's unsent edit wins and the notice names the property.
        var hostMoved = WithComponents( Cube( IdA, position: "5,0,0" ), Renderer( RenderGuid ) );
        var clash = ChangeRebase.RebaseOntoHostChange( IdA, known, dragging, hostMoved );
        assert( clash.NewLocal.Data["Position"].GetValue<string>() == "9,0,0", "rebase_host_change_same_property_local_wins" );
        assert( clash.Notice == "Your unsent change to \"Cube\" replaced another collaborator's edit to: Position.", "rebase_host_change_clash_notice_names_property" );

        // A component edit and an object-field edit from opposite sides combine.
        var localComponent = WithComponents( Cube( IdA ), Renderer( RenderGuid, "0,0,1,1" ) );
        var hostField = With( known, "Name", "Renamed" );
        var combined = ChangeRebase.RebaseOntoHostChange( IdA, known, localComponent, hostField );
        assert( combined.NewLocal.Data["Name"].GetValue<string>() == "Renamed" && Tint( combined.NewLocal ) == "0,0,1,1" && combined.Notice == null, "rebase_host_change_component_edit_vs_field_edit" );

        // A component the host added while the user edited a field survives next to the user's edit.
        var hostAddedLight = WithComponents( Cube( IdA ), Renderer( RenderGuid ), Light( LightGuid ) );
        var localRename = With( known, "Name", "Mine" );
        var added = ChangeRebase.RebaseOntoHostChange( IdA, known, localRename, hostAddedLight );
        assert( added.NewLocal.Data["Name"].GetValue<string>() == "Mine" && added.NewLocal.Data["Components"].AsArray().Count == 2 && added.Notice == null, "rebase_host_change_component_add_vs_field_edit" );

        // The clash list names every clashing property, components included.
        var hostBoth = WithComponents( Cube( IdA, position: "5,0,0" ), Renderer( RenderGuid, "1,0,0,1" ) );
        var localBoth = WithComponents( Cube( IdA, position: "9,0,0" ), Renderer( RenderGuid, "0,1,0,1" ) );
        var both = ChangeRebase.RebaseOntoHostChange( IdA, known, localBoth, hostBoth );
        assert( both.Notice == "Your unsent change to \"Cube\" replaced another collaborator's edit to: Position, ModelRenderer.Tint.", "rebase_host_change_clash_notice_lists_every_property" );
    }

    static void HostChangeEdgeCases( Action<bool, string> assert )
    {
        var known = Cube( IdA, "Crate" );
        var edited = With( known, "Position", "9,0,0" );

        // The host deleted an object the user edited: the unsent edit is discarded and the user is told why.
        var deleted = ChangeRebase.RebaseOntoHostChange( IdA, known, edited, null );
        assert( deleted.NewLocal == null, "rebase_host_delete_discards_unsent_edit" );
        assert( deleted.Notice == "Your unsent change to \"Crate\" was discarded because the object was deleted by another collaborator.", "rebase_host_delete_notice_is_formal" );

        // The host deleted an object the user did not touch: it goes away silently.
        var quiet = ChangeRebase.RebaseOntoHostChange( IdA, known, known, null );
        assert( quiet.NewLocal == null && quiet.Notice == null, "rebase_host_delete_without_local_edit_is_silent" );

        // Both deleted it: nothing to say.
        var bothGone = ChangeRebase.RebaseOntoHostChange( IdA, known, null, null );
        assert( bothGone.NewLocal == null && bothGone.Notice == null, "rebase_host_delete_both_sides_is_silent" );

        // The user deleted it locally (not yet sent) while the host edited it: the pending deletion stays, later edit wins.
        var hostEdit = With( known, "Name", "Theirs" );
        var pendingDelete = ChangeRebase.RebaseOntoHostChange( IdA, known, null, hostEdit );
        assert( pendingDelete.NewLocal == null && pendingDelete.Notice == null, "rebase_local_delete_stays_pending_against_host_edit" );

        // An object the host created and the user never had.
        var created = ChangeRebase.RebaseOntoHostChange( IdA, null, null, known );
        assert( StateRules.Equal( created.NewLocal, known ) && created.Notice == null, "rebase_host_creation_is_applied" );

        // `known` null while the user holds a different version: cannot be combined, the host's version replaces it.
        var unknown = ChangeRebase.RebaseOntoHostChange( IdA, null, edited, hostEdit );
        assert( StateRules.Equal( unknown.NewLocal, hostEdit ), "rebase_null_known_takes_host_state" );
        assert( unknown.Notice == "Your unsent change to \"Crate\" could not be combined with another collaborator's edit and was replaced by theirs.", "rebase_null_known_notice_says_could_not_combine" );

        // A merge that cannot be done (a component without a guid) falls back the same way.
        var broken = With( edited, "Components", new JsonArray( new JsonObject { ["__type"] = "Sandbox.ModelRenderer" } ) );
        var fallback = ChangeRebase.RebaseOntoHostChange( IdA, known, broken, hostEdit );
        assert( StateRules.Equal( fallback.NewLocal, hostEdit ) && fallback.Notice.Contains( "could not be combined" ), "rebase_unmergeable_falls_back_to_host_state" );

        // Float noise is not an edit: a local state that only differs by the engine's 32-bit echo counts as unedited.
        var noisy = With( known, "Mass", JsonNode.Parse( "0.600000024" ) );
        var noiseKnown = With( known, "Mass", JsonNode.Parse( "0.6" ) );
        var noise = ChangeRebase.RebaseOntoHostChange( IdA, noiseKnown, noisy, hostEdit );
        assert( StateRules.Equal( noise.NewLocal, hostEdit ) && noise.Notice == null, "rebase_float_echo_counts_as_unedited" );

        // The result never aliases its inputs.
        var hostAlias = With( known, "Name", "Alias" );
        var taken = ChangeRebase.RebaseOntoHostChange( IdA, known, known, hostAlias );
        taken.NewLocal.Data["Name"] = "Mutated";
        taken.NewLocal.Data["Components"].AsArray().Add( new JsonObject { ["__guid"] = "x" } );
        assert( hostAlias.Data["Name"].GetValue<string>() == "Alias" && hostAlias.Data["Components"].AsArray().Count == 0, "rebase_result_does_not_alias_host_state" );
        var knownMerge = Cube( IdA );
        var localMerge = With( knownMerge, "Position", "4,0,0" );
        var hostMerge = With( knownMerge, "Name", "H" );
        var merged = ChangeRebase.RebaseOntoHostChange( IdA, knownMerge, localMerge, hostMerge );
        merged.NewLocal.Data["Name"] = "Mutated";
        merged.NewLocal.Data["Position"] = "Mutated";
        assert( hostMerge.Data["Name"].GetValue<string>() == "H" && localMerge.Data["Position"].GetValue<string>() == "4,0,0" && knownMerge.Data["Position"].GetValue<string>() == "0,0,0", "rebase_merged_result_does_not_alias_inputs" );
    }

    static void RemoteChange( Action<bool, string> assert )
    {
        // The follower's `known` normally equals the host's Before. When it does not (a bookkeeping gap) the host is the authority:
        // the session must not fail, and an object the user has not touched simply takes the host's state.
        var hostBefore = Cube( IdA, "Crate" );
        var hostAfter = With( hostBefore, "Position", "5,0,0" );
        var staleKnown = With( hostBefore, "Name", "Stale" );
        var untouched = ChangeRebase.RebaseOntoRemoteChange( IdA, staleKnown, staleKnown, hostBefore, hostAfter );
        assert( StateRules.Equal( untouched.NewLocal, hostAfter ) && untouched.Notice == null, "rebase_remote_stale_known_untouched_takes_host_state" );

        // Stale `known` and an unsent edit: the host's Before is the common ancestor of the merge.
        var local = With( staleKnown, "Scale", "2,2,2" );
        var edited = ChangeRebase.RebaseOntoRemoteChange( IdA, staleKnown, local, hostBefore, hostAfter );
        assert( edited.NewLocal.Data["Position"].GetValue<string>() == "5,0,0", "rebase_remote_stale_known_host_edit_survives" );
        assert( edited.NewLocal.Data["Scale"].GetValue<string>() == "2,2,2" && edited.NewLocal.Data["Name"].GetValue<string>() == "Stale", "rebase_remote_stale_known_local_edit_survives" );

        // A consistent `known` behaves exactly like RebaseOntoHostChange.
        var consistent = ChangeRebase.RebaseOntoRemoteChange( IdA, hostBefore, With( hostBefore, "Name", "Mine" ), hostBefore, hostAfter );
        assert( consistent.NewLocal.Data["Name"].GetValue<string>() == "Mine" && consistent.NewLocal.Data["Position"].GetValue<string>() == "5,0,0" && consistent.Notice == null, "rebase_remote_consistent_known_merges" );

        // A creation never consults the old known: the host's Before is null.
        var creation = ChangeRebase.RebaseOntoRemoteChange( IdA, null, null, null, hostAfter );
        assert( StateRules.Equal( creation.NewLocal, hostAfter ) && creation.Notice == null, "rebase_remote_creation_takes_host_state" );

        // A deletion with a stale known and an edit on top is discarded with the notice.
        var deleted = ChangeRebase.RebaseOntoRemoteChange( IdA, staleKnown, local, hostBefore, null );
        assert( deleted.NewLocal == null && deleted.Notice != null && deleted.Notice.Contains( "was deleted by another collaborator" ), "rebase_remote_delete_with_stale_known_discards_edit" );
    }

    static void OwnReceipt( Action<bool, string> assert )
    {
        var original = WithComponents( Cube( IdA, "Crate" ), Renderer( RenderGuid ) );
        var sent = With( original, "Position", "9,0,0" );

        // Nothing merged: the scene already shows what was sent, no edit is produced and no notice appears.
        var same = ChangeRebase.RebaseOntoOwnReceipt( IdA, sent, sent, sent );
        assert( StateRules.Equal( same.NewLocal, sent ) && same.Notice == null, "rebase_receipt_unmerged_changes_nothing" );

        // Merged: the host kept the user's Position and somebody else's Tint. The scene takes the host's state.
        var applied = WithComponents( sent, Renderer( RenderGuid, "1,0,0,1" ) );
        var merged = ChangeRebase.RebaseOntoOwnReceipt( IdA, sent, sent, applied );
        assert( StateRules.Equal( merged.NewLocal, applied ) && merged.Notice == null, "rebase_receipt_merged_is_applied_to_scene" );

        // The user kept dragging while the receipt was in flight: the merged state plus the further edit.
        var further = With( sent, "Scale", "3,3,3" );
        var onTop = ChangeRebase.RebaseOntoOwnReceipt( IdA, sent, further, applied );
        assert( onTop.NewLocal.Data["Scale"].GetValue<string>() == "3,3,3" && onTop.NewLocal.Data["Position"].GetValue<string>() == "9,0,0" && Tint( onTop.NewLocal ) == "1,0,0,1" && onTop.Notice == null,
            "rebase_receipt_further_local_edit_merged_on_top" );

        // The host applied exactly what was sent but the user edited further: the local state stays, the scan sends it next.
        var kept = ChangeRebase.RebaseOntoOwnReceipt( IdA, sent, further, sent );
        assert( StateRules.Equal( kept.NewLocal, further ) && kept.Notice == null, "rebase_receipt_unmerged_keeps_further_local_edit" );

        // Further edit on the same property the host changed: the user's newer edit wins and is named.
        var hostPosition = With( sent, "Position", "7,0,0" );
        var furtherPosition = With( sent, "Position", "8,0,0" );
        var clash = ChangeRebase.RebaseOntoOwnReceipt( IdA, sent, furtherPosition, hostPosition );
        assert( clash.NewLocal.Data["Position"].GetValue<string>() == "8,0,0" && clash.Notice != null && clash.Notice.Contains( "Position" ), "rebase_receipt_same_property_local_wins_and_names_it" );

        // The merge cannot be done: the host's state wins and the user is told.
        var broken = With( further, "Components", new JsonArray( new JsonObject { ["__type"] = "Sandbox.ModelRenderer" } ) );
        var fallback = ChangeRebase.RebaseOntoOwnReceipt( IdA, sent, broken, applied );
        assert( StateRules.Equal( fallback.NewLocal, applied ), "rebase_receipt_fallback_takes_applied_state" );
        assert( fallback.Notice == "Your unsent change to \"Crate\" could not be combined with another collaborator's edit and was replaced by theirs.", "rebase_receipt_fallback_notice_says_could_not_combine" );

        // The host's merge deleted the object: it goes away locally.
        var gone = ChangeRebase.RebaseOntoOwnReceipt( IdA, sent, sent, null );
        assert( gone.NewLocal == null && gone.Notice == null, "rebase_receipt_host_deleted_removes_object" );
        var goneFurther = ChangeRebase.RebaseOntoOwnReceipt( IdA, sent, further, null );
        assert( goneFurther.NewLocal == null && goneFurther.Notice != null && goneFurther.Notice.Contains( "discarded" ), "rebase_receipt_host_deleted_discards_further_edit_with_notice" );

        // The user deleted the object after sending the edit: the deletion stays pending and goes out next.
        var deletedLocally = ChangeRebase.RebaseOntoOwnReceipt( IdA, sent, null, applied );
        assert( deletedLocally.NewLocal == null && deletedLocally.Notice == null, "rebase_receipt_local_delete_stays_pending" );

        // The user's own deletion was answered with a restore (the host's merge kept a concurrent edit).
        var restored = ChangeRebase.RebaseOntoOwnReceipt( IdA, null, null, applied );
        assert( StateRules.Equal( restored.NewLocal, applied ), "rebase_receipt_restored_after_own_delete_is_applied" );

        // An echo that already matches the applied state needs nothing, whatever was sent (also covers a receipt whose sent
        // edit is no longer known).
        var already = ChangeRebase.RebaseOntoOwnReceipt( IdA, null, applied, applied );
        assert( StateRules.Equal( already.NewLocal, applied ) && already.Notice == null, "rebase_receipt_local_already_equals_applied" );

        // Whether the receipt needs any rebasing at all: only when the host combined something or applied a different After than was sent.
        assert( !ChangeRebase.NeedsOwnReceiptRebase( false, true, sent, sent ), "rebase_receipt_needs_no_rebase_when_unmerged_and_identical" );
        assert( ChangeRebase.NeedsOwnReceiptRebase( true, true, sent, sent ), "rebase_receipt_needs_rebase_when_host_says_merged" );
        assert( ChangeRebase.NeedsOwnReceiptRebase( false, true, sent, applied ), "rebase_receipt_needs_rebase_when_applied_differs_from_sent" );
        assert( !ChangeRebase.NeedsOwnReceiptRebase( false, false, null, applied ), "rebase_receipt_unknown_sent_edit_is_not_compared" );

        // The result never aliases the inputs.
        var aliasApplied = With( sent, "Name", "Alias" );
        var taken = ChangeRebase.RebaseOntoOwnReceipt( IdA, sent, sent, aliasApplied );
        taken.NewLocal.Data["Name"] = "Mutated";
        assert( aliasApplied.Data["Name"].GetValue<string>() == "Alias", "rebase_receipt_result_does_not_alias_applied_state" );
        var mergedAlias = ChangeRebase.RebaseOntoOwnReceipt( IdA, sent, further, applied );
        mergedAlias.NewLocal.Data["Scale"] = "Mutated";
        mergedAlias.NewLocal.Data["Position"] = "Mutated";
        assert( further.Data["Scale"].GetValue<string>() == "3,3,3" && applied.Data["Position"].GetValue<string>() == "9,0,0", "rebase_receipt_merged_result_does_not_alias_inputs" );
    }

    static void Notices( Action<bool, string> assert )
    {
        var known = Cube( IdA, "Crate" );
        var edited = With( known, "Position", "9,0,0" );

        // The name comes from another machine: control characters are replaced and the length is bounded.
        var hostile = With( edited, "Name", "Evil\r\nName\u0007\t!" );
        var hostileKnown = With( known, "Name", "Evil\r\nName\u0007\t!" );
        var discarded = ChangeRebase.RebaseOntoHostChange( IdA, hostileKnown, hostile, null );
        assert( discarded.Notice == "Your unsent change to \"Evil  Name  !\" was discarded because the object was deleted by another collaborator.", "rebase_notice_replaces_control_characters" );

        var longName = new string( 'x', 200 );
        var longKnown = With( known, "Name", longName );
        var longEdited = With( longKnown, "Position", "9,0,0" );
        var longNotice = ChangeRebase.RebaseOntoHostChange( IdA, longKnown, longEdited, null ).Notice;
        assert( longNotice.Contains( new string( 'x', 60 ) ) && !longNotice.Contains( new string( 'x', 61 ) ), "rebase_notice_name_cut_at_sixty_characters" );

        // A missing or blank name falls back to the id; a non-string name does too.
        var nameless = new ObjectState( IdA, "", new JsonObject { ["__guid"] = IdA, ["Name"] = 5, ["Position"] = "1,1,1" } );
        var namelessNotice = ChangeRebase.RebaseOntoHostChange( IdA, nameless, With( nameless, "Position", "2,2,2" ), null ).Notice;
        assert( namelessNotice.Contains( "\"" + IdA + "\"" ), "rebase_notice_falls_back_to_id" );

        // Surrogate pairs are not cut in half at the limit.
        string emoji = "a" + string.Concat( Enumerable.Repeat( "\U0001F600", 40 ) );
        var emojiKnown = With( known, "Name", emoji );
        var emojiNotice = ChangeRebase.RebaseOntoHostChange( IdA, emojiKnown, With( emojiKnown, "Position", "1,0,0" ), null ).Notice;
        string between = emojiNotice.Substring( emojiNotice.IndexOf( '"' ) + 1 );
        between = between.Substring( 0, between.IndexOf( '"' ) );
        assert( between.Length <= 60 && !char.IsHighSurrogate( between[^1] ), "rebase_notice_cut_keeps_surrogate_pairs_whole" );

        // JoinNotices: a space between sentences, empties skipped, 300 characters at most.
        assert( ChangeRebase.JoinNotices( "A.", null, "", "B." ) == "A. B.", "rebase_join_notices_skips_empty_and_joins_with_space" );
        assert( ChangeRebase.JoinNotices( null, "" ) == null, "rebase_join_notices_nothing_to_say_is_null" );
        string joined = ChangeRebase.JoinNotices( new string( 'a', 200 ), new string( 'b', 200 ) );
        assert( joined.Length == 300 && joined.EndsWith( "..." ), "rebase_join_notices_bounded_to_300" );
        assert( ChangeRebase.JoinNotices( "One\nTwo\u0001" ) == "One Two", "rebase_join_notices_replaces_control_characters" );

        // The dock line: a merge notice is news, not a problem, and an error or recovery line still outranks it.
        assert( ControlsMessageRules.Compose( null, null, null, null, null, null, null, "Merged." ) == "Merged.", "rebase_dock_shows_merge_notice" );
        assert( ControlsMessageRules.Compose( null, null, null, null, "action", "undo", "save", "Merged." ) == "action", "rebase_dock_action_outranks_merge_notice" );
        assert( ControlsMessageRules.Compose( null, null, null, null, null, "undo", "save", "Merged." ) == "Merged.", "rebase_dock_merge_notice_outranks_undo_and_save" );
        assert( ControlsMessageRules.Compose( null, "failed", null, null, null, null, null, "Merged." ) == "failed", "rebase_dock_error_outranks_merge_notice" );
    }
}
