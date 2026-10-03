using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TeamCreate;

/// <summary>
/// The host's half of project file synchronization: it answers files-index and file-get from its own disk, and applies members' file-put
/// and file-delete to its own project (backing up first) before telling everyone else to fetch or delete. Chunks to each member leave
/// through a per-member queue drained only while that member's window has credit, so live packets are never stuck behind a whole file.
/// </summary>
public sealed partial class SessionServer
{
    private sealed class FileSendJob
    {
        public string Path;

        public string Sha256;

        public string Target;

        public long Total;

        public long Offset;
    }

    private sealed class FileMemberState
    {
        public readonly Queue<FileSendJob> Jobs = new();

        public readonly FileChunkWindow Window = new();

        public FileChunkAssembler Upload;

        public Packet UploadHeader;

        /// <summary>A put the host decided not to apply (a resend or content it already holds): its chunks are acknowledged and dropped.</summary>
        public Packet Discard;

        /// <summary>The digests this member agreed to receive as code. Replaced (never extended) by each code-consent; empty until one arrives.</summary>
        public HashSet<string> CodeConsent = new( StringComparer.OrdinalIgnoreCase );
    }

    private readonly Dictionary<Member, FileMemberState> fileMembers = new();

    private readonly FileAuthority fileAuthority = new();

    private FileSyncIndex fileIndex;

    private FileSyncReceiver fileReceiver;

    private int fileUploads;

    /// <summary>The project folder whose files this host offers. Null disables file synchronization (and its welcome feature).</summary>
    public string FileProjectRoot { get; set; }

    /// <summary>The host's engine fingerprint (AdmissionRules.EngineFingerprint); null keeps the full-baseline admission check.</summary>
    public string HostEngineBaseline { get; set; }

    /// <summary>The clock that names recovery folders; tests pin it.</summary>
    public Func<DateTime> FileClock { get; set; }

    /// <summary>Called with the project-relative path of every file the host wrote or deleted for a member, so its editor can refresh the asset.</summary>
    public Action<string> FileChanged { get; set; }

    /// <summary>The latest file synchronization notice on the host's side, for the host's own dock.</summary>
    public string FileNotice { get; private set; }

    private bool HandleFilePacket( Member member, Packet packet )
    {
        if ( packet.Kind is not ( "files-index" or "file-get" or "file-ack" or "file-put" or "file-chunk" or "file-delete" or "code-index" or "code-consent" ) ) return false;
        if ( string.IsNullOrEmpty( FileProjectRoot ) )
        {
            if ( packet.Kind != "file-ack" ) FileRefuse( member, "This host does not synchronize project files.", packet.Path );
            return true;
        }
        fileIndex ??= new FileSyncIndex( FileProjectRoot );
        fileReceiver ??= new FileSyncReceiver( FileProjectRoot, FileClock );
        if ( !fileMembers.TryGetValue( member, out var state ) ) fileMembers[member] = state = new FileMemberState();
        try
        {
            switch ( packet.Kind )
            {
            case "files-index":
                SendIndex( member );
                break;
            case "code-index":
                SendIndex( member, true );
                break;
            case "code-consent":
                RecordConsent( state, packet );
                break;
            case "file-ack":
                state.Window.Release();
                break;
            case "file-get":
                QueueFile( member, state, packet );
                break;
            case "file-put":
                BeginUpload( member, state, packet );
                break;
            case "file-chunk":
                ReceiveChunk( member, state, packet );
                break;
            case "file-delete":
                ApplyDelete( member, packet );
                break;
            }
        }
        catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException )
        {
            FileRefuse( member, "Project file synchronization refused the operation: " + ex.Message, packet.Path );
        }
        return true;
    }

    // The member's agreement to receive exactly these digests as code. It replaces any earlier one; a malformed or oversized list grants nothing.
    private static void RecordConsent( FileMemberState state, Packet packet )
    {
        state.CodeConsent = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
        var hashes = packet.Hashes;
        if ( hashes == null || hashes.Count > CodeTransferLimits.MaxFiles || hashes.Any( x => !FileSyncReceiver.ValidHash( x ) ) ) return;
        foreach ( string hash in hashes ) state.CodeConsent.Add( hash );
    }

    private void SendIndex( Member member, bool codeOnly = false )
    {
        string kind = codeOnly ? "code-index" : "files-index";
        List<FileIndexEntry> entries;
        try
        {
            entries = fileIndex.Build();
        }
        catch ( InvalidOperationException ex )
        {
            // Reported, never truncated: a partial index would make the guest believe the missing files do not exist.
            member.Wire.Send( new Packet { Kind = kind, Final = true, Error = ex.Message } );
            return;
        }
        if ( codeOnly ) entries = entries.Where( x => CodeTransferRules.IsTransferable( x.Path ) ).ToList();
        else foreach ( var entry in entries ) fileAuthority.Seed( entry.Path, entry.Sha256 );
        for ( int i = 0; i == 0 || i < entries.Count; i += FileSyncLimits.IndexPage )
        {
            bool final = i + FileSyncLimits.IndexPage >= entries.Count;
            member.Wire.Send( new Packet
            {
                Kind = kind,
                FileEntries = entries.Skip( i ).Take( FileSyncLimits.IndexPage ).ToList(),
                Final = final,
                // Only the names, only on the last page: the project file is Code and is never sent, but the guest can say what it lacks.
                Packages = final && !codeOnly ? HostPackages() : null
            } );
        }
    }

    private List<string> HostPackages()
    {
        try
        {
            string file = Directory.EnumerateFiles( FileProjectRoot, "*.sbproj", SearchOption.TopDirectoryOnly ).OrderBy( x => x, StringComparer.Ordinal ).FirstOrDefault();
            return file == null ? null : PackageReferenceDiff.Idents( File.ReadAllText( file ) );
        }
        catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException )
        {
            return null;
        }
    }

    private void QueueFile( Member member, FileMemberState state, Packet packet )
    {
        // The kind is decided here from the path alone, never from anything the member sent: a path that is not an asset is code and
        // is served only for a digest this member agreed to, and never from Libraries, Git, build output or the library's own folder.
        bool code = !string.IsNullOrWhiteSpace( packet.Path ) && JoinReadiness.Classify( packet.Path ) != ProjectFileKind.Asset;
        var kind = code ? FileTransferKind.Code : FileTransferKind.Asset;
        if ( code && ( !FileSyncReceiver.ValidHash( packet.Sha256 ) || !state.CodeConsent.Contains( packet.Sha256 ) ) && CodeTransferRules.IsTransferable( packet.Path ) )
        {
            FileRefuse( member, $"The file \"{FileJoinPlan.Bounded( packet.Path )}\" is code and is sent only to a member who agreed to receive it; no agreement covers it.", packet.Path );
            return;
        }
        if ( !FileSyncReceiver.ValidPath( FileProjectRoot, packet.Path, kind, out string target, out string error ) )
        {
            FileRefuse( member, error, packet.Path );
            return;
        }
        var entry = fileIndex.Entry( packet.Path );
        if ( entry == null )
        {
            FileRefuse( member, $"The file \"{packet.Path}\" no longer exists on the host.", packet.Path );
            return;
        }
        if ( entry.Size > ( code ? CodeTransferLimits.MaxFileBytes : FileSyncLimits.MaxFileSize ) )
        {
            FileRefuse( member, code ? $"The code file \"{FileJoinPlan.Bounded( packet.Path )}\" is larger than 4 MiB and is not sent." : FileJoinPlan.TooLarge( packet.Path ), packet.Path );
            return;
        }
        if ( !string.Equals( entry.Sha256, packet.Sha256, StringComparison.OrdinalIgnoreCase ) )
        {
            FileRefuse( member, $"The file \"{packet.Path}\" changed on the host after it was offered; the newer version will be offered again.", packet.Path );
            return;
        }
        state.Jobs.Enqueue( new FileSendJob { Path = packet.Path, Sha256 = entry.Sha256, Target = target, Total = entry.Size } );
    }

    private void BeginUpload( Member member, FileMemberState state, Packet packet )
    {
        state.Upload?.Dispose();
        state.Upload = null;
        state.UploadHeader = null;
        state.Discard = null;
        if ( !FileSyncReceiver.ValidPath( FileProjectRoot, packet.Path, out string target, out string error ) )
        {
            // Refused, and the chunks that follow are acknowledged and dropped so the sender's window keeps moving.
            state.Discard = packet;
            FileRefuse( member, error, packet.Path );
            return;
        }
        if ( packet.Total < 0 || packet.Total > FileSyncLimits.MaxFileSize || !FileSyncReceiver.ValidHash( packet.Sha256 ) )
        {
            state.Discard = packet;
            FileRefuse( member, packet.Total > FileSyncLimits.MaxFileSize ? FileJoinPlan.TooLarge( packet.Path ) : $"The file \"{packet.Path}\" was offered without a valid SHA-256 digest.", packet.Path );
            return;
        }
        if ( fileAuthority.Evaluate( packet.Path, packet.Id, packet.Sha256, File.Exists( target ) ) != FileVerdict.Apply )
        {
            state.Discard = packet;
            return;
        }
        // A distinct part file per upload: two members may send the same content at once.
        state.Upload = new FileChunkAssembler( FileProjectRoot, packet.Path, packet.Sha256, packet.Total, "-upload-" + ++fileUploads );
        state.UploadHeader = packet;
        if ( state.Upload.IsComplete ) FinishUpload( member, state );
    }

    private void ReceiveChunk( Member member, FileMemberState state, Packet packet )
    {
        // Every chunk is acknowledged, applied or not: the acknowledgement only returns the sender's credit.
        member.Wire.Send( new Packet { Kind = "file-ack", Path = packet.Path, Offset = packet.Offset } );
        if ( state.Discard != null && state.Discard.Path == packet.Path && state.Discard.Sha256 == packet.Sha256 ) return;
        var upload = state.Upload;
        if ( upload == null || state.UploadHeader.Path != packet.Path || !string.Equals( upload.Sha256, packet.Sha256, StringComparison.OrdinalIgnoreCase ) || packet.Total != upload.Total )
        {
            FileRefuse( member, $"An unexpected chunk of \"{FileJoinPlan.Bounded( packet.Path )}\" was ignored.", packet.Path );
            return;
        }
        if ( !upload.Add( packet.Offset, packet.Data ) )
        {
            upload.Dispose();
            state.Upload = null;
            state.Discard = state.UploadHeader;
            FileRefuse( member, $"The upload of \"{packet.Path}\" contained a chunk with an invalid offset or length and was discarded.", packet.Path );
            return;
        }
        if ( upload.IsComplete ) FinishUpload( member, state );
    }

    private void FinishUpload( Member member, FileMemberState state )
    {
        var upload = state.Upload;
        var header = state.UploadHeader;
        state.Upload = null;
        state.UploadHeader = null;
        try
        {
            string error = upload.Complete( fileReceiver, out string recovery );
            if ( error != null )
            {
                FileRefuse( member, error, header.Path );
                return;
            }
            fileIndex.Invalidate( header.Path );
            FileChanged?.Invoke( header.Path );
            string clash = fileAuthority.Commit( header.Path, header.Id, header.BaseSha256, header.Sha256, member.Name ?? "a collaborator", member.Peer, recovery, out string otherPeer );
            if ( clash != null ) FileClashNotice( member, otherPeer, clash );
            Broadcast( new Packet { Kind = "file-changed", Path = header.Path, Sha256 = header.Sha256.ToLowerInvariant(), Total = header.Total, Peer = member.Peer } );
        }
        finally
        {
            upload.Dispose();
        }
    }

    private void ApplyDelete( Member member, Packet packet )
    {
        if ( !FileSyncReceiver.ValidPath( FileProjectRoot, packet.Path, out string target, out string error ) )
        {
            FileRefuse( member, error, packet.Path );
            return;
        }
        if ( fileAuthority.Evaluate( packet.Path, packet.Id, null, File.Exists( target ) ) != FileVerdict.Apply ) return;
        error = fileReceiver.Delete( packet.Path, out string recovery );
        if ( error != null )
        {
            FileRefuse( member, error, packet.Path );
            return;
        }
        fileIndex.Invalidate( packet.Path );
        FileChanged?.Invoke( packet.Path );
        string clash = fileAuthority.Commit( packet.Path, packet.Id, packet.BaseSha256, null, member.Name ?? "a collaborator", member.Peer, recovery, out string otherPeer );
        if ( clash != null ) FileClashNotice( member, otherPeer, clash );
        Broadcast( new Packet { Kind = "file-deleted", Path = packet.Path, BaseSha256 = packet.BaseSha256, Peer = member.Peer } );
    }

    // A refusal keeps the session: the offending file is skipped and the member is told why, with the path so its client can move on.
    private void FileRefuse( Member member, string message, string path )
    {
        FileNotice = message;
        member.Wire.Send( new Packet { Kind = "file-notice", Notice = message, Path = path } );
    }

    // The clash notice goes to both writers: the member whose version won and the one whose version was replaced (when still connected).
    private void FileClashNotice( Member member, string otherPeer, string notice )
    {
        FileNotice = notice;
        foreach ( var target in members.Where( x => x == member || ( otherPeer != null && x.Peer == otherPeer ) ).ToList() )
        {
            target.Wire.Send( new Packet { Kind = "file-notice", Notice = notice } );
        }
    }

    private void PumpFileOutboxes()
    {
        foreach ( var (member, state) in fileMembers.ToList() )
        {
            if ( !members.Contains( member ) )
            {
                ForgetFileMember( member );
                continue;
            }
            try
            {
                while ( state.Jobs.Count > 0 && state.Window.TryAcquire() )
                {
                    var job = state.Jobs.Peek();
                    member.Wire.Send( NextChunk( job ) );
                    if ( job.Offset >= job.Total ) state.Jobs.Dequeue();
                }
            }
            catch ( IOException ex )
            {
                // The file vanished or the connection failed. A vanished file is reported and skipped; a failed connection is detected by the
                // server's own receive path, so nothing more is sent to it here.
                if ( state.Jobs.Count > 0 && member.Wire.Failure == null )
                {
                    var job = state.Jobs.Dequeue();
                    state.Window.Release();
                    FileRefuse( member, $"The file \"{job.Path}\" could not be read on the host: {ex.Message}", job.Path );
                }
            }
        }
    }

    // Reads one chunk lazily, so a large file never sits in memory whole. A file changed mid-transfer fails the receiver's digest check.
    private static Packet NextChunk( FileSendJob job )
    {
        int length = (int)Math.Min( FileSyncLimits.ChunkSize, job.Total - job.Offset );
        byte[] data = new byte[length];
        using ( var file = new FileStream( job.Target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete ) )
        {
            file.Position = job.Offset;
            file.ReadExactly( data );
        }
        var packet = new Packet { Kind = "file-chunk", Path = job.Path, Sha256 = job.Sha256, Offset = job.Offset, Total = job.Total, Data = data };
        job.Offset += length;
        return packet;
    }

    private void ForgetFileMember( Member member )
    {
        if ( fileMembers.Remove( member, out var state ) ) state.Upload?.Dispose();
    }
}
