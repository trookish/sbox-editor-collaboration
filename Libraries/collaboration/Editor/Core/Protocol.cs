using System.Text.Json.Nodes;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Linq;

namespace TeamCreate;

public sealed record ObjectState(string Id, string Parent, JsonObject Data);

public sealed record Change(string Id, ObjectState Before, ObjectState After);

public sealed class Packet
{
    public string Kind { get; set; }

    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Peer { get; set; }

    public string Name { get; set; }

    public string Project { get; set; }

    public string Baseline { get; set; }

    public string Token { get; set; }

    public string Session { get; set; }

    public string Scene { get; set; }

    public string Error { get; set; }

    public long Revision { get; set; }

    public bool Final { get; set; }

    public List<ObjectState> Objects { get; set; }

    public List<Change> Changes { get; set; }

    public JsonObject Presence { get; set; }

    public int? ProtocolVersion { get; set; }

    public CollaborationMode? Mode { get; set; }

    public string RuntimeRunId { get; set; }

    public long? RuntimeTick { get; set; }

    public long? RuntimeSequence { get; set; }

    public string RuntimeOperationKind { get; set; }

    public string SteamId { get; set; }

    public string DisplayName { get; set; }

    public string SteamToken { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string OrgIdent { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string LibraryVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ProjectManifest { get; set; }
}

public static class Protocol
{
    public const int CurrentVersion = 4;

    public const string CurrentVersionName = "teamcreate-4";

    public const string VersionName = "teamcreate-4";

    public const string OlderLibraryMessage = "This peer runs an older collaboration library. Update the collaboration library on both machines and reconnect.";

    public const string MissingProjectRevisionMessage = "This peer did not report its project revision. Update the collaboration library on both machines and reconnect.";

    public static bool IsValidMode(CollaborationMode? mode)
    {
        return mode == CollaborationMode.Source || mode == CollaborationMode.RuntimeExperimental;
    }

    public static bool ValidateSourceHandshake(Packet p)
    {
        return GetHandshakeError(p) == null && p != null && p.Mode == CollaborationMode.Source;
    }

    public static string GetHandshakeError(Packet p, bool requireValidPeer = false)
    {
        if (p == null || p.Kind != "hello" || (requireValidPeer && !Guid.TryParse(p.Peer, out var _)))
        {
            return "Invalid collaboration handshake";
        }
        if (p.ProtocolVersion != 4)
        {
            return $"Unsupported collaboration protocol version. This client requires protocol {4} ({"teamcreate-4"}).";
        }
        if (!IsValidMode(p.Mode))
        {
            return "Invalid collaboration mode in hello. Source or runtime collaboration mode is required.";
        }
        if (p.Mode == CollaborationMode.RuntimeExperimental && !RuntimeStateRules.IsValidRunId(p.RuntimeRunId))
        {
            return "Runtime collaboration handshake requires a valid runtime run ID.";
        }
        if (!SteamIdentityRules.TryParse(p.SteamId, out var _))
        {
            return "Collaboration handshake requires a valid Steam account ID. Sign in to Steam and reconnect.";
        }
        if (!SteamTokenRules.IsPlausibleToken(p.SteamToken))
        {
            return "Collaboration handshake requires a Steam identity token. Reconnect to obtain one.";
        }
        if (string.IsNullOrWhiteSpace(p.OrgIdent) || string.IsNullOrWhiteSpace(p.LibraryVersion) || !Version.TryParse(p.LibraryVersion, out Version _))
        {
            return "This peer runs an older collaboration library. Update the collaboration library on both machines and reconnect.";
        }
        if (string.IsNullOrWhiteSpace(p.ProjectManifest))
        {
            return "This peer did not report its project revision. Update the collaboration library on both machines and reconnect.";
        }
        return null;
    }

    public static bool ValidateHandshake(Packet p)
    {
        return GetHandshakeError(p) == null;
    }

    public static bool ValidateRuntimeEnvelope(Packet p, string activeRunId, string activeScenePath = null, long? lastAcceptedSequence = null)
    {
        if (p == null || p.ProtocolVersion != 4 || p.Mode != CollaborationMode.RuntimeExperimental)
        {
            return false;
        }
        if (!IsValidMode(p.Mode) || !RuntimeStateRules.IsValidRunId(activeRunId) || !RuntimeStateRules.IsValidRunId(p.RuntimeRunId))
        {
            return false;
        }
        if (!string.Equals(p.RuntimeRunId, activeRunId, StringComparison.Ordinal))
        {
            return false;
        }
        if (!p.RuntimeSequence.HasValue || p.RuntimeSequence.Value <= 0)
        {
            return false;
        }
        if (lastAcceptedSequence.HasValue && p.RuntimeSequence.Value <= lastAcceptedSequence.Value)
        {
            return false;
        }
        return activeScenePath == null || string.Equals(p.Scene, activeScenePath, StringComparison.Ordinal);
    }
}

public static class StateRules
{
    public const string Version = "teamcreate-1";

    public static readonly HashSet<string> ObjectFields = new HashSet<string> { "Name", "Position", "Rotation", "Scale", "Enabled", "Tags" };

    public static readonly Dictionary<string, HashSet<string>> ComponentFields = new Dictionary<string, HashSet<string>>
    {
        ["Sandbox.ModelRenderer"] = new HashSet<string> { "Tint" },
        ["Sandbox.CameraComponent"] = new HashSet<string> { "FieldOfView", "BackgroundColor", "Orthographic", "OrthographicHeight", "ZNear", "ZFar" },
        ["Sandbox.DirectionalLight"] = new HashSet<string> { "LightColor", "SkyColor", "Shadows" },
        ["Sandbox.PointLight"] = new HashSet<string> { "LightColor", "Radius", "Shadows" },
        ["Sandbox.BoxCollider"] = new HashSet<string> { "Center", "Scale", "IsTrigger" }
    };

    public static bool Equal(ObjectState a, ObjectState b)
    {
        return ((object)a == null) ? ((object)b == null) : ((object)b != null && a.Id == b.Id && a.Parent == b.Parent && JsonNode.DeepEquals(a.Data, b.Data));
    }

    public static Dictionary<string, ObjectState> Map(IEnumerable<ObjectState> objects)
    {
        return objects.ToDictionary((ObjectState x) => x.Id);
    }

    public static List<Change> Diff(Dictionary<string, ObjectState> before, Dictionary<string, ObjectState> after)
    {
        return (from id in before.Keys.Union(after.Keys)
            orderby id
            select new Change(id, before.GetValueOrDefault(id), after.GetValueOrDefault(id)) into x
            where !Equal(x.Before, x.After)
            select x).ToList();
    }

    public static void ValidateChange(Change c)
    {
        if ((object)c.Before == null && (object)c.After == null)
        {
            throw new InvalidOperationException("Empty change");
        }
        foreach (ObjectState item in new ObjectState[2] { c.Before, c.After }.Where((ObjectState x) => (object)x != null))
        {
            if (!Guid.TryParse(c.Id, out var _) || item.Id != c.Id || item.Data["__guid"]?.GetValue<string>() != c.Id)
            {
                throw new InvalidOperationException("Invalid object identity");
            }
            if (item.Data.Any((KeyValuePair<string, JsonNode> p) => p.Key.Contains("Prefab", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Prefab edits are not supported");
            }
        }
        if ((object)c.Before == null || (object)c.After == null)
        {
            ObjectState objectState = c.Before ?? c.After;
            if (!(objectState.Data["Components"] is JsonArray jsonArray))
            {
                return;
            }
            {
                foreach (JsonNode item2 in jsonArray)
                {
                    JsonObject jsonObject = item2.AsObject();
                    string type = jsonObject["__type"].GetValue<string>();
                    if (!ComponentFields.ContainsKey(type))
                    {
                        throw new InvalidOperationException("Creation/deletion of " + type + " is not supported");
                    }
                    if (jsonObject.Any((KeyValuePair<string, JsonNode> x) => !SafeRenderOptions(x) && (x.Value is JsonObject { Count: >0 } || (x.Value is JsonArray jsonArray4 && jsonArray4.Count > 0))))
                    {
                        throw new InvalidOperationException("Creation/deletion of components with embedded graphs or object references is not supported");
                    }
                    bool SafeRenderOptions(KeyValuePair<string, JsonNode> field)
                    {
                        return type == "Sandbox.ModelRenderer" && field.Key == "RenderOptions" && field.Value is JsonObject source && source.All((KeyValuePair<string, JsonNode> x) => new ReadOnlySpan<string>(new string[4] { "GameLayer", "OverlayLayer", "BloomLayer", "AfterUILayer" }).Contains(x.Key) && x.Value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var _));
                    }
                }
                return;
            }
        }
        foreach (string item3 in c.Before.Data.Select((KeyValuePair<string, JsonNode> x) => x.Key).Union(c.After.Data.Select((KeyValuePair<string, JsonNode> x) => x.Key)))
        {
            if (JsonNode.DeepEquals(c.Before.Data[item3], c.After.Data[item3]) || ObjectFields.Contains(item3))
            {
                continue;
            }
            if (item3 != "Components")
            {
                throw new InvalidOperationException("Unsupported GameObject property: " + item3);
            }
            JsonArray jsonArray2 = c.Before.Data[item3].AsArray();
            JsonArray jsonArray3 = c.After.Data[item3].AsArray();
            if (jsonArray2.Count != jsonArray3.Count)
            {
                throw new InvalidOperationException("Component addition/removal is not supported");
            }
            for (int num = 0; num < jsonArray2.Count; num++)
            {
                JsonObject jsonObject2 = jsonArray2[num].AsObject();
                JsonObject jsonObject3 = jsonArray3[num].AsObject();
                string value = jsonObject2["__type"].GetValue<string>();
                foreach (string item4 in jsonObject2.Select((KeyValuePair<string, JsonNode> x) => x.Key).Union(jsonObject3.Select((KeyValuePair<string, JsonNode> x) => x.Key)))
                {
                    if (!JsonNode.DeepEquals(jsonObject2[item4], jsonObject3[item4]))
                    {
                        if (!ComponentFields.TryGetValue(value, out var value2) || !value2.Contains(item4))
                        {
                            throw new InvalidOperationException("Unsupported inspector property: " + value + "." + item4);
                        }
                        if (!(jsonObject3[item4] is JsonValue))
                        {
                            throw new InvalidOperationException("Only scalar inspector values are supported");
                        }
                    }
                }
            }
        }
    }

    public static void ValidateGraph(Dictionary<string, ObjectState> objects)
    {
        HashSet<string> hashSet = new HashSet<string>(objects.Keys);
        foreach (ObjectState value in objects.Values)
        {
            if (value.Data["Components"] is JsonArray jsonArray)
            {
                foreach (JsonNode item in jsonArray)
                {
                    string text = item?["__guid"]?.GetValue<string>();
                    if (text == null || !Guid.TryParse(text, out var _) || !hashSet.Add(text))
                    {
                        throw new InvalidOperationException("Duplicate/invalid component identity");
                    }
                }
            }
            HashSet<string> hashSet2 = new HashSet<string> { value.Id };
            string parent = value.Parent;
            while (!string.IsNullOrEmpty(parent))
            {
                if (!objects.ContainsKey(parent))
                {
                    throw new InvalidOperationException("Parent does not exist");
                }
                if (!hashSet2.Add(parent))
                {
                    throw new InvalidOperationException("Parenting cycle");
                }
                parent = objects[parent].Parent;
            }
        }
    }
}

public sealed class Authority
{
    private readonly Dictionary<string, Packet> receipts = new Dictionary<string, Packet>();

    private readonly Action<Packet> persist;

    public Dictionary<string, Dictionary<string, ObjectState>> Scenes { get; } = new Dictionary<string, Dictionary<string, ObjectState>>();

    public long Revision { get; private set; }

    public Authority(Action<Packet> persist)
    {
        this.persist = persist;
    }

    public Packet Subscribe(Packet p)
    {
        if (!Scenes.ContainsKey(p.Scene))
        {
            Dictionary<string, ObjectState> dictionary = StateRules.Map(p.Objects);
            StateRules.ValidateGraph(dictionary);
            persist(new Packet
            {
                Kind = "seed",
                Scene = p.Scene,
                Objects = p.Objects
            });
            Scenes.Add(p.Scene, dictionary);
        }
        return new Packet
        {
            Kind = "snapshot",
            Scene = p.Scene,
            Objects = Scenes[p.Scene].Values.ToList(),
            Revision = Revision
        };
    }

    public Packet Apply(Packet p)
    {
        string key = p.Peer + ":" + p.Id;
        if (receipts.TryGetValue(key, out var value))
        {
            return value;
        }
        try
        {
            if (!Scenes.TryGetValue(p.Scene, out var value2))
            {
                throw new InvalidOperationException("Subscribe to scene first");
            }
            if (p.Changes == null || (p.Changes.Count == 0 && !p.Final))
            {
                throw new InvalidOperationException("Empty operation");
            }
            if (p.Changes.Select((Change x) => x.Id).Distinct().Count() != p.Changes.Count)
            {
                throw new InvalidOperationException("Duplicate object in operation");
            }
            Dictionary<string, ObjectState> dictionary = new Dictionary<string, ObjectState>(value2);
            foreach (Change change in p.Changes)
            {
                StateRules.ValidateChange(change);
                if (!StateRules.Equal(value2.GetValueOrDefault(change.Id), change.Before))
                {
                    throw new InvalidOperationException("Conflict on " + change.Id + ": first accepted edit wins. Your edit is retained in recovery.");
                }
                if ((object)change.After == null)
                {
                    dictionary.Remove(change.Id);
                }
                else
                {
                    dictionary[change.Id] = change.After;
                }
            }
            StateRules.ValidateGraph(dictionary);
            value = new Packet
            {
                Kind = "accepted",
                Id = p.Id,
                Peer = p.Peer,
                Scene = p.Scene,
                Changes = p.Changes,
                Revision = Revision + 1,
                Final = p.Final
            };
            persist(value);
            Revision++;
            Scenes[p.Scene] = dictionary;
        }
        catch (Exception ex)
        {
            value = new Packet
            {
                Kind = "reject",
                Id = p.Id,
                Scene = p.Scene,
                Error = ex.Message
            };
        }
        receipts[key] = value;
        return value;
    }
}
