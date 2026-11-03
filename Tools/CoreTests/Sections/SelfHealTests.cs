using TeamCreate;

// Small faults that used to cost a session or an edit now cost nothing: a project with no or two project files, a file another program
// holds open, a send that fails mid-undo and a save the session made itself are each handled where they happen.
static class SelfHealTests
{
    private static string NewRoot( string name )
    {
        string root = Path.Combine( Environment.GetEnvironmentVariable( "TMPDIR" ) is { Length: > 0 } t ? t : Path.GetTempPath(), "teamcreate-coretests", "selfheal-" + name + "-" + Guid.NewGuid().ToString( "N" ) );
        Directory.CreateDirectory( root );
        return root;
    }

    private static void Write( string root, string path, string text )
    {
        string full = Path.Combine( root, path.Replace( '/', Path.DirectorySeparatorChar ) );
        Directory.CreateDirectory( Path.GetDirectoryName( full ) );
        File.WriteAllText( full, text );
    }

    public static void Run( Action<bool, string> assert )
    {
        ProjectFiles( assert );
        LockedFiles( assert );
        LockedScan( assert );
        UndoCommit( assert );
        OwnSave( assert );
    }

    // B4: the project file is found by a rule, not by assuming there is exactly one.
    private static void ProjectFiles( Action<bool, string> assert )
    {
        string root = NewRoot( "sbproj" );
        try
        {
            assert( ProjectFile.Find( root ) == null, "heal_a_folder_without_a_project_file_has_none" );
            bool threw = false;
            string message = null;
            try { ProjectFile.Require( root ); }
            catch ( InvalidOperationException ex ) { threw = true; message = ex.Message; }
            assert( threw && message != null && message.Contains( ".sbproj" ) && message.Contains( root ), "heal_requiring_a_missing_project_file_says_which_folder: " + message );

            Write( root, "b.sbproj", "{}" );
            assert( Path.GetFileName( ProjectFile.Find( root ) ) == "b.sbproj", "heal_a_single_project_file_is_found" );
            Write( root, "a.sbproj", "{}" );
            assert( Path.GetFileName( ProjectFile.Find( root ) ) == "a.sbproj" && Path.GetFileName( ProjectFile.Require( root ) ) == "a.sbproj",
                "heal_with_two_project_files_the_first_in_ordinal_order_is_used_instead_of_failing" );
            assert( ProjectFile.Find( Path.Combine( root, "missing" ) ) == null, "heal_a_folder_that_does_not_exist_has_no_project_file" );
        }
        finally
        {
            try { Directory.Delete( root, true ); } catch ( IOException ) { }
        }
    }

    // B5: a file another program holds open is skipped for now, never mistaken for a deleted one, and read once it is released.
    private static void LockedFiles( Action<bool, string> assert )
    {
        string root = NewRoot( "locked" );
        try
        {
            Write( root, "Assets/held.txt", "held" );
            var index = new FileSyncIndex( root );
            FileIndexEntry entry;
            using ( new FileStream( Path.Combine( root, "Assets", "held.txt" ), FileMode.Open, FileAccess.ReadWrite, FileShare.None ) )
            {
                entry = index.Entry( "Assets/held.txt" );
                assert( entry == null && index.IsLocked( "Assets/held.txt" ), "heal_a_file_held_open_by_another_program_is_reported_as_locked_not_thrown" );
            }
            entry = index.Entry( "Assets/held.txt" );
            assert( entry != null && entry.Size == 4 && !index.IsLocked( "Assets/held.txt" ), "heal_a_released_file_is_read_on_the_next_try" );
            File.Delete( Path.Combine( root, "Assets", "held.txt" ) );
            assert( index.Entry( "Assets/held.txt" ) == null && !index.IsLocked( "Assets/held.txt" ), "heal_a_missing_file_is_not_locked" );
        }
        finally
        {
            try { Directory.Delete( root, true ); } catch ( IOException ) { }
        }
    }

    private static void LockedScan( Action<bool, string> assert )
    {
        string root = NewRoot( "scan" );
        try
        {
            Write( root, "Assets/keep.txt", "first" );
            Write( root, "Assets/other.txt", "other" );
            var sent = new List<Packet>();
            var client = new FileSyncClient( root ) { PollInterval = TimeSpan.Zero };
            client.Begin( sent.Add );
            client.Handle( new Packet { Kind = "files-index", Final = true, FileEntries = new List<FileIndexEntry>() }, sent.Add, Array.Empty<string>() );
            assert( client.JoinReady, "heal_scan_setup_the_join_is_ready" );
            for ( int i = 0; i < 3; i++ ) client.Poll( sent.Add, Array.Empty<string>() );
            sent.Clear();
            while ( client.TakeNotice() != null ) { }

            var notices = new List<string>();
            // The program holding the file has just changed it (an unchanged file is answered from the cache and needs no read).
            using ( var held = new FileStream( Path.Combine( root, "Assets", "keep.txt" ), FileMode.Open, FileAccess.ReadWrite, FileShare.None ) )
            {
                held.SetLength( 0 );
                held.Write( "being written"u8 );
                held.Flush();
                File.SetLastWriteTimeUtc( Path.Combine( root, "Assets", "keep.txt" ), DateTime.UtcNow.AddMinutes( 1 ) );
                Write( root, "Assets/other.txt", "other, changed" );
                for ( int i = 0; i < 6; i++ )
                {
                    client.Poll( sent.Add, Array.Empty<string>() );
                    for ( string n; ( n = client.TakeNotice() ) != null; ) notices.Add( n );
                }
            }
            assert( !sent.Any( p => p.Kind == "file-delete" ), "heal_a_locked_file_is_never_reported_deleted" );
            assert( sent.Any( p => p.Kind == "file-put" && p.Path == "Assets/other.txt" ), "heal_a_locked_file_does_not_hold_up_the_other_files" );
            var lockedNotices = notices.Where( n => n.Contains( "Assets/keep.txt" ) ).ToList();
            assert( lockedNotices.Count == 1 && lockedNotices[0].Contains( "another program" ), "heal_a_locked_file_is_reported_once_while_it_stays_locked: " + string.Join( " | ", notices ) );

            File.WriteAllText( Path.Combine( root, "Assets", "keep.txt" ), "second" );
            for ( int i = 0; i < 6; i++ ) client.Poll( sent.Add, Array.Empty<string>() );
            assert( sent.Any( p => p.Kind == "file-put" && p.Path == "Assets/keep.txt" ), "heal_a_released_file_is_uploaded_by_a_later_poll" );
            client.Dispose();
        }
        finally
        {
            try { Directory.Delete( root, true ); } catch ( IOException ) { }
        }
    }

    // B7: an undo whose edit could not be sent keeps its entry, so the person can undo again once reconnected.
    private static void UndoCommit( Action<bool, string> assert )
    {
        var stack = new Stack<int>( new[] { 1, 2 } );
        bool threw = false;
        try { EditCommit.AfterSend( () => throw new IOException( "closed" ), () => stack.Pop() ); }
        catch ( IOException ) { threw = true; }
        assert( threw && stack.Count == 2 && stack.Peek() == 2, "heal_an_undo_whose_send_fails_keeps_its_entry_and_reports_the_failure" );
        EditCommit.AfterSend( () => { }, () => stack.Pop() );
        assert( stack.Count == 1 && stack.Peek() == 1, "heal_an_undo_that_was_sent_pops_its_entry" );
        bool rolled = false;
        try { EditCommit.AfterSend( () => throw new InvalidOperationException( "full" ), () => stack.Pop(), () => rolled = true ); }
        catch ( InvalidOperationException ) { }
        assert( rolled && stack.Count == 1, "heal_a_failed_send_runs_the_rollback_and_never_the_commit" );
    }

    // B8: a save is the session's own only when the file is the one it wrote; a different file under the same time is someone else's.
    private static void OwnSave( Action<bool, string> assert )
    {
        var stamp = new DateTime( 2026, 11, 3, 10, 0, 0, DateTimeKind.Utc );
        assert( SceneSaveRules.IsOwnSave( true, stamp, stamp.AddSeconds( 5 ), "aa", "bb" ), "heal_a_save_while_the_session_is_saving_is_its_own" );
        assert( SceneSaveRules.IsOwnSave( false, stamp, stamp, "aa", "AA" ), "heal_the_same_time_and_content_is_the_sessions_own_save" );
        assert( !SceneSaveRules.IsOwnSave( false, stamp, stamp, "aa", "bb" ), "heal_the_same_time_with_different_content_is_not_the_sessions_save" );
        assert( !SceneSaveRules.IsOwnSave( false, stamp, stamp.AddSeconds( 1 ), "aa", "aa" ), "heal_a_new_time_is_not_the_sessions_save" );
        assert( SceneSaveRules.IsOwnSave( false, stamp, stamp, null, "bb" ) && SceneSaveRules.IsOwnSave( false, stamp, stamp, "aa", null ),
            "heal_without_a_digest_on_either_side_the_time_alone_decides" );
        assert( SceneSaveRules.KeepsDiskCopy( SceneSaveOutcome.Warn ) && !SceneSaveRules.KeepsDiskCopy( SceneSaveOutcome.Absorb ) && !SceneSaveRules.KeepsDiskCopy( SceneSaveOutcome.Ignore ),
            "heal_a_save_that_may_differ_from_the_session_is_copied_to_recovery_before_it_is_accepted" );
    }
}
