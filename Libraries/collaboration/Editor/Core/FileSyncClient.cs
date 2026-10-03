using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TeamCreate;

/// <summary>
/// One editor's half of project file synchronization, driven entirely from the editor's frame (no threads, no FileSystemWatcher: its
/// callbacks run on worker threads, which hot-loaded editor code must not use). After welcome it fetches the host's index and the assets
/// that differ, one file at a time; then it polls its own asset tree every PollInterval by (size, last-write time), a bounded number of
/// files per frame, and sends what changed to the host. Files it wrote itself are recorded as known, so they are never sent back.
/// </summary>
public sealed class FileSyncClient : IDisposable
{
    private readonly string root;

    private readonly FileSyncIndex index;

    private readonly FileSyncReceiver receiver;

    private readonly Func<DateTime> clock;

    private readonly List<FileIndexEntry> remote = new();

    private readonly LinkedList<FileIndexEntry> fetches = new();

    private readonly Dictionary<string, int> failures = new( StringComparer.OrdinalIgnoreCase );

    // The digest this machine last agreed with the session for each asset: the base of the next put, and how a file this client wrote is
    // told apart from one the person changed.
    private readonly Dictionary<string, FileIndexEntry> known = new( StringComparer.OrdinalIgnoreCase );

    private readonly Queue<string> notices = new();

    private readonly LinkedList<(FileIndexEntry Entry, string Base)> uploads = new();

    private readonly FileChunkWindow uploadWindow = new();

    private FileChunkAssembler fetching;

    private UploadJob uploading;

    private List<string> scanPaths;

    private int scanPosition;

    private HashSet<string> scanSeen;

    private DateTime nextPoll = DateTime.MinValue;

    private bool indexRequested;

    private int planned;

    private int finished;

    private long plannedBytes;

    private long finishedBytes;

    private sealed class UploadJob
    {
        public FileIndexEntry Entry;

        public long Offset;
    }

    public FileSyncClient( string root, Func<DateTime> clock = null )
    {
        this.root = root;
        this.clock = clock ?? ( () => DateTime.UtcNow );
        index = new FileSyncIndex( root );
        receiver = new FileSyncReceiver( root, this.clock );
    }

    /// <summary>How often the asset tree is polled once joined.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds( 2 );

    /// <summary>Files examined per frame while polling.</summary>
    public int FilesPerFrame { get; set; } = 64;

    /// <summary>Bytes read for hashing per frame while polling; a frame stops early once it is exceeded (at least one file is examined).</summary>
    public long HashBytesPerFrame { get; set; } = 8L * 1024 * 1024;

    /// <summary>
    /// Called with the project-relative path of every file this client wrote or deleted, after the disk change, so the editor can make
    /// the asset system notice it at once. Optional; exceptions are the caller's to contain.
    /// </summary>
    public Action<string> FileChanged { get; set; }

    /// <summary>True once the files a join needed have arrived (or been refused); scene subscription waits for it.</summary>
    public bool JoinReady { get; private set; }

    /// <summary>True when synchronization stopped for good (the project exceeds the index limit); the session itself continues.</summary>
    public bool Disabled { get; private set; }

    public int Planned => planned;

    public int Finished => finished;

    public string Progress => JoinReady ? null
        : !indexRequested || remote.Count == 0 && planned == 0 ? "Receiving project files: comparing with the host."
        : $"Receiving project files: {finished} of {planned} ({finishedBytes / 1048576} of {plannedBytes / 1048576} MB).";

    /// <summary>The next formal notice for the person, or null. Every notice is delivered once.</summary>
    public string TakeNotice()
    {
        return notices.Count > 0 ? notices.Dequeue() : null;
    }

    public void Begin( Action<Packet> send )
    {
        JoinReady = false;
        indexRequested = true;
        remote.Clear();
        send( new Packet { Kind = "files-index" } );
    }

    public bool Handle( Packet p, Action<Packet> send, IEnumerable<string> openScenes )
    {
        switch ( p.Kind )
        {
        case "files-index":
            ReceiveIndex( p, openScenes );
            return true;
        case "file-chunk":
            ReceiveChunk( p, send );
            return true;
        case "file-ack":
            uploadWindow.Release();
            return true;
        case "file-changed":
            RemoteChanged( p, openScenes );
            return true;
        case "file-deleted":
            RemoteDeleted( p, openScenes );
            return true;
        case "file-notice":
            if ( p.Notice != null ) Notify( p.Notice );
            // A refusal of the file being fetched ends that fetch, so the join does not wait for chunks that will never come.
            if ( fetching != null && p.Path != null && string.Equals( p.Path, fetching.Path, StringComparison.OrdinalIgnoreCase ) ) EndFetch( false );
            return true;
        default:
            return false;
        }
    }

    public void Poll( Action<Packet> send, IEnumerable<string> openScenes )
    {
        SendUploadChunks( send );
        StartNextFetch( send );
        if ( !JoinReady || Disabled ) return;
        Scan( send, openScenes );
    }

    private void ReceiveIndex( Packet p, IEnumerable<string> openScenes )
    {
        if ( JoinReady || !indexRequested ) return;
        if ( p.Error != null )
        {
            Stop( p.Error );
            return;
        }
        if ( p.FileEntries != null ) remote.AddRange( p.FileEntries.Where( x => x != null ) );
        if ( remote.Count > FileSyncLimits.MaxEntries )
        {
            Stop( $"The host's project has more than {FileSyncLimits.MaxEntries} files; file synchronization stopped. Synchronize this project through Git." );
            return;
        }
        if ( !p.Final ) return;
        List<FileIndexEntry> local;
        try
        {
            local = index.Build();
        }
        catch ( InvalidOperationException ex )
        {
            Stop( ex.Message );
            return;
        }
        var plan = FileJoinPlan.Plan( remote, local, openScenes );
        if ( plan.CodeNotice != null ) Notify( plan.CodeNotice );
        foreach ( string notice in plan.Notices ) Notify( notice );
        // Reported once, never installed: the packages are the person's to add, and the project file is not transferred.
        if ( p.Packages != null )
        {
            string packageNotice = PackageReferenceDiff.Notice( PackageReferenceDiff.Missing( p.Packages.Take( PackageReferenceDiff.MaxIdents ), LocalProjectFile() ) );
            if ( packageNotice != null ) Notify( packageNotice );
        }
        foreach ( var entry in local.Where( x => JoinReadiness.Classify( x.Path ) == ProjectFileKind.Asset ) ) known[entry.Path] = entry;
        foreach ( var entry in plan.Fetch )
        {
            if ( !FileSyncReceiver.ValidPath( root, entry.Path, out _, out string error ) )
            {
                Notify( error );
                continue;
            }
            fetches.AddLast( entry );
            planned++;
            plannedBytes += entry.Size;
        }
        remote.Clear();
        JoinReady = fetches.Count == 0;
    }

    // The guest's own project file, read only to compare package names; null when there is none or it cannot be read (then every host package is reported).
    private string LocalProjectFile()
    {
        try
        {
            string file = Directory.EnumerateFiles( root, "*.sbproj", SearchOption.TopDirectoryOnly ).OrderBy( x => x, StringComparer.Ordinal ).FirstOrDefault();
            return file == null ? null : File.ReadAllText( file );
        }
        catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException )
        {
            return null;
        }
    }

    private void StartNextFetch( Action<Packet> send )
    {
        while ( fetching == null && fetches.Count > 0 )
        {
            var entry = fetches.First.Value;
            fetches.RemoveFirst();
            try
            {
                fetching = new FileChunkAssembler( root, entry.Path, entry.Sha256, entry.Size );
            }
            catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException or InvalidOperationException )
            {
                Notify( $"The file \"{entry.Path}\" could not be received: {ex.Message}" );
                Count( entry.Size );
                continue;
            }
            if ( fetching.IsComplete )
            {
                // An empty file needs no chunk; it is verified and written like any other.
                EndFetch( true );
                continue;
            }
            send( new Packet { Kind = "file-get", Path = entry.Path, Sha256 = entry.Sha256 } );
        }
        if ( fetching == null && fetches.Count == 0 && !JoinReady && planned > 0 ) JoinReady = true;
    }

    private void ReceiveChunk( Packet p, Action<Packet> send )
    {
        // Acknowledged whatever happens to it: the acknowledgement only returns the host's credit for this connection.
        send( new Packet { Kind = "file-ack", Path = p.Path, Offset = p.Offset } );
        if ( fetching == null || !string.Equals( p.Path, fetching.Path, StringComparison.OrdinalIgnoreCase ) || !string.Equals( p.Sha256, fetching.Sha256, StringComparison.OrdinalIgnoreCase ) || p.Total != fetching.Total ) return;
        long before = fetching.Received;
        if ( !fetching.Add( p.Offset, p.Data ) )
        {
            Notify( $"A chunk of \"{fetching.Path}\" had an invalid offset or length." );
            EndFetch( false );
            return;
        }
        finishedBytes += fetching.Received - before;
        if ( fetching.IsComplete ) EndFetch( true );
    }

    // Finishes the current fetch: verifies and writes it when complete, otherwise counts a failure. A failed file is requested once more,
    // then reported and skipped, so one bad file never holds up the join.
    private void EndFetch( bool complete )
    {
        var assembler = fetching;
        fetching = null;
        var entry = new FileIndexEntry( assembler.Path, assembler.Sha256, assembler.Total );
        string error = complete ? assembler.Complete( receiver, out _ ) : "incomplete";
        assembler.Dispose();
        finishedBytes -= assembler.Received;
        if ( error == null )
        {
            index.Invalidate( entry.Path );
            known[entry.Path] = entry;
            failures.Remove( entry.Path );
            Count( entry.Size );
            FileChanged?.Invoke( entry.Path );
            return;
        }
        int attempts = failures.GetValueOrDefault( entry.Path ) + 1;
        failures[entry.Path] = attempts;
        if ( attempts <= 1 && complete )
        {
            fetches.AddFirst( entry );
            return;
        }
        if ( complete ) Notify( $"The file \"{entry.Path}\" failed SHA-256 verification twice and was not applied." );
        Count( entry.Size );
    }

    private void Count( long size )
    {
        if ( JoinReady ) return;
        finished = Math.Min( planned, finished + 1 );
        finishedBytes += size;
    }

    private void RemoteChanged( Packet p, IEnumerable<string> openScenes )
    {
        if ( !FileSyncReceiver.ValidPath( root, p.Path, out _, out string error ) )
        {
            // The host asked this machine to write code or outside the project: refused, reported, and the session continues.
            Notify( error );
            return;
        }
        if ( p.Total < 0 || p.Total > FileSyncLimits.MaxFileSize )
        {
            Notify( FileJoinPlan.TooLarge( p.Path ) );
            return;
        }
        if ( !FileSyncReceiver.ValidHash( p.Sha256 ) ) return;
        if ( openScenes != null && openScenes.Contains( p.Path, StringComparer.OrdinalIgnoreCase ) ) return;
        var local = index.Entry( p.Path );
        if ( local != null && string.Equals( local.Sha256, p.Sha256, StringComparison.OrdinalIgnoreCase ) )
        {
            // Already identical: this machine wrote it (its own put coming back) or the same content arrived another way.
            known[p.Path] = local;
            return;
        }
        for ( var node = fetches.First; node != null; node = node.Next )
        {
            if ( string.Equals( node.Value.Path, p.Path, StringComparison.OrdinalIgnoreCase ) )
            {
                fetches.Remove( node );
                break;
            }
        }
        fetches.AddLast( new FileIndexEntry( p.Path, p.Sha256.ToLowerInvariant(), p.Total ) );
    }

    private void RemoteDeleted( Packet p, IEnumerable<string> openScenes )
    {
        if ( !FileSyncReceiver.ValidPath( root, p.Path, out _, out string error ) )
        {
            Notify( error );
            return;
        }
        if ( openScenes != null && openScenes.Contains( p.Path, StringComparer.OrdinalIgnoreCase ) ) return;
        error = receiver.Delete( p.Path, out _ );
        if ( error != null )
        {
            Notify( error );
            return;
        }
        index.Invalidate( p.Path );
        known.Remove( p.Path );
        FileChanged?.Invoke( p.Path );
    }

    // Polls the asset tree a bounded slice per frame: the path list once per cycle, then at most FilesPerFrame files and HashBytesPerFrame
    // bytes of hashing per call. Deletions are concluded only after a complete walk, and never from a walk that could not list a folder.
    private void Scan( Action<Packet> send, IEnumerable<string> openScenes )
    {
        if ( scanPaths == null )
        {
            if ( clock() < nextPoll ) return;
            try
            {
                scanPaths = index.Paths();
            }
            catch ( InvalidOperationException ex )
            {
                Stop( ex.Message );
                return;
            }
            scanPosition = 0;
            scanSeen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
        }
        var open = new HashSet<string>( openScenes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase );
        long hashedAtStart = index.HashedBytes;
        int examined = 0;
        while ( scanPosition < scanPaths.Count && examined < FilesPerFrame && ( examined == 0 || index.HashedBytes - hashedAtStart < HashBytesPerFrame ) )
        {
            string path = scanPaths[scanPosition++];
            examined++;
            if ( JoinReadiness.Classify( path ) != ProjectFileKind.Asset || !ProjectManifest.Covers( path ) ) continue;
            scanSeen.Add( path );
            if ( open.Contains( path ) || IsBeingFetched( path ) ) continue;
            var entry = index.Entry( path );
            if ( entry == null ) continue;
            known.TryGetValue( path, out var prior );
            if ( prior != null && prior.Sha256 == entry.Sha256 ) continue;
            known[path] = entry;
            if ( entry.Size > FileSyncLimits.MaxFileSize )
            {
                Notify( FileJoinPlan.TooLarge( path ) );
                continue;
            }
            QueueUpload( entry, prior?.Sha256 );
        }
        if ( scanPosition < scanPaths.Count ) return;
        if ( !index.Incomplete )
        {
            foreach ( var prior in known.Values.ToList() )
            {
                if ( scanSeen.Contains( prior.Path ) || open.Contains( prior.Path ) || IsBeingFetched( prior.Path ) ) continue;
                known.Remove( prior.Path );
                send( new Packet { Kind = "file-delete", Path = prior.Path, BaseSha256 = prior.Sha256 == "" ? null : prior.Sha256 } );
            }
        }
        scanPaths = null;
        nextPoll = clock() + PollInterval;
    }

    private bool IsBeingFetched( string path )
    {
        return fetching != null && string.Equals( fetching.Path, path, StringComparison.OrdinalIgnoreCase ) || fetches.Any( x => string.Equals( x.Path, path, StringComparison.OrdinalIgnoreCase ) );
    }

    private void QueueUpload( FileIndexEntry entry, string baseHash )
    {
        for ( var node = uploads.First; node != null; node = node.Next )
        {
            if ( string.Equals( node.Value.Entry.Path, entry.Path, StringComparison.OrdinalIgnoreCase ) )
            {
                // A newer change of a file still waiting replaces it but keeps the base the session last agreed on.
                node.Value = (entry, node.Value.Base);
                return;
            }
        }
        uploads.AddLast( (entry, baseHash) );
    }

    // One upload at a time; its chunks leave only with window credit, returned by the host's file-ack. Live packets are sent by the
    // session directly on the wire and so are never queued behind more than the window's eight chunks.
    private void SendUploadChunks( Action<Packet> send )
    {
        if ( uploading == null && uploads.Count > 0 )
        {
            var (entry, baseHash) = uploads.First.Value;
            uploads.RemoveFirst();
            send( new Packet { Kind = "file-put", Path = entry.Path, BaseSha256 = baseHash, Sha256 = entry.Sha256, Total = entry.Size } );
            uploading = new UploadJob { Entry = entry };
        }
        while ( uploading != null && uploading.Offset < uploading.Entry.Size && uploadWindow.TryAcquire() )
        {
            var job = uploading;
            int length = (int)Math.Min( FileSyncLimits.ChunkSize, job.Entry.Size - job.Offset );
            byte[] data = new byte[length];
            try
            {
                using var file = new FileStream( Path.Combine( root, job.Entry.Path.Replace( '/', Path.DirectorySeparatorChar ) ), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete );
                file.Position = job.Offset;
                file.ReadExactly( data );
            }
            catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException )
            {
                // Changed or removed while being sent: the host's digest check discards the partial upload, and the next poll sends it again.
                uploadWindow.Release();
                index.Invalidate( job.Entry.Path );
                known.Remove( job.Entry.Path );
                uploading = null;
                return;
            }
            send( new Packet { Kind = "file-chunk", Path = job.Entry.Path, Sha256 = job.Entry.Sha256, Offset = job.Offset, Total = job.Entry.Size, Data = data } );
            job.Offset += length;
        }
        if ( uploading != null && uploading.Offset >= uploading.Entry.Size ) uploading = null;
    }

    private void Stop( string notice )
    {
        Notify( notice );
        Disabled = true;
        JoinReady = true;
        fetches.Clear();
    }

    private void Notify( string notice )
    {
        if ( notices.Count < 64 ) notices.Enqueue( notice );
    }

    public void Dispose()
    {
        fetching?.Dispose();
        fetching = null;
    }
}
