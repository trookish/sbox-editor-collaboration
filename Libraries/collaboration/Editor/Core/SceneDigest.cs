using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeamCreate;

/// <summary>
/// A short fingerprint of a whole scene, small enough to ride on every ping. Each peer computes it
/// over its own copy; when the host sees a peer report a different digest than its own it knows the
/// two editors have drifted apart and can resend a snapshot.
///
/// The digest is only useful if two editors holding the same scene always agree, so it is computed
/// over a canonical text rather than over whatever the dictionaries and JSON trees happen to hold:
/// objects are sorted by id, object keys are sorted at every depth, arrays keep their order, and
/// numbers are normalised to what survives the engine's 32-bit float round trip (0.6 comes back as
/// 0.600000024). The text is JSON-shaped, with every string quoted and escaped, so no characters
/// moved between neighbouring fields can ever produce the same text.
///
/// Engine-independent on purpose (no Sandbox or Editor types), so CoreTests can exercise it.
/// </summary>
public static class SceneDigest
{
    /// <summary>Hexadecimal characters kept from the SHA-256 hash. Sixty-four bits is plenty to notice drift, which is not an adversarial setting.</summary>
    public const int Length = 16;

    // 2^24: the largest magnitude below which every whole number is exactly a 32-bit float. Whole
    // numbers beyond it can still be genuine 64-bit identifiers (seeds, hashes) that never passed
    // through a float, so they are written exactly instead of being rounded.
    private const long ExactFloatLimit = 16_777_216;

    // System.Text.Json refuses to parse deeper than 64 levels, so no scene that arrived over the
    // wire can exceed this. A tree built in memory can be arbitrarily deep, and recursing into one
    // would overflow the stack, which cannot be caught; refusing it is the only safe answer.
    private const int MaxDepth = 64;

    /// <summary>
    /// Computes the digest of <paramref name="scene"/>: the first <see cref="Length"/> lowercase
    /// hexadecimal characters of the SHA-256 hash of its canonical text. An empty scene has a fixed
    /// digest of its own.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The scene is null, or it contains an object that cannot be fingerprinted faithfully (a null
    /// object, a null id, an id that disagrees with its dictionary key, null data, a value of a type
    /// JSON cannot express, or data nested deeper than any parsed scene could be). A digest of a
    /// malformed scene must not look like a valid one, so these are refused rather than skipped.
    /// </exception>
    public static string Compute( IReadOnlyDictionary<string, ObjectState> scene )
    {
        if ( scene == null )
        {
            throw new ArgumentException( "A scene digest cannot be computed because the scene is null.", nameof( scene ) );
        }

        var objects = new List<ObjectState>( scene.Count );
        foreach ( var pair in scene )
        {
            var state = pair.Value;
            if ( state == null )
            {
                throw new ArgumentException( $"A scene digest cannot be computed because the entry '{pair.Key}' holds no object state.", nameof( scene ) );
            }
            if ( !string.Equals( state.Id, pair.Key, StringComparison.Ordinal ) )
            {
                throw new ArgumentException( $"A scene digest cannot be computed because the entry '{pair.Key}' holds an object whose id is '{state.Id ?? "(null)"}'. Each key must equal the id of its object.", nameof( scene ) );
            }
            if ( state.Data == null )
            {
                throw new ArgumentException( $"A scene digest cannot be computed because the object '{pair.Key}' has no data.", nameof( scene ) );
            }
            objects.Add( state );
        }

        // Ordinal, never culture-aware: two machines with different locales must sort alike.
        objects.Sort( ( a, b ) => string.CompareOrdinal( a.Id, b.Id ) );

        var text = new StringBuilder( 64 + objects.Count * 256 );
        text.Append( '[' );
        for ( int i = 0; i < objects.Count; i++ )
        {
            if ( i > 0 ) text.Append( ',' );
            var state = objects[i];
            text.Append( "{\"id\":" );
            AppendString( text, state.Id );
            // A missing parent and an empty one are the same root-level state.
            text.Append( ",\"parent\":" );
            AppendString( text, state.Parent ?? string.Empty );
            text.Append( ",\"data\":" );
            AppendNode( text, state.Data, 0, state.Id );
            text.Append( '}' );
        }
        text.Append( ']' );

        var hash = SHA256.HashData( Encoding.UTF8.GetBytes( text.ToString() ) );
        return Convert.ToHexString( hash, 0, Length / 2 ).ToLowerInvariant();
    }

    private static void AppendNode( StringBuilder text, JsonNode node, int depth, string objectId )
    {
        if ( depth > MaxDepth )
        {
            throw new ArgumentException( $"A scene digest cannot be computed because the data of object '{objectId}' is nested more than {MaxDepth} levels deep.", "scene" );
        }

        switch ( node )
        {
            case null:
                text.Append( "null" );
                break;

            case JsonObject obj:
                {
                    // Sorted by key so two editors that built the same object in different orders agree.
                    var properties = new List<KeyValuePair<string, JsonNode>>( obj.Count );
                    foreach ( var property in obj ) properties.Add( property );
                    properties.Sort( ( a, b ) => string.CompareOrdinal( a.Key, b.Key ) );

                    text.Append( '{' );
                    for ( int i = 0; i < properties.Count; i++ )
                    {
                        if ( i > 0 ) text.Append( ',' );
                        AppendString( text, properties[i].Key );
                        text.Append( ':' );
                        AppendNode( text, properties[i].Value, depth + 1, objectId );
                    }
                    text.Append( '}' );
                    break;
                }

            case JsonArray array:
                {
                    // Array order is meaningful (component order decides execution order), so it is kept.
                    text.Append( '[' );
                    for ( int i = 0; i < array.Count; i++ )
                    {
                        if ( i > 0 ) text.Append( ',' );
                        AppendNode( text, array[i], depth + 1, objectId );
                    }
                    text.Append( ']' );
                    break;
                }

            case JsonValue value:
                AppendValue( text, value, objectId );
                break;

            default:
                throw new ArgumentException( $"A scene digest cannot be computed because the data of object '{objectId}' holds an unsupported node of type {node.GetType().Name}.", "scene" );
        }
    }

    private static void AppendValue( StringBuilder text, JsonValue value, string objectId )
    {
        switch ( value.GetValueKind() )
        {
            case JsonValueKind.True:
                text.Append( "true" );
                return;

            case JsonValueKind.False:
                text.Append( "false" );
                return;

            case JsonValueKind.Null:
                text.Append( "null" );
                return;

            case JsonValueKind.Number:
                if ( TryAppendNumber( text, value ) ) return;
                break;

            case JsonValueKind.String:
                // A CLR-backed value that merely serialises as a string (a Guid, a DateTime) reports
                // String but cannot be read back as one; it is refused below like any other stranger.
                if ( value.TryGetValue<string>( out var s ) )
                {
                    AppendString( text, s );
                    return;
                }
                break;
        }

        throw new ArgumentException( $"A scene digest cannot be computed because the data of object '{objectId}' holds a value that cannot be written canonically.", "scene" );
    }

    private static bool TryAppendNumber( StringBuilder text, JsonValue value )
    {
        // Whole numbers keep their exact text. Up to 2^24 that is also what the float rule below
        // would produce, and beyond it the value is a real 64-bit quantity that must not be rounded.
        if ( TryReadWhole( value, out long whole ) )
        {
            text.Append( whole.ToString( CultureInfo.InvariantCulture ) );
            return true;
        }

        // Above long.MaxValue only an unsigned value is still a whole number.
        if ( value.TryGetValue<ulong>( out ulong huge ) )
        {
            text.Append( huge.ToString( CultureInfo.InvariantCulture ) );
            return true;
        }

        if ( !TryReadReal( value, out double real ) ) return false;

        // The engine keeps 32-bit floats, so anything finer than a float is noise: 0.6 returns as
        // 0.600000024, and 1 as 1.0 or 1.00000001. Rounding to float first makes all of those agree.
        float rounded = (float)real;
        if ( float.IsNaN( rounded ) )
        {
            text.Append( "NaN" );
        }
        else if ( float.IsPositiveInfinity( rounded ) )
        {
            text.Append( "Infinity" );
        }
        else if ( float.IsNegativeInfinity( rounded ) )
        {
            text.Append( "-Infinity" );
        }
        else if ( rounded == MathF.Floor( rounded ) && MathF.Abs( rounded ) <= ExactFloatLimit )
        {
            // Also folds -0 into 0 and 1.0 into 1.
            text.Append( ( (long)rounded ).ToString( CultureInfo.InvariantCulture ) );
        }
        else
        {
            text.Append( rounded.ToString( "R", CultureInfo.InvariantCulture ) );
        }
        return true;
    }

    // A value parsed from JSON text is backed by a JsonElement and converts between numeric types,
    // but a value built in memory holds one specific CLR type and answers only to that exact type:
    // JsonValue.Create( 5 ).TryGetValue<long>() is false. Both origins reach this code, so each
    // integral CLR type is tried in turn.
    private static bool TryReadWhole( JsonValue value, out long whole )
    {
        if ( value.TryGetValue<long>( out whole ) ) return true;
        if ( value.TryGetValue<int>( out int i ) ) { whole = i; return true; }
        if ( value.TryGetValue<uint>( out uint ui ) ) { whole = ui; return true; }
        if ( value.TryGetValue<short>( out short sh ) ) { whole = sh; return true; }
        if ( value.TryGetValue<ushort>( out ushort ush ) ) { whole = ush; return true; }
        if ( value.TryGetValue<byte>( out byte b ) ) { whole = b; return true; }
        if ( value.TryGetValue<sbyte>( out sbyte sb ) ) { whole = sb; return true; }
        if ( value.TryGetValue<ulong>( out ulong ul ) && ul <= long.MaxValue ) { whole = (long)ul; return true; }
        whole = 0;
        return false;
    }

    // The fractional counterpart: a parsed number answers to double, an in-memory one only to the
    // exact CLR type it was built from.
    private static bool TryReadReal( JsonValue value, out double real )
    {
        if ( value.TryGetValue<double>( out real ) ) return true;
        if ( value.TryGetValue<float>( out float single ) ) { real = single; return true; }
        if ( value.TryGetValue<decimal>( out decimal exact ) ) { real = (double)exact; return true; }
        real = 0;
        return false;
    }

    // JSON escaping, with one addition: an unpaired surrogate is written as \uXXXX, because UTF-8
    // would encode every one of them as U+FFFD and distinct strings would hash alike.
    private static void AppendString( StringBuilder text, string value )
    {
        text.Append( '"' );
        for ( int i = 0; i < value.Length; i++ )
        {
            char c = value[i];
            if ( c == '"' ) text.Append( "\\\"" );
            else if ( c == '\\' ) text.Append( "\\\\" );
            else if ( c < ' ' ) AppendEscape( text, c );
            else if ( char.IsHighSurrogate( c ) && i + 1 < value.Length && char.IsLowSurrogate( value[i + 1] ) )
            {
                text.Append( c ).Append( value[i + 1] );
                i++;
            }
            else if ( char.IsSurrogate( c ) ) AppendEscape( text, c );
            else text.Append( c );
        }
        text.Append( '"' );
    }

    private static void AppendEscape( StringBuilder text, char c )
    {
        text.Append( "\\u" ).Append( ( (int)c ).ToString( "x4", CultureInfo.InvariantCulture ) );
    }
}
