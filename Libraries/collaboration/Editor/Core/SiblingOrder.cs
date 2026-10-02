using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeamCreate;

/// <summary>
/// Sibling order as synchronised scene state. The engine keeps the order of an object's children (and of the scene's root
/// objects) in a list, but a serialised object does not say where it stands in its parent's list, so an <see cref="ObjectState"/>
/// carried no order and a reorder was invisible to every other editor. The capture now writes the object's position among
/// its saved siblings into its data under <see cref="Key"/>, a key the library owns, so a reorder travels, merges and is
/// journaled like any other field (two people moving different objects both win; moving the same object, the later one wins).
/// The key is removed again before the data is handed to the engine.
/// </summary>
public static class SiblingOrder
{
    /// <summary>The library-owned key holding the object's zero-based position among its saved siblings.</summary>
    public const string Key = "__collabOrder";

    /// <summary>
    /// The order written in <paramref name="data"/>, or null when there is none or it is not a whole number that fits an
    /// <see cref="int"/>. A malformed order is ignored rather than refused: the object still syncs, it simply keeps its place.
    /// </summary>
    public static int? Read( JsonObject data )
    {
        if ( data?[Key] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number )
        {
            return null;
        }
        return int.TryParse( value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int order ) ? order : null;
    }

    /// <summary>
    /// A copy of <paramref name="data"/> ready for the engine's deserializer: without the order key, which the engine does
    /// not know, and without <c>Children</c>, because children are separate states applied on their own and must never be
    /// created twice by a deserialize call. The tracked state itself is left untouched.
    /// </summary>
    public static JsonObject ForEngine( JsonObject data )
    {
        JsonObject copy = (JsonObject)data.DeepClone();
        copy.Remove( Key );
        copy.Remove( "Children" );
        return copy;
    }

    /// <summary>
    /// The order the given siblings should stand in. Siblings with an order come first, by order; two equal orders (two
    /// people moved different objects to the same place at once) are settled by id, so every editor arrives at the same list.
    /// Siblings without an order follow, in the order <paramref name="current"/> lists them.
    /// </summary>
    public static List<string> Arrange( IEnumerable<string> current, IReadOnlyDictionary<string, int?> orders )
    {
        List<string> ids = current.ToList();
        List<string> ordered = ids
            .Where( x => orders.GetValueOrDefault( x ).HasValue )
            .OrderBy( x => orders[x].Value )
            .ThenBy( x => x, StringComparer.Ordinal )
            .ToList();
        ordered.AddRange( ids.Where( x => !orders.GetValueOrDefault( x ).HasValue ) );
        return ordered;
    }
}
