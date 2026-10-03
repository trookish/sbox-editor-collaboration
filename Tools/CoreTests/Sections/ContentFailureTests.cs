using TeamCreate;
using System.Text.Json.Nodes;

// Content failures outside the apply itself (ContentFailureRules): a packet for a scene this editor no longer has open, a
// connection completing while the game is playing, the play-mode buffer overflowing, and a scene file changed on disk.
static class ContentFailureTests
{
    public static void Run( Action<bool, string> assert )
    {
        // ---- Routing: only a malformed scene key is a protocol failure.
        assert( ContentFailureRules.ClassifyRouteFailure( sceneKeyValid: false, tracked: true, targetOpen: true ) == RouteFailureAction.Fail,
            "heal_route_malformed_scene_key_stays_fail_closed" );
        assert( ContentFailureRules.ClassifyRouteFailure( true, tracked: false, targetOpen: false ) == RouteFailureAction.IgnoreClosed,
            "heal_route_packet_for_an_untracked_scene_is_ignored" );
        assert( ContentFailureRules.ClassifyRouteFailure( true, tracked: true, targetOpen: false ) == RouteFailureAction.IgnoreClosed,
            "heal_route_packet_for_a_closed_scene_is_ignored" );
        assert( ContentFailureRules.ClassifyRouteFailure( true, tracked: true, targetOpen: true ) == RouteFailureAction.ResyncWhenRoutable,
            "heal_route_open_scene_that_cannot_take_the_packet_now_is_resynchronized_later" );

        // ---- Promotion: playing never ends a session that is still connecting.
        assert( ContentFailureRules.MayPromoteConnection( PlaySpectateDecision.PausedForPlay, canPromoteConnection: true ),
            "heal_connection_completes_while_the_game_is_playing" );
        assert( ContentFailureRules.MayPromoteConnection( PlaySpectateDecision.NoScene, true ) && ContentFailureRules.MayPromoteConnection( PlaySpectateDecision.Normal, true ),
            "heal_connection_completes_with_no_scene_or_normally" );
        assert( !ContentFailureRules.MayPromoteConnection( PlaySpectateDecision.FailStop, true ), "heal_interrupted_runtime_still_refuses_promotion" );
        assert( !ContentFailureRules.MayPromoteConnection( PlaySpectateDecision.Normal, canPromoteConnection: false ), "heal_teardown_in_progress_still_refuses_promotion" );

        // ---- Held-packet overflow: discard, remember every affected scene, resync after play.
        HeldPacketBuffer buffer = new HeldPacketBuffer();
        for ( int i = 0; i < HeldPacketBuffer.Capacity; i++ )
        {
            buffer.TryHold( new Packet { Kind = "accepted", Scene = i % 2 == 0 ? "scenes/a.scene" : "scenes/b.scene" } );
        }
        Packet overflowing = new Packet { Kind = "accepted", Scene = "scenes/c.scene" };
        assert( !buffer.TryHold( overflowing ), "heal_overflow_precondition_buffer_is_full" );
        HashSet<string> affected = ContentFailureRules.DrainOverflow( buffer, overflowing );
        assert( buffer.IsEmpty, "heal_overflow_discards_the_held_packets" );
        assert( affected.SetEquals( new[] { "scenes/a.scene", "scenes/b.scene", "scenes/c.scene" } ), "heal_overflow_resyncs_every_affected_scene" );
        assert( ContentFailureRules.DrainOverflow( new HeldPacketBuffer(), new Packet { Kind = "accepted" } ).Count == 0, "heal_overflow_ignores_a_packet_without_a_scene" );

        // ---- Disk stamp: the live scene wins; never a failure.
        assert( ContentFailureRules.OnDiskStampMismatch( flagged: false, backupWritten: false ) == DiskStampAction.None, "heal_disk_stamp_unflagged_does_nothing" );
        assert( ContentFailureRules.OnDiskStampMismatch( true, backupWritten: true ) == DiskStampAction.KeepLiveScene, "heal_disk_stamp_change_keeps_the_live_scene_after_a_backup" );
        assert( ContentFailureRules.OnDiskStampMismatch( true, backupWritten: false ) == DiskStampAction.PauseScene, "heal_disk_stamp_without_a_backup_pauses_only_that_scene" );
    }
}
