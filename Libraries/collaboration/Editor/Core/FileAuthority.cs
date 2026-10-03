using System;
using System.Collections.Generic;

namespace TeamCreate;

public enum FileVerdict
{
    /// <summary>Write (or delete) the file and record the new revision.</summary>
    Apply,

    /// <summary>The same packet id was already handled: a resend after a reconnect. Nothing is written twice.</summary>
    Duplicate,

    /// <summary>The host already holds exactly this content (or this deletion): typically a member reporting the file the host just wrote.</summary>
    Unchanged
}

/// <summary>
/// The host's per-path record of project files changed during the session: who wrote the current version, and where the version it
/// replaced was backed up. Later write wins, always; a write whose base is not the current version is still applied, and the clash is
/// reported with the path of the earlier version so nothing is lost. Pure and in memory: it is not journaled like scene edits, because the
/// outcome it decides is already durable in the host's project folder and its recovery copies; after a host restart the revisions are
/// re-seeded from disk, and only the clash detection for edits that straddle the restart is lost.
/// </summary>
public sealed class FileAuthority
{
    private const int RememberedPackets = 4096;

    private readonly Dictionary<string, Revision> revisions = new( StringComparer.OrdinalIgnoreCase );

    private readonly HashSet<string> seen = new( StringComparer.Ordinal );

    private readonly Queue<string> seenOrder = new();

    private sealed record Revision( string Hash, string Name, string Peer, string Recovery );

    /// <summary>The current digest of a path, or null when it is unknown or deleted.</summary>
    public string HashOf( string path )
    {
        return revisions.TryGetValue( path, out var revision ) ? revision.Hash : null;
    }

    /// <summary>Records what the host's own disk holds, unless the session already changed the path (a later index must not undo that).</summary>
    public void Seed( string path, string hash )
    {
        revisions.TryAdd( path, new Revision( hash, "the host", null, null ) );
    }

    /// <summary>What to do with a put (hash is the new digest) or a delete (hash null). existsOnDisk is only consulted for an unknown path.</summary>
    public FileVerdict Evaluate( string path, string packetId, string hash, bool existsOnDisk )
    {
        if ( packetId != null && seen.Contains( packetId ) ) return FileVerdict.Duplicate;
        if ( revisions.TryGetValue( path, out var current ) )
        {
            if ( Same( current.Hash, hash ) ) return Remember( packetId, FileVerdict.Unchanged );
        }
        else if ( hash == null && !existsOnDisk )
        {
            return Remember( packetId, FileVerdict.Unchanged );
        }
        return FileVerdict.Apply;
    }

    /// <summary>
    /// Records an applied write or delete and returns the clash notice when the writer's base was not the current version, else null.
    /// clashWithPeer names the member who wrote the version that was replaced, so the notice can reach both people.
    /// </summary>
    public string Commit( string path, string packetId, string baseHash, string hash, string name, string peer, string recovery, out string clashWithPeer )
    {
        clashWithPeer = null;
        Remember( packetId, FileVerdict.Apply );
        string notice = null;
        if ( revisions.TryGetValue( path, out var previous ) && !Same( previous.Hash, baseHash ) )
        {
            // A put after a delete replaces no file; the earlier version is then the copy the delete backed up.
            string earlier = recovery ?? previous.Recovery ?? ".collaboration/recovery";
            notice = $"\"{path}\" was changed by {name} and by {previous.Name} at the same time. The later version was kept; the earlier one is in {earlier}.";
            clashWithPeer = previous.Peer;
        }
        revisions[path] = new Revision( hash, name, peer, recovery );
        return notice;
    }

    private FileVerdict Remember( string packetId, FileVerdict verdict )
    {
        if ( packetId != null && seen.Add( packetId ) )
        {
            seenOrder.Enqueue( packetId );
            if ( seenOrder.Count > RememberedPackets ) seen.Remove( seenOrder.Dequeue() );
        }
        return verdict;
    }

    private static bool Same( string a, string b )
    {
        return string.Equals( a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase );
    }
}
