using System;

namespace TeamCreate;

/// <summary>
/// Whether this computer takes part in shared play (one person plays, the others watch and fly through a mirror). A per-person choice, so it lives in
/// <c>.collaboration/shared-play.txt</c>, which Git ignores and the project manifest skips (the same reasons as <see cref="LocalTunnelSetting"/>).
/// Unlike the tunnel, this defaults to OFF: no file, a blank file, or a value that is neither "on" nor "off" all mean off, and a value that is
/// neither says so. Off, the library behaves exactly as it did before shared play existed.
/// </summary>
public static class SharedPlaySetting
{
    public const string FileName = "shared-play.txt";

    public static bool Parse( string text, out string error )
    {
        error = null;
        if ( string.IsNullOrWhiteSpace( text ) )
        {
            return false;
        }
        foreach ( string raw in text.Split( '\n' ) )
        {
            string line = raw.Trim();
            if ( line.Length == 0 || line[0] == '#' )
            {
                continue;
            }
            if ( string.Equals( line, "on", StringComparison.OrdinalIgnoreCase ) )
            {
                return true;
            }
            if ( string.Equals( line, "off", StringComparison.OrdinalIgnoreCase ) )
            {
                return false;
            }
            error = "The shared play preference in .collaboration/" + FileName + " is neither \"on\" nor \"off\", so shared play stays off. Set it again with the Shared play mode checkbox.";
            return false;
        }
        return false;
    }

    public static string Format( bool enabled ) =>
        "# Whether this computer takes part in shared play (experimental): on or off. Set from the Shared play mode checkbox.\n" + ( enabled ? "on" : "off" ) + "\n";
}
