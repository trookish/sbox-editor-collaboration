using System.Text.Json.Nodes;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Linq;

namespace TeamCreate;

public sealed record ObjectState(string Id, string Parent, JsonObject Data);

public sealed record Change(string Id, ObjectState Before, ObjectState After);

/// <summary>
/// One message on the wire. Every property that is unset (null, zero or false) is omitted from the JSON: a presence packet used to carry about twenty
/// <c>"Field":null</c> entries, almost half of its 778 bytes, and presence is the one message sent thirty times a second by every editor. Receivers read
/// a missing field as the same default, so older peers are unaffected in both directions.
/// </summary>
public sealed class Packet
{
    // Project file synchronization (FileSync*.cs). Every member is additive and omitted from the wire when unset, so presence and edits
    // stay as small as before and peers that predate file sync ignore them.

    /// <summary>A project-relative file path with "/" separators. Untrusted: the receiver validates it with FilePathRules before any disk access.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Path { get; set; }

    /// <summary>SHA-256 (lowercase hex) of the file as ProjectManifest hashes it (a .meta file through MetaFileRules.Canonical).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Sha256 { get; set; }

    /// <summary>On file-put and file-delete: the version the sender last agreed with the session, null for a file it believes is new.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string BaseSha256 { get; set; }

    /// <summary>Byte offset of a file-chunk (or of the chunk a file-ack acknowledges).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Offset { get; set; }

    /// <summary>The whole file's size in bytes on file-chunk, file-put and file-changed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Total { get; set; }

    /// <summary>One chunk of file content (base64 on the wire, at most FileSyncLimits.ChunkSize bytes before encoding).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[] Data { get; set; }

    /// <summary>One page of the host's file index on a files-index reply; Final marks the last page.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FileIndexEntry> FileEntries { get; set; }

    /// <summary>
    /// What the host supports, sent on welcome. A guest starts file synchronization only when it holds FileSyncLimits.Feature: a newer
    /// guest is admitted by an older host, which would answer an unknown message kind with a fatal error.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string> Features { get; set; }

    /// <summary>
    /// On hello: the fingerprint of the engine build alone (AdmissionRules.EngineFingerprint). The full Baseline also covers every project
    /// file, so it necessarily differs for a guest whose files are about to be synchronized; this keeps the engine check for that guest.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string EngineBaseline { get; set; }

    public string Kind { get; set; }

    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Peer { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Name { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Project { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Baseline { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Token { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Session { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Scene { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Error { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long Revision { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Final { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ObjectState> Objects { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Change> Changes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject Presence { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProtocolVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CollaborationMode? Mode { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string RuntimeRunId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RuntimeTick { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RuntimeSequence { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string RuntimeOperationKind { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string SteamId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string DisplayName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string SteamToken { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string OrgIdent { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string LibraryVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ProjectManifest { get; set; }

    /// <summary>
    /// A formal sentence for the people in the session when the host combined concurrent edits (or let one edit replace another);
    /// null when nothing needs saying. Additive: omitted from the wire when unset, ignored by peers that predate it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Notice { get; set; }

    /// <summary>
    /// True on an accepted packet when the host applied something different from what the sender sent, so the sender knows to
    /// take the host's state rather than keep its own. Additive: omitted from the wire when false.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Merged { get; set; }

    /// <summary>
    /// Scene-level settings (<see cref="SettingsMerge"/>): on a subscribe, the subscriber's settings for the scene; on a snapshot, the host's;
    /// on a client's <c>settings</c> packet, what the sender has now; on the host's <c>settings</c> broadcast, the merged result every member
    /// adopts. Additive: omitted from the wire when unset, ignored by peers that predate it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject Settings { get; set; }

    /// <summary>On a client's <c>settings</c> packet: the settings the sender last had confirmed by the host, the base of the three-way merge. Additive.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject SettingsBefore { get; set; }

    /// <summary>
    /// On a ping: the fingerprint (<see cref="SceneDigest"/>) of the scene state this client last had confirmed by the host, so the
    /// host can notice an editor that has silently drifted. Additive: omitted from the wire when unset, and a host that predates it ignores it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Digest { get; set; }

    /// <summary>On a ping: the last revision this client had applied when it computed <see cref="Digest"/>. The host compares digests only at equal revisions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DigestRevision { get; set; }

    /// <summary>On a ping: the scene <see cref="Digest"/> describes.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string DigestScene { get; set; }

    /// <summary>
    /// On a presence packet: the sender's own monotonic milliseconds (<see cref="SessionClock"/>) when it was sent, so receivers can place the
    /// sample in time and draw a smooth path instead of the latest raw value. Not wall-clock time, and meaningful only to the same sender's
    /// later stamps. The host relays it untouched. Additive: omitted from the wire when 0, which is also how a peer that predates the field reads.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double SentMs { get; set; }
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

/// <summary>A change the scan loop did not send, with the formal sentence telling the user why.</summary>
public sealed record HeldChange( Change Change, string Reason );

public static class StateRules
{
    public const string Version = "teamcreate-1";

    // The structural limits on one object, in one place. Nothing here asks whether a property or component type is on a list:
    // every edit a person can make is synchronised, and these bounds only protect the host, the peers and the relay from a
    // hostile or runaway payload. Each limit is inclusive (an object at exactly the limit passes).

    /// <summary>Largest serialised <see cref="ObjectState"/>, in UTF-8 bytes of its JSON.</summary>
    public const int MaxObjectBytes = 1024 * 1024;

    /// <summary>
    /// Deepest JSON nesting inside an object's data, counting the data object itself as level 1. System.Text.Json refuses to
    /// write or read beyond 64 levels, and the data travels inside envelopes: a packet adds four levels (packet, change list,
    /// change, After), the outbox file one more and the failure report two more. 56 leaves those envelopes room with a margin
    /// of two, so an object that passes here can always be sent, stored and reported.
    /// </summary>
    public const int MaxDataDepth = 56;

    /// <summary>Most components on one object.</summary>
    public const int MaxComponents = 256;

    /// <summary>Largest single text value, in UTF-8 bytes.</summary>
    public const int MaxStringBytes = 256 * 1024;

    /// <summary>Longest property name, in characters.</summary>
    public const int MaxKeyLength = 200;

    /// <summary>Longest component type name, in characters.</summary>
    public const int MaxTypeNameLength = 200;

    /// <summary>
    /// The reserved (<c>__</c>) property names the engine and this library are known to write. Observed in the scenes and
    /// prefabs installed with s&amp;box: <c>__guid</c>, <c>__version</c>, <c>__type</c>, <c>__enabled</c>, <c>__references</c>,
    /// <c>__Prefab</c>, <c>__PrefabInstancePatch</c>, <c>__PrefabIdToInstanceId</c> (scenes) and <c>__properties</c>,
    /// <c>__variables</c> (prefabs). Read or written by the engine's own serializer (GameObject and Component in
    /// Sandbox.Engine): <c>__PrefabVariables</c>, <c>__EditorPrefabNestedInstance</c>, <c>__EditorSkipPrefabBreakOnRefresh</c>,
    /// <c>__NetworkedPrefaInstance</c> (the engine's spelling), <c>__snapshot</c>. <c>__PrefabVariant</c> is listed because
    /// the engine is expected to write it, though no installed sample carries it. <see cref="SiblingOrder.Key"/> is the library's own.
    /// A name outside this set is not refused (a newer engine may add one, and refusing it would stop the session's work);
    /// <see cref="FindUnknownReservedKeys"/> reports it so it can be counted.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownReservedKeys = new HashSet<string>( StringComparer.Ordinal )
    {
        "__guid", "__version", "__type", "__enabled", "__references", "__Prefab", "__PrefabInstancePatch", "__PrefabIdToInstanceId",
        "__properties", "__variables", "__PrefabVariables", "__EditorPrefabNestedInstance", "__EditorSkipPrefabBreakOnRefresh",
        "__NetworkedPrefaInstance", "__snapshot", "__PrefabVariant", SiblingOrder.Key
    };

    public static bool Equal(ObjectState a, ObjectState b)
    {
        return ((object)a == null) ? ((object)b == null) : ((object)b != null && a.Id == b.Id && a.Parent == b.Parent && ValuesEqual(a.Data, b.Data));
    }

    /// <summary>
    /// Structural equality of two scene values, tolerant of the engine's 32-bit float round trip. It matches
    /// <see cref="JsonNode.DeepEquals"/> (objects equal whatever their key order but only with the same key set, arrays element by element in
    /// order, null equal to null, strings and booleans exact) except for numbers. Two numbers that both read as a
    /// <c>long</c> are compared as longs, so integers above 2^53 stay exact; otherwise two numbers are equal when they are
    /// the same 32-bit float, which makes <c>0.6</c> equal to <c>0.600000024</c> (what the editor writes back) and <c>1</c>
    /// equal to <c>1.0</c>. Without this every echo of a float property looked like an edit by somebody else.
    /// </summary>
    public static bool ValuesEqual( JsonNode a, JsonNode b )
    {
        if ( (object)a == (object)b )
        {
            return true;
        }
        if ( a is null || b is null )
        {
            return false;
        }
        if ( a is JsonObject objectA )
        {
            if ( b is not JsonObject objectB || objectA.Count != objectB.Count )
            {
                return false;
            }
            foreach ( KeyValuePair<string, JsonNode> member in objectA )
            {
                if ( !objectB.TryGetPropertyValue( member.Key, out JsonNode other ) || !ValuesEqual( member.Value, other ) )
                {
                    return false;
                }
            }
            return true;
        }
        if ( a is JsonArray arrayA )
        {
            if ( b is not JsonArray arrayB || arrayA.Count != arrayB.Count )
            {
                return false;
            }
            for ( int i = 0; i < arrayA.Count; i++ )
            {
                if ( !ValuesEqual( arrayA[i], arrayB[i] ) )
                {
                    return false;
                }
            }
            return true;
        }
        if ( b is JsonObject || b is JsonArray )
        {
            return false;
        }
        if ( a.GetValueKind() == System.Text.Json.JsonValueKind.Number && b.GetValueKind() == System.Text.Json.JsonValueKind.Number )
        {
            return NumbersEqual( a, b );
        }
        return JsonNode.DeepEquals( a, b );
    }

    // Reads the numbers through their JSON text, which is the same whether a node was parsed off the wire or built in code
    // (a JsonValue made from an int does not report TryGetValue<long>). Exactly equal numbers are settled first, so the
    // common unchanged case never parses anything.
    private static bool NumbersEqual( JsonNode a, JsonNode b )
    {
        if ( JsonNode.DeepEquals( a, b ) )
        {
            return true;
        }
        string textA = a.ToJsonString();
        string textB = b.ToJsonString();
        System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
        if ( long.TryParse( textA, System.Globalization.NumberStyles.AllowLeadingSign, invariant, out long longA ) && long.TryParse( textB, System.Globalization.NumberStyles.AllowLeadingSign, invariant, out long longB ) )
        {
            return longA == longB;
        }
        if ( double.TryParse( textA, System.Globalization.NumberStyles.Float, invariant, out double doubleA ) && double.TryParse( textB, System.Globalization.NumberStyles.Float, invariant, out double doubleB ) )
        {
            return (float)doubleA == (float)doubleB;
        }
        return false;
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

    /// <summary>Throws the formal refusal of <see cref="RefusalOf"/> when there is one.</summary>
    public static void ValidateChange( Change c )
    {
        string refusal = RefusalOf( c );
        if ( refusal != null )
        {
            throw new InvalidOperationException( refusal );
        }
    }

    /// <summary>
    /// Why <paramref name="c"/> may not be sent or applied, as a formal sentence naming the object, or null when it may. Only
    /// structure is checked: the change is a creation (no Before), an edit or a deletion (no After) of one identified object;
    /// an edit keeps its identity; and the state that would be sent (After) is a well-formed object within the size limits.
    /// Any property, any component type and any component property are allowed, as are adding, removing and reordering
    /// components. Before is held to identity only: it is the state the host already holds, so an object that is already too
    /// large can still be shrunk or deleted.
    /// </summary>
    public static string RefusalOf( Change c )
    {
        if ( c is null || ( (object)c.Before == null && (object)c.After == null ) )
        {
            return "An empty change was not sent.";
        }
        foreach ( ObjectState state in new[] { c.Before, c.After } )
        {
            if ( (object)state != null && !HasIdentity( c.Id, state ) )
            {
                return "The object \"" + NameOf( state, c.Id ) + "\" does not have a valid identifier and was not sent.";
            }
        }
        return (object)c.After == null ? null : RefusalOfState( c.After );
    }

    // The id is a GUID, the state carries that id and its data names the same one in __guid, so no edit changes an identity.
    private static bool HasIdentity( string id, ObjectState state )
    {
        return Guid.TryParse( id, out _ )
            && string.Equals( state.Id, id, StringComparison.Ordinal )
            && state.Data is not null
            && state.Data["__guid"] is JsonValue guid && guid.TryGetValue( out string text ) && string.Equals( text, id, StringComparison.Ordinal );
    }

    // The structural rules on one state that is about to be sent. The tree walk comes first: it bounds the depth, so the
    // serialisation that measures the size cannot fail on a payload nested too deep to write.
    private static string RefusalOfState( ObjectState state )
    {
        string name = "The object \"" + NameOf( state, state.Id ) + "\" ";
        string walk = RefusalOfTree( state.Data, 1 );
        if ( walk != null )
        {
            return name + walk;
        }
        if ( state.Data.TryGetPropertyValue( "Components", out JsonNode list ) )
        {
            if ( list is not JsonArray components )
            {
                return name + "has a component list that is not a list and was not sent.";
            }
            if ( components.Count > MaxComponents )
            {
                return name + "has more than " + MaxComponents + " components, beyond the collaboration limit, and was not sent.";
            }
            HashSet<string> guids = new HashSet<string>( StringComparer.Ordinal );
            foreach ( JsonNode entry in components )
            {
                if ( entry is not JsonObject component )
                {
                    return name + "has a component entry that is not an object and was not sent.";
                }
                if ( component["__type"] is not JsonValue typeValue || !typeValue.TryGetValue( out string type ) || string.IsNullOrEmpty( type ) )
                {
                    return name + "has a component without a type name and was not sent.";
                }
                if ( type.Length > MaxTypeNameLength )
                {
                    return name + "has a component type name longer than " + MaxTypeNameLength + " characters and was not sent.";
                }
                if ( component["__guid"] is not JsonValue guidValue || !guidValue.TryGetValue( out string guid ) || !Guid.TryParse( guid, out _ ) )
                {
                    return name + "has a component without a valid identifier and was not sent.";
                }
                if ( !guids.Add( guid ) )
                {
                    return name + "has two components with the identifier " + guid + " and was not sent.";
                }
            }
        }
        if ( System.Text.Json.JsonSerializer.SerializeToUtf8Bytes( state ).Length > MaxObjectBytes )
        {
            return name + "is larger than the " + MaxObjectBytes / ( 1024 * 1024 ) + " MiB collaboration limit and was not sent.";
        }
        return null;
    }

    // Depth, property names and text sizes, everywhere in the tree. `depth` is the level of `node` (the data object is 1).
    private static string RefusalOfTree( JsonNode node, int depth )
    {
        if ( node is JsonObject obj )
        {
            if ( depth > MaxDataDepth )
            {
                return DepthRefusal();
            }
            foreach ( KeyValuePair<string, JsonNode> member in obj )
            {
                if ( member.Key.Length == 0 )
                {
                    return "contains an empty property name and was not sent.";
                }
                if ( member.Key.Length > MaxKeyLength )
                {
                    return "contains a property name longer than " + MaxKeyLength + " characters and was not sent.";
                }
                string inner = RefusalOfTree( member.Value, depth + 1 );
                if ( inner != null )
                {
                    return inner;
                }
            }
            return null;
        }
        if ( node is JsonArray array )
        {
            if ( depth > MaxDataDepth )
            {
                return DepthRefusal();
            }
            foreach ( JsonNode item in array )
            {
                string inner = RefusalOfTree( item, depth + 1 );
                if ( inner != null )
                {
                    return inner;
                }
            }
            return null;
        }
        if ( node is JsonValue value && value.TryGetValue( out string text ) && System.Text.Encoding.UTF8.GetByteCount( text ) > MaxStringBytes )
        {
            return "contains a text value larger than the " + MaxStringBytes / 1024 + " KiB collaboration limit and was not sent.";
        }
        return null;
    }

    private static string DepthRefusal() => "is nested more than " + MaxDataDepth + " levels deep, beyond the collaboration limit, and was not sent.";

    /// <summary>
    /// The reserved (<c>__</c>) property names anywhere in <paramref name="data"/> that are not in <see cref="KnownReservedKeys"/>,
    /// each once, in the order they are first met. They are allowed; the caller counts or logs them.
    /// </summary>
    public static List<string> FindUnknownReservedKeys( JsonObject data )
    {
        List<string> found = new List<string>();
        Visit( data );
        return found;

        void Visit( JsonNode node )
        {
            if ( node is JsonObject obj )
            {
                foreach ( KeyValuePair<string, JsonNode> member in obj )
                {
                    if ( member.Key.StartsWith( "__", StringComparison.Ordinal ) && !KnownReservedKeys.Contains( member.Key ) && !found.Contains( member.Key ) )
                    {
                        found.Add( member.Key );
                    }
                    Visit( member.Value );
                }
            }
            else if ( node is JsonArray array )
            {
                foreach ( JsonNode item in array )
                {
                    Visit( item );
                }
            }
        }
    }

    /// <summary>
    /// Splits the scan loop's batch into what is sent and what is held. A change that <see cref="RefusalOf"/> refuses is held
    /// with its refusal, and so is every change that would leave the host's scene inconsistent without it: an object whose
    /// parent is held (or otherwise absent), and the deletion of an object that a held state still names as its parent. The
    /// rest is returned in the original order and, applied to <paramref name="known"/>, always passes <see cref="ValidateGraph"/>.
    /// Holding one object's change keeps the session running: the others are sent, and the held object stays as the session
    /// last confirmed it until its edit fits again.
    /// </summary>
    public static List<Change> SendableChanges( IReadOnlyDictionary<string, ObjectState> known, IEnumerable<Change> changes, List<HeldChange> held )
    {
        List<Change> all = changes.ToList();
        Dictionary<string, string> reasons = new Dictionary<string, string>( StringComparer.Ordinal );
        List<string> order = new List<string>();
        void Hold( Change change, string reason )
        {
            reasons[change.Id] = reason;
            order.Add( change.Id );
        }

        foreach ( Change change in all )
        {
            string refusal = RefusalOf( change );
            if ( refusal != null )
            {
                Hold( change, refusal );
            }
        }

        Dictionary<string, ObjectState> result = null;
        bool settled = false;
        while ( !settled )
        {
            settled = true;
            result = Result( known, all, reasons );
            foreach ( Change change in all.Where( x => !reasons.ContainsKey( x.Id ) ) )
            {
                if ( (object)change.After != null && !string.IsNullOrEmpty( change.After.Parent ) && !result.ContainsKey( change.After.Parent ) )
                {
                    Hold( change, "The object \"" + NameOf( change.After, change.Id ) + "\" was not sent because its parent \"" + NameIn( all, known, change.After.Parent ) + "\" was not sent." );
                    settled = false;
                }
                else if ( (object)change.After == null && result.Values.FirstOrDefault( x => string.Equals( x.Parent, change.Id, StringComparison.Ordinal ) ) is { } child )
                {
                    Hold( change, "The deletion of \"" + NameOf( change.Before, change.Id ) + "\" was not sent because its child \"" + NameOf( child, child.Id ) + "\" was not sent." );
                    settled = false;
                }
            }
        }

        // Anything the rules above did not foresee (a component identity used by two objects, a parenting cycle) holds the
        // whole batch rather than letting the host refuse it.
        try
        {
            ValidateGraph( result );
        }
        catch ( InvalidOperationException ex )
        {
            foreach ( Change change in all.Where( x => !reasons.ContainsKey( x.Id ) ).ToList() )
            {
                Hold( change, "The change to \"" + NameOf( change.After ?? change.Before, change.Id ) + "\" was not sent because the scene would not stay consistent (" + ex.Message + ")." );
            }
        }

        foreach ( string id in order )
        {
            held.Add( new HeldChange( all.First( x => x.Id == id ), reasons[id] ) );
        }
        return all.Where( x => !reasons.ContainsKey( x.Id ) ).ToList();
    }

    private static Dictionary<string, ObjectState> Result( IReadOnlyDictionary<string, ObjectState> known, List<Change> all, Dictionary<string, string> reasons )
    {
        Dictionary<string, ObjectState> result = known.ToDictionary( x => x.Key, x => x.Value );
        foreach ( Change change in all.Where( x => !reasons.ContainsKey( x.Id ) ) )
        {
            if ( (object)change.After == null )
            {
                result.Remove( change.Id );
            }
            else
            {
                result[change.Id] = change.After;
            }
        }
        return result;
    }

    // The name of an object the batch or the session knows, for a sentence about it.
    private static string NameIn( List<Change> all, IReadOnlyDictionary<string, ObjectState> known, string id )
    {
        Change change = all.FirstOrDefault( x => x.Id == id );
        ObjectState state = change?.After ?? change?.Before ?? known.GetValueOrDefault( id );
        return (object)state == null ? id : NameOf( state, id );
    }

    /// <summary>The name a person recognises an object by (its Name, shortened when very long), or its id when it has none.</summary>
    public static string NameOf( ObjectState state, string id )
    {
        if ( state?.Data?["Name"] is JsonValue value && value.TryGetValue( out string name ) && !string.IsNullOrEmpty( name ) )
        {
            return name.Length <= 100 ? name : name.Substring( 0, 100 ) + "...";
        }
        return id ?? state?.Id;
    }

    /// <summary>The notice for a component type the sender has and this project lacks; shown once per type.</summary>
    public static string MissingComponentTypeNotice( string type ) =>
        "The component type \"" + type + "\" is not available in your project. Update your code to see it.";

    /// <summary>The notice for an object that stopped being saved with the scene (for example, marked as not saved).</summary>
    public static string NoLongerSavedNotice( string name ) =>
        "The object \"" + name + "\" can no longer be saved with the scene, so it was removed from the shared scene.";

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

    /// <summary>
    /// The host's scene-level settings per scene, taken from the first subscribe that created the scene and changed only through
    /// <see cref="ApplySettings"/>. A scene whose first subscriber sent none has no entry.
    /// </summary>
    public Dictionary<string, JsonObject> SceneSettings { get; } = new Dictionary<string, JsonObject>();

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
                Objects = p.Objects,
                Settings = p.Settings
            });
            Scenes.Add(p.Scene, dictionary);
            // No size bound here: the subscribe packet has already arrived whole, and refusing the settings would leave the scene with
            // none, which every later change would then replace wholesale. The bound applies to changes, which are rebroadcast.
            if (p.Settings != null)
            {
                SceneSettings[p.Scene] = SettingsMerge.Clone(p.Settings);
            }
        }
        // The snapshot is a copy: the same packet goes to a drift resync and, in-process, to tests that may change it.
        return new Packet
        {
            Kind = "snapshot",
            Scene = p.Scene,
            Objects = Scenes[p.Scene].Values.ToList(),
            Settings = SettingsMerge.Clone(SceneSettings.GetValueOrDefault(p.Scene)),
            Revision = Revision
        };
    }

    /// <summary>
    /// Applies one member's change to a scene's settings (<see cref="SettingsMerge"/>): merged per top-level key against what the host holds,
    /// later change wins on a clash with the key named in <see cref="Packet.Notice"/>. Returns the <c>settings</c> packet to broadcast (the
    /// merged settings, sender, id and new revision, also what the journal records) or a <c>reject</c> for the sender alone. A resent packet
    /// gets the receipt it got the first time, as <see cref="Apply"/> does.
    /// </summary>
    public Packet ApplySettings(Packet p)
    {
        string key = p.Peer + ":" + p.Id;
        if (receipts.TryGetValue(key, out var value))
        {
            return value;
        }
        try
        {
            if (p.Scene == null || !Scenes.ContainsKey(p.Scene))
            {
                throw new InvalidOperationException("Subscribe to scene first");
            }
            if (p.Settings == null || p.SettingsBefore == null)
            {
                throw new InvalidOperationException("A scene settings change must carry the settings before and after the change.");
            }
            string sizeError = SettingsMerge.SizeError(p.Settings) ?? SettingsMerge.SizeError(p.SettingsBefore);
            if (sizeError != null)
            {
                throw new InvalidOperationException(sizeError);
            }
            JsonObject merged;
            List<string> clashed;
            if (!SceneSettings.TryGetValue(p.Scene, out JsonObject current))
            {
                // Nothing to merge with: the scene's first subscriber reported no settings. The change is adopted whole.
                merged = SettingsMerge.Clone(p.Settings);
                clashed = new List<string>();
            }
            else if (!SettingsMerge.TryMerge(current, p.SettingsBefore, p.Settings, out merged, out clashed))
            {
                throw new InvalidOperationException("The scene settings change could not be merged with the host's settings.");
            }
            sizeError = SettingsMerge.SizeError(merged);
            if (sizeError != null)
            {
                throw new InvalidOperationException(sizeError);
            }
            value = new Packet
            {
                Kind = SettingsMerge.PacketKind,
                Id = p.Id,
                Peer = p.Peer,
                Scene = p.Scene,
                Settings = SettingsMerge.Clone(merged),
                Revision = Revision + 1,
                Merged = clashed.Count > 0 || !StateRules.ValuesEqual(merged, p.Settings),
                Notice = SettingsMerge.ClashNotice(clashed)
            };
            persist(value);
            Revision++;
            SceneSettings[p.Scene] = merged;
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
            List<Change> applied = new List<Change>();
            List<string> notices = new List<string>();
            foreach (Change change in p.Changes)
            {
                StateRules.ValidateChange(change);
                Change change2 = Reconcile(change, value2.GetValueOrDefault(change.Id), notices);
                if ((object)change2.After == null)
                {
                    dictionary.Remove(change2.Id);
                }
                else
                {
                    dictionary[change2.Id] = change2.After;
                }
                applied.Add(change2);
            }
            StateRules.ValidateGraph(dictionary);
            // The packet carries what the host applied, not what the sender sent: every follower's Before is then the state it
            // already holds, and the journal replays to the host's scene. Merged is set when the host's result is not simply the
            // sender's edit: some applied After differs from the one sent, or the outcome needed a notice (a clash resolved in the
            // sender's favour still leaves After equal to the one sent, yet the people in the session must be told).
            bool merged = notices.Count > 0;
            for (int i = 0; i < applied.Count; i++)
            {
                merged |= !StateRules.Equal(applied[i].After, p.Changes[i].After);
            }
            value = new Packet
            {
                Kind = "accepted",
                Id = p.Id,
                Peer = p.Peer,
                Scene = p.Scene,
                Changes = applied,
                Revision = Revision + 1,
                Final = p.Final,
                Merged = merged,
                Notice = (notices.Count == 0) ? null : string.Join(" ", notices)
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

    // Decides what the host applies for one incoming change, given the object as the host holds it now (null if absent). An edit
    // built on the host's current state is applied as sent. Otherwise somebody else changed the object in the meantime, and the
    // rule is: the later edit (the one processed second) wins, per property, and a clash is reported in `notices` rather than
    // refused. Only the unambiguous cases are resolved; anything else keeps the conflict refusal.
    private static Change Reconcile(Change change, ObjectState current, List<string> notices)
    {
        if (StateRules.Equal(current, change.Before))
        {
            return change;
        }
        // A creation of an id that already exists is two objects claiming one identity, not an edit to merge.
        if ((object)change.Before == null)
        {
            throw Conflict(change);
        }
        Change applied;
        if ((object)change.After == null)
        {
            // Deleting an object that someone edited meanwhile: the deletion wins. Deleting one that is already gone has
            // nothing to apply, so it stays a conflict.
            if ((object)current == null)
            {
                throw Conflict(change);
            }
            applied = new Change(change.Id, current, null);
            notices.Add("The later deletion of \"" + DisplayName(current) + "\" was kept; a concurrent edit to the same object was discarded.");
        }
        else if ((object)current == null)
        {
            // Editing an object that was deleted meanwhile: the later edit wins and the object comes back as the editor left it.
            applied = new Change(change.Id, null, change.After);
            notices.Add("A concurrent deletion of \"" + DisplayName(change.After) + "\" was overridden; the later edit was kept and the object was restored.");
        }
        else
        {
            if (!PropertyMerge.TryMerge(current, change.Before, change.After, out ObjectState merged, out List<string> clashed))
            {
                throw Conflict(change);
            }
            applied = new Change(change.Id, current, merged);
            if (clashed.Count > 0)
            {
                notices.Add("Concurrent edits to \"" + DisplayName(current) + "\" were combined; the later edit was kept for: " + string.Join(", ", clashed) + ".");
            }
        }
        // The applied change is what every peer will see, so it passes the same rules as an edit sent in one piece.
        StateRules.ValidateChange(applied);
        return applied;
    }

    private static InvalidOperationException Conflict(Change change)
    {
        return new InvalidOperationException("Conflict on " + change.Id + ": first accepted edit wins. Your edit is retained in recovery.");
    }

    // The name a person recognises the object by; the id when it has no readable name.
    private static string DisplayName(ObjectState state)
    {
        return (state.Data?["Name"] is JsonValue jsonValue && jsonValue.TryGetValue(out string value) && !string.IsNullOrEmpty(value)) ? value : state.Id;
    }
}
