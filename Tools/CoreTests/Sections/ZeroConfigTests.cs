using System.Text.Json.Nodes;
using TeamCreate;

// A new project needs nothing written by hand: the settings file is optional, both computers agree on the project id from the project's
// own ident, the session folder is kept out of Git, the dock opens itself once and the invite is on the clipboard when hosting starts.
static class ZeroConfigTests
{
    private static string NewRoot( string name )
    {
        string root = Path.Combine( Environment.GetEnvironmentVariable( "TMPDIR" ) is { Length: > 0 } t ? t : Path.GetTempPath(), "teamcreate-coretests", "zeroconfig-" + name + "-" + Guid.NewGuid().ToString( "N" ) );
        Directory.CreateDirectory( root );
        return root;
    }

    public static void Run( Action<bool, string> assert )
    {
        SettingsFile( assert );
        ProjectIdRule( assert );
        GitIgnore( assert );
        GitIgnoreFile( assert );
        FirstRun( assert );
        InviteCopy( assert );
        Wording( assert );
    }

    // C1: a missing file is the defaults with every gate on; the file written on first host carries only what it must.
    private static void SettingsFile( Action<bool, string> assert )
    {
        string root = NewRoot( "settings" );
        try
        {
            string path = Path.Combine( root, "ProjectSettings", "collaboration.json" );
            var missing = CollaborationConfig.LoadFrom( path, out string missingError );
            assert( missingError == null && missing.RequireSteamFriend && missing.RequireOrgMember && missing.RequirePublished && missing.ProjectId == null,
                "zero_a_missing_settings_file_is_the_defaults_with_every_gate_on" );

            var made = CollaborationSetup.EnsureConfig( path, "myorg", "myorg.sbox_editor_collab", "1.2.0" );
            var written = JsonNode.Parse( File.ReadAllText( path ) ) as JsonObject;
            assert( made.Created && written != null && written.ContainsKey( "ProjectId" ) && !written.ContainsKey( "RequireSteamFriend" )
                && !written.ContainsKey( "RequireOrgMember" ) && !written.ContainsKey( "RequirePublished" ),
                "zero_the_file_written_on_first_host_never_carries_the_safety_flags: " + written?.ToJsonString() );
            var loaded = CollaborationConfig.LoadFrom( path, out string loadedError );
            assert( loadedError == null && loaded.RequireSteamFriend && loaded.RequireOrgMember && loaded.RequirePublished && loaded.Org == "myorg",
                "zero_a_written_file_still_loads_with_every_gate_on" );

            File.WriteAllText( path, "{ \"RequireOrgMember\": false, \"Org\": \"keep\" }" );
            var partial = CollaborationConfig.LoadFrom( path, out string partialError );
            assert( partialError == null && !partial.RequireOrgMember && partial.RequireSteamFriend && partial.RequirePublished && partial.Org == "keep",
                "zero_a_partial_file_keeps_its_explicit_values_and_defaults_the_rest" );
            CollaborationSetup.EnsureConfig( path, "other", null, "9.9.9" );
            var kept = CollaborationConfig.LoadFrom( path, out _ );
            assert( !kept.RequireOrgMember && kept.Org == "keep" && Guid.TryParse( kept.ProjectId, out _ ), "zero_filling_in_a_project_id_keeps_every_explicit_value" );

            string published = Path.Combine( root, "published", "collaboration.json" );
            var skipped = CollaborationSetup.EnsureConfig( published, "myorg", null, "1.2.0", projectIdent: "myorg.mygame" );
            assert( !skipped.Created && skipped.Error == null && !File.Exists( published ),
                "zero_a_project_with_its_own_ident_needs_no_settings_file" );
        }
        finally
        {
            try { Directory.Delete( root, true ); } catch ( IOException ) { }
        }
    }

    // C2: the project id is the configured one, else one derived from the project's ident, the same on every computer.
    private static void ProjectIdRule( Action<bool, string> assert )
    {
        string a = ProjectIdentity.Resolve( null, "myorg.mygame" );
        string b = ProjectIdentity.Resolve( "", "MyOrg.MyGame" );
        assert( a != null && Guid.TryParse( a, out _ ) && a == b, "zero_the_same_ident_gives_the_same_project_id_on_every_computer: " + a + " " + b );
        assert( ProjectIdentity.Resolve( null, "myorg.othergame" ) != a, "zero_a_different_ident_gives_a_different_project_id" );
        assert( ProjectIdentity.Resolve( "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f", "myorg.mygame" ) == "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f",
            "zero_a_configured_project_id_wins_over_the_ident" );
        assert( ProjectIdentity.Resolve( null, null ) == null && ProjectIdentity.Resolve( null, "  " ) == null, "zero_no_id_and_no_ident_gives_no_project_id" );
        assert( ProjectIdentity.Resolve( null, " myorg.mygame " ) == a, "zero_surrounding_spaces_do_not_change_the_project_id" );
    }

    // C3: .collaboration/ is added once to an existing .gitignore, in the file's own line endings.
    private static void GitIgnore( Action<bool, string> assert )
    {
        var lf = GitIgnoreRules.Apply( "bin/\nobj/\n" );
        assert( lf.Changed && lf.Text == "bin/\nobj/\n.collaboration/\n", "zero_gitignore_an_lf_file_gets_the_rule_in_lf: " + lf.Text );
        var crlf = GitIgnoreRules.Apply( "bin/\r\nobj/\r\n" );
        assert( crlf.Changed && crlf.Text == "bin/\r\nobj/\r\n.collaboration/\r\n", "zero_gitignore_a_crlf_file_gets_the_rule_in_crlf" );
        var noNewline = GitIgnoreRules.Apply( "bin/" );
        assert( noNewline.Changed && noNewline.Text == "bin/\n.collaboration/\n", "zero_gitignore_a_file_without_a_last_newline_gets_one_before_the_rule" );
        var empty = GitIgnoreRules.Apply( "" );
        assert( empty.Changed && empty.Text == ".collaboration/\n", "zero_gitignore_an_empty_file_gets_just_the_rule" );
        foreach ( string rule in new[] { ".collaboration", ".collaboration/", "/.collaboration", "/.collaboration/", ".collaboration/**", "/.collaboration/**", "  .collaboration/  " } )
        {
            var same = GitIgnoreRules.Apply( "bin/\n" + rule + "\n" );
            assert( !same.Changed && same.Text == "bin/\n" + rule + "\n", "zero_gitignore_an_equivalent_rule_is_left_alone: " + rule );
        }
        var comment = GitIgnoreRules.Apply( "# .collaboration/\n" );
        assert( comment.Changed && comment.Text == "# .collaboration/\n.collaboration/\n", "zero_gitignore_a_commented_out_rule_does_not_count" );
        var nested = GitIgnoreRules.Apply( "Assets/.collaboration/\n" );
        assert( nested.Changed, "zero_gitignore_a_rule_for_a_nested_folder_does_not_count" );
        var twice = GitIgnoreRules.Apply( GitIgnoreRules.Apply( "bin/\r\n" ).Text );
        assert( !twice.Changed, "zero_gitignore_applying_twice_changes_nothing_the_second_time" );
        assert( GitIgnoreRules.Notice == "Added .collaboration/ to .gitignore.", "zero_gitignore_notice_wording" );
    }

    private static void GitIgnoreFile( Action<bool, string> assert )
    {
        string root = NewRoot( "gitignore" );
        try
        {
            var none = GitIgnoreRules.EnsureIgnored( root );
            assert( !none.Changed && none.Error == null && !File.Exists( Path.Combine( root, ".gitignore" ) ), "zero_gitignore_a_project_without_one_is_left_without_one" );
            string file = Path.Combine( root, ".gitignore" );
            File.WriteAllBytes( file, "bin/\r\n"u8.ToArray() );
            var first = GitIgnoreRules.EnsureIgnored( root );
            var second = GitIgnoreRules.EnsureIgnored( root );
            assert( first.Changed && first.Error == null && !second.Changed && File.ReadAllText( file ) == "bin/\r\n.collaboration/\r\n",
                "zero_gitignore_the_file_is_updated_once: " + File.ReadAllText( file ).Replace( "\r", "\\r" ).Replace( "\n", "\\n" ) );
            assert( !File.Exists( file + ".tmp" ), "zero_gitignore_no_temporary_file_is_left" );
        }
        finally
        {
            try { Directory.Delete( root, true ); } catch ( IOException ) { }
        }
    }

    // C4: the dock opens itself the first time the library loads in a project, and never again.
    private static void FirstRun( Action<bool, string> assert )
    {
        assert( FirstRunRules.ShouldOpenDock( alreadyOpened: false, soloCopy: false ), "zero_first_run_opens_the_dock" );
        assert( !FirstRunRules.ShouldOpenDock( alreadyOpened: true, soloCopy: false ), "zero_first_run_opens_it_only_once" );
        assert( !FirstRunRules.ShouldOpenDock( alreadyOpened: false, soloCopy: true ), "zero_first_run_a_solo_test_copy_opens_its_own_way" );
        string a = FirstRunRules.CookieKey( "C:/Projects/Game" );
        assert( a == FirstRunRules.CookieKey( "c:\\projects\\game\\" ) && a != FirstRunRules.CookieKey( "C:/Projects/Other" ) && a.StartsWith( "teamcreate.first-run." ),
            "zero_first_run_is_remembered_per_project: " + a );
    }

    // C5: the invite is copied the first time each new code appears, never again for the same code.
    private static void InviteCopy( Action<bool, string> assert )
    {
        assert( InviteCopyRules.ShouldCopy( "ABCD-1234", null ), "zero_invite_a_new_code_is_copied" );
        assert( !InviteCopyRules.ShouldCopy( "ABCD-1234", "ABCD-1234" ), "zero_invite_the_same_code_is_not_copied_again" );
        assert( InviteCopyRules.ShouldCopy( "WXYZ-9999", "ABCD-1234" ), "zero_invite_a_changed_code_is_copied" );
        assert( !InviteCopyRules.ShouldCopy( null, null ) && !InviteCopyRules.ShouldCopy( " ", "ABCD-1234" ), "zero_invite_no_code_is_never_copied" );
        assert( InviteCopyRules.Toast == "Invite copied. Send it to your collaborators.", "zero_invite_toast_wording" );
    }

    // C6: refusals and notices point at what the tool does for you, not at Git.
    private static void Wording( Action<bool, string> assert )
    {
        assert( !AdmissionRules.BaselineRefusalMessage.Contains( "Git" ) && AdmissionRules.BaselineRefusalMessage.Contains( "s&box" ),
            "zero_the_baseline_refusal_no_longer_sends_people_to_git: " + AdmissionRules.BaselineRefusalMessage );
        var plan = FileJoinPlan.Plan( new[] { new FileIndexEntry( "Code/a.cs", "aa", 1 ) }, new[] { new FileIndexEntry( "Code/a.cs", "bb", 1 ) }, Array.Empty<string>() );
        assert( plan.CodeNotice != null && !plan.CodeNotice.Contains( "Git" ) && plan.CodeNotice.Contains( "Code card" ),
            "zero_the_code_notice_points_at_the_code_card: " + plan.CodeNotice );
    }
}
