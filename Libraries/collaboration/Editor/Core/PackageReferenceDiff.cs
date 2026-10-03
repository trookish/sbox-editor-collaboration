using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TeamCreate;

/// <summary>
/// Which asset-library packages the host's project lists that a guest's does not. A project names its packages in the <c>.sbproj</c>
/// (<c>PackageReferences</c> and <c>EditorReferences</c>); that file is project configuration the guest runs, so it is never transferred.
/// Only the validated package identifiers cross the wire, and the guest is told, never installed for: adding a package changes what code
/// and content the guest's machine loads, which is the person's decision. Everything here is pure and total: malformed input yields an
/// empty result instead of an exception, because a project file this library cannot read must not interfere with joining.
/// </summary>
public static class PackageReferenceDiff
{
    /// <summary>The most identifiers read from one project file, sent in one reply or named in one result.</summary>
    public const int MaxIdents = 50;

    /// <summary>The longest identifier accepted.</summary>
    public const int MaxIdentLength = 100;

    /// <summary>How many names one notice spells out; the remainder is counted.</summary>
    public const int NoticeNames = 5;

    // A project file is a few hundred bytes; anything this large is not one, and parsing it would only spend the host's frame.
    private const int MaxTextChars = 1024 * 1024;

    // Entries examined per list. The result is bounded to MaxIdents, but a hostile list of millions of invalid strings would still be walked.
    private const int MaxEntriesScanned = 2000;

    private static readonly string[] ReferenceLists = { "PackageReferences", "EditorReferences" };

    /// <summary>True when <paramref name="ident"/> is 1 to 100 characters of A-Z, a-z, 0-9, '.', '_' and '-'. Written as a loop: a regular expression's <c>$</c> would also accept a trailing line break.</summary>
    public static bool IsValidIdent( string ident )
    {
        if ( ident == null || ident.Length == 0 || ident.Length > MaxIdentLength ) return false;
        foreach ( char c in ident )
        {
            bool ok = ( c >= 'a' && c <= 'z' ) || ( c >= 'A' && c <= 'Z' ) || ( c >= '0' && c <= '9' ) || c == '.' || c == '_' || c == '-';
            if ( !ok ) return false;
        }
        return true;
    }

    /// <summary>The valid package identifiers a project file lists, deduplicated, in ordinal order, at most <see cref="MaxIdents"/>. Empty for text that is not a project file.</summary>
    public static List<string> Idents( string sbprojText )
    {
        var found = new HashSet<string>( StringComparer.Ordinal );
        if ( string.IsNullOrWhiteSpace( sbprojText ) || sbprojText.Length > MaxTextChars ) return new List<string>();
        try
        {
            using var document = JsonDocument.Parse( sbprojText, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 } );
            if ( document.RootElement.ValueKind != JsonValueKind.Object ) return new List<string>();
            foreach ( string name in ReferenceLists )
            {
                if ( !document.RootElement.TryGetProperty( name, out var list ) || list.ValueKind != JsonValueKind.Array ) continue;
                int scanned = 0;
                foreach ( var item in list.EnumerateArray() )
                {
                    if ( ++scanned > MaxEntriesScanned ) break;
                    if ( item.ValueKind != JsonValueKind.String ) continue;
                    string ident = item.GetString();
                    if ( IsValidIdent( ident ) ) found.Add( ident );
                }
            }
        }
        catch ( Exception ex ) when ( ex is JsonException or InvalidOperationException or ArgumentException )
        {
            return new List<string>();
        }
        return found.OrderBy( x => x, StringComparer.Ordinal ).Take( MaxIdents ).ToList();
    }

    /// <summary>The identifiers listed by the host's project file that the guest's does not list.</summary>
    public static List<string> Missing( string hostSbprojText, string guestSbprojText ) => Missing( Idents( hostSbprojText ), guestSbprojText );

    /// <summary>
    /// The same, with the host's identifiers already extracted (what arrives on the wire). Each is validated again, because a peer wrote
    /// them. Identifiers match without regard to letter case: the package system treats them so, and reporting "Cyn.Zebra" as missing
    /// beside a listed "cyn.zebra" would be a false alarm.
    /// </summary>
    public static List<string> Missing( IEnumerable<string> hostIdents, string guestSbprojText )
    {
        var have = new HashSet<string>( Idents( guestSbprojText ), StringComparer.OrdinalIgnoreCase );
        var missing = new HashSet<string>( StringComparer.Ordinal );
        foreach ( string ident in hostIdents ?? Enumerable.Empty<string>() )
        {
            if ( IsValidIdent( ident ) && !have.Contains( ident ) ) missing.Add( ident );
            if ( missing.Count >= MaxIdents ) break;
        }
        return missing.OrderBy( x => x, StringComparer.Ordinal ).Take( MaxIdents ).ToList();
    }

    /// <summary>The one notice for the guest, or null when nothing is missing. One package gets the exact sentence; several are named (at most five) in the same sentence.</summary>
    public static string Notice( IReadOnlyList<string> missing )
    {
        if ( missing == null || missing.Count == 0 ) return null;
        const string Rest = "which your project does not list. Add {0} in the Library Manager or through Git; scenes may show {1} as missing until then.";
        if ( missing.Count == 1 ) return "The host's project uses the package \"" + missing[0] + "\" " + string.Format( Rest, "it", "it" );
        var named = missing.Take( NoticeNames ).Select( x => "\"" + x + "\"" ).ToList();
        string list = missing.Count <= NoticeNames
            ? string.Join( ", ", named.Take( named.Count - 1 ) ) + " and " + named[named.Count - 1]
            : string.Join( ", ", named ) + " and " + ( missing.Count - NoticeNames ) + " more";
        return "The host's project uses the packages " + list + " " + string.Format( Rest, "them", "them" );
    }
}
