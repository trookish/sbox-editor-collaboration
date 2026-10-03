using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TeamCreate;

/// <summary>
/// Mapping-tool geometry (blob) synchronization: the content-addressed store, the placeholder rules, the version rate limit and the
/// BlobExchange endpoint that holds a scene packet until the geometry it names has arrived. See docs/BLOB-SYNC.md.
/// </summary>
static class BlobSyncTests
{
    // A real MeshBlob (3215 bytes) taken from the compiled installed scene level2_blockout, as the base64 string a bare
    // GameObject.Serialize() writes for MeshComponent.Mesh.Data. SHA-256 of the decoded bytes: d576c1c0...e833.
    const string SampleMeshBase64 = "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";

    const string SampleMeshSha = "d576c1c0b1d88a2556dd2ec9db82ae8fa3fa8a7aa820d08e54a78c3435f7e833";

    // The saved-scene form of the same property, copied from pregamelobby.bff7fe0ef510ba14.scene:232-238 (a $blob GUID, no bytes).
    const string SavedSceneMesh = "{ \"Data\": { \"$blob\": \"3e85ca21-ade2-89a7-a712-dc95b056b8ed\" }, \"Materials\": [ \"prefabs/city/water/water_map_t.vmat\" ] }";

    static readonly DateTime T0 = new DateTime( 2026, 10, 3, 12, 0, 0, DateTimeKind.Utc );

    static string TempDir()
    {
        string dir = Path.Combine( Path.GetTempPath(), "blobtests-" + Guid.NewGuid().ToString( "N" ) );
        Directory.CreateDirectory( dir );
        return dir;
    }

    static byte[] Bytes( int length, int seed )
    {
        byte[] data = new byte[length];
        new Random( seed ).NextBytes( data );
        return data;
    }

    static ObjectState MeshObject( string id, string data )
    {
        JsonObject json = new JsonObject
        {
            ["__guid"] = id,
            ["Name"] = "Floor",
            ["Components"] = new JsonArray
            {
                new JsonObject
                {
                    ["__type"] = "Sandbox.MeshComponent",
                    ["__guid"] = "2004dcd3-12a3-4338-a48e-d35c734d06ea",
                    ["Mesh"] = new JsonObject { ["Data"] = data, ["Materials"] = new JsonArray( "prefabs/city/water/water_map_t.vmat" ) }
                }
            }
        };
        return new ObjectState( id, "", json );
    }

    static string Id( int n ) => "00000000-0000-0000-0000-" + n.ToString( "D12" );

    static string Base64Of( byte[] bytes ) => Convert.ToBase64String( bytes );

    // Two endpoints joined by in-memory queues; Deliver moves every queued packet to the other side until both are quiet.
    sealed class Link
    {
        public readonly BlobStore StoreA, StoreB;
        public readonly BlobExchange A, B;
        public readonly List<Packet> AtoB = new(), BtoA = new();
        public DateTime Now = T0;

        public Link( long budgetB = BlobLimits.MaxPendingBytes )
        {
            StoreA = new BlobStore( null, 512L * 1024 * 1024, 0 );
            StoreB = new BlobStore( null, 512L * 1024 * 1024, 0 );
            A = new BlobExchange( StoreA, p => AtoB.Add( p ), () => Now );
            B = new BlobExchange( StoreB, p => BtoA.Add( p ), () => Now, new BlobBudget( budgetB ) );
        }

        public int Deliver( int rounds = 200 )
        {
            int moved = 0;
            for ( int i = 0; i < rounds; i++ )
            {
                A.Pump();
                B.Pump();
                if ( AtoB.Count == 0 && BtoA.Count == 0 ) break;
                foreach ( Packet p in AtoB.ToArray() ) { AtoB.Remove( p ); B.Handle( p ); moved++; }
                foreach ( Packet p in BtoA.ToArray() ) { BtoA.Remove( p ); A.Handle( p ); moved++; }
            }
            return moved;
        }
    }

    static Packet SnapshotNaming( params string[] hashes )
    {
        List<ObjectState> objects = new();
        for ( int i = 0; i < hashes.Length; i++ )
        {
            objects.Add( MeshObject( Id( i + 1 ), BlobRules.Placeholder( hashes[i] ) ) );
        }
        return new Packet { Kind = "snapshot", Scene = "scenes/a.scene", Objects = objects };
    }

    public static void Run( Action<bool, string> assert )
    {
        StoreTests( assert );
        RuleTests( assert );
        LimiterTests( assert );
        MergeTests( assert );
        ExchangeTests( assert );
        RefusalTests( assert );
    }

    // ---- BlobStore -------------------------------------------------------------------------------------------------------------

    static void StoreTests( Action<bool, string> assert )
    {
        BlobStore memory = new BlobStore( null, 1024 * 1024, 0 );
        byte[] one = Bytes( 5000, 1 );
        string hash = memory.Put( one, out string error );
        assert( hash == BlobStore.HashOf( one ) && error == null && hash.Length == 64 && hash == hash.ToLowerInvariant(), "blob_store_put_returns_lowercase_sha256" );
        assert( memory.Has( hash ) && memory.TryGet( hash, out byte[] back ) && back.SequenceEqual( one ), "blob_store_get_returns_the_bytes" );

        memory.Put( one, out _ );
        assert( memory.MemoryCount == 1 && memory.MemoryBytes == 5000, "blob_store_dedupes_identical_content" );

        // Eviction at the cap: three 400 KiB blobs in a 1 MiB cache. The first is the least recently used and goes; reading the
        // second before the third arrives protects it.
        BlobStore small = new BlobStore( null, 1024 * 1024, 0 );
        string first = small.Put( Bytes( 400 * 1024, 11 ), out _ );
        string second = small.Put( Bytes( 400 * 1024, 12 ), out _ );
        small.TryGet( first, out _ );
        string third = small.Put( Bytes( 400 * 1024, 13 ), out _ );
        assert( small.MemoryBytes <= 1024 * 1024, "blob_store_memory_never_exceeds_the_cap" );
        assert( small.Has( first ) && !small.Has( second ) && small.Has( third ), "blob_store_evicts_the_least_recently_used_first" );

        // Hash verification on write: bytes that do not hash to the claimed key are refused and not stored.
        string wrong = BlobStore.HashOf( Bytes( 4096, 5 ) );
        bool accepted = memory.Put( wrong, Bytes( 4096, 6 ), out string wrongError );
        assert( !accepted && wrongError != null && !memory.Has( wrong ), "blob_store_refuses_bytes_that_do_not_match_the_hash" );

        // Hostile keys never reach the file system.
        string dir = TempDir();
        BlobStore disk = new BlobStore( dir, 1024 * 1024, 1024 * 1024 );
        string[] hostile = { "../../etc/passwd", "..\\..\\x", new string( 'a', 63 ), new string( 'A', 64 ), new string( 'g', 64 ), "", null, new string( 'a', 60 ) + "/..", "C:" + new string( 'a', 62 ) };
        bool allRejected = true;
        foreach ( string key in hostile )
        {
            allRejected &= !BlobStore.ValidHash( key ) && !disk.Has( key ) && !disk.TryGet( key, out _ ) && !disk.Put( key, Bytes( 100, 1 ), out _ );
        }
        assert( allRejected, "blob_store_rejects_path_like_and_malformed_hashes" );
        assert( BlobStore.ValidHash( SampleMeshSha ), "blob_store_accepts_a_lowercase_sha256" );

        // Size refusal: a blob over 32 MiB is not stored.
        BlobStore big = new BlobStore( null, 512L * 1024 * 1024, 0 );
        string tooBig = big.Put( new byte[BlobLimits.MaxBlobBytes + 1], out string bigError );
        assert( tooBig == null && bigError != null && bigError.Contains( "32 MiB" ), "blob_store_refuses_a_blob_over_32_mib" );
        string atLimit = big.Put( new byte[BlobLimits.MaxBlobBytes], out _ );
        assert( atLimit != null, "blob_store_accepts_a_blob_of_exactly_32_mib" );

        // Disk: written atomically (no temporary left), survives a new instance, verified on read.
        byte[] persisted = Bytes( 9000, 21 );
        string pHash = disk.Put( persisted, out _ );
        string blobDir = Path.Combine( dir, ".collaboration", "blobs" );
        assert( File.Exists( Path.Combine( blobDir, pHash ) ) && Directory.GetFiles( blobDir ).All( x => !x.Contains( ".tmp" ) ), "blob_store_writes_atomically_without_leftovers" );
        BlobStore reopened = new BlobStore( dir, 1024 * 1024, 1024 * 1024 );
        assert( reopened.TryGet( pHash, out byte[] fromDisk ) && fromDisk.SequenceEqual( persisted ), "blob_store_serves_from_disk_after_restart" );

        File.WriteAllBytes( Path.Combine( blobDir, pHash ), Bytes( 9000, 22 ) );
        BlobStore corrupt = new BlobStore( dir, 1024 * 1024, 1024 * 1024 );
        assert( !corrupt.TryGet( pHash, out _ ) && !File.Exists( Path.Combine( blobDir, pHash ) ), "blob_store_verifies_the_hash_on_read_and_drops_corrupt_files" );

        // Disk eviction by age down to the cap: three 400 KiB files in a 1 MiB cap, the oldest written goes.
        string pruneDir = TempDir();
        BlobStore pruned = new BlobStore( pruneDir, 8 * 1024 * 1024, 1024 * 1024 );
        string[] hashes = new string[3];
        for ( int i = 0; i < 3; i++ )
        {
            hashes[i] = pruned.Put( Bytes( 400 * 1024, 30 + i ), out _ );
            File.SetLastWriteTimeUtc( Path.Combine( pruneDir, ".collaboration", "blobs", hashes[i] ), T0.AddMinutes( i ) );
        }
        pruned.Prune();
        string[] remaining = Directory.GetFiles( Path.Combine( pruneDir, ".collaboration", "blobs" ) ).Select( Path.GetFileName ).ToArray();
        assert( remaining.Length == 2 && !remaining.Contains( hashes[0] ) && remaining.Contains( hashes[2] ), "blob_store_prunes_the_disk_cache_oldest_first_to_the_cap" );
    }

    // ---- BlobRules -------------------------------------------------------------------------------------------------------------

    static void RuleTests( Action<bool, string> assert )
    {
        string placeholder = BlobRules.Placeholder( SampleMeshSha );
        assert( placeholder == "teamcreate-blob:sha256:" + SampleMeshSha, "blob_placeholder_has_the_documented_form" );
        assert( BlobRules.TryParsePlaceholder( placeholder, out string parsed ) && parsed == SampleMeshSha, "blob_placeholder_round_trips" );
        assert( !BlobRules.TryParsePlaceholder( placeholder + "x", out _ ) && !BlobRules.TryParsePlaceholder( placeholder.ToUpperInvariant(), out _ )
            && !BlobRules.TryParsePlaceholder( "teamcreate-blob:sha256:../../x", out _ ) && !BlobRules.TryParsePlaceholder( null, out _ ), "blob_placeholder_parse_is_exact" );
        assert( !BlobRules.IsExternalizable( placeholder ), "blob_placeholder_is_never_externalized_again" );

        assert( BlobRules.IsExternalizable( SampleMeshBase64 ), "blob_real_mesh_base64_is_externalizable" );
        assert( !BlobRules.IsExternalizable( "AAAA" ) && !BlobRules.IsExternalizable( new string( 'A', BlobLimits.MinExternalChars - 4 ) ), "blob_short_strings_stay_inline" );
        assert( !BlobRules.IsExternalizable( new string( 'a', 5000 ) + " text" ) && !BlobRules.IsExternalizable( SampleMeshBase64 + "=" ), "blob_text_and_malformed_base64_stay_inline" );
        // 4000 bytes end in "==": the last character carries four padding bits that must be zero. Setting one of them still decodes
        // (to the same bytes) but re-encodes differently, so restoring the decoded bytes would not give the original text back.
        string canonical = Base64Of( Bytes( 4000, 3 ) );
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        int at = canonical.Length - 3;
        string tampered = canonical.Substring( 0, at ) + alphabet[alphabet.IndexOf( canonical[at] ) | 1] + "==";
        assert( canonical.EndsWith( "==" ) && tampered != canonical && Convert.FromBase64String( tampered ).SequenceEqual( Convert.FromBase64String( canonical ) ), "blob_test_setup_non_canonical_text_decodes_to_the_same_bytes" );
        assert( BlobRules.IsExternalizable( canonical ) && !BlobRules.IsExternalizable( tampered ), "blob_non_canonical_base64_stays_inline_so_the_original_text_is_never_altered" );

        // Externalize replaces the string wherever it sits, keeps the rest, and the restored text is the exact original.
        ObjectState mesh = MeshObject( Id( 1 ), SampleMeshBase64 );
        JsonObject data = (JsonObject)mesh.Data.DeepClone();
        Dictionary<string, byte[]> sunk = new();
        List<string> refused = new();
        int replaced = BlobRules.Externalize( data, ( hash, bytes ) => sunk[hash] = bytes, refused );
        string after = data["Components"][0]["Mesh"]["Data"].GetValue<string>();
        assert( replaced == 1 && refused.Count == 0 && after == placeholder, "blob_externalize_replaces_the_inline_geometry_with_its_hash" );
        assert( sunk.ContainsKey( SampleMeshSha ) && sunk[SampleMeshSha].Length == 3215 && BlobStore.HashOf( sunk[SampleMeshSha] ) == SampleMeshSha, "blob_externalize_hands_over_the_decoded_bytes" );
        assert( data["Name"].GetValue<string>() == "Floor" && data["Components"][0]["Mesh"]["Materials"][0].GetValue<string>() == "prefabs/city/water/water_map_t.vmat", "blob_externalize_leaves_everything_else_untouched" );
        assert( data.ToJsonString().Length < 600, "blob_externalized_state_is_small" );
        assert( BlobRules.Referenced( data ).SequenceEqual( new[] { SampleMeshSha } ), "blob_referenced_lists_the_hashes_of_a_state" );

        BlobStore store = new BlobStore( null, 1024 * 1024, 0 );
        foreach ( var pair in sunk ) store.Put( pair.Key, pair.Value, out _ );
        JsonObject restored = BlobRules.Internalize( data, store, out List<string> missing );
        assert( missing.Count == 0 && restored != null && restored.ToJsonString() == mesh.Data.ToJsonString(), "blob_internalize_restores_the_exact_original_state" );
        assert( data["Components"][0]["Mesh"]["Data"].GetValue<string>() == placeholder, "blob_internalize_does_not_modify_its_input" );
        JsonObject lacking = BlobRules.Internalize( data, new BlobStore( null, 1024, 0 ), out List<string> gone );
        assert( lacking == null && gone.SequenceEqual( new[] { SampleMeshSha } ), "blob_internalize_reports_missing_blobs_and_returns_nothing" );

        // The saved-scene form is not ours: a $blob reference object is not a string and is left exactly as it is.
        JsonObject saved = (JsonObject)JsonNode.Parse( "{ \"Mesh\": " + SavedSceneMesh + " }" );
        string before = saved.ToJsonString();
        int none = BlobRules.Externalize( saved, ( _, _ ) => { }, new List<string>() );
        assert( none == 0 && saved.ToJsonString() == before && BlobRules.Referenced( saved ).Count == 0, "blob_saved_scene_dollar_blob_reference_is_left_alone" );

        // A string just under and just over the externalization threshold.
        string justUnder = Base64Of( new byte[( BlobLimits.MinExternalChars - 4 ) / 4 * 3] );
        string atThreshold = Base64Of( new byte[BlobLimits.MinExternalChars / 4 * 3] );
        assert( justUnder.Length < BlobLimits.MinExternalChars && !BlobRules.IsExternalizable( justUnder ) && atThreshold.Length == BlobLimits.MinExternalChars && BlobRules.IsExternalizable( atThreshold ), "blob_threshold_is_inclusive" );

        // Size refusal: decoded over 32 MiB is not externalized, stays inline and is reported by path.
        JsonObject huge = new JsonObject { ["Components"] = new JsonArray( new JsonObject { ["Mesh"] = new JsonObject { ["Data"] = Base64Of( new byte[BlobLimits.MaxBlobBytes + 3] ) } } ) };
        List<string> hugeRefused = new();
        int hugeReplaced = BlobRules.Externalize( huge, ( _, _ ) => throw new InvalidOperationException( "must not be handed over" ), hugeRefused );
        assert( hugeReplaced == 0 && hugeRefused.Count == 1 && hugeRefused[0].Contains( "Data" ), "blob_externalize_refuses_geometry_over_32_mib" );
        JsonObject exact = new JsonObject { ["Data"] = Base64Of( new byte[BlobLimits.MaxBlobBytes] ) };
        assert( BlobRules.Externalize( exact, ( _, _ ) => { }, new List<string>() ) == 1, "blob_externalize_accepts_geometry_of_exactly_32_mib" );
    }

    // ---- Rate limit ------------------------------------------------------------------------------------------------------------

    static void LimiterTests( Action<bool, string> assert )
    {
        DateTime now = T0;
        BlobVersionLimiter limiter = new BlobVersionLimiter( () => now );
        string[] v1 = { new string( '1', 64 ) }, v2 = { new string( '2', 64 ) }, v3 = { new string( '3', 64 ) };
        assert( limiter.Admit( "a", v1 ), "blob_limiter_admits_the_first_version" );
        now = T0.AddMilliseconds( 100 );
        assert( !limiter.Admit( "a", v2 ), "blob_limiter_holds_a_second_version_within_500_ms" );
        assert( limiter.Admit( "a", v1 ), "blob_limiter_always_admits_the_version_already_sent" );
        assert( limiter.Admit( "b", v2 ), "blob_limiter_is_per_object" );
        now = T0.AddMilliseconds( 499 );
        assert( !limiter.Admit( "a", v2 ), "blob_limiter_still_holds_at_499_ms" );
        now = T0.AddMilliseconds( 500 );
        assert( limiter.Admit( "a", v3 ), "blob_limiter_admits_the_latest_version_at_500_ms" );
        now = T0.AddMilliseconds( 600 );
        assert( !limiter.Admit( "a", v2 ), "blob_limiter_restarts_the_window_after_each_admission" );
        assert( limiter.Admit( "c", Array.Empty<string>() ), "blob_limiter_ignores_objects_without_geometry" );
        limiter.Forget( "a" );
        assert( limiter.Admit( "a", v2 ), "blob_limiter_forgets_a_deleted_object" );
    }

    // ---- Merge: the sidecar is an ordinary field --------------------------------------------------------------------------------

    static void MergeTests( Action<bool, string> assert )
    {
        string h1 = BlobRules.Placeholder( new string( '1', 64 ) ), h2 = BlobRules.Placeholder( new string( '2', 64 ) ), h3 = BlobRules.Placeholder( new string( '3', 64 ) );
        ObjectState before = MeshObject( Id( 1 ), h1 );
        ObjectState hostNow = MeshObject( Id( 1 ), h2 );
        ObjectState mine = MeshObject( Id( 1 ), h3 );
        assert( !StateRules.Equal( before, mine ), "blob_changed_geometry_is_a_changed_object" );
        assert( StateRules.Diff( new Dictionary<string, ObjectState> { [Id( 1 )] = before }, new Dictionary<string, ObjectState> { [Id( 1 )] = mine } ).Count == 1, "blob_changed_geometry_appears_in_the_diff" );
        assert( PropertyMerge.TryMerge( hostNow, before, mine, out ObjectState merged, out List<string> clashed ), "blob_two_edits_of_one_mesh_merge" );
        assert( merged.Data["Components"][0]["Mesh"]["Data"].GetValue<string>() == h3, "blob_the_later_mesh_edit_wins" );
        assert( clashed.SequenceEqual( new[] { "Mesh geometry" } ), "blob_the_clash_is_named_mesh_geometry" );
    }

    // ---- BlobExchange ----------------------------------------------------------------------------------------------------------

    static void ExchangeTests( Action<bool, string> assert )
    {
        // Hold until complete: a 600 KiB blob is three chunks. The packet stays held after two chunks and is released by the third.
        {
            Link link = new Link();
            byte[] geometry = Bytes( 600 * 1024, 41 );
            string hash = link.StoreA.Put( geometry, out _ );
            Packet snapshot = SnapshotNaming( hash );
            assert( link.B.Offer( snapshot ) == BlobOffer.Held, "blob_a_packet_naming_missing_geometry_is_held" );
            assert( link.B.HeldCount == 1 && !link.B.TryTakeReady( out _ ), "blob_a_held_packet_is_not_released_early" );
            assert( link.BtoA.Count == 0 || link.BtoA.All( x => x.Kind == "blob-need" ), "blob_the_receiver_asks_with_blob_need" );
            link.B.Pump();
            assert( link.BtoA.Count == 1 && link.BtoA[0].Kind == "blob-need" && link.BtoA[0].Hashes.SequenceEqual( new[] { hash } ), "blob_need_names_exactly_the_missing_hashes" );

            // Deliver the request and the first two chunks only, by hand.
            Packet need = link.BtoA[0]; link.BtoA.Clear();
            link.A.Handle( need );
            link.A.Pump();
            assert( link.AtoB.Count == 3 && link.AtoB.All( x => x.Kind == "blob-data" && x.Total == geometry.Length ), "blob_the_sender_streams_the_blob_in_256_kib_chunks" );
            List<Packet> chunks = link.AtoB.ToList(); link.AtoB.Clear();
            link.B.Handle( chunks[0] );
            link.B.Handle( chunks[1] );
            assert( !link.B.TryTakeReady( out _ ) && link.B.HeldCount == 1 && !link.StoreB.Has( hash ), "blob_two_of_three_chunks_do_not_release_the_packet" );
            link.B.Handle( chunks[2] );
            assert( link.StoreB.Has( hash ) && link.B.TryTakeReady( out Packet released ) && ReferenceEquals( released, snapshot ) && link.B.HeldCount == 0, "blob_the_last_verified_chunk_releases_the_packet" );
        }

        // End to end through the link, with the window: a 5 MiB blob is 20 chunks, never more than eight unacknowledged.
        {
            Link link = new Link();
            byte[] geometry = Bytes( 5 * 1024 * 1024, 42 );
            string hash = link.StoreA.Put( geometry, out _ );
            Packet snapshot = SnapshotNaming( hash );
            link.B.Offer( snapshot );
            int maxInFlight = 0;
            for ( int round = 0; round < 200 && link.B.HeldCount > 0; round++ )
            {
                link.A.Pump(); link.B.Pump();
                maxInFlight = Math.Max( maxInFlight, link.AtoB.Count( x => x.Kind == "blob-data" ) );
                foreach ( Packet p in link.AtoB.ToArray() ) { link.AtoB.Remove( p ); link.B.Handle( p ); }
                foreach ( Packet p in link.BtoA.ToArray() ) { link.BtoA.Remove( p ); link.A.Handle( p ); }
            }
            assert( link.StoreB.TryGet( hash, out byte[] got ) && got.SequenceEqual( geometry ) && link.B.TryTakeReady( out _ ), "blob_a_large_blob_arrives_intact_through_the_window" );
            assert( maxInFlight <= FileSyncLimits.Window && maxInFlight > 0, "blob_chunks_in_flight_never_exceed_the_window" );
        }

        // Nothing missing: the packet is processed at once. A packet behind a held one waits for it (order is kept).
        {
            Link link = new Link();
            string have = link.StoreB.Put( Bytes( 5000, 43 ), out _ );
            string lacking = link.StoreA.Put( Bytes( 5000, 44 ), out _ );
            assert( link.B.Offer( SnapshotNaming( have ) ) == BlobOffer.Process, "blob_a_packet_whose_geometry_is_present_is_processed_at_once" );
            Packet head = SnapshotNaming( lacking );
            Packet behind = SnapshotNaming( have );
            link.B.Offer( head );
            assert( link.B.Offer( behind ) == BlobOffer.Held, "blob_a_packet_behind_a_held_one_is_held_too" );
            link.Deliver();
            bool first = link.B.TryTakeReady( out Packet one );
            bool second = link.B.TryTakeReady( out Packet two );
            assert( first && second && ReferenceEquals( one, head ) && ReferenceEquals( two, behind ), "blob_held_packets_are_released_in_arrival_order" );
            assert( link.B.Offer( new Packet { Kind = "presence" } ) == BlobOffer.Process, "blob_packets_that_carry_no_scene_state_are_never_held" );
        }

        // Bounds: the 33rd held packet is refused without throwing; the 32 before it stay.
        {
            Link link = new Link();
            string unknown = new string( 'a', 64 );
            BlobOffer last = BlobOffer.Process;
            Exception thrown = null;
            try
            {
                for ( int i = 0; i < BlobLimits.MaxPendingPackets + 1; i++ ) last = link.B.Offer( SnapshotNaming( unknown ) );
            }
            catch ( Exception ex ) { thrown = ex; }
            List<(Packet Packet, string Reason)> dropped = link.B.TakeDropped();
            assert( thrown == null && last == BlobOffer.Dropped && link.B.HeldCount == BlobLimits.MaxPendingPackets && dropped.Count == 1, "blob_the_pending_packet_bound_refuses_without_throwing" );
            assert( dropped[0].Reason.Contains( "32" ) && link.B.TakeNotices().Any(), "blob_the_refusal_is_reported_with_a_notice" );
        }

        // Bounds: the pending-bytes budget is 64 MiB, inclusive. Two 32 MiB geometries fit exactly; a third, however small, does not and is
        // refused without throwing. The packet that named all three is dropped and every reservation is returned.
        {
            Link link = new Link();
            string h1 = new string( '1', 64 ), h2 = new string( '2', 64 ), h3 = new string( '3', 64 );
            link.B.Offer( SnapshotNaming( h1, h2, h3 ) );
            link.B.Handle( new Packet { Kind = "blob-data", Sha256 = h1, Offset = 0, Total = BlobLimits.MaxBlobBytes, Data = new byte[FileSyncLimits.ChunkSize] } );
            link.B.Handle( new Packet { Kind = "blob-data", Sha256 = h2, Offset = 0, Total = BlobLimits.MaxBlobBytes, Data = new byte[FileSyncLimits.ChunkSize] } );
            long atLimit = link.B.PendingBytes;
            Exception thrown = null;
            try { link.B.Handle( new Packet { Kind = "blob-data", Sha256 = h3, Offset = 0, Total = 300 * 1024, Data = new byte[FileSyncLimits.ChunkSize] } ); }
            catch ( Exception ex ) { thrown = ex; }
            assert( thrown == null && atLimit == 64L * 1024 * 1024, "blob_pending_bytes_up_to_64_mib_are_accepted_inclusive" );
            assert( link.B.HeldCount == 0 && link.B.PendingBytes == 0 && link.B.TakeDropped().Count == 1, "blob_pending_bytes_over_64_mib_are_refused_without_throwing_and_released" );
        }

        // Bounds: a declared size over 32 MiB is refused at once.
        {
            Link link = new Link();
            string h = new string( '3', 64 );
            link.B.Offer( SnapshotNaming( h ) );
            link.B.Handle( new Packet { Kind = "blob-data", Sha256 = h, Offset = 0, Total = BlobLimits.MaxBlobBytes + 1, Data = new byte[FileSyncLimits.ChunkSize] } );
            assert( link.B.PendingBytes == 0 && link.B.TakeDropped().Count == 1, "blob_a_declared_size_over_32_mib_is_refused" );
        }

        // Timeout with injected time: still held at 59 s, dropped and reported at 60 s.
        {
            Link link = new Link();
            link.B.Offer( SnapshotNaming( new string( '4', 64 ) ) );
            link.Now = T0.AddSeconds( 59 );
            link.B.Pump();
            assert( link.B.HeldCount == 1, "blob_a_held_packet_waits_until_the_timeout" );
            link.Now = T0.AddSeconds( 60 );
            link.B.Pump();
            List<(Packet Packet, string Reason)> dropped = link.B.TakeDropped();
            assert( link.B.HeldCount == 0 && dropped.Count == 1 && dropped[0].Reason.Contains( "60" ), "blob_a_held_packet_is_dropped_at_the_timeout" );
        }

        // Hostile or broken chunks.
        {
            Link link = new Link();
            byte[] geometry = Bytes( 300 * 1024, 45 );
            string hash = BlobStore.HashOf( geometry );
            link.B.Offer( SnapshotNaming( hash ) );
            byte[] first = geometry.Take( FileSyncLimits.ChunkSize ).ToArray();
            byte[] rest = geometry.Skip( FileSyncLimits.ChunkSize ).ToArray();
            link.B.Handle( new Packet { Kind = "blob-data", Sha256 = hash, Offset = 0, Total = geometry.Length, Data = first } );
            link.B.Handle( new Packet { Kind = "blob-data", Sha256 = hash, Offset = 0, Total = geometry.Length, Data = first } );
            assert( link.B.PendingBytes == geometry.Length && !link.StoreB.Has( hash ), "blob_a_duplicate_chunk_is_ignored" );
            // Past Total: a chunk that would run beyond the declared size is refused and the assembly is abandoned.
            link.B.Handle( new Packet { Kind = "blob-data", Sha256 = hash, Offset = FileSyncLimits.ChunkSize, Total = geometry.Length, Data = new byte[rest.Length + 5] } );
            assert( !link.StoreB.Has( hash ) && link.B.PendingBytes == 0 && link.B.TakeDropped().Count == 1, "blob_a_chunk_past_total_is_refused_and_reported" );
        }
        {
            Link link = new Link();
            byte[] geometry = Bytes( 300 * 1024, 46 );
            string hash = BlobStore.HashOf( geometry );
            link.B.Offer( SnapshotNaming( hash ) );
            byte[] poisoned = (byte[])geometry.Clone();
            poisoned[1000] ^= 0xFF;
            link.B.Handle( new Packet { Kind = "blob-data", Sha256 = hash, Offset = 0, Total = poisoned.Length, Data = poisoned.Take( FileSyncLimits.ChunkSize ).ToArray() } );
            link.B.Handle( new Packet { Kind = "blob-data", Sha256 = hash, Offset = FileSyncLimits.ChunkSize, Total = poisoned.Length, Data = poisoned.Skip( FileSyncLimits.ChunkSize ).ToArray() } );
            assert( !link.StoreB.Has( hash ) && link.B.PendingBytes == 0, "blob_bytes_that_do_not_hash_to_the_name_are_discarded" );
            List<(Packet Packet, string Reason)> dropped = link.B.TakeDropped();
            assert( dropped.Count == 1 && link.B.HeldCount == 0 && !link.B.TryTakeReady( out _ ), "blob_a_packet_whose_geometry_failed_verification_is_never_applied" );
        }
        {
            // Unsolicited data is not stored: nobody asked for it.
            Link link = new Link();
            byte[] geometry = Bytes( 5000, 47 );
            string hash = BlobStore.HashOf( geometry );
            link.B.Handle( new Packet { Kind = "blob-data", Sha256 = hash, Offset = 0, Total = geometry.Length, Data = geometry } );
            assert( !link.StoreB.Has( hash ) && link.B.PendingBytes == 0, "blob_unsolicited_data_is_not_stored" );
            // A hostile hash in a request is ignored; the sender never touches a path.
            link.A.Handle( new Packet { Kind = "blob-need", Hashes = new List<string> { "../../secret", new string( 'A', 64 ) } } );
            link.A.Pump();
            assert( link.AtoB.Count == 0, "blob_a_request_with_hostile_hashes_is_ignored" );
            // The sender answers an unknown hash with an empty reply so the requester does not wait for the timeout.
            link.A.Handle( new Packet { Kind = "blob-need", Hashes = new List<string> { hash } } );
            link.A.Pump();
            assert( link.AtoB.Count == 1 && link.AtoB[0].Kind == "blob-data" && link.AtoB[0].Total == 0, "blob_an_unknown_hash_is_answered_as_unavailable" );
        }
        {
            Link link = new Link();
            string hash = new string( '5', 64 );
            link.B.Offer( SnapshotNaming( hash ) );
            link.B.Handle( new Packet { Kind = "blob-data", Sha256 = hash, Offset = 0, Total = 0 } );
            assert( link.B.HeldCount == 0 && link.B.TakeDropped().Count == 1, "blob_an_unavailable_reply_drops_the_waiting_packet" );
        }
        {
            // A request names at most 64 hashes; the rest of an oversized request is ignored.
            Link link = new Link();
            List<string> many = Enumerable.Range( 0, 100 ).Select( i => link.StoreA.Put( Bytes( 4100, 100 + i ), out _ ) ).ToList();
            link.A.Handle( new Packet { Kind = "blob-need", Hashes = many } );
            int jobs = 0;
            for ( int i = 0; i < 40; i++ ) { link.A.Pump(); jobs += link.AtoB.Count; link.AtoB.Clear(); link.A.Handle( new Packet { Kind = "blob-ack" } ); }
            assert( link.A.QueuedBlobs <= 64 || jobs <= 64 + 8, "blob_a_request_is_bounded_to_64_hashes" );
        }
    }

    // ---- Refusals in StateRules --------------------------------------------------------------------------------------------------

    static void RefusalTests( Action<bool, string> assert )
    {
        // Geometry that is still inline (too large to externalize) is refused by name with a sentence about geometry, not a bare
        // "text value" message.
        ObjectState huge = MeshObject( Id( 1 ), Base64Of( new byte[BlobLimits.MaxBlobBytes + 3] ) );
        string refusal = StateRules.RefusalOf( new Change( Id( 1 ), null, huge ) );
        assert( refusal != null && refusal.Contains( "\"Floor\"" ) && refusal.Contains( "geometry" ) && refusal.Contains( "32 MiB" ), "blob_oversize_geometry_is_refused_with_a_formal_notice_naming_the_object" );

        // A placeholder object passes the structural rules at any mesh size: that is the point.
        ObjectState small = MeshObject( Id( 2 ), BlobRules.Placeholder( SampleMeshSha ) );
        assert( StateRules.RefusalOf( new Change( Id( 2 ), null, small ) ) == null, "blob_a_placeholder_object_passes_the_structural_rules" );
    }
}
