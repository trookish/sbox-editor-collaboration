using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using TeamCreate;

/// <summary>
/// The pieces the one-click relay is wired from, checked against real material: cloudflared's own captured output (Tools/TunnelSmoke/sample-output.txt),
/// the command line the editor starts it with, the per-computer opt-out file, and the health answer of a real <see cref="EmbeddedRelay"/>.
/// </summary>
static class HostingWiringTests
{
    public static void Run( Action<bool, string> assert )
    {
        string root = Directory.GetCurrentDirectory();
        while ( root != null && !File.Exists( Path.Combine( root, "Libraries", "collaboration", "VERSION" ) ) )
            root = Directory.GetParent( root )?.FullName;
        assert( root != null, "hostwire_repo_root_found" );
        string[] lines = File.ReadAllLines( Path.Combine( root, "Tools", "TunnelSmoke", "sample-output.txt" ) );

        // Exactly one line of a real run reports the registered edge connection, and it is the last one; the address banner is not it.
        assert( lines.Count( CloudflaredRules.IsRegisteredLine ) == 1 && CloudflaredRules.IsRegisteredLine( lines[^1] ), "hostwire_registered_real_line" );
        assert( lines.Any( l => CloudflaredRules.ParseTunnelAddress( l ) != null && !CloudflaredRules.IsRegisteredLine( l ) ), "hostwire_banner_is_not_registration" );
        assert( !CloudflaredRules.IsRegisteredLine( "2026-10-02T22:45:16Z INF Registered" ), "hostwire_registered_refuses_truncated" );
        assert( !CloudflaredRules.IsRegisteredLine( "2026-10-02T22:45:16Z ERR Registered tunnel connection failed" ), "hostwire_registered_refuses_error_level" );
        assert( !CloudflaredRules.IsRegisteredLine( null ) && !CloudflaredRules.IsRegisteredLine( lines[^1] + new string( ' ', 5000 ) ), "hostwire_registered_bounded" );

        // The tunnel rewrites Host to the listener's own name: HttpListener's localhost prefix answers 400 to the public trycloudflare Host header.
        string args = CloudflaredRules.BuildEmbeddedArguments( 8443 );
        assert( args.StartsWith( CloudflaredRules.BuildArguments( 8443 ), StringComparison.Ordinal ) && args.EndsWith( " --http-host-header localhost:8443", StringComparison.Ordinal ), "hostwire_origin_host_rewrite" );

        // The opt-out file: absent or blank is the default (on); anything unrecognised keeps the public tunnel closed and says why.
        assert( LocalTunnelSetting.Parse( null, out string e1 ) && e1 == null, "hostwire_setting_absent_is_on" );
        assert( LocalTunnelSetting.Parse( "  \n", out _ ), "hostwire_setting_blank_is_on" );
        assert( !LocalTunnelSetting.Parse( LocalTunnelSetting.Format( false ), out string e2 ) && e2 == null, "hostwire_setting_off_round_trip" );
        assert( LocalTunnelSetting.Parse( LocalTunnelSetting.Format( true ), out string e3 ) && e3 == null, "hostwire_setting_on_round_trip" );
        assert( !LocalTunnelSetting.Parse( "maybe\n", out string e4 ) && e4 != null && e4.Contains( LocalTunnelSetting.FileName ), "hostwire_setting_unknown_fails_closed" );

        // The host dials its local relay and a friend dials the public one; both must land in the same room, which rides in the address.
        byte[] secret = Enumerable.Range( 0, 16 ).Select( x => (byte)x ).ToArray();
        string local = RelayProtocol.ConnectUrl( HostingPlan.LocalRelayUrl( 8443 ), secret );
        string publicUrl = RelayProtocol.ConnectUrl( "wss://quiet-river-1234.trycloudflare.com/relay", secret );
        assert( new Uri( local ).PathAndQuery == new Uri( publicUrl ).PathAndQuery, "hostwire_same_room_local_and_public" );

        // The glue's reachability verdict accepts what a real embedded relay answers at /health (the same bytes the tunnel forwards).
        var probe = new TcpListener( IPAddress.Loopback, 0 );
        probe.Start();
        int port = ( (IPEndPoint)probe.LocalEndpoint ).Port;
        probe.Stop();
        using var relay = new EmbeddedRelay( port );
        relay.Start();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds( 10 ) };
        Task<HttpResponseMessage> get = http.GetAsync( "http://localhost:" + port + "/health" );
        var deadline = DateTime.UtcNow.AddSeconds( 10 );
        while ( !get.IsCompleted && DateTime.UtcNow < deadline )
        {
            relay.Pump();
            Thread.Sleep( 5 );
        }
        assert( get.IsCompletedSuccessfully, "hostwire_real_relay_answers_health" );
        using HttpResponseMessage response = get.Result;
        string body = response.Content.ReadAsStringAsync().Result;
        assert( HostingPlan.ProbeVerdict( (int)response.StatusCode, body ) == HostingPlan.ProbeOk, "hostwire_real_relay_health_accepted" );
    }
}
