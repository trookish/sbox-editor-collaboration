using TeamCreate;

/// <summary>The spectator's free-fly camera maths: pure, so speed limits, the pitch clamp and finite-ness are tested without an editor.</summary>
static partial class SharedPlayTests
{
    static FlyInput NoInput() => new FlyInput();

    static void FreeFly( Action<bool, string> assert )
    {
        // One step is capped at MaxStepSeconds, so the movement tests use exactly that and expect speed * T.
        const float T = FreeFlyCamera.MaxStepSeconds;
        var start = FlyState.At( 0, 0, 0 );
        assert( start.Yaw == 0 && start.Pitch == 0 && start.Speed == FreeFlyCamera.DefaultSpeed, "play_fly_start_state" );

        // Axes follow the engine: X forward, Y left, Z up. Looking down +X, "forward" moves +X and "right" moves toward -Y.
        var forward = FreeFlyCamera.Step( start, NoInput() with { Forward = 1 }, T );
        assert( Near( forward.X, FreeFlyCamera.DefaultSpeed * T ) && Near( forward.Y, 0 ) && Near( forward.Z, 0 ), "play_fly_forward_moves_along_x" );
        var right = FreeFlyCamera.Step( start, NoInput() with { Right = 1 }, T );
        assert( Near( right.Y, -FreeFlyCamera.DefaultSpeed * T ) && Near( right.X, 0 ), "play_fly_right_moves_toward_minus_y" );
        var up = FreeFlyCamera.Step( start, NoInput() with { Up = 1 }, T );
        assert( Near( up.Z, FreeFlyCamera.DefaultSpeed * T ) && Near( up.X, 0 ), "play_fly_up_is_world_up" );
        var turned = FreeFlyCamera.Step( start with { Yaw = 90 }, NoInput() with { Forward = 1 }, T );
        assert( Near( turned.Y, FreeFlyCamera.DefaultSpeed * T ) && Near( turned.X, 0 ), "play_fly_forward_follows_yaw" );
        var climbing = FreeFlyCamera.Step( start with { Pitch = 45 }, NoInput() with { Forward = 1 }, T );
        assert( climbing.Z > 0 && Near( climbing.Z, climbing.X ), "play_fly_forward_follows_pitch" );

        // Diagonals are not faster than straight lines.
        var diagonal = FreeFlyCamera.Step( start, NoInput() with { Forward = 1, Right = 1 }, T );
        assert( Near( Dist( diagonal ), FreeFlyCamera.DefaultSpeed * T ), "play_fly_diagonal_is_normalised" );
        var overdriven = FreeFlyCamera.Step( start, NoInput() with { Forward = 50 }, T );
        assert( Near( overdriven.X, FreeFlyCamera.DefaultSpeed * T ), "play_fly_axis_input_is_clamped" );

        // Shift and Ctrl: fast, slow, and both together cancel.
        assert( Near( FreeFlyCamera.Step( start, NoInput() with { Forward = 1, Fast = true }, T ).X, FreeFlyCamera.DefaultSpeed * FreeFlyCamera.FastFactor * T ), "play_fly_shift_is_fast" );
        assert( Near( FreeFlyCamera.Step( start, NoInput() with { Forward = 1, Slow = true }, T ).X, FreeFlyCamera.DefaultSpeed * FreeFlyCamera.SlowFactor * T ), "play_fly_ctrl_is_slow" );
        assert( Near( FreeFlyCamera.Step( start, NoInput() with { Forward = 1, Fast = true, Slow = true }, T ).X, FreeFlyCamera.DefaultSpeed * T ), "play_fly_shift_and_ctrl_cancel" );

        // The wheel scales the base speed, within hard limits.
        var faster = FreeFlyCamera.Step( start, NoInput() with { Wheel = 3 }, 0f );
        assert( faster.Speed > start.Speed && Near( faster.Speed, start.Speed * MathF.Pow( FreeFlyCamera.WheelFactor, 3 ) ), "play_fly_wheel_up_raises_speed" );
        var slower = FreeFlyCamera.Step( start, NoInput() with { Wheel = -3 }, 0f );
        assert( slower.Speed < start.Speed, "play_fly_wheel_down_lowers_speed" );
        assert( FreeFlyCamera.Step( start, NoInput() with { Wheel = 1000 }, 0f ).Speed == FreeFlyCamera.MaxSpeed && FreeFlyCamera.Step( start, NoInput() with { Wheel = -1000 }, 0f ).Speed == FreeFlyCamera.MinSpeed, "play_fly_speed_limits" );
        assert( FreeFlyCamera.Step( start, NoInput() with { Forward = 1, Fast = true }, T ).X <= FreeFlyCamera.MaxSpeed * FreeFlyCamera.FastFactor * T, "play_fly_speed_with_fast_is_bounded" );

        // Looking needs the right mouse button; without it the mouse does nothing. The pitch never leaves +-89 degrees.
        assert( FreeFlyCamera.Step( start, NoInput() with { LookX = 500, LookY = 500, Looking = false }, 0.016f ) == start, "play_fly_mouse_without_button_changes_nothing" );
        var looked = FreeFlyCamera.Step( start, NoInput() with { LookX = 100, LookY = 0, Looking = true }, 0.016f );
        assert( Near( looked.Yaw, -100 * FreeFlyCamera.Sensitivity ), "play_fly_mouse_right_turns_right" );
        var upLook = FreeFlyCamera.Step( start, NoInput() with { LookY = -100000, Looking = true }, 0.016f );
        var downLook = FreeFlyCamera.Step( start, NoInput() with { LookY = 100000, Looking = true }, 0.016f );
        assert( upLook.Pitch == 89 && downLook.Pitch == -89, "play_fly_pitch_is_clamped_to_89" );
        var wrapped = FreeFlyCamera.Step( start with { Yaw = 179 }, NoInput() with { LookX = -100, Looking = true }, 0.016f );
        assert( wrapped.Yaw > -180 && wrapped.Yaw <= 180 && Near( wrapped.Yaw, -171 ), "play_fly_yaw_wraps" );

        // Nothing non-finite ever comes out: not from NaN or infinite input, not from a bad delta time, not from a corrupt state.
        float nan = float.NaN, inf = float.PositiveInfinity;
        var poisoned = FreeFlyCamera.Step( start, new FlyInput( nan, inf, -inf, nan, inf, true, true, nan, true ), nan );
        assert( Finite( poisoned ), "play_fly_nan_input_stays_finite" );
        var corrupt = FreeFlyCamera.Step( new FlyState( nan, inf, -inf, nan, inf, nan ), NoInput() with { Forward = 1 }, 0.016f );
        assert( Finite( corrupt ) && corrupt.Speed >= FreeFlyCamera.MinSpeed && corrupt.Speed <= FreeFlyCamera.MaxSpeed && Math.Abs( corrupt.Pitch ) <= 89, "play_fly_corrupt_state_is_repaired" );
        assert( FreeFlyCamera.Step( start, NoInput() with { Forward = 1 }, 0f ) == start && FreeFlyCamera.Step( start, NoInput() with { Forward = 1 }, -1f ) == start, "play_fly_zero_or_negative_dt_does_not_move" );
        assert( Near( FreeFlyCamera.Step( start, NoInput() with { Forward = 1 }, 5000f ).X, FreeFlyCamera.DefaultSpeed * FreeFlyCamera.MaxStepSeconds ), "play_fly_dt_is_capped_after_a_stall" );
        assert( FreeFlyCamera.Step( start, NoInput(), 0.016f ) == start, "play_fly_idle_does_not_drift" );

        // Held at the clamp and flown for a long time: the position stays inside the world bound.
        var far = start;
        for ( int i = 0; i < 2000; i++ ) far = FreeFlyCamera.Step( far with { Speed = FreeFlyCamera.MaxSpeed }, NoInput() with { Forward = 1, Fast = true }, 0.1f );
        assert( Finite( far ) && Math.Abs( far.X ) <= FreeFlyCamera.WorldLimit, "play_fly_position_is_bounded" );

        // The view direction is a unit vector everywhere, including straight up and down.
        foreach ( float pitch in new[] { -89f, 0f, 45f, 89f } )
        {
            var (x, y, z) = FreeFlyCamera.Forward( start with { Pitch = pitch, Yaw = 33 } );
            assert( Near( MathF.Sqrt( x * x + y * y + z * z ), 1f ), "play_fly_forward_is_a_unit_vector_" + pitch );
        }
    }

    static bool Near( float a, float b ) => MathF.Abs( a - b ) < 1e-3f * MathF.Max( 1f, MathF.Abs( b ) );

    static float Dist( FlyState s ) => MathF.Sqrt( s.X * s.X + s.Y * s.Y + s.Z * s.Z );

    static bool Finite( FlyState s ) => float.IsFinite( s.X ) && float.IsFinite( s.Y ) && float.IsFinite( s.Z ) && float.IsFinite( s.Yaw ) && float.IsFinite( s.Pitch ) && float.IsFinite( s.Speed );
}
