using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace TeamCreate;

/// <summary>
/// The fixed limits of mapping-tool geometry synchronization (docs/BLOB-SYNC.md). They are part of the protocol: a receiver refuses what
/// exceeds them, so a sender that respected them is never refused for size.
/// </summary>
public static class BlobLimits
{
    /// <summary>Advertised by the host on welcome. A guest sends geometry placeholders only to a host that holds this feature.</summary>
    public const string Feature = "blobs-1";

    /// <summary>The largest geometry (decoded bytes) that is synchronized. The largest mesh in the installed sample scenes is 576 KB.</summary>
    public const int MaxBlobBytes = 32 * 1024 * 1024;

    /// <summary>Geometry that has started to arrive and is not complete yet, summed over one endpoint (every member on a host).</summary>
    public const long MaxPendingBytes = 64L * 1024 * 1024;

    /// <summary>Scene packets one endpoint holds while the geometry they name is on its way.</summary>
    public const int MaxPendingPackets = 32;

    /// <summary>How long a held packet waits for its geometry before it is discarded and the scene is synchronized again.</summary>
    public const int WaitSeconds = 60;

    /// <summary>Hashes one blob-need may name; the remainder of a longer request is ignored.</summary>
    public const int MaxHashesPerRequest = 64;

    /// <summary>
    /// Shortest text value that is moved out of an object's state (4 KiB). Below this the inline text costs less than the placeholder
    /// round trip, and nothing in the engine writes geometry that small except an empty mesh.
    /// </summary>
    public const int MinExternalChars = 4096;

    /// <summary>One new version of an object's geometry per this many milliseconds, so dragging a vertex does not flood the session.</summary>
    public const int VersionIntervalMs = 500;

    public const long MemoryCacheBytes = 256L * 1024 * 1024;

    public const long DiskCacheBytes = 256L * 1024 * 1024;
}

/// <summary>
/// Content-addressed geometry bytes, keyed by the lowercase hex SHA-256 of the bytes. A bounded in-memory cache (least recently used out)
/// sits in front of an optional disk cache in <c>.collaboration/blobs/&lt;sha256&gt;</c>. Nothing is stored under a key its bytes do not
/// hash to, and a key that is not exactly 64 lowercase hex digits never reaches the file system, so a hostile peer cannot name a path.
/// </summary>
public sealed class BlobStore
{
    private sealed class Entry
    {
        public byte[] Bytes;

        public long Touched;
    }

    private readonly object gate = new();

    private readonly Dictionary<string, Entry> memory = new( StringComparer.Ordinal );

    private readonly long memoryCap;

    private long diskCap;

    private string directory;

    private long clock;

    public BlobStore( string root, long memoryCap, long diskCap )
    {
        this.memoryCap = memoryCap;
        if ( root != null ) AttachDisk( root, diskCap );
    }

    public int MemoryCount
    {
        get { lock ( gate ) return memory.Count; }
    }

    public long MemoryBytes { get; private set; }

    public static string HashOf( byte[] bytes )
    {
        return Convert.ToHexString( SHA256.HashData( bytes ) ).ToLowerInvariant();
    }

    public static bool ValidHash( string hash )
    {
        if ( hash == null || hash.Length != 64 ) return false;
        foreach ( char c in hash )
        {
            if ( !( ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ) ) ) return false;
        }
        return true;
    }

    /// <summary>
    /// Starts keeping blobs on disk under <paramref name="root"/>, and writes the ones already in memory, so an object captured before the
    /// session began hosting can still be served to a late joiner. A folder that cannot be created leaves the store memory-only.
    /// </summary>
    public void AttachDisk( string root, long cap )
    {
        string folder;
        try
        {
            folder = Path.Combine( root, ".collaboration", "blobs" );
            Directory.CreateDirectory( folder );
        }
        catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException )
        {
            return;
        }
        lock ( gate )
        {
            directory = folder;
            diskCap = cap;
            foreach ( KeyValuePair<string, Entry> pair in memory ) WriteFile( pair.Key, pair.Value.Bytes );
        }
    }

    /// <summary>Stores <paramref name="bytes"/> under their own hash. Null (and <paramref name="error"/>) when they are empty or over the limit.</summary>
    public string Put( byte[] bytes, out string error )
    {
        error = null;
        if ( bytes == null || bytes.Length == 0 )
        {
            error = "Empty geometry is not stored.";
            return null;
        }
        if ( bytes.Length > BlobLimits.MaxBlobBytes )
        {
            error = TooLarge();
            return null;
        }
        string hash = HashOf( bytes );
        return Store( hash, bytes ) ? hash : null;
    }

    /// <summary>Stores <paramref name="bytes"/> only if they hash to <paramref name="hash"/>; anything else is refused and nothing is written.</summary>
    public bool Put( string hash, byte[] bytes, out string error )
    {
        error = null;
        if ( !ValidHash( hash ) )
        {
            error = "The geometry was offered under a name that is not a SHA-256 digest.";
            return false;
        }
        if ( bytes == null || bytes.Length == 0 )
        {
            error = "Empty geometry is not stored.";
            return false;
        }
        if ( bytes.Length > BlobLimits.MaxBlobBytes )
        {
            error = TooLarge();
            return false;
        }
        if ( HashOf( bytes ) != hash )
        {
            error = "The geometry did not match the digest it was offered under and was discarded.";
            return false;
        }
        return Store( hash, bytes );
    }

    public static string TooLarge() => "The geometry is larger than the 32 MiB collaboration limit and was not synchronized.";

    public bool Has( string hash )
    {
        if ( !ValidHash( hash ) ) return false;
        lock ( gate )
        {
            if ( memory.ContainsKey( hash ) ) return true;
            return directory != null && File.Exists( Path.Combine( directory, hash ) );
        }
    }

    public bool TryGet( string hash, out byte[] bytes )
    {
        bytes = null;
        if ( !ValidHash( hash ) ) return false;
        lock ( gate )
        {
            if ( memory.TryGetValue( hash, out Entry entry ) )
            {
                entry.Touched = ++clock;
                bytes = entry.Bytes;
                return true;
            }
            if ( directory == null ) return false;
            string file = Path.Combine( directory, hash );
            byte[] read;
            try
            {
                if ( !File.Exists( file ) ) return false;
                read = File.ReadAllBytes( file );
            }
            catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException )
            {
                return false;
            }
            // A file that does not hash to its own name (a torn write, a disk fault, a person's edit) is never served: it is removed so
            // the geometry is fetched again.
            if ( read.Length == 0 || read.Length > BlobLimits.MaxBlobBytes || HashOf( read ) != hash )
            {
                TryDelete( file );
                return false;
            }
            Remember( hash, read );
            try
            {
                File.SetLastWriteTimeUtc( file, DateTime.UtcNow );
            }
            catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException )
            {
            }
            bytes = read;
            return true;
        }
    }

    /// <summary>Deletes the oldest files until the disk cache is within its cap. Called by the session from time to time, never on the capture path.</summary>
    public void Prune()
    {
        lock ( gate )
        {
            if ( directory == null || diskCap <= 0 ) return;
            List<FileInfo> files;
            try
            {
                files = new DirectoryInfo( directory ).GetFiles().Where( x => ValidHash( x.Name ) ).ToList();
            }
            catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException )
            {
                return;
            }
            long total = files.Sum( x => x.Length );
            foreach ( FileInfo file in files.OrderBy( x => x.LastWriteTimeUtc ) )
            {
                if ( total <= diskCap ) break;
                total -= file.Length;
                TryDelete( file.FullName );
            }
        }
    }

    private bool Store( string hash, byte[] bytes )
    {
        lock ( gate )
        {
            if ( memory.TryGetValue( hash, out Entry known ) )
            {
                known.Touched = ++clock;
                if ( directory != null && !File.Exists( Path.Combine( directory, hash ) ) ) WriteFile( hash, bytes );
                return true;
            }
            Remember( hash, bytes );
            if ( directory != null ) WriteFile( hash, bytes );
            return true;
        }
    }

    // Adds to the memory cache and evicts the least recently used entries until it fits. The entry just added is never the one evicted.
    private void Remember( string hash, byte[] bytes )
    {
        memory[hash] = new Entry { Bytes = bytes, Touched = ++clock };
        MemoryBytes += bytes.Length;
        while ( MemoryBytes > memoryCap && memory.Count > 1 )
        {
            string oldest = null;
            long oldestTouch = long.MaxValue;
            foreach ( KeyValuePair<string, Entry> pair in memory )
            {
                if ( pair.Key != hash && pair.Value.Touched < oldestTouch )
                {
                    oldest = pair.Key;
                    oldestTouch = pair.Value.Touched;
                }
            }
            if ( oldest == null ) break;
            MemoryBytes -= memory[oldest].Bytes.Length;
            memory.Remove( oldest );
        }
    }

    // Written to a temporary name and renamed, so a reader never sees half a file. A failure leaves the memory copy as the only one.
    private void WriteFile( string hash, byte[] bytes )
    {
        string target = Path.Combine( directory, hash );
        string temporary = Path.Combine( directory, hash + "." + Guid.NewGuid().ToString( "N" ) + ".tmp" );
        try
        {
            File.WriteAllBytes( temporary, bytes );
            File.Move( temporary, target, true );
        }
        catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException )
        {
            TryDelete( temporary );
        }
    }

    private static void TryDelete( string path )
    {
        try
        {
            File.Delete( path );
        }
        catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException )
        {
        }
    }
}
