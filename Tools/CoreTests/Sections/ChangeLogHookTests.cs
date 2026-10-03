using System.Net;
using System.Net.Sockets;
using System.Text;
using TeamCreate;

/// <summary>
/// The recording hooks, end to end: a real SessionServer and two real FileSyncClients over loopback, each with its own change log, and what
/// each log says after one member adds, edits and deletes project files. Everything touches only a temporary folder.
/// </summary>
static class ChangeLogHookTests
{
    private const string Engine = "engine-fingerprint-hook";

    private sealed class Member
    {
        public Wire Wire;

        public FileSyncClient Client;

        public SessionChangeLog Log = new();
    }

    public static void Run( Action<bool, string> assert )
    {
        string root = Path.Combine( Environment.GetEnvironmentVariable( "TMPDIR" ) is { Length: > 0 } t ? t : Path.GetTempPath(), "teamcreate-coretests", "changelog-" + Guid.NewGuid().ToString( "N" ) );
        string host = Path.Combine( root, "host" );
        string[] copies = { Path.Combine( root, "a" ), Path.Combine( root, "b" ) };
        foreach ( string dir in new[] { host }.Concat( copies ) ) Write( dir, "Assets/old.txt", "original" );
        SessionServer server = null;
        var members = new List<Member>();
        try
        {
            int port = 0;
            for ( int attempt = 0; attempt < 10 && server == null; attempt++ )
            {
                var listener = new TcpListener( IPAddress.Loopback, 0 );
                listener.Start();
                port = ( (IPEndPoint)listener.LocalEndpoint ).Port;
                listener.Stop();
                try { server = new SessionServer( "127.0.0.1", port, "test-project", "test-baseline", "test-token", Path.Combine( root, $"journal-{attempt}.jsonl" ) ); }
                catch ( SocketException ) { }
            }
            if ( server == null ) throw new IOException( "Unable to reserve a loopback port for the change log hook tests." );
            server.AllowUnverifiedPeers = true;
            server.FileProjectRoot = host;
            server.HostEngineBaseline = Engine;
            server.hostManifest = "host-manifest";
            var hostLog = new SessionChangeLog();
            server.Changes = hostLog;

            string[] names = { "MemberA", "MemberB" };
            for ( int i = 0; i < 2; i++ )
            {
                var member = new Member { Wire = Wire.Connect( "127.0.0.1", port ).GetAwaiter().GetResult() };
                members.Add( member );
                member.Wire.Send( new Packet
                {
                    Kind = "hello", Peer = Guid.NewGuid().ToString( "N" ), ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source,
                    Name = names[i], Token = "test-token", Project = "test-project", Baseline = "guest-baseline", EngineBaseline = Engine,
                    SteamId = ( 76561198000000001L + i ).ToString(), SteamToken = "a-realistic-session-token", OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "guest-manifest"
                } );
                var welcome = new List<Packet>();
                Pump( server, members, () => member.Wire.Incoming.TryPeek( out var p ) && p.Kind == "welcome" || false, welcome );
                member.Client = new FileSyncClient( copies[i] ) { PollInterval = TimeSpan.Zero, Changes = member.Log };
                member.Client.Begin( member.Wire.Send );
            }
            Pump( server, members, () => members.All( m => m.Client.JoinReady ) );
            assert( hostLog.Count == 0 && members.All( m => m.Log.Count == 0 ), "changelog_hook_identical_copies_record_nothing" );

            // Member A adds one file and edits another; the host and member B must be told, each in its own words.
            Write( copies[0], "Assets/new.txt", "brand new" );
            Write( copies[0], "Assets/old.txt", "edited by A" );
            Pump( server, members, () => File.Exists( Path.Combine( copies[1], "Assets", "new.txt" ) ) && File.ReadAllText( Path.Combine( copies[1], "Assets", "old.txt" ) ) == "edited by A" );
            assert( Kind( hostLog, "Assets/new.txt" ) == SessionChangeKind.Added && hostLog.Rows().Single( r => r.Path == "Assets/new.txt" ).By == "MemberA", "changelog_hook_host_records_a_member_upload_of_a_new_file_as_added" );
            assert( Kind( hostLog, "Assets/old.txt" ) == SessionChangeKind.Changed, "changelog_hook_host_records_a_member_upload_over_an_existing_file_as_changed" );
            assert( Kind( members[0].Log, "Assets/new.txt" ) == SessionChangeKind.Added && Kind( members[0].Log, "Assets/old.txt" ) == SessionChangeKind.Changed
                && members[0].Log.Rows().All( r => r.By == "you" ), "changelog_hook_the_writing_member_records_its_own_changes" );
            assert( Kind( members[1].Log, "Assets/new.txt" ) == SessionChangeKind.ReceivedFromHost && Kind( members[1].Log, "Assets/old.txt" ) == SessionChangeKind.ReceivedFromHost
                && members[1].Log.Rows().All( r => r.By == "the host" ), "changelog_hook_the_other_member_records_what_the_host_sent" );

            // A deletion reaches all three logs; the file A added stays an addition.
            File.Delete( Path.Combine( copies[0], "Assets", "old.txt" ) );
            Pump( server, members, () => !File.Exists( Path.Combine( host, "Assets", "old.txt" ) ) && !File.Exists( Path.Combine( copies[1], "Assets", "old.txt" ) ) );
            assert( Kind( hostLog, "Assets/old.txt" ) == SessionChangeKind.Deleted, "changelog_hook_host_records_a_member_deletion" );
            assert( Kind( members[0].Log, "Assets/old.txt" ) == SessionChangeKind.Deleted && Kind( members[1].Log, "Assets/old.txt" ) == SessionChangeKind.Deleted, "changelog_hook_both_members_record_the_deletion" );
            assert( Kind( hostLog, "Assets/new.txt" ) == SessionChangeKind.Added && hostLog.Count == 2, "changelog_hook_the_added_file_is_still_listed_once" );
            assert( SessionChangeLog.SummaryLine( hostLog.TotalChanged ) == "2 files changed. Commit them through Git when you are done.", "changelog_hook_host_summary_counts_both_files" );
        }
        finally
        {
            foreach ( var member in members )
            {
                member.Client?.Dispose();
                member.Wire.Dispose();
            }
            server?.Dispose();
            try { Directory.Delete( root, true ); } catch ( IOException ) { }
        }
    }

    static SessionChangeKind? Kind( SessionChangeLog log, string path ) => log.Rows().FirstOrDefault( r => r.Path == path )?.Kind;

    static void Pump( SessionServer server, List<Member> members, Func<bool> done, List<Packet> sink = null )
    {
        var deadline = DateTime.UtcNow.AddSeconds( 30 );
        while ( DateTime.UtcNow < deadline )
        {
            server.Pump();
            foreach ( var member in members )
            {
                if ( member.Client == null ) continue;
                while ( member.Wire.Incoming.TryDequeue( out var packet ) ) member.Client.Handle( packet, member.Wire.Send, Array.Empty<string>() );
                if ( member.Wire.Failure == null ) member.Client.Poll( member.Wire.Send, Array.Empty<string>() );
            }
            if ( done() ) return;
            Thread.Sleep( 1 );
        }
        throw new TimeoutException( "The fake members did not reach the expected change log state." );
    }

    static void Write( string root, string relative, string text )
    {
        string path = Path.Combine( root, relative.Replace( '/', Path.DirectorySeparatorChar ) );
        Directory.CreateDirectory( Path.GetDirectoryName( path )! );
        File.WriteAllBytes( path, Encoding.UTF8.GetBytes( text ) );
    }
}
