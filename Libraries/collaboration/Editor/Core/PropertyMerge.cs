using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TeamCreate;

/// <summary>
/// Three-way, property-level merge of one existing scene object. The host uses it when an edit arrives built on
/// an older state than the host now holds: instead of refusing the whole edit because somebody else touched the
/// same object, only what the incoming edit actually changed is applied on top of what the host holds, so a move
/// and a tint change made by two people at once both survive.
/// </summary>
/// <remarks>
/// Pure and engine-independent. It never mutates its inputs, and the result shares no nodes with them.
/// </remarks>
public static class PropertyMerge
{
    private const string ComponentsKey = "Components";

    /// <summary>
    /// Merges <paramref name="after"/> (what the editing client produced from <paramref name="before"/>) into
    /// <paramref name="current"/> (what the host holds now). Only properties the incoming edit changed are applied;
    /// every other property keeps the host's value. A property is a clash when the incoming edit changed it and the host's
    /// value differs from both the old and the new incoming value: the incoming value is kept (the later edit wins) and the
    /// property is named in <paramref name="clashed"/>. A property the host already changed to the incoming value is not a clash.
    /// </summary>
    /// <returns>
    /// False, with <paramref name="merged"/> null and <paramref name="clashed"/> empty, when the three states cannot be
    /// merged safely: a null argument, different ids, a component without a string <c>__guid</c>, a duplicate component
    /// guid within one list, or a component guid that the host and the incoming edit both added with different content.
    /// The caller then keeps the plain conflict refusal.
    /// </returns>
    public static bool TryMerge( ObjectState current, ObjectState before, ObjectState after, out ObjectState merged, out List<string> clashed )
    {
        merged = null;
        clashed = new List<string>();
        if ( current is null || before is null || after is null || current.Data is null || before.Data is null || after.Data is null )
        {
            return false;
        }
        if ( !string.Equals( current.Id, before.Id, StringComparison.Ordinal ) || !string.Equals( before.Id, after.Id, StringComparison.Ordinal ) )
        {
            return false;
        }
        if ( !TryReadComponents( current.Data, out ComponentSet currentSet )
            || !TryReadComponents( before.Data, out ComponentSet beforeSet )
            || !TryReadComponents( after.Data, out ComponentSet afterSet ) )
        {
            return false;
        }

        List<string> found = new List<string>();
        JsonObject mergedData = (JsonObject)current.Data.DeepClone();

        // Parent is a field like any other. A missing parent and an empty one both mean "the scene root", so a client that
        // writes one where the host holds the other is not re-parenting anything.
        string mergedParent = current.Parent;
        string currentParent = current.Parent ?? string.Empty;
        string beforeParent = before.Parent ?? string.Empty;
        string afterParent = after.Parent ?? string.Empty;
        if ( !string.Equals( beforeParent, afterParent, StringComparison.Ordinal ) && !string.Equals( currentParent, afterParent, StringComparison.Ordinal ) )
        {
            if ( !string.Equals( currentParent, beforeParent, StringComparison.Ordinal ) )
            {
                AddClash( found, "Parent" );
            }
            mergedParent = after.Parent;
        }

        // Every other top-level key except Components is a field.
        MergeFields( mergedData, before.Data, after.Data, string.Empty, ComponentsKey, found );

        if ( !MergeComponents( mergedData, currentSet, beforeSet, afterSet, current.Data, before.Data, after.Data, found ) )
        {
            return false;
        }

        merged = new ObjectState( current.Id, mergedParent, mergedData );
        clashed = found;
        return true;
    }

    // Applies the incoming change of each field onto `target`, a private copy of the host's object. A field is "changed by the
    // incoming edit" when before and after disagree (presence included: a key that vanished is a removal, an explicit null is
    // not an absent key). `skipKey` is left to the caller (the component list).
    private static void MergeFields( JsonObject target, JsonObject before, JsonObject after, string prefix, string skipKey, List<string> found )
    {
        List<string> keys = before.Select( x => x.Key ).Concat( after.Select( x => x.Key ) ).Distinct( StringComparer.Ordinal ).ToList();
        foreach ( string key in keys )
        {
            if ( skipKey != null && string.Equals( key, skipKey, StringComparison.Ordinal ) )
            {
                continue;
            }
            bool inBefore = before.ContainsKey( key );
            bool inAfter = after.ContainsKey( key );
            JsonNode beforeValue = before[key];
            JsonNode afterValue = after[key];
            if ( inBefore == inAfter && StateRules.ValuesEqual( beforeValue, afterValue ) )
            {
                continue;
            }
            bool inTarget = target.ContainsKey( key );
            JsonNode targetValue = target[key];
            if ( inTarget == inAfter && StateRules.ValuesEqual( targetValue, afterValue ) )
            {
                continue;
            }
            if ( !( inTarget == inBefore && StateRules.ValuesEqual( targetValue, beforeValue ) ) )
            {
                AddClash( found, prefix + key );
            }
            if ( inAfter )
            {
                target[key] = afterValue?.DeepClone();
            }
            else
            {
                target.Remove( key );
            }
        }
    }

    // Components are matched by __guid, never by position: two people can reorder or add components independently. The host's
    // order is kept; components the incoming edit adds (or brings back) are appended in the incoming order.
    private static bool MergeComponents( JsonObject mergedData, ComponentSet currentSet, ComponentSet beforeSet, ComponentSet afterSet, JsonObject currentData, JsonObject beforeData, JsonObject afterData, List<string> found )
    {
        List<JsonObject> ordered = currentSet.Items.Select( x => (JsonObject)x.DeepClone() ).ToList();
        Dictionary<string, JsonObject> live = new Dictionary<string, JsonObject>( StringComparer.Ordinal );
        foreach ( JsonObject component in ordered )
        {
            live[GuidOf( component )] = component;
        }
        List<JsonObject> appended = new List<JsonObject>();

        foreach ( JsonObject afterComponent in afterSet.Items )
        {
            string guid = GuidOf( afterComponent );
            bool inCurrent = live.TryGetValue( guid, out JsonObject mergedComponent );
            if ( !beforeSet.ByGuid.TryGetValue( guid, out JsonObject beforeComponent ) )
            {
                // The incoming edit added it. If the host added the same guid too, the two copies must agree, otherwise
                // there is no safe way to pick one.
                if ( inCurrent )
                {
                    if ( !StateRules.ValuesEqual( currentSet.ByGuid[guid], afterComponent ) )
                    {
                        return false;
                    }
                }
                else
                {
                    appended.Add( (JsonObject)afterComponent.DeepClone() );
                }
                continue;
            }
            if ( StateRules.ValuesEqual( beforeComponent, afterComponent ) )
            {
                continue;
            }
            string name = TypeShort( afterComponent, beforeComponent );
            if ( inCurrent )
            {
                MergeFields( mergedComponent, beforeComponent, afterComponent, name + ".", null, found );
            }
            else
            {
                // The host removed a component the incoming edit still edited: the later edit wins and the component returns.
                AddClash( found, name );
                appended.Add( (JsonObject)afterComponent.DeepClone() );
            }
        }

        HashSet<string> removed = new HashSet<string>( StringComparer.Ordinal );
        foreach ( JsonObject beforeComponent in beforeSet.Items )
        {
            string guid = GuidOf( beforeComponent );
            if ( afterSet.ByGuid.ContainsKey( guid ) || !live.ContainsKey( guid ) )
            {
                continue;
            }
            removed.Add( guid );
            if ( !StateRules.ValuesEqual( currentSet.ByGuid[guid], beforeComponent ) )
            {
                AddClash( found, TypeShort( beforeComponent, null ) + " (removed)" );
            }
        }

        JsonArray result = new JsonArray();
        foreach ( JsonObject component in ordered.Where( x => !removed.Contains( GuidOf( x ) ) ) )
        {
            result.Add( component );
        }
        foreach ( JsonObject component in appended )
        {
            result.Add( component );
        }
        // No Components key appears from nothing: it is written when something is in it, when the host already had one, or when
        // the incoming edit itself introduced the key.
        bool incomingIntroducedKey = afterData.ContainsKey( ComponentsKey ) && !beforeData.ContainsKey( ComponentsKey );
        if ( result.Count > 0 || mergedData.ContainsKey( ComponentsKey ) || incomingIntroducedKey )
        {
            mergedData[ComponentsKey] = result;
        }
        return true;
    }

    // A component list is mergeable only when every entry is an object with a string __guid and no guid repeats. An absent
    // key is simply an empty list.
    private static bool TryReadComponents( JsonObject data, out ComponentSet set )
    {
        set = new ComponentSet();
        if ( !data.ContainsKey( ComponentsKey ) )
        {
            return true;
        }
        if ( data[ComponentsKey] is not JsonArray array )
        {
            return false;
        }
        foreach ( JsonNode item in array )
        {
            if ( item is not JsonObject component || component["__guid"] is not JsonValue guidValue || !guidValue.TryGetValue( out string guid ) || string.IsNullOrEmpty( guid ) )
            {
                return false;
            }
            if ( !set.ByGuid.TryAdd( guid, component ) )
            {
                return false;
            }
            set.Items.Add( component );
        }
        return true;
    }

    private static string GuidOf( JsonObject component ) => component["__guid"].GetValue<string>();

    // "Sandbox.ModelRenderer" -> "ModelRenderer": the name a person recognises in the inspector.
    private static string TypeShort( JsonObject primary, JsonObject fallback )
    {
        foreach ( JsonObject component in new[] { primary, fallback } )
        {
            if ( component?["__type"] is JsonValue typeValue && typeValue.TryGetValue( out string type ) && !string.IsNullOrEmpty( type ) )
            {
                int dot = type.LastIndexOf( '.' );
                return dot >= 0 && dot < type.Length - 1 ? type.Substring( dot + 1 ) : type;
            }
        }
        return "Component";
    }

    private static void AddClash( List<string> found, string name )
    {
        if ( !found.Contains( name ) )
        {
            found.Add( name );
        }
    }

    private sealed class ComponentSet
    {
        public readonly List<JsonObject> Items = new List<JsonObject>();

        public readonly Dictionary<string, JsonObject> ByGuid = new Dictionary<string, JsonObject>( StringComparer.Ordinal );
    }
}
