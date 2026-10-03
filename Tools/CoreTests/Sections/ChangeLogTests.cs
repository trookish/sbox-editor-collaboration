using System.Text;
using TeamCreate;

/// <summary>The session change log: one row per project file changed during the session, its merge rules, its bound, hostile paths, persistence, wording, and the fence that keeps it away from Git.</summary>
static class ChangeLogTests
{
    static readonly DateTime T0 = new DateTime( 2026, 10, 4, 12, 0, 0, DateTimeKind.Utc );

    static string RepoRoot()
    {
        string root = Directory.GetCurrentDirectory();
        while ( root != null && !File.Exists( Path.Combine( root, "Libraries", "collaboration", "VERSION" ) ) )
            root = Directory.GetParent( root )?.FullName;
        return root ?? throw new DirectoryNotFoundException( "The repository root was not found from " + Directory.GetCurrentDirectory() );
    }

    const string A = "Assets/a.png";

    static SessionChangeKind? KindOf( SessionChangeLog log, string path )
    {
        var row = log.Rows().FirstOrDefault( r => r.Path == path );
        return row?.Kind;
    }

    static SessionChangeKind? Merge( SessionChangeKind first, SessionChangeKind second )
    {
        var log = new SessionChangeLog( () => T0 );
        log.Record( A, first, "Sam" );
        log.Record( A, second, "Lee" );
        return KindOf( log, A );
    }

    public static void Run( Action<bool, string> assert )
    {
        const SessionChangeKind Add = SessionChangeKind.Added, Chg = SessionChangeKind.Changed, Del = SessionChangeKind.Deleted;
        const SessionChangeKind Rec = SessionChangeKind.ReceivedFromHost, Code = SessionChangeKind.CodeReceived;

        // ---- merge rules: every pair (first, then second) ----
        // A file that did not exist at the start of the session stays "Added" however it is touched afterwards, and vanishes when deleted again.
        assert( Merge( Add, Add ) == Add, "changelog_merge_added_then_added_is_added" );
        assert( Merge( Add, Chg ) == Add, "changelog_merge_added_then_changed_stays_added" );
        assert( Merge( Add, Del ) == null, "changelog_merge_added_then_deleted_removes_the_row" );
        assert( Merge( Add, Rec ) == Add, "changelog_merge_added_then_received_stays_added" );
        assert( Merge( Add, Code ) == Add, "changelog_merge_added_then_code_stays_added" );
        assert( Merge( Chg, Add ) == Chg, "changelog_merge_changed_then_added_is_changed" );
        assert( Merge( Chg, Chg ) == Chg, "changelog_merge_changed_then_changed_is_changed" );
        assert( Merge( Chg, Del ) == Del, "changelog_merge_changed_then_deleted_is_deleted" );
        assert( Merge( Chg, Rec ) == Rec, "changelog_merge_changed_then_received_latest_wins" );
        assert( Merge( Chg, Code ) == Code, "changelog_merge_changed_then_code_latest_wins" );
        assert( Merge( Del, Add ) == Chg, "changelog_merge_deleted_then_added_is_changed" );
        assert( Merge( Del, Chg ) == Chg, "changelog_merge_deleted_then_changed_is_changed" );
        assert( Merge( Del, Del ) == Del, "changelog_merge_deleted_then_deleted_is_deleted" );
        assert( Merge( Del, Rec ) == Rec, "changelog_merge_deleted_then_received_latest_wins" );
        assert( Merge( Del, Code ) == Code, "changelog_merge_deleted_then_code_latest_wins" );
        assert( Merge( Rec, Add ) == Add, "changelog_merge_received_then_added_latest_wins" );
        assert( Merge( Rec, Chg ) == Chg, "changelog_merge_received_then_changed_latest_wins" );
        assert( Merge( Rec, Del ) == Del, "changelog_merge_received_then_deleted_is_deleted" );
        assert( Merge( Rec, Rec ) == Rec, "changelog_merge_received_then_received_is_received" );
        assert( Merge( Rec, Code ) == Code, "changelog_merge_received_then_code_latest_wins" );
        assert( Merge( Code, Add ) == Add, "changelog_merge_code_then_added_latest_wins" );
        assert( Merge( Code, Chg ) == Chg, "changelog_merge_code_then_changed_latest_wins" );
        assert( Merge( Code, Del ) == Del, "changelog_merge_code_then_deleted_is_deleted" );
        assert( Merge( Code, Rec ) == Rec, "changelog_merge_code_then_received_latest_wins" );
        assert( Merge( Code, Code ) == Code, "changelog_merge_code_then_code_is_code" );

        // The latest event supplies the peer, the time and the digest; the row count stays one per path.
        {
            var now = T0;
            var log = new SessionChangeLog( () => now );
            log.Record( A, Chg, "Sam", new string( 'a', 64 ) );
            now = T0.AddMinutes( 3 );
            log.Record( A, Chg, "Lee", new string( 'b', 64 ) );
            var row = log.Rows().Single();
            assert( row.By == "Lee" && row.AtUtc == T0.AddMinutes( 3 ) && row.Sha256 == new string( 'b', 64 ), "changelog_latest_event_supplies_peer_time_and_digest" );
            // Added then Deleted leaves nothing, and a counter that is not touched by it.
            log.Record( "Assets/new.png", Add, "Sam" );
            log.Record( "Assets/new.png", Del, "Sam" );
            assert( log.Count == 1 && log.OlderNotShown == 0, "changelog_added_then_deleted_leaves_no_trace_and_no_counter" );
            int revision = log.Revision;
            log.Record( "../x", Chg, "Sam" );
            assert( log.Revision == revision, "changelog_a_refused_path_does_not_change_the_revision" );
            log.Record( A, Chg, "Sam" );
            assert( log.Revision == revision + 1, "changelog_a_recorded_change_advances_the_revision" );
        }

        // ---- the bound ----
        {
            var log = new SessionChangeLog( () => T0 );
            for ( int i = 0; i < SessionChangeLog.MaxEntries; i++ ) log.Record( "Assets/f" + i.ToString( "D5" ) + ".txt", Chg, "Sam" );
            assert( log.Count == SessionChangeLog.MaxEntries && log.OlderNotShown == 0, "changelog_bound_holds_exactly_the_maximum_without_loss" );
            log.Record( "Assets/f00000.txt", Chg, "Lee" ); // an existing row refreshed: not a new row, nothing dropped, and now the newest
            assert( log.Count == SessionChangeLog.MaxEntries && log.OlderNotShown == 0, "changelog_bound_refreshing_a_row_drops_nothing" );
            log.Record( "Assets/extra.txt", Chg, "Sam" );
            assert( log.Count == SessionChangeLog.MaxEntries, "changelog_bound_never_exceeds_the_maximum" );
            assert( log.OlderNotShown == 1, "changelog_bound_counts_the_dropped_row" );
            var paths = log.Rows().Select( r => r.Path ).ToHashSet();
            assert( !paths.Contains( "Assets/f00001.txt" ) && paths.Contains( "Assets/f00000.txt" ) && paths.Contains( "Assets/extra.txt" ), "changelog_bound_drops_the_oldest_untouched_row_first" );
            assert( log.TotalChanged == SessionChangeLog.MaxEntries + 1, "changelog_total_includes_the_older_rows_not_shown" );
        }

        // ---- ordering and display ----
        {
            var log = new SessionChangeLog( () => T0 );
            log.Record( "Assets/b.png", Chg, "Sam" );
            log.Record( "Assets/a.png", Chg, "Sam" );
            log.Record( "Assets/B.png", Chg, "Sam" );
            log.Record( "Assets/Sub/z.png", Chg, "Sam" );
            var order = log.Rows().Select( r => r.Path ).ToList();
            assert( order.SequenceEqual( new[] { "Assets/B.png", "Assets/Sub/z.png", "Assets/a.png", "Assets/b.png" } ), "changelog_rows_are_in_ordinal_path_order: " + string.Join( ",", order ) );
            assert( order.All( p => !p.Contains( '\\' ) && p.StartsWith( "Assets/", StringComparison.Ordinal ) ), "changelog_paths_are_project_relative_with_forward_slashes" );
            assert( SessionChangeLog.Describe( log.Rows()[0] ) == "Changed Assets/B.png (Sam)", "changelog_row_text_is_kind_path_peer: " + SessionChangeLog.Describe( log.Rows()[0] ) );
            var rows = new SessionChangeLog( () => T0 );
            rows.Record( A, Rec, "the host" );
            rows.Record( "Assets/b.png", Code, "the host" );
            assert( SessionChangeLog.Describe( rows.Rows()[0] ) == "Received from host Assets/a.png (the host)", "changelog_received_label" );
            assert( SessionChangeLog.Describe( rows.Rows()[1] ) == "Code received Assets/b.png (the host)", "changelog_code_label" );
            rows.Record( "Assets/c.png", Add, null );
            assert( SessionChangeLog.Describe( rows.Rows()[2] ) == "Added Assets/c.png (unknown)", "changelog_missing_peer_is_unknown" );
        }

        // ---- hostile paths ----
        {
            var log = new SessionChangeLog( () => T0 );
            string[] hostile =
            {
                null, "", "  ", "../Assets/a.png", "Assets/../../x.png", "Assets\\a.png", "C:/Windows/x.png", "/Assets/a.png", "Assets/a.png:stream",
                "Assets/nul.txt", "Assets/a\u0001.png", "Assets//a.png", "Code/a.cs", "Assets/", "Assets", new string( 'a', 300 ),
            };
            foreach ( string path in hostile )
            {
                assert( !log.Record( path, Chg, "Sam" ), "changelog_hostile_path_refused: " + ( path == null ? "null" : path.Length > 40 ? "long" : path.Replace( '\u0001', '?' ) ) );
            }
            assert( log.Count == 0, "changelog_hostile_paths_leave_no_row" );
            // A code transfer names a path outside Assets, with the same traversal rules.
            assert( log.Record( "Code/Player.cs", Code, "Sam" ) && KindOf( log, "Code/Player.cs" ) == Code, "changelog_code_path_outside_assets_is_accepted" );
            assert( !log.Record( "Code/../../x.cs", Code, "Sam" ) && !log.Record( "C:/x.cs", Code, "Sam" ) && !log.Record( "Code\\x.cs", Code, "Sam" ), "changelog_hostile_code_paths_refused" );
            assert( !log.Record( A, (SessionChangeKind)99, "Sam" ), "changelog_unknown_kind_refused" );
        }

        // ---- wording ----
        assert( SessionChangeLog.SummaryLine( 1 ) == "1 file changed. Commit it through Git when you are done.", "changelog_summary_singular: " + SessionChangeLog.SummaryLine( 1 ) );
        assert( SessionChangeLog.SummaryLine( 2 ) == "2 files changed. Commit them through Git when you are done.", "changelog_summary_plural: " + SessionChangeLog.SummaryLine( 2 ) );
        assert( SessionChangeLog.SummaryLine( 0 ) == SessionChangeLog.EmptyText && SessionChangeLog.EmptyText == "No project files have changed in this session.", "changelog_summary_empty" );
        assert( SessionChangeLog.OlderLine( 1 ) == "1 older change is not shown.", "changelog_older_singular" );
        assert( SessionChangeLog.OlderLine( 7 ) == "7 older changes are not shown.", "changelog_older_plural" );
        assert( SessionChangeLog.PreviousLine( 1 ) == "Previous session: 1 file changed." && SessionChangeLog.PreviousLine( 3 ) == "Previous session: 3 files changed.", "changelog_previous_line_wording" );

        // ---- persistence ----
        string dir = Path.Combine( Path.GetTempPath(), "changelog-" + Guid.NewGuid().ToString( "N" ) );
        Directory.CreateDirectory( dir );
        try
        {
            string file = Path.Combine( dir, ".collaboration", "session-changes.json" );
            var now = T0;
            var log = new SessionChangeLog( () => now );
            assert( !log.Flush( dir, false ) && !File.Exists( file ), "changelog_nothing_is_written_while_nothing_changed" );
            var started = new SessionChangeLog( () => now );
            started.RequestWrite();
            assert( started.Flush( dir, false ) && SessionChangeLog.ReadPreviousCount( dir ) == 0, "changelog_a_new_session_replaces_the_old_file_even_when_empty" );
            File.Delete( file );
            log.Record( A, Chg, "Sam", new string( 'c', 64 ) );
            assert( log.Flush( dir, false ) && File.Exists( file ), "changelog_first_change_is_written" );
            assert( SessionChangeLog.ReadPreviousCount( dir ) == 1, "changelog_round_trip_reads_the_count" );
            log.Record( "Assets/b.png", Add, "Lee" );
            now = T0.AddSeconds( 4 );
            assert( !log.Flush( dir, false ) && SessionChangeLog.ReadPreviousCount( dir ) == 1, "changelog_writes_at_most_once_per_five_seconds" );
            now = T0.AddSeconds( 5 );
            assert( log.Flush( dir, false ) && SessionChangeLog.ReadPreviousCount( dir ) == 2, "changelog_writes_again_after_five_seconds" );
            log.Record( "Assets/c.png", Add, "Lee" );
            assert( log.Flush( dir, true ) && SessionChangeLog.ReadPreviousCount( dir ) == 3, "changelog_forced_flush_ignores_the_interval" );
            assert( !log.Flush( dir, true ), "changelog_forced_flush_with_nothing_new_writes_nothing" );
            assert( Directory.GetFiles( Path.GetDirectoryName( file ), "*.tmp" ).Length == 0 && Directory.GetFiles( Path.GetDirectoryName( file ) ).Length == 1, "changelog_atomic_write_leaves_no_temporary_file" );

            // A new session never merges the old file: a fresh log starts empty and loading is a count only.
            var next = new SessionChangeLog( () => now );
            assert( next.Count == 0 && SessionChangeLog.ReadPreviousCount( dir ) == 3, "changelog_previous_session_is_a_count_only_and_never_merged" );

            File.WriteAllText( file, "{ this is not json" );
            assert( SessionChangeLog.ReadPreviousCount( dir ) == null, "changelog_malformed_file_is_ignored" );
            File.WriteAllText( file, "{\"version\":1,\"count\":\"many\",\"entries\":5}" );
            assert( SessionChangeLog.ReadPreviousCount( dir ) == null, "changelog_wrongly_typed_file_is_ignored" );
            File.WriteAllText( file, "{\"version\":1,\"count\":-4,\"entries\":[]}" );
            assert( SessionChangeLog.ReadPreviousCount( dir ) == null, "changelog_negative_count_is_ignored" );
            File.WriteAllText( file, "{\"version\":1,\"count\":2,\"padding\":\"" + new string( 'x', SessionChangeLog.MaxFileBytes ) + "\"}" );
            assert( new FileInfo( file ).Length > SessionChangeLog.MaxFileBytes && SessionChangeLog.ReadPreviousCount( dir ) == null, "changelog_file_over_one_mebibyte_is_ignored" );
            File.Delete( file );
            assert( SessionChangeLog.ReadPreviousCount( dir ) == null, "changelog_missing_file_is_no_previous_session" );

            // A full log still fits inside the bound the reader enforces, so a crash after a busy session is still readable.
            var big = new SessionChangeLog( () => T0 );
            for ( int i = 0; i < SessionChangeLog.MaxEntries; i++ ) big.Record( "Assets/" + new string( 'd', 90 ) + "/" + new string( 'e', 90 ) + i.ToString( "D5" ) + ".txt", Chg, new string( 'p', 40 ), new string( 'f', 64 ) );
            assert( big.Flush( dir, true ) && new FileInfo( file ).Length <= SessionChangeLog.MaxFileBytes, "changelog_a_full_log_is_written_within_the_read_bound" );
            assert( SessionChangeLog.ReadPreviousCount( dir ) == SessionChangeLog.MaxEntries, "changelog_a_full_log_reads_back_its_full_count" );

            // An unwritable location never throws into the editor: the flush reports false.
            string blocker = Path.Combine( dir, "blocker" );
            File.WriteAllText( blocker, "x" );
            var stuck = new SessionChangeLog( () => T0 );
            stuck.Record( A, Chg, "Sam" );
            assert( !stuck.Flush( blocker, true ), "changelog_unwritable_location_reports_false_without_throwing" );
        }
        finally
        {
            try { Directory.Delete( dir, true ); } catch ( IOException ) { }
        }

        // ---- copy text ----
        {
            var log = new SessionChangeLog( () => T0 );
            log.Record( "Assets/b.png", Add, "Lee" );
            log.Record( A, Chg, "Sam" );
            assert( log.CopyText() == "Changed Assets/a.png (Sam)\nAdded Assets/b.png (Lee)", "changelog_copy_text_is_one_row_per_line: " + log.CopyText().Replace( "\n", "|" ) );
            assert( new SessionChangeLog( () => T0 ).CopyText() == "", "changelog_copy_text_of_an_empty_log_is_empty" );
        }

        // ---- the fence: the change log reads and writes files but never runs Git ----
        string root = RepoRoot();
        string[] files =
        {
            Path.Combine( root, "Libraries", "collaboration", "Editor", "Core", "SessionChangeLog.cs" ),
            Path.Combine( root, "Libraries", "collaboration", "Editor", "Dock", "ChangesCard.cs" ),
        };
        foreach ( string path in files )
        {
            assert( File.Exists( path ), "changelog_fence_found_" + Path.GetFileName( path ) );
            string text = File.ReadAllText( path, Encoding.UTF8 );
            foreach ( string token in new[] { "Process.Start", "ProcessStartInfo", "\"git\"", "\"git.exe\"", "git ", "System.Diagnostics" } )
            {
                assert( !text.Contains( token, StringComparison.Ordinal ), "changelog_fence_" + Path.GetFileName( path ) + "_has_no_" + token.Trim( '"' ) );
            }
        }
    }
}
