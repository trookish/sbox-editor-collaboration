using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using TeamCreate;

/// <summary>
/// Drift detection: the pure DriftDetector rules, then a real SessionServer driven over loopback by a fake client that reports whatever
/// digest the test wants, so the host's reaction (one snapshot, then quiet) is observed on the wire rather than assumed.
/// </summary>
static class DriftTests
{
    private const string Scene = "scenes/drift.scene";

    private const string OtherScene = "scenes/other.scene";

    private const string Bad = "deadbeefdeadbeef";

    private const string Good = "0123456789abcdef";

    /// <summary>Feeds one ping that reports a digest differing from the host's, at equal revisions.</summary>
    private static DriftVerdict Mismatch( DriftDetector d, double now, string member = "m1", string scene = Scene )
        => d.ShouldResync( member, scene, 7, Bad, 7, Good, now );

    private static DriftVerdict Match( DriftDetector d, double now, string member = "m1" )
        => d.ShouldResync( member, Scene, 7, Good, 7, Good, now );

    /// <summary>Two consecutive mismatches at one moment: the first must stay quiet, the second is the verdict.</summary>
    private static DriftVerdict TwoMismatches( DriftDetector d, double now, Action<string> fail, string member = "m1" )
    {
        var first = Mismatch( d, now, member );
        if ( first != DriftVerdict.None ) fail( "the first mismatch of a streak returned " + first );
        return Mismatch( d, now + 0.1, member );
    }

    public static void Run( Action<bool, string> assert )
    {
        DetectorRules( assert );
        ServerBehaviour( assert );
    }

    private static void DetectorRules( Action<bool, string> assert )
    {
        // ---- equal digests never act ----
        {
            var d = new DriftDetector();
            var all = Enumerable.Range( 0, 10 ).Select( i => Match( d, i ) ).All( v => v == DriftVerdict.None );
            assert( all, "drift_equal_digests_never_resync" );
        }

        // ---- one mismatch is not enough: a ping can cross an edit ----
        {
            var d = new DriftDetector();
            assert( Mismatch( d, 0 ) == DriftVerdict.None, "drift_single_mismatch_is_quiet" );
        }
        {
            var d = new DriftDetector();
            Mismatch( d, 0 );
            assert( Mismatch( d, 2 ) == DriftVerdict.Resync, "drift_second_consecutive_mismatch_resyncs" );
        }

        // ---- the streak must be consecutive: a matching ping in between starts over ----
        {
            var d = new DriftDetector();
            Mismatch( d, 0 );
            Match( d, 2 );
            assert( Mismatch( d, 4 ) == DriftVerdict.None, "drift_match_between_mismatches_resets_streak" );
            assert( Mismatch( d, 6 ) == DriftVerdict.Resync, "drift_streak_counts_again_after_reset" );
        }

        // ---- the revision gate: a difference while edits are in flight is normal ----
        {
            var d = new DriftDetector();
            var verdicts = new List<DriftVerdict>();
            for ( int i = 0; i < 6; i++ )
            {
                // The client is one revision behind the host on every ping.
                verdicts.Add( d.ShouldResync( "m1", Scene, 6, Bad, 7, Good, i * 2 ) );
            }
            assert( verdicts.All( v => v == DriftVerdict.None ), "drift_differing_revisions_never_resync" );
        }
        {
            var d = new DriftDetector();
            Mismatch( d, 0 );
            d.ShouldResync( "m1", Scene, 6, Bad, 7, Good, 2 );
            assert( Mismatch( d, 4 ) == DriftVerdict.None, "drift_revision_gap_resets_streak" );
        }

        // ---- missing information never acts ----
        {
            var d = new DriftDetector();
            Mismatch( d, 0 );
            var v1 = d.ShouldResync( "m1", Scene, 7, null, 7, Good, 2 );
            var v2 = d.ShouldResync( "m1", Scene, 7, "", 7, Good, 4 );
            var v3 = d.ShouldResync( "m1", Scene, 7, Bad, 7, null, 6 );
            var v4 = d.ShouldResync( "m1", Scene, 7, Bad, 7, "", 8 );
            assert( v1 == DriftVerdict.None && v2 == DriftVerdict.None && v3 == DriftVerdict.None && v4 == DriftVerdict.None, "drift_missing_digest_is_none" );
            assert( Mismatch( d, 10 ) == DriftVerdict.None, "drift_missing_digest_resets_streak" );
        }

        // ---- a streak belongs to one scene of one member ----
        {
            var d = new DriftDetector();
            Mismatch( d, 0, "m1", Scene );
            assert( Mismatch( d, 2, "m1", OtherScene ) == DriftVerdict.None, "drift_scene_change_resets_streak" );
        }
        {
            var d = new DriftDetector();
            Mismatch( d, 0, "m1" );
            assert( Mismatch( d, 2, "m2" ) == DriftVerdict.None, "drift_streaks_are_per_member" );
        }
        {
            var d = new DriftDetector();
            Mismatch( d, 0 );
            d.Forget( "m1" );
            assert( Mismatch( d, 2 ) == DriftVerdict.None, "drift_forget_clears_streak" );
        }

        // ---- the rate cap: at most one resync per 10 s per member ----
        {
            var d = new DriftDetector();
            string failure = null;
            var first = TwoMismatches( d, 0, m => failure = m );
            assert( first == DriftVerdict.Resync && failure == null, "drift_first_resync_is_immediate" );
            // The member keeps drifting. Each of these would be a resync but for the cap.
            var capped = new List<DriftVerdict> { Mismatch( d, 1 ), Mismatch( d, 3 ), Mismatch( d, 5 ), Mismatch( d, 9.9 ) };
            assert( capped.All( v => v == DriftVerdict.None ), "drift_rate_cap_blocks_second_resync_within_ten_seconds" );
            assert( Mismatch( d, 10.5 ) == DriftVerdict.Resync, "drift_rate_cap_releases_after_ten_seconds" );
        }
        {
            var d = new DriftDetector();
            TwoMismatches( d, 0, _ => { } );
            // Another member is not held by this member's cap.
            Mismatch( d, 1, "m2" );
            assert( Mismatch( d, 2, "m2" ) == DriftVerdict.Resync, "drift_rate_cap_is_per_member" );
        }

        // ---- persistence: after five resyncs in five minutes the host is told, not looped ----
        {
            var d = new DriftDetector();
            string failure = null;
            var resyncs = 0;
            for ( int i = 0; i < 5; i++ )
            {
                if ( TwoMismatches( d, i * 10.2, m => failure = m ) == DriftVerdict.Resync ) resyncs++;
            }
            assert( resyncs == 5 && failure == null, "drift_five_resyncs_are_still_resyncs" );
            assert( TwoMismatches( d, 51, _ => { } ) == DriftVerdict.Persistent, "drift_sixth_attempt_in_window_is_persistent" );
            assert( TwoMismatches( d, 53, _ => { } ) == DriftVerdict.Persistent, "drift_persistent_stays_persistent_while_drifting" );
            // The oldest resync was at t=0; at t=299 all five are still inside the five-minute window.
            assert( TwoMismatches( d, 299, _ => { } ) == DriftVerdict.Persistent, "drift_persistent_inside_window" );
            // The last of them was at t=40.8, so by t=345 every one has aged out and the member gets a fresh start.
            assert( TwoMismatches( d, 345, _ => { } ) == DriftVerdict.Resync, "drift_window_expiry_allows_resync_again" );
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // A real SessionServer over loopback.

    private static string Guid( int n ) => "00000000-0000-0000-0000-" + n.ToString( "D12" );

    private static ObjectState Cube( int n, string name )
        => new( Guid( n ), "", new JsonObject
        {
            ["__guid"] = Guid( n ),
            ["Name"] = name,
            ["Position"] = "0,0,0",
            ["Rotation"] = "0,0,0,1",
            ["Scale"] = "1,1,1",
            ["Enabled"] = true,
            ["Tags"] = "",
            ["Components"] = new JsonArray()
        } );

    private static List<ObjectState> SeedObjects() => new() { Cube( 1, "Floor" ), Cube( 2, "Lamp" ) };

    private static string DigestOf( IEnumerable<ObjectState> objects ) => SceneDigest.Compute( StateRules.Map( objects ) );

    private sealed class Rig : IDisposable
    {
        public SessionServer Server;
        public Wire Client;
        public double Now;
        public string Directory;
        public string Peer = System.Guid.NewGuid().ToString();
        public List<Packet> Seen = new();

        public void Dispose()
        {
            Client?.Dispose();
            Server?.Dispose();
            try { if ( System.IO.Directory.Exists( Directory ) ) System.IO.Directory.Delete( Directory, true ); }
            catch ( IOException ) { }
        }

        public void PumpFor( Func<bool> done, int milliseconds = 5000 )
        {
            var deadline = DateTime.UtcNow.AddMilliseconds( milliseconds );
            while ( DateTime.UtcNow < deadline )
            {
                Server.Pump();
                while ( Client.Incoming.TryDequeue( out var packet ) ) Seen.Add( packet );
                if ( done() ) return;
                Thread.Sleep( 5 );
            }
            throw new TimeoutException( "The fake client did not receive the expected packets from the host." );
        }

        /// <summary>
        /// Sends one ping, then a plain sentinel ping, and waits for both pongs. The wire is ordered and the host answers in order, so
        /// everything the host sent because of the first ping has arrived by the time the sentinel's pong does.
        /// </summary>
        public List<Packet> Ping( string digest, long? revision, string scene )
        {
            var before = Seen.Count;
            var expectedPongs = Seen.Count( p => p.Kind == "pong" ) + 2;
            Client.Send( new Packet { Kind = "ping", Digest = digest, DigestRevision = revision, DigestScene = scene } );
            Client.Send( new Packet { Kind = "ping" } );
            PumpFor( () => Seen.Count( p => p.Kind == "pong" ) >= expectedPongs );
            return Seen.Skip( before ).ToList();
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener( IPAddress.Loopback, 0 );
        listener.Start();
        var port = ( (IPEndPoint)listener.LocalEndpoint ).Port;
        listener.Stop();
        return port;
    }

    private static Rig Start()
    {
        var rig = new Rig();
        var root = Environment.GetEnvironmentVariable( "TMPDIR" );
        if ( string.IsNullOrWhiteSpace( root ) ) root = Path.GetTempPath();
        rig.Directory = Path.Combine( root, "teamcreate-coretests", System.Guid.NewGuid().ToString( "N" ) );
        Exception last = null;
        var port = 0;
        for ( int attempt = 0; attempt < 10 && rig.Server == null; attempt++ )
        {
            try
            {
                port = GetFreePort();
                rig.Server = new SessionServer( "127.0.0.1", port, "test-project", "test-baseline", "test-token", Path.Combine( rig.Directory, $"journal-{attempt}.jsonl" ) );
            }
            catch ( SocketException error ) { last = error; }
        }
        if ( rig.Server == null ) throw new IOException( "Unable to reserve a loopback port for the drift tests.", last );
        rig.Server.AllowUnverifiedPeers = true;
        rig.Server.DriftClock = () => rig.Now;
        rig.Client = Wire.Connect( "127.0.0.1", port ).GetAwaiter().GetResult();
        rig.Client.Send( new Packet
        {
            Kind = "hello", Peer = rig.Peer, ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source,
            Name = "Drifter", Token = "test-token", Project = "test-project", Baseline = "test-baseline",
            SteamId = "76561198000000001", SteamToken = "a-realistic-session-token", OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "a1b2c3d4"
        } );
        rig.PumpFor( () => rig.Seen.Any( p => p.Kind == "welcome" ) );
        rig.Client.Send( new Packet { Kind = "subscribe", Scene = Scene, Objects = SeedObjects() } );
        rig.PumpFor( () => rig.Seen.Any( p => p.Kind == "snapshot" ) );
        rig.Seen.Clear();
        return rig;
    }

    private static int Snapshots( List<Packet> packets ) => packets.Count( p => p.Kind == "snapshot" );

    private static void ServerBehaviour( Action<bool, string> assert )
    {
        var hostDigest = DigestOf( SeedObjects() );

        // ---- two corrupt digests at equal revisions: exactly one snapshot, none once the digest matches again ----
        using ( var rig = Start() )
        {
            var first = rig.Ping( Bad, 0, Scene );
            assert( Snapshots( first ) == 0 && first.Count( p => p.Kind == "pong" ) == 2, "drift_server_first_mismatch_sends_pong_only" );

            var second = rig.Ping( Bad, 0, Scene );
            var snapshot = second.FirstOrDefault( p => p.Kind == "snapshot" );
            assert( Snapshots( second ) == 1 && snapshot.Scene == Scene && snapshot.Objects.Count == 2 && snapshot.Revision == 0, "drift_server_second_mismatch_sends_one_snapshot_of_the_scene" );
            assert( second.Count( p => p.Kind == "pong" ) == 2, "drift_server_pong_is_unchanged_when_resyncing" );
            assert( DigestOf( snapshot.Objects ) == hostDigest, "drift_server_snapshot_carries_the_hosts_scene" );

            var afterFix = new List<Packet>();
            for ( int i = 0; i < 3; i++ ) afterFix.AddRange( rig.Ping( hostDigest, 0, Scene ) );
            assert( Snapshots( afterFix ) == 0, "drift_server_no_snapshot_once_digest_matches" );
            assert( rig.Server.DriftNotice == null, "drift_server_no_notice_for_ordinary_drift" );
        }

        // ---- differing revisions never resync, however often the digest differs ----
        using ( var rig = Start() )
        {
            var all = new List<Packet>();
            for ( int i = 0; i < 6; i++ ) all.AddRange( rig.Ping( Bad, 5, Scene ) );
            assert( Snapshots( all ) == 0 && all.Count( p => p.Kind == "pong" ) == 12, "drift_server_differing_revisions_never_resync" );
        }

        // ---- the revision gate works against a real revision, not just zero ----
        using ( var rig = Start() )
        {
            var edit = new Packet
            {
                Kind = "edit", Scene = Scene, Final = true,
                Changes = new List<Change> { new( Guid( 2 ), Cube( 2, "Lamp" ), Cube( 2, "Lamp renamed" ) ) }
            };
            rig.Client.Send( edit );
            rig.PumpFor( () => rig.Seen.Any( p => p.Kind == "accepted" ) );
            var accepted = rig.Seen.First( p => p.Kind == "accepted" );
            var afterEdit = DigestOf( new[] { Cube( 1, "Floor" ), Cube( 2, "Lamp renamed" ) } );
            rig.Seen.Clear();

            var stale = new List<Packet>();
            for ( int i = 0; i < 4; i++ ) stale.AddRange( rig.Ping( Bad, accepted.Revision - 1, Scene ) );
            assert( Snapshots( stale ) == 0, "drift_server_client_behind_the_host_is_not_drift" );

            var equal = new List<Packet>();
            for ( int i = 0; i < 2; i++ ) equal.AddRange( rig.Ping( Bad, accepted.Revision, Scene ) );
            var snapshot = equal.FirstOrDefault( p => p.Kind == "snapshot" );
            assert( Snapshots( equal ) == 1 && snapshot.Revision == accepted.Revision && DigestOf( snapshot.Objects ) == afterEdit, "drift_server_resync_carries_the_edited_scene_at_the_current_revision" );
        }

        // ---- old-style and malformed pings still get their pong and never a snapshot ----
        using ( var rig = Start() )
        {
            var old = rig.Ping( null, null, null );
            assert( old.Count( p => p.Kind == "pong" ) == 2 && Snapshots( old ) == 0, "drift_server_old_style_ping_still_gets_its_pong" );

            var partial = new List<Packet>();
            for ( int i = 0; i < 3; i++ ) partial.AddRange( rig.Ping( Bad, null, Scene ) );
            for ( int i = 0; i < 3; i++ ) partial.AddRange( rig.Ping( null, 0, Scene ) );
            for ( int i = 0; i < 3; i++ ) partial.AddRange( rig.Ping( Bad, 0, null ) );
            assert( Snapshots( partial ) == 0 && partial.Count( p => p.Kind == "pong" ) == 18, "drift_server_ping_missing_a_field_does_nothing" );

            var unknown = new List<Packet>();
            for ( int i = 0; i < 3; i++ ) unknown.AddRange( rig.Ping( Bad, 0, "scenes/never-subscribed.scene" ) );
            assert( Snapshots( unknown ) == 0 && unknown.Count( p => p.Kind == "pong" ) == 6, "drift_server_ping_for_unknown_scene_gets_a_pong_only" );
            assert( !rig.Seen.Any( p => p.Kind == "fatal" ), "drift_server_ping_for_unknown_scene_does_not_fail_the_session" );
        }

        // ---- a malformed scene on the host side cannot break ping handling: that is the host's own state, so it must not throw at a guest ----
        // (The host only ever stores validated scenes, so this is pinned at the digest level: Compute refuses a malformed scene.)
        {
            var refused = false;
            try { SceneDigest.Compute( new Dictionary<string, ObjectState> { ["x"] = null } ); }
            catch ( ArgumentException ) { refused = true; }
            assert( refused, "drift_digest_of_malformed_scene_is_refused_so_callers_must_catch" );
        }

        // ---- the rate cap on the wire ----
        using ( var rig = Start() )
        {
            rig.Now = 0;
            rig.Ping( Bad, 0, Scene );
            var resync = rig.Ping( Bad, 0, Scene );
            assert( Snapshots( resync ) == 1, "drift_server_cap_setup_first_resync" );

            var capped = new List<Packet>();
            rig.Now = 1;
            for ( int i = 0; i < 4; i++ ) capped.AddRange( rig.Ping( Bad, 0, Scene ) );
            assert( Snapshots( capped ) == 0, "drift_server_resync_at_most_once_per_ten_seconds" );

            rig.Now = 11;
            var later = rig.Ping( Bad, 0, Scene );
            assert( Snapshots( later ) == 1, "drift_server_resync_allowed_again_after_ten_seconds" );
        }

        // ---- persistent drift raises a notice instead of looping ----
        using ( var rig = Start() )
        {
            for ( int i = 0; i < 5; i++ )
            {
                rig.Now = i * 11;
                rig.Ping( Bad, 0, Scene );
                var resync = rig.Ping( Bad, 0, Scene );
                if ( Snapshots( resync ) != 1 ) throw new InvalidOperationException( "Resync " + ( i + 1 ) + " of 5 did not arrive." );
            }
            assert( rig.Server.DriftNotice == null, "drift_server_no_notice_before_the_fifth_resync_has_failed" );

            rig.Now = 55;
            rig.Ping( Bad, 0, Scene );
            var sixth = rig.Ping( Bad, 0, Scene );
            assert( Snapshots( sixth ) == 0, "drift_server_persistent_drift_sends_no_new_snapshot" );
            assert( !string.IsNullOrWhiteSpace( rig.Server.DriftNotice ) && rig.Server.DriftNotice.Contains( "Drifter" ) && rig.Server.DriftNotice.Contains( Scene ), "drift_server_persistent_drift_raises_a_notice_naming_the_member_and_scene" );

            // Once the member agrees with the host again the notice has nothing left to say.
            rig.Ping( hostDigest, 0, Scene );
            assert( rig.Server.DriftNotice == null, "drift_server_notice_clears_when_the_member_matches_again" );
        }

        // ---- the host hashes a scene once per revision, however many pings ask ----
        using ( var rig = Start() )
        {
            for ( int i = 0; i < 6; i++ ) rig.Ping( hostDigest, 0, Scene );
            assert( rig.Server.HostDigestComputations == 1, "drift_server_digest_is_cached_per_scene_and_revision" );

            rig.Client.Send( new Packet
            {
                Kind = "edit", Scene = Scene, Final = true,
                Changes = new List<Change> { new( Guid( 2 ), Cube( 2, "Lamp" ), Cube( 2, "Lamp moved" ) ) }
            } );
            rig.PumpFor( () => rig.Seen.Any( p => p.Kind == "accepted" ) );
            var revision = rig.Seen.First( p => p.Kind == "accepted" ).Revision;
            rig.Ping( hostDigest, revision, Scene );
            rig.Ping( hostDigest, revision, Scene );
            assert( rig.Server.HostDigestComputations == 2, "drift_server_digest_is_recomputed_when_the_revision_moves" );
        }
    }
}
