using TeamCreate;

/// <summary>
/// Who is playing: the host-ordered roster of at most one run. Every transition the host can see is covered, including the ones that are only
/// reachable through a disconnect or a reconnect.
/// </summary>
static partial class SharedPlayTests
{
    const string PlayScene = "scenes/level.scene";

    static int runCounter;

    static string NextRunId() => "run-" + ( ++runCounter );

    static void Roster( Action<bool, string> assert )
    {
        var roster = new PlayRoster();
        assert( !roster.IsPlaying && roster.Player == null && roster.RunId == null, "play_roster_starts_idle" );

        var first = roster.TryStart( "peer-a", "Alpha", PlayScene, NextRunId );
        assert( first.Verdict == PlayStartVerdict.Started && first.RunId != null && roster.IsPlaying && roster.Player == "peer-a" && roster.Scene == PlayScene && roster.PlayerName == "Alpha", "play_roster_first_start_is_accepted" );

        // The single-player rule: a second person pressing Play while someone plays is refused, with the formal notice, and nothing changes.
        var runBefore = roster.RunId;
        var second = roster.TryStart( "peer-b", "Bravo", PlayScene, NextRunId );
        assert( second.Verdict == PlayStartVerdict.Refused, "play_roster_second_player_is_refused" );
        assert( second.Notice == "Alpha is already playing. Wait until they stop, or ask them to stop. Your run is local and is not shown to the others.", "play_roster_refusal_notice_is_exact: " + second.Notice );
        assert( roster.Player == "peer-a" && roster.RunId == runBefore, "play_roster_refusal_changes_nothing" );

        // A stop that does not come from the player, or names another run, is ignored: it must not end somebody else's run.
        assert( !roster.Stop( "peer-b", roster.RunId ), "play_roster_stop_from_non_player_is_ignored" );
        assert( !roster.Stop( "peer-a", "some-older-run" ) && roster.IsPlaying, "play_roster_stop_with_stale_run_is_ignored" );
        assert( roster.IsPlayer( "peer-a", runBefore ) && !roster.IsPlayer( "peer-b", runBefore ) && !roster.IsPlayer( "peer-a", "other" ), "play_roster_is_player_checks_peer_and_run" );

        assert( roster.Stop( "peer-a", runBefore ) && !roster.IsPlaying && roster.Player == null, "play_roster_player_stop_ends_the_run" );
        assert( !roster.Stop( "peer-a", runBefore ), "play_roster_second_stop_is_a_no_op" );

        // After a stop the next person may play: the refusal lasted only as long as the run.
        var third = roster.TryStart( "peer-b", "Bravo", PlayScene, NextRunId );
        assert( third.Verdict == PlayStartVerdict.Started && roster.Player == "peer-b", "play_roster_next_player_after_stop" );

        // A guest who is the player leaves: the run ends for everyone. A spectator leaving changes nothing.
        assert( !roster.PeerLeft( "peer-c" ) && roster.IsPlaying, "play_roster_spectator_leaving_keeps_the_run" );
        assert( roster.PeerLeft( "peer-b" ) && !roster.IsPlaying, "play_roster_player_leaving_ends_the_run" );
        assert( !roster.PeerLeft( "peer-b" ), "play_roster_leaving_twice_is_a_no_op" );

        // Reconnect mid-play: the player's connection drops (leave), then the same person asks again and is a new run with a new id.
        var a1 = roster.TryStart( "peer-a", "Alpha", PlayScene, NextRunId );
        roster.PeerLeft( "peer-a" );
        var a2 = roster.TryStart( "peer-a", "Alpha", PlayScene, NextRunId );
        assert( a2.Verdict == PlayStartVerdict.Started && a2.RunId != a1.RunId, "play_roster_reconnecting_player_gets_a_new_run" );

        // The same player asking again while its run is active replaces the run (a restarted local play), never a refusal against itself.
        var a3 = roster.TryStart( "peer-a", "Alpha", PlayScene, NextRunId );
        assert( a3.Verdict == PlayStartVerdict.Started && a3.RunId != a2.RunId && roster.RunId == a3.RunId, "play_roster_same_player_restart_replaces_the_run" );
        assert( !roster.IsPlayer( "peer-a", a2.RunId ), "play_roster_replaced_run_is_no_longer_current" );
        roster.PeerLeft( "peer-a" );

        // The host orders two simultaneous requests: whichever it reads first plays, the other is refused, and the order decides nothing else.
        var racing = new PlayRoster();
        var winner = racing.TryStart( "peer-b", "Bravo", PlayScene, NextRunId );
        var loser = racing.TryStart( "peer-a", "Alpha", PlayScene, NextRunId );
        assert( winner.Verdict == PlayStartVerdict.Started && loser.Verdict == PlayStartVerdict.Refused && racing.Player == "peer-b", "play_roster_race_first_read_wins" );
        assert( loser.Player == "peer-b", "play_roster_refusal_names_the_player" );

        // The host as the player is not special.
        var hosted = new PlayRoster();
        assert( hosted.TryStart( "host-peer", "Host", PlayScene, NextRunId ).Verdict == PlayStartVerdict.Started && hosted.TryStart( "guest", "Guest", PlayScene, NextRunId ).Verdict == PlayStartVerdict.Refused, "play_roster_host_as_player" );

        // Input is untrusted: the scene must be a project scene path (the same rule as a subscribe), the peer must be named, and a name is
        // cleaned before it goes into a sentence shown to people.
        var bad = new PlayRoster();
        assert( bad.TryStart( "peer-a", "Alpha", "../../etc/passwd", NextRunId ).Verdict == PlayStartVerdict.Invalid, "play_roster_refuses_a_path_outside_scenes" );
        assert( bad.TryStart( "peer-a", "Alpha", "scenes/../x.scene", NextRunId ).Verdict == PlayStartVerdict.Invalid, "play_roster_refuses_dot_dot_in_scene" );
        assert( bad.TryStart( "peer-a", "Alpha", "scenes/" + new string( 'x', 400 ), NextRunId ).Verdict == PlayStartVerdict.Invalid, "play_roster_refuses_an_oversized_scene_name" );
        assert( bad.TryStart( "peer-a", "Alpha", null, NextRunId ).Verdict == PlayStartVerdict.Invalid, "play_roster_refuses_a_missing_scene" );
        assert( bad.TryStart( null, "Alpha", PlayScene, NextRunId ).Verdict == PlayStartVerdict.Invalid, "play_roster_refuses_a_missing_peer" );
        assert( !bad.IsPlaying, "play_roster_invalid_start_leaves_it_idle" );
        var named = new PlayRoster();
        named.TryStart( "peer-a", "Al\r\npha\u0007" + new string( 'z', 200 ), PlayScene, NextRunId );
        var refusedNamed = named.TryStart( "peer-b", "B", PlayScene, NextRunId );
        assert( !refusedNamed.Notice.Contains( '\n' ) && !refusedNamed.Notice.Contains( '\r' ) && !refusedNamed.Notice.Contains( '\u0007' ) && refusedNamed.Notice.Length < 300, "play_roster_notice_name_is_cleaned_and_bounded" );
        var anonymous = new PlayRoster();
        anonymous.TryStart( "peer-a", "  ", PlayScene, NextRunId );
        assert( anonymous.TryStart( "peer-b", "B", PlayScene, NextRunId ).Notice.StartsWith( "A collaborator is already playing.", StringComparison.Ordinal ), "play_roster_blank_name_is_a_collaborator" );
    }
}
