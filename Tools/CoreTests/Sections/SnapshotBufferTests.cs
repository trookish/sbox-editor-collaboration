using TeamCreate;

/// <summary>
/// SnapshotBuffer: where was this object at time t, from a short history of timestamped poses. Expected values are written out by hand
/// (whole numbers and halves, or an independent atan2/sin/cos construction for rotations) rather than by calling the production blend.
/// </summary>
static class SnapshotBufferTests
{
    private static Pose At( float x, float y, float z ) => new Pose( x, y, z, 0f, 0f, 0f, 1f );

    private static bool Near( float a, float b, float eps = 1e-4f ) => MathF.Abs( a - b ) <= eps;

    private static bool PosNear( Pose p, float x, float y, float z, float eps = 1e-4f ) => Near( p.X, x, eps ) && Near( p.Y, y, eps ) && Near( p.Z, z, eps );

    private static bool ScaleNear( Pose p, float x, float y, float z, float eps = 1e-4f ) => Near( p.Sx, x, eps ) && Near( p.Sy, y, eps ) && Near( p.Sz, z, eps );

    private static bool RotNear( Pose p, float x, float y, float z, float w, float eps = 1e-4f ) => Near( p.Qx, x, eps ) && Near( p.Qy, y, eps ) && Near( p.Qz, z, eps ) && Near( p.Qw, w, eps );

    private static float QLength( Pose p ) => MathF.Sqrt( p.Qx * p.Qx + p.Qy * p.Qy + p.Qz * p.Qz + p.Qw * p.Qw );

    private static bool IsFinitePose( Pose p ) => float.IsFinite( p.X ) && float.IsFinite( p.Y ) && float.IsFinite( p.Z )
        && float.IsFinite( p.Qx ) && float.IsFinite( p.Qy ) && float.IsFinite( p.Qz ) && float.IsFinite( p.Qw )
        && float.IsFinite( p.Sx ) && float.IsFinite( p.Sy ) && float.IsFinite( p.Sz );

    /// <summary>A rotation of <paramref name="degrees"/> about the vertical axis as a unit quaternion.</summary>
    private static Pose Yaw( float x, float degrees )
    {
        double half = degrees * Math.PI / 360.0;
        return new Pose( x, 0f, 0f, 0f, 0f, (float)Math.Sin( half ), (float)Math.Cos( half ) );
    }

    private static Pose FromComponents( float[] v ) => new Pose( v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9] );

    public static void Run( Action<bool, string> assert )
    {
        // ---- empty and single-sample behaviour ----
        {
            var buffer = new SnapshotBuffer();
            bool got = buffer.TrySample( 100, out var pose );
            assert( !got && pose == default && buffer.Count == 0, "snap_empty_buffer_samples_false" );
        }
        {
            var buffer = new SnapshotBuffer();
            var only = new Pose( 4f, 5f, 6f, 0f, 0f, 0f, 1f, 2f, 2f, 2f );
            buffer.Add( 100, only );
            bool before = buffer.TrySample( 0, out var p0 );
            bool on = buffer.TrySample( 100, out var p1 );
            bool after = buffer.TrySample( 500, out var p2 );
            assert( before && on && after && p0 == only && p1 == only && p2 == only, "snap_single_sample_holds" );
        }

        // ---- interpolation ----
        {
            var a = new Pose( 0f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f );
            var b = new Pose( 10f, 20f, 30f, 0f, 0f, 0f, 1f, 3f, 5f, 7f );
            var buffer = new SnapshotBuffer();
            buffer.Add( 100, a );
            buffer.Add( 200, b );
            bool got = buffer.TrySample( 150, out var mid );
            assert( got && PosNear( mid, 5f, 10f, 15f ), "snap_midpoint_position" );
            assert( ScaleNear( mid, 2f, 3f, 4f ), "snap_midpoint_scale" );
        }
        {
            // 0 to 90 degrees of yaw: the half way rotation is 45 degrees, i.e. half-angle 22.5 degrees. At the midpoint nlerp and slerp coincide.
            var buffer = new SnapshotBuffer();
            buffer.Add( 100, Yaw( 0f, 0f ) );
            buffer.Add( 200, Yaw( 0f, 90f ) );
            buffer.TrySample( 150, out var mid );
            float s = (float)Math.Sin( Math.PI / 8 ), c = (float)Math.Cos( Math.PI / 8 );
            assert( RotNear( mid, 0f, 0f, s, c ), "snap_yaw_90_midpoint_is_yaw_45" );
        }
        {
            var a = new Pose( 0f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f );
            var b = new Pose( 40f, 80f, -40f, 0f, 0f, 0f, 1f, 3f, 5f, 9f );
            var buffer = new SnapshotBuffer();
            buffer.Add( 10, a );
            buffer.Add( 50, b );
            buffer.TrySample( 20, out var q );
            assert( PosNear( q, 10f, 20f, -10f ) && ScaleNear( q, 1.5f, 2f, 3f ), "snap_quarter_point_position_and_scale" );
        }
        {
            // Normalised lerp is not constant angular speed: a quarter of the way along a 90 degree turn is about 21.6 degrees, not 22.5 degrees.
            // The expected value is built from atan2 so it does not repeat the production formula.
            var buffer = new SnapshotBuffer();
            float s90 = (float)Math.Sin( Math.PI / 4 );
            buffer.Add( 0, Yaw( 0f, 0f ) );
            buffer.Add( 100, Yaw( 0f, 90f ) );
            buffer.TrySample( 25, out var q );
            double half = Math.Atan2( 0.25 * s90, 0.75 + 0.25 * s90 );
            assert( RotNear( q, 0f, 0f, (float)Math.Sin( half ), (float)Math.Cos( half ) ), "snap_quarter_point_rotation_is_nlerp" );
        }
        {
            // The newest sample arrives first (t = 30), then an older one (t = 10): the buffer must still order them by timestamp.
            var buffer = new SnapshotBuffer();
            buffer.Add( 30, At( 30f, 0f, 0f ) );
            buffer.Add( 10, At( 10f, 0f, 0f ) );
            bool got = buffer.TrySample( 20, out var mid );
            assert( buffer.Count == 2 && got && PosNear( mid, 20f, 0f, 0f ), "snap_out_of_order_add_interpolates_correctly" );
        }
        {
            var buffer = new SnapshotBuffer();
            buffer.Add( 30, At( 30f, 0f, 0f ) );
            buffer.Add( 10, At( 10f, 0f, 0f ) );
            buffer.Add( 20, At( 25f, 0f, 0f ) ); // arrives last, belongs in the middle
            buffer.TrySample( 15, out var p15 );
            buffer.TrySample( 25, out var p25 );
            assert( buffer.Count == 3 && PosNear( p15, 17.5f, 0f, 0f ) && PosNear( p25, 27.5f, 0f, 0f ), "snap_out_of_order_add_lands_between_neighbours" );
        }

        // ---- duplicate timestamps ----
        {
            var buffer = new SnapshotBuffer();
            buffer.Add( 10, At( 1f, 0f, 0f ) );
            buffer.Add( 10, At( 2f, 0f, 0f ) );
            buffer.TrySample( 10, out var p );
            assert( buffer.Count == 1 && PosNear( p, 2f, 0f, 0f ), "snap_duplicate_timestamp_replaces" );
        }
        {
            var buffer = new SnapshotBuffer();
            buffer.Add( 10, At( 10f, 0f, 0f ) );
            buffer.Add( 20, At( 20f, 0f, 0f ) );
            buffer.Add( 30, At( 30f, 0f, 0f ) );
            buffer.Add( 20, At( 50f, 0f, 0f ) ); // a retransmission of the middle sample with a corrected value
            buffer.TrySample( 20, out var onIt );
            buffer.TrySample( 25, out var after );
            assert( buffer.Count == 3 && PosNear( onIt, 50f, 0f, 0f ) && PosNear( after, 40f, 0f, 0f ), "snap_duplicate_timestamp_in_the_middle_replaces" );
        }

        // ---- before the first sample, exactly on a sample ----
        {
            var buffer = new SnapshotBuffer();
            var first = new Pose( 1f, 2f, 3f, 0f, 0f, 0f, 1f, 2f, 2f, 2f );
            buffer.Add( 100, first );
            buffer.Add( 200, At( 50f, 50f, 50f ) );
            bool a = buffer.TrySample( 50, out var p50 );
            bool b = buffer.TrySample( -1_000_000, out var pFar );
            assert( a && b && p50 == first && pFar == first, "snap_before_first_sample_returns_first" );
        }
        {
            var p10 = new Pose( 1f, 2f, 3f, 0.5f, 0.5f, 0.5f, 0.5f, 1f, 2f, 3f );
            var p20 = new Pose( 4f, 5f, 6f, 0.5f, -0.5f, 0.5f, 0.5f, 4f, 5f, 6f );
            var p30 = new Pose( 7f, 8f, 9f, -0.5f, 0.5f, 0.5f, 0.5f, 7f, 8f, 9f );
            var buffer = new SnapshotBuffer();
            buffer.Add( 10, p10 );
            buffer.Add( 20, p20 );
            buffer.Add( 30, p30 );
            buffer.TrySample( 10, out var s10 );
            buffer.TrySample( 20, out var s20 );
            buffer.TrySample( 30, out var s30 );
            assert( s10 == p10 && s20 == p20 && s30 == p30, "snap_render_time_exactly_on_a_sample_returns_it" );
        }
        {
            // Exactly on a sample means that sample, untouched: the blend's renormalisation must not alter a stored rotation (this one is not unit length).
            var raw = new Pose( 4f, 5f, 6f, 0f, 0f, 3f, 4f, 2f, 2f, 2f );
            var buffer = new SnapshotBuffer();
            buffer.Add( 10, At( 0f, 0f, 0f ) );
            buffer.Add( 20, raw );
            buffer.Add( 30, At( 9f, 9f, 9f ) );
            buffer.TrySample( 20, out var onIt );
            assert( onIt == raw, "snap_exact_sample_is_returned_unchanged_even_if_its_rotation_is_not_unit_length" );
        }

        // ---- extrapolation: position only, at most 120 ms, then hold ----
        {
            // Velocity from the last two samples is (0.1, -0.2, 0.05) units per millisecond.
            var buffer = new SnapshotBuffer();
            buffer.Add( 100, At( 0f, 0f, 0f ) );
            buffer.Add( 200, At( 10f, -20f, 5f ) );
            buffer.TrySample( 260, out var at60 );
            buffer.TrySample( 320, out var at120 );
            buffer.TrySample( 321, out var at121 );
            buffer.TrySample( 600, out var at400 );
            assert( PosNear( at60, 16f, -32f, 8f ), "snap_extrapolates_linearly_60ms_past_last" );
            assert( PosNear( at120, 22f, -44f, 11f ), "snap_extrapolation_reaches_cap_at_120ms" );
            assert( PosNear( at121, 22f, -44f, 11f ), "snap_extrapolation_stops_just_past_120ms" );
            assert( PosNear( at400, 22f, -44f, 11f ), "snap_extrapolation_holds_last_position_at_400ms" );
        }
        {
            var lastRotation = Yaw( 0f, 90f );
            var buffer = new SnapshotBuffer();
            buffer.Add( 100, new Pose( 0f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f ) );
            buffer.Add( 200, lastRotation with { X = 10f, Sx = 3f, Sy = 4f, Sz = 5f } );
            buffer.TrySample( 260, out var p );
            assert( PosNear( p, 16f, 0f, 0f ) && RotNear( p, lastRotation.Qx, lastRotation.Qy, lastRotation.Qz, lastRotation.Qw, 0f ) && ScaleNear( p, 3f, 4f, 5f, 0f ),
                "snap_extrapolation_never_extrapolates_rotation_or_scale" );
        }

        // ---- shortest-path rotation ----
        {
            // Identity and its negation are the same rotation; the interpolation must stay there, not swing through a zero-length quaternion.
            var buffer = new SnapshotBuffer();
            buffer.Add( 0, new Pose( 0f, 0f, 0f, 0f, 0f, 0f, 1f ) );
            buffer.Add( 100, new Pose( 0f, 0f, 0f, 0f, 0f, 0f, -1f ) );
            bool allUnit = true, allIdentity = true;
            foreach ( int t in new[] { 10, 25, 50, 75, 90 } )
            {
                buffer.TrySample( t, out var p );
                allUnit &= Near( p.Qx * p.Qx + p.Qy * p.Qy + p.Qz * p.Qz + p.Qw * p.Qw, 1f, 1e-5f );
                allIdentity &= Near( p.Qw, 1f ) && Near( p.Qx, 0f ) && Near( p.Qy, 0f ) && Near( p.Qz, 0f );
            }
            assert( allUnit, "snap_identity_to_negated_identity_stays_unit_length" );
            assert( allIdentity, "snap_identity_to_negated_identity_takes_the_short_way" );
        }
        {
            // The second pose is the 90 degree yaw written with the opposite sign. It is still 90 degrees away from the first, so the middle is 45 degrees.
            var yaw90 = Yaw( 0f, 90f );
            var negated = new Pose( 0f, 0f, 0f, -yaw90.Qx, -yaw90.Qy, -yaw90.Qz, -yaw90.Qw );
            var buffer = new SnapshotBuffer();
            buffer.Add( 0, Yaw( 0f, 0f ) );
            buffer.Add( 100, negated );
            buffer.TrySample( 50, out var mid );
            float s = (float)Math.Sin( Math.PI / 8 ), c = (float)Math.Cos( Math.PI / 8 );
            assert( RotNear( mid, 0f, 0f, s, c ), "snap_negated_yaw_90_midpoint_is_yaw_45" );
        }
        {
            // For any two rotations the blend lies on the short great arc between them, so the two partial angles add up to the whole angle.
            var rng = new Random( 7 );
            Func<Pose> randomUnit = () =>
            {
                double x = rng.NextDouble() - 0.5, y = rng.NextDouble() - 0.5, z = rng.NextDouble() - 0.5, w = rng.NextDouble() - 0.5;
                double len = Math.Sqrt( x * x + y * y + z * z + w * w );
                return new Pose( 0f, 0f, 0f, (float)(x / len), (float)(y / len), (float)(z / len), (float)(w / len) );
            };
            Func<Pose, Pose, double> angle = ( p, q ) =>
            {
                double d = Math.Abs( (double)p.Qx * q.Qx + (double)p.Qy * q.Qy + (double)p.Qz * q.Qz + (double)p.Qw * q.Qw );
                return Math.Acos( Math.Min( 1.0, d ) );
            };
            bool onArc = true, unit = true;
            for ( int i = 0; i < 300; i++ )
            {
                var a = randomUnit();
                var b = randomUnit();
                double u = 0.1 + 0.8 * rng.NextDouble();
                var buffer = new SnapshotBuffer();
                buffer.Add( 0, a );
                buffer.Add( 1000, b );
                buffer.TrySample( u * 1000, out var r );
                unit &= Near( QLength( r ), 1f, 1e-5f );
                double whole = angle( a, b );
                onArc &= Math.Abs( angle( a, r ) + angle( r, b ) - whole ) < 3e-3;
            }
            assert( unit, "snap_random_rotation_blend_is_unit_length" );
            assert( onArc, "snap_random_rotation_blend_stays_on_the_short_arc" );
        }
        {
            // A zero quaternion is invalid input, but it must not poison the picture with NaN.
            var buffer = new SnapshotBuffer();
            var zero = new Pose( 0f, 0f, 0f, 0f, 0f, 0f, 0f );
            buffer.Add( 0, zero );
            buffer.Add( 100, zero with { X = 10f } );
            bool got = buffer.TrySample( 50, out var p );
            assert( got && IsFinitePose( p ) && Near( p.X, 5f ), "snap_zero_quaternion_does_not_produce_nan" );
        }

        // ---- capacity ----
        {
            var buffer = new SnapshotBuffer();
            for ( int i = 0; i < 40; i++ )
            {
                buffer.Add( i * 10, At( i * 10f, 0f, 0f ) );
            }
            // Samples 0..7 were the oldest eight and are gone: asking for t = 50 (between 5 and 6) can only return the oldest kept sample, t = 80.
            buffer.TrySample( 50, out var old );
            buffer.TrySample( 80, out var oldest );
            buffer.TrySample( 395, out var past );
            assert( buffer.Count == SnapshotBuffer.Capacity && SnapshotBuffer.Capacity == 32, "snap_capacity_keeps_32_of_40_adds" );
            assert( PosNear( old, 80f, 0f, 0f ) && PosNear( oldest, 80f, 0f, 0f ), "snap_capacity_drops_the_oldest_samples" );
            assert( PosNear( past, 390f + 5f, 0f, 0f ), "snap_capacity_keeps_the_newest_samples" );
        }
        {
            var buffer = new SnapshotBuffer();
            for ( int i = 0; i < 40; i++ )
            {
                buffer.Add( 1000 + i * 10, At( i * 10f, 0f, 0f ) );
            }
            buffer.Add( 5, At( -999f, 0f, 0f ) ); // older than everything kept while the buffer is full: it is the oldest, so it is the one dropped
            buffer.TrySample( 5, out var p );
            assert( buffer.Count == SnapshotBuffer.Capacity && PosNear( p, 80f, 0f, 0f ), "snap_full_buffer_discards_a_sample_older_than_all_kept" );
        }

        // ---- non-finite input ----
        {
            var good = new Pose( 1f, 2f, 3f, 0f, 0f, 0f, 1f, 1f, 1f, 1f );
            var buffer = new SnapshotBuffer();
            buffer.Add( 100, good );
            buffer.Add( double.NaN, good );
            buffer.Add( double.PositiveInfinity, good );
            buffer.Add( double.NegativeInfinity, good );
            assert( buffer.Count == 1, "snap_non_finite_timestamp_is_ignored" );

            bool allIgnored = true, replacedNothing = true;
            var bad = new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity };
            for ( int component = 0; component < 10; component++ )
            {
                foreach ( float value in bad )
                {
                    var v = new float[] { 1f, 2f, 3f, 0f, 0f, 0f, 1f, 1f, 1f, 1f };
                    v[component] = value;
                    buffer.Add( 200, FromComponents( v ) ); // a new timestamp
                    allIgnored &= buffer.Count == 1;
                    buffer.Add( 100, FromComponents( v ) ); // the existing timestamp: must not overwrite the good sample
                    buffer.TrySample( 100, out var p );
                    replacedNothing &= p == good && buffer.Count == 1;
                }
            }
            assert( allIgnored, "snap_non_finite_pose_component_is_ignored" );
            assert( replacedNothing, "snap_non_finite_pose_does_not_overwrite_an_existing_sample" );
        }
        {
            var buffer = new SnapshotBuffer();
            buffer.Add( 0, At( 0f, 0f, 0f ) );
            buffer.Add( 100, At( 10f, 0f, 0f ) );
            bool nan = buffer.TrySample( double.NaN, out var pNan );
            bool pos = buffer.TrySample( double.PositiveInfinity, out var pPos );
            bool neg = buffer.TrySample( double.NegativeInfinity, out var pNeg );
            assert( !nan && !pos && !neg && pNan == default && pPos == default && pNeg == default, "snap_non_finite_render_time_returns_false" );
        }
        {
            // Two samples a hair apart in time make the velocity overflow; the result must still be a finite pose (held at the newest position).
            var buffer = new SnapshotBuffer();
            buffer.Add( 0, At( 0f, 0f, 0f ) );
            buffer.Add( 1e-310, At( 1f, 1f, 1f ) );
            bool got = buffer.TrySample( 50, out var p );
            assert( got && IsFinitePose( p ) && PosNear( p, 1f, 1f, 1f ), "snap_extrapolation_from_samples_a_hair_apart_holds_instead_of_overflowing" );
        }

        // ---- clear ----
        {
            var buffer = new SnapshotBuffer();
            buffer.Add( 10, At( 1f, 0f, 0f ) );
            buffer.Add( 20, At( 2f, 0f, 0f ) );
            buffer.Clear();
            bool got = buffer.TrySample( 15, out var p );
            buffer.Add( 5, At( 9f, 0f, 0f ) );
            buffer.TrySample( 5, out var again );
            assert( !got && buffer.Count == 1 && PosNear( again, 9f, 0f, 0f ), "snap_clear_empties_and_buffer_is_reusable" );
        }

        // ---- long run ----
        {
            var buffer = new SnapshotBuffer();
            bool bounded = true, finite = true;
            for ( int i = 0; i < 10_000; i++ )
            {
                buffer.Add( i * 10.0, At( i * 10f, 0f, 0f ) );
                if ( i % 7 == 3 )
                {
                    buffer.Add( (i - 2) * 10.0 + 5, At( (i - 2) * 10f + 5, 0f, 0f ) ); // a late packet that belongs between two older samples
                }
                if ( i % 11 == 5 )
                {
                    buffer.Add( i * 10.0, At( i * 10f, 0f, 0f ) ); // a duplicate
                }
                bounded &= buffer.Count <= SnapshotBuffer.Capacity;
                finite &= buffer.TrySample( i * 10.0 - 80, out var p ) && IsFinitePose( p );
            }
            assert( bounded && finite && buffer.Count <= SnapshotBuffer.Capacity, "snap_ten_thousand_adds_and_samples_stay_bounded" );
        }
        {
            // The buffer runs every frame for several objects: once warmed up, neither Add nor TrySample may allocate.
            var buffer = new SnapshotBuffer();
            double sink = 0;
            for ( int i = 0; i < 2_000; i++ )
            {
                buffer.Add( i * 10.0, At( i, 0f, 0f ) );
                buffer.TrySample( i * 10.0 - 80, out var warm );
                sink += warm.X;
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for ( int i = 2_000; i < 12_000; i++ )
            {
                buffer.Add( i * 10.0, At( i, 0f, 0f ) );
                if ( i % 7 == 3 )
                {
                    buffer.Add( (i - 2) * 10.0 + 5, At( i, 0f, 0f ) );
                }
                buffer.TrySample( i * 10.0 - 80, out var p );
                sink += p.X;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            assert( allocated == 0 && sink > 0, "snap_steady_state_add_and_sample_allocate_nothing" );
        }
    }
}
