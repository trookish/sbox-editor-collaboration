using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TeamCreate;

/// <summary>
/// Project file synchronization: the pure pieces (index and its cache, join plan, chunk assembler, window, authority, receiver's write
/// procedure, admission) and a real SessionServer on loopback with fake members, some driven by the real FileSyncClient and some by raw
/// packets. Everything touches only a temporary folder.
/// </summary>
static class FileSyncTests
{
    private static readonly DateTime FixedClock = new( 2026, 10, 3, 12, 34, 56, DateTimeKind.Utc );

    private const string Engine = "engine-fingerprint-1";

    public static void Run( Action<bool, string> assert )
    {
        string root = NewDirectory();
        try
        {
            IndexAndPlan( assert, root );
            Assembler( assert, root );
            Authority( assert );
            Receiver( assert, root );
            Admission( assert );
            ServerJoin( assert, root );
            ServerLive( assert, root );
            ServerRaw( assert, root );
        }
        finally
        {
            try { Directory.Delete( root, true ); } catch ( IOException ) { } catch ( UnauthorizedAccessException ) { }
        }
    }

    // ---------------------------------------------------------------- pure

    private static void IndexAndPlan( Action<bool, string> assert, string root )
    {
        string project = Path.Combine( root, "index" );
        Write( project, "Assets/a.png", "one" );
        Write( project, "Code/x.cs", "class X {}" );
        Write( project, "Assets/.collaboration/ignored.png", "internal" );
        var index = new FileSyncIndex( project );
        var first = index.Build();
        assert( first.Select( x => x.Path ).SequenceEqual( ProjectManifest.EnumerateInputs( project ) ) && first.Count == 2, "files_index_lists_exactly_the_manifest_inputs" );
        assert( first.Single( x => x.Path == "Assets/a.png" ).Sha256 == FileSyncIndex.Hash( "Assets/a.png", Encoding.UTF8.GetBytes( "one" ) ) && first[0].Size == 3, "files_index_entry_has_digest_and_size" );
        assert( index.HashComputations == 2, "files_index_hashes_each_file_once" );
        index.Build();
        assert( index.HashComputations == 2, "files_index_cache_reuses_unchanged_digests" );
        Write( project, "Assets/a.png", "changed" );
        index.Build();
        assert( index.HashComputations == 3, "files_index_cache_rehashes_a_changed_file" );

        string h( string text ) => FileSyncIndex.Hash( "x", Encoding.UTF8.GetBytes( text ) );
        var host = new List<FileIndexEntry>
        {
            new( "Assets/m1.png", h( "m1" ), 2 ), new( "Assets/m2.vmdl", h( "m2" ), 2 ), new( "Assets/m3.sound", h( "m3" ), 2 ),
            new( "Assets/changed.vmat", h( "host" ), 4 ), new( "Assets/same.png", h( "same" ), 4 ),
            new( "Code/a.cs", h( "a-host" ), 6 ), new( "Code/b.cs", h( "b-host" ), 6 ),
            new( "Assets/scenes/open.scene", h( "open" ), 4 ), new( "Assets/scenes/closed.scene", h( "closed" ), 6 ),
            new( "Assets/huge.vmdl", "", 600L * 1024 * 1024 )
        };
        var guest = new List<FileIndexEntry>
        {
            new( "Assets/changed.vmat", h( "guest" ), 5 ), new( "Assets/same.png", h( "same" ), 4 ),
            new( "Code/a.cs", h( "a-guest" ), 7 ), new( "Assets/extra.png", h( "extra" ), 5 )
        };
        var plan = FileJoinPlan.Plan( host, guest, new[] { "Assets/scenes/open.scene" } );
        var fetched = plan.Fetch.Select( x => x.Path ).OrderBy( x => x, StringComparer.Ordinal ).ToList();
        assert( fetched.SequenceEqual( new[] { "Assets/changed.vmat", "Assets/m1.png", "Assets/m2.vmdl", "Assets/m3.sound", "Assets/scenes/closed.scene" } ),
            "files_plan_fetches_missing_and_changed_assets_and_closed_scenes_only" );
        assert( plan.CodeNotice == "Your code differs from the host's in 2 file(s), for example Code/a.cs, Code/b.cs. Update it through Git. Scenes and assets are synchronized automatically.",
            "files_plan_reports_code_differences_in_one_notice" );
        assert( plan.Notices.Contains( FileJoinPlan.TooLarge( "Assets/huge.vmdl" ) ), "files_plan_names_a_file_above_the_limit" );
        assert( FileJoinPlan.Plan( host.Take( 5 ).ToList(), host.Take( 5 ).ToList(), Array.Empty<string>() ).Fetch.Count == 0, "files_plan_identical_projects_fetch_nothing" );
    }

    private static void Assembler( Action<bool, string> assert, string root )
    {
        string project = Path.Combine( root, "assembler" );
        Directory.CreateDirectory( project );
        var receiver = new FileSyncReceiver( project );
        byte[] content = Random( FileSyncLimits.ChunkSize * 2 + 1000, 7 );
        string hash = FileSyncIndex.Hash( "Assets/big.bin", content );
        byte[] Slice( int index ) => content.Skip( index * FileSyncLimits.ChunkSize ).Take( FileSyncLimits.ChunkSize ).ToArray();

        using ( var assembler = new FileChunkAssembler( project, "Assets/big.bin", hash, content.Length ) )
        {
            assert( !assembler.Add( content.Length, new byte[] { 1 } ), "files_chunk_past_total_refused" );
            assert( !assembler.Add( FileSyncLimits.ChunkSize * 2, new byte[2000] ), "files_chunk_running_past_total_refused" );
            assert( !assembler.Add( 100, Slice( 0 ) ), "files_chunk_off_boundary_refused" );
            assert( !assembler.Add( 0, new byte[10] ), "files_short_chunk_that_is_not_the_last_refused" );
            assert( assembler.Add( FileSyncLimits.ChunkSize * 2, Slice( 2 ) ) && assembler.Add( 0, Slice( 0 ) ) && assembler.Add( 0, Slice( 0 ) ) && !assembler.IsComplete,
                "files_chunks_out_of_order_and_duplicates_accepted" );
            assert( assembler.Add( FileSyncLimits.ChunkSize, Slice( 1 ) ) && assembler.IsComplete && assembler.Received == content.Length, "files_duplicate_chunk_counted_once" );
            assert( assembler.Complete( receiver, out _ ) == null && File.ReadAllBytes( Path.Combine( project, "Assets", "big.bin" ) ).SequenceEqual( content ),
                "files_assembled_file_is_byte_for_byte" );
        }
        assert( !Directory.EnumerateFiles( Path.Combine( project, ".collaboration", "incoming" ) ).Any(), "files_part_file_removed_after_use" );

        byte[] other = Encoding.UTF8.GetBytes( "the content that was promised" );
        using ( var wrong = new FileChunkAssembler( project, "Assets/wrong.bin", FileSyncIndex.Hash( "Assets/wrong.bin", other ), other.Length ) )
        {
            byte[] tampered = Encoding.UTF8.GetBytes( "the content that was delivered" ).Take( other.Length ).ToArray();
            wrong.Add( 0, tampered );
            string error = wrong.Complete( receiver, out _ );
            assert( error != null && error.Contains( "SHA-256" ) && !File.Exists( Path.Combine( project, "Assets", "wrong.bin" ) ), "files_wrong_hash_discarded_and_nothing_written" );
        }

        using ( var empty = new FileChunkAssembler( project, "Assets/empty.txt", FileSyncIndex.Hash( "Assets/empty.txt", Array.Empty<byte>() ), 0 ) )
        {
            assert( empty.IsComplete && empty.Complete( receiver, out _ ) == null && new FileInfo( Path.Combine( project, "Assets", "empty.txt" ) ).Length == 0, "files_zero_byte_file_written" );
        }

        bool refused = false;
        string bigHash = FileSyncIndex.Hash( "Assets/huge.bin", other );
        try
        {
            using var huge = new FileChunkAssembler( project, "Assets/huge.bin", bigHash, 600L * 1024 * 1024 );
        }
        catch ( InvalidOperationException ex )
        {
            refused = ex.Message.Contains( "Assets/huge.bin" );
        }
        assert( refused && !File.Exists( Path.Combine( project, ".collaboration", "incoming", bigHash + ".part" ) ), "files_declared_600_mib_refused_before_any_write" );

        var window = new FileChunkWindow();
        bool filled = Enumerable.Range( 0, FileSyncLimits.Window ).All( _ => window.TryAcquire() );
        assert( filled && !window.TryAcquire() && window.InFlight == 8, "files_window_stops_at_eight_in_flight" );
        window.Release();
        assert( window.TryAcquire() && !window.TryAcquire(), "files_window_resumes_one_per_ack" );
    }

    private static void Authority( Action<bool, string> assert )
    {
        const string P = "Assets/a.png";
        var authority = new FileAuthority();
        authority.Seed( P, "h0" );
        assert( authority.Evaluate( P, "p1", "h1", true ) == FileVerdict.Apply && authority.Commit( P, "p1", "h0", "h1", "Ann", "peer-ann", "rec-1", out _ ) == null,
            "files_authority_put_on_current_base_has_no_notice" );
        assert( authority.Evaluate( P, "p2", "h2", true ) == FileVerdict.Apply, "files_authority_stale_put_still_applies" );
        string clash = authority.Commit( P, "p2", "h0", "h2", "Bob", "peer-bob", ".collaboration/recovery/files-20261003-123456/Assets/a.png", out string other );
        assert( clash == "\"Assets/a.png\" was changed by Bob and by Ann at the same time. The later version was kept; the earlier one is in .collaboration/recovery/files-20261003-123456/Assets/a.png."
            && other == "peer-ann" && authority.HashOf( P ) == "h2", "files_authority_later_wins_with_clash_notice" );
        assert( authority.Evaluate( P, "p2", "h2", true ) == FileVerdict.Duplicate && authority.Evaluate( P, "p1", "h1", true ) == FileVerdict.Duplicate,
            "files_authority_resend_by_packet_id_is_idempotent" );
        assert( authority.Evaluate( P, "p3", "H2", true ) == FileVerdict.Unchanged, "files_authority_content_already_held_is_unchanged" );

        // A delete based on a version that was already replaced: still applied, and reported.
        assert( authority.Evaluate( P, "d1", null, true ) == FileVerdict.Apply, "files_authority_stale_delete_applies" );
        string deleteClash = authority.Commit( P, "d1", "h1", null, "Cid", "peer-cid", "rec-delete", out _ );
        assert( deleteClash != null && deleteClash.Contains( "by Cid and by Bob" ) && deleteClash.EndsWith( "rec-delete." ) && authority.HashOf( P ) == null,
            "files_authority_delete_racing_a_put_wins_with_notice" );
        // A put from someone who never saw the delete: the later put wins, and the earlier version is the copy the delete kept.
        assert( authority.Evaluate( P, "p4", "h4", false ) == FileVerdict.Apply, "files_authority_put_after_delete_applies" );
        string putClash = authority.Commit( P, "p4", "h2", "h4", "Dan", "peer-dan", null, out _ );
        assert( putClash != null && putClash.Contains( "by Dan and by Cid" ) && putClash.EndsWith( "rec-delete." ) && authority.HashOf( P ) == "h4",
            "files_authority_put_racing_a_delete_wins_with_notice" );
        assert( authority.Evaluate( "Assets/never.png", "d9", null, false ) == FileVerdict.Unchanged, "files_authority_delete_of_absent_file_is_unchanged" );
    }

    private static void Receiver( Action<bool, string> assert, string root )
    {
        string project = Path.Combine( root, "receiver", "project" );
        Write( project, "Assets/item.png", "old" );
        var receiver = new FileSyncReceiver( project, () => FixedClock );
        byte[] bytes = Encoding.UTF8.GetBytes( "replacement" );
        string Hash( string path ) => FileSyncIndex.Hash( path, bytes );

        var refusedPaths = new[]
        {
            "../outside.png", "Assets/../../outside.png", "C:/outside.png", "/outside.png", "Code/hack.cs", "Assets/hack.cs", "ProjectSettings/collaboration.json",
            ".git/hooks/pre-commit", "Assets/.collaboration/x.png", "Assets/obj/x.png", "Assets/CON.png", "outside.png"
        };
        foreach ( string path in refusedPaths )
        {
            assert( receiver.Accept( path, Hash( path ), bytes, out _ ) != null, "files_receiver_refuses_" + path );
        }
        assert( !File.Exists( Path.Combine( project, "..", "outside.png" ) ) && Files( project ).SequenceEqual( new[] { "Assets/item.png" } ),
            "files_receiver_refused_paths_write_nothing" );
        assert( receiver.Delete( "Code/hack.cs", out _ ) != null && receiver.Delete( "../outside.png", out _ ) != null, "files_receiver_refuses_unsafe_deletes" );

        assert( receiver.Accept( "Assets/item.png", FileSyncIndex.Hash( "Assets/item.png", Encoding.UTF8.GetBytes( "other" ) ), bytes, out _ ) != null
            && Read( project, "Assets/item.png" ) == "old", "files_receiver_verifies_the_digest_before_writing" );

        string error = receiver.Accept( "Assets/item.png", Hash( "Assets/item.png" ), bytes, out string recovery );
        assert( error == null && recovery == ".collaboration/recovery/files-20261003-123456/Assets/item.png" && Read( project, recovery ) == "old"
            && Read( project, "Assets/item.png" ) == "replacement", "files_receiver_backs_up_before_replacing" );
        assert( !Directory.EnumerateFiles( Path.Combine( project, "Assets" ) ).Any( x => x.EndsWith( ".tmp" ) ), "files_receiver_replace_leaves_no_temporary_file" );
        assert( receiver.Accept( "Assets/item.png", Hash( "Assets/item.png" ), bytes, out string again ) == null && again == null, "files_receiver_identical_content_needs_no_backup" );

        byte[] third = Encoding.UTF8.GetBytes( "third" );
        receiver.Accept( "Assets/item.png", FileSyncIndex.Hash( "Assets/item.png", third ), third, out string secondBackup );
        assert( secondBackup == ".collaboration/recovery/files-20261003-123456-2/Assets/item.png" && Read( project, secondBackup ) == "replacement" && Read( project, recovery ) == "old",
            "files_receiver_never_overwrites_a_backup" );

        assert( receiver.Accept( "Assets/new/deep/file.vmat", Hash( "Assets/new/deep/file.vmat" ), bytes, out string none ) == null && none == null
            && Read( project, "Assets/new/deep/file.vmat" ) == "replacement", "files_receiver_creates_new_file_without_backup" );
        assert( receiver.Delete( "Assets/new/deep/file.vmat", out string deleted ) == null && !File.Exists( Path.Combine( project, "Assets", "new", "deep", "file.vmat" ) )
            && Read( project, deleted ) == "replacement", "files_receiver_backs_up_before_deleting" );
    }

    private static void Admission( Action<bool, string> assert )
    {
        assert( AdmissionRules.Evaluate( new PeerFacts( "1", true, true, true, "1.0.0", "different", "cyn" ), CollaborationConfig.Defaults(), "1.0.0", "host", out _ ) == null,
            "files_admission_manifest_difference_not_refused" );
        var accepted = new HashSet<string> { "base" };
        assert( AdmissionRules.BaselineRefusal( "guest", "host", "other-base", accepted, Engine, Engine ) == null, "files_admission_different_files_same_engine_admitted" );
        assert( AdmissionRules.BaselineRefusal( "guest", "host", "other-base", accepted, "other-engine", Engine ) == AdmissionRules.EngineRefusalMessage,
            "files_admission_different_engine_refused" );
        assert( AdmissionRules.BaselineRefusal( "guest", "host", "other-base", accepted, null, Engine ) == AdmissionRules.EngineUnknownRefusalMessage,
            "files_admission_peer_without_file_sync_refused_when_files_differ" );
        assert( AdmissionRules.BaselineRefusal( "host", "host", "other-base", accepted, Engine, Engine ) == AdmissionRules.BaselineRefusalMessage
            && AdmissionRules.BaselineRefusal( "host", "host", "base", accepted, null, Engine ) == null, "files_admission_same_files_keep_the_full_baseline_check" );
        assert( AdmissionRules.EngineFingerprint( new[] { "a", "b" } ) == AdmissionRules.EngineFingerprint( new[] { "a", "b" } )
            && AdmissionRules.EngineFingerprint( new[] { "a", "b" } ) != AdmissionRules.EngineFingerprint( new[] { "a", "c" } ), "files_admission_engine_fingerprint_is_stable" );
    }

    // ---------------------------------------------------------------- real server

    private static void ServerJoin( Action<bool, string> assert, string root )
    {
        string host = Path.Combine( root, "join", "host" );
        string guest = Path.Combine( root, "join", "guest" );
        byte[] large = Random( 16 * 1024 * 1024 + 12345, 1 );
        byte[] model = Random( 700 * 1024, 2 );
        Write( host, "Assets/textures/large.png", large );
        Write( host, "Assets/models/m2.vmdl", model );
        Write( host, "Assets/sounds/empty.sound", Array.Empty<byte>() );
        Write( host, "Assets/materials/changed.vmat", "host version" );
        Write( host, "Assets/same.png", "same" );
        Write( host, "Code/a.cs", "class A { /* host */ }" );
        Write( host, "Code/b.cs", "class B { /* host */ }" );
        Write( guest, "Assets/materials/changed.vmat", "guest version" );
        Write( guest, "Assets/same.png", "same" );
        Write( guest, "Code/a.cs", "class A { /* guest */ }" );
        Write( guest, "Code/b.cs", "class B { /* guest */ }" );
        Write( guest, "Assets/extra.png", "guest only" );
        var hostBefore = Snapshot( host );
        var guestBefore = Snapshot( guest );

        using var rig = FileRig.Start( host, Path.Combine( root, "join" ) );
        var refused = rig.Join( "Mallory", guest, engine: "other-engine", withClient: false );
        assert( refused.Welcome == null && refused.Seen.Any( p => p.Error == AdmissionRules.EngineRefusalMessage ), "files_server_refuses_a_different_engine_build" );

        var member = rig.Join( "Guest", guest, engine: Engine );
        assert( member.Welcome != null && member.Welcome.Features != null && member.Welcome.Features.Contains( FileSyncLimits.Feature ),
            "files_server_admits_a_guest_whose_files_differ" );
        var clock = Stopwatch.StartNew();
        rig.PumpUntil( () => member.Client.JoinReady );
        clock.Stop();
        var notices = member.Notices();

        bool exact = Read( guest, "Assets/textures/large.png", binary: true ).SequenceEqual( large )
            && File.ReadAllBytes( Path.Combine( guest, "Assets", "models", "m2.vmdl" ) ).SequenceEqual( model )
            && new FileInfo( Path.Combine( guest, "Assets", "sounds", "empty.sound" ) ).Length == 0
            && Read( guest, "Assets/materials/changed.vmat" ) == "host version";
        assert( exact && member.Client.Planned == 4 && member.Client.Finished == 4, "files_server_join_receives_exactly_the_four_assets_byte_for_byte" );
        assert( Read( guest, "Code/a.cs" ) == "class A { /* guest */ }" && Read( guest, "Code/b.cs" ) == "class B { /* guest */ }" && Read( guest, "Assets/extra.png" ) == "guest only",
            "files_server_join_never_writes_code_or_guest_only_files" );
        var expected = guestBefore.Keys.Concat( new[] { "Assets/textures/large.png", "Assets/models/m2.vmdl", "Assets/sounds/empty.sound" } ).OrderBy( x => x, StringComparer.Ordinal );
        assert( Files( guest ).SequenceEqual( expected ), "files_server_join_writes_nothing_else" );
        string backup = Directory.EnumerateFiles( Path.Combine( guest, ".collaboration", "recovery" ), "changed.vmat", SearchOption.AllDirectories ).SingleOrDefault();
        assert( backup != null && File.ReadAllText( backup ) == "guest version", "files_server_join_backs_up_the_changed_asset" );
        assert( notices.Any( x => x.StartsWith( "Your code differs from the host's in 2 file(s), for example Code/a.cs, Code/b.cs." ) ), "files_server_join_reports_the_code_difference" );
        assert( Snapshot( host ).SequenceEqual( hostBefore ), "files_server_join_never_changes_the_hosts_files" );
        assert( !Directory.EnumerateFiles( Path.Combine( guest, ".collaboration", "incoming" ) ).Any(), "files_server_join_leaves_no_part_files" );

        double megabytes = ( large.Length + model.Length ) / 1048576.0;
        Console.WriteLine( $"files_throughput: {megabytes:0.0} MiB joined over loopback in {clock.Elapsed.TotalSeconds:0.00} s = {megabytes / clock.Elapsed.TotalSeconds:0.0} MiB/s" );

        // A reconnect re-plans from disk: everything already received is kept, nothing is fetched again.
        var again = rig.Join( "Guest again", guest, engine: Engine );
        assert( again.Welcome != null, "files_server_admits_the_guest_again_after_reconnecting" );
        rig.PumpUntil( () => again.Client.JoinReady );
        assert( again.Client.Planned == 0 && again.Notices().Any( x => x.StartsWith( "Your code differs" ) ), "files_server_rejoin_fetches_only_what_is_missing" );
    }

    private static void ServerLive( Action<bool, string> assert, string root )
    {
        string host = Path.Combine( root, "live", "host" );
        Write( host, "Assets/materials/shared.vmat", "original" );
        Write( host, "Assets/textures/doomed.png", "to be deleted" );
        Write( host, "Code/a.cs", "class A {}" );
        var copies = new[] { "a", "b", "c" }.Select( name => Path.Combine( root, "live", name ) ).ToList();
        foreach ( string copy in copies ) CopyTree( host, copy );

        using var rig = FileRig.Start( host, Path.Combine( root, "live" ) );
        var members = copies.Select( ( copy, i ) => rig.Join( "Member" + (char)( 'A' + i ), copy, engine: Engine ) ).ToList();
        rig.PumpUntil( () => members.All( m => m.Client.JoinReady ) );
        assert( members.All( m => m.Client.Planned == 0 ), "files_server_live_identical_members_fetch_nothing" );
        var hostRefreshed = new List<string>();
        var memberRefreshed = new List<string>();
        rig.Server.FileChanged = hostRefreshed.Add;
        members[1].Client.FileChanged = memberRefreshed.Add;

        byte[] edit = Random( FileSyncLimits.ChunkSize + 77, 3 );
        Write( copies[0], "Assets/materials/shared.vmat", edit );
        Write( copies[0], "Assets/textures/new.png", "brand new" );
        rig.PumpUntil( () => copies.Skip( 1 ).All( c => File.Exists( Path.Combine( c, "Assets", "textures", "new.png" ) )
            && File.ReadAllBytes( Path.Combine( c, "Assets", "materials", "shared.vmat" ) ).SequenceEqual( edit ) ) );
        assert( File.ReadAllBytes( Path.Combine( host, "Assets", "materials", "shared.vmat" ) ).SequenceEqual( edit ) && Read( host, "Assets/textures/new.png" ) == "brand new",
            "files_server_live_put_is_written_on_the_host" );
        assert( copies.Skip( 1 ).All( c => Read( c, "Assets/textures/new.png" ) == "brand new" ), "files_server_live_put_reaches_the_other_two_members" );
        assert( copies.Skip( 1 ).All( c => Directory.EnumerateFiles( Path.Combine( c, ".collaboration", "recovery" ), "shared.vmat", SearchOption.AllDirectories ).Any( f => File.ReadAllText( f ) == "original" ) ),
            "files_server_live_put_backs_up_before_replacing" );
        var written = new[] { "Assets/materials/shared.vmat", "Assets/textures/new.png" };
        assert( written.All( hostRefreshed.Contains ) && written.All( memberRefreshed.Contains ) && hostRefreshed.Count == 2 && memberRefreshed.Count == 2,
            "files_server_live_writes_are_reported_for_asset_refresh" );

        File.Delete( Path.Combine( copies[1], "Assets", "textures", "doomed.png" ) );
        rig.PumpUntil( () => !File.Exists( Path.Combine( host, "Assets", "textures", "doomed.png" ) ) && copies.All( c => !File.Exists( Path.Combine( c, "Assets", "textures", "doomed.png" ) ) ) );
        assert( new[] { copies[0], copies[2], host }.All( c => Directory.EnumerateFiles( Path.Combine( c, ".collaboration", "recovery" ), "doomed.png", SearchOption.AllDirectories ).Any() ),
            "files_server_live_delete_reaches_everyone_with_a_backup" );

        // Settle, then make sure nothing echoes: no member re-sends what it received, so no clash notice is ever produced.
        rig.PumpFor( 600 );
        assert( members.All( m => !m.Notices().Any( x => x.Contains( "at the same time" ) ) ) && rig.Server.FileNotice == null, "files_server_live_writes_do_not_echo" );
        assert( copies.All( c => Read( c, "Code/a.cs" ) == "class A {}" ), "files_server_live_code_untouched" );
    }

    private static void ServerRaw( Action<bool, string> assert, string root )
    {
        string host = Path.Combine( root, "raw", "host" );
        byte[] big = Random( FileSyncLimits.ChunkSize * 20, 4 );
        Write( host, "Assets/big.bin", big );
        Write( host, "Assets/clash.png", "base" );
        string baseHash = FileSyncIndex.Hash( "Assets/clash.png", Encoding.UTF8.GetBytes( "base" ) );
        using var rig = FileRig.Start( host, Path.Combine( root, "raw" ) );
        var one = rig.Join( "Ann", Path.Combine( root, "raw", "one" ), engine: Engine, withClient: false );
        var two = rig.Join( "Bob", Path.Combine( root, "raw", "two" ), engine: Engine, withClient: false );

        // ---- code and paths outside the project are refused; the session continues ----
        byte[] code = Encoding.UTF8.GetBytes( "evil" );
        foreach ( string path in new[] { "Code/evil.cs", "../evil.png" } )
        {
            one.Seen.Clear();
            one.Wire.Send( new Packet { Kind = "file-put", Path = path, Sha256 = FileSyncIndex.Hash( path, code ), Total = code.Length } );
            one.Wire.Send( new Packet { Kind = "file-chunk", Path = path, Sha256 = FileSyncIndex.Hash( path, code ), Total = code.Length, Data = code } );
            one.Wire.Send( new Packet { Kind = "ping" } );
            rig.PumpUntil( () => one.Seen.Any( p => p.Kind == "pong" ) );
            assert( one.Seen.Any( p => p.Kind == "file-notice" && p.Path == path ) && !one.Seen.Any( p => p.Kind == "file-changed" ), "files_server_refuses_put_of_" + path );
        }
        assert( !File.Exists( Path.Combine( host, "Code", "evil.cs" ) ) && !File.Exists( Path.Combine( host, "..", "evil.png" ) ), "files_server_refused_puts_write_nothing" );

        // ---- two members change one file from the same base: the later wins and both are told ----
        one.Seen.Clear();
        two.Seen.Clear();
        Put( one, "Assets/clash.png", "ann's version", baseHash );
        rig.PumpUntil( () => one.Seen.Any( p => p.Kind == "file-changed" ) && two.Seen.Any( p => p.Kind == "file-changed" ) );
        Put( two, "Assets/clash.png", "bob's version", baseHash );
        rig.PumpUntil( () => two.Seen.Count( p => p.Kind == "file-changed" ) == 2 && one.Seen.Count( p => p.Kind == "file-changed" ) == 2 );
        string clash = two.Seen.FirstOrDefault( p => p.Kind == "file-notice" )?.Notice;
        assert( Read( host, "Assets/clash.png" ) == "bob's version", "files_server_later_write_wins" );
        assert( clash != null && clash.StartsWith( "\"Assets/clash.png\" was changed by Bob and by Ann at the same time. The later version was kept; the earlier one is in .collaboration/recovery/files-" )
            && one.Seen.Any( p => p.Kind == "file-notice" && p.Notice == clash ), "files_server_clash_notice_reaches_both_writers" );
        string earlier = clash.Substring( clash.IndexOf( ".collaboration/", StringComparison.Ordinal ) ).TrimEnd( '.' );
        assert( Read( host, earlier ) == "ann's version", "files_server_clash_keeps_the_earlier_version_in_recovery" );

        // ---- a resend of the same put (same packet id) is not applied twice ----
        var resend = Put( one, "Assets/clash.png", "ann's version", baseHash );
        rig.PumpUntil( () => one.Seen.Count( p => p.Kind == "file-changed" ) == 3 );
        one.Wire.Send( resend );
        one.Wire.Send( new Packet { Kind = "file-chunk", Path = resend.Path, Sha256 = resend.Sha256, Total = resend.Total, Data = Encoding.UTF8.GetBytes( "ann's version" ) } );
        int pongs = one.Seen.Count( p => p.Kind == "pong" );
        one.Wire.Send( new Packet { Kind = "ping" } );
        rig.PumpUntil( () => one.Seen.Count( p => p.Kind == "pong" ) > pongs );
        assert( one.Seen.Count( p => p.Kind == "file-changed" ) == 3, "files_server_resent_put_is_not_applied_twice" );

        // ---- live packets overtake file chunks: with no acknowledgement only the window's eight chunks leave ----
        one.Seen.Clear();
        string bigHash = FileSyncIndex.Hash( "Assets/big.bin", big );
        one.Wire.Send( new Packet { Kind = "file-get", Path = "Assets/big.bin", Sha256 = bigHash } );
        rig.PumpFor( 300 );
        one.Wire.Send( new Packet { Kind = "ping" } );
        rig.PumpUntil( () => one.Seen.Any( p => p.Kind == "pong" ) );
        int beforePong = one.Seen.TakeWhile( p => p.Kind != "pong" ).Count( p => p.Kind == "file-chunk" );
        assert( beforePong == FileSyncLimits.Window && one.Seen.Count( p => p.Kind == "file-chunk" ) == FileSyncLimits.Window, "files_server_live_packet_overtakes_file_chunks" );

        var received = new byte[big.Length];
        int acked = 0;
        rig.PumpUntil( () =>
        {
            foreach ( var chunk in one.Seen.Where( p => p.Kind == "file-chunk" ).Skip( acked ).ToList() )
            {
                Array.Copy( chunk.Data, 0, received, chunk.Offset, chunk.Data.Length );
                one.Wire.Send( new Packet { Kind = "file-ack", Path = chunk.Path, Offset = chunk.Offset } );
                acked++;
            }
            return acked == 20;
        } );
        assert( received.SequenceEqual( big ), "files_server_window_resumes_on_acknowledgement_and_delivers_the_file" );
    }

    private static Packet Put( FakeMember member, string path, string text, string baseHash )
    {
        byte[] bytes = Encoding.UTF8.GetBytes( text );
        var put = new Packet { Kind = "file-put", Path = path, BaseSha256 = baseHash, Sha256 = FileSyncIndex.Hash( path, bytes ), Total = bytes.Length };
        member.Wire.Send( put );
        member.Wire.Send( new Packet { Kind = "file-chunk", Path = path, Sha256 = put.Sha256, Total = bytes.Length, Data = bytes } );
        return put;
    }

    // ---------------------------------------------------------------- harness

    private sealed class FakeMember
    {
        public Wire Wire;

        public FileSyncClient Client;

        public Packet Welcome;

        public readonly List<Packet> Seen = new();

        public readonly List<string> NoticeLog = new();

        public List<string> Notices()
        {
            for ( string n = Client?.TakeNotice(); n != null; n = Client.TakeNotice() ) NoticeLog.Add( n );
            return NoticeLog;
        }
    }

    private sealed class FileRig : IDisposable
    {
        public SessionServer Server;

        private int port;

        private readonly List<FakeMember> members = new();

        public static FileRig Start( string hostRoot, string directory )
        {
            var rig = new FileRig();
            Exception last = null;
            for ( int attempt = 0; attempt < 10 && rig.Server == null; attempt++ )
            {
                try
                {
                    rig.port = FreePort();
                    rig.Server = new SessionServer( "127.0.0.1", rig.port, "test-project", "test-baseline", "test-token", Path.Combine( directory, $"journal-{attempt}.jsonl" ) );
                }
                catch ( SocketException error ) { last = error; }
            }
            if ( rig.Server == null ) throw new IOException( "Unable to reserve a loopback port for the file synchronization tests.", last );
            rig.Server.AllowUnverifiedPeers = true;
            rig.Server.FileProjectRoot = hostRoot;
            rig.Server.HostEngineBaseline = Engine;
            rig.Server.hostManifest = "host-manifest";
            return rig;
        }

        public FakeMember Join( string name, string root, string engine, bool withClient = true )
        {
            Directory.CreateDirectory( root );
            var member = new FakeMember { Wire = Wire.Connect( "127.0.0.1", port ).GetAwaiter().GetResult() };
            members.Add( member );
            member.Wire.Send( new Packet
            {
                Kind = "hello", Peer = Guid.NewGuid().ToString( "N" ), ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source,
                Name = name, Token = "test-token", Project = "test-project", Baseline = "guest-baseline", EngineBaseline = engine,
                SteamId = ( 76561198000000001L + members.Count ).ToString(), SteamToken = "a-realistic-session-token", OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "guest-manifest"
            } );
            PumpUntil( () => member.Seen.Any( p => p.Kind == "welcome" || p.Error != null ) );
            member.Welcome = member.Seen.FirstOrDefault( p => p.Kind == "welcome" );
            if ( withClient && member.Welcome != null )
            {
                member.Client = new FileSyncClient( root, () => FixedClock.AddSeconds( Environment.TickCount64 / 1000.0 ) ) { PollInterval = TimeSpan.Zero };
                member.Client.Begin( member.Wire.Send );
            }
            return member;
        }

        public void PumpOnce()
        {
            Server.Pump();
            foreach ( var member in members )
            {
                while ( member.Wire.Incoming.TryDequeue( out var packet ) )
                {
                    if ( member.Client == null || !member.Client.Handle( packet, member.Wire.Send, Array.Empty<string>() ) ) member.Seen.Add( packet );
                }
                if ( member.Wire.Failure == null ) member.Client?.Poll( member.Wire.Send, Array.Empty<string>() );
            }
        }

        public void PumpUntil( Func<bool> done, int milliseconds = 30000 )
        {
            var deadline = DateTime.UtcNow.AddMilliseconds( milliseconds );
            while ( DateTime.UtcNow < deadline )
            {
                PumpOnce();
                if ( done() ) return;
                Thread.Sleep( 1 );
            }
            throw new TimeoutException( "The fake members did not reach the expected file synchronization state." );
        }

        public void PumpFor( int milliseconds )
        {
            var deadline = DateTime.UtcNow.AddMilliseconds( milliseconds );
            while ( DateTime.UtcNow < deadline )
            {
                PumpOnce();
                Thread.Sleep( 1 );
            }
        }

        public void Dispose()
        {
            foreach ( var member in members )
            {
                member.Client?.Dispose();
                member.Wire.Dispose();
            }
            Server?.Dispose();
        }
    }

    // ---------------------------------------------------------------- files

    private static string NewDirectory()
    {
        string root = Environment.GetEnvironmentVariable( "TMPDIR" );
        if ( string.IsNullOrWhiteSpace( root ) ) root = Path.GetTempPath();
        string path = Path.Combine( root, "teamcreate-coretests", "files-" + Guid.NewGuid().ToString( "N" ) );
        Directory.CreateDirectory( path );
        return path;
    }

    private static int FreePort()
    {
        var listener = new TcpListener( IPAddress.Loopback, 0 );
        listener.Start();
        int port = ( (IPEndPoint)listener.LocalEndpoint ).Port;
        listener.Stop();
        return port;
    }

    private static void Write( string root, string relative, string text ) => Write( root, relative, Encoding.UTF8.GetBytes( text ) );

    private static void Write( string root, string relative, byte[] bytes )
    {
        string path = Path.Combine( root, relative.Replace( '/', Path.DirectorySeparatorChar ) );
        Directory.CreateDirectory( Path.GetDirectoryName( path )! );
        File.WriteAllBytes( path, bytes );
    }

    private static string Read( string root, string relative ) => File.ReadAllText( Path.Combine( root, relative.Replace( '/', Path.DirectorySeparatorChar ) ) );

    private static byte[] Read( string root, string relative, bool binary ) => File.ReadAllBytes( Path.Combine( root, relative.Replace( '/', Path.DirectorySeparatorChar ) ) );

    private static byte[] Random( int length, int seed )
    {
        var bytes = new byte[length];
        new Random( seed ).NextBytes( bytes );
        return bytes;
    }

    // Every file under the root except the library's own folder, as sorted project-relative paths.
    private static List<string> Files( string root )
    {
        return Directory.EnumerateFiles( root, "*", SearchOption.AllDirectories )
            .Select( x => Path.GetRelativePath( root, x ).Replace( '\\', '/' ) )
            .Where( x => !x.StartsWith( ".collaboration/", StringComparison.Ordinal ) )
            .OrderBy( x => x, StringComparer.Ordinal ).ToList();
    }

    private static SortedDictionary<string, string> Snapshot( string root )
    {
        var result = new SortedDictionary<string, string>( StringComparer.Ordinal );
        foreach ( string file in Files( root ) ) result[file] = Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( File.ReadAllBytes( Path.Combine( root, file ) ) ) );
        return result;
    }

    private static void CopyTree( string from, string to )
    {
        foreach ( string file in Files( from ) ) Write( to, file, File.ReadAllBytes( Path.Combine( from, file ) ) );
    }
}
