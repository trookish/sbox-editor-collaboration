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

    // Consented code, queued only by ConsentToCode and fetched only after every asset (assets are the join; code is the person's choice).
    private readonly LinkedList<FileIndexEntry> codeFetches = new();

    private readonly List<FileIndexEntry> codeRemote = new();

    private readonly List<string> codeRecoveries = new();

    private readonly HashSet<string> codeFailedHashes = new( StringComparer.OrdinalIgnoreCase );

    private CodeOffer offer;

    private string seenFingerprint;

    private string declinedFingerprint;

    private bool codeIndexPending;

    private DateTime nextCodePoll = DateTime.MinValue;

    private int codeExpected;

    private int codeDone;

    private int codeFailed;

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

    /// <summary>The session's change log; every project file this client writes, deletes or sends is recorded in it. Null records nothing.</summary>
    public SessionChangeLog Changes { get; set; }
    /// <summary>The host's display name, for the code card and its notices. Null reads as "the host".</summary>
    public string HostName { get; set; }

    /// <summary>
    /// Whether the host's identity is verified. Fail closed: false until the session can say otherwise. The only evidence a guest could hold
    /// is a verdict on the host's Steam identity; the session reports it here.
    /// </summary>
    public bool HostIdentityVerified { get; set; }

    /// <summary>The person's existing setting "Allow peers whose Steam identity could not be verified".</summary>
    public bool AllowUnverifiedHost { get; set; }

    /// <summary>True when the host advertised <see cref="CodeTransferLimits.Feature"/>. Without it no code message is ever sent and the former notice is shown.</summary>
    public bool HostHasCode { get; set; }

    /// <summary>How often a joined client asks the host for its code listing, to notice that the host's code changed after the person decided. Zero asks every poll.</summary>
    public TimeSpan CodePollInterval { get; set; } = TimeSpan.FromSeconds( 15 );

    public bool CodeTrusted => CodeTrust.Permits( HostIdentityVerified, AllowUnverifiedHost );

    public CodeTransferState CodeState { get; private set; }

    /// <summary>The offer the person should see (also one that only reports what the limits left out), or null while there is none, while receiving, and after "Not now".</summary>
    public CodeOffer Offer => CodeState is CodeTransferState.Receiving or CodeTransferState.Received or CodeTransferState.Declined ? null : offer;

    /// <summary>
    /// The person agrees to exactly the files of the current offer. Refused (false, nothing sent) unless an offer with files is shown, the host
    /// can transfer code, and the host is trusted. Sends one code-consent naming the offered hashes; the files are then fetched like assets.
    /// </summary>
    public bool ConsentToCode( Action<Packet> send )
    {
        if ( !HostHasCode || !CodeTrusted || CodeState != CodeTransferState.Offered || offer == null || !offer.CanOffer ) return false;
        send( new Packet { Kind = "code-consent", Hashes = offer.Hashes.ToList() } );
        codeRecoveries.Clear();
        codeFailedHashes.Clear();
        codeDone = 0;
        codeFailed = 0;
        codeExpected = offer.Files.Count;
        foreach ( var file in offer.Files ) codeFetches.AddLast( new FileIndexEntry( file.Path, file.Sha256, file.Size ) { Kind = FileTransferKind.Code } );
        CodeState = CodeTransferState.Receiving;
        return true;
    }

    /// <summary>"Not now": the shown set is hidden; a different set (the host changed its code) is offered again.</summary>
    public void DeclineCode()
    {
        if ( CodeState != CodeTransferState.Offered || offer == null ) return;
        declinedFingerprint = offer.Fingerprint;
        offer = null;
        CodeState = CodeTransferState.Declined;
    }

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
        case "code-index":
            ReceiveCodeIndex( p );
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
        PollCode( send );
        Scan( send, openScenes );
    }

    // Asks the host for its code listing now and then, so a change the host makes after the person decided produces a new offer. Only while
    // idle: nothing is compared while a consented set is being received, and one request is in flight at a time.
    private void PollCode( Action<Packet> send )
    {
        if ( !HostHasCode || codeIndexPending || CodeState == CodeTransferState.Receiving || DateTime.UtcNow < nextCodePoll ) return;
        codeIndexPending = true;
        codeRemote.Clear();
        send( new Packet { Kind = "code-index" } );
    }

    private void ReceiveCodeIndex( Packet p )
    {
        if ( !codeIndexPending ) return;
        if ( p.Error != null )
        {
            codeIndexPending = false;
            nextCodePoll = DateTime.UtcNow + CodePollInterval;
            Notify( p.Error );
            return;
        }
        if ( p.FileEntries != null ) codeRemote.AddRange( p.FileEntries.Where( x => x != null ) );
        if ( codeRemote.Count > FileSyncLimits.MaxEntries )
        {
            codeRemote.Clear();
            codeIndexPending = false;
            nextCodePoll = DateTime.UtcNow + CodePollInterval;
            Notify( $"The host listed more than {FileSyncLimits.MaxEntries} code files; the code listing was not used." );
            return;
        }
        if ( !p.Final ) return;
        codeIndexPending = false;
        nextCodePoll = DateTime.UtcNow + CodePollInterval;
        var entries = codeRemote.ToList();
        codeRemote.Clear();
        UpdateOffer( entries );
    }

    // This machine's own transferable code, read from disk (cached by size and time), never the whole project: the offer compares code only.
    private List<FileIndexEntry> LocalCodeEntries()
    {
        var entries = new List<FileIndexEntry>();
        foreach ( string path in index.Paths() )
        {
            if ( !CodeTransferRules.IsTransferable( path ) ) continue;
            var entry = index.Entry( path );
            if ( entry != null ) entries.Add( entry );
        }
        return entries;
    }

    /// <summary>
    /// Compares the host's code listing with this machine's and shows, replaces or withdraws the offer. A set the person declined stays hidden;
    /// a set that differs from the one last shown is offered afresh with a change notice; a set being received is left alone until it is done.
    /// </summary>
    private void UpdateOffer( IReadOnlyList<FileIndexEntry> hostEntries )
    {
        if ( CodeState == CodeTransferState.Receiving ) return;
        List<FileIndexEntry> local;
        try
        {
            local = LocalCodeEntries();
        }
        catch ( InvalidOperationException )
        {
            // Too many files to enumerate: the asset path has already stopped and said so; no offer is built from a listing that cannot be trusted complete.
            return;
        }
        var next = CodeOffer.Build( hostEntries, local );
        if ( next == null )
        {
            offer = null;
            if ( CodeState is CodeTransferState.Offered or CodeTransferState.Declined ) CodeState = CodeTransferState.None;
            return;
        }
        if ( CodeState == CodeTransferState.Declined && next.Fingerprint == declinedFingerprint ) return;
        if ( offer != null && offer.Fingerprint == next.Fingerprint && offer.ExcessNotice == next.ExcessNotice && CodeState is CodeTransferState.Offered or CodeTransferState.None ) return;
        // "Changed" only when the person already saw a different set; files left over from a partly failed receive are not a change on the host.
        if ( seenFingerprint != null && seenFingerprint != next.Fingerprint && next.CanOffer && !next.Files.All( x => codeFailedHashes.Contains( x.Sha256 ) ) )
        {
            next.ChangeNotice = CodeOfferText.ChangedNotice( HostName, next.Files.Count );
        }
        seenFingerprint = next.Fingerprint;
        offer = next;
        CodeState = next.CanOffer ? CodeTransferState.Offered : CodeTransferState.None;
        foreach ( string notice in next.Rejected ) Notify( notice );
        if ( next.ExcessNotice != null ) Notify( next.ExcessNotice );
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
        // A host that can transfer code gets an offer (shown on the code card); an older one gets the former notice, since nothing else can be done.
        if ( HostHasCode ) UpdateOffer( remote.ToList() );
        else if ( plan.CodeNotice != null ) Notify( plan.CodeNotice );
        nextCodePoll = DateTime.UtcNow + CodePollInterval;
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
        while ( fetching == null && ( fetches.Count > 0 || codeFetches.Count > 0 ) )
        {
            // Assets first: they are the join. Code is fetched only once none is left, and only because the person agreed.
            var list = fetches.Count > 0 ? fetches : codeFetches;
            var entry = list.First.Value;
            list.RemoveFirst();
            try
            {
                fetching = new FileChunkAssembler( root, entry.Path, entry.Sha256, entry.Size, null, entry.Kind );
            }
            catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException or InvalidOperationException )
            {
                Notify( $"The file \"{entry.Path}\" could not be received: {ex.Message}" );
                if ( entry.Kind == FileTransferKind.Code ) CodeFileFinished( entry, false, null );
                else Count( entry.Size );
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
        bool assetBusy = fetches.Count > 0 || fetching != null && fetching.Kind == FileTransferKind.Asset;
        if ( !assetBusy && !JoinReady && planned > 0 ) JoinReady = true;
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
        bool isCode = assembler.Kind == FileTransferKind.Code;
        var entry = new FileIndexEntry( assembler.Path, assembler.Sha256, assembler.Total ) { Kind = assembler.Kind };
        string recovered = null;
        string error = complete ? assembler.Complete( receiver, out recovered ) : "incomplete";
        assembler.Dispose();
        finishedBytes -= assembler.Received;
        if ( isCode )
        {
            // Code never enters `known` (the upload scan ignores code, and a known code file would be reported deleted) and is never uploaded.
            if ( error == null )
            {
                index.Invalidate( entry.Path );
                failures.Remove( entry.Path );
                CodeFileFinished( entry, true, recovered );
                FileChanged?.Invoke( entry.Path );
                return;
            }
            bool binary = error == FileSyncReceiver.BinaryRefusal( entry.Path );
            int tries = failures.GetValueOrDefault( entry.Path ) + 1;
            failures[entry.Path] = tries;
            if ( tries <= 1 && complete && !binary )
            {
                codeFetches.AddFirst( entry );
                return;
            }
            Notify( binary ? error : complete ? $"The file \"{entry.Path}\" failed SHA-256 verification twice and was not applied." : error );
            CodeFileFinished( entry, false, null );
            return;
        }
        if ( error == null )
        {
            index.Invalidate( entry.Path );
            known[entry.Path] = entry;
            failures.Remove( entry.Path );
            Count( entry.Size );
            FileChanged?.Invoke( entry.Path );
            Changes?.Record( entry.Path, SessionChangeKind.ReceivedFromHost, "the host", entry.Sha256 );
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

    // One consented file is finished (written or refused). When the last one is, the person is told once and the offer is re-evaluated on the next listing.
    private void CodeFileFinished( FileIndexEntry entry, bool ok, string recovery )
    {
        if ( ok ) codeDone++;
        else
        {
            codeFailed++;
            codeFailedHashes.Add( entry.Sha256 );
        }
        if ( recovery != null )
        {
            // The recovery path is a file inside a dated folder; the person is told the folder.
            string folder = RecoveryFolder( recovery );
            if ( folder != null && !codeRecoveries.Contains( folder ) ) codeRecoveries.Add( folder );
        }
        if ( codeDone + codeFailed < codeExpected ) return;
        string recoveryText = codeRecoveries.Count == 0 ? null : string.Join( ", ", codeRecoveries );
        Notify( CodeOfferText.ReceivedNotice( codeDone, codeExpected, recoveryText ) );
        codeExpected = 0;
        offer = null;
        CodeState = CodeTransferState.Received;
        // The next listing decides whether anything is left to offer (a file that was refused is, and is shown again as new or differing).
        declinedFingerprint = null;
        nextCodePoll = DateTime.MinValue;
        codeDone = 0;
        codeFailed = 0;
    }

    private static string RecoveryFolder( string recovery )
    {
        string text = recovery.Replace( '\\', '/' );
        int at = text.IndexOf( ".collaboration/recovery/", StringComparison.Ordinal );
        if ( at < 0 ) return null;
        string rest = text.Substring( at + ".collaboration/recovery/".Length );
        int slash = rest.IndexOf( '/' );
        return ".collaboration/recovery/" + ( slash < 0 ? rest : rest.Substring( 0, slash ) );
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
        Changes?.Record( p.Path, SessionChangeKind.Deleted, "the host" );
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
            Changes?.Record( path, prior == null ? SessionChangeKind.Added : SessionChangeKind.Changed, "you", entry.Sha256 );
        }
        if ( scanPosition < scanPaths.Count ) return;
        if ( !index.Incomplete )
        {
            foreach ( var prior in known.Values.ToList() )
            {
                if ( scanSeen.Contains( prior.Path ) || open.Contains( prior.Path ) || IsBeingFetched( prior.Path ) ) continue;
                known.Remove( prior.Path );
                send( new Packet { Kind = "file-delete", Path = prior.Path, BaseSha256 = prior.Sha256 == "" ? null : prior.Sha256 } );
                Changes?.Record( prior.Path, SessionChangeKind.Deleted, "you" );
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
