using TeamCreate;

/// <summary>
/// Join readiness: which project files differ between a guest and the host, and what the guest must be told or asked.
/// The input is a relative path to SHA-256 map per side (ProjectManifest exposes only an aggregate hash today).
/// </summary>
static class JoinReadinessTests
{
    private static readonly string[] NoScenes = new string[0];

    public static void Run( Action<bool, string> assert )
    {
        Classification( assert );
        Identical( assert );
        SingleDifferences( assert );
        MixedAndReasons( assert );
        ExtraFiles( assert );
        CaseAndHashComparison( assert );
        DirtyScenes( assert );
        Truncation( assert );
        Libraries( assert );
        Ordering( assert );
        Arguments( assert );
        DisplayText( assert );
    }

    private static Dictionary<string, string> Files( params (string Path, string Hash)[] entries )
    {
        // Deliberately the default (case-sensitive) comparer: the case-insensitive matching must come from Evaluate, not from the caller's dictionary.
        var map = new Dictionary<string, string>();
        foreach ( var entry in entries )
        {
            map[entry.Path] = entry.Hash;
        }
        return map;
    }

    private static string Describe( JoinReadinessResult result )
    {
        return string.Join( "|", result.Differences.Select( d => d.Kind + ":" + d.FileKind + ":" + d.Path ) );
    }

    private static bool Throws<T>( Action action ) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch ( T )
        {
            return true;
        }
    }

    /// <summary>The parameter a call refuses as null, or null when it does not throw ArgumentNullException. Naming it matters: LINQ throws the same type from inside, which would hide a missing explicit check.</summary>
    private static string NullParameter( Action action )
    {
        try
        {
            action();
            return null;
        }
        catch ( ArgumentNullException ex )
        {
            return ex.ParamName;
        }
    }

    private static void Classification( Action<bool, string> assert )
    {
        var code = new[]
        {
            "Code/Player.cs", "code/player.txt", "CODE/data.json", "Editor/x.png", "Libraries/cyn.tool/a.png", "LIBRARIES/x/y.txt",
            "ProjectSettings/collaboration.json", "projectsettings/a.txt", "game.sbproj", "Game.SBPROJ", "x.csproj", "Assets/code.cs",
            "Assets/ui/Panel.razor", "Assets/ui/Panel.RAZOR", "Assets/ui/Panel.razor.scss", "Assets/ui/style.SCSS", "Assets/a.CS",
            "Code\\Player.txt", "Assets\\a.cs", "Libraries\\cyn.tool\\a.png"
        };
        foreach ( var path in code )
        {
            assert( JoinReadiness.Classify( path ) == ProjectFileKind.Code, "join_classify_code_" + path );
        }

        var assets = new[]
        {
            "Assets/models/a.vmdl", "Assets/scenes/main.scene", "Assets/materials/m.vmat", "Assets/a.png", "readme.txt",
            "Assets/Code/readme.png", "Assets/Editor/x.png", "Assets/Libraries/a.png", "Assets/ProjectSettings/a.png",
            "Codex/a.png", "Editors/a.png", "Assets/a.csv", "Assets/a.cs.png", "Assets/a.cshtml", "Assets/a.sbprojx", "Assets\\models\\a.vmdl"
        };
        foreach ( var path in assets )
        {
            assert( JoinReadiness.Classify( path ) == ProjectFileKind.Asset, "join_classify_asset_" + path );
        }

        // A source file is code wherever it sits, so a .cs hidden under Assets cannot be received without consent.
        assert( JoinReadiness.Classify( "Assets/code.cs" ) == ProjectFileKind.Code, "join_classify_cs_under_assets_is_code" );
        // Only the FIRST folder decides; the folder name must be a whole segment, and a bare root file named like a folder is not "under" it.
        assert( JoinReadiness.Classify( "Code" ) == ProjectFileKind.Asset, "join_classify_bare_name_is_not_a_folder" );

        assert( Throws<ArgumentException>( () => JoinReadiness.Classify( null ) ), "join_classify_null_throws" );
        assert( Throws<ArgumentException>( () => JoinReadiness.Classify( "" ) ), "join_classify_empty_throws" );
        assert( Throws<ArgumentException>( () => JoinReadiness.Classify( "   " ) ), "join_classify_blank_throws" );
    }

    private static void Identical( Action<bool, string> assert )
    {
        var files = Files( ("Assets/a.png", "h1"), ("Code/Player.cs", "h2"), ("game.sbproj", "h3") );
        var result = JoinReadiness.Evaluate( NoScenes, files, Files( ("Assets/a.png", "h1"), ("Code/Player.cs", "h2"), ("game.sbproj", "h3") ) );
        assert( result.CanJoinWithoutTransfer, "join_identical_can_join" );
        assert( result.Differences.Count == 0, "join_identical_no_differences" );
        assert( result.Reasons.Count == 0, "join_identical_no_reasons" );
        assert( !result.NeedsAssetTransfer && !result.NeedsCodeConsent, "join_identical_needs_nothing" );

        var empty = JoinReadiness.Evaluate( NoScenes, Files(), Files() );
        assert( empty.CanJoinWithoutTransfer && empty.Differences.Count == 0 && empty.Reasons.Count == 0, "join_both_empty_can_join" );
    }

    private static void SingleDifferences( Action<bool, string> assert )
    {
        var changed = JoinReadiness.Evaluate( NoScenes, Files( ("Assets/a.png", "old") ), Files( ("Assets/a.png", "new") ) );
        assert( Describe( changed ) == "Changed:Asset:Assets/a.png", "join_changed_asset_difference" );
        assert( changed.NeedsAssetTransfer && !changed.NeedsCodeConsent && !changed.CanJoinWithoutTransfer, "join_changed_asset_flags" );
        assert( changed.Reasons.Count == 1 && changed.Reasons[0] == "1 project file differs from the host's: Assets/a.png.", "join_changed_asset_reason" );

        var missingAsset = JoinReadiness.Evaluate( NoScenes, Files(), Files( ("Assets/a.png", "h") ) );
        assert( Describe( missingAsset ) == "Missing:Asset:Assets/a.png", "join_missing_asset_difference" );
        assert( missingAsset.NeedsAssetTransfer && !missingAsset.NeedsCodeConsent, "join_missing_asset_flags" );

        var missingCode = JoinReadiness.Evaluate( NoScenes, Files(), Files( ("Code/Player.cs", "h") ) );
        assert( Describe( missingCode ) == "Missing:Code:Code/Player.cs", "join_missing_code_difference" );
        assert( missingCode.NeedsCodeConsent && !missingCode.NeedsAssetTransfer && !missingCode.CanJoinWithoutTransfer, "join_missing_code_needs_consent_not_transfer" );
        assert( missingCode.Reasons.Count == 1
            && missingCode.Reasons[0] == "1 code file differs and needs your approval before it is received: Code/Player.cs.", "join_missing_code_reason" );

        var changedCode = JoinReadiness.Evaluate( NoScenes, Files( ("Code/Player.cs", "a") ), Files( ("Code/Player.cs", "b") ) );
        assert( Describe( changedCode ) == "Changed:Code:Code/Player.cs" && changedCode.NeedsCodeConsent && !changedCode.NeedsAssetTransfer, "join_changed_code_needs_consent" );

        // A .cs under Assets is code, so it must raise the consent flag and never the asset-transfer flag.
        var csInAssets = JoinReadiness.Evaluate( NoScenes, Files(), Files( ("Assets/code.cs", "h") ) );
        assert( csInAssets.NeedsCodeConsent && !csInAssets.NeedsAssetTransfer, "join_cs_under_assets_needs_consent_not_transfer" );

        // Project settings and the descriptor are code too: they change what runs.
        var settings = JoinReadiness.Evaluate( NoScenes, Files( ("ProjectSettings/a.json", "1"), ("game.sbproj", "1") ), Files( ("ProjectSettings/a.json", "2"), ("game.sbproj", "2") ) );
        assert( settings.NeedsCodeConsent && !settings.NeedsAssetTransfer, "join_settings_and_descriptor_are_code" );
    }

    private static void MixedAndReasons( Action<bool, string> assert )
    {
        var guest = Files( ("Assets/a.png", "g"), ("Assets/same.png", "s"), ("Code/A.cs", "g") );
        var host = Files( ("Assets/a.png", "h"), ("Assets/b.png", "h"), ("Assets/c.mat", "h"), ("Assets/same.png", "s"), ("Code/A.cs", "h"), ("Code/B.cs", "h") );
        var result = JoinReadiness.Evaluate( NoScenes, guest, host );
        assert( Describe( result ) == "Changed:Asset:Assets/a.png|Missing:Asset:Assets/b.png|Missing:Asset:Assets/c.mat|Changed:Code:Code/A.cs|Missing:Code:Code/B.cs",
            "join_mixed_differences" );
        assert( result.NeedsAssetTransfer && result.NeedsCodeConsent && !result.CanJoinWithoutTransfer, "join_mixed_flags" );
        assert( result.Reasons.Count == 2, "join_mixed_two_reasons" );
        assert( result.Reasons[0] == "3 project files differ from the host's: Assets/a.png, Assets/b.png and Assets/c.mat.", "join_mixed_asset_reason" );
        assert( result.Reasons[1] == "2 code files differ and need your approval before they are received: Code/A.cs and Code/B.cs.", "join_mixed_code_reason" );

        // Two assets read "a and b", not "a, b".
        var two = JoinReadiness.Evaluate( NoScenes, Files(), Files( ("Assets/a.png", "h"), ("Assets/b.png", "h") ) );
        assert( two.Reasons[0] == "2 project files differ from the host's: Assets/a.png and Assets/b.png.", "join_two_paths_use_and" );
    }

    private static void ExtraFiles( Action<bool, string> assert )
    {
        // Extra = only the guest has it. It is the guest's own work: nothing to receive, nothing to approve, never a reason to refuse.
        var only = JoinReadiness.Evaluate( NoScenes, Files( ("Assets/mine.png", "m"), ("Code/Mine.cs", "m") ), Files() );
        assert( Describe( only ) == "Extra:Asset:Assets/mine.png|Extra:Code:Code/Mine.cs", "join_extra_reported" );
        assert( only.CanJoinWithoutTransfer, "join_extra_does_not_block_joining" );
        assert( !only.NeedsAssetTransfer && !only.NeedsCodeConsent, "join_extra_needs_no_transfer_or_consent" );
        assert( only.Reasons.Count == 0, "join_extra_alone_has_no_reasons" );

        var mixed = JoinReadiness.Evaluate( NoScenes, Files( ("Assets/mine.png", "m"), ("Assets/a.png", "old") ), Files( ("Assets/a.png", "new") ) );
        assert( Describe( mixed ) == "Changed:Asset:Assets/a.png|Extra:Asset:Assets/mine.png", "join_extra_beside_changed_listed" );
        assert( mixed.Reasons.Count == 1 && mixed.Reasons[0] == "1 project file differs from the host's: Assets/a.png.", "join_extra_not_counted_in_reasons" );
        assert( mixed.NeedsAssetTransfer && !mixed.CanJoinWithoutTransfer, "join_extra_beside_changed_still_needs_transfer" );

        // The extra code file must not raise consent either: nothing is being received.
        var extraCode = JoinReadiness.Evaluate( NoScenes, Files( ("Code/Mine.cs", "m"), ("Assets/a.png", "x") ), Files( ("Assets/a.png", "x") ) );
        assert( !extraCode.NeedsCodeConsent && extraCode.CanJoinWithoutTransfer, "join_extra_code_needs_no_consent" );
    }

    private static void CaseAndHashComparison( Action<bool, string> assert )
    {
        // Windows file systems treat paths case-insensitively, so a path that differs only by case is the same file.
        var sameFile = JoinReadiness.Evaluate( NoScenes, Files( ("assets/A.PNG", "h1") ), Files( ("Assets/a.png", "h1") ) );
        assert( sameFile.Differences.Count == 0 && sameFile.CanJoinWithoutTransfer, "join_case_different_path_is_same_file" );

        var sameFileChanged = JoinReadiness.Evaluate( NoScenes, Files( ("assets/A.PNG", "old") ), Files( ("Assets/a.png", "new") ) );
        assert( Describe( sameFileChanged ) == "Changed:Asset:Assets/a.png", "join_case_different_path_changed_reports_host_path" );

        assert( JoinReadiness.Evaluate( NoScenes, Files( ("Assets/a.png", "ABCDEF") ), Files( ("Assets/a.png", "abcdef") ) ).CanJoinWithoutTransfer, "join_hash_hex_case_is_ignored" );

        // A hash that is missing cannot prove the files equal, so it must not: refuse closed rather than call the project identical.
        assert( Describe( JoinReadiness.Evaluate( NoScenes, Files( ("Assets/a.png", null) ), Files( ("Assets/a.png", "h") ) ) ) == "Changed:Asset:Assets/a.png", "join_null_guest_hash_is_changed" );
        assert( Describe( JoinReadiness.Evaluate( NoScenes, Files( ("Assets/a.png", "h") ), Files( ("Assets/a.png", "") ) ) ) == "Changed:Asset:Assets/a.png", "join_empty_host_hash_is_changed" );
        assert( Describe( JoinReadiness.Evaluate( NoScenes, Files( ("Assets/a.png", "") ), Files( ("Assets/a.png", "") ) ) ) == "Changed:Asset:Assets/a.png", "join_two_empty_hashes_are_not_equal" );

        // Two keys that differ only by case collapse to one Windows file; which hash is real is unknowable, so the file is reported as changed.
        var collision = JoinReadiness.Evaluate( NoScenes, Files( ("Assets/a.png", "h"), ("assets/A.PNG", "h") ), Files( ("Assets/a.png", "h") ) );
        assert( Describe( collision ) == "Changed:Asset:Assets/a.png", "join_case_collision_in_one_side_is_changed" );
    }

    private static void DirtyScenes( Action<bool, string> assert )
    {
        var same = Files( ("Assets/a.png", "h") );
        var one = JoinReadiness.Evaluate( new[] { "Assets/scenes/main.scene" }, same, Files( ("Assets/a.png", "h") ) );
        assert( one.Reasons.Count == 1, "join_dirty_scene_one_reason" );
        assert( one.Reasons[0] == "Scene \"Assets/scenes/main.scene\" has unsaved changes. They will be backed up and replaced by the host's scene.", "join_dirty_scene_reason_text" );
        // The backup makes the replacement safe, so an unsaved scene informs the guest but never blocks the join.
        assert( one.CanJoinWithoutTransfer && !one.NeedsAssetTransfer && !one.NeedsCodeConsent, "join_dirty_scene_does_not_block" );

        var two = JoinReadiness.Evaluate( new[] { "Assets/scenes/a.scene", "Assets/scenes/b.scene" }, Files( ("Assets/a.png", "g") ), Files( ("Assets/a.png", "h") ) );
        assert( two.Reasons.Count == 3, "join_dirty_scenes_one_reason_each_plus_files" );
        assert( two.Reasons[0].StartsWith( "Scene \"Assets/scenes/a.scene\"" ) && two.Reasons[1].StartsWith( "Scene \"Assets/scenes/b.scene\"" ), "join_dirty_scenes_listed_first_in_given_order" );
        assert( two.Reasons[2].StartsWith( "1 project file differs" ), "join_dirty_scenes_before_file_reasons" );

        var twice = JoinReadiness.Evaluate( new[] { "Assets/scenes/a.scene", "assets/scenes/A.SCENE" }, same, Files( ("Assets/a.png", "h") ) );
        assert( twice.Reasons.Count == 1, "join_dirty_scene_duplicate_ignoring_case_reported_once" );

        assert( Throws<ArgumentException>( () => JoinReadiness.Evaluate( new string[] { null }, same, same ) ), "join_dirty_scene_null_entry_throws" );
        assert( Throws<ArgumentException>( () => JoinReadiness.Evaluate( new[] { "  " }, same, same ) ), "join_dirty_scene_blank_entry_throws" );
    }

    private static void Truncation( Action<bool, string> assert )
    {
        string[] names = { "a", "b", "c", "d", "e", "f", "g" };
        Func<int, JoinReadinessResult> hostHas = count => JoinReadiness.Evaluate( NoScenes, Files(), Files( names.Take( count ).Select( n => ("Assets/" + n + ".png", "h") ).ToArray() ) );

        assert( hostHas( 5 ).Reasons[0] == "5 project files differ from the host's: Assets/a.png, Assets/b.png, Assets/c.png, Assets/d.png and Assets/e.png.", "join_five_paths_all_listed" );
        assert( hostHas( 6 ).Reasons[0] == "6 project files differ from the host's: Assets/a.png, Assets/b.png, Assets/c.png, Assets/d.png, Assets/e.png and 1 more.", "join_six_paths_truncated_with_one_more" );
        assert( hostHas( 7 ).Reasons[0] == "7 project files differ from the host's: Assets/a.png, Assets/b.png, Assets/c.png, Assets/d.png, Assets/e.png and 2 more.", "join_seven_paths_truncated_with_two_more" );
        assert( hostHas( 7 ).Differences.Count == 7, "join_truncation_keeps_every_difference_in_the_list" );

        var code = JoinReadiness.Evaluate( NoScenes, Files(), Files( names.Select( n => ("Code/" + n + ".cs", "h") ).ToArray() ) );
        assert( code.Reasons[0] == "7 code files differ and need your approval before they are received: Code/a.cs, Code/b.cs, Code/c.cs, Code/d.cs, Code/e.cs and 2 more.", "join_code_paths_truncated_too" );
    }

    private static void Libraries( Action<bool, string> assert )
    {
        const string LibraryReason = "The installed library files differ from the host's. Install the same library version on both machines.";

        var missing = JoinReadiness.Evaluate( NoScenes, Files(), Files( ("Libraries/cyn.tool/Editor/Tool.cs", "h") ) );
        assert( Describe( missing ) == "Missing:Code:Libraries/cyn.tool/Editor/Tool.cs", "join_library_difference_is_code_kind" );
        assert( missing.NeedsCodeConsent && !missing.NeedsAssetTransfer && !missing.CanJoinWithoutTransfer, "join_library_difference_flags" );
        assert( missing.Reasons.Contains( LibraryReason ), "join_library_dedicated_reason" );
        // A library cannot be approved and received like a project file; it must be installed. Listing it as "needs your approval" would mislead.
        assert( missing.Reasons.Count == 1, "join_library_files_not_listed_as_approvable_code" );

        var changed = JoinReadiness.Evaluate( NoScenes, Files( ("Libraries/cyn.tool/a.txt", "1"), ("Libraries/cyn.tool/b.txt", "1") ), Files( ("Libraries/cyn.tool/a.txt", "2"), ("Libraries/cyn.tool/b.txt", "2") ) );
        assert( changed.Reasons.Count( r => r == LibraryReason ) == 1, "join_library_reason_appears_once" );

        var both = JoinReadiness.Evaluate( NoScenes, Files(), Files( ("Libraries/cyn.tool/a.txt", "h"), ("Code/Player.cs", "h") ) );
        assert( both.Reasons.Contains( LibraryReason ), "join_library_and_code_library_reason" );
        assert( both.Reasons.Contains( "1 code file differs and needs your approval before it is received: Code/Player.cs." ), "join_library_and_code_code_reason_excludes_library" );

        // A library file only the guest has is the guest's own business, like any extra.
        var extra = JoinReadiness.Evaluate( NoScenes, Files( ("Libraries/cyn.tool/a.txt", "h") ), Files() );
        assert( extra.CanJoinWithoutTransfer && extra.Reasons.Count == 0, "join_library_extra_does_not_block" );
    }

    private static void Ordering( Action<bool, string> assert )
    {
        // Built in reverse order on both sides: the output order must come from the paths, not from dictionary insertion order.
        var guest = Files( ("Assets/z.png", "1"), ("Assets/B.png", "1"), ("Assets/extra.png", "1") );
        var host = Files( ("Code/Player.cs", "2"), ("Assets/a.png", "2"), ("Assets/z.png", "2"), ("Assets/B.png", "2") );
        var result = JoinReadiness.Evaluate( NoScenes, guest, host );
        var paths = result.Differences.Select( d => d.Path ).ToArray();
        assert( string.Join( "," , paths ) == "Assets/a.png,Assets/B.png,Assets/extra.png,Assets/z.png,Code/Player.cs", "join_differences_sorted_by_path_ignoring_case" );

        var again = JoinReadiness.Evaluate( NoScenes, guest, host );
        assert( Describe( again ) == Describe( result ) && again.Reasons.SequenceEqual( result.Reasons ), "join_evaluate_is_deterministic" );

        var reversedHost = Files( ("Assets/B.png", "2"), ("Assets/z.png", "2"), ("Assets/a.png", "2"), ("Code/Player.cs", "2") );
        assert( Describe( JoinReadiness.Evaluate( NoScenes, guest, reversedHost ) ) == Describe( result ), "join_result_independent_of_insertion_order" );
    }

    private static void Arguments( Action<bool, string> assert )
    {
        var files = Files( ("Assets/a.png", "h") );
        assert( NullParameter( () => JoinReadiness.Evaluate( null, files, files ) ) == "guestDirtyScenePaths", "join_null_dirty_scenes_throws" );
        assert( NullParameter( () => JoinReadiness.Evaluate( NoScenes, null, files ) ) == "guestFiles", "join_null_guest_files_throws" );
        assert( NullParameter( () => JoinReadiness.Evaluate( NoScenes, files, null ) ) == "hostFiles", "join_null_host_files_throws" );
        // A manifest entry with no name cannot be classified or shown; the caller must validate the manifest before asking.
        assert( Throws<ArgumentException>( () => JoinReadiness.Evaluate( NoScenes, Files( ("", "h") ), files ) ), "join_empty_guest_path_throws" );
        assert( Throws<ArgumentException>( () => JoinReadiness.Evaluate( NoScenes, files, Files( ("  ", "h") ) ) ), "join_blank_host_path_throws" );
        // Refused even when both sides agree on it and nothing would be classified: a malformed manifest is not made valid by matching.
        assert( Throws<ArgumentException>( () => JoinReadiness.Evaluate( NoScenes, Files( ("", "h") ), Files( ("", "h") ) ) ), "join_empty_path_throws_even_when_both_sides_match" );
    }

    private static void DisplayText( Action<bool, string> assert )
    {
        // Host-supplied paths reach the guest's screen: control characters must not, and one absurd path must not flood the dialog.
        var hostile = JoinReadiness.Evaluate( NoScenes, Files(), Files( ("Assets/a\nb\u0000c.png", "h") ) );
        assert( hostile.Differences[0].Path == "Assets/a\nb\u0000c.png", "join_difference_keeps_the_raw_path" );
        assert( hostile.Reasons[0] == "1 project file differs from the host's: Assets/a?b?c.png.", "join_reason_replaces_control_characters" );

        var longPath = "Assets/" + new string( 'x', 500 ) + ".png";
        var longResult = JoinReadiness.Evaluate( NoScenes, Files(), Files( (longPath, "h") ) );
        assert( longResult.Reasons[0].Length < 200, "join_reason_bounds_a_very_long_path" );
        assert( longResult.Reasons[0].Contains( "..." ), "join_reason_marks_a_shortened_path" );
        assert( longResult.Differences[0].Path == longPath, "join_difference_keeps_a_long_path_whole" );

        var scene = JoinReadiness.Evaluate( new[] { "Assets/s\r\n.scene" }, Files(), Files() );
        assert( scene.Reasons[0].Contains( "Assets/s??.scene" ) && !scene.Reasons[0].Contains( '\n' ), "join_dirty_scene_reason_replaces_control_characters" );
    }
}
