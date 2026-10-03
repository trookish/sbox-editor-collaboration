using System.Net;
using System.Net.Sockets;
using System.Text;
using TeamCreate;

/// <summary>
/// Consent-gated code transfer (w7-code): the classification of what may ever travel as code, the offer the guest is shown, the trust gate,
/// the host's refusal of any code request that no consent covers, the guarantee that code never flows guest to host, and the whole
/// "fresh empty project joins" scenario over a real SessionServer on loopback with fake members. Everything touches only temporary folders.
/// </summary>
static class CodeTransferTests
{
    private static readonly DateTime FixedClock = new( 2026, 10, 3, 12, 34, 56, DateTimeKind.Utc );

    private const string Engine = "engine-fingerprint-1";

    public static void Run( Action<bool, string> assert )
    {
        string root = NewDirectory();
        try
        {
            Classification( assert );
            OfferBuilding( assert );
            Limits( assert );
            HostileEntries( assert );
            Trust( assert );
            ServerRefusals( assert, root );
            NoCodeUpload( assert, root );
            EmptyProjectOnboarding( assert, root );
            ChangedAfterConsent( assert, root );
            BinaryContent( assert, root );
            LegacyHost( assert, root );
        }
        finally
        {
            try { Directory.Delete( root, true ); } catch ( IOException ) { } catch ( UnauthorizedAccessException ) { }
        }
    }

    // ---------------------------------------------------------------- pure

    private static string H( string text ) => FileSyncIndex.Hash( "x", Encoding.UTF8.GetBytes( text ) );

    private static FileIndexEntry E( string path, string content ) => new( path, H( content ), Encoding.UTF8.GetByteCount( content ) );

    private static void Classification( Action<bool, string> assert )
    {
        string[] transferable =
        {
            "Code/Player.cs", "Code/ui/Hud.razor", "Code/ui/Hud.scss", "Editor/Tool.cs", "ProjectSettings/Physics.config", "Assets/scripts/Thing.cs",
            "CODE/Upper.cs".Replace( "CODE", "Code" ), "Code/UPPER.CS", "Code/Deep/er/File.RAZOR", "Code/Notes.txt", "Editor/readme.md"
        };
        foreach ( string path in transferable )
        {
            assert( CodeTransferRules.IsTransferable( path ), "code_classify_transferable_" + path );
        }
        string[] refused =
        {
            "Libraries/foo/x.cs", "libraries/foo/x.cs", "Libraries/collaboration/Editor/Core/FileSync.cs", ".git/config", "Code/.git/config", ".collaboration/state.json",
            "Code/.collaboration/x.cs", "obj/x.cs", "bin/x.cs", ".sbox/x.cs", "Code/obj/x.cs", "Code/bin/Debug/x.cs", "Editor/.sbox/x.cs",
            "../Code/x.cs", "Code/../../x.cs", "C:/Code/x.cs", "C:x.cs", "/Code/x.cs", "Code\\x.cs", "Code/x.cs:stream", "game.sbproj", "game.csproj", "Code/game.csproj",
            "Assets/a.png", "Assets/scenes/a.scene", "Code", "Code/", "", "   ", "Code/a\nb.cs", "Code/CON.cs", "Code/x.dll"
        };
        foreach ( string path in refused )
        {
            assert( !CodeTransferRules.IsTransferable( path ), "code_classify_refused_" + path.Replace( "\n", "\\n" ) );
        }
        assert( !CodeTransferRules.IsTransferable( null ), "code_classify_null_refused" );
        assert( CodeTransferRules.IsExcluded( "Libraries/foo/x.cs" ) && CodeTransferRules.IsExcluded( ".GIT/config" ) && !CodeTransferRules.IsExcluded( "Code/x.cs" ), "code_excluded_folders" );

        // The asset path (continuous sync, join) keeps refusing code: nothing in this task widens it.
        assert( !FileSyncReceiver.ValidPath( "C:/nowhere", "Code/Player.cs", out _, out _ ) && !FileSyncReceiver.ValidPath( "C:/nowhere", "Assets/scripts/Thing.cs", out _, out _ ), "code_asset_path_still_refuses_code" );
    }

    private static void OfferBuilding( Action<bool, string> assert )
    {
        var host = new List<FileIndexEntry>
        {
            E( "Code/b.cs", "b-host" ), E( "Code/a.cs", "a-host" ), E( "Code/same.cs", "same" ), E( "Editor/e.cs", "e-host" ),
            E( "Libraries/foo/x.cs", "lib" ), E( "Assets/a.png", "png" ), E( "game.sbproj", "{}" )
        };
        var guest = new List<FileIndexEntry> { E( "Code/a.cs", "a-guest" ), E( "code/SAME.cs", "same" ), E( "Code/mine.cs", "mine" ), E( "Libraries/foo/x.cs", "other" ) };
        var offer = CodeOffer.Build( host, guest );
        assert( offer != null && offer.Files.Select( x => x.Path ).SequenceEqual( new[] { "Code/a.cs", "Code/b.cs", "Editor/e.cs" } ), "code_offer_lists_only_missing_and_changed_code_in_path_order" );
        assert( offer.Files[0].Status == CodeFileStatus.Differs && offer.Files[1].Status == CodeFileStatus.New && offer.Files[2].Status == CodeFileStatus.New, "code_offer_status_new_or_differs" );
        assert( offer.TotalBytes == offer.Files.Sum( x => x.Size ) && offer.Files.All( x => x.Sha256.Length == 64 ), "code_offer_total_is_the_sum_of_the_listed_files" );
        assert( offer.Files.All( x => x.Path != "Libraries/foo/x.cs" && x.Path != "game.sbproj" && x.Path != "Assets/a.png" ), "code_offer_excludes_libraries_project_file_and_assets" );
        assert( offer.Hashes.OrderBy( x => x, StringComparer.Ordinal ).SequenceEqual( offer.Files.Select( x => x.Sha256 ).Distinct().OrderBy( x => x, StringComparer.Ordinal ) ), "code_offer_hashes_are_exactly_the_listed_files" );

        var reordered = CodeOffer.Build( host.AsEnumerable().Reverse().ToList(), guest );
        assert( reordered.Fingerprint == offer.Fingerprint, "code_offer_fingerprint_ignores_listing_order" );
        var changed = host.ToList();
        changed[0] = E( "Code/b.cs", "b-host-2" );
        assert( CodeOffer.Build( changed, guest ).Fingerprint != offer.Fingerprint, "code_offer_fingerprint_changes_with_content" );
        assert( CodeOffer.Build( host.Where( x => !x.Path.StartsWith( "Code/" ) && !x.Path.StartsWith( "Editor/" ) ).ToList(), guest ) == null, "code_offer_is_null_when_nothing_differs" );
        assert( CodeOffer.Build( host, host ) == null, "code_offer_is_null_for_identical_projects" );
    }

    private static void Limits( Action<bool, string> assert )
    {
        var none = new List<FileIndexEntry>();
        var many = Enumerable.Range( 0, CodeTransferLimits.MaxFiles + 1 ).Select( i => E( $"Code/f{i:D5}.cs", "c" + i ) ).ToList();
        var offer = CodeOffer.Build( many, none );
        assert( offer.Files.Count == 2000 && offer.ExcessCount == 1 && offer.ExcessNotice != null && offer.ExcessNotice.Contains( "2,000" ) && offer.ExcessNotice.Contains( "Code/f02000.cs" ), "code_limit_2000_files_then_the_excess_is_named" );
        assert( offer.Files.All( x => x.Path != "Code/f02000.cs" ), "code_limit_the_excess_file_is_not_offered" );

        var big = new List<FileIndexEntry> { new( "Code/big.cs", H( "big" ), CodeTransferLimits.MaxFileBytes + 1 ), new( "Code/ok.cs", H( "ok" ), CodeTransferLimits.MaxFileBytes ) };
        offer = CodeOffer.Build( big, none );
        assert( offer.Files.Select( x => x.Path ).SequenceEqual( new[] { "Code/ok.cs" } ) && offer.ExcessCount == 1 && offer.ExcessNotice.Contains( "4 MiB" ) && offer.ExcessNotice.Contains( "Code/big.cs" ), "code_limit_a_file_above_4_mib_is_not_offered_and_is_named" );

        var total = Enumerable.Range( 0, 17 ).Select( i => new FileIndexEntry( $"Code/t{i:D2}.cs", H( "t" + i ), CodeTransferLimits.MaxFileBytes ) ).ToList();
        offer = CodeOffer.Build( total, none );
        assert( offer.Files.Count == 16 && offer.TotalBytes == CodeTransferLimits.MaxTotalBytes && offer.ExcessCount == 1 && offer.ExcessNotice.Contains( "64 MiB" ), "code_limit_64_mib_total_is_allowed_and_the_next_file_is_excess" );

        var only = CodeOffer.Build( new List<FileIndexEntry> { new( "Code/huge.cs", H( "h" ), CodeTransferLimits.MaxFileBytes + 1 ) }, none );
        assert( only != null && only.Files.Count == 0 && only.ExcessCount == 1 && !only.CanOffer, "code_limit_an_offer_with_only_excess_has_nothing_to_receive" );
    }

    private static void HostileEntries( Action<bool, string> assert )
    {
        var none = new List<FileIndexEntry>();
        var entries = new List<FileIndexEntry>();
        for ( int i = 0; i < 5000; i++ ) entries.Add( E( $"Code/h{i:D4}.cs", "h" + i ) );
        var offer = CodeOffer.Build( entries, none );
        assert( offer.Files.Count == 2000 && offer.ExcessCount == 3000, "code_hostile_5000_entries_are_bounded_and_counted" );

        var bad = new List<FileIndexEntry>
        {
            new( "Code/huge.cs", H( "a" ), long.MaxValue ), new( "Code/neg.cs", H( "b" ), -1 ), new( "Code/nohash.cs", "", 3 ), new( "Code/badhash.cs", "zz", 3 ),
            new( "Code/ctl\n.cs", H( "c" ), 3 ), new( "../Code/up.cs", H( "d" ), 3 ), new( "Code/dup.cs", H( "e" ), 3 ), new( "Code/dup.cs", H( "f" ), 3 ), null,
            new( new string( 'a', 5000 ) + ".cs", H( "g" ), 3 ), new( "Code/fine.cs", H( "fine" ), 4 )
        };
        offer = CodeOffer.Build( bad, none );
        assert( offer.Files.Select( x => x.Path ).SequenceEqual( new[] { "Code/fine.cs" } ), "code_hostile_malformed_entries_are_never_offered" );
        assert( offer.Rejected.Count > 0 && offer.Rejected.Count <= 6 && offer.Rejected.All( x => !x.Contains( '\n' ) ), "code_hostile_rejections_are_bounded_single_line_notices" );
        assert( CodeOffer.Build( new List<FileIndexEntry> { new( "Code/neg.cs", H( "b" ), -1 ) }, none )?.Files.Count == 0, "code_hostile_negative_size_not_offered" );
    }

    private static void Trust( Action<bool, string> assert )
    {
        assert( CodeTrust.Permits( hostIdentityVerified: true, allowUnverified: false ), "code_trust_verified_host_permitted" );
        assert( CodeTrust.Permits( hostIdentityVerified: false, allowUnverified: true ), "code_trust_unverified_host_permitted_only_when_the_setting_is_ticked" );
        assert( !CodeTrust.Permits( hostIdentityVerified: false, allowUnverified: false ), "code_trust_unverified_host_fails_closed" );
        assert( CodeTrust.UnverifiedMessage == "The host's identity could not be verified, so code from this host cannot be received.", "code_trust_unverified_wording" );
        assert( CodeOfferText.Button( "Ann" ) == "Receive code from Ann" && CodeOfferText.Button( null ) == "Receive code from the host" && CodeOfferText.Button( "  " ) == "Receive code from the host", "code_card_button_wording" );
        assert( CodeOfferText.Button( "Ann\nBob" ) == "Receive code from Ann Bob", "code_card_button_name_is_single_line" );
        assert( CodeOfferText.ChangedNotice( "Ann", 3 ) == "Code changed on Ann: 3 file(s).", "code_card_changed_wording" );
    }

    // ---------------------------------------------------------------- real server

    private static void ServerRefusals( Action<bool, string> assert, string root )
    {
        string host = Path.Combine( root, "refusals", "host" );
        Write( host, "Code/a.cs", "class A {}" );
        Write( host, "Code/b.cs", "class B {}" );
        Write( host, "Libraries/foo/x.cs", "class Lib {}" );
        Write( host, "Assets/a.png", "png" );
        string ha = H2( "Code/a.cs", "class A {}" ), hb = H2( "Code/b.cs", "class B {}" ), hl = H2( "Libraries/foo/x.cs", "class Lib {}" );
        using var rig = Rig.Start( host, Path.Combine( root, "refusals" ) );
        var raw = rig.JoinRaw( "Mallory" );

        int Chunks() => raw.Seen.Count( p => p.Kind == "file-chunk" );
        void Ping()
        {
            int pongs = raw.Seen.Count( p => p.Kind == "pong" );
            raw.Wire.Send( new Packet { Kind = "ping" } );
            rig.PumpUntil( () => raw.Seen.Count( p => p.Kind == "pong" ) > pongs );
        }

        raw.Wire.Send( new Packet { Kind = "file-get", Path = "Code/a.cs", Sha256 = ha } );
        Ping();
        assert( Chunks() == 0 && raw.Seen.Any( p => p.Kind == "file-notice" && p.Path == "Code/a.cs" ), "code_server_refuses_a_code_get_without_consent" );
        raw.Seen.Clear();

        raw.Wire.Send( new Packet { Kind = "code-consent", Hashes = new List<string> { hb } } );
        raw.Wire.Send( new Packet { Kind = "file-get", Path = "Code/a.cs", Sha256 = ha } );
        Ping();
        assert( Chunks() == 0 && raw.Seen.Any( p => p.Kind == "file-notice" && p.Path == "Code/a.cs" ), "code_server_consent_is_bound_to_the_exact_hashes_listed" );
        raw.Seen.Clear();

        raw.Wire.Send( new Packet { Kind = "code-consent", Hashes = new List<string> { hl } } );
        raw.Wire.Send( new Packet { Kind = "file-get", Path = "Libraries/foo/x.cs", Sha256 = hl } );
        Ping();
        assert( Chunks() == 0 && raw.Seen.Any( p => p.Kind == "file-notice" && p.Path == "Libraries/foo/x.cs" ), "code_server_never_serves_libraries_even_with_consent" );
        raw.Seen.Clear();

        raw.Wire.Send( new Packet { Kind = "code-consent", Hashes = new List<string> { ha } } );
        raw.Wire.Send( new Packet { Kind = "file-get", Path = "Code/b.cs", Sha256 = hb } );
        Ping();
        assert( Chunks() == 0, "code_server_consent_for_one_file_does_not_cover_another" );
        raw.Seen.Clear();

        raw.Wire.Send( new Packet { Kind = "file-get", Path = "Code/a.cs", Sha256 = ha } );
        rig.PumpUntil( () => Chunks() == 1 );
        assert( Encoding.UTF8.GetString( raw.Seen.First( p => p.Kind == "file-chunk" ).Data ) == "class A {}", "code_server_serves_a_consented_hash" );
        raw.Seen.Clear();

        // A second member has not consented: the first member's consent is not shared.
        var other = rig.JoinRaw( "Eve" );
        other.Wire.Send( new Packet { Kind = "file-get", Path = "Code/a.cs", Sha256 = ha } );
        other.Wire.Send( new Packet { Kind = "ping" } );
        rig.PumpUntil( () => other.Seen.Any( p => p.Kind == "pong" ) );
        assert( !other.Seen.Any( p => p.Kind == "file-chunk" ) && other.Seen.Any( p => p.Kind == "file-notice" ), "code_server_consent_is_per_member" );

        // A consent naming more hashes than any offer can hold, or a malformed hash, clears the member's consent instead of widening it.
        raw.Wire.Send( new Packet { Kind = "code-consent", Hashes = Enumerable.Range( 0, CodeTransferLimits.MaxFiles + 1 ).Select( i => H( "n" + i ) ).ToList() } );
        raw.Wire.Send( new Packet { Kind = "file-get", Path = "Code/a.cs", Sha256 = ha } );
        Ping();
        assert( Chunks() == 0, "code_server_oversized_consent_grants_nothing" );
        raw.Wire.Send( new Packet { Kind = "code-consent", Hashes = new List<string> { ha, "not-a-hash" } } );
        raw.Wire.Send( new Packet { Kind = "file-get", Path = "Code/a.cs", Sha256 = ha } );
        Ping();
        assert( Chunks() == 0, "code_server_malformed_consent_grants_nothing" );

        // The code index lists transferable code only, never Libraries or assets.
        raw.Seen.Clear();
        raw.Wire.Send( new Packet { Kind = "code-index" } );
        rig.PumpUntil( () => raw.Seen.Any( p => p.Kind == "code-index" && p.Final ) );
        var listed = raw.Seen.Where( p => p.Kind == "code-index" ).SelectMany( p => p.FileEntries ?? new List<FileIndexEntry>() ).Select( x => x.Path ).OrderBy( x => x, StringComparer.Ordinal ).ToList();
        assert( listed.SequenceEqual( new[] { "Code/a.cs", "Code/b.cs" } ), "code_server_code_index_lists_transferable_code_only" );
    }

    private static void NoCodeUpload( Action<bool, string> assert, string root )
    {
        string host = Path.Combine( root, "upload", "host" );
        string guest = Path.Combine( root, "upload", "guest" );
        Write( host, "Assets/a.png", "png" );
        Write( host, "Code/a.cs", "class A {}" );
        Write( guest, "Assets/a.png", "png" );
        Write( guest, "Code/a.cs", "class A {}" );
        using var rig = Rig.Start( host, Path.Combine( root, "upload" ) );
        var member = rig.JoinClient( "Guest", guest );
        rig.PumpUntil( () => member.Client.JoinReady );
        rig.PumpFor( 200 );
        Write( guest, "Code/a.cs", "class A { /* guest edit */ }" );
        Write( guest, "Code/new.cs", "class New {}" );
        Write( guest, "Editor/tool.cs", "class Tool {}" );
        Write( guest, "Assets/b.png", "an asset still goes" );
        rig.PumpUntil( () => member.Sent.Any( p => p.Kind == "file-put" && p.Path == "Assets/b.png" ) );
        rig.PumpFor( 300 );
        assert( !member.Sent.Any( p => ( p.Kind is "file-put" or "file-chunk" or "file-delete" ) && p.Path != null && !p.Path.StartsWith( "Assets/" ) ), "code_member_changed_code_is_never_uploaded" );
        assert( Read( host, "Code/a.cs" ) == "class A {}" && !File.Exists( Path.Combine( host, "Code", "new.cs" ) ), "code_host_never_writes_code_from_a_member" );
    }

    private static void EmptyProjectOnboarding( Action<bool, string> assert, string root )
    {
        string host = Path.Combine( root, "onboard", "host" );
        string guest = Path.Combine( root, "onboard", "guest" );
        byte[] png = Random( 1000, 1 ), vmat = Random( 300 * 1024, 2 ), sound = Random( 77, 3 );
        string big = string.Concat( Enumerable.Range( 0, 40000 ).Select( i => $"// line {i}\n" ) );
        Write( host, "Assets/tex/a1.png", png );
        Write( host, "Assets/mat/a2.vmat", vmat );
        Write( host, "Assets/snd/a3.sound", sound );
        Write( host, "Code/A.cs", big );
        Write( host, "Code/ui/B.razor", "<div>b</div>" );
        Write( host, "Editor/E.cs", "class E {}" );
        Write( host, "ProjectSettings/physics.config", "{ \"g\": 9 }" );
        Write( host, "Libraries/foo/x.cs", "class FromLibrary {}" );
        Write( host, ".git/config", "[core]" );
        Write( host, "Code/obj/gen.cs", "class Generated {}" );
        Write( host, "host.sbproj", "{ \"Title\": \"Host\" }" );

        Write( guest, "guest.sbproj", "{ \"Title\": \"Guest\" }" );
        Write( guest, "Libraries/collaboration/Editor/lib.cs", "class TheLibrary {}" );
        Write( guest, "Code/Mine.cs", "class Mine {}" );
        Write( guest, ".collaboration/state.json", "{}" );
        var guestBefore = SnapshotAll( guest );
        var hostBefore = SnapshotAll( host );

        using var rig = Rig.Start( host, Path.Combine( root, "onboard" ) );
        var member = rig.JoinClient( "Guest", guest, hostName: "Ann", verified: true );
        rig.PumpUntil( () => member.Client.JoinReady );
        rig.PumpFor( 300 );
        var offer = member.Client.Offer;
        assert( offer != null && offer.Files.Select( x => x.Path ).SequenceEqual( new[] { "Code/A.cs", "Code/ui/B.razor", "Editor/E.cs", "ProjectSettings/physics.config" } ), "code_onboard_offer_lists_the_four_code_files" );
        assert( member.Client.CodeState == CodeTransferState.Offered && member.Client.CodeTrusted, "code_onboard_state_is_offered_and_trusted" );
        assert( Read( guest, "Assets/tex/a1.png", binary: true ).SequenceEqual( png ) && Read( guest, "Assets/mat/a2.vmat", binary: true ).SequenceEqual( vmat ) && Read( guest, "Assets/snd/a3.sound", binary: true ).SequenceEqual( sound ), "code_onboard_assets_arrive_byte_exact_without_consent" );
        assert( !member.Sent.Any( p => p.Kind is "file-get" or "code-consent" && p.Path != null && !p.Path.StartsWith( "Assets/" ) ) && !member.Sent.Any( p => p.Kind == "code-consent" ), "code_no_code_requested_before_consent" );
        assert( !File.Exists( Path.Combine( guest, "Code", "A.cs" ) ) && !File.Exists( Path.Combine( guest, "Editor", "E.cs" ) ), "code_nothing_written_before_consent" );

        assert( member.Client.ConsentToCode( member.Send ), "code_onboard_consent_accepted" );
        assert( member.Sent.Count( p => p.Kind == "code-consent" ) == 1 && member.Sent.Last( p => p.Kind == "code-consent" ).Hashes.OrderBy( x => x ).SequenceEqual( offer.Hashes.OrderBy( x => x ) ), "code_consent_packet_lists_exactly_the_offered_hashes" );
        rig.PumpUntil( () => member.Client.CodeState == CodeTransferState.Received );
        assert( Read( guest, "Code/A.cs" ) == big && Read( guest, "Code/ui/B.razor" ) == "<div>b</div>" && Read( guest, "Editor/E.cs" ) == "class E {}" && Read( guest, "ProjectSettings/physics.config" ) == "{ \"g\": 9 }", "code_onboard_code_arrives_byte_exact_after_consent" );
        var requested = member.Sent.Where( p => p.Kind == "file-get" ).Select( p => p.Path ).ToList();
        assert( requested.All( x => !x.StartsWith( "Libraries/" ) && !x.StartsWith( ".git/" ) && !x.StartsWith( ".collaboration/" ) && !x.Contains( "/obj/" ) ), "code_onboard_libraries_and_git_never_requested" );
        assert( requested.Count == 7 && member.Sent.Count( p => p.Kind == "file-get" && !p.Path.StartsWith( "Assets/" ) ) == 4, "code_onboard_exactly_three_assets_and_four_code_files_requested" );
        assert( Snapshot( guest, "Libraries/" ).SequenceEqual( guestBefore.Where( x => x.Key.StartsWith( "Libraries/" ) ) ) && !File.Exists( Path.Combine( guest, "Libraries", "foo", "x.cs" ) ) && !Directory.Exists( Path.Combine( guest, ".git" ) ), "code_onboard_libraries_and_git_untouched_on_the_guest" );
        assert( Read( guest, "Code/Mine.cs" ) == "class Mine {}" && Read( guest, ".collaboration/state.json" ) == "{}" && File.Exists( Path.Combine( guest, "guest.sbproj" ) ) && Read( guest, "guest.sbproj" ) == "{ \"Title\": \"Guest\" }", "code_onboard_guest_extras_and_project_file_untouched" );
        assert( !File.Exists( Path.Combine( guest, "Code", "obj", "gen.cs" ) ), "code_onboard_build_output_not_transferred" );
        assert( SnapshotAll( host ).SequenceEqual( hostBefore ), "code_onboard_host_unchanged" );
        string received = member.Notices().FirstOrDefault( x => x.StartsWith( "Code received" ) );
        assert( received == "Code received (4 files). The editor will compile it now.", "code_onboard_received_notice_when_nothing_was_replaced" );
        assert( member.Client.Offer == null, "code_onboard_offer_is_gone_after_receiving" );
        assert( !Directory.EnumerateFiles( Path.Combine( guest, ".collaboration", "incoming" ) ).Any(), "code_onboard_leaves_no_part_files" );
        assert( !member.Sent.Any( p => p.Kind == "file-put" ), "code_onboard_nothing_pushed_back_to_the_host" );
    }

    private static void ChangedAfterConsent( Action<bool, string> assert, string root )
    {
        string host = Path.Combine( root, "changed", "host" );
        string guest = Path.Combine( root, "changed", "guest" );
        Write( host, "Code/a.cs", "class A { /* v1 */ }" );
        Write( host, "Code/b.cs", "class B {}" );
        Write( guest, "Code/a.cs", "class A { /* guest */ }" );
        using var rig = Rig.Start( host, Path.Combine( root, "changed" ) );
        var member = rig.JoinClient( "Guest", guest, hostName: "Ann", verified: true );
        rig.PumpUntil( () => member.Client.Offer != null );
        assert( member.Client.Offer.ChangeNotice == null, "code_changed_first_offer_has_no_change_notice" );
        member.Client.ConsentToCode( member.Send );
        rig.PumpUntil( () => member.Client.CodeState == CodeTransferState.Received );
        string done = member.Notices().First( x => x.StartsWith( "Code received" ) );
        assert( done == "Code received (2 files). The editor will compile it now. Previous versions are in .collaboration/recovery/files-20261003-123456.", "code_changed_received_notice_names_the_recovery_folder" );
        assert( Read( guest, ".collaboration/recovery/files-20261003-123456/Code/a.cs" ) == "class A { /* guest */ }", "code_changed_replaced_file_is_backed_up" );

        // The host edits code afterwards: a new offer appears, with its own notice, and nothing is applied until the person agrees again.
        Write( host, "Code/a.cs", "class A { /* v2 */ }" );
        rig.PumpUntil( () => member.Client.Offer != null );
        assert( member.Client.Offer.Files.Select( x => x.Path ).SequenceEqual( new[] { "Code/a.cs" } ) && member.Client.CodeState == CodeTransferState.Offered, "code_changed_a_new_offer_lists_the_changed_file" );
        assert( member.Client.Offer.ChangeNotice == "Code changed on Ann: 1 file(s).", "code_changed_new_offer_has_the_change_notice" );
        rig.PumpFor( 300 );
        assert( Read( guest, "Code/a.cs" ) == "class A { /* v1 */ }" && member.Sent.Count( p => p.Kind == "code-consent" ) == 1 && member.Sent.Count( p => p.Kind == "file-get" ) == 2, "code_changed_new_offer_is_never_auto_applied" );
        var firstFingerprint = member.Client.Offer.Fingerprint;
        member.Client.DeclineCode();
        rig.PumpFor( 300 );
        assert( member.Client.Offer == null && member.Client.CodeState == CodeTransferState.Declined, "code_changed_not_now_hides_the_offer" );
        Write( host, "Code/a.cs", "class A { /* v3 */ }" );
        rig.PumpUntil( () => member.Client.Offer != null );
        assert( member.Client.Offer.Fingerprint != firstFingerprint && member.Client.Offer.ChangeNotice == "Code changed on Ann: 1 file(s).", "code_changed_after_not_now_a_different_set_is_offered_again" );
        assert( member.Client.ConsentToCode( member.Send ), "code_changed_second_consent_accepted" );
        rig.PumpUntil( () => member.Client.CodeState == CodeTransferState.Received );
        assert( Read( guest, "Code/a.cs" ) == "class A { /* v3 */ }" && member.Sent.Count( p => p.Kind == "code-consent" ) == 2, "code_changed_second_consent_applies_the_new_set" );
        rig.PumpFor( 300 );
        assert( member.Client.Offer == null, "code_changed_no_offer_once_in_sync" );
    }

    private static void BinaryContent( Action<bool, string> assert, string root )
    {
        string host = Path.Combine( root, "binary", "host" );
        string guest = Path.Combine( root, "binary", "guest" );
        Write( host, "Code/good.cs", "class Good {}" );
        Write( host, "Code/bad.cs", new byte[] { 0x63, 0x6c, 0x61, 0x73, 0x73, 0x00, 0x7d } );
        using var rig = Rig.Start( host, Path.Combine( root, "binary" ) );
        var member = rig.JoinClient( "Guest", guest, hostName: "Ann", verified: true );
        rig.PumpUntil( () => member.Client.Offer != null );
        member.Client.ConsentToCode( member.Send );
        rig.PumpUntil( () => member.Client.CodeState == CodeTransferState.Received );
        var notices = member.Notices();
        assert( Read( guest, "Code/good.cs" ) == "class Good {}" && !File.Exists( Path.Combine( guest, "Code", "bad.cs" ) ), "code_binary_nul_file_is_refused_and_not_written" );
        assert( notices.Contains( "The file \"Code/bad.cs\" contains binary data (a NUL byte), which a code file must not, and was not applied." ), "code_binary_refusal_notice_wording" );
        assert( notices.Any( x => x.StartsWith( "Code received for 1 of 2 files" ) ), "code_binary_result_notice_counts_the_refused_file" );
        assert( member.Sent.Count( p => p.Kind == "file-get" && p.Path == "Code/bad.cs" ) == 1, "code_binary_refusal_is_not_retried" );
    }

    private static void LegacyHost( Action<bool, string> assert, string root )
    {
        // A host that does not advertise code transfer is told nothing new: no offer, no code-index, the former notice.
        string host = Path.Combine( root, "legacy", "host" );
        string guest = Path.Combine( root, "legacy", "guest" );
        Write( host, "Code/a.cs", "class A {}" );
        using var rig = Rig.Start( host, Path.Combine( root, "legacy" ) );
        var member = rig.JoinClient( "Guest", guest, hostName: "Ann", verified: true, hostHasCode: false );
        rig.PumpUntil( () => member.Client.JoinReady );
        rig.PumpFor( 300 );
        assert( member.Client.Offer == null && !member.Sent.Any( p => p.Kind is "code-index" or "code-consent" ) && member.Notices().Any( x => x.StartsWith( "Your code differs" ) ), "code_legacy_host_gets_the_former_notice_and_no_new_messages" );
        assert( !member.Client.ConsentToCode( member.Send ), "code_legacy_host_consent_is_refused_locally" );
    }

    // ---------------------------------------------------------------- harness

    private sealed class FakeMember
    {
        public Wire Wire;

        public FileSyncClient Client;

        public Packet Welcome;

        public readonly List<Packet> Seen = new();

        public readonly List<Packet> Sent = new();

        public readonly List<string> NoticeLog = new();

        public void Send( Packet packet )
        {
            Sent.Add( packet );
            Wire.Send( packet );
        }

        public List<string> Notices()
        {
            for ( string n = Client?.TakeNotice(); n != null; n = Client.TakeNotice() ) NoticeLog.Add( n );
            return NoticeLog;
        }
    }

    private sealed class Rig : IDisposable
    {
        public SessionServer Server;

        private int port;

        private readonly List<FakeMember> members = new();

        public static Rig Start( string hostRoot, string directory )
        {
            var rig = new Rig();
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
            if ( rig.Server == null ) throw new IOException( "Unable to reserve a loopback port for the code transfer tests.", last );
            rig.Server.AllowUnverifiedPeers = true;
            rig.Server.FileProjectRoot = hostRoot;
            rig.Server.HostEngineBaseline = Engine;
            rig.Server.hostManifest = "host-manifest";
            return rig;
        }

        private FakeMember Connect( string name )
        {
            var member = new FakeMember { Wire = Wire.Connect( "127.0.0.1", port ).GetAwaiter().GetResult() };
            members.Add( member );
            member.Wire.Send( new Packet
            {
                Kind = "hello", Peer = Guid.NewGuid().ToString( "N" ), ProtocolVersion = Protocol.CurrentVersion, Mode = CollaborationMode.Source,
                Name = name, Token = "test-token", Project = "test-project", Baseline = "guest-baseline", EngineBaseline = Engine,
                SteamId = ( 76561198000000001L + members.Count ).ToString(), SteamToken = "a-realistic-session-token", OrgIdent = "cyn", LibraryVersion = "1.1.1", ProjectManifest = "guest-manifest"
            } );
            PumpUntil( () => member.Seen.Any( p => p.Kind == "welcome" || p.Error != null ) );
            member.Welcome = member.Seen.FirstOrDefault( p => p.Kind == "welcome" );
            return member;
        }

        public FakeMember JoinRaw( string name ) => Connect( name );

        public FakeMember JoinClient( string name, string root, string hostName = "the host", bool verified = false, bool hostHasCode = true )
        {
            Directory.CreateDirectory( root );
            var member = Connect( name );
            member.Client = new FileSyncClient( root, () => FixedClock ) { PollInterval = TimeSpan.Zero, HostName = hostName, HostIdentityVerified = verified, HostHasCode = hostHasCode, CodePollInterval = TimeSpan.FromMilliseconds( 50 ) };
            member.Client.Begin( member.Send );
            return member;
        }

        public void PumpOnce()
        {
            Server.Pump();
            foreach ( var member in members )
            {
                while ( member.Wire.Incoming.TryDequeue( out var packet ) )
                {
                    if ( member.Client == null || !member.Client.Handle( packet, member.Send, Array.Empty<string>() ) ) member.Seen.Add( packet );
                }
                if ( member.Wire.Failure == null ) member.Client?.Poll( member.Send, Array.Empty<string>() );
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
            throw new TimeoutException( "The fake members did not reach the expected code transfer state." );
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

    private static string H2( string path, string content ) => FileSyncIndex.Hash( path, Encoding.UTF8.GetBytes( content ) );

    private static string NewDirectory()
    {
        string root = Environment.GetEnvironmentVariable( "TMPDIR" );
        if ( string.IsNullOrWhiteSpace( root ) ) root = Path.GetTempPath();
        string path = Path.Combine( root, "teamcreate-coretests", "code-" + Guid.NewGuid().ToString( "N" ) );
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

    // Every file under the root as sorted project-relative paths mapped to a content hash, the library's own working folders excluded
    // (they change as a side effect of receiving).
    private static SortedDictionary<string, string> SnapshotAll( string root )
    {
        var result = new SortedDictionary<string, string>( StringComparer.Ordinal );
        foreach ( string file in Directory.EnumerateFiles( root, "*", SearchOption.AllDirectories ) )
        {
            string relative = Path.GetRelativePath( root, file ).Replace( '\\', '/' );
            if ( relative.StartsWith( ".collaboration/incoming/", StringComparison.Ordinal ) || relative.StartsWith( ".collaboration/recovery/", StringComparison.Ordinal ) ) continue;
            result[relative] = Convert.ToHexString( System.Security.Cryptography.SHA256.HashData( File.ReadAllBytes( file ) ) );
        }
        return result;
    }

    private static IEnumerable<KeyValuePair<string, string>> Snapshot( string root, string prefix ) => SnapshotAll( root ).Where( x => x.Key.StartsWith( prefix, StringComparison.Ordinal ) );
}
