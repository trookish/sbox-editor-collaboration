using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace TeamCreate;

/// <summary>
/// Scene-level settings (what the engine's <c>Scene.SerializeProperties()</c> returns: <c>TimeScale</c>, <c>NetworkInterpolation</c>,
/// <c>WantsSystemScene</c>, <c>Metadata</c>, <c>NavMesh</c>, <c>GameObjectSystems</c> and whatever else a project adds) are one more piece of
/// host-authoritative state per scene. Two people may change them at once, so the host merges each change per TOP-LEVEL key with the rule
/// already chosen for object properties: a key the sender changed takes the sender's value, a key the sender left alone keeps the host's,
/// and when both changed the same key the later change (the one the host processes second) wins and the key is named in a notice.
///
/// A top-level value is one unit even when it is an object: two people changing different fields inside <c>NavMesh</c> clash on
/// <c>NavMesh</c>, and the later <c>NavMesh</c> replaces the earlier whole. Stitching fields together would produce a navigation setup
/// that neither person made, and the engine reads these objects as wholes (<c>NavMesh.Deserialize</c>, the per-system overrides).
///
/// Every comparison goes through <see cref="StateRules.ValuesEqual"/>, so a value that only moved by the engine's 32-bit float round trip
/// is not a change. Engine-independent so CoreTests can exercise it; nothing here mutates or shares nodes with its inputs.
/// </summary>
public static class SettingsMerge
{
    /// <summary>The packet kind of a settings change (client to host) and of its broadcast (host to every member).</summary>
    public const string PacketKind = "settings";

    /// <summary>
    /// The largest settings object, serialised, that the host accepts in one change: 1 MiB. Ordinary settings are a few hundred bytes;
    /// the bound exists because <c>GameObjectSystems</c> may carry project-defined values of any size, every accepted change is broadcast
    /// to every member and written to the journal, and the wire refuses frames above 8 MiB outright.
    /// </summary>
    public const int MaxSettingsBytes = 1024 * 1024;

    /// <summary>
    /// Merges a sender's change into the host's settings. <paramref name="current"/> is what the host holds, <paramref name="before"/> what
    /// the sender last knew and <paramref name="after"/> what the sender has now. Returns false, with no result, when any input is missing.
    /// <paramref name="clashed"/> lists, in key order, the keys where the host's value differed from both the sender's before and after, so
    /// the sender's later value replaced somebody else's change.
    /// </summary>
    public static bool TryMerge( JsonObject current, JsonObject before, JsonObject after, out JsonObject merged, out List<string> clashed )
    {
        merged = null;
        clashed = new List<string>();
        if ( current == null || before == null || after == null )
        {
            return false;
        }

        // Key order: the host's keys first, then keys only the sender has, so a merged object reads like the host's with the change applied.
        var result = new JsonObject();
        foreach ( string key in KeysOf( current, after, before ) )
        {
            bool senderChanged = !Same( before, after, key );
            if ( !senderChanged )
            {
                CopyKey( current, result, key );
                continue;
            }
            // Equal to the sender's new value already (the same change twice) or untouched by anybody else since the sender read it:
            // no clash either way. Otherwise somebody else changed this key meanwhile, and the later change still wins.
            if ( !Same( current, after, key ) && !Same( current, before, key ) )
            {
                clashed.Add( key );
            }
            CopyKey( after, result, key );
        }
        merged = result;
        return true;
    }

    /// <summary>
    /// What the scan loop should send, or null when the user changed nothing. <paramref name="confirmed"/> is the host's settings as this
    /// editor last had them confirmed (<c>Tracked.Settings</c>); <paramref name="applied"/> what this editor last wrote into the scene (the
    /// host's settings, possibly with an unsent local change kept on top); <paramref name="echo"/> what the engine reported right after that
    /// write; <paramref name="live"/> what the scene reports now. <paramref name="applied"/> and <paramref name="echo"/> are null until the
    /// first write, and then <paramref name="confirmed"/> stands in for both.
    ///
    /// The echo matters because the engine does not take every value it is given: <c>Metadata</c> is computed from components, a
    /// <c>GameObjectSystems</c> entry for a type this project lacks is dropped, a blob reference may not resolve. Per top-level key: a key the
    /// user touched since the write (live differs from echo) is sent as live. A key untouched since the write is sent as live when the engine
    /// took the written value (that value is either the host's or an unsent local change, which must still go out), and as the host's value
    /// when the engine did not (that difference is residue, not the user's work: sending it would revert the other person's setting on the
    /// host, and two editors would then revert each other indefinitely).
    /// </summary>
    public static JsonObject LocalChange( JsonObject confirmed, JsonObject applied, JsonObject echo, JsonObject live )
    {
        if ( confirmed == null || live == null )
        {
            return null;
        }
        JsonObject written = applied ?? confirmed;
        JsonObject reported = echo ?? written;
        var result = new JsonObject();
        foreach ( string key in KeysOf( confirmed, live, reported ) )
        {
            bool userTouched = !Same( reported, live, key );
            bool engineTook = Same( written, reported, key );
            CopyKey( userTouched || engineTook ? live : confirmed, result, key );
        }
        return StateRules.ValuesEqual( result, confirmed ) ? null : result;
    }

    /// <summary>The formal sentence for the people in the session when a later settings change replaced an earlier one; null when nothing clashed.</summary>
    public static string ClashNotice( IReadOnlyCollection<string> clashed )
    {
        return ( clashed == null || clashed.Count == 0 ) ? null : "Concurrent changes to the scene settings were combined; the later change was kept for: " + string.Join( ", ", clashed ) + ".";
    }

    /// <summary>The formal sentence when this editor's unsent settings change was kept on top of a collaborator's change to the same keys; null when none.</summary>
    public static string LocalKeptNotice( IReadOnlyCollection<string> clashed )
    {
        return ( clashed == null || clashed.Count == 0 ) ? null : "A collaborator changed the scene settings while your own change was unsent; your change was kept and will be sent for: " + string.Join( ", ", clashed ) + ".";
    }

    /// <summary>UTF-8 size of the compact JSON text of <paramref name="settings"/>; zero for null.</summary>
    public static int SerializedBytes( JsonObject settings )
    {
        return settings == null ? 0 : Encoding.UTF8.GetByteCount( settings.ToJsonString() );
    }

    /// <summary>The formal refusal for a settings object above <see cref="MaxSettingsBytes"/>; null when it is within the bound.</summary>
    public static string SizeError( JsonObject settings )
    {
        int bytes = SerializedBytes( settings );
        return bytes <= MaxSettingsBytes ? null : "The scene settings change was refused: its settings are " + bytes + " bytes, above the limit of " + MaxSettingsBytes + " bytes. The host's settings were kept; reduce the size of the scene's settings (GameObjectSystems or metadata) and change them again.";
    }

    /// <summary>A deep copy of <paramref name="settings"/>, or null.</summary>
    public static JsonObject Clone( JsonObject settings )
    {
        return (JsonObject)settings?.DeepClone();
    }

    private static IEnumerable<string> KeysOf( JsonObject first, JsonObject second, JsonObject third )
    {
        var seen = new HashSet<string>( StringComparer.Ordinal );
        foreach ( JsonObject source in new[] { first, second, third } )
        {
            foreach ( KeyValuePair<string, JsonNode> pair in source )
            {
                if ( seen.Add( pair.Key ) )
                {
                    yield return pair.Key;
                }
            }
        }
    }

    // Absent and present are different values (a present null included), so adding or removing a key is a change like any other.
    private static bool Same( JsonObject a, JsonObject b, string key )
    {
        bool hasA = a.TryGetPropertyValue( key, out JsonNode valueA );
        bool hasB = b.TryGetPropertyValue( key, out JsonNode valueB );
        return hasA == hasB && ( !hasA || StateRules.ValuesEqual( valueA, valueB ) );
    }

    private static void CopyKey( JsonObject source, JsonObject target, string key )
    {
        if ( source.TryGetPropertyValue( key, out JsonNode value ) )
        {
            target[key] = value?.DeepClone();
        }
    }
}
