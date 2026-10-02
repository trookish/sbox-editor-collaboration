using System.Diagnostics;
using TeamCreate;

/// <summary>
/// Safe file paths for received files. A path comes from another person's machine, so a hostile or buggy host must never be able
/// to name a file outside the project's Assets folder. Every refusal rule gets its own assertion, and each one also pins the message,
/// because a path often breaks two rules at once (".." also ends with a dot) and a bare "was refused" would stay green if one rule were deleted.
/// </summary>
static class FilePathRulesTests
{
    public static void Run( Action<bool, string> assert )
    {
        Accepted( assert );
        Refused( assert );
        ReservedNames( assert );
        Limits( assert );
        Inside( assert );
        Resolve( assert );
        Links( assert );
    }

    private static void AssertRefused( Action<bool, string> assert, string path, string phrase, string name )
    {
        bool ok = FilePathRules.TryNormalize( path, out var normalized, out var error );
        assert( !ok, name + "_refused" );
        assert( normalized == null, name + "_no_normalized_value" );
        assert( error != null && error.Contains( phrase ), name + "_message" );
        assert( error != null && !error.Contains( '\0' ) && !error.Contains( '\n' ), name + "_message_is_clean" );
    }

    private static void AssertAccepted( Action<bool, string> assert, string path, string name )
    {
        bool ok = FilePathRules.TryNormalize( path, out var normalized, out var error );
        assert( ok, name + "_accepted" );
        assert( normalized == path, name + "_returned_unchanged" );
        assert( error == null, name + "_no_error" );
    }

    private static void Accepted( Action<bool, string> assert )
    {
        AssertAccepted( assert, "Assets/models/a.vmdl", "path_accept_model" );
        AssertAccepted( assert, "Assets/scenes/main.scene", "path_accept_scene" );
        AssertAccepted( assert, "Assets/a/b/c/d/e/f/g/h/i/j/deep.png", "path_accept_deep" );
        AssertAccepted( assert, "Assets/\u30e2\u30c7\u30eb/\u6905\u5b50.vmdl", "path_accept_unicode_japanese" );
        AssertAccepted( assert, "Assets/caf\u00e9/stra\u00dfe.png", "path_accept_unicode_latin" );
        AssertAccepted( assert, "Assets/my file.png", "path_accept_inner_space" );
        AssertAccepted( assert, "Assets/a.b.c.png", "path_accept_many_dots" );
        AssertAccepted( assert, "Assets/.keep", "path_accept_leading_dot_name" );
        AssertAccepted( assert, "Assets/a_b-c+d,e;f=g@h#i$j%k&l'm(n)o[p]q{r}s~t!u.png", "path_accept_punctuation" );
        AssertAccepted( assert, "Assets/console.txt", "path_accept_name_that_only_starts_like_a_device" );
        AssertAccepted( assert, "Assets/nullable.txt", "path_accept_nullable" );
        AssertAccepted( assert, "Assets/com10.txt", "path_accept_com10" );
        AssertAccepted( assert, "Assets/lpt.txt", "path_accept_lpt_without_digit" );
        AssertAccepted( assert, "Assets/prn_backup/a.png", "path_accept_device_name_prefix_with_suffix" );
        AssertAccepted( assert, "Assets/Assets/a.png", "path_accept_nested_assets_folder" );
    }

    private static void Refused( Action<bool, string> assert )
    {
        AssertRefused( assert, null, "empty", "path_null" );
        AssertRefused( assert, "", "empty", "path_empty" );
        AssertRefused( assert, "   ", "empty", "path_whitespace" );
        AssertRefused( assert, "\t", "empty", "path_tab_only" );

        AssertRefused( assert, "Assets\\a.png", "backslash", "path_backslash" );
        AssertRefused( assert, "Assets/sub\\a.png", "backslash", "path_backslash_inside" );
        AssertRefused( assert, "\\Assets\\a.png", "backslash", "path_backslash_leading" );
        AssertRefused( assert, "\\\\server\\share\\a.png", "backslash", "path_unc_with_backslashes" );

        AssertRefused( assert, "/Assets/a.png", "relative", "path_leading_slash" );
        AssertRefused( assert, "//server/share/Assets/a.png", "relative", "path_unc_slashes" );

        AssertRefused( assert, "C:/Windows/a.png", "colon", "path_drive_letter" );
        AssertRefused( assert, "c:Assets/a.png", "colon", "path_drive_relative" );
        AssertRefused( assert, "Assets/a.png:evil", "colon", "path_alternate_data_stream" );
        AssertRefused( assert, "Assets/a.png::$DATA", "colon", "path_alternate_data_stream_default" );
        AssertRefused( assert, "Assets/a:b/c.png", "colon", "path_colon_in_directory" );
        AssertRefused( assert, "Assets/:a.png", "colon", "path_colon_leading_in_name" );

        AssertRefused( assert, "Assets//a.png", "empty segment", "path_empty_segment" );
        AssertRefused( assert, "Assets/sub//a.png", "empty segment", "path_empty_segment_deep" );

        AssertRefused( assert, "Assets/a/", "end with", "path_trailing_slash" );
        AssertRefused( assert, "Assets/", "end with", "path_trailing_slash_assets_only" );

        AssertRefused( assert, "Assets/./a.png", "\"..\"", "path_dot_segment" );
        AssertRefused( assert, "Assets/../a.png", "\"..\"", "path_dotdot_segment" );
        AssertRefused( assert, "Assets/a/../../Code/Player.cs", "\"..\"", "path_dotdot_escape" );
        AssertRefused( assert, "Assets/a/b/..", "\"..\"", "path_dotdot_last_segment" );
        AssertRefused( assert, "Assets/.", "\"..\"", "path_dot_last_segment" );
        AssertRefused( assert, "../Assets/a.png", "\"..\"", "path_dotdot_first_segment" );

        AssertRefused( assert, "Assets/a\0.png", "control character", "path_nul" );
        AssertRefused( assert, "Assets/a\tb.png", "control character", "path_tab" );
        AssertRefused( assert, "Assets/a\nb.png", "control character", "path_newline" );
        AssertRefused( assert, "Assets/a\rb.png", "control character", "path_carriage_return" );
        AssertRefused( assert, "Assets/a\u001bb.png", "control character", "path_escape_char" );
        AssertRefused( assert, "Assets/a\u007fb.png", "control character", "path_delete_char" );
        AssertRefused( assert, "Assets/a\u0085b.png", "control character", "path_c1_control" );

        AssertRefused( assert, "Assets/a /b.png", "space or a dot", "path_directory_ends_with_space" );
        AssertRefused( assert, "Assets/a.png ", "space or a dot", "path_file_ends_with_space" );
        AssertRefused( assert, "Assets/a./b.png", "space or a dot", "path_directory_ends_with_dot" );
        AssertRefused( assert, "Assets/a.png.", "space or a dot", "path_file_ends_with_dot" );
        AssertRefused( assert, "Assets/a...", "space or a dot", "path_file_ends_with_dots" );

        foreach ( var bad in new[] { '<', '>', '"', '|', '?', '*' } )
        {
            AssertRefused( assert, "Assets/a" + bad + "b.png", "Windows file names", "path_invalid_char_" + (int)bad );
        }

        AssertRefused( assert, "assets/a.png", "must start with", "path_first_segment_lowercase" );
        AssertRefused( assert, "ASSETS/a.png", "must start with", "path_first_segment_uppercase" );
        AssertRefused( assert, "Code/Player.cs", "must start with", "path_first_segment_code" );
        AssertRefused( assert, "Libraries/cyn.tool/a.cs", "must start with", "path_first_segment_libraries" );
        AssertRefused( assert, "ProjectSettings/a.json", "must start with", "path_first_segment_settings" );
        AssertRefused( assert, "a.png", "must start with", "path_first_segment_bare_file" );
        AssertRefused( assert, "Assetsx/a.png", "must start with", "path_first_segment_prefix_only" );
        AssertRefused( assert, "Assets.x/a.png", "must start with", "path_first_segment_with_suffix" );
        AssertRefused( assert, " Assets/a.png", "must start with", "path_first_segment_leading_space" );
        // The folder alone names the directory, not a file inside it: writing there would replace the folder.
        AssertRefused( assert, "Assets", "inside", "path_assets_folder_alone" );
    }

    private static void ReservedNames( Action<bool, string> assert )
    {
        var names = new List<string> { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM\u00b9", "COM\u00b2", "COM\u00b3", "LPT\u00b9", "LPT\u00b2", "LPT\u00b3" };
        for ( int i = 1; i <= 9; i++ )
        {
            names.Add( "COM" + i );
            names.Add( "LPT" + i );
        }
        foreach ( var name in names )
        {
            AssertRefused( assert, "Assets/" + name, "reserved", "path_reserved_" + name );
            AssertRefused( assert, "Assets/" + name.ToLowerInvariant(), "reserved", "path_reserved_lower_" + name );
            AssertRefused( assert, "Assets/" + name + ".txt", "reserved", "path_reserved_stem_" + name );
            AssertRefused( assert, "Assets/dir/" + name + "/a.png", "reserved", "path_reserved_dir_" + name );
        }
        AssertRefused( assert, "Assets/NuL.txt", "reserved", "path_reserved_mixed_case" );
        AssertRefused( assert, "Assets/nul.txt", "reserved", "path_reserved_nul_txt" );
        AssertRefused( assert, "Assets/aux.tar.gz", "reserved", "path_reserved_first_dot_decides" );
        AssertRefused( assert, "Assets/COM1.a.b.c", "reserved", "path_reserved_many_dots" );
        // Windows ignores spaces before the dot, so "nul .txt" opens the NUL device as well.
        AssertRefused( assert, "Assets/nul .txt", "reserved", "path_reserved_space_before_dot" );
    }

    private static void Limits( Action<bool, string> assert )
    {
        // Whole path: 240 characters is the longest accepted.
        string at240 = "Assets/" + string.Join( "/", Enumerable.Repeat( new string( 'a', 49 ), 4 ) ) + "/" + new string( 'b', 240 - 7 - 4 * 50 );
        assert( at240.Length == 240, "path_limit_fixture_is_240" );
        AssertAccepted( assert, at240, "path_limit_240" );
        AssertRefused( assert, at240 + "c", "240", "path_limit_241" );
        AssertRefused( assert, "Assets/" + new string( 'a', 400 ), "240", "path_limit_400" );

        // One segment: 100 characters is the longest accepted.
        AssertAccepted( assert, "Assets/" + new string( 'a', 100 ), "path_segment_100" );
        AssertRefused( assert, "Assets/" + new string( 'a', 101 ), "100 characters", "path_segment_101" );
        AssertRefused( assert, "Assets/" + new string( 'a', 101 ) + "/b.png", "100 characters", "path_directory_segment_101" );
    }

    private static void Inside( Action<bool, string> assert )
    {
        // The containment check cannot be reached through TryResolveUnder (a normalised path never escapes), so it is pinned directly.
        string root = Path.Combine( Path.GetTempPath(), "proj" );
        assert( FilePathRules.IsStrictlyInside( root, Path.Combine( root, "Assets", "a.png" ) ), "path_inside_child_is_inside" );
        assert( !FilePathRules.IsStrictlyInside( root, root + "-evil" + Path.DirectorySeparatorChar + "a.png" ), "path_inside_sibling_with_same_prefix_is_outside" );
        assert( !FilePathRules.IsStrictlyInside( root, root + "-evil" ), "path_inside_sibling_directory_is_outside" );
        assert( !FilePathRules.IsStrictlyInside( root, root ), "path_inside_root_itself_is_not_strictly_inside" );
        assert( !FilePathRules.IsStrictlyInside( root, root + Path.DirectorySeparatorChar ), "path_inside_root_with_separator_is_not_strictly_inside" );
        assert( FilePathRules.IsStrictlyInside( root + Path.DirectorySeparatorChar, Path.Combine( root, "Assets" ) ), "path_inside_root_given_with_separator" );
        assert( !FilePathRules.IsStrictlyInside( root, Path.GetDirectoryName( root ) ), "path_inside_parent_is_outside" );
        if ( Path.DirectorySeparatorChar == '\\' )
        {
            assert( FilePathRules.IsStrictlyInside( root.ToUpperInvariant(), Path.Combine( root, "Assets", "a.png" ) ), "path_inside_ignores_case_on_windows" );
        }
    }

    private static string MakeTemp()
    {
        string tmp = Path.Combine( Path.GetTempPath(), "tc-path-" + Guid.NewGuid().ToString( "N" ) );
        Directory.CreateDirectory( tmp );
        return tmp;
    }

    private static void Resolve( Action<bool, string> assert )
    {
        string tmp = MakeTemp();
        try
        {
            string root = Path.Combine( tmp, "proj" );
            string evil = Path.Combine( tmp, "proj-evil" );
            Directory.CreateDirectory( Path.Combine( root, "Assets", "models" ) );
            Directory.CreateDirectory( evil );

            bool ok = FilePathRules.TryResolveUnder( root, "Assets/models/a.vmdl", out var full, out var error );
            assert( ok && error == null, "path_resolve_normal_inside" );
            assert( full == Path.GetFullPath( Path.Combine( root, "Assets", "models", "a.vmdl" ) ), "path_resolve_normal_full_path" );
            assert( FilePathRules.IsStrictlyInside( root, full ) && !FilePathRules.IsStrictlyInside( evil, full ), "path_resolve_result_is_in_root_not_sibling" );

            File.WriteAllText( Path.Combine( root, "Assets", "models", "exists.vmdl" ), "x" );
            assert( FilePathRules.TryResolveUnder( root, "Assets/models/exists.vmdl", out _, out _ ), "path_resolve_existing_file_ok" );
            assert( FilePathRules.TryResolveUnder( root + "/", "Assets/models/a.vmdl", out var withSlash, out _ ) && withSlash == full, "path_resolve_root_with_trailing_slash" );
            assert( FilePathRules.TryResolveUnder( root.Replace( '\\', '/' ), "Assets/models/a.vmdl", out var forward, out _ ) && forward == full, "path_resolve_root_with_forward_slashes" );

            // Resolving is a question, not an action: it must not create the folders the caller might later write into.
            assert( FilePathRules.TryResolveUnder( root, "Assets/new/deeper/file.png", out _, out _ ), "path_resolve_missing_folders_ok" );
            assert( !Directory.Exists( Path.Combine( root, "Assets", "new" ) ), "path_resolve_creates_nothing" );

            // Root-prefix trick: a sibling that merely starts with the root's name must not be reachable, whatever the relative path says.
            assert( !FilePathRules.TryResolveUnder( root, "../proj-evil/a.png", out var trick, out var trickError ), "path_resolve_sibling_trick_refused" );
            assert( trick == null && trickError != null && trickError.Contains( "\"..\"" ), "path_resolve_sibling_trick_refused_by_normalise" );
            assert( !FilePathRules.TryResolveUnder( root, "Assets/../../proj-evil/a.png", out _, out var deepTrick ) && deepTrick.Contains( "\"..\"" ), "path_resolve_deep_sibling_trick_refused" );

            // Normalise runs first: everything it refuses stays refused here, with its message.
            assert( !FilePathRules.TryResolveUnder( root, "Assets\\a.png", out _, out var backslashError ) && backslashError.Contains( "backslash" ), "path_resolve_rechecks_backslash" );
            assert( !FilePathRules.TryResolveUnder( root, "Code/Player.cs", out var codeFull, out var codeError ) && codeFull == null && codeError.Contains( "must start with" ), "path_resolve_rechecks_first_segment" );
            assert( !FilePathRules.TryResolveUnder( root, "Assets/nul.txt", out _, out var nulError ) && nulError.Contains( "reserved" ), "path_resolve_rechecks_reserved_name" );
            assert( !FilePathRules.TryResolveUnder( root, "C:/Windows/a.png", out _, out var driveError ) && driveError.Contains( "colon" ), "path_resolve_rechecks_drive_letter" );
            assert( !FilePathRules.TryResolveUnder( root, null, out _, out _ ), "path_resolve_null_path_refused" );

            assert( !FilePathRules.TryResolveUnder( null, "Assets/a.png", out var nullRoot, out var nullRootError ) && nullRoot == null && nullRootError.Contains( "root" ), "path_resolve_null_root_refused" );
            assert( !FilePathRules.TryResolveUnder( "  ", "Assets/a.png", out _, out var blankRootError ) && blankRootError.Contains( "root" ), "path_resolve_blank_root_refused" );
        }
        finally
        {
            Directory.Delete( tmp, true );
        }
        assert( !Directory.Exists( tmp ), "path_resolve_temp_directory_cleaned_up" );
    }

    /// <summary>A directory junction needs no privilege on Windows, so it gives real reparse-point coverage even where symlinks are denied.</summary>
    private static bool TryMakeJunction( string link, string target )
    {
        if ( Path.DirectorySeparatorChar != '\\' )
        {
            return false;
        }
        try
        {
            var start = new ProcessStartInfo( "cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"" ) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start( start );
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if ( !process.WaitForExit( 15000 ) )
            {
                return false;
            }
            return process.ExitCode == 0 && Directory.Exists( link );
        }
        catch ( Exception ex ) when ( ex is IOException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception )
        {
            return false;
        }
    }

    private static void Links( Action<bool, string> assert )
    {
        string tmp = MakeTemp();
        var links = new List<string>();
        try
        {
            string root = Path.Combine( tmp, "proj" );
            string assets = Path.Combine( root, "Assets" );
            string outside = Path.Combine( tmp, "outside" );
            Directory.CreateDirectory( Path.Combine( assets, "real" ) );
            Directory.CreateDirectory( outside );
            File.WriteAllText( Path.Combine( outside, "victim.txt" ), "original" );

            // Control: with no link present, the same shape of path resolves. Without it a refusal below could be an accident of the fixture.
            assert( FilePathRules.TryResolveUnder( root, "Assets/real/a.png", out _, out _ ), "path_link_control_real_directory_resolves" );

            string junction = Path.Combine( assets, "jlink" );
            if ( TryMakeJunction( junction, outside ) )
            {
                links.Add( junction );
                assert( ( File.GetAttributes( junction ) & FileAttributes.ReparsePoint ) != 0, "path_link_junction_fixture_is_a_reparse_point" );
                assert( !FilePathRules.TryResolveUnder( root, "Assets/jlink/evil.png", out var jf, out var jerr ) && jf == null && jerr.Contains( "link" ), "path_link_junction_directory_refused" );
                assert( !FilePathRules.TryResolveUnder( root, "Assets/jlink/victim.txt", out _, out var jerr2 ) && jerr2.Contains( "link" ), "path_link_junction_existing_file_through_it_refused" );
                assert( !FilePathRules.TryResolveUnder( root, "Assets/jlink/deep/er/evil.png", out _, out var jerr3 ) && jerr3.Contains( "link" ), "path_link_junction_deep_path_refused" );
                assert( !FilePathRules.TryResolveUnder( root, "Assets/jlink", out _, out var jerr4 ) && jerr4.Contains( "link" ), "path_link_junction_as_last_component_refused" );
                assert( FilePathRules.TryResolveUnder( root, "Assets/real/b.png", out _, out _ ), "path_link_junction_does_not_block_real_directories" );
            }
            else
            {
                Console.WriteLine( "SKIP: junction creation not available" );
            }

            string dirLink = Path.Combine( assets, "slink" );
            try
            {
                Directory.CreateSymbolicLink( dirLink, outside );
                links.Add( dirLink );
                assert( !FilePathRules.TryResolveUnder( root, "Assets/slink/evil.png", out var sf, out var serr ) && sf == null && serr.Contains( "link" ), "path_link_symlink_directory_refused" );
            }
            catch ( Exception ex ) when ( ex is UnauthorizedAccessException || ex is IOException || ex is PlatformNotSupportedException )
            {
                Console.WriteLine( "SKIP: symlink creation not permitted" );
            }

            string fileLink = Path.Combine( assets, "real", "flink.png" );
            try
            {
                File.CreateSymbolicLink( fileLink, Path.Combine( outside, "victim.txt" ) );
                links.Add( fileLink );
                // The link is the file itself: a write to it would land on the victim outside the project.
                assert( !FilePathRules.TryResolveUnder( root, "Assets/real/flink.png", out _, out var ferr ) && ferr.Contains( "link" ), "path_link_symlink_file_refused" );
            }
            catch ( Exception ex ) when ( ex is UnauthorizedAccessException || ex is IOException || ex is PlatformNotSupportedException )
            {
                Console.WriteLine( "SKIP: symlink creation not permitted" );
            }

            assert( File.ReadAllText( Path.Combine( outside, "victim.txt" ) ) == "original", "path_link_outside_file_untouched" );
        }
        finally
        {
            // Remove each link itself first (a non-recursive delete never follows it), then the tree, so cleanup cannot reach the link targets.
            foreach ( var link in links )
            {
                try
                {
                    if ( Directory.Exists( link ) ) Directory.Delete( link, false );
                    else if ( File.Exists( link ) ) File.Delete( link );
                }
                catch ( Exception ex ) when ( ex is IOException || ex is UnauthorizedAccessException )
                {
                    Console.WriteLine( "WARN: could not remove test link " + link );
                }
            }
            Directory.Delete( tmp, true );
        }
        assert( !Directory.Exists( tmp ), "path_link_temp_directory_cleaned_up" );
    }
}
