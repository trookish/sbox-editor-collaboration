using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Editor;
using Sandbox;

namespace TeamCreate;

public static class SceneAdapter
{
    public static string Root => Project.Current.RootDirectory.FullName;

    public static string PathOf(SceneEditorSession session)
    {
        return session?.Scene?.Source?.ResourcePath?.Replace('\\', '/').ToLowerInvariant();
    }

    public static DateTime DiskStamp(string path)
    {
        return File.GetLastWriteTimeUtc(Path.Combine(Root, "Assets", path));
    }

    public static IEnumerable<GameObject> Walk(GameObject parent)
    {
        GameObject[] array = parent.Children.ToArray();
        foreach (GameObject child in array)
        {
            if (!child.IsValid() || child.Flags.HasFlag(GameObjectFlags.NotSaved))
            {
                continue;
            }
            yield return child;
            foreach (GameObject item in Walk(child))
            {
                yield return item;
            }
        }
    }

    // Components this editor could not create because their type is not in its project (the sender has code this editor
    // lacks), per scene and object: their JSON and their position in the object's list. Capture writes them back into the
    // object's state, so the next scan does not mistake the missing component for a removal and delete it for everyone.
    private static readonly ConditionalWeakTable<Scene, Dictionary<string, List<SkippedComponent>>> skipped = new ConditionalWeakTable<Scene, Dictionary<string, List<SkippedComponent>>>();

    private sealed record SkippedComponent( int Index, string Guid, JsonObject Json );

    // Component types already reported, so each missing type is announced once per editor session, and the notices gathered
    // since the session last collected them.
    private static readonly HashSet<string> reportedTypes = new HashSet<string>( StringComparer.Ordinal );

    private static readonly List<string> notices = new List<string>();

    /// <summary>
    /// The formal sentences gathered by <see cref="Apply"/> since the last call (one per component type this project lacks),
    /// joined, or null when there is nothing to say. The collaboration session shows them through its merge notice.
    /// </summary>
    public static string TakeNotices()
    {
        if ( notices.Count == 0 )
        {
            return null;
        }
        string text = ChangeRebase.JoinNotices( notices.ToArray() );
        notices.Clear();
        return text;
    }

    public static Dictionary<string, ObjectState> Capture(Scene scene)
    {
        Dictionary<string, ObjectState> result = new Dictionary<string, ObjectState>();
        skipped.TryGetValue( scene, out Dictionary<string, List<SkippedComponent>> held );
        using (scene.Push())
        {
            // The order key is the object's position among the siblings the engine saves (the serialised Children list, or the
            // scene's saved root objects), so it means the same thing on every editor whatever unsaved objects sit in between.
            int rootOrder = 0;
            GameObject[] array = scene.Children.ToArray();
            foreach (GameObject gameObject in array)
            {
                if (gameObject.IsValid())
                {
                    JsonObject jsonObject = gameObject.Serialize();
                    if (jsonObject != null)
                    {
                        Flatten(JsonNode.Parse(jsonObject.ToJsonString()).AsObject(), "", rootOrder++);
                    }
                }
            }
            return result;
        }
        void Flatten(JsonObject data, string parent, int order)
        {
            string value = data["__guid"].GetValue<string>();
            JsonArray jsonArray = data["Children"] as JsonArray;
            data.Remove("Children");
            data[SiblingOrder.Key] = order;
            if ( held != null && held.TryGetValue( value, out List<SkippedComponent> missing ) )
            {
                RestoreSkipped( data, missing );
            }
            result.Add(value, new ObjectState(value, parent, data));
            if (jsonArray != null)
            {
                int childOrder = 0;
                foreach (JsonNode item in jsonArray)
                {
                    Flatten(item.AsObject(), value, childOrder++);
                }
            }
        }
    }

    // Puts the components this editor could not create back at the positions they hold in the shared scene.
    private static void RestoreSkipped( JsonObject data, List<SkippedComponent> missing )
    {
        JsonArray components = data["Components"] as JsonArray;
        if ( components == null )
        {
            components = new JsonArray();
            data["Components"] = components;
        }
        foreach ( SkippedComponent component in missing.OrderBy( x => x.Index ) )
        {
            if ( components.Any( x => x?["__guid"] is JsonValue guid && guid.TryGetValue( out string text ) && text == component.Guid ) )
            {
                continue;
            }
            components.Insert( Math.Min( component.Index, components.Count ), component.Json.DeepClone() );
        }
    }

    /// <summary>
    /// Brings the scene to exactly <paramref name="target"/>: objects are created, deleted, re-parented and put in their
    /// sibling order, and every changed object is made equal to its state, whatever the property or component.
    /// </summary>
    /// <remarks>
    /// Engine API, from the decompiled Sandbox.Engine (GameObject.Serialize.cs) and Sandbox.Engine.xml:
    /// <c>GameObject.Deserialize( JsonObject, DeserializeOptions )</c> can update an existing hierarchy in place only with
    /// <c>DeserializeOptions.IsRefreshing</c>, which is <c>internal</c> (the editor's undo and prefab refresh use it; a library
    /// cannot set it). Without it, a Components array in the JSON creates every component again and a Children array creates
    /// every child again, on top of the existing ones. So the fallback is used, per object:
    /// <list type="bullet">
    /// <item>Object-level state: <c>GameObject.Deserialize( json )</c> with <c>Components</c> and <c>Children</c> removed. With
    /// neither array present it only reads the object's own fields (name, transform, enabled, tags, flags, network mode, flags,
    /// orphan mode, transmit, owner transfer), so every object property the engine saves is applied without a list here.</item>
    /// <item>Components, matched by <c>__guid</c>: an existing one is updated with <c>Component.DeserializeImmediately</c>; a new
    /// one is created with <c>ComponentList.Create( TypeDescription, false )</c>, the type resolved as the engine resolves it
    /// (<c>Game.TypeLibrary.GetType&lt;Component&gt;( name, true )</c>, then the short name), and deserialised (which sets its
    /// <c>__guid</c> and <c>__enabled</c>); one absent from the state is destroyed (<c>Component.Destroy</c> removes it from the
    /// list at once); the list is put in the state's order with <c>ComponentList.Move</c>. Components the engine never saves
    /// (<c>NotSaved</c>, <c>NotCloned</c>) are left alone.</item>
    /// <item>A prefab instance (<c>__Prefab</c>, before or after) cannot be patched this way: its components and children come
    /// from the prefab file through <c>__PrefabInstancePatch</c>. It is destroyed with <c>DestroyImmediate</c> (so its guid is
    /// free at once) and deserialised again from the state, as a creation is; children that are states of their own are moved
    /// to the scene root first and put back by the parenting pass.</item>
    /// <item>Sibling order: <c>GameObject.Children</c> is a public, ordered list and the engine reorders it by assigning its
    /// elements (the refresh path does <c>Children[i] = ...</c>); the saved siblings are written back into the positions
    /// they already occupy, in <see cref="SiblingOrder.Arrange"/> order, so unsaved siblings keep their places.</item>
    /// </list>
    /// A component type this project lacks is not created: its JSON stays in the state, Capture writes it back, and one notice
    /// per type is gathered for <see cref="TakeNotices"/>.
    /// </remarks>
    public static void Apply(SceneEditorSession session, Dictionary<string, ObjectState> target)
    {
        Scene scene = session.Scene;
        using (scene.Push())
        {
            StateRules.ValidateGraph(target);
            Dictionary<string, GameObject> live = Walk(scene).ToDictionary((GameObject x) => x.Id.ToString());
            Dictionary<string, ObjectState> before = Capture(scene);
            List<Change> list = StateRules.Diff(before, target);
            Dictionary<string, List<SkippedComponent>> held = skipped.GetValue( scene, _ => new Dictionary<string, List<SkippedComponent>>() );

            // Prefab instances, and objects becoming or ceasing to be one, are rebuilt from their state.
            foreach (Change change in list.Where((Change x) => (object)x.Before != null && (object)x.After != null && (IsPrefab(x.Before) || IsPrefab(x.After))))
            {
                GameObject old = live[change.Id];
                foreach (GameObject child in old.Children.ToArray())
                {
                    if (child.IsValid() && target.TryGetValue(child.Id.ToString(), out ObjectState childState) && childState.Parent == change.Id)
                    {
                        child.SetParent(scene, keepWorldPosition: false);
                    }
                }
                old.DestroyImmediate();
                live[change.Id] = Create(scene, change.After);
                held.Remove(change.Id);
            }
            foreach (Change change in list.Where((Change x) => (object)x.Before == null))
            {
                live.Add(change.Id, Create(scene, change.After));
                held.Remove(change.Id);
            }
            foreach (ObjectState state in target.Values)
            {
                if (live.TryGetValue(state.Id, out GameObject gameObject))
                {
                    GameObject parent = (string.IsNullOrEmpty(state.Parent) ? scene : live[state.Parent]);
                    if (gameObject.Parent != parent)
                    {
                        gameObject.SetParent(parent, keepWorldPosition: false);
                    }
                }
            }
            foreach (Change change in list.Where((Change x) => (object)x.Before != null && (object)x.After != null && !IsPrefab(x.Before) && !IsPrefab(x.After)))
            {
                GameObject gameObject = live[change.Id];
                JsonObject own = SiblingOrder.ForEngine(change.After.Data);
                own.Remove("Components");
                gameObject.Deserialize(own);
                List<SkippedComponent> missing = ApplyComponents(gameObject, change.After.Data["Components"] as JsonArray);
                if (missing.Count > 0)
                {
                    held[change.Id] = missing;
                }
                else
                {
                    held.Remove(change.Id);
                }
            }
            foreach (Change change in list.Where((Change x) => (object)x.After == null))
            {
                held.Remove(change.Id);
                if (live[change.Id].IsValid())
                {
                    live[change.Id].Destroy();
                }
            }
            ApplySiblingOrder(scene, target, live);
            session.HasUnsavedChanges |= list.Count > 0;
        }
    }

    private static bool IsPrefab(ObjectState state) => state.Data.ContainsKey("__Prefab");

    // A new object from its state. The engine's own creation path handles every component, including one whose type this
    // project lacks: it keeps that component's JSON in a MissingComponent, which serialises back unchanged.
    private static GameObject Create(Scene scene, ObjectState state)
    {
        GameObject gameObject = scene.CreateObject(enabled: false);
        gameObject.Deserialize(SiblingOrder.ForEngine(state.Data));
        if (gameObject.Id.ToString() != state.Id)
        {
            throw new InvalidOperationException("Engine did not preserve object GUID");
        }
        if (state.Data["Components"] is JsonArray components)
        {
            foreach (JsonNode component in components)
            {
                string type = component?["__type"]?.GetValue<string>();
                if (type != null && ResolveType(type) == null)
                {
                    ReportMissing(type);
                }
            }
        }
        return gameObject;
    }

    // Makes the object's saved components equal `components` (absent means none). Returns the ones whose type this project lacks.
    private static List<SkippedComponent> ApplyComponents(GameObject gameObject, JsonArray components)
    {
        List<SkippedComponent> missing = new List<SkippedComponent>();
        List<Component> desired = new List<Component>();
        Dictionary<string, Component> existing = new Dictionary<string, Component>(StringComparer.Ordinal);
        foreach (Component component in gameObject.Components.GetAll().ToArray())
        {
            if (component != null && IsSaved(component))
            {
                existing[component.Id.ToString()] = component;
            }
        }
        int index = 0;
        foreach (JsonNode node in components ?? new JsonArray())
        {
            JsonObject json = node.AsObject();
            string guid = json["__guid"].GetValue<string>();
            string type = json["__type"].GetValue<string>();
            existing.TryGetValue(guid, out Component component);
            existing.Remove(guid);
            TypeDescription description = ResolveType(type);
            if (description == null)
            {
                // Not created here; a MissingComponent left by an earlier creation holds stale JSON, so it goes too.
                component?.Destroy();
                missing.Add(new SkippedComponent(index++, guid, (JsonObject)json.DeepClone()));
                ReportMissing(type);
                continue;
            }
            if (component != null && (component is MissingComponent || component.GetType() != description.TargetType))
            {
                component.Destroy();
                component = null;
            }
            component ??= gameObject.Components.Create(description, startEnabled: false);
            component.DeserializeImmediately((JsonObject)json.DeepClone());
            desired.Add(component);
            index++;
        }
        foreach (Component component in existing.Values)
        {
            component.Destroy();
        }
        // Into the state's order, using the positions the saved components occupy now.
        List<Component> all = gameObject.Components.GetAll().ToList();
        List<int> slots = desired.Select((Component x) => all.IndexOf(x)).OrderBy((int x) => x).ToList();
        for (int i = 0; i < desired.Count; i++)
        {
            int at = gameObject.Components.GetAll().ToList().IndexOf(desired[i]);
            if (at != slots[i])
            {
                gameObject.Components.Move(desired[i], slots[i] - at);
            }
        }
        return missing;
    }

    private static bool IsSaved(Component component) => !component.Flags.Contains(ComponentFlags.NotSaved) && !component.Flags.Contains(ComponentFlags.NotCloned);

    // As the engine resolves a component type when it loads a scene: the full name, then the name after the last dot.
    private static TypeDescription ResolveType(string type)
    {
        TypeDescription description = Sandbox.Game.TypeLibrary.GetType<Component>(type, true);
        int dot = type.LastIndexOf('.');
        if (description == null && dot > 0 && dot < type.Length - 1)
        {
            description = Sandbox.Game.TypeLibrary.GetType<Component>(type.Substring(dot + 1), true);
        }
        return (description == null || description.TargetType.IsAbstract) ? null : description;
    }

    private static void ReportMissing(string type)
    {
        if (reportedTypes.Add(type))
        {
            notices.Add(StateRules.MissingComponentTypeNotice(type));
        }
    }

    // Puts every parent's saved children in the order their states give.
    private static void ApplySiblingOrder(Scene scene, Dictionary<string, ObjectState> target, Dictionary<string, GameObject> live)
    {
        foreach (IGrouping<string, ObjectState> group in target.Values.GroupBy((ObjectState x) => x.Parent ?? ""))
        {
            GameObject parent = (group.Key == "") ? scene : (live.TryGetValue(group.Key, out GameObject found) ? found : null);
            if (parent == null || !parent.IsValid())
            {
                continue;
            }
            List<GameObject> children = parent.Children;
            List<string> current = children.Where((GameObject x) => x.IsValid() && target.ContainsKey(x.Id.ToString())).Select((GameObject x) => x.Id.ToString()).ToList();
            Dictionary<string, int?> orders = group.ToDictionary((ObjectState x) => x.Id, (ObjectState x) => SiblingOrder.Read(x.Data));
            List<string> arranged = SiblingOrder.Arrange(current, orders);
            if (arranged.SequenceEqual(current))
            {
                continue;
            }
            List<int> slots = current.Select((string id) => children.IndexOf(live[id])).OrderBy((int x) => x).ToList();
            for (int i = 0; i < arranged.Count; i++)
            {
                children[slots[i]] = live[arranged[i]];
            }
        }
    }

    public static string Baseline(bool includeScenes = true)
    {
        List<string> list = new List<string> { "teamcreate-1" };
        Assembly[] array = new Assembly[2]
        {
            typeof(Scene).Assembly,
            typeof(Widget).Assembly
        };
        foreach (Assembly assembly in array)
        {
            list.Add(assembly.ManifestModule.ModuleVersionId.ToString());
        }
        string[] array2 = new string[5] { "Assets", "Code", "Editor", "Libraries", "ProjectSettings" };
        foreach (string path in array2)
        {
            string path2 = Path.Combine(Root, path);
            if (!Directory.Exists(path2))
            {
                continue;
            }
            foreach (string item in Directory.EnumerateFiles(path2, "*", SearchOption.AllDirectories).OrderBy((string x) => x, StringComparer.Ordinal))
            {
                string text = Path.GetRelativePath(Root, item).Replace('\\', '/');
                if (!text.Split('/').Any((string x) =>
                {
                    switch (x)
                    {
                    case "obj":
                    case "bin":
                    case "Properties":
                    case ".sbox":
                        return true;
                    default:
                        return false;
                    }
                }) && !item.EndsWith(".csproj") && !item.EndsWith("_c") && !item.EndsWith("_d") && !item.Contains(".generated.") && (includeScenes || !item.EndsWith(".scene", StringComparison.OrdinalIgnoreCase)))
                {
                    list.Add(text + ":" + Convert.ToHexString(SHA256.HashData(MetaFileRules.Canonical(item, File.ReadAllBytes(item)))));
                }
            }
        }
        JsonNode jsonNode = JsonNode.Parse(File.ReadAllText(Directory.GetFiles(Root, "*.sbproj").Single()));
        list.Add(jsonNode.ToJsonString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", list))));
    }
}
