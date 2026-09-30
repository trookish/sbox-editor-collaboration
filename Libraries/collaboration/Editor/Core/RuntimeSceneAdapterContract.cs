using System;
using System.Text.Json.Nodes;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TeamCreate;

public sealed record RuntimeSessionDescriptor
{
    public RuntimeSessionKind Kind { get; init; }

    public object SessionToken { get; init; }

    public object ParentSessionToken { get; init; }

    public bool IsValid { get; init; }

    public bool IsDestroyed { get; init; }

    public string SourceScenePath { get; init; }
}

public static class RuntimeSessionGuard
{
    public static RuntimeSessionDescriptor EnsureRuntimeSession(RuntimeSessionDescriptor session, object expectedSourceSession, string expectedSourcePath)
    {
        if ((object)session == null || session.SessionToken == null || !session.IsValid || session.IsDestroyed)
        {
            throw new InvalidOperationException("Runtime session is missing or destroyed.");
        }
        if (session.Kind != RuntimeSessionKind.Game)
        {
            throw new InvalidOperationException("Runtime scene adapter accepts only GameEditorSession.");
        }
        if (expectedSourceSession == null)
        {
            throw new InvalidOperationException("Expected source session is missing.");
        }
        if (!RuntimeStateRules.IsSafeScenePath(expectedSourcePath))
        {
            throw new InvalidOperationException("Configured source path is not a safe scenes/... path.");
        }
        if (!RuntimeStateRules.IsSafeScenePath(session.SourceScenePath))
        {
            throw new InvalidOperationException("Runtime session source path is not a safe scenes/... path.");
        }
        if (session.ParentSessionToken != expectedSourceSession)
        {
            throw new InvalidOperationException("Runtime session parent is not the expected source session.");
        }
        if (!string.Equals(CanonicalPath(session.SourceScenePath), CanonicalPath(expectedSourcePath), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Runtime session source path does not match the configured source path.");
        }
        return session;
    }

    private static string CanonicalPath(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant();
    }
}

public static class RuntimeSceneCapture
{
    public static ObjectState CaptureSerializedObject(JsonNode serialized, string parent)
    {
        if (serialized == null)
        {
            throw new InvalidOperationException("Runtime state shape is invalid: serialized object is missing.");
        }
        return RuntimeSceneSerialization.ProjectObject(serialized, parent);
    }

    public static void EnsureRuntimeObjectEntry(object runtimeEntry, bool isRoot)
    {
        if (runtimeEntry == null)
        {
            throw new InvalidOperationException("Runtime state shape is invalid: runtime object entry is null (" + (isRoot ? "root" : "child") + ").");
        }
    }

    public static ObjectState CaptureObject(object runtimeEntry, Guid expectedRuntimeId, JsonNode serialized, string parent, bool isRoot)
    {
        EnsureRuntimeObjectEntry(runtimeEntry, isRoot);
        if (expectedRuntimeId == Guid.Empty)
        {
            throw new InvalidOperationException("Runtime object identity is invalid: runtime object id is missing or invalid.");
        }
        ObjectState objectState = CaptureSerializedObject(serialized, parent);
        if (!string.Equals(expectedRuntimeId.ToString("D"), objectState.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Runtime object identity is invalid: runtime object id {expectedRuntimeId:D} does not match serialized __guid.");
        }
        return objectState;
    }
}

public static class RuntimeSceneSerialization
{
    public static ObjectState ProjectObject(JsonNode serialized, string parent)
    {
        if (serialized == null)
        {
            throw new InvalidOperationException("Runtime state shape is invalid: serialized object is missing.");
        }
        JsonNode jsonNode;
        try
        {
            jsonNode = JsonNode.Parse(serialized.ToJsonString());
        }
        catch (Exception innerException)
        {
            throw new InvalidOperationException("Runtime state shape is invalid: typed JSON could not be normalized.", innerException);
        }
        if (!(jsonNode is JsonObject jsonObject))
        {
            throw new InvalidOperationException("Runtime state shape is invalid: normalized serialization must be a JSON object.");
        }
        if (jsonObject.Any((KeyValuePair<string, JsonNode> field) => field.Key.Contains("Prefab", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Runtime state is not allowlisted: prefab data");
        }
        JsonObject jsonObject2 = new JsonObject();
        if (!TryGetString(jsonObject, "__guid", out var value) || !Guid.TryParse(value, out var result))
        {
            throw new InvalidOperationException("Runtime object identity is invalid: serialized object __guid is missing or invalid.");
        }
        jsonObject2["__guid"] = result.ToString("D");
        string[] array = new string[6] { "Name", "Position", "Rotation", "Scale", "Enabled", "Tags" };
        foreach (string propertyName in array)
        {
            if (jsonObject.TryGetPropertyValue(propertyName, out JsonNode jsonNode2))
            {
                jsonObject2[propertyName] = jsonNode2?.DeepClone();
            }
        }
        if (jsonObject.TryGetPropertyValue("Components", out JsonNode jsonNode3))
        {
            if (!(jsonNode3 is JsonArray jsonArray))
            {
                throw new InvalidOperationException("Runtime state shape is invalid: GameObject field 'Components' must be a JSON array");
            }
            JsonArray jsonArray2 = new JsonArray();
            foreach (JsonNode item in jsonArray)
            {
                if (!(item is JsonObject jsonObject3))
                {
                    throw new InvalidOperationException("Runtime state shape is invalid: Components entries must be JSON objects");
                }
                if (jsonObject3.Any((KeyValuePair<string, JsonNode> field) => field.Key.Contains("Prefab", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("Runtime state is not allowlisted: prefab data");
                }
                if (!TryGetString(jsonObject3, "__type", out var value2) || !RuntimeStateRules.IsAllowedComponentType(value2))
                {
                    throw new InvalidOperationException("Runtime component type is not allowlisted: " + value2);
                }
                if (!TryGetString(jsonObject3, "__guid", out var value3) || !Guid.TryParse(value3, out var result2))
                {
                    throw new InvalidOperationException("Runtime object identity is invalid: component __guid is missing or invalid");
                }
                JsonObject jsonObject4 = new JsonObject
                {
                    ["__type"] = value2,
                    ["__guid"] = result2.ToString("D")
                };
                foreach (KeyValuePair<string, JsonNode> item2 in jsonObject3)
                {
                    string key = item2.Key;
                    bool flag = ((key == "__type" || key == "__guid") ? true : false);
                    if (!flag && RuntimeStateRules.IsAllowedComponentField(value2, item2.Key))
                    {
                        jsonObject4[item2.Key] = item2.Value?.DeepClone();
                    }
                }
                jsonArray2.Add(jsonObject4);
            }
            jsonObject2["Components"] = jsonArray2;
        }
        ObjectState objectState = new ObjectState(result.ToString("D"), parent, jsonObject2);
        RuntimeStateRules.ValidateObject(objectState);
        return objectState;
    }

    private static bool TryGetString(JsonObject data, string key, out string value)
    {
        value = null;
        JsonNode jsonNode;
        return data.TryGetPropertyValue(key, out jsonNode) && jsonNode is JsonValue jsonValue && jsonValue.TryGetValue<string>(out value);
    }
}

public enum RuntimeSceneOperationKind
{
    Create,
    Update,
    Delete
}

public enum RuntimeSceneApplyStatus
{
    Unsupported,
    Ready
}

public sealed record RuntimeSceneOperation(string Id, RuntimeSceneOperationKind Kind, ObjectState Before, ObjectState After);

public sealed class RuntimeSceneApplyPlan
{
    public string ScenePath { get; }

    public RuntimeSceneApplyStatus Status { get; }

    public bool MutationSupported => Status == RuntimeSceneApplyStatus.Ready;

    public IReadOnlyList<RuntimeSceneOperation> Operations { get; }

    public string Reason { get; }

    public RuntimeSceneApplyPlan(string scenePath, RuntimeSceneApplyStatus status, IEnumerable<RuntimeSceneOperation> operations, string reason)
    {
        ScenePath = scenePath;
        Status = status;
        Operations = operations?.ToArray() ?? Array.Empty<RuntimeSceneOperation>();
        Reason = reason;
    }
}

public static class RuntimeSceneApplyPlanner
{
    public const string UnsupportedReason = "Runtime scene mutation is disabled until a tested runtime undo/apply strategy is proven.";

    public static RuntimeSceneApplyPlan Plan(string scenePath, IReadOnlyDictionary<string, ObjectState> before, IReadOnlyDictionary<string, ObjectState> target)
    {
        Dictionary<string, ObjectState> current = ValidateAndCanonicalize(scenePath, before);
        Dictionary<string, ObjectState> desired = ValidateAndCanonicalize(scenePath, target);
        RuntimeSceneOperation[] operations = (from operation in current.Keys.Union(desired.Keys, StringComparer.Ordinal).OrderBy((string id) => id, StringComparer.Ordinal).Select((string id) =>
            {
                current.TryGetValue(id, out var value);
                desired.TryGetValue(id, out var value2);
                RuntimeSceneOperationKind kind = (((object)value != null) ? (((object)value2 != null) ? RuntimeSceneOperationKind.Update : RuntimeSceneOperationKind.Delete) : RuntimeSceneOperationKind.Create);
                return new RuntimeSceneOperation(id, kind, Clone(value), Clone(value2));
            })
            where operation.Kind != RuntimeSceneOperationKind.Update || !StateRules.Equal(operation.Before, operation.After)
            select operation).ToArray();
        return new RuntimeSceneApplyPlan(scenePath, RuntimeSceneApplyStatus.Unsupported, operations, "Runtime scene mutation is disabled until a tested runtime undo/apply strategy is proven.");
    }

    private static Dictionary<string, ObjectState> ValidateAndCanonicalize(string scenePath, IReadOnlyDictionary<string, ObjectState> objects)
    {
        RuntimeStateRules.ValidateSnapshot(scenePath, objects);
        Dictionary<string, ObjectState> dictionary = new Dictionary<string, ObjectState>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, ObjectState> @object in objects)
        {
            string text = Guid.Parse(@object.Key).ToString("D");
            string parent = (string.IsNullOrEmpty(@object.Value.Parent) ? @object.Value.Parent : Guid.Parse(@object.Value.Parent).ToString("D"));
            JsonObject jsonObject = (JsonObject)@object.Value.Data.DeepClone();
            jsonObject["__guid"] = text;
            if (jsonObject["Components"] is JsonArray jsonArray)
            {
                foreach (JsonNode item in jsonArray)
                {
                    if (item is JsonObject jsonObject2 && Guid.TryParse(jsonObject2["__guid"]?.GetValue<string>(), out var result))
                    {
                        jsonObject2["__guid"] = result.ToString("D");
                    }
                }
            }
            if (!dictionary.TryAdd(text, new ObjectState(text, parent, jsonObject)))
            {
                throw new InvalidOperationException("Runtime object identity is invalid: snapshot contains equivalent duplicate object id " + text);
            }
        }
        RuntimeStateRules.ValidateSnapshot(scenePath, dictionary);
        return dictionary;
    }

    private static ObjectState Clone(ObjectState state)
    {
        return ((object)state == null) ? null : state with
        {
            Data = (JsonObject)state.Data.DeepClone()
        };
    }
}

public sealed class RuntimeSceneTargetOnlyBoundary
{
    private readonly SourceSessionCallCounter sourceCalls;

    private int planCalls;

    public static RuntimeSceneTargetOnlyBoundary Shared { get; } = new RuntimeSceneTargetOnlyBoundary();

    public int SourceCalls => sourceCalls.TotalCalls;

    public int PlanCalls => Volatile.Read(in planCalls);

    public RuntimeSceneTargetOnlyBoundary(SourceSessionCallCounter sourceCalls)
    {
        this.sourceCalls = sourceCalls ?? throw new ArgumentNullException("sourceCalls");
    }

    public RuntimeSceneTargetOnlyBoundary()
        : this(new SourceSessionCallCounter())
    {
    }

    public RuntimeSceneApplyPlan Plan(string scenePath, IReadOnlyDictionary<string, ObjectState> before, IReadOnlyDictionary<string, ObjectState> target)
    {
        Interlocked.Increment(ref planCalls);
        return RuntimeSceneApplyPlanner.Plan(scenePath, before, target);
    }

    public RuntimeSceneApplyPlan PlanForAdapter(string scenePath, IReadOnlyDictionary<string, ObjectState> before, IReadOnlyDictionary<string, ObjectState> target)
    {
        return Plan(scenePath, before, target);
    }
}

public sealed class SourceSessionCallCounter
{
    public int SaveCalls { get; private set; }

    public int ReloadCalls { get; private set; }

    public int DirtyCalls { get; private set; }

    public int UndoCalls { get; private set; }

    public int TotalCalls => SaveCalls + ReloadCalls + DirtyCalls + UndoCalls;

    public void RecordSave()
    {
        SaveCalls++;
    }

    public void RecordReload()
    {
        ReloadCalls++;
    }

    public void RecordDirty()
    {
        DirtyCalls++;
    }

    public void RecordUndo()
    {
        UndoCalls++;
    }
}
