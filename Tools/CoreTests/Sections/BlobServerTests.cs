using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using TeamCreate;

/// <summary>
/// The geometry channel against a real SessionServer over loopback, with two fake editors that each hold their own BlobStore and
/// BlobExchange exactly as the editor does. Observes on the wire that the host holds an edit until the geometry arrived, that a second
/// editor then receives the edit and fetches the bytes from the host, and that a missing blob is refused rather than hanging.
/// </summary>
static class BlobServerTests
{
    private const string Scene = "scenes/blobs.scene";

    private static string Guid( int n ) => "00000000-0000-0000-0000-" + n.ToString( "D12" );

    private static ObjectState Plain( int n, string name )
        => new( Guid( n ), "", new JsonObject
        {
            ["__guid"] = Guid( n ),
            ["Name"] = name,
            ["Components"] = new JsonArray()
        } );

    private static ObjectState Mesh( int n, string name, string hash )
        => new( Guid( n ), "", new JsonObject
        {
            ["__guid"] = Guid( n ),
            ["Name"] = name,
            ["Components"] = new JsonArray
            {
                new JsonObject
                {
                    ["__type"] = "Sandbox.MeshComponent",
                    ["__guid"] = "2004dcd3-12a3-4338-a48e-d35c734d06ea",
                    ["Mesh"] = new JsonObject { ["Data"] = BlobRules.Placeholder( hash ) }
                }
            }
        } );

    private sealed class Editor : IDisposable
    {
        public Wire Wire;
        public readonly BlobStore Store = new( null, 64L * 1024 * 1024, 0 );
        public BlobExchange Exchange;
        public readonly List<Packet> Seen = new();
        public readonly string Peer = System.Guid.NewGuid().ToString();

        public void Pump()
        {
            Exchange.Pump();
            Drain();
            while ( Exchange.TryTakeReady( out Packet ready ) ) Seen.Add( ready );
        }

        private void Drain()
        {
            while ( Wire.Incoming.TryDequeue( out Packet packet ) )
            {
                if ( BlobExchange.IsBlobPacket( packet.Kind ) ) Exchange.Handle( packet );
                else if ( Exchange.Offer( packet ) == BlobOffer.Process ) Seen.Add( packet );
            }
        }

        public void Dispose() => Wire?.Dispose();
    }

    private sealed class Rig : IDisposable
    {
        public SessionServer Server;
        public readonly BlobStore HostStore = new( null, 64L * 1024 * 1024, 0 );
        public readonly List<Editor> Editors = new();
        public string Directory;

        public void PumpFor( Func<bool> done, int milliseconds = 5000 )
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds( milliseconds );
            while ( DateTime.UtcNow < deadline )
            {
                Server.Pump();
                foreach ( Editor editor in Editors ) editor.Pump();
                if ( done() ) return;
                Thread.Sleep( 2 );
            }
            throw new TimeoutException( "The fake editors did not receive the expected packets from the host." );
        }

        // Runs the pump for a fixed time and reports nothing: used to prove that something does NOT happen.
        public void Settle( int milliseconds = 300 )
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds( milliseconds );
            while ( DateTime.UtcNow < deadline )
            {
                Server.Pump();
                foreach ( Editor editor in Editors ) editor.Pump();
                Thread.Sleep( 2 );
            }
        }

        public Editor Join( string name, int steam, int port )
        {
            Editor editor = new Editor();
            editor.Wire = Wire.Connect( "127.0.0.1", port ).GetAwaiter().GetResult();
            editor.Exchange = new BlobExchange( editor.Store, p => editor.Wire.Send( p ), () => DateTime.UtcNow );
            editor.Wire.Send( new Packet
            {
                Kind = "hello", Peer = editor.Peer, ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source,
                Name = name, Token = "test-token", Project = "test-project", Baseline = "test-baseline",
                SteamId = "7656119800000000" + steam, SteamToken = "a-realistic-session-token", OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "a1b2c3d4"
            } );
            Editors.Add( editor );
            PumpFor( () => editor.Seen.Any( p => p.Kind == "welcome" ) );
            return editor;
        }

        public void Dispose()
        {
            foreach ( Editor editor in Editors ) editor.Dispose();
            Server?.Dispose();
            try { if ( System.IO.Directory.Exists( Directory ) ) System.IO.Directory.Delete( Directory, true ); }
            catch ( IOException ) { }
        }
    }

    private static int FreePort()
    {
        TcpListener listener = new TcpListener( IPAddress.Loopback, 0 );
        listener.Start();
        int port = ( (IPEndPoint)listener.LocalEndpoint ).Port;
        listener.Stop();
        return port;
    }

    private static (Rig Rig, int Port) Start( bool withStore = true )
    {
        Rig rig = new Rig();
        string root = Environment.GetEnvironmentVariable( "TMPDIR" );
        if ( string.IsNullOrWhiteSpace( root ) ) root = Path.GetTempPath();
        rig.Directory = Path.Combine( root, "teamcreate-coretests", System.Guid.NewGuid().ToString( "N" ) );
        Exception last = null;
        int port = 0;
        for ( int attempt = 0; attempt < 10 && rig.Server == null; attempt++ )
        {
            try
            {
                port = FreePort();
                rig.Server = new SessionServer( "127.0.0.1", port, "test-project", "test-baseline", "test-token", Path.Combine( rig.Directory, $"journal-{attempt}.jsonl" ) );
            }
            catch ( SocketException error ) { last = error; }
        }
        if ( rig.Server == null ) throw new IOException( "Unable to reserve a loopback port for the blob tests.", last );
        rig.Server.AllowUnverifiedPeers = true;
        if ( withStore ) rig.Server.BlobStore = rig.HostStore;
        return (rig, port);
    }

    public static void Run( Action<bool, string> assert )
    {
        // ---- a second editor receives an edit whose geometry only the first editor holds ----
        {
            ( Rig rig, int port ) = Start();
            using ( rig )
            {
                Editor a = rig.Join( "Mapper", 1, port );
                Editor b = rig.Join( "Watcher", 2, port );
                assert( a.Seen.First( p => p.Kind == "welcome" ).Features?.Contains( BlobLimits.Feature ) == true, "blobserver_welcome_advertises_the_geometry_feature" );

                a.Wire.Send( new Packet { Kind = "subscribe", Scene = Scene, Objects = new List<ObjectState> { Plain( 1, "Floor" ) } } );
                b.Wire.Send( new Packet { Kind = "subscribe", Scene = Scene, Objects = new List<ObjectState> { Plain( 1, "Floor" ) } } );
                rig.PumpFor( () => a.Seen.Any( p => p.Kind == "snapshot" ) && b.Seen.Any( p => p.Kind == "snapshot" ) );

                byte[] geometry = new byte[700 * 1024];
                new Random( 7 ).NextBytes( geometry );
                string hash = a.Store.Put( geometry, out _ );
                a.Seen.Clear();
                b.Seen.Clear();
                a.Wire.Send( new Packet { Kind = "edit", Scene = Scene, Final = true, Changes = new List<Change> { new( Guid( 2 ), null, Mesh( 2, "Roof", hash ) ) } } );

                rig.PumpFor( () => b.Seen.Any( p => p.Kind == "accepted" ) && a.Seen.Any( p => p.Kind == "accepted" ) );
                Packet received = b.Seen.First( p => p.Kind == "accepted" );
                assert( received.Changes.Count == 1 && received.Changes[0].After.Data["Components"][0]["Mesh"]["Data"].GetValue<string>() == BlobRules.Placeholder( hash ), "blobserver_the_edit_reaches_the_other_editor_as_a_placeholder" );
                assert( rig.HostStore.Has( hash ) && b.Store.TryGet( hash, out byte[] got ) && got.SequenceEqual( geometry ), "blobserver_the_geometry_reaches_the_other_editor_intact_through_the_host" );
                assert( b.Seen.IndexOf( received ) >= 0 && b.Store.Has( hash ), "blobserver_the_edit_is_released_only_after_its_geometry_arrived" );
            }
        }

        // ---- the host holds an edit until the geometry arrives: an editor that never answers gets no acceptance ----
        {
            ( Rig rig, int port ) = Start();
            using ( rig )
            {
                Editor a = rig.Join( "Mapper", 1, port );
                Editor b = rig.Join( "Watcher", 2, port );
                a.Wire.Send( new Packet { Kind = "subscribe", Scene = Scene, Objects = new List<ObjectState> { Plain( 1, "Floor" ) } } );
                rig.PumpFor( () => a.Seen.Any( p => p.Kind == "snapshot" ) );
                a.Seen.Clear();
                b.Seen.Clear();

                // The sender names geometry it does not hold (it cannot serve it) and sends presence right behind the edit.
                string missing = new string( 'c', 64 );
                a.Wire.Send( new Packet { Kind = "edit", Scene = Scene, Final = true, Changes = new List<Change> { new( Guid( 2 ), null, Mesh( 2, "Roof", missing ) ) } } );
                a.Wire.Send( new Packet { Kind = "presence", Peer = a.Peer, Name = "Mapper" } );
                rig.PumpFor( () => b.Seen.Any( p => p.Kind == "presence" ) );
                assert( !b.Seen.Any( p => p.Kind == "accepted" ), "blobserver_an_edit_naming_geometry_the_sender_lacks_is_not_accepted" );
                assert( b.Seen.Any( p => p.Kind == "presence" ), "blobserver_presence_is_not_held_behind_a_waiting_edit" );

                // The sender answers "unavailable" (it holds nothing): the host rejects the edit at once instead of waiting for the timeout.
                rig.PumpFor( () => a.Seen.Any( p => p.Kind == "reject" ) );
                Packet reject = a.Seen.First( p => p.Kind == "reject" );
                assert( reject.Scene == Scene && reject.Error != null && reject.Error.Contains( "mesh geometry" ) && rig.Server.BlobNotice != null, "blobserver_a_blob_the_sender_cannot_supply_rejects_the_edit_with_a_formal_reason" );
                assert( !b.Seen.Any( p => p.Kind == "accepted" ), "blobserver_a_rejected_edit_is_never_broadcast" );
            }
        }

        // ---- a host without a store does not advertise the feature and does not hold anything ----
        {
            ( Rig rig, int port ) = Start( withStore: false );
            using ( rig )
            {
                Editor a = rig.Join( "Mapper", 1, port );
                assert( a.Seen.First( p => p.Kind == "welcome" ).Features?.Contains( BlobLimits.Feature ) != true, "blobserver_a_host_without_a_store_does_not_advertise_the_feature" );
            }
        }
    }
}
