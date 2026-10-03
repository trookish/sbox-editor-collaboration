using System;

namespace TeamCreate;

/// <summary>
/// Whether this computer opens the internet relay (a public Cloudflare Quick Tunnel to its own relay) when it hosts. A per-person choice, so it lives in
/// <c>.collaboration/internet-relay.txt</c>, which Git ignores and the project manifest skips: in the shared ProjectSettings it would change the manifest
/// hash and make the host's project differ from every friend's. No file means on, the default. A file that says something other than "on" or "off"
/// keeps the tunnel closed, because opening a public address is the one outcome that must not happen by accident.
/// </summary>
public static class LocalTunnelSetting
{
    public const string FileName = "internet-relay.txt";

    /// <summary>The first line that is neither blank nor a comment decides; <paramref name="error"/> explains a value that is neither "on" nor "off".</summary>
    public static bool Parse( string text, out string error )
    {
        error = null;
        if ( string.IsNullOrWhiteSpace( text ) )
            return true;
        foreach ( string raw in text.Split( '\n' ) )
        {
            string line = raw.Trim();
            if ( line.Length == 0 || line[0] == '#' )
                continue;
            if ( string.Equals( line, "on", StringComparison.OrdinalIgnoreCase ) )
                return true;
            if ( string.Equals( line, "off", StringComparison.OrdinalIgnoreCase ) )
                return false;
            error = "The internet relay preference in .collaboration/" + FileName + " is neither \"on\" nor \"off\", so the internet relay stays off. Set it again in the Relay card.";
            return false;
        }
        return true;
    }

    public static string Format( bool enabled ) =>
        "# Whether this computer opens the internet relay when it hosts: on or off. Set from the Relay card.\n" + ( enabled ? "on" : "off" ) + "\n";
}
