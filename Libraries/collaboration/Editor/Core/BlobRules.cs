using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace TeamCreate;

/// <summary>
/// Moves mesh geometry out of an object's state and back. <c>GameObject.Serialize()</c> run outside a blob context writes the whole
/// <c>PolygonMesh</c> as one base64 text value (docs/BLOB-SYNC.md, section 1). A mesh of a realistic map is tens of kilobytes to over half a
/// megabyte and every change carries it twice (Before and After), so it is replaced in the state by a short placeholder that names the
/// SHA-256 of the decoded bytes, and the bytes travel separately (<see cref="BlobExchange"/>). The placeholder is an ordinary text value:
/// a changed mesh is a changed object, and diff, merge and rebase need to know nothing about it.
/// </summary>
public static class BlobRules
{
    public const string PlaceholderPrefix = "teamcreate-blob:sha256:";

    // The notice names the unit a person edited. Chosen for the mapping tool, the only writer of such geometry.
    public const string ClashName = "Mesh geometry";

    public static string Placeholder( string hash ) => PlaceholderPrefix + hash;

    /// <summary>Exact form only: lowercase digest, nothing after it. Anything else is text, never a name for a file.</summary>
    public static bool TryParsePlaceholder( string text, out string hash )
    {
        hash = null;
        if ( text == null || text.Length != PlaceholderPrefix.Length + 64 || !text.StartsWith( PlaceholderPrefix, StringComparison.Ordinal ) ) return false;
        string candidate = text.Substring( PlaceholderPrefix.Length );
        if ( !BlobStore.ValidHash( candidate ) ) return false;
        hash = candidate;
        return true;
    }

    /// <summary>
    /// A text value that is moved out of the state: long enough, and canonical base64 (decoding and encoding again gives the same text, so
    /// restoring the bytes restores the exact original text). Whitespace, a non-zero padding tail or a short value stay inline.
    /// </summary>
    public static bool IsExternalizable( string text )
    {
        return text != null && text.Length >= BlobLimits.MinExternalChars && IsCanonicalBase64( text ) && DecodedLength( text ) <= BlobLimits.MaxBlobBytes;
    }

    /// <summary>
    /// Canonical base64 without decoding: a length that is a multiple of four, only alphabet characters, at most two trailing '=', and the
    /// unused low bits of the last data character zero (the only non-canonical form a decoder accepts besides whitespace).
    /// </summary>
    public static bool IsCanonicalBase64( string text )
    {
        int length = text.Length;
        if ( length == 0 || length % 4 != 0 ) return false;
        int padding = text[length - 1] == '=' ? ( text[length - 2] == '=' ? 2 : 1 ) : 0;
        int data = length - padding;
        for ( int i = 0; i < data; i++ )
        {
            if ( Value( text[i] ) < 0 ) return false;
        }
        if ( padding == 1 && ( Value( text[length - 2] ) & 0x03 ) != 0 ) return false;
        if ( padding == 2 && ( Value( text[length - 3] ) & 0x0F ) != 0 ) return false;
        return true;
    }

    private static int Value( char c )
    {
        if ( c >= 'A' && c <= 'Z' ) return c - 'A';
        if ( c >= 'a' && c <= 'z' ) return c - 'a' + 26;
        if ( c >= '0' && c <= '9' ) return c - '0' + 52;
        if ( c == '+' ) return 62;
        if ( c == '/' ) return 63;
        return -1;
    }

    private static long DecodedLength( string text )
    {
        int padding = text[text.Length - 1] == '=' ? ( text[text.Length - 2] == '=' ? 2 : 1 ) : 0;
        return (long)text.Length / 4 * 3 - padding;
    }

    /// <summary>
    /// Replaces every externalizable text value in <paramref name="node"/> (in place) with its placeholder and hands the decoded bytes to
    /// <paramref name="sink"/>. A value that would be externalized but decodes to more than 32 MiB stays inline and its path is added to
    /// <paramref name="refused"/>; the structural rules then refuse the object with a notice. Returns how many values were replaced.
    /// </summary>
    public static int Externalize( JsonNode node, Action<string, byte[]> sink, List<string> refused, IReadOnlyDictionary<string, string> aliases = null )
    {
        return Externalize( node, sink, refused, aliases, "" );
    }

    private static int Externalize( JsonNode node, Action<string, byte[]> sink, List<string> refused, IReadOnlyDictionary<string, string> aliases, string path )
    {
        int replaced = 0;
        if ( node is JsonObject obj )
        {
            foreach ( string key in obj.Select( x => x.Key ).ToList() )
            {
                JsonNode child = obj[key];
                string childPath = path.Length == 0 ? key : path + "." + key;
                if ( TryExternalize( child, sink, refused, aliases, childPath, out string placeholder ) )
                {
                    obj[key] = JsonValue.Create( placeholder );
                    replaced++;
                }
                else
                {
                    replaced += Externalize( child, sink, refused, aliases, childPath );
                }
            }
        }
        else if ( node is JsonArray array )
        {
            for ( int i = 0; i < array.Count; i++ )
            {
                JsonNode child = array[i];
                string childPath = path + "[" + i + "]";
                if ( TryExternalize( child, sink, refused, aliases, childPath, out string placeholder ) )
                {
                    array[i] = JsonValue.Create( placeholder );
                    replaced++;
                }
                else
                {
                    replaced += Externalize( child, sink, refused, aliases, childPath );
                }
            }
        }
        return replaced;
    }

    // `aliases` maps the hash of the engine's re-encoding of a mesh to the hash it was received as: the engine does not promise that
    // loading a mesh and saving it again gives the same bytes, and without this an editor that applied a mesh would read its own
    // re-encoding as a new edit and send it back, and the two editors would trade the same mesh forever.
    private static bool TryExternalize( JsonNode node, Action<string, byte[]> sink, List<string> refused, IReadOnlyDictionary<string, string> aliases, string path, out string placeholder )
    {
        placeholder = null;
        if ( node is not JsonValue value || !value.TryGetValue( out string text ) || text.Length < BlobLimits.MinExternalChars ) return false;
        if ( !IsCanonicalBase64( text ) ) return false;
        if ( DecodedLength( text ) > BlobLimits.MaxBlobBytes )
        {
            refused?.Add( path );
            return false;
        }
        byte[] bytes = Convert.FromBase64String( text );
        string hash = BlobStore.HashOf( bytes );
        sink( hash, bytes );
        placeholder = Placeholder( aliases != null && aliases.TryGetValue( hash, out string original ) ? original : hash );
        return true;
    }

    /// <summary>The distinct blob hashes a state refers to, in the order they are met.</summary>
    public static List<string> Referenced( JsonNode node )
    {
        List<string> found = new List<string>();
        Visit( node, text =>
        {
            if ( TryParsePlaceholder( text, out string hash ) && !found.Contains( hash ) ) found.Add( hash );
            return null;
        } );
        return found;
    }

    /// <summary>True when any value inside <paramref name="node"/> is a placeholder: the merge names such an object's clash "Mesh geometry".</summary>
    public static bool ContainsPlaceholder( JsonNode node )
    {
        bool found = false;
        Visit( node, text =>
        {
            found |= TryParsePlaceholder( text, out _ );
            return null;
        } );
        return found;
    }

    /// <summary>
    /// A copy of <paramref name="data"/> with every placeholder replaced by the exact original text, ready for the engine. Null, with
    /// <paramref name="missing"/> naming what could not be resolved, when any geometry is absent from <paramref name="store"/> or a value
    /// carries the placeholder prefix in a form that is not a valid name; the object is then not applied at all, never half-way. The input is not modified.
    /// </summary>
    public static JsonObject Internalize( JsonObject data, BlobStore store, out List<string> missing )
    {
        missing = new List<string>();
        JsonObject copy = (JsonObject)data.DeepClone();
        List<string> absent = missing;
        Visit( copy, text =>
        {
            if ( TryParsePlaceholder( text, out string hash ) )
            {
                if ( !store.TryGet( hash, out byte[] bytes ) )
                {
                    if ( !absent.Contains( hash ) ) absent.Add( hash );
                    return null;
                }
                return Convert.ToBase64String( bytes );
            }
            if ( text.StartsWith( PlaceholderPrefix, StringComparison.Ordinal ) )
            {
                if ( !absent.Contains( text ) ) absent.Add( text.Length <= 120 ? text : text.Substring( 0, 120 ) );
            }
            return null;
        } );
        return missing.Count == 0 ? copy : null;
    }

    // Walks every text value; a non-null return from `replace` becomes the new value.
    private static void Visit( JsonNode node, Func<string, string> replace )
    {
        if ( node is JsonObject obj )
        {
            foreach ( string key in obj.Select( x => x.Key ).ToList() )
            {
                JsonNode child = obj[key];
                if ( child is JsonValue value && value.TryGetValue( out string text ) )
                {
                    string changed = replace( text );
                    if ( changed != null ) obj[key] = JsonValue.Create( changed );
                }
                else
                {
                    Visit( child, replace );
                }
            }
        }
        else if ( node is JsonArray array )
        {
            for ( int i = 0; i < array.Count; i++ )
            {
                JsonNode child = array[i];
                if ( child is JsonValue value && value.TryGetValue( out string text ) )
                {
                    string changed = replace( text );
                    if ( changed != null ) array[i] = JsonValue.Create( changed );
                }
                else
                {
                    Visit( child, replace );
                }
            }
        }
    }
}

/// <summary>
/// At most one new geometry version per object per 500 ms. A vertex drag changes the geometry on every scan; sending each would stream a
/// whole new blob twenty times a second. Between versions the object keeps its last confirmed state (the caller substitutes it), and the
/// latest geometry goes out once the interval has passed, so the drag ends on the final shape.
/// </summary>
public sealed class BlobVersionLimiter
{
    private readonly Func<DateTime> clock;

    private readonly Dictionary<string, (string Key, DateTime At)> sent = new( StringComparer.Ordinal );

    public BlobVersionLimiter( Func<DateTime> clock )
    {
        this.clock = clock;
    }

    /// <summary>True when the object's geometry may be captured now. An object without geometry is always admitted and forgotten.</summary>
    public bool Admit( string objectId, IReadOnlyCollection<string> hashes )
    {
        if ( hashes == null || hashes.Count == 0 )
        {
            sent.Remove( objectId );
            return true;
        }
        string key = string.Join( ",", hashes.OrderBy( x => x, StringComparer.Ordinal ) );
        DateTime now = clock();
        if ( sent.TryGetValue( objectId, out var last ) )
        {
            // The version already sent is never held: re-reading it changes nothing.
            if ( last.Key == key ) return true;
            if ( ( now - last.At ).TotalMilliseconds < BlobLimits.VersionIntervalMs ) return false;
        }
        sent[objectId] = (key, now);
        return true;
    }

    public void Forget( string objectId )
    {
        sent.Remove( objectId );
    }
}
