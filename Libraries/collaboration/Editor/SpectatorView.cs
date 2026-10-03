using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Editor;
using Sandbox;

namespace TeamCreate;

/// <summary>
/// The spectator's window onto somebody else's run: a SEPARATE in-memory scene (<c>Scene.CreateEditorScene()</c>, the same call the editor's own
/// preview widgets use) that is drawn by a <see cref="SceneRenderingWidget"/> and flown through with a free-fly camera. It is never the user's edit
/// scene, is never saved, and is destroyed when the run ends, the session is left or the code hotloads.
///
/// What it contains comes only from the <see cref="PlayMirror"/>, which holds plain data that already passed <see cref="SpectateWhitelist"/>. Nothing
/// here creates a component from a string the stream chose: the type is looked up only after the exact-match whitelist accepts it, and it must then
/// be defined in the engine's own assembly. A game component is therefore never created, so no game code runs on a spectator.
///
/// Not confirmed in a running editor (compiled only): that the editor scene draws these components, and that a component whose type is on the list
/// does not run anything of its own in <c>EditorTick</c>.
/// </summary>
public sealed class SpectatorView : SceneRenderingWidget
{
    /// <summary>Interpolation renders this far behind the newest frame (two frames at 20 Hz), so motion is smooth between samples.</summary>
    public const double InterpolationDelayMs = 100;

    /// <summary>Beyond this distance from the camera (100 m in engine units) an object snaps to the newest pose instead of being smoothed, to bound work.</summary>
    public const float SmoothingRadius = 3937f;

    private static readonly System.Reflection.Assembly EngineAssembly = typeof( ModelRenderer ).Assembly;

    private sealed class Puppet
    {
        public GameObject Object;

        public readonly SnapshotBuffer Poses = new SnapshotBuffer();

        public readonly Dictionary<string, (Component Component, int Revision)> Components = new Dictionary<string, (Component, int)>( StringComparer.Ordinal );

        public double[] Position = new double[3];
    }

    private readonly PlayMirror mirror;

    private readonly Dictionary<int, Puppet> puppets = new Dictionary<int, Puppet>();

    private readonly CameraComponent camera;

    private FlyState fly = FlyState.At( 0, -400, 200 );

    private long appliedVersion = -1;

    private bool looking;

    private bool framed;

    private Vector2 lastMouse;

    private Vector2 lookDelta;

    private float wheel;

    private double nowMs;

    public SpectatorView( PlayMirror mirror, Widget parent = null ) : base( parent )
    {
        this.mirror = mirror ?? throw new ArgumentNullException( nameof( mirror ) );
        MinimumSize = new Vector2( 480, 320 );
        MouseTracking = true;
        FocusMode = FocusMode.Click;
        Scene = Scene.CreateEditorScene();
        using ( Scene.Push() )
        {
            camera = new GameObject( true, "spectator camera" ).GetOrAddComponent<CameraComponent>( false );
            camera.BackgroundColor = Color.Black;
            camera.ZFar = 50000;
            camera.FieldOfView = 80;
            camera.Enabled = true;
            // The spectator's own light, so a scene whose lights are not in the stream (or are not mirrored) is not black. It is not part of the mirror.
            var ambient = new GameObject( true, "spectator ambient" ).GetOrAddComponent<AmbientLight>( false );
            ambient.Color = Color.White * 0.35f;
            ambient.Enabled = true;
        }
    }

    public int PuppetCount => puppets.Count;

    public override void OnDestroyed()
    {
        base.OnDestroyed();
        Release();
    }

    /// <summary>Destroys the puppet scene. Safe to call more than once.</summary>
    public void Release()
    {
        puppets.Clear();
        Scene scene = Scene;
        Scene = null;
        scene?.Destroy();
    }

    protected override void OnMousePress( MouseEvent e )
    {
        base.OnMousePress( e );
        if ( ( e.ButtonState & MouseButtons.Right ) != 0 )
        {
            looking = true;
            lastMouse = e.LocalPosition;
            Focus();
        }
    }

    protected override void OnMouseReleased( MouseEvent e )
    {
        base.OnMouseReleased( e );
        if ( ( e.ButtonState & MouseButtons.Right ) == 0 )
        {
            looking = false;
        }
    }

    protected override void OnMouseMove( MouseEvent e )
    {
        base.OnMouseMove( e );
        if ( looking )
        {
            lookDelta += e.LocalPosition - lastMouse;
        }
        lastMouse = e.LocalPosition;
    }

    protected override void OnMouseWheel( WheelEvent e )
    {
        base.OnMouseWheel( e );
        wheel += e.Delta > 0 ? 1 : e.Delta < 0 ? -1 : 0;
        e.Accept();
    }

    protected override void PreFrame()
    {
        if ( !Scene.IsValid() )
        {
            return;
        }
        float dt = RealTime.Delta;
        nowMs = RealTime.Now * 1000.0;
        if ( mirror.Version != appliedVersion )
        {
            appliedVersion = mirror.Version;
            Rebuild();
        }
        UpdatePoses();
        FlyInput input = new FlyInput(
            Forward: Axis( KeyCode.W, KeyCode.S ), Right: Axis( KeyCode.D, KeyCode.A ), Up: Axis( KeyCode.E, KeyCode.Q ),
            LookX: lookDelta.x, LookY: lookDelta.y,
            Fast: Editor.Application.IsKeyDown( KeyCode.Shift ), Slow: Editor.Application.IsKeyDown( KeyCode.Control ),
            Wheel: wheel, Looking: looking );
        lookDelta = default;
        wheel = 0;
        if ( !IsFocused )
        {
            // Keys are read globally, so only a focused view flies; an unfocused one still follows the stream.
            input = input with { Forward = 0, Right = 0, Up = 0 };
        }
        fly = FreeFlyCamera.Step( fly, input, dt );
        var forward = FreeFlyCamera.Forward( fly );
        camera.WorldPosition = new Vector3( fly.X, fly.Y, fly.Z );
        camera.WorldRotation = Rotation.LookAt( new Vector3( forward.X, forward.Y, forward.Z ), Vector3.Up );
        Scene.EditorTick( RealTime.Now, dt );
    }

    private static float Axis( KeyCode positive, KeyCode negative ) => ( Editor.Application.IsKeyDown( positive ) ? 1f : 0f ) - ( Editor.Application.IsKeyDown( negative ) ? 1f : 0f );

    /// <summary>Makes the puppet scene match the mirror: objects created and removed, parents, enabled flags and components whose data changed.</summary>
    private void Rebuild()
    {
        using ( Scene.Push() )
        {
            foreach ( int gone in puppets.Keys.Where( k => !mirror.Objects.ContainsKey( k ) ).ToList() )
            {
                if ( puppets[gone].Object.IsValid() ) puppets[gone].Object.Destroy();
                puppets.Remove( gone );
            }
            foreach ( MirrorObject source in mirror.Objects.Values )
            {
                if ( !puppets.TryGetValue( source.Index, out Puppet puppet ) )
                {
                    puppet = new Puppet { Object = new GameObject( true, source.Name ) };
                    puppets[source.Index] = puppet;
                }
                puppet.Object.Enabled = source.Enabled;
                if ( puppet.Object.Name != source.Name ) puppet.Object.Name = source.Name;
                puppet.Position = source.Position;
                double stamp = nowMs;
                puppet.Poses.Add( stamp, PoseOf( source ) );
                ApplyComponents( puppet, source );
            }
            // A second pass for parents, because an object may arrive before its parent. World transforms are what the stream carries, so a parent link
            // is only for the hierarchy shown to people; it must not move the child, hence the world transform is written again after it.
            foreach ( MirrorObject source in mirror.Objects.Values )
            {
                Puppet puppet = puppets[source.Index];
                GameObject parent = source.Parent >= 0 && puppets.TryGetValue( source.Parent, out Puppet p ) ? p.Object : Scene;
                if ( puppet.Object.Parent != parent && parent != puppet.Object )
                {
                    puppet.Object.SetParent( parent, false );
                }
            }
        }
        if ( !framed && puppets.Count > 0 )
        {
            // First sight of a scene: stand back from its centre, so the spectator is not inside it.
            var centre = mirror.Objects.Values.Aggregate( new double[3], ( a, o ) => { a[0] += o.Position[0]; a[1] += o.Position[1]; a[2] += o.Position[2]; return a; } );
            float n = mirror.Objects.Count;
            fly = FlyState.At( (float)centre[0] / n, (float)centre[1] / n - 600, (float)centre[2] / n + 250 );
            framed = true;
        }
    }

    private static Pose PoseOf( MirrorObject o ) =>
        new Pose( (float)o.Position[0], (float)o.Position[1], (float)o.Position[2], (float)o.Rotation[0], (float)o.Rotation[1], (float)o.Rotation[2], (float)o.Rotation[3], (float)o.Scale[0], (float)o.Scale[1], (float)o.Scale[2] );

    private void UpdatePoses()
    {
        var eye = camera.WorldPosition;
        foreach ( Puppet puppet in puppets.Values )
        {
            if ( !puppet.Object.IsValid() || puppet.Poses.Count == 0 )
            {
                continue;
            }
            if ( !puppet.Poses.TrySample( nowMs - InterpolationDelayMs, out Pose pose ) && !puppet.Poses.TrySample( nowMs, out pose ) )
            {
                continue;
            }
            var position = new Vector3( pose.X, pose.Y, pose.Z );
            // Near the camera the pose is smoothed, far away it snaps to the newest, so interpolation work is bounded.
            if ( ( position - eye ).LengthSquared > SmoothingRadius * SmoothingRadius && puppet.Poses.TrySample( nowMs, out Pose newest ) )
            {
                pose = newest;
                position = new Vector3( pose.X, pose.Y, pose.Z );
            }
            puppet.Object.WorldPosition = position;
            puppet.Object.WorldRotation = new Rotation( pose.Qx, pose.Qy, pose.Qz, pose.Qw );
            puppet.Object.WorldScale = new Vector3( pose.Sx, pose.Sy, pose.Sz );
        }
    }

    private void ApplyComponents( Puppet puppet, MirrorObject source )
    {
        foreach ( string guid in puppet.Components.Keys.Where( g => !source.Components.ContainsKey( g ) ).ToList() )
        {
            if ( puppet.Components[guid].Component.IsValid() ) puppet.Components[guid].Component.Destroy();
            puppet.Components.Remove( guid );
        }
        foreach ( MirrorComponent data in source.Components.Values )
        {
            if ( puppet.Components.TryGetValue( data.Guid, out var existing ) && existing.Revision == data.Revision && existing.Component.IsValid() )
            {
                continue;
            }
            Component component = existing.Component;
            if ( !component.IsValid() )
            {
                component = CreateComponent( puppet.Object, data.Type );
                if ( component == null )
                {
                    continue;
                }
            }
            // Only what the whitelist kept is written, through the engine's own component deserialiser, the call SceneAdapter already uses.
            var json = new JsonObject { ["__type"] = data.Type, ["__guid"] = data.Guid, ["__enabled"] = data.Enabled };
            foreach ( var property in data.Props ) json[property.Key] = property.Value?.DeepClone();
            component.DeserializeImmediately( json );
            component.Enabled = data.Enabled;
            puppet.Components[data.Guid] = (component, data.Revision);
        }
    }

    /// <summary>
    /// Creates the component of a whitelisted type, or null. The whitelist is checked again here (a caller must not be able to skip it), and the
    /// resolved type must be defined in the engine's own assembly: a game type of the same short name can never be returned.
    /// </summary>
    private static Component CreateComponent( GameObject owner, string type )
    {
        if ( !SpectateWhitelist.IsAllowedType( type ) )
        {
            return null;
        }
        TypeDescription description = Game.TypeLibrary.GetType<Component>( type, true );
        if ( description == null || description.TargetType.Assembly != EngineAssembly || description.TargetType.FullName != type )
        {
            return null;
        }
        return owner.Components.Create( description, startEnabled: false );
    }
}
