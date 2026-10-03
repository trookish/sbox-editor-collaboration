using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace TeamCreate;

/// <summary>
/// The fixed limits of project file synchronization. They are part of the protocol: a sender and a receiver must agree on the chunk size
/// (the assembler refuses a chunk that does not start on a chunk boundary) and on the window (the sender stops at eight unacknowledged chunks).
/// </summary>
public static class FileSyncLimits
{
    /// <summary>Advertised by the host on welcome. A guest starts file synchronization only when its host holds this feature.</summary>
    public const string Feature = "files-1";

    /// <summary>256 KiB: about 350 KB as base64 JSON, far below Wire.MaxBytes, and small enough that one chunk never delays a live edit noticeably.</summary>
    public const int ChunkSize = 256 * 1024;

    /// <summary>
    /// At most eight chunks per receiver are on the wire unacknowledged. The rest wait in the sender's own queue, outside the connection's FIFO
    /// (Wire queues up to 256 frames in order and cannot reorder them), so a live edit or presence packet sent now is queued behind at most eight
    /// chunks instead of behind the remainder of the file, and the receiver's 256-packet receive queue can never be flooded by one file.
    /// </summary>
    public const int Window = 8;

    public const long MaxFileSize = 512L * 1024 * 1024;

    public const int MaxEntries = 50000;

    /// <summary>Index entries per files-index reply page (about 40 KB of JSON per page).</summary>
    public const int IndexPage = 256;
}

/// <summary>Whether a file travels on the automatic asset path or only after the person's consent (code, which runs on the receiver's machine).</summary>
public enum FileTransferKind
{
    Asset,

    Code,
}

/// <summary>
/// One entry of a file index. <see cref="Kind"/> is how a receiver files an entry it has queued (the same assembler, window, hash check, backup and
/// atomic write serve both kinds); it is not read from the wire as a decision: a receiver classifies the path itself, and the host decides
/// from the path alone whether a request needs consent.
/// </summary>
public sealed record FileIndexEntry( string Path, string Sha256, long Size )
{
    [System.Text.Json.Serialization.JsonIgnore]
    public FileTransferKind Kind { get; init; }
}

/// <summary>
/// Builds the file index: one entry for every path ProjectManifest.EnumerateInputs yields, hashed exactly as ProjectManifest hashes it
/// (a .meta file through MetaFileRules.Canonical). Digests are cached by (path, size, last-write time), so re-indexing an unchanged
/// project reads no file contents. A file above the transfer limit is listed with an empty digest instead of being read: it is never
/// transferred, and hashing half a gigabyte on the editor's frame would stall it.
/// </summary>
public sealed class FileSyncIndex
{
    private readonly string root;

    private readonly Dictionary<string, (long Size, long Stamp, string Hash)> cache = new( StringComparer.OrdinalIgnoreCase );

    public FileSyncIndex( string root )
    {
        this.root = root;
    }

    /// <summary>How many files were actually read and hashed; the cache tests count it.</summary>
    public int HashComputations { get; private set; }

    /// <summary>Bytes read for hashing so far; the editor's poll bounds its per-frame work with it.</summary>
    public long HashedBytes { get; private set; }

    /// <summary>True when the last enumeration could not list a folder: a path missing from it is then unknown, not deleted.</summary>
    public bool Incomplete { get; private set; }

    /// <summary>The project-relative paths the manifest covers, refusing a project above the entry limit with a message that says so.</summary>
    public List<string> Paths()
    {
        var paths = ProjectManifest.EnumerateInputs( root, out bool incomplete ).ToList();
        Incomplete = incomplete;
        if ( paths.Count > FileSyncLimits.MaxEntries )
        {
            throw new InvalidOperationException( $"The project has {paths.Count} files; file synchronization supports at most {FileSyncLimits.MaxEntries}. Synchronize this project through Git." );
        }
        return paths;
    }

    public List<FileIndexEntry> Build()
    {
        var entries = new List<FileIndexEntry>();
        foreach ( string path in Paths() )
        {
            var entry = Entry( path );
            if ( entry != null ) entries.Add( entry );
        }
        return entries;
    }

    /// <summary>The entry of one file, or null when it vanished between enumeration and reading (it is then simply not in this index).</summary>
    public FileIndexEntry Entry( string path )
    {
        var info = new FileInfo( Path.Combine( root, path.Replace( '/', Path.DirectorySeparatorChar ) ) );
        if ( !info.Exists ) return null;
        if ( info.Length > FileSyncLimits.MaxFileSize ) return new FileIndexEntry( path, "", info.Length );
        long stamp = info.LastWriteTimeUtc.Ticks;
        if ( cache.TryGetValue( path, out var cached ) && cached.Size == info.Length && cached.Stamp == stamp )
        {
            return new FileIndexEntry( path, cached.Hash, cached.Size );
        }
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes( info.FullName );
        }
        catch ( FileNotFoundException )
        {
            return null;
        }
        catch ( DirectoryNotFoundException )
        {
            return null;
        }
        string hash = Hash( path, bytes );
        HashComputations++;
        HashedBytes += bytes.LongLength;
        // The stamp is the one read before the content: a write that lands during the read moves the stamp again and is re-hashed next time.
        cache[path] = (bytes.LongLength, stamp, hash);
        return new FileIndexEntry( path, hash, bytes.LongLength );
    }

    public void Invalidate( string path )
    {
        if ( path != null ) cache.Remove( path );
    }

    public static string Hash( string path, byte[] bytes )
    {
        return Convert.ToHexString( SHA256.HashData( MetaFileRules.Canonical( path, bytes ) ) ).ToLowerInvariant();
    }
}

/// <summary>What a joining guest does with the host's index: which files to fetch and what to tell the person about the rest.</summary>
public sealed record FileJoinPlanResult( List<FileIndexEntry> Fetch, string CodeNotice, List<string> Notices );

public static class FileJoinPlan
{
    /// <summary>
    /// Compares the two indexes through JoinReadiness.Evaluate. Missing and changed assets are fetched, except a scene open in the guest's
    /// editor (the live scene snapshot owns it) and a file above the transfer limit (named in a notice). Code differences are never fetched;
    /// they produce one notice. Guest-only files are left alone. A host entry that is malformed is refused by name.
    /// </summary>
    public static FileJoinPlanResult Plan( IReadOnlyList<FileIndexEntry> host, IReadOnlyList<FileIndexEntry> guest, IEnumerable<string> openScenes )
    {
        if ( host.Count > FileSyncLimits.MaxEntries )
        {
            throw new InvalidOperationException( $"The host's project has more than {FileSyncLimits.MaxEntries} files; file synchronization cannot continue. Synchronize this project through Git." );
        }
        var notices = new List<string>();
        var remote = new Dictionary<string, FileIndexEntry>( StringComparer.OrdinalIgnoreCase );
        foreach ( var entry in host )
        {
            // Only well-formedness here: code entries are compared, never written, and every asset is validated again by FileSyncReceiver
            // (FilePathRules) before it is requested.
            if ( string.IsNullOrWhiteSpace( entry?.Path ) || entry.Path.Length > 1024 || entry.Path.Any( char.IsControl ) || entry.Size < 0 || remote.ContainsKey( entry.Path ) )
            {
                notices.Add( $"The host listed an invalid project file entry \"{Bounded( entry?.Path )}\"; it was not transferred." );
                continue;
            }
            remote[entry.Path] = entry;
        }
        var local = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
        foreach ( var entry in guest ) local.TryAdd( entry.Path, entry.Sha256 ?? "" );
        var readiness = JoinReadiness.Evaluate( Array.Empty<string>(), local, remote.ToDictionary( x => x.Key, x => x.Value.Sha256 ?? "", StringComparer.OrdinalIgnoreCase ) );
        var open = new HashSet<string>( openScenes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase );
        var differing = readiness.Differences.Where( x => x.Kind != FileDifferenceKind.Extra ).ToList();
        var code = differing.Where( x => x.FileKind == ProjectFileKind.Code ).Select( x => x.Path ).ToList();
        string codeNotice = code.Count == 0 ? null
            : $"Your code differs from the host's in {code.Count} file(s), for example {string.Join( ", ", code.Take( 3 ) )}. Update it through Git. Scenes and assets are synchronized automatically.";
        var fetch = new List<FileIndexEntry>();
        foreach ( var difference in differing.Where( x => x.FileKind == ProjectFileKind.Asset ) )
        {
            var entry = remote[difference.Path];
            if ( open.Contains( entry.Path ) ) continue;
            if ( entry.Size > FileSyncLimits.MaxFileSize )
            {
                notices.Add( TooLarge( entry.Path ) );
                continue;
            }
            if ( !FileSyncReceiver.ValidHash( entry.Sha256 ) )
            {
                notices.Add( $"The host listed \"{Bounded( entry.Path )}\" without a valid SHA-256 digest; it was not transferred." );
                continue;
            }
            fetch.Add( entry );
        }
        return new FileJoinPlanResult( fetch, codeNotice, notices );
    }

    public static string TooLarge( string path )
    {
        return $"The file \"{Bounded( path )}\" is larger than 512 MiB and is not synchronized. Copy it through Git or Asset Party.";
    }

    internal static string Bounded( string path )
    {
        if ( path == null ) return "";
        string flat = path.Replace( '\r', ' ' ).Replace( '\n', ' ' );
        return flat.Length <= 200 ? flat : flat.Substring( 0, 200 ) + "…";
    }
}

/// <summary>Credits for chunks in flight to one receiver: a chunk is sent only with a credit, and the receiver's file-ack returns it.</summary>
public sealed class FileChunkWindow
{
    public int InFlight { get; private set; }

    public bool TryAcquire()
    {
        if ( InFlight >= FileSyncLimits.Window ) return false;
        InFlight++;
        return true;
    }

    public void Release()
    {
        if ( InFlight > 0 ) InFlight--;
    }
}

/// <summary>
/// Assembles one incoming file from untrusted chunks into .collaboration/incoming/&lt;sha&gt;.part. A chunk must start on a chunk boundary,
/// be a whole chunk unless it is the last one, and lie within the declared size; anything else is refused without writing. A duplicate
/// chunk is accepted and ignored, so chunks may arrive in any order and be resent. The declared size is checked before the part file is
/// created, so a 600 MiB declaration writes nothing.
/// </summary>
public sealed class FileChunkAssembler : IDisposable
{
    private readonly string part;

    private readonly HashSet<long> received = new();

    public FileChunkAssembler( string root, string path, string hash, long total, string partSuffix = null, FileTransferKind kind = FileTransferKind.Asset )
    {
        if ( !FileSyncReceiver.ValidPath( root, path, kind, out _, out string error ) ) throw new InvalidOperationException( error );
        if ( kind == FileTransferKind.Code && ( total < 0 || total > CodeTransferLimits.MaxFileBytes ) ) throw new InvalidOperationException( $"The code file \"{FileJoinPlan.Bounded( path )}\" is larger than 4 MiB and is not received." );
        if ( total < 0 || total > FileSyncLimits.MaxFileSize ) throw new InvalidOperationException( FileJoinPlan.TooLarge( path ) );
        if ( !FileSyncReceiver.ValidHash( hash ) ) throw new InvalidOperationException( $"The file \"{FileJoinPlan.Bounded( path )}\" was offered without a valid SHA-256 digest." );
        Path = path;
        Kind = kind;
        Sha256 = hash.ToLowerInvariant();
        Total = total;
        string incoming = System.IO.Path.Combine( root, ".collaboration", "incoming" );
        Directory.CreateDirectory( incoming );
        part = System.IO.Path.Combine( incoming, Sha256 + ( partSuffix ?? "" ) + ".part" );
        using var file = new FileStream( part, FileMode.Create, FileAccess.Write, FileShare.None );
        file.SetLength( total );
    }

    public string Path { get; }

    public FileTransferKind Kind { get; }

    public string Sha256 { get; }

    public long Total { get; }

    public long Received { get; private set; }

    public bool IsComplete => Received == Total;

    /// <summary>
    /// The shape rule for one chunk of a transfer of <paramref name="total"/> bytes: it starts on a chunk boundary, lies within the declared size
    /// and is a whole chunk unless it is the last. Shared with <see cref="BlobExchange"/>, which assembles geometry in memory by the same rule.
    /// </summary>
    public static bool IsValidChunk( long total, long offset, int length )
    {
        if ( offset < 0 || offset > total || length > FileSyncLimits.ChunkSize || length > total - offset ) return false;
        if ( offset % FileSyncLimits.ChunkSize != 0 ) return false;
        if ( length != FileSyncLimits.ChunkSize && offset + length != total ) return false;
        return true;
    }

    public bool Add( long offset, byte[] data )
    {
        data ??= Array.Empty<byte>();
        if ( !IsValidChunk( Total, offset, data.Length ) ) return false;
        if ( !received.Add( offset ) ) return true;
        using ( var file = new FileStream( part, FileMode.Open, FileAccess.Write, FileShare.None ) )
        {
            file.Position = offset;
            file.Write( data );
        }
        Received += data.Length;
        return true;
    }

    /// <summary>
    /// Verifies the assembled file and hands it to the receiver's write procedure. Null on success (recovery then names the backup of
    /// the replaced file, or null when nothing was replaced); otherwise the formal reason, and nothing was written.
    /// </summary>
    public string Complete( FileSyncReceiver receiver, out string recovery )
    {
        recovery = null;
        if ( !IsComplete ) return $"The file \"{FileJoinPlan.Bounded( Path )}\" is incomplete.";
        return receiver.AcceptStaged( Path, Sha256, part, out recovery, Kind );
    }

    public void Dispose()
    {
        try
        {
            if ( File.Exists( part ) ) File.Delete( part );
        }
        catch ( IOException )
        {
        }
        catch ( UnauthorizedAccessException )
        {
        }
    }
}

/// <summary>
/// The only code that writes or deletes project files on behalf of another machine. Every path goes through FilePathRules (normalisation,
/// traversal, drive, device names, junctions) and must classify as an asset: code and project settings are never written. A file that
/// exists and differs is first copied to .collaboration/recovery/files-&lt;yyyyMMdd-HHmmss&gt;/&lt;path&gt;; the new content is written to a
/// temporary file in the target's own folder and renamed over the target, so the target is never half-written.
/// </summary>
public sealed class FileSyncReceiver
{
    private readonly string root;

    private readonly Func<DateTime> clock;

    public FileSyncReceiver( string root, Func<DateTime> clock = null )
    {
        this.root = root;
        this.clock = clock ?? ( () => DateTime.UtcNow );
    }

    public static bool ValidHash( string hash )
    {
        return hash != null && hash.Length == 64 && hash.All( x => ( x >= '0' && x <= '9' ) || ( x >= 'a' && x <= 'f' ) || ( x >= 'A' && x <= 'F' ) );
    }

    public static bool ValidPath( string root, string path, out string target, out string error )
    {
        return ValidPath( root, path, FileTransferKind.Asset, out target, out error );
    }

    /// <summary>
    /// The path rules for one kind. An asset path is refused when it classifies as code (the automatic path never writes code). A code path
    /// must be transferable code (<see cref="CodeTransferRules"/>): Libraries, Git, the library's own folder and build output are refused here
    /// as well, so even a consented request cannot reach them, and it is resolved through the same traversal, drive, device-name and junction checks.
    /// </summary>
    public static bool ValidPath( string root, string path, FileTransferKind kind, out string target, out string error )
    {
        target = null;
        if ( kind == FileTransferKind.Code )
        {
            if ( !CodeTransferRules.IsTransferable( path ) )
            {
                error = $"The file \"{FileJoinPlan.Bounded( path )}\" is not code this library transfers (installed libraries, Git, build output and the project file are never transferred) and was refused.";
                return false;
            }
            if ( !FilePathRules.TryResolveUnderCode( root, path, out target, out error ) )
            {
                error = $"The code file path \"{FileJoinPlan.Bounded( path )}\" was refused: {error}";
                target = null;
                return false;
            }
            return true;
        }
        // Classified first, so code is refused with the sentence that says why (FilePathRules would refuse it only as "not under Assets").
        if ( !string.IsNullOrWhiteSpace( path ) && JoinReadiness.Classify( path ) != ProjectFileKind.Asset )
        {
            error = $"The file \"{FileJoinPlan.Bounded( path )}\" is code or project configuration and is not synchronized automatically. Code is received only after you agree to it on the Code card.";
            return false;
        }
        if ( !FilePathRules.TryNormalize( path, out string normalized, out error ) )
        {
            error = $"The project file path \"{FileJoinPlan.Bounded( path )}\" was refused: {error}";
            return false;
        }
        if ( !ProjectManifest.Covers( normalized ) )
        {
            error = $"The path \"{normalized}\" is outside the synchronized project files (Assets, excluding library and build folders) and was refused.";
            return false;
        }
        if ( !FilePathRules.TryResolveUnder( root, normalized, out target, out error ) )
        {
            error = $"The project file path \"{normalized}\" was refused: {error}";
            target = null;
            return false;
        }
        return true;
    }

    /// <summary>Writes complete content received in one piece. Null on success, else the reason; nothing is written on failure.</summary>
    public string Accept( string path, string hash, byte[] bytes, out string recovery )
    {
        recovery = null;
        if ( bytes == null || bytes.LongLength > FileSyncLimits.MaxFileSize ) return FileJoinPlan.TooLarge( path );
        if ( !ValidPath( root, path, out _, out string error ) ) return error;
        string incoming = Path.Combine( root, ".collaboration", "incoming" );
        Directory.CreateDirectory( incoming );
        string part = Path.Combine( incoming, Guid.NewGuid().ToString( "N" ) + ".part" );
        File.WriteAllBytes( part, bytes );
        try
        {
            return AcceptStaged( path, hash, part, out recovery );
        }
        finally
        {
            if ( File.Exists( part ) ) File.Delete( part );
        }
    }

    public string AcceptStaged( string path, string hash, string part, out string recovery, FileTransferKind kind = FileTransferKind.Asset )
    {
        recovery = null;
        if ( !ValidPath( root, path, kind, out string target, out string error ) ) return error;
        if ( !ValidHash( hash ) ) return $"The file \"{path}\" was offered without a valid SHA-256 digest.";
        byte[] content = File.ReadAllBytes( part );
        if ( !string.Equals( FileSyncIndex.Hash( path, content ), hash, StringComparison.OrdinalIgnoreCase ) )
        {
            return $"The file \"{path}\" did not match its SHA-256 digest and was discarded.";
        }
        // Checked after the digest, so a host that merely lied about the content is told apart from one whose code file is binary.
        if ( kind == FileTransferKind.Code && CodeTransferRules.HasBinaryContent( content ) ) return BinaryRefusal( path );
        Directory.CreateDirectory( Path.GetDirectoryName( target )! );
        // Resolved again after creating the folders: a junction planted in the path in between is caught here, before any write.
        if ( !ValidPath( root, path, kind, out target, out error ) ) return error;
        if ( File.Exists( target ) )
        {
            if ( string.Equals( FileSyncIndex.Hash( path, File.ReadAllBytes( target ) ), hash, StringComparison.OrdinalIgnoreCase ) ) return null;
            recovery = Backup( path, target );
        }
        // The ".tmp" extension is one ProjectManifest excludes, so a poll that runs mid-write never lists the temporary file as a project file.
        string temp = Path.Combine( Path.GetDirectoryName( target )!, "." + Path.GetFileName( target ) + "." + Guid.NewGuid().ToString( "N" ) + ".tmp" );
        try
        {
            File.WriteAllBytes( temp, content );
            File.Move( temp, target, true );
        }
        finally
        {
            if ( File.Exists( temp ) ) File.Delete( temp );
        }
        return null;
    }

    /// <summary>The refusal of a code file that holds a NUL byte. A fixed sentence per path, so the client can tell it from a transient digest failure and not retry it.</summary>
    public static string BinaryRefusal( string path )
    {
        return $"The file \"{FileJoinPlan.Bounded( path )}\" contains binary data (a NUL byte), which a code file must not, and was not applied.";
    }

    /// <summary>Backs up, then deletes. Null on success (also when the file is already gone), else the reason.</summary>
    public string Delete( string path, out string recovery )
    {
        recovery = null;
        if ( !ValidPath( root, path, out string target, out string error ) ) return error;
        if ( !File.Exists( target ) ) return null;
        recovery = Backup( path, target );
        File.Delete( target );
        return null;
    }

    // A copy is never overwritten: two replacements of one file within the same second get distinct folders.
    private string Backup( string path, string target )
    {
        string stamp = clock().ToString( "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture );
        string relative = ".collaboration/recovery/files-" + stamp + "/" + path;
        for ( int n = 2; File.Exists( Path.Combine( root, relative.Replace( '/', Path.DirectorySeparatorChar ) ) ); n++ )
        {
            relative = ".collaboration/recovery/files-" + stamp + "-" + n + "/" + path;
        }
        string backup = Path.Combine( root, relative.Replace( '/', Path.DirectorySeparatorChar ) );
        Directory.CreateDirectory( Path.GetDirectoryName( backup )! );
        File.Copy( target, backup );
        return relative;
    }
}
