using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TeamCreate;

// One collaborator's problem stays that collaborator's problem: a guest that leaves mid-verification, sends something the host cannot relay,
// claims another person's collaborator ID or reconnects from a new editor must never cost anybody else the session, and must never stop the host.
static class HostIsolationTests
{
    private const string AccountA = "76561198000000011";
    private const string AccountB = "76561198000000012";

    private static int FreePort()
    {
        using var reservation = new TcpListener( IPAddress.Loopback, 0 );
        reservation.Start();
        return ((IPEndPoint)reservation.LocalEndpoint).Port;
    }

    private static Packet Hello( string steamId, string peer = null ) => new Packet
    {
        Kind = "hello", Peer = peer ?? Guid.NewGuid().ToString(), ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source,
        Name = "Isolation " + steamId[^2..], Token = "test-token", Project = "test-project", Baseline = "test-baseline",
        SteamId = steamId, SteamToken = "a-realistic-session-token", OrgIdent = "cyn", LibraryVersion = "1.2.0", ProjectManifest = "a1b2c3d4"
    };

    /// <summary>A real host on loopback. Verification answers per account: an account with a gate waits for it, every other account verifies at once.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly SessionServer Server;
        public readonly int Port;
        public readonly ConcurrentDictionary<string, TaskCompletionSource<SteamTokenVerdict>> Gates = new();
        public Exception PumpError;
        private readonly string directory;
        private readonly List<IDisposable> owned = new();

        public Rig()
        {
            var root = Environment.GetEnvironmentVariable( "TMPDIR" );
            if ( string.IsNullOrWhiteSpace( root ) ) root = Path.GetTempPath();
            directory = Path.Combine( root, "teamcreate-coretests", Guid.NewGuid().ToString( "N" ) );
            for ( int attempt = 0; attempt < 10 && Server == null; attempt++ )
            {
                Port = FreePort();
                try
                {
                    Server = new SessionServer( "127.0.0.1", Port, "test-project", "test-baseline", "test-token", Path.Combine( directory, $"journal-{attempt}.jsonl" ), asyncVerifyVerdict:
                        hello => Gates.TryGetValue( hello.SteamId ?? "", out var gate ) ? gate.Task : Task.FromResult( SteamTokenVerdict.Verified ) );
                }
                catch ( SocketException ) { }
            }
            if ( Server == null ) throw new IOException( "Unable to reserve a disposable SessionServer port" );
        }

        /// <summary>Pumps the host once, recording (not throwing) an exception that escapes it: an escaping exception ends a real host's session.</summary>
        public void Pump()
        {
            try { Server.Pump(); }
            catch ( Exception error ) { PumpError ??= error; }
        }

        public async Task<Wire> ConnectAsync()
        {
            var wire = await Wire.Connect( "127.0.0.1", Port );
            owned.Add( wire );
            return wire;
        }

        /// <summary>Pumps until <paramref name="until"/> holds or the time runs out, collecting every packet each listed wire receives.</summary>
        public async Task<bool> PumpUntilAsync( Func<bool> until, double seconds, params (Wire Wire, List<Packet> Seen)[] wires )
        {
            var deadline = DateTime.UtcNow.AddSeconds( seconds );
            while ( DateTime.UtcNow < deadline )
            {
                Pump();
                foreach ( var (wire, seen) in wires ) while ( wire.Incoming.TryDequeue( out var p ) ) seen.Add( p );
                if ( until() ) return true;
                await Task.Delay( 10 );
            }
            return until();
        }

        public void Own( IDisposable item ) => owned.Add( item );

        public void Dispose()
        {
            foreach ( var item in owned ) { try { item.Dispose(); } catch ( Exception ) { } }
            try { Server.Dispose(); } catch ( Exception ) { }
            try { if ( Directory.Exists( directory ) ) Directory.Delete( directory, true ); } catch ( IOException ) { }
        }
    }

    /// <summary>Connects, says hello and waits for the host's answer; returns the wire and everything it received.</summary>
    private static async Task<(Wire Wire, List<Packet> Seen)> JoinAsync( Rig rig, Packet hello, double seconds = 4 )
    {
        var wire = await rig.ConnectAsync();
        var seen = new List<Packet>();
        wire.Send( hello );
        await rig.PumpUntilAsync( () => seen.Any( p => p.Kind is "welcome" or "reject" or "fatal" ), seconds, (wire, seen) );
        return (wire, seen);
    }

    /// <summary>True when the host still serves this member: a ping is answered with a pong.</summary>
    private static async Task<bool> StillServedAsync( Rig rig, Wire wire, List<Packet> seen )
    {
        if ( wire.Failure != null ) return false;
        int before = seen.Count( p => p.Kind == "pong" );
        try { wire.Send( new Packet { Kind = "ping" } ); }
        catch ( IOException ) { return false; }
        return await rig.PumpUntilAsync( () => seen.Count( p => p.Kind == "pong" ) > before, 3, (wire, seen) );
    }

    private static bool Welcomed( List<Packet> seen ) => seen.Any( p => p.Kind == "welcome" );

    /// <summary>Writes one unencrypted frame (the loopback test host has no session key) holding exactly these UTF-8 bytes.</summary>
    private static void WriteRawFrame( NetworkStream stream, string json )
    {
        byte[] body = Encoding.UTF8.GetBytes( json );
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian( frame, body.Length );
        body.CopyTo( frame, 4 );
        stream.Write( frame );
    }

    public static void Run( Action<bool, string> assert )
    {
        LeavingDuringVerificationAsync( assert ).GetAwaiter().GetResult();
        RepeatedHelloWhileVerifyingAsync( assert ).GetAwaiter().GetResult();
        SameAccountReconnectAsync( assert ).GetAwaiter().GetResult();
        PeerIdTheftAsync( assert ).GetAwaiter().GetResult();
        UnrelayablePresenceAsync( assert ).GetAwaiter().GetResult();
        OversizedFirstFrameAsync( assert ).GetAwaiter().GetResult();
        HostStallAsync( assert ).GetAwaiter().GetResult();
    }

    // A host that stalls (a hotload, a long save) must not mistake members that kept talking for silent ones: silence is counted from the
    // last frame the connection read, not the last one the host got round to handling. A member that really is silent is still removed.
    private static async Task HostStallAsync( Action<bool, string> assert )
    {
        using var rig = new Rig();
        rig.Server.SilenceLimit = TimeSpan.FromSeconds( 1 );
        var talker = await JoinAsync( rig, Hello( AccountA ) );
        var quiet = await JoinAsync( rig, Hello( AccountB ) );
        assert( Welcomed( talker.Seen ) && Welcomed( quiet.Seen ), "isolation_stall_setup_both_guests_are_admitted" );

        // The host does not pump for twice the silence limit; one guest keeps pinging throughout, the other says nothing.
        var stalled = DateTime.UtcNow.AddSeconds( 2 );
        while ( DateTime.UtcNow < stalled )
        {
            talker.Wire.Send( new Packet { Kind = "ping" } );
            await Task.Delay( 100 );
        }
        await rig.PumpUntilAsync( () => quiet.Wire.Failure != null, 3, talker, quiet );
        assert( talker.Wire.Failure == null && !talker.Seen.Any( p => p.Kind == "fatal" ), "isolation_a_member_that_kept_talking_through_a_host_stall_is_kept" );
        assert( await StillServedAsync( rig, talker.Wire, talker.Seen ), "isolation_after_a_host_stall_the_talking_member_is_still_served" );
        assert( quiet.Wire.Failure != null, "isolation_a_member_that_was_really_silent_is_still_removed" );
        assert( rig.PumpError == null, $"isolation_a_host_stall_never_escapes_the_host_pump: {rig.PumpError?.Message}" );
    }

    // A guest that disconnects while its identity is still being checked used to stay queued; when the check finished, the welcome went to a
    // closed wire and the exception escaped the host's pump, which ends a real host's session for everyone.
    private static async Task LeavingDuringVerificationAsync( Action<bool, string> assert )
    {
        using var rig = new Rig();
        var a = await JoinAsync( rig, Hello( AccountA ) );
        assert( Welcomed( a.Seen ), "isolation_verification_setup_first_guest_is_admitted" );

        var gate = new TaskCompletionSource<SteamTokenVerdict>( TaskCreationOptions.RunContinuationsAsynchronously );
        rig.Gates[AccountB] = gate;
        var b = await rig.ConnectAsync();
        b.Send( Hello( AccountB ) );
        await rig.PumpUntilAsync( () => rig.Server.PendingVerificationCount == 1, 3, a );
        assert( rig.Server.PendingVerificationCount == 1, "isolation_verification_setup_second_guest_is_held_for_verification" );

        b.Dispose();
        bool forgotten = await rig.PumpUntilAsync( () => rig.Server.PendingVerificationCount == 0, 3, a );
        assert( forgotten, "isolation_a_guest_that_leaves_during_verification_is_forgotten" );

        gate.SetResult( SteamTokenVerdict.Verified );
        await rig.PumpUntilAsync( () => false, 0.5, a );
        assert( rig.PumpError == null, $"isolation_a_late_verdict_for_a_departed_guest_never_escapes_the_host_pump: {rig.PumpError?.Message}" );
        assert( await StillServedAsync( rig, a.Wire, a.Seen ), "isolation_a_late_verdict_for_a_departed_guest_costs_the_others_nothing" );
    }

    // A second hello from a guest whose first is still being verified is a retry, not a flood: it must not be refused as one.
    private static async Task RepeatedHelloWhileVerifyingAsync( Action<bool, string> assert )
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource<SteamTokenVerdict>( TaskCreationOptions.RunContinuationsAsynchronously );
        rig.Gates[AccountA] = gate;
        var wire = await rig.ConnectAsync();
        var seen = new List<Packet>();
        var hello = Hello( AccountA );
        wire.Send( hello );
        await rig.PumpUntilAsync( () => rig.Server.PendingVerificationCount == 1, 3, (wire, seen) );
        wire.Send( hello );
        await rig.PumpUntilAsync( () => seen.Count > 0, 0.5, (wire, seen) );
        gate.SetResult( SteamTokenVerdict.Verified );
        await rig.PumpUntilAsync( () => seen.Any( p => p.Kind is "welcome" or "fatal" or "reject" ), 3, (wire, seen) );
        assert( Welcomed( seen ) && !seen.Any( p => p.Kind is "fatal" or "reject" ),
            $"isolation_a_repeated_hello_during_verification_is_not_refused: {string.Join( ",", seen.Select( p => p.Kind + ":" + p.Error ) )}" );
        assert( rig.PumpError == null, $"isolation_repeated_hello_never_escapes_the_host_pump: {rig.PumpError?.Message}" );
    }

    // An editor that restarts (a hotload or a crash) joins with a new collaborator ID while the host still holds its old, half-open connection.
    // A verified account takes over its own old connection at once instead of being refused as a duplicate until the old one times out.
    private static async Task SameAccountReconnectAsync( Action<bool, string> assert )
    {
        using var rig = new Rig();
        var witness = await JoinAsync( rig, Hello( AccountB ) );
        var first = await JoinAsync( rig, Hello( AccountA ) );
        assert( Welcomed( first.Seen ) && Welcomed( witness.Seen ), "isolation_reconnect_setup_both_guests_are_admitted" );

        var second = await JoinAsync( rig, Hello( AccountA ) );
        assert( Welcomed( second.Seen ), $"isolation_the_same_verified_account_reconnecting_with_a_new_id_is_admitted_at_once: {string.Join( ",", second.Seen.Select( p => p.Kind + ":" + p.Error ) )}" );
        bool oldClosed = await rig.PumpUntilAsync( () => first.Wire.Failure != null, 3, first, witness, second );
        assert( oldClosed, "isolation_the_replaced_connection_of_that_account_is_closed" );
        assert( await StillServedAsync( rig, second.Wire, second.Seen ), "isolation_the_reconnected_editor_is_served" );
        assert( await StillServedAsync( rig, witness.Wire, witness.Seen ), "isolation_a_reconnect_costs_the_other_guests_nothing" );

        // The same editor again under the same ID: the existing replacement path keeps working.
        string peer = Guid.NewGuid().ToString();
        var third = await JoinAsync( rig, Hello( AccountA, peer ) );
        var fourth = await JoinAsync( rig, Hello( AccountA, peer ) );
        assert( Welcomed( third.Seen ) && Welcomed( fourth.Seen ), "isolation_the_same_account_and_id_reconnecting_is_admitted" );
        assert( rig.PumpError == null, $"isolation_reconnects_never_escape_the_host_pump: {rig.PumpError?.Message}" );
    }

    // Collaborator IDs are visible to every member (presence carries them). Another account claiming one must be refused, not allowed to
    // displace its owner.
    private static async Task PeerIdTheftAsync( Action<bool, string> assert )
    {
        using var rig = new Rig();
        string peer = Guid.NewGuid().ToString();
        var owner = await JoinAsync( rig, Hello( AccountA, peer ) );
        assert( Welcomed( owner.Seen ), "isolation_theft_setup_owner_is_admitted" );
        var thief = await JoinAsync( rig, Hello( AccountB, peer ) );
        assert( !Welcomed( thief.Seen ) && thief.Seen.Any( p => p.Kind is "reject" or "fatal" ), "isolation_another_account_claiming_a_members_id_is_refused" );
        assert( await StillServedAsync( rig, owner.Wire, owner.Seen ), "isolation_the_owner_of_a_claimed_id_keeps_its_session" );
    }

    // A presence whose clock stamp cannot be written back out (1e999 reads as infinity) used to fail the broadcast to every recipient, removing
    // all of them. It is now refused once, before fan-out, and only the sender pays.
    private static async Task UnrelayablePresenceAsync( Action<bool, string> assert )
    {
        using var rig = new Rig();
        var witness = await JoinAsync( rig, Hello( AccountB ) );
        var tcp = new TcpClient();
        rig.Own( tcp );
        await tcp.ConnectAsync( IPAddress.Loopback, rig.Port );
        var stream = tcp.GetStream();
        string peer = Guid.NewGuid().ToString();
        WriteRawFrame( stream, System.Text.Json.JsonSerializer.Serialize( Hello( AccountA, peer ) ) );
        await rig.PumpUntilAsync( () => tcp.Available > 0, 3, witness );
        assert( tcp.Available > 0, "isolation_unrelayable_setup_raw_guest_is_answered" );
        WriteRawFrame( stream, "{\"Kind\":\"presence\",\"Peer\":\"" + peer + "\",\"Scene\":\"scenes/minimal.scene\",\"SentMs\":1e999}" );
        await rig.PumpUntilAsync( () => false, 0.5, witness );
        assert( rig.PumpError == null, $"isolation_unrelayable_presence_never_escapes_the_host_pump: {rig.PumpError?.Message}" );
        assert( witness.Wire.Failure == null && await StillServedAsync( rig, witness.Wire, witness.Seen ), "isolation_an_unrelayable_presence_costs_the_recipients_nothing" );
        assert( !witness.Seen.Any( p => p.Kind == "left" && p.Peer != peer ) && !witness.Seen.Any( p => p.Kind == "fatal" ), "isolation_recipients_of_an_unrelayable_presence_are_not_removed" );
    }

    // Before a hello, a connection is nobody: it must not make the host reserve a full 8 MiB frame, or wait the full silence limit for it.
    private static async Task OversizedFirstFrameAsync( Action<bool, string> assert )
    {
        using var rig = new Rig();
        var tcp = new TcpClient();
        rig.Own( tcp );
        await tcp.ConnectAsync( IPAddress.Loopback, rig.Port );
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian( header, 4 * 1024 * 1024 );
        tcp.GetStream().Write( header );
        bool closed = await rig.PumpUntilAsync( () =>
        {
            try
            {
                if ( !tcp.Client.Poll( 0, SelectMode.SelectRead ) ) return false;
                return tcp.Client.Available == 0;
            }
            catch ( Exception ) { return true; }
        }, 3 );
        assert( closed, "isolation_an_oversized_frame_before_the_hello_closes_that_connection_at_once" );
        assert( rig.PumpError == null, $"isolation_oversized_first_frame_never_escapes_the_host_pump: {rig.PumpError?.Message}" );
    }
}
