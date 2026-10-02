using System;
using System.Text;

namespace TeamCreate;

/// <summary>
/// The name of, and the sentence about, the copy of a user's unsent scene changes that is written before the host's snapshot replaces
/// them. The scene name comes from a peer, so it is reduced to a plain file name here: whatever it contains, the result is one file in
/// the recovery folder.
/// </summary>
public static class RecoveryBackup
{
    /// <summary>The most characters of the scene name kept in the file name.</summary>
    public const int MaxSceneNameLength = 80;

    private const string Placeholder = "scene";

    /// <summary>
    /// <c>pre-sync-&lt;yyyyMMdd-HHmmss&gt;-&lt;scene&gt;.scene</c>. The scene part keeps only letters, digits, dot, underscore and hyphen
    /// (everything else, including every kind of separator, becomes an underscore), never holds two dots in a row, is cut to
    /// <see cref="MaxSceneNameLength"/> characters and falls back to a placeholder when nothing is left. The caller passes UTC.
    /// </summary>
    public static string BuildFileName( string scene, DateTime utcNow )
    {
        return "pre-sync-" + utcNow.ToString( "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture ) + "-" + Sanitise( scene ) + ".scene";
    }

    /// <summary>What the user is told once the host's version has replaced their copy and their changes are saved at <paramref name="path"/>.</summary>
    public static string BuildNotice( string scene, string path )
    {
        return "The host's version of \"" + scene + "\" was restored after your copy drifted. Your unsent changes were saved to " + path + ".";
    }

    /// <summary>The message when the copy could not be written: the host's scene is then not applied, because unsaved work is never replaced without a copy.</summary>
    public const string BackupFailedMessage = "Your unsaved changes could not be backed up, so the host's scene was not applied.";

    private static string Sanitise( string scene )
    {
        if ( string.IsNullOrWhiteSpace( scene ) )
        {
            return Placeholder;
        }

        var text = new StringBuilder( Math.Min( scene.Length, MaxSceneNameLength ) );
        foreach ( char c in scene )
        {
            if ( text.Length >= MaxSceneNameLength )
            {
                break;
            }
            bool plain = ( c >= 'A' && c <= 'Z' ) || ( c >= 'a' && c <= 'z' ) || ( c >= '0' && c <= '9' ) || c == '.' || c == '_' || c == '-';
            text.Append( plain ? c : '_' );
        }

        // ".." means "parent folder" to a path, and the suffix is appended after this part, so a trailing dot would make one too.
        string result = text.ToString();
        while ( result.Contains( ".." ) )
        {
            result = result.Replace( "..", "._" );
        }
        result = result.TrimEnd( '.' );
        return result.Length == 0 ? Placeholder : result;
    }
}
