using System;

namespace TeamCreate;

/// <summary>One frame of spectator input, already read from the keyboard and mouse. Axes are -1..1 (the camera clamps them); look is mouse delta in pixels.</summary>
public readonly record struct FlyInput( float Forward = 0, float Right = 0, float Up = 0, float LookX = 0, float LookY = 0, bool Fast = false, bool Slow = false, float Wheel = 0, bool Looking = false );

/// <summary>
/// A free-fly camera pose. Yaw is degrees counter-clockwise seen from above (0 looks along +X, 90 along +Y), Pitch is degrees with positive LOOKING UP
/// (the editor's own camera uses the opposite sign for pitch; convert at the single place the pose is applied to a camera).
/// </summary>
public readonly record struct FlyState( float X, float Y, float Z, float Yaw, float Pitch, float Speed )
{
    public static FlyState At( float x, float y, float z ) => new FlyState( x, y, z, 0, 0, FreeFlyCamera.DefaultSpeed );
}

/// <summary>
/// The maths of the spectator's camera, with no engine type anywhere so it is tested in the plain test runner. Movement is relative to where the
/// camera looks (forward follows pitch, so looking up and pressing W climbs), strafing stays level, and Up is world up. Every output is finite and
/// bounded whatever goes in, because a spectator may be fed a corrupt pose by a restored layout and input comes from hardware.
/// </summary>
public static class FreeFlyCamera
{
    /// <summary>Source units per second at the default speed (the wheel scales it).</summary>
    public const float DefaultSpeed = 400f;

    public const float MinSpeed = 10f;

    public const float MaxSpeed = 8000f;

    public const float FastFactor = 4f;

    public const float SlowFactor = 0.25f;

    /// <summary>Each wheel notch multiplies the base speed by this.</summary>
    public const float WheelFactor = 1.25f;

    /// <summary>Degrees per mouse pixel.</summary>
    public const float Sensitivity = 0.1f;

    public const float MaxPitch = 89f;

    /// <summary>A stalled frame (a breakpoint, a hitch) must not throw the camera across the map.</summary>
    public const float MaxStepSeconds = 0.25f;

    /// <summary>The position is kept inside this cube, far beyond any map and well inside float precision.</summary>
    public const float WorldLimit = 1_000_000f;

    public static FlyState Step( FlyState state, FlyInput input, float dt )
    {
        state = Repair( state );
        float yaw = state.Yaw;
        float pitch = state.Pitch;
        if ( input.Looking )
        {
            yaw = Wrap( yaw - Finite( input.LookX ) * Sensitivity );
            pitch = Math.Clamp( pitch - Finite( input.LookY ) * Sensitivity, -MaxPitch, MaxPitch );
        }
        float speed = Math.Clamp( state.Speed * MathF.Pow( WheelFactor, Math.Clamp( Finite( input.Wheel ), -100f, 100f ) ), MinSpeed, MaxSpeed );

        float seconds = float.IsFinite( dt ) ? Math.Clamp( dt, 0f, MaxStepSeconds ) : 0f;
        float x = state.X, y = state.Y, z = state.Z;
        if ( seconds > 0f )
        {
            float forward = Math.Clamp( Finite( input.Forward ), -1f, 1f );
            float right = Math.Clamp( Finite( input.Right ), -1f, 1f );
            float up = Math.Clamp( Finite( input.Up ), -1f, 1f );
            float length = MathF.Sqrt( forward * forward + right * right + up * up );
            if ( length > 1f )
            {
                forward /= length;
                right /= length;
                up /= length;
            }
            float factor = input.Fast == input.Slow ? 1f : input.Fast ? FastFactor : SlowFactor;
            float distance = speed * factor * seconds;
            var (fx, fy, fz) = Forward( yaw, pitch );
            float yawRad = yaw * MathF.PI / 180f;
            float rx = MathF.Sin( yawRad ), ry = -MathF.Cos( yawRad );
            x += ( fx * forward + rx * right ) * distance;
            y += ( fy * forward + ry * right ) * distance;
            z += ( fz * forward + up ) * distance;
        }
        return new FlyState( Math.Clamp( x, -WorldLimit, WorldLimit ), Math.Clamp( y, -WorldLimit, WorldLimit ), Math.Clamp( z, -WorldLimit, WorldLimit ), yaw, pitch, speed );
    }

    /// <summary>The unit view direction of a pose.</summary>
    public static (float X, float Y, float Z) Forward( FlyState state ) => Forward( Repair( state ).Yaw, Repair( state ).Pitch );

    private static (float X, float Y, float Z) Forward( float yaw, float pitch )
    {
        float yawRad = yaw * MathF.PI / 180f;
        float pitchRad = pitch * MathF.PI / 180f;
        float flat = MathF.Cos( pitchRad );
        return ( flat * MathF.Cos( yawRad ), flat * MathF.Sin( yawRad ), MathF.Sin( pitchRad ) );
    }

    /// <summary>A pose with every field finite and in range, for a state that came from outside.</summary>
    public static FlyState Repair( FlyState state ) => new FlyState(
        Math.Clamp( Finite( state.X ), -WorldLimit, WorldLimit ),
        Math.Clamp( Finite( state.Y ), -WorldLimit, WorldLimit ),
        Math.Clamp( Finite( state.Z ), -WorldLimit, WorldLimit ),
        Wrap( Finite( state.Yaw ) ),
        Math.Clamp( Finite( state.Pitch ), -MaxPitch, MaxPitch ),
        float.IsFinite( state.Speed ) ? Math.Clamp( state.Speed, MinSpeed, MaxSpeed ) : DefaultSpeed );

    private static float Finite( float value ) => float.IsFinite( value ) ? value : 0f;

    /// <summary>Degrees into (-180, 180].</summary>
    private static float Wrap( float degrees )
    {
        degrees %= 360f;
        if ( degrees > 180f )
        {
            degrees -= 360f;
        }
        else if ( degrees <= -180f )
        {
            degrees += 360f;
        }
        return degrees;
    }
}
