using TeamCreate;

/// <summary>
/// The name of the copy written before a snapshot replaces unsaved local work, and the sentence that tells the user where it went. The
/// scene name arrives from a peer and ends up in a file name, so the hostile cases are the point.
/// </summary>
static class RecoveryBackupTests
{
    private static readonly DateTime Fixed = new( 2026, 10, 2, 9, 5, 7, DateTimeKind.Utc );

    private static bool IsSafeFileName( string name )
    {
        if ( string.IsNullOrEmpty( name ) ) return false;
        if ( name.Contains( ".." ) ) return false;
        if ( name.IndexOfAny( new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0' } ) >= 0 ) return false;
        if ( name.Any( c => c < ' ' ) ) return false;
        if ( Path.GetFileName( name ) != name ) return false;
        return name.All( c => ( c >= 'A' && c <= 'Z' ) || ( c >= 'a' && c <= 'z' ) || ( c >= '0' && c <= '9' ) || c == '.' || c == '_' || c == '-' );
    }

    public static void Run( Action<bool, string> assert )
    {
        // ---- format with a fixed time ----
        assert( RecoveryBackup.BuildFileName( "scenes/main.scene", Fixed ) == "pre-sync-20261002-090507-scenes_main.scene.scene", "backup_file_name_format_with_fixed_time" );
        assert( RecoveryBackup.BuildFileName( "level1", Fixed ) == "pre-sync-20261002-090507-level1.scene", "backup_file_name_plain_scene_name" );

        // Single-digit months, days and times are zero padded, so names sort in time order.
        {
            var utc = new DateTime( 2026, 1, 31, 23, 59, 59, DateTimeKind.Utc );
            assert( RecoveryBackup.BuildFileName( "a", utc ).StartsWith( "pre-sync-20260131-235959-" ), "backup_file_name_stamp_is_zero_padded_utc" );
        }

        // ---- hostile names never escape the folder or carry a separator ----
        var hostile = new[]
        {
            "../../x", "..\\..\\x", "a/b", "a\\b", "C:\\Windows\\system32", "scene:with:colons", "..", ".", "...", "a..b", "con", "nul:",
            "tab\tname", "new\nline", "nul\0byte", "quote\"s", "star*", "pipe|", "question?", "<angle>", "spaces in name", "ünïcödé", "名前", " ", "\u202Ertl",
            new string( 'a', 500 ), "scenes/" + new string( 'b', 500 ) + ".scene", new string( '.', 300 )
        };
        foreach ( var name in hostile )
        {
            var result = RecoveryBackup.BuildFileName( name, Fixed );
            var label = name.Length > 20 ? name.Substring( 0, 20 ) + "..." : name;
            assert( IsSafeFileName( result ), "backup_hostile_name_stays_a_plain_file_name: " + label.Replace( "\0", "\\0" ).Replace( "\n", "\\n" ).Replace( "\t", "\\t" ) );
            assert( result.StartsWith( "pre-sync-20261002-090507-" ) && result.EndsWith( ".scene" ), "backup_hostile_name_keeps_prefix_and_extension: " + label.Replace( "\0", "\\0" ).Replace( "\n", "\\n" ).Replace( "\t", "\\t" ) );
        }

        // ---- the resolved path stays inside the recovery folder ----
        {
            var folder = Path.GetFullPath( Path.Combine( Path.GetTempPath(), "recovery-probe" ) );
            var escaped = hostile.Any( name =>
            {
                var full = Path.GetFullPath( Path.Combine( folder, RecoveryBackup.BuildFileName( name, Fixed ) ) );
                return !string.Equals( Path.GetDirectoryName( full ), folder, StringComparison.OrdinalIgnoreCase );
            } );
            assert( !escaped, "backup_hostile_names_resolve_inside_the_recovery_folder" );
        }

        // ---- length cap ----
        {
            var scenePart = RecoveryBackup.BuildFileName( new string( 'a', 500 ), Fixed );
            var prefix = "pre-sync-20261002-090507-";
            var middle = scenePart.Substring( prefix.Length, scenePart.Length - prefix.Length - ".scene".Length );
            assert( middle.Length == 80, "backup_scene_part_is_capped_at_eighty_characters" );
            assert( RecoveryBackup.BuildFileName( new string( 'a', 80 ), Fixed ).Contains( new string( 'a', 80 ) ), "backup_name_of_exactly_eighty_characters_is_kept_whole" );
        }

        // ---- empty or unusable names still produce a usable file ----
        {
            var empty = RecoveryBackup.BuildFileName( "", Fixed );
            var nullName = RecoveryBackup.BuildFileName( null, Fixed );
            var blank = RecoveryBackup.BuildFileName( "   ", Fixed );
            assert( IsSafeFileName( empty ) && IsSafeFileName( nullName ) && IsSafeFileName( blank ), "backup_empty_null_and_blank_names_are_safe" );
            assert( empty == "pre-sync-20261002-090507-scene.scene", "backup_empty_name_falls_back_to_a_placeholder" );
            assert( IsSafeFileName( RecoveryBackup.BuildFileName( "///", Fixed ) ), "backup_all_separator_name_is_a_plain_file_name" );
        }

        // ---- two different moments never collide, whatever the scene ----
        {
            var a = RecoveryBackup.BuildFileName( "scenes/a.scene", Fixed );
            var b = RecoveryBackup.BuildFileName( "scenes/a.scene", Fixed.AddSeconds( 1 ) );
            assert( a != b, "backup_names_differ_by_the_second" );
        }

        // ---- notice wording ----
        {
            var notice = RecoveryBackup.BuildNotice( "scenes/main.scene", "C:/proj/.collaboration/recovery/pre-sync-x.scene" );
            assert( notice == "The host's version of \"scenes/main.scene\" was restored after your copy drifted. Your unsent changes were saved to C:/proj/.collaboration/recovery/pre-sync-x.scene.", "backup_notice_wording" );
        }
    }
}
