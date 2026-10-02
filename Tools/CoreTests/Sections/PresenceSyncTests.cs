using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamCreate;

// Presence for smooth motion: the sender stamps every presence with its own monotonic clock (SentMs), the host relays it untouched, and the
// cadence is fast enough (33 ms) to give the receiver something to blend. The bandwidth section is the arithmetic that justifies the cadence.
static class PresenceSyncTests
{
    private static int FreePort()
    {
        using var reservation = new TcpListener( IPAddress.Loopback, 0 );
        reservation.Start();
        return ((IPEndPoint)reservation.LocalEndpoint).Port;
    }

    /// <summary>A realistic presence as the editor sends it while flying the camera: camera, a one-object selection, state, tool, scene, and the stamp.</summary>
    private static Packet TypicalPresence( double sentMs )
    {
        return new Packet
        {
            Kind = "presence",
            Peer = Guid.NewGuid().ToString(),
            Scene = "scenes/minimal.scene",
            SentMs = sentMs,
            Presence = new JsonObject
            {
                // The engine writes a Vector3 and a Rotation as one comma-separated string (the same form a .scene file uses); eight significant
                // digits is the longest a camera value gets.
                ["position"] = "1234.5679,-2345.6788,345.67891",
                ["rotation"] = "0.12345678,-0.23456789,0.34567891,0.45678912",
                ["fov"] = 60.0,
                ["selection"] = new JsonArray( Guid.NewGuid().ToString() ),
                ["state"] = "idle",
                ["tool"] = "Object"
            }
        };
    }

    /// <summary>Starts a real host, admits one guest, has it send <paramref name="sent"/> as presence and returns what the host broadcast.</summary>
    private static async Task<Packet> RelayedPresenceAsync( Packet sent )
    {
        var root = Environment.GetEnvironmentVariable( "TMPDIR" );
        if ( string.IsNullOrWhiteSpace( root ) ) root = Path.GetTempPath();
        var directory = Path.Combine( root, "teamcreate-coretests", Guid.NewGuid().ToString( "N" ) );
        SessionServer server = null;
        Wire client = null;
        try
        {
            int port = 0;
            for ( int attempt = 0; attempt < 10 && server == null; attempt++ )
            {
                port = FreePort();
                try { server = new SessionServer( "127.0.0.1", port, "test-project", "test-baseline", "test-token", Path.Combine( directory, $"journal-{attempt}.jsonl" ), null, hello => Task.FromResult( SteamTokenVerdict.Verified ) ); }
                catch ( SocketException ) { }
            }
            if ( server == null ) throw new IOException( "Unable to reserve a disposable SessionServer port" );
            client = await Wire.Connect( "127.0.0.1", port );
            client.Send( new Packet
            {
                Kind = "hello", Peer = Guid.NewGuid().ToString(), ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source,
                Name = "Presence Tester", Token = "test-token", Project = "test-project", Baseline = "test-baseline",
                SteamId = "76561198000000007", SteamToken = "a-realistic-session-token", OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "a1b2c3d4"
            } );
            var deadline = DateTime.UtcNow.AddSeconds( 6 );
            bool welcomed = false;
            while ( DateTime.UtcNow < deadline && !welcomed )
            {
                server.Pump();
                while ( client.Incoming.TryDequeue( out var reply ) ) if ( reply.Kind == "welcome" ) welcomed = true;
                await Task.Delay( 10 );
            }
            if ( !welcomed ) return null;
            client.Send( sent );
            deadline = DateTime.UtcNow.AddSeconds( 6 );
            while ( DateTime.UtcNow < deadline )
            {
                server.Pump();
                while ( client.Incoming.TryDequeue( out var reply ) ) if ( reply.Kind == "presence" ) return reply;
                await Task.Delay( 10 );
            }
            return null;
        }
        finally
        {
            client?.Dispose();
            server?.Dispose();
            try { if ( Directory.Exists( directory ) ) Directory.Delete( directory, true ); }
            catch ( IOException ) { }
        }
    }

    public static void Run( Action<bool, string> assert )
    {
        // ---- The sender's clock -----------------------------------------------------------------------------------------------------------
        var clock = new SessionClock();
        double previous = 0;
        bool positive = true, monotonic = true;
        for ( int i = 0; i < 2000; i++ )
        {
            double now = clock.NowMs;
            positive &= now > 0 && double.IsFinite( now );
            monotonic &= now >= previous;
            previous = now;
        }
        assert( positive, "presence_clock_is_always_positive_so_the_stamp_is_never_the_omitted_zero" );
        assert( monotonic, "presence_clock_never_goes_backwards" );
        double before = clock.NowMs;
        Thread.Sleep( 50 );
        double elapsed = clock.NowMs - before;
        assert( elapsed >= 40 && elapsed < 2000, $"presence_clock_runs_in_milliseconds: {elapsed:F1} ms after a 50 ms sleep" );
        assert( clock.NowMs < 60_000, "presence_clock_starts_with_the_session_not_the_machine_boot" );

        // ---- The wire format --------------------------------------------------------------------------------------------------------------
        string unstamped = JsonSerializer.Serialize( new Packet { Kind = "presence", Peer = "p" } );
        assert( !unstamped.Contains( "SentMs" ), "presence_sent_ms_is_omitted_from_the_wire_when_zero" );
        string stamped = JsonSerializer.Serialize( new Packet { Kind = "presence", Peer = "p", SentMs = 1234.5 } );
        assert( stamped.Contains( "\"SentMs\":1234.5" ), "presence_sent_ms_is_written_when_set" );
        var roundTrip = JsonSerializer.Deserialize<Packet>( stamped );
        assert( roundTrip.SentMs == 1234.5, "presence_sent_ms_round_trips_exactly" );
        var oldPeer = JsonSerializer.Deserialize<Packet>( "{\"Kind\":\"presence\",\"Peer\":\"p\",\"Presence\":{\"state\":\"idle\"}}" );
        assert( oldPeer.SentMs == 0 && !PeerSmoothing.CanSmooth( oldPeer.SentMs ), "presence_from_a_peer_without_the_field_reads_as_zero_and_is_drawn_raw" );

        // ---- The host relays the stamp unchanged ----------------------------------------------------------------------------------------------
        var relayed = RelayedPresenceAsync( TypicalPresence( 98765.4321 ) ).GetAwaiter().GetResult();
        assert( relayed != null && relayed.SentMs == 98765.4321, $"presence_host_broadcast_keeps_sent_ms_untouched: {relayed?.SentMs}" );
        var relayedOld = RelayedPresenceAsync( TypicalPresence( 0 ) ).GetAwaiter().GetResult();
        assert( relayedOld != null && relayedOld.SentMs == 0, "presence_host_broadcast_of_an_unstamped_presence_stays_unstamped" );

        // ---- Cadence ------------------------------------------------------------------------------------------------------------------------
        assert( SendCadence.PresenceMinimum == TimeSpan.FromMilliseconds( 33 ), "presence_minimum_interval_is_33_ms" );
        assert( SendCadence.ShouldSendPresence( changed: true, sinceLastSend: TimeSpan.FromMilliseconds( 33 ), isEditing: false ), "presence_changed_is_sent_after_33_ms" );
        assert( !SendCadence.ShouldSendPresence( changed: true, sinceLastSend: TimeSpan.FromMilliseconds( 32 ), isEditing: false ), "presence_changed_is_held_back_before_33_ms" );
        assert( !SendCadence.ShouldSendPresence( changed: false, sinceLastSend: TimeSpan.FromMilliseconds( 500 ), isEditing: false ), "presence_unchanged_is_still_not_resent_at_the_fast_cadence" );
        assert( SendCadence.PresenceRefresh == TimeSpan.FromSeconds( 2 ) && SendCadence.PingInterval == TimeSpan.FromSeconds( 2 ) && SendCadence.MaxSilence == TimeSpan.FromSeconds( 4 ), "presence_refresh_ping_and_silence_intervals_are_unchanged" );

        // ---- Worst-case bandwidth: four people all moving, through the host's fan-out ------------------------------------------------------
        // The host relays each presence to every other member, so each of four senders produces three copies. The size that matters is the packet AS
        // RELAYED, which the real host has stamped with the sender's name and verified Steam id, so it is measured from the real host's broadcast.
        // Counted on the plain JSON the library sends: the encrypted frame adds a few tens of bytes per message, which is inside the margin below.
        byte[] typical = JsonSerializer.SerializeToUtf8Bytes( TypicalPresence( 123456.789 ) );
        byte[] relayedBytes = JsonSerializer.SerializeToUtf8Bytes( RelayedPresenceAsync( TypicalPresence( 123456.789 ) ).GetAwaiter().GetResult() );
        double presencePerSecond = 1.0 / SendCadence.PresenceMinimum.TotalSeconds;
        double hostUploadBytesPerSecond = 4 * presencePerSecond * 3 * relayedBytes.Length;
        double guestDownloadBytesPerSecond = 3 * presencePerSecond * relayedBytes.Length;
        Console.WriteLine( $"INFO: a typical presence packet is {typical.Length} bytes sent, {relayedBytes.Length} bytes as relayed; four peers all moving: host upload {hostUploadBytesPerSecond / 1000.0:F0} KB/s, each guest download {guestDownloadBytesPerSecond / 1000.0:F0} KB/s" );
        assert( typical.Length < 400, $"presence_typical_packet_is_under_400_bytes_as_sent: {typical.Length}" );
        assert( relayedBytes.Length > typical.Length && relayedBytes.Length < 500, $"presence_relayed_packet_is_a_little_larger_with_the_name_and_steam_id: {relayedBytes.Length}" );
        assert( Math.Abs( presencePerSecond - 1000.0 / 33.0 ) < 1e-9, "presence_rate_while_moving_is_one_per_33_ms" );
        // 4 senders x 30.3 packets x 3 copies = about 364 packets a second leave the host. The budget is what a home upstream of about 2 Mbit/s carries.
        assert( hostUploadBytesPerSecond <= 200_000, $"presence_four_peers_moving_cost_the_host_at_most_200_kb_per_second: {hostUploadBytesPerSecond:F0} B/s" );
        assert( Math.Abs( guestDownloadBytesPerSecond * 4.0 - hostUploadBytesPerSecond ) < 1e-6, "presence_fan_out_arithmetic_host_upload_is_four_times_what_one_guest_receives" );
    }
}
