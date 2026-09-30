using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

    public static Dictionary<string, ObjectState> Capture(Scene scene)
    {
        Dictionary<string, ObjectState> result = new Dictionary<string, ObjectState>();
        using (scene.Push())
        {
            GameObject[] array = scene.Children.ToArray();
            foreach (GameObject gameObject in array)
            {
                if (gameObject.IsValid())
                {
                    JsonObject jsonObject = gameObject.Serialize();
                    if (jsonObject != null)
                    {
                        Flatten(JsonNode.Parse(jsonObject.ToJsonString()).AsObject(), "");
                    }
                }
            }
            return result;
        }
        void Flatten(JsonObject data, string parent)
        {
            string value = data["__guid"].GetValue<string>();
            JsonArray jsonArray = data["Children"] as JsonArray;
            data.Remove("Children");
            result.Add(value, new ObjectState(value, parent, data));
            if (jsonArray != null)
            {
                foreach (JsonNode item in jsonArray)
                {
                    Flatten(item.AsObject(), value);
                }
            }
        }
    }

    public static void Apply(SceneEditorSession session, Dictionary<string, ObjectState> target)
    {
        using (session.Scene.Push())
        {
            Dictionary<string, GameObject> dictionary = Walk(session.Scene).ToDictionary((GameObject x) => x.Id.ToString());
            Dictionary<string, ObjectState> before = Capture(session.Scene);
            List<Change> list = StateRules.Diff(before, target);
            foreach (Change item in list)
            {
                StateRules.ValidateChange(item);
            }
            StateRules.ValidateGraph(target);
            foreach (Change item2 in list.Where((Change x) => (object)x.Before == null))
            {
                GameObject gameObject = session.Scene.CreateObject(enabled: false);
                gameObject.Deserialize((JsonObject)item2.After.Data.DeepClone());
                if (gameObject.Id.ToString() != item2.Id)
                {
                    throw new InvalidOperationException("Engine did not preserve object GUID");
                }
                dictionary.Add(item2.Id, gameObject);
            }
            foreach (ObjectState value2 in target.Values)
            {
                if (dictionary.TryGetValue(value2.Id, out var value))
                {
                    GameObject gameObject2 = (string.IsNullOrEmpty(value2.Parent) ? session.Scene : dictionary[value2.Parent]);
                    if (value.Parent != gameObject2)
                    {
                        value.SetParent(gameObject2, keepWorldPosition: false);
                    }
                }
            }
            foreach (Change item3 in list.Where((Change x) => (object)x.After != null))
            {
                GameObject gameObject3 = dictionary[item3.Id];
                JsonObject data = item3.After.Data;
                gameObject3.Name = data["Name"].GetValue<string>();
                gameObject3.LocalPosition = data["Position"].Deserialize<Vector3>();
                gameObject3.LocalRotation = data["Rotation"].Deserialize<Rotation>();
                gameObject3.LocalScale = data["Scale"].Deserialize<Vector3>();
                gameObject3.Enabled = data["Enabled"].GetValue<bool>();
                gameObject3.Tags.RemoveAll();
                gameObject3.Tags.Add(data["Tags"].GetValue<string>().Split(',', StringSplitOptions.RemoveEmptyEntries));
                if ((object)item3.Before == null || JsonNode.DeepEquals(item3.Before.Data["Components"], data["Components"]))
                {
                    continue;
                }
                foreach (JsonNode item4 in data["Components"].AsArray())
                {
                    string id = item4["__guid"].GetValue<string>();
                    Component component = gameObject3.Components.GetAll().Single((Component x) => x.Id.ToString() == id);
                    component.DeserializeImmediately((JsonObject)item4.DeepClone());
                }
            }
            foreach (Change item5 in list.Where((Change x) => (object)x.After == null))
            {
                if (dictionary[item5.Id].IsValid())
                {
                    dictionary[item5.Id].Destroy();
                }
            }
            session.HasUnsavedChanges |= list.Count > 0;
        }
    }

    public static void ValidateSiblingOrder(Dictionary<string, ObjectState> before, Dictionary<string, ObjectState> after)
    {
        foreach (string parent in before.Values.Select((ObjectState x) => x.Parent).Distinct())
        {
            string[] ids = (from x in before.Values
                where x.Parent == parent && after.TryGetValue(x.Id, out var value) && value.Parent == parent
                select x.Id).ToArray();
            if (!ids.SequenceEqual(from x in after.Values
                where x.Parent == parent && ids.Contains(x.Id)
                select x.Id))
            {
                throw new InvalidOperationException("Sibling reordering is not supported; local order retained");
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
