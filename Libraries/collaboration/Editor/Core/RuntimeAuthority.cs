using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TeamCreate;

public static class RuntimeAuthorityContract
{
    public const string OperationKind = "runtime-operation";

    public const string SnapshotKind = "snapshot";

    public const string EditKind = "edit";
}

public sealed class RuntimeAuthority
{
    private sealed class Namespace
    {
        public string RunId;

        public string ScenePath;

        public Dictionary<string, ObjectState> State = new Dictionary<string, ObjectState>(StringComparer.Ordinal);

        public Dictionary<OperationIdentity, Packet> Receipts = new Dictionary<OperationIdentity, Packet>();

        public long Revision;

        public long LastSequence;

        public long LastTick = -1L;
    }

    private readonly record struct OperationIdentity(string Peer, string Id);

    private sealed record ValidatedOperation(int? ProtocolVersion, CollaborationMode? Mode, string Id, string Peer, string ScenePath, string RunId, long Sequence, long Tick, string OperationKind);

    private readonly Dictionary<string, Namespace> namespaces = new Dictionary<string, Namespace>(StringComparer.Ordinal);

    private readonly Dictionary<string, string> runScenes = new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly Action<Packet> journal;

    private bool journalInProgress;

    public RuntimeAuthority(Action<Packet> journal)
    {
        this.journal = journal ?? throw new ArgumentNullException("journal");
    }

    public Packet Apply(Packet operation)
    {
        if (journalInProgress)
        {
            return Reject(operation, CurrentRevision(operation?.RuntimeRunId), "Runtime authority rejects re-entrant Apply while journal persistence is in progress.");
        }
        if (!TryValidateEnvelope(operation, out var error))
        {
            return Reject(operation, CurrentRevision(operation?.RuntimeRunId), error);
        }
        ValidatedOperation validatedOperation = CaptureValidatedOperation(operation);
        if (runScenes.TryGetValue(validatedOperation.RunId, out var value) && !string.Equals(value, validatedOperation.ScenePath, StringComparison.Ordinal))
        {
            return Reject(CreateCanonicalPacket(validatedOperation, operation), CurrentRevision(validatedOperation.RunId), $"Runtime run '{validatedOperation.RunId}' is bound to scene '{value}', not '{validatedOperation.ScenePath}'.");
        }
        string key = MakeNamespaceKey(validatedOperation.RunId, validatedOperation.ScenePath);
        namespaces.TryGetValue(key, out var value2);
        if (value2 != null)
        {
            OperationIdentity key2 = new OperationIdentity(validatedOperation.Peer, validatedOperation.Id);
            if (value2.Receipts.TryGetValue(key2, out var value3))
            {
                try
                {
                    return CloneReceipt(value3);
                }
                catch (Exception ex)
                {
                    return Reject(CreateCanonicalPacket(validatedOperation, operation), value2.Revision, "Runtime accepted receipt could not be copied: " + ex.Message);
                }
            }
            if (validatedOperation.Sequence <= value2.LastSequence)
            {
                return Reject(CreateCanonicalPacket(validatedOperation, operation), value2.Revision, $"Runtime sequence {validatedOperation.Sequence} is stale or duplicate; last accepted sequence is {value2.LastSequence}.");
            }
            if (validatedOperation.Tick < value2.LastTick)
            {
                return Reject(CreateCanonicalPacket(validatedOperation, operation), value2.Revision, $"Runtime tick {validatedOperation.Tick} is stale; last accepted tick is {value2.LastTick}.");
            }
        }
        else if (!string.Equals(validatedOperation.OperationKind, "snapshot", StringComparison.Ordinal))
        {
            return Reject(CreateCanonicalPacket(validatedOperation, operation), 0L, "Runtime run '" + validatedOperation.RunId + "' has no authority namespace; begin with a complete snapshot operation.");
        }
        Dictionary<string, ObjectState> next;
        if (string.Equals(validatedOperation.OperationKind, "snapshot", StringComparison.Ordinal))
        {
            if (!TryBuildSnapshot(validatedOperation.ScenePath, operation.Objects, out next, out error))
            {
                return Reject(CreateCanonicalPacket(validatedOperation, operation), value2?.Revision ?? 0, error);
            }
        }
        else if (!TryBuildChanges(value2, operation.Changes, out next, out error))
        {
            return Reject(CreateCanonicalPacket(validatedOperation, operation), value2?.Revision ?? 0, error);
        }
        long revision = (value2?.Revision ?? 0) + 1;
        Packet packet;
        try
        {
            List<Change> changes = ((validatedOperation.OperationKind == "edit") ? operation.Changes.Select((Change change) => new Change(change?.Id, CloneIfPresent(change?.Before), CloneIfPresent(change?.After))).ToList() : null);
            Packet operation2 = CreateCanonicalPacket(validatedOperation, operation);
            packet = CreateReceipt(operation2, "accepted", revision, null, changes);
        }
        catch (Exception ex2)
        {
            return Reject(CreateCanonicalPacket(validatedOperation, operation), value2?.Revision ?? 0, "Runtime operation payload could not be copied for an accepted receipt: " + ex2.Message);
        }
        Packet packet2;
        try
        {
            packet2 = CloneReceipt(packet);
        }
        catch (Exception ex3)
        {
            return Reject(packet, value2?.Revision ?? 0, "Runtime accepted receipt could not be copied: " + ex3.Message);
        }
        journalInProgress = true;
        try
        {
            Packet packet3 = CloneReceipt(packet2);
            if (packet3.Objects == null && packet.Objects != null)
            {
                throw new InvalidOperationException("Runtime accepted receipt could not be isolated for journaling.");
            }
            if (packet3.Changes == null && packet.Changes != null)
            {
                throw new InvalidOperationException("Runtime accepted receipt could not be isolated for journaling.");
            }
            journal(packet3);
        }
        catch (Exception ex4)
        {
            return Reject(packet, value2?.Revision ?? 0, "Runtime journal persistence failed: " + ex4.Message);
        }
        finally
        {
            journalInProgress = false;
        }
        if (value2 == null)
        {
            value2 = new Namespace
            {
                RunId = validatedOperation.RunId,
                ScenePath = validatedOperation.ScenePath
            };
            namespaces.Add(key, value2);
            runScenes.Add(validatedOperation.RunId, validatedOperation.ScenePath);
        }
        value2.State = next;
        value2.Revision = revision;
        value2.LastSequence = validatedOperation.Sequence;
        value2.LastTick = validatedOperation.Tick;
        value2.Receipts.Add(new OperationIdentity(validatedOperation.Peer, validatedOperation.Id), packet2);
        return packet;
    }

    public IReadOnlyDictionary<string, ObjectState> State(string runId, string scenePath)
    {
        if (!TryGetNamespace(runId, scenePath, out var current))
        {
            throw MissingNamespace(runId, scenePath);
        }
        return current.State.Values.ToDictionary((ObjectState state) => state.Id, (ObjectState state) => Clone(state), StringComparer.Ordinal);
    }

    public long Revision(string runId, string scenePath)
    {
        if (!TryGetNamespace(runId, scenePath, out var current))
        {
            throw MissingNamespace(runId, scenePath);
        }
        return current.Revision;
    }

    public long LastSequence(string runId, string scenePath)
    {
        if (!TryGetNamespace(runId, scenePath, out var current))
        {
            throw MissingNamespace(runId, scenePath);
        }
        return current.LastSequence;
    }

    public long LastTick(string runId, string scenePath)
    {
        if (!TryGetNamespace(runId, scenePath, out var current))
        {
            throw MissingNamespace(runId, scenePath);
        }
        return current.LastTick;
    }

    public int ReceiptCount(string runId, string scenePath)
    {
        if (!TryGetNamespace(runId, scenePath, out var current))
        {
            throw MissingNamespace(runId, scenePath);
        }
        return current.Receipts.Count;
    }

    private bool TryBuildSnapshot(string scenePath, IEnumerable<ObjectState> objects, out Dictionary<string, ObjectState> next, out string error)
    {
        next = null;
        error = null;
        if (objects == null)
        {
            error = "Runtime snapshot operation requires a complete Objects list.";
            return false;
        }
        Dictionary<string, ObjectState> dictionary = new Dictionary<string, ObjectState>(StringComparer.Ordinal);
        try
        {
            foreach (ObjectState @object in objects)
            {
                RuntimeStateRules.ValidateObject(@object);
                ObjectState objectState = Normalize(@object);
                if (!dictionary.TryAdd(objectState.Id, objectState))
                {
                    throw new InvalidOperationException("Runtime object identity is invalid: snapshot duplicates object " + objectState.Id);
                }
            }
            RuntimeStateRules.ValidateSnapshot(scenePath, dictionary);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        next = dictionary;
        return true;
    }

    private bool TryBuildChanges(Namespace current, IEnumerable<Change> changes, out Dictionary<string, ObjectState> next, out string error)
    {
        next = null;
        error = null;
        if (changes == null || !changes.Any())
        {
            error = "Runtime edit operation requires at least one change.";
            return false;
        }
        Dictionary<string, ObjectState> dictionary = current.State.Values.ToDictionary((ObjectState state) => state.Id, Clone, StringComparer.Ordinal);
        HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (Change change in changes)
            {
                if ((object)change == null || !Guid.TryParse(change.Id, out var result))
                {
                    throw new InvalidOperationException("Runtime object identity is invalid: change id must be a GUID.");
                }
                if (!hashSet.Add(result.ToString("D")))
                {
                    throw new InvalidOperationException($"Runtime operation changes object {result:D} more than once.");
                }
                foreach (ObjectState item in new ObjectState[2] { change.Before, change.After }.Where((ObjectState state) => (object)state != null))
                {
                    RuntimeStateRules.ValidateObject(item);
                    if (!Guid.TryParse(item.Id, out var result2) || result2 != result)
                    {
                        throw new InvalidOperationException($"Runtime object identity is invalid: change {result:D} does not match object {item.Id}.");
                    }
                }
                string text = result.ToString("D");
                current.State.TryGetValue(text, out var value);
                if (!RuntimeEqual(value, change.Before))
                {
                    throw new InvalidOperationException("Runtime conflict on " + text + ": first accepted edit wins; reconcile and retry this object.");
                }
                if ((object)change.After == null)
                {
                    dictionary.Remove(text);
                }
                else
                {
                    dictionary[text] = Normalize(change.After);
                }
            }
            RuntimeStateRules.ValidateSnapshot(current.ScenePath, dictionary);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        next = dictionary;
        return true;
    }

    private static Packet CreateCanonicalPacket(ValidatedOperation validated, Packet operation)
    {
        return new Packet
        {
            ProtocolVersion = validated.ProtocolVersion,
            Mode = validated.Mode,
            Id = validated.Id,
            Peer = validated.Peer,
            Scene = validated.ScenePath,
            RuntimeRunId = validated.RunId,
            RuntimeSequence = validated.Sequence,
            RuntimeTick = validated.Tick,
            RuntimeOperationKind = validated.OperationKind,
            Objects = operation.Objects,
            Changes = operation.Changes
        };
    }

    private static ValidatedOperation CaptureValidatedOperation(Packet operation)
    {
        return new ValidatedOperation(operation.ProtocolVersion, operation.Mode, operation.Id, operation.Peer, operation.Scene, operation.RuntimeRunId, operation.RuntimeSequence.Value, operation.RuntimeTick.Value, operation.RuntimeOperationKind);
    }

    private static bool TryValidateEnvelope(Packet operation, out string error)
    {
        if (operation == null)
        {
            error = "Runtime operation is missing.";
            return false;
        }
        if (!string.Equals(operation.Kind, "runtime-operation", StringComparison.Ordinal))
        {
            error = "Runtime authority accepts only explicit runtime-operation envelopes.";
            return false;
        }
        if (operation.ProtocolVersion != 4)
        {
            error = $"Runtime operation requires protocol version {4} ({"teamcreate-4"}).";
            return false;
        }
        if (operation.Mode != CollaborationMode.RuntimeExperimental)
        {
            error = "Runtime operation requires experimental runtime collaboration mode.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(operation.Id))
        {
            error = "Runtime operation requires a non-empty operation ID.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(operation.Peer))
        {
            error = "Runtime operation requires a non-empty peer identity.";
            return false;
        }
        if (!RuntimeStateRules.IsValidRunId(operation.RuntimeRunId))
        {
            error = "Runtime operation requires a valid runtime run ID.";
            return false;
        }
        if (!RuntimeStateRules.IsSafeScenePath(operation.Scene))
        {
            error = "Runtime scene path is invalid: " + (operation.Scene ?? "<null>");
            return false;
        }
        if (!operation.RuntimeSequence.HasValue || operation.RuntimeSequence.Value <= 0)
        {
            error = "Runtime operation requires a positive sequence number.";
            return false;
        }
        if (!operation.RuntimeTick.HasValue || operation.RuntimeTick.Value < 0)
        {
            error = "Runtime operation requires a non-negative tick.";
            return false;
        }
        string runtimeOperationKind = operation.RuntimeOperationKind;
        if ((!(runtimeOperationKind == "snapshot") && !(runtimeOperationKind == "edit")) || 1 == 0)
        {
            error = "Runtime operation kind must be 'snapshot' or 'edit'.";
            return false;
        }
        error = null;
        return true;
    }

    private static Packet Reject(Packet operation, long revision, string error)
    {
        try
        {
            return CreateReceipt(operation, "reject", revision, error);
        }
        catch (Exception ex)
        {
            string text = (string.IsNullOrWhiteSpace(error) ? "Runtime operation payload is malformed." : error);
            return CreateMinimalReceipt(operation, "reject", revision, text + " Payload could not be copied for the reject receipt: " + ex.Message);
        }
    }

    private static Packet CreateReceipt(Packet operation, string kind, long revision, string error, List<Change> changes = null)
    {
        Packet packet = new Packet();
        packet.Kind = kind;
        packet.Id = operation?.Id;
        packet.Peer = operation?.Peer;
        packet.Scene = operation?.Scene;
        packet.Error = error;
        packet.Revision = revision;
        packet.ProtocolVersion = operation?.ProtocolVersion;
        packet.Mode = operation?.Mode;
        packet.RuntimeRunId = operation?.RuntimeRunId;
        packet.RuntimeSequence = operation?.RuntimeSequence;
        packet.RuntimeTick = operation?.RuntimeTick;
        packet.RuntimeOperationKind = operation?.RuntimeOperationKind;
        packet.Objects = operation?.Objects?.Select(Clone).ToList();
        packet.Changes = changes ?? operation?.Changes?.Select((Change change) => ((object)change == null) ? null : new Change(change.Id, CloneIfPresent(change.Before), CloneIfPresent(change.After))).ToList();
        return packet;
    }

    private static Packet CloneReceipt(Packet packet)
    {
        return CreateReceipt(packet, packet.Kind, packet.Revision, packet.Error);
    }

    private static Packet CreateMinimalReceipt(Packet operation, string kind, long revision, string error)
    {
        return new Packet
        {
            Kind = kind,
            Id = operation?.Id,
            Peer = operation?.Peer,
            Scene = operation?.Scene,
            Error = error,
            Revision = revision,
            ProtocolVersion = operation?.ProtocolVersion,
            Mode = operation?.Mode,
            RuntimeRunId = operation?.RuntimeRunId,
            RuntimeSequence = operation?.RuntimeSequence,
            RuntimeTick = operation?.RuntimeTick,
            RuntimeOperationKind = operation?.RuntimeOperationKind
        };
    }

    private static ObjectState Clone(ObjectState state)
    {
        if ((object)state == null || state.Data == null)
        {
            throw new InvalidOperationException("Runtime object state is missing or has no data.");
        }
        return state with
        {
            Data = (JsonObject)state.Data.DeepClone()
        };
    }

    private static ObjectState CloneIfPresent(ObjectState state)
    {
        return ((object)state == null) ? null : Clone(state);
    }

    private static ObjectState Normalize(ObjectState state)
    {
        string text = Guid.Parse(state.Id).ToString("D");
        string parent = (string.IsNullOrEmpty(state.Parent) ? state.Parent : Guid.Parse(state.Parent).ToString("D"));
        JsonObject jsonObject = (JsonObject)state.Data.DeepClone();
        jsonObject["__guid"] = text;
        if (jsonObject["Components"] is JsonArray jsonArray)
        {
            for (int i = 0; i < jsonArray.Count; i++)
            {
                if (!(jsonArray[i] is JsonObject jsonObject2) || !jsonObject2.TryGetPropertyValue("__guid", out JsonNode jsonNode) || !(jsonNode is JsonValue jsonValue) || !jsonValue.TryGetValue<string>(out string value))
                {
                    throw new InvalidOperationException("Runtime state shape is invalid: component __guid is missing.");
                }
                jsonObject2["__guid"] = Guid.Parse(value).ToString("D");
            }
        }
        return new ObjectState(text, parent, jsonObject);
    }

    private static bool RuntimeEqual(ObjectState left, ObjectState right)
    {
        if ((object)left == null || (object)right == null)
        {
            return (object)left == null && (object)right == null;
        }
        ObjectState objectState = Normalize(left);
        ObjectState objectState2 = Normalize(right);
        if (!string.Equals(objectState.Id, objectState2.Id, StringComparison.Ordinal))
        {
            return false;
        }
        if (!string.Equals(objectState.Parent, objectState2.Parent, StringComparison.Ordinal))
        {
            return false;
        }
        return JsonNode.DeepEquals(objectState.Data, objectState2.Data);
    }

    private long CurrentRevision(string runId)
    {
        string value;
        long revision;
        return (!string.IsNullOrWhiteSpace(runId) && runScenes.TryGetValue(runId, out value) && CurrentRevision(runId, value, out revision)) ? revision : 0;
    }

    private bool CurrentRevision(string runId, string scenePath, out long revision)
    {
        if (namespaces.TryGetValue(MakeNamespaceKey(runId, scenePath), out var value))
        {
            revision = value.Revision;
            return true;
        }
        revision = 0L;
        return false;
    }

    private bool TryGetNamespace(string runId, string scenePath, out Namespace current)
    {
        return namespaces.TryGetValue(MakeNamespaceKey(runId, scenePath), out current);
    }

    private static InvalidOperationException MissingNamespace(string runId, string scenePath)
    {
        return new InvalidOperationException($"Runtime authority namespace does not exist for run '{runId ?? "<null>"}' and scene '{scenePath ?? "<null>"}'.");
    }

    private static string MakeNamespaceKey(string runId, string scenePath)
    {
        if (runId == null || scenePath == null)
        {
            throw MissingNamespace(runId, scenePath);
        }
        return $"{runId.Length}:{runId}{scenePath.Length}:{scenePath}";
    }
}
