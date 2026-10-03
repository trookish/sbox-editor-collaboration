using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace TeamCreate;

/// <summary>What happened to a project file during the session, from the point of view of the machine that keeps the log.</summary>
public enum SessionChangeKind
{
    /// <summary>The file did not exist at the start of the session.</summary>
    Added,

    /// <summary>The file existed and its content changed.</summary>
    Changed,

    /// <summary>The file was removed.</summary>
    Deleted,

    /// <summary>The host's version of the file was written here by file synchronization.</summary>
    ReceivedFromHost,

    /// <summary>A code file was written here by the code transfer the member accepted.</summary>
    CodeReceived
}

/// <summary>One row of the change log. Sha256 is null when the writer did not hash the file (a deletion, or a scene saved by the editor).</summary>
public sealed record SessionChange( string Path, SessionChangeKind Kind, string By, DateTime AtUtc, string Sha256 );

/// <summary>
/// The list the dock shows as "Files changed in this session". The library never runs Git, so after a session the people who worked in it must
/// commit what changed themselves; this log tells them, plainly, which project files that is. It is pure and in memory, with one row per path:
/// the latest event names the peer, the time and the digest, and the kinds merge so the row stays true to the project's state at the start
/// of the session (a file that was added and then changed is still an addition, and a file that was added and then deleted is no change at all).
/// It is bounded: past <see cref="MaxEntries"/> rows the least recently touched row is dropped and counted in <see cref="OlderNotShown"/>.
/// Paths are refused unless FilePathRules accepts them, because a peer chooses them; a refused path is never shown.
/// The log is written to .collaboration/session-changes.json so a crash does not lose it, and the next session reads only the number of rows
/// from that file to say "Previous session: N files changed."; sessions are never merged. Nothing here starts a process or touches version control.
/// </summary>
public sealed class SessionChangeLog
{
    /// <summary>The most rows kept. A session that changes more files than this still lists the newest and counts the rest.</summary>
    public const int MaxEntries = 5000;

    /// <summary>The largest log file that is written or read; a larger file is not a log this code wrote and is ignored.</summary>
    public const int MaxFileBytes = 1024 * 1024;

    /// <summary>The persisted log, relative to the project root.</summary>
    public const string RelativeFile = ".collaboration/session-changes.json";

    /// <summary>The least time between two writes of the file while the log keeps changing.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds( 5 );

    /// <summary>Shown when there is nothing to list.</summary>
    public const string EmptyText = "No project files have changed in this session.";

    private const int MaxPeerLength = 40;

    private sealed class Row
    {
        public SessionChange Change;

        // Position in the order rows were last touched; the smallest is the one dropped at the bound.
        public long Touched;
    }

    private readonly Dictionary<string, Row> rows = new( StringComparer.Ordinal );

    private readonly Func<DateTime> clock;

    private long touches;

    private bool dirty;

    private DateTime lastWrite = DateTime.MinValue;

    public SessionChangeLog( Func<DateTime> clock = null )
    {
        this.clock = clock ?? ( () => DateTime.UtcNow );
    }

    /// <summary>Marks the log as needing a write even though it is empty, so the persisted file describes this session from its start.</summary>
    public void RequestWrite()
    {
        dirty = true;
    }

    /// <summary>The rows currently kept.</summary>
    public int Count => rows.Count;

    /// <summary>Increases with every recorded change, so a card can redraw only when the list is different.</summary>
    public int Revision { get; private set; }

    /// <summary>How many rows were dropped at the bound; they are not shown, but they did change.</summary>
    public int OlderNotShown { get; private set; }

    /// <summary>Every file changed, including the rows that were dropped.</summary>
    public int TotalChanged => rows.Count + OlderNotShown;

    /// <summary>
    /// Records one event. False when nothing was recorded: the path or kind was refused. A code transfer names a path outside Assets, so for
    /// that kind the same rules are applied below an Assets folder that stands in for the project root (every segment rule still applies).
    /// </summary>
    public bool Record( string path, SessionChangeKind kind, string by, string sha256 = null )
    {
        if ( !Enum.IsDefined( kind ) || string.IsNullOrEmpty( path ) ) return false;
        string probe = kind == SessionChangeKind.CodeReceived ? FilePathRules.AllowedRoot + "/" + path : path;
        if ( !FilePathRules.TryNormalize( probe, out _, out _ ) ) return false;

        if ( rows.TryGetValue( path, out Row existing ) )
        {
            SessionChangeKind? merged = Merge( existing.Change.Kind, kind );
            if ( merged == null )
            {
                // Added and then deleted again: the project is as it was, so there is nothing to commit.
                rows.Remove( path );
            }
            else
            {
                existing.Change = new SessionChange( path, merged.Value, CleanPeer( by ), clock(), sha256 );
                existing.Touched = ++touches;
            }
            dirty = true;
            Revision++;
            return true;
        }
        if ( rows.Count >= MaxEntries ) DropOldest();
        rows[path] = new Row { Change = new SessionChange( path, kind, CleanPeer( by ), clock(), sha256 ), Touched = ++touches };
        dirty = true;
        Revision++;
        return true;
    }

    // Folds a new event into the kind already recorded for the path. Null means the row disappears.
    private static SessionChangeKind? Merge( SessionChangeKind existing, SessionChangeKind incoming )
    {
        switch ( existing )
        {
            case SessionChangeKind.Added:
                return incoming == SessionChangeKind.Deleted ? null : SessionChangeKind.Added;
            case SessionChangeKind.Changed:
            case SessionChangeKind.Deleted:
                // The file existed when the session began, so recreating it is a change, never an addition.
                return incoming == SessionChangeKind.Added ? SessionChangeKind.Changed : incoming;
            default:
                return incoming;
        }
    }

    private bool DropOldest()
    {
        string oldest = null;
        long touched = long.MaxValue;
        foreach ( var pair in rows )
        {
            if ( pair.Value.Touched < touched )
            {
                touched = pair.Value.Touched;
                oldest = pair.Key;
            }
        }
        if ( oldest == null ) return false;
        rows.Remove( oldest );
        OlderNotShown++;
        return true;
    }

    // A peer name comes from another machine: control characters would break the list's lines and an enormous one would flood it.
    private static string CleanPeer( string by )
    {
        if ( string.IsNullOrWhiteSpace( by ) ) return "unknown";
        var text = new StringBuilder();
        foreach ( char c in by.Trim() )
        {
            if ( text.Length >= MaxPeerLength ) break;
            text.Append( char.IsControl( c ) ? ' ' : c );
        }
        return text.ToString();
    }

    /// <summary>The rows in path order (ordinal), so the list is the same on every machine and every run.</summary>
    public IReadOnlyList<SessionChange> Rows()
    {
        return rows.Values.Select( r => r.Change ).OrderBy( c => c.Path, StringComparer.Ordinal ).ToList();
    }

    public static string KindLabel( SessionChangeKind kind )
    {
        return kind switch
        {
            SessionChangeKind.Added => "Added",
            SessionChangeKind.Changed => "Changed",
            SessionChangeKind.Deleted => "Deleted",
            SessionChangeKind.ReceivedFromHost => "Received from host",
            _ => "Code received"
        };
    }

    /// <summary>One row as the list shows it: kind, project-relative path, and who changed it.</summary>
    public static string Describe( SessionChange change )
    {
        return KindLabel( change.Kind ) + " " + change.Path + " (" + change.By + ")";
    }

    /// <summary>Every row, one per line, for the clipboard.</summary>
    public string CopyText()
    {
        return string.Join( "\n", Rows().Select( Describe ) );
    }

    public static string SummaryLine( int count )
    {
        if ( count <= 0 ) return EmptyText;
        return count == 1
            ? "1 file changed. Commit it through Git when you are done."
            : count + " files changed. Commit them through Git when you are done.";
    }

    public static string OlderLine( int count )
    {
        return count == 1 ? "1 older change is not shown." : count + " older changes are not shown.";
    }

    public static string PreviousLine( int count )
    {
        return "Previous session: " + ( count == 1 ? "1 file changed." : count + " files changed." );
    }

    /// <summary>
    /// Writes the log when it changed since the last write and either <paramref name="force"/> is set (leaving, stopping) or the interval has
    /// passed. True only when the file was written. A failure is reported as false and never thrown: the log is a convenience and must not
    /// disturb the editor; it is tried again after the next interval.
    /// </summary>
    public bool Flush( string root, bool force )
    {
        if ( !dirty || string.IsNullOrWhiteSpace( root ) ) return false;
        DateTime now = clock();
        if ( !force && now - lastWrite < FlushInterval ) return false;
        lastWrite = now;
        string temporary = null;
        try
        {
            string target = Path.Combine( root, RelativeFile.Replace( '/', Path.DirectorySeparatorChar ) );
            Directory.CreateDirectory( Path.GetDirectoryName( target )! );
            temporary = target + "." + Guid.NewGuid().ToString( "N" ) + ".tmp";
            File.WriteAllBytes( temporary, Serialize( now ) );
            File.Move( temporary, target, true );
            temporary = null;
            dirty = false;
            return true;
        }
        catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException or NotSupportedException )
        {
            return false;
        }
        finally
        {
            try { if ( temporary != null && File.Exists( temporary ) ) File.Delete( temporary ); } catch ( IOException ) { } catch ( UnauthorizedAccessException ) { }
        }
    }

    // The count is always the full total; the rows are written newest first until the file would pass the bound the reader enforces.
    private byte[] Serialize( DateTime now )
    {
        var newestFirst = rows.Values.OrderByDescending( r => r.Touched ).Select( r => r.Change ).ToList();
        int keep = newestFirst.Count;
        while ( true )
        {
            var entries = new JsonArray();
            foreach ( SessionChange change in newestFirst.Take( keep ) )
            {
                entries.Add( new JsonObject
                {
                    ["path"] = change.Path,
                    ["kind"] = change.Kind.ToString(),
                    ["by"] = change.By,
                    ["at"] = change.AtUtc.ToString( "o", System.Globalization.CultureInfo.InvariantCulture ),
                    ["sha256"] = change.Sha256
                } );
            }
            var document = new JsonObject
            {
                ["version"] = 1,
                ["savedUtc"] = now.ToString( "o", System.Globalization.CultureInfo.InvariantCulture ),
                ["count"] = TotalChanged,
                ["olderNotShown"] = OlderNotShown + ( rows.Count - keep ),
                ["entries"] = entries
            };
            byte[] bytes = Encoding.UTF8.GetBytes( document.ToJsonString() );
            if ( bytes.Length <= MaxFileBytes || keep == 0 ) return bytes;
            keep = keep * 9 / 10;
        }
    }

    /// <summary>
    /// The number of files the previous session changed, read from the persisted log, or null when there is no usable file: missing, larger
    /// than <see cref="MaxFileBytes"/>, malformed, or carrying a count that is not a non-negative whole number. Only the count is read; the
    /// rows of an earlier session are never merged into this one.
    /// </summary>
    public static int? ReadPreviousCount( string root )
    {
        if ( string.IsNullOrWhiteSpace( root ) ) return null;
        try
        {
            string path = Path.Combine( root, RelativeFile.Replace( '/', Path.DirectorySeparatorChar ) );
            var info = new FileInfo( path );
            if ( !info.Exists || info.Length > MaxFileBytes ) return null;
            using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete );
            var buffer = new byte[MaxFileBytes + 1];
            int read = 0;
            while ( read < buffer.Length )
            {
                int n = stream.Read( buffer, read, buffer.Length - read );
                if ( n == 0 ) break;
                read += n;
            }
            if ( read > MaxFileBytes ) return null;
            JsonNode node = JsonNode.Parse( Encoding.UTF8.GetString( buffer, 0, read ) );
            if ( node is not JsonObject document || document["count"] is not JsonValue value || !value.TryGetValue( out int count ) || count < 0 ) return null;
            return count;
        }
        catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException or ArgumentException )
        {
            return null;
        }
    }
}
