using TeamCreate;
using System.Text.Json.Nodes;

// The self-healing apply (ApplyRecovery): what a scene does when applying another person's change throws in this editor, the
// 3-per-60-seconds limit on resynchronizations, the per-object isolation of a fresh snapshot that still fails, and the replay of
// the edits held while the scene was resynchronizing. Every clock is injected.
static class ApplyRecoveryTests
{
    static ObjectState State( string id, string parent, string name, bool enabled = true )
    {
        return new ObjectState( id, parent, new JsonObject { ["Name"] = name, ["Enabled"] = enabled } );
    }

    static Dictionary<string, ObjectState> Scene( params ObjectState[] states )
    {
        Dictionary<string, ObjectState> scene = new Dictionary<string, ObjectState>();
        foreach ( ObjectState state in states )
        {
            scene[state.Id] = state;
        }
        return scene;
    }

    public static void Run( Action<bool, string> assert )
    {
        DateTime t0 = new DateTime( 2026, 10, 3, 12, 0, 0, DateTimeKind.Utc );

        // ---- Decision: never Fail; resync, then isolate, then pause.
        ApplyRecovery first = new ApplyRecovery();
        assert( first.Failed( t0, snapshot: false ) == ApplyRecoveryDecision.Resync && first.Resyncing && !first.Paused,
            "recover_first_apply_error_resyncs_instead_of_failing" );
        assert( first.HoldsOutgoing, "recover_resyncing_scene_holds_outgoing_edits" );
        assert( first.Failed( t0.AddSeconds( 1 ), snapshot: true ) == ApplyRecoveryDecision.SkipObjects,
            "recover_fresh_snapshot_that_fails_again_is_applied_object_by_object" );
        first.Succeeded();
        assert( !first.Resyncing && !first.HoldsOutgoing, "recover_success_releases_held_edits" );

        ApplyRecovery drift = new ApplyRecovery();
        assert( drift.Failed( t0, snapshot: true ) == ApplyRecoveryDecision.Resync,
            "recover_unrequested_snapshot_failure_resyncs_first_rather_than_skipping" );

        // ---- Rate limit: at most three resyncs per scene in any 60 seconds, then the scene pauses.
        ApplyRecovery limited = new ApplyRecovery();
        for ( int i = 0; i < 3; i++ )
        {
            assert( limited.Failed( t0.AddSeconds( i * 10 ), snapshot: false ) == ApplyRecoveryDecision.Resync, "recover_resync_" + ( i + 1 ) + "_of_3_allowed" );
            limited.Succeeded();
        }
        assert( limited.Failed( t0.AddSeconds( 30 ), snapshot: false ) == ApplyRecoveryDecision.Pause && limited.Paused,
            "recover_fourth_failure_within_60_seconds_pauses_the_scene" );
        assert( limited.HoldsOutgoing && !limited.AppliesIncoming, "recover_paused_scene_sends_nothing_and_applies_nothing" );
        assert( limited.Failed( t0.AddSeconds( 500 ), snapshot: false ) == ApplyRecoveryDecision.Pause,
            "recover_pause_is_latched_until_the_scene_is_reopened" );
        limited.Reopen();
        assert( !limited.Paused && limited.Failed( t0.AddSeconds( 501 ), snapshot: false ) == ApplyRecoveryDecision.Resync,
            "recover_reopening_the_scene_clears_the_pause" );

        ApplyRecovery sliding = new ApplyRecovery();
        sliding.Failed( t0, false ); sliding.Succeeded();
        sliding.Failed( t0.AddSeconds( 10 ), false ); sliding.Succeeded();
        sliding.Failed( t0.AddSeconds( 20 ), false ); sliding.Succeeded();
        assert( sliding.Failed( t0.AddSeconds( 61 ), false ) == ApplyRecoveryDecision.Resync,
            "recover_window_slides_a_resync_older_than_60_seconds_no_longer_counts" );
        sliding.Succeeded();
        assert( sliding.Failed( t0.AddSeconds( 62 ), false ) == ApplyRecoveryDecision.Pause,
            "recover_window_still_counts_the_three_most_recent_resyncs" );

        ApplyRecovery skipping = new ApplyRecovery();
        skipping.Failed( t0, false );
        for ( int i = 0; i < 5; i++ )
        {
            assert( skipping.Failed( t0.AddSeconds( 1 + i ), true ) == ApplyRecoveryDecision.SkipObjects, "recover_isolation_does_not_consume_the_resync_budget_" + i );
        }

        // ---- The skip set never becomes an outgoing deletion or edit (simulated scan: capture, preserve, diff).
        Dictionary<string, ObjectState> known = Scene( State( "a", "", "Broken" ), State( "b", "a", "Child of broken" ), State( "c", "", "Fine" ) );
        HashSet<string> skipped = new HashSet<string> { "a", "b" };
        Dictionary<string, ObjectState> liveMissing = Scene( State( "c", "", "Fine, edited" ) );
        List<Change> scan = StateRules.Diff( known, ApplyRecovery.PreserveSkipped( known, liveMissing, skipped ) );
        assert( scan.Count == 1 && scan[0].Id == "c", "recover_skipped_objects_absent_locally_are_never_sent_as_deletions" );
        Dictionary<string, ObjectState> liveStale = Scene( State( "a", "", "Old local copy" ), State( "b", "a", "Child of broken" ), State( "c", "", "Fine" ) );
        assert( StateRules.Diff( known, ApplyRecovery.PreserveSkipped( known, liveStale, skipped ) ).Count == 0,
            "recover_a_stale_local_copy_of_a_skipped_object_is_never_sent_as_this_editors_change" );

        // ---- Per-object isolation with an injected apply that throws for one object.
        Dictionary<string, ObjectState> target = Scene( State( "p", "", "Parent" ), State( "q", "p", "Child" ), State( "r", "", "Unusual" ), State( "s", "r", "Under unusual" ) );
        Dictionary<string, ObjectState> live = Scene( State( "p", "", "Parent (old)" ), State( "x", "", "Deleted by host" ) );
        int applyCalls = 0;
        Func<Dictionary<string, ObjectState>, Exception> engine = candidate =>
        {
            applyCalls++;
            StateRules.ValidateGraph( candidate );
            return candidate.ContainsKey( "r" ) ? new InvalidOperationException( "Component type Foo threw in OnValidate\n   at Sandbox.Something" ) : null;
        };
        Dictionary<string, string> reasons = new Dictionary<string, string>();
        Dictionary<string, ObjectState> shown = ApplyRecovery.IsolateObjects( target, live, engine, reasons );
        assert( shown.ContainsKey( "p" ) && StateRules.Equal( shown["p"], target["p"] ) && StateRules.Equal( shown["q"], target["q"] ),
            "recover_isolation_shows_every_object_that_applies" );
        assert( !shown.ContainsKey( "r" ) && !shown.ContainsKey( "s" ) && reasons.ContainsKey( "r" ) && reasons.ContainsKey( "s" ),
            "recover_isolation_skips_the_failing_object_and_its_descendants" );
        assert( reasons["r"] == "Component type Foo threw in OnValidate", "recover_isolation_reports_the_short_engine_reason" );
        assert( !shown.ContainsKey( "x" ), "recover_isolation_removes_objects_the_host_deleted" );
        assert( applyCalls < 12, "recover_isolation_bisects_instead_of_one_apply_per_object (" + applyCalls + " calls)" );

        Dictionary<string, ObjectState> liveWithOld = Scene( State( "r", "", "Unusual (old)" ) );
        Func<Dictionary<string, ObjectState>, Exception> rejectsNewR = candidate =>
            candidate.TryGetValue( "r", out ObjectState r ) && r.Data["Name"]!.GetValue<string>() == "Unusual" ? new InvalidOperationException( "bad" ) : null;
        Dictionary<string, string> reasonsOld = new Dictionary<string, string>();
        Dictionary<string, ObjectState> shownOld = ApplyRecovery.IsolateObjects( target, liveWithOld, rejectsNewR, reasonsOld );
        assert( shownOld.ContainsKey( "r" ) && shownOld["r"].Data["Name"]!.GetValue<string>() == "Unusual (old)" && reasonsOld.ContainsKey( "r" ),
            "recover_isolation_keeps_the_local_version_of_an_object_whose_new_version_fails" );
        assert( shownOld.ContainsKey( "s" ) && !reasonsOld.ContainsKey( "s" ), "recover_isolation_still_shows_children_of_a_kept_local_version" );

        assert( ApplyRecovery.ShortReason( null ) == "unknown engine failure" && ApplyRecovery.ShortReason( new string( 'x', 300 ) ).Length == 120,
            "recover_short_reason_is_bounded" );
        assert( ApplyRecovery.SkippedNotice( "Lamp", "Missing parent" ) == "\"Lamp\" could not be shown in your editor (Missing parent). It is kept unchanged for everyone else.",
            "recover_skipped_notice_wording" );
        assert( ApplyRecovery.PausedNotice( "scenes/main.scene" ) == "Synchronization of \"scenes/main.scene\" is paused because it repeatedly failed to apply. Your changes to this scene are kept locally. Reopen the scene to try again.",
            "recover_paused_notice_wording" );

        // ---- Held edits are replayed on top of the fresh snapshot, in the order the scene holds them.
        Dictionary<string, ObjectState> heldBase = Scene( State( "a", "", "A" ), State( "gone", "", "Gone" ), State( "r", "", "R" ) );
        Dictionary<string, ObjectState> held = Scene( State( "a", "", "Mine" ), State( "n", "", "New" ), State( "n2", "n", "New child" ), State( "gone", "", "Gone, edited" ), State( "r", "", "R, edited" ) );
        Dictionary<string, ObjectState> fresh = Scene( State( "a", "", "A", enabled: false ), State( "b", "", "Host's" ), State( "r", "", "R, host" ) );
        Dictionary<string, ObjectState> freshShown = Scene( State( "a", "", "A", enabled: false ), State( "b", "", "Host's" ), State( "r", "", "R (old)" ) );
        List<string> notices = new List<string>();
        Dictionary<string, ObjectState> final = ApplyRecovery.ReplayHeld( heldBase, held, fresh, freshShown, new HashSet<string> { "r" }, notices );
        assert( final["a"].Data["Name"]!.GetValue<string>() == "Mine" && final["a"].Data["Enabled"]!.GetValue<bool>() == false,
            "recover_held_edit_is_merged_onto_the_fresh_snapshot" );
        assert( final.ContainsKey( "n" ) && final.ContainsKey( "n2" ) && final.ContainsKey( "b" ), "recover_held_creations_are_not_dropped" );
        StateRules.ValidateGraph( final );
        List<string> outgoing = StateRules.Diff( fresh, ApplyRecovery.PreserveSkipped( fresh, new Dictionary<string, ObjectState>( final ), new HashSet<string> { "r" } ) ).Select( x => x.Id ).ToList();
        assert( string.Join( ",", outgoing ) == "a,n,n2", "recover_held_edits_replay_in_scene_order_and_nothing_else_is_sent (" + string.Join( ",", outgoing ) + ")" );
        assert( !final.ContainsKey( "gone" ) && notices.Any( x => x != null && x.Contains( "Gone" ) ), "recover_held_edit_to_an_object_the_host_deleted_is_discarded_with_a_notice" );
        assert( final["r"].Data["Name"]!.GetValue<string>() == "R (old)", "recover_held_edit_to_a_skipped_object_is_never_replayed" );

        // ---- Bookkeeping helpers used by the editor glue.
        Dictionary<string, ObjectState> current = Scene( State( "a", "", "After" ), State( "new", "", "Created" ) );
        Dictionary<string, ObjectState> restored = ApplyRecovery.WithPrior( current, new Dictionary<string, ObjectState> { ["a"] = State( "a", "", "Before" ), ["new"] = null } );
        assert( restored["a"].Data["Name"]!.GetValue<string>() == "Before" && !restored.ContainsKey( "new" ) && current.ContainsKey( "new" ),
            "recover_with_prior_rebuilds_the_pre_change_state_without_touching_the_input" );
        Dictionary<string, ObjectState> recorded = Scene( State( "a", "", "A" ), State( "d", "", "D" ) );
        ApplyRecovery.Record( recorded, new List<Change> { new Change( "a", null, State( "a", "", "A2" ) ), new Change( "d", null, null ), new Change( "e", null, State( "e", "", "E" ) ) } );
        assert( recorded["a"].Data["Name"]!.GetValue<string>() == "A2" && !recorded.ContainsKey( "d" ) && recorded.ContainsKey( "e" ),
            "recover_paused_scene_still_records_what_the_host_accepted" );

        // ---- Settings resync backup: a pending settings change alone requires the backup (Scene.Serialize carries "Properties").
        assert( ApplyRecovery.NeedsBackup( unsentObjects: false, unsentSettings: true ), "recover_unsent_settings_alone_require_a_backup_before_resync" );
        assert( ApplyRecovery.NeedsBackup( true, false ) && !ApplyRecovery.NeedsBackup( false, false ), "recover_backup_only_when_something_unsent_would_be_replaced" );
    }
}
