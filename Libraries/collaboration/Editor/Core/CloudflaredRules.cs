using System;
using System.Globalization;
using System.Security.Cryptography;

namespace TeamCreate;

/// <summary>
/// The rules around a Cloudflare Quick Tunnel: <c>cloudflared tunnel --url http://localhost:8080</c> gives a host a public address with no account,
/// and the editor reads that address out of cloudflared's own output. Everything here is a pure function of text and bytes; starting the process,
/// downloading the executable and reading its output are the editor's job, so that every decision they depend on can be tested without a network.
///
/// The address is handed to friends in an invite and dialled by their editors, so it is treated as untrusted text twice over: cloudflared also logs
/// its API host and error messages that quote URLs, and a lookalike address would send a friend's room proof to someone else's server.
/// </summary>
public static class CloudflaredRules
{
    /// <summary>The longest line of cloudflared output that is examined. Real banner lines are about 100 characters; anything longer is not one.</summary>
    public const int MaxLineChars = 4096;

    private const string Scheme = "https://";

    private const string Suffix = ".trycloudflare.com";

    // cloudflared contacts https://api.trycloudflare.com/tunnel to create a quick tunnel and logs that host, so the label that is not a tunnel is excluded by name.
    private const string ReservedLabel = "api";

    // The longest DNS label. A longer one is not a DNS name, so the engine would refuse the relay URL built from it.
    private const int MaxLabelChars = 63;

    private const int Sha256HexChars = 64;

    private const string EmptyDownload = "The downloaded tool was empty.";

    private const string InvalidExpectedChecksum = "The expected checksum is not valid.";

    private const string ChecksumMismatch = "The downloaded tool does not match its expected checksum and was not run.";

    /// <summary>
    /// Finds the tunnel's public address in ONE line of cloudflared's output, or returns null. The banner draws the address inside a box
    /// (<c>INF |  https://random-words.trycloudflare.com  |</c>), so the address is whatever sits between spaces and pipes. The result is always the
    /// lower-case <c>https://&lt;label&gt;.trycloudflare.com</c> and nothing else: no path, port, userinfo, query, or fragment, and never the label
    /// <c>api</c>. The first valid address wins; a candidate that fails is skipped, not repaired, so a lookalike can never be trimmed into a valid one.
    /// </summary>
    public static string ParseTunnelAddress( string line )
    {
        if ( string.IsNullOrEmpty( line ) || line.Length > MaxLineChars )
            return null;

        int from = 0;
        while ( from < line.Length )
        {
            int at = line.IndexOf( Scheme, from, StringComparison.OrdinalIgnoreCase );
            if ( at < 0 )
                return null;
            from = at + Scheme.Length;

            // The scheme must start a token. Without this, "https://evil.com/https://x.trycloudflare.com" would yield the second address.
            if ( at > 0 && !IsBoundary( line[at - 1] ) )
                continue;

            int end = from;
            while ( end < line.Length && IsHostChar( line[end] ) )
                end++;

            // The host must also END a token. A "/", ":", "@", "?" or "#" here means a path, port, userinfo, query or fragment; a letter or digit
            // beyond the host characters is some other host. Any such address is refused outright rather than cut back to its host part.
            if ( end < line.Length && !IsBoundary( line[end] ) )
                continue;

            string label = GetTunnelLabel( line.Substring( from, end - from ) );
            if ( label != null )
                return Scheme + label + Suffix;
        }
        return null;
    }

    /// <summary>
    /// The relay address for a tunnel: <c>wss://&lt;label&gt;.trycloudflare.com/relay</c>. The argument may be untrusted, so it is validated again here and
    /// must be exactly an address and nothing else (surrounding text, spaces and banner pipes are not accepted; extracting from a line is
    /// <see cref="ParseTunnelAddress"/>'s job). Null when it is not a valid tunnel address.
    /// </summary>
    public static string ToRelayUrl( string tunnelAddress )
    {
        string address = ParseTunnelAddress( tunnelAddress );
        if ( address == null || address.Length != tunnelAddress.Length )
            return null;
        return "wss://" + address.Substring( Scheme.Length ) + RelayProtocol.DefaultPath;
    }

    /// <summary>The command line that runs a quick tunnel to a relay listening on this computer. No automatic update: the executable is the one that was verified.</summary>
    public static string BuildArguments( int localPort )
    {
        if ( localPort < 1 || localPort > 65535 )
            throw new ArgumentOutOfRangeException( nameof( localPort ), localPort, "The local port must be between 1 and 65535." );
        return "tunnel --url http://localhost:" + localPort.ToString( CultureInfo.InvariantCulture ) + " --no-autoupdate";
    }

    /// <summary>
    /// Null when a downloaded executable may be run, otherwise the reason it may not. It may only when it has content and its SHA-256 equals the pinned
    /// value, compared in constant time. The pinned value is the code's own, so one that is malformed (wrong length, not hexadecimal) refuses
    /// everything: a check that silently passed on a typo would run whatever was downloaded.
    /// </summary>
    public static string GetDownloadError( byte[] bytes, string expectedSha256Hex )
    {
        if ( bytes == null || bytes.Length == 0 )
            return EmptyDownload;
        if ( !IsSha256Hex( expectedSha256Hex ) )
            return InvalidExpectedChecksum;

        byte[] expected = Convert.FromHexString( expectedSha256Hex );
        byte[] actual = SHA256.HashData( bytes );
        return CryptographicOperations.FixedTimeEquals( actual, expected ) ? null : ChecksumMismatch;
    }

    // The label of "<label>.trycloudflare.com", lower-cased, or null when the host is anything else or the label is not an acceptable tunnel name.
    private static string GetTunnelLabel( string host )
    {
        host = host.ToLowerInvariant();
        if ( !host.EndsWith( Suffix, StringComparison.Ordinal ) )
            return null;
        string label = host.Substring( 0, host.Length - Suffix.Length );
        if ( label.Length == 0 || label.Length > MaxLabelChars || label[0] == '-' || label[label.Length - 1] == '-' )
            return null;
        foreach ( char c in label )
        {
            if ( !( ( c >= 'a' && c <= 'z' ) || ( c >= '0' && c <= '9' ) || c == '-' ) )
                return null;
        }
        return label == ReservedLabel ? null : label;
    }

    // Ranges rather than char.IsLetterOrDigit, which accepts letters and digits from every script.
    private static bool IsHostChar( char c ) => ( c >= 'a' && c <= 'z' ) || ( c >= 'A' && c <= 'Z' ) || ( c >= '0' && c <= '9' ) || c == '-' || c == '.';

    private static bool IsBoundary( char c ) => c == '|' || char.IsWhiteSpace( c );

    private static bool IsSha256Hex( string text )
    {
        if ( text == null || text.Length != Sha256HexChars )
            return false;
        foreach ( char c in text )
        {
            if ( !( ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ) || ( c >= 'A' && c <= 'F' ) ) )
                return false;
        }
        return true;
    }
}
