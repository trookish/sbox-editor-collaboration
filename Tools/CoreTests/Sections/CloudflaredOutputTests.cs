using TeamCreate;

// The parser against REAL cloudflared output. Tools/TunnelSmoke/sample-output.txt is what an actual `cloudflared tunnel --url ... --no-autoupdate` run
// printed (captured by Tools/Smoke-Tunnel.ps1 on 2026-10-02 from the pinned 2026.9.3 build; only this machine's private LAN and link-local addresses were
// replaced with documentation addresses before committing), so these assertions are the one place ParseTunnelAddress meets text it did not write itself.
// When the pin moves, rerun the smoke script and update the address below: the capture and this test are a pair.
public static class CloudflaredOutputTests
{
    // The address cloudflared printed in that run. It was a throwaway tunnel and was closed seconds later.
    private const string Expected = "https://preceding-years-pacific-michelle.trycloudflare.com";

    // The pinned build's SHA-256, which cloudflared reports about itself in its "Version ... (Checksum ...)" line. Copied from CloudflaredPin (not compiled
    // into CoreTests) so the test can tell that the capture came from the pinned binary and not from some other cloudflared.
    private const string PinnedSha256 = "f096265ec2fcbe9bb6e2d64268db167ced3fcbb83d894bdb9e2fcdb26f2ea7e2";

    public static void Run( Action<bool, string> assert )
    {
        string path = FindSample();
        assert( path != null, "cfreal_sample_file_present" );
        string[] lines = File.ReadAllLines( path );
        assert( lines.Length > 0, "cfreal_sample_not_empty" );
        assert( lines.Any( l => l.Contains( "(Checksum " + PinnedSha256 + ")", StringComparison.Ordinal ) ), "cfreal_capture_is_from_pinned_build" );

        // The address must come from the banner line, which follows the "has been created" line, and from nowhere else: the capture also holds the
        // terms URL, the docs URL, the local origin URL and the Cloudflare API host, all of which must be ignored.
        int created = Array.FindIndex( lines, l => l.Contains( "Your quick Tunnel has been created", StringComparison.Ordinal ) );
        assert( created >= 0 && created + 1 < lines.Length, "cfreal_banner_present" );
        int matches = 0;
        for ( int i = 0; i < lines.Length; i++ )
        {
            string parsed = CloudflaredRules.ParseTunnelAddress( lines[i] );
            if ( i == created + 1 )
                assert( parsed == Expected, "cfreal_banner_line_yields_the_real_address" );
            else
                assert( parsed == null, "cfreal_non_banner_line_yields_null" );
            if ( parsed != null )
                matches++;
        }
        assert( matches == 1, "cfreal_exactly_one_line_yields_an_address" );
        assert( CloudflaredRules.ToRelayUrl( Expected ) == "wss://preceding-years-pacific-michelle.trycloudflare.com/relay", "cfreal_relay_url_from_real_address" );
    }

    private static string FindSample()
    {
        string dir = AppContext.BaseDirectory;
        while ( !string.IsNullOrEmpty( dir ) )
        {
            string candidate = Path.Combine( dir, "Tools", "TunnelSmoke", "sample-output.txt" );
            if ( File.Exists( candidate ) )
                return candidate;
            dir = Path.GetDirectoryName( dir.TrimEnd( Path.DirectorySeparatorChar ) );
        }
        return null;
    }
}
