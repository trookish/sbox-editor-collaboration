using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeamCreate;

public static class RuntimeStateRules
{
    public const int MaxRuntimeRunIdLength = 128;

    private static readonly HashSet<string> GameObjectFields = new HashSet<string>(StringComparer.Ordinal) { "__guid", "Name", "Position", "Rotation", "Scale", "Enabled", "Tags", "Components" };

    private static readonly Dictionary<string, HashSet<string>> ComponentFields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
    {
        ["Sandbox.ModelRenderer"] = new HashSet<string>(StringComparer.Ordinal) { "Tint" },
        ["Sandbox.CameraComponent"] = new HashSet<string>(StringComparer.Ordinal) { "FieldOfView", "Orthographic", "OrthographicHeight", "ZNear", "ZFar", "BackgroundColor" },
        ["Sandbox.DirectionalLight"] = new HashSet<string>(StringComparer.Ordinal) { "LightColor", "SkyColor", "Shadows" },
        ["Sandbox.PointLight"] = new HashSet<string>(StringComparer.Ordinal) { "LightColor", "Radius", "Shadows" },
        ["Sandbox.BoxCollider"] = new HashSet<string>(StringComparer.Ordinal) { "Center", "Scale", "IsTrigger" }
    };

    public static bool IsValidRunId(string runId)
    {
        return !string.IsNullOrWhiteSpace(runId) && runId.Length <= 128 && runId == runId.Trim() && !runId.Any(char.IsControl);
    }

    public static void ValidateObject(ObjectState state)
    {
        if ((object)state == null)
        {
            throw InvalidIdentity("object is missing");
        }
        if (!string.IsNullOrEmpty(state.Parent) && !TryGetCanonicalId(state.Parent, out var canonicalId))
        {
            throw InvalidIdentity("parent id " + state.Parent + " is not a GUID");
        }
        if (!TryGetCanonicalId(state.Id, out var canonicalId2) || state.Data == null || !TryGetString(state.Data, "__guid", out var value) || !TryGetCanonicalId(value, out var canonicalId3) || !string.Equals(canonicalId2, canonicalId3, StringComparison.Ordinal))
        {
            throw InvalidIdentity(state.Id + " does not match its serialized __guid");
        }
        if (state.Data.Any((KeyValuePair<string, JsonNode> field) => field.Key.Contains("Prefab", StringComparison.OrdinalIgnoreCase)))
        {
            throw NotAllowlisted("GameObject field '" + FindPrefabField(state.Data) + "' is prefab data");
        }
        bool value2;
        foreach (KeyValuePair<string, JsonNode> datum in state.Data)
        {
            if (!GameObjectFields.Contains(datum.Key))
            {
                throw NotAllowlisted("GameObject field '" + datum.Key + "'");
            }
            switch (datum.Key)
            {
            case "__guid":
            case "Name":
            case "Components":
                value2 = true;
                break;
            default:
                value2 = false;
                break;
            }
            if (!value2 && !IsScalar(datum.Value))
            {
                throw NotAllowlisted("GameObject field '" + datum.Key + "' is not a scalar JsonValue");
            }
        }
        if (!TryGetString(state.Data, "Name", out canonicalId))
        {
            throw InvalidShape("GameObject field 'Name' must be a non-null JSON string");
        }
        if (state.Data.TryGetPropertyValue("Enabled", out JsonNode jsonNode) && (!(jsonNode is JsonValue jsonValue) || !jsonValue.TryGetValue<bool>(out value2)))
        {
            throw InvalidShape("GameObject field 'Enabled' must be a JSON boolean");
        }
        if (state.Data.TryGetPropertyValue("Tags", out JsonNode _) && !TryGetString(state.Data, "Tags", out canonicalId))
        {
            throw InvalidShape("GameObject field 'Tags' must be a JSON string");
        }
        if (!(state.Data["Components"] is JsonArray jsonArray))
        {
            throw InvalidShape("GameObject field 'Components' must be a JSON array");
        }
        HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode item in jsonArray)
        {
            if (!(item is JsonObject jsonObject))
            {
                throw InvalidShape("Components entries must be JSON objects");
            }
            if (!TryGetString(jsonObject, "__type", out var value3) || string.IsNullOrWhiteSpace(value3))
            {
                throw InvalidShape("Component field '__type' must be a non-empty JSON string");
            }
            if (!ComponentFields.TryGetValue(value3, out var value4))
            {
                throw new InvalidOperationException("Runtime component type is not allowlisted: " + value3);
            }
            if (!TryGetString(jsonObject, "__guid", out var value5) || !TryGetCanonicalId(value5, out var canonicalId4))
            {
                throw InvalidIdentity("component " + value3 + " has an invalid __guid");
            }
            if (string.Equals(canonicalId4, canonicalId2, StringComparison.Ordinal))
            {
                throw InvalidIdentity("component " + value3 + " reuses its object __guid " + value5);
            }
            if (!hashSet.Add(canonicalId4))
            {
                throw InvalidIdentity("component " + value3 + " duplicates __guid " + value5);
            }
            foreach (KeyValuePair<string, JsonNode> item2 in jsonObject)
            {
                canonicalId = item2.Key;
                if ((!(canonicalId == "__type") && !(canonicalId == "__guid")) || 1 == 0)
                {
                    if (item2.Key.Contains("Prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        throw NotAllowlisted($"component field '{value3}.{item2.Key}' is prefab data");
                    }
                    if (!value4.Contains(item2.Key))
                    {
                        throw NotAllowlisted($"component field '{value3}.{item2.Key}'");
                    }
                    if (!IsScalar(item2.Value))
                    {
                        throw NotAllowlisted($"component field '{value3}.{item2.Key}' is not a scalar JsonValue");
                    }
                }
            }
        }
    }

    public static void ValidateSnapshot(string scenePath, IReadOnlyDictionary<string, ObjectState> objects)
    {
        if (!IsSafeScenePath(scenePath))
        {
            throw new InvalidOperationException("Runtime scene path is invalid: " + (scenePath ?? "<null>"));
        }
        if (objects == null)
        {
            throw InvalidIdentity("snapshot is missing");
        }
        HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> hashSet2 = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, ObjectState> dictionary = new Dictionary<string, ObjectState>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ObjectState> @object in objects)
        {
            if ((object)@object.Value == null)
            {
                throw InvalidIdentity("snapshot contains missing object id " + @object.Key);
            }
            ValidateObject(@object.Value);
            if (!TryGetCanonicalId(@object.Key, out var canonicalId) || !TryGetCanonicalId(@object.Value.Id, out var canonicalId2) || !string.Equals(canonicalId, canonicalId2, StringComparison.Ordinal))
            {
                throw InvalidIdentity("snapshot key " + @object.Key + " does not match object id " + @object.Value.Id);
            }
            if (!hashSet.Add(canonicalId))
            {
                throw InvalidIdentity("snapshot contains duplicate object id " + @object.Key);
            }
            if (!hashSet2.Add(canonicalId2))
            {
                throw InvalidIdentity("snapshot contains duplicate object id " + @object.Key);
            }
            if (!dictionary.TryAdd(canonicalId2, @object.Value))
            {
                throw InvalidIdentity("snapshot contains duplicate object id " + @object.Key);
            }
            JsonArray jsonArray = @object.Value.Data["Components"].AsArray();
            foreach (JsonNode item in jsonArray)
            {
                string value = item["__guid"].GetValue<string>();
                if (!TryGetCanonicalId(value, out var canonicalId3))
                {
                    throw InvalidIdentity("component " + value + " is not a GUID");
                }
                if (!hashSet2.Add(canonicalId3))
                {
                    throw InvalidIdentity("snapshot contains duplicate object/component id " + value);
                }
            }
        }
        foreach (KeyValuePair<string, ObjectState> object2 in objects)
        {
            if (!TryGetCanonicalId(object2.Key, out var canonicalId4))
            {
                throw InvalidIdentity("snapshot key " + object2.Key + " is not a GUID");
            }
            HashSet<string> hashSet3 = new HashSet<string>(StringComparer.Ordinal) { canonicalId4 };
            string parent = object2.Value.Parent;
            while (!string.IsNullOrEmpty(parent))
            {
                if (!TryGetCanonicalId(parent, out var canonicalId5) || !dictionary.TryGetValue(canonicalId5, out var value2))
                {
                    throw new InvalidOperationException("Runtime parent does not exist: " + parent);
                }
                if ((object)value2 == null)
                {
                    throw InvalidIdentity("Runtime parent is missing: " + parent);
                }
                if (!hashSet3.Add(canonicalId5))
                {
                    throw new InvalidOperationException("Runtime parent cycle detected at " + parent);
                }
                parent = value2.Parent;
            }
        }
    }

    public static bool IsAllowedComponentType(string type)
    {
        return ComponentFields.ContainsKey(type);
    }

    public static bool IsAllowedComponentField(string type, string field)
    {
        HashSet<string> value;
        return ComponentFields.TryGetValue(type, out value) && value.Contains(field);
    }

    public static bool IsSafeScenePath(string scenePath)
    {
        if (string.IsNullOrWhiteSpace(scenePath) || !scenePath.StartsWith("scenes/", StringComparison.Ordinal) || scenePath.Contains('\\') || scenePath.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(scenePath) || scenePath.Any(char.IsControl))
        {
            return false;
        }
        string[] source = scenePath.Split('/');
        if (source.Any((string segment) =>
        {
            bool flag = segment.Length == 0;
            bool flag2 = flag;
            if (!flag2)
            {
                bool flag3 = ((segment == "." || segment == "..") ? true : false);
                flag2 = flag3;
            }
            return flag2;
        }))
        {
            return false;
        }
        return scenePath.EndsWith(".scene", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetCanonicalId(string value, out string canonicalId)
    {
        if (Guid.TryParse(value, out var result))
        {
            canonicalId = result.ToString("D");
            return true;
        }
        canonicalId = null;
        return false;
    }

    private static string FindPrefabField(JsonObject data)
    {
        return data.First((KeyValuePair<string, JsonNode> field) => field.Key.Contains("Prefab", StringComparison.OrdinalIgnoreCase)).Key;
    }

    private static bool IsScalar(JsonNode node)
    {
        JsonValue jsonValue = node as JsonValue;
        bool flag = jsonValue != null;
        bool flag2 = flag;
        if (flag2)
        {
            JsonValueKind valueKind = jsonValue.GetValueKind();
            bool flag3 = valueKind - 3 <= JsonValueKind.Number;
            flag2 = flag3;
        }
        return flag2;
    }

    private static bool TryGetString(JsonObject data, string key, out string value)
    {
        value = null;
        JsonNode jsonNode;
        return data.TryGetPropertyValue(key, out jsonNode) && jsonNode is JsonValue jsonValue && jsonValue.TryGetValue<string>(out value);
    }

    private static InvalidOperationException NotAllowlisted(string detail)
    {
        return new InvalidOperationException("Runtime state is not allowlisted: " + detail);
    }

    private static InvalidOperationException InvalidIdentity(string detail)
    {
        return new InvalidOperationException("Runtime object identity is invalid: " + detail);
    }

    private static InvalidOperationException InvalidShape(string detail)
    {
        return new InvalidOperationException("Runtime state shape is invalid: " + detail);
    }
}

public enum RuntimeSessionKind
{
    Source,
    Game
}
