using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TeamCreate;

/// <summary>
/// The fixed limits of a consented code transfer. They bound what one offer may ask a guest to accept, so a hostile or careless host
/// cannot make a guest review (or the library write) an unbounded amount of code.
/// </summary>
public static class CodeTransferLimits
{
    /// <summary>Advertised by a host on welcome. A guest sends code-index and code-consent only to a host that holds it: an older host answers an unknown message kind with a fatal error.</summary>
    public const string Feature = "code-1";

    public const int MaxFiles = 2000;

    public const long MaxTotalBytes = 64L * 1024 * 1024;

    public const long MaxFileBytes = 4L * 1024 * 1024;
}

/// <summary>
/// What may ever travel as code. Code is the one kind of project file that runs on the receiver's machine, so the set is closed and small:
/// source and project settings under the project's own folders. The installed libraries (version pinned, installed from packages), Git's
/// folder, the library's own working folder and build output are excluded entirely: they are never listed, requested or written.
/// </summary>
public static class CodeTransferRules
{
    // Compared per folder name, without regard to case, wherever the folder sits in the path (a build folder under Code/ is still build output).
    private static readonly string[] ExcludedAnywhere = { ".git", ".collaboration", "obj", "bin", ".sbox" };

    /// <summary>True for a path that must never be transferred as code: anything under Libraries (first folder), and any path through .git, .collaboration, obj, bin or .sbox.</summary>
    public static bool IsExcluded( string path )
    {
        if ( string.IsNullOrWhiteSpace( path ) ) return false;
        string[] segments = path.Replace( '\\', '/' ).Split( '/' );
        if ( segments.Length > 1 && string.Equals( segments[0], "Libraries", StringComparison.OrdinalIgnoreCase ) ) return true;
        return segments.Take( segments.Length - 1 ).Any( s => ExcludedAnywhere.Any( x => string.Equals( x, s, StringComparison.OrdinalIgnoreCase ) ) );
    }

    /// <summary>
    /// True when <paramref name="path"/> is code this library may transfer after consent: a safe relative path (FilePathRules) under Code, Editor,
    /// ProjectSettings or Assets that JoinReadiness classifies as code, that the project manifest covers, and that no exclusion names.
    /// The project descriptor and *.csproj are classified as code but are not transferable: the first names the guest's own project, the second is generated.
    /// </summary>
    public static bool IsTransferable( string path )
    {
        if ( string.IsNullOrWhiteSpace( path ) ) return false;
        if ( !FilePathRules.TryNormalizeCode( path, out string normalized, out _ ) ) return false;
        if ( JoinReadiness.Classify( normalized ) != ProjectFileKind.Code ) return false;
        return !IsExcluded( normalized ) && ProjectManifest.Covers( normalized );
    }

    /// <summary>A code file never contains a NUL byte; one that does is a binary or damaged file in a code path and is refused.</summary>
    public static bool HasBinaryContent( byte[] content )
    {
        return content != null && Array.IndexOf( content, (byte)0 ) >= 0;
    }
}

/// <summary>Whether this machine may accept code from the host: only a host whose identity was verified, or when the person ticked the existing setting for unverified peers.</summary>
public static class CodeTrust
{
    public const string UnverifiedMessage = "The host's identity could not be verified, so code from this host cannot be received.";

    public static bool Permits( bool hostIdentityVerified, bool allowUnverified )
    {
        return hostIdentityVerified || allowUnverified;
    }
}

public enum CodeFileStatus
{
    /// <summary>The guest has no such file.</summary>
    New,

    /// <summary>The guest has the file with other content.</summary>
    Differs,
}

public sealed record CodeOfferFile( string Path, string Sha256, long Size, CodeFileStatus Status );

public enum CodeTransferState
{
    /// <summary>Nothing to offer, or the host cannot transfer code.</summary>
    None,

    /// <summary>An offer is shown and waits for the person.</summary>
    Offered,

    /// <summary>The person agreed; the files are being received.</summary>
    Receiving,

    /// <summary>The last agreed set was received; a later change by the host starts a new offer.</summary>
    Received,

    /// <summary>The person chose "Not now" for the set last shown.</summary>
    Declined,
}

/// <summary>
/// The code a host has that a guest lacks or holds in another version, as the guest is shown it: every file with its size and status, the total,
/// and what the limits left out. The guest consents to exactly this list: <see cref="Hashes"/> is what the consent names, and
/// <see cref="Fingerprint"/> identifies the set, so a changed set is a different offer. Built from the host's index, which is untrusted.
/// </summary>
public sealed class CodeOffer
{
    private const int MaxNamedFiles = 5;

    private const int MaxRejectedNotices = 5;

    private CodeOffer( List<CodeOfferFile> files, int excessCount, string excessNotice, List<string> rejected )
    {
        Files = files;
        ExcessCount = excessCount;
        ExcessNotice = excessNotice;
        Rejected = rejected;
        TotalBytes = files.Sum( x => x.Size );
        Hashes = files.Select( x => x.Sha256 ).Distinct( StringComparer.Ordinal ).OrderBy( x => x, StringComparer.Ordinal ).ToList();
        var text = new StringBuilder();
        foreach ( var file in files ) text.Append( file.Path ).Append( '\n' ).Append( file.Sha256 ).Append( '\n' );
        Fingerprint = Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( text.ToString() ) ) ).ToLowerInvariant();
    }

    public IReadOnlyList<CodeOfferFile> Files { get; }

    public long TotalBytes { get; }

    /// <summary>The distinct SHA-256 digests of the listed files, ordinal order: exactly what a consent names.</summary>
    public IReadOnlyList<string> Hashes { get; }

    public string Fingerprint { get; }

    /// <summary>How many differing code files the limits left out of the offer.</summary>
    public int ExcessCount { get; }

    /// <summary>The formal sentence that names what was left out and why, or null.</summary>
    public string ExcessNotice { get; }

    /// <summary>Single-line notices about host entries that were malformed and so not offered (at most six).</summary>
    public IReadOnlyList<string> Rejected { get; }

    /// <summary>"Code changed on &lt;host&gt;: N file(s)." when this offer replaces one the person had already seen; set by the client.</summary>
    public string ChangeNotice { get; internal set; }

    public bool CanOffer => Files.Count > 0;

    /// <summary>Null when the host has no transferable code the guest lacks and nothing was left out or rejected.</summary>
    public static CodeOffer Build( IEnumerable<FileIndexEntry> host, IEnumerable<FileIndexEntry> guest )
    {
        var local = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
        foreach ( var entry in guest ?? Enumerable.Empty<FileIndexEntry>() )
        {
            if ( entry?.Path != null ) local.TryAdd( entry.Path, entry.Sha256 ?? "" );
        }
        var rejected = new List<string>();
        int rejectedCount = 0;
        void Reject( string path )
        {
            rejectedCount++;
            if ( rejected.Count < MaxRejectedNotices ) rejected.Add( $"The host listed an invalid code file entry \"{Show( path )}\"; it was not offered." );
        }
        var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
        // A path the host lists more than once is ambiguous (which digest?): none of its entries is offered.
        var duplicated = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
        foreach ( var entry in host ?? Enumerable.Empty<FileIndexEntry>() )
        {
            if ( entry?.Path != null && !seen.Add( entry.Path ) ) duplicated.Add( entry.Path );
        }
        seen.Clear();
        var candidates = new List<CodeOfferFile>();
        foreach ( var entry in host ?? Enumerable.Empty<FileIndexEntry>() )
        {
            if ( entry == null || string.IsNullOrWhiteSpace( entry.Path ) || entry.Path.Length > 1024 || entry.Path.Any( char.IsControl ) )
            {
                Reject( entry?.Path );
                continue;
            }
            if ( !CodeTransferRules.IsTransferable( entry.Path ) ) continue;
            if ( duplicated.Contains( entry.Path ) )
            {
                if ( seen.Add( entry.Path ) ) Reject( entry.Path );
                continue;
            }
            if ( entry.Size < 0 || !FileSyncReceiver.ValidHash( entry.Sha256 ) )
            {
                Reject( entry.Path );
                continue;
            }
            bool have = local.TryGetValue( entry.Path, out string guestHash );
            if ( have && string.Equals( guestHash, entry.Sha256, StringComparison.OrdinalIgnoreCase ) ) continue;
            candidates.Add( new CodeOfferFile( entry.Path, entry.Sha256.ToLowerInvariant(), entry.Size, have ? CodeFileStatus.Differs : CodeFileStatus.New ) );
        }
        candidates.Sort( ( a, b ) => string.CompareOrdinal( a.Path, b.Path ) );

        var files = new List<CodeOfferFile>();
        var excess = new List<string>();
        long total = 0;
        foreach ( var candidate in candidates )
        {
            if ( candidate.Size > CodeTransferLimits.MaxFileBytes || files.Count >= CodeTransferLimits.MaxFiles || total + candidate.Size > CodeTransferLimits.MaxTotalBytes )
            {
                excess.Add( candidate.Path );
                continue;
            }
            files.Add( candidate );
            total += candidate.Size;
        }
        if ( rejectedCount > rejected.Count ) rejected.Add( $"{rejectedCount - rejected.Count} more invalid code file entries from the host were not offered." );
        if ( files.Count == 0 && excess.Count == 0 && rejected.Count == 0 ) return null;
        string notice = null;
        if ( excess.Count > 0 )
        {
            string named = string.Join( ", ", excess.Take( MaxNamedFiles ).Select( Show ) );
            if ( excess.Count > MaxNamedFiles ) named += $" and {excess.Count - MaxNamedFiles} more";
            notice = $"{excess.Count} code file(s) exceed the limits of one transfer (at most 2,000 files, 64 MiB in total and 4 MiB per file) and will not be received: {named}.";
        }
        return new CodeOffer( files, excess.Count, notice, rejected );
    }

    // A path as a sentence may carry it: control characters would fake a second line, and one absurd name must not flood the card.
    internal static string Show( string path )
    {
        if ( path == null ) return "";
        var text = new StringBuilder( Math.Min( path.Length, 120 ) );
        foreach ( char c in path.Take( 120 ) ) text.Append( char.IsControl( c ) ? '?' : c );
        if ( path.Length > 120 ) text.Append( "..." );
        return text.ToString();
    }
}

/// <summary>The words of the code card and of its notices, kept in the engine-free core so they are tested.</summary>
public static class CodeOfferText
{
    /// <summary>The host's name as one short line, or "the host" when it has none.</summary>
    public static string HostLabel( string hostName )
    {
        if ( string.IsNullOrWhiteSpace( hostName ) ) return "the host";
        string flat = new string( hostName.Select( c => char.IsControl( c ) ? ' ' : c ).ToArray() ).Trim();
        if ( flat.Length > 64 ) flat = flat.Substring( 0, 64 );
        return flat.Length == 0 ? "the host" : flat;
    }

    public static string Button( string hostName ) => "Receive code from " + HostLabel( hostName );

    public static string ChangedNotice( string hostName, int files ) => $"Code changed on {HostLabel( hostName )}: {files} file(s).";

    public static string Size( long bytes )
    {
        if ( bytes < 1024 ) return bytes + " B";
        if ( bytes < 1024 * 1024 ) return ( bytes / 1024.0 ).ToString( "0.#", System.Globalization.CultureInfo.InvariantCulture ) + " KiB";
        return ( bytes / 1048576.0 ).ToString( "0.#", System.Globalization.CultureInfo.InvariantCulture ) + " MiB";
    }

    public static string Headline( CodeOffer offer, string hostName )
    {
        int n = offer.Files.Count;
        return $"{HostLabel( hostName )} has {n} code file{( n == 1 ? "" : "s" )} for this project that you do not have in this version ({Size( offer.TotalBytes )} in total). Code runs on this computer. Nothing is received until you agree.";
    }

    public static string FileLine( CodeOfferFile file ) => $"{( file.Status == CodeFileStatus.New ? "new" : "differs" )}  {CodeOffer.Show( file.Path )}  ({Size( file.Size )})";

    /// <summary>The sentence reported once the agreed set has been written.</summary>
    public static string ReceivedNotice( int received, int total, string recoveryFolder )
    {
        if ( received == total )
        {
            string text = $"Code received ({received} file{( received == 1 ? "" : "s" )}). The editor will compile it now.";
            return recoveryFolder == null ? text : text + $" Previous versions are in {recoveryFolder}.";
        }
        string partial = $"Code received for {received} of {total} files; the others could not be applied and are listed above. The editor will compile what was received now.";
        return recoveryFolder == null ? partial : partial + $" Previous versions are in {recoveryFolder}.";
    }
}
