using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Editor;
using Sandbox;

namespace TeamCreate;

/// <summary>
/// Reads the running game's scene into <see cref="PlayObject"/>s for the <see cref="PlaySampler"/>. Read-only: nothing is written to the runtime scene
/// and no component is touched except to serialise it. Transforms are read every sample; component data, which is far more expensive to produce, is
/// refreshed every <see cref="ComponentRefreshTicks"/> samples and for objects not seen before, so a 300-object scene is not fully serialised at 20 Hz.
/// The sampler strips everything off the whitelist again before it sends, so this does not filter.
/// </summary>
public sealed class PlayCapture
{
    public const int ComponentRefreshTicks = 5;

    /// <summary>A scene bigger than this is cut off, so a runaway spawner cannot make one sample unbounded.</summary>
    public const int MaxObjects = 5000;

    private readonly Dictionary<string, JsonArray> components = new Dictionary<string, JsonArray>( StringComparer.Ordinal );

    private int tick;

    public bool Truncated { get; private set; }

    public List<PlayObject> Capture( GameEditorSession session )
    {
        var result = new List<PlayObject>();
        Scene scene = session?.Scene;
        if ( scene == null || !scene.IsValid() )
        {
            return result;
        }
        bool refresh = tick++ % ComponentRefreshTicks == 0;
        Truncated = false;
        using ( scene.Push() )
        {
            foreach ( GameObject root in scene.Children.ToArray() )
            {
                Walk( root, null, refresh, result );
            }
        }
        if ( refresh )
        {
            var live = new HashSet<string>( StringComparer.Ordinal );
            foreach ( PlayObject o in result ) live.Add( o.Id );
            foreach ( string gone in new List<string>( components.Keys ) )
            {
                if ( !live.Contains( gone ) ) components.Remove( gone );
            }
        }
        return result;
    }

    private void Walk( GameObject go, string parent, bool refresh, List<PlayObject> into )
    {
        if ( go == null || !go.IsValid() || go.IsDestroyed )
        {
            return;
        }
        if ( into.Count >= MaxObjects )
        {
            Truncated = true;
            return;
        }
        string id = go.Id.ToString();
        if ( ( refresh || !components.ContainsKey( id ) ) )
        {
            components[id] = ComponentsOf( go );
        }
        Vector3 p = go.WorldPosition, s = go.WorldScale;
        Rotation r = go.WorldRotation;
        into.Add( new PlayObject( id, parent, go.Name ?? "", new Vec3( p.x, p.y, p.z ), new Quat( r.x, r.y, r.z, r.w ), new Vec3( s.x, s.y, s.z ), go.Enabled, components[id] ) );
        foreach ( GameObject child in go.Children.ToArray() )
        {
            Walk( child, id, refresh, into );
        }
    }

    private static readonly System.Reflection.Assembly EngineAssembly = typeof( ModelRenderer ).Assembly;

    /// <summary>
    /// The serialised form of the components of this object that a spectator may ever create, and nothing else. A game's own component is not even
    /// serialised (it would only be stripped afterwards), which also keeps its data off the wire. This is the first of the two whitelist checks; the
    /// sampler and the receiver repeat it. A prefab instance's components come out the same way, because each component serialises itself.
    /// </summary>
    private static JsonArray ComponentsOf( GameObject go )
    {
        var result = new JsonArray();
        try
        {
            foreach ( Component component in go.Components.GetAll() )
            {
                if ( component == null || !component.IsValid() )
                {
                    continue;
                }
                Type type = component.GetType();
                if ( type.Assembly != EngineAssembly || !SpectateWhitelist.IsAllowedType( type.FullName ) )
                {
                    continue;
                }
                JsonNode node = component.Serialize();
                if ( node != null ) result.Add( node );
            }
        }
        catch ( Exception )
        {
            // One object that cannot be serialised right now (mid-destruction, say) must not end the share: it is sent with what was read so far.
        }
        return result;
    }
}
