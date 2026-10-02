using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TeamCreate;

/// <summary>Whether a file is something the engine loads as content or something that runs. The two are treated very differently when a guest joins.</summary>
public enum ProjectFileKind
{
    /// <summary>Content (models, textures, scenes, materials). Safe to receive automatically.</summary>
    Asset,

    /// <summary>Source, project settings, the project descriptor, installed libraries. Never applied without the receiver's consent, because it runs on their machine.</summary>
    Code,
}

/// <summary>How one file differs between the guest and the host.</summary>
public enum FileDifferenceKind
{
    /// <summary>The host has the file and the guest does not.</summary>
    Missing,

    /// <summary>Both have the file and its content differs.</summary>
    Changed,

    /// <summary>Only the guest has the file. It is the guest's own work: nothing to receive and never a reason to refuse.</summary>
    Extra,
}

public sealed record FileDifference( string Path, FileDifferenceKind Kind, ProjectFileKind FileKind );

public sealed record JoinReadinessResult(
    IReadOnlyList<FileDifference> Differences,
    IReadOnlyList<string> Reasons,
    bool CanJoinWithoutTransfer,
    bool NeedsAssetTransfer,
    bool NeedsCodeConsent );

/// <summary>
/// Decides what a guest has to do before joining a host: which project files differ, whether anything must be received, and what the guest
/// should be told. Joining used to be refused with an eight-character hash that named nothing; this names the files, so a later step can fetch the
/// missing content automatically while code (which would run on the receiver's machine) always waits for consent.
///
/// <para>The input is one map per side from a project-relative path to the SHA-256 hex of that file. <see cref="ProjectManifest"/> exposes only an
/// aggregate hash today, so a later step has to produce these maps. Pure and deterministic: no file or network access.</para>
/// </summary>
public static class JoinReadiness
{
    /// <summary>How many paths one reason names before it says "and N more": a dialog that lists two hundred files helps nobody.</summary>
    private const int MaxListedPaths = 5;

    /// <summary>A path longer than this is shown shortened. Paths come from the host, and one absurd name must not flood the guest's screen.</summary>
    private const int MaxDisplayedPathLength = 120;

    private const string LibraryReason = "The installed library files differ from the host's. Install the same library version on both machines.";

    private static readonly string[] CodeFolders = { "Code", "Editor", "Libraries", "ProjectSettings" };

    private static readonly string[] CodeExtensions = { ".sbproj", ".csproj", ".cs", ".razor", ".scss" };

    /// <summary>
    /// <see cref="ProjectFileKind.Code"/> for anything under the top-level Code, Editor, Libraries or ProjectSettings folder and for project
    /// descriptors and source files wherever they sit (a .cs under Assets is still code); everything else is an asset. Folder and extension
    /// match ignores case, and a backslash counts as a separator. Only the FIRST folder decides: Assets/Code/readme.png is an asset.
    /// </summary>
    public static ProjectFileKind Classify( string relativePath )
    {
        if ( string.IsNullOrWhiteSpace( relativePath ) )
        {
            throw new ArgumentException( "A file path is required.", nameof( relativePath ) );
        }
        string path = relativePath.Replace( '\\', '/' );
        int slash = path.IndexOf( '/' );
        // A name with no separator is a root file, not something "under" a folder, so "Code" alone is not the Code folder.
        if ( slash > 0 )
        {
            string first = path.Substring( 0, slash );
            if ( CodeFolders.Any( folder => string.Equals( folder, first, StringComparison.OrdinalIgnoreCase ) ) )
            {
                return ProjectFileKind.Code;
            }
        }
        if ( CodeExtensions.Any( extension => path.EndsWith( extension, StringComparison.OrdinalIgnoreCase ) ) )
        {
            return ProjectFileKind.Code;
        }
        return ProjectFileKind.Asset;
    }

    /// <summary>
    /// Compares the guest's files with the host's by path (case-insensitive, as on Windows) and hash. Extra files (only the guest has them) are
    /// listed but never block joining; Missing and Changed files do, and decide whether assets must be received or code needs the guest's consent.
    /// <paramref name="guestDirtyScenePaths"/> only adds a reason: the join backs the scene up and replaces it, so it informs and does not block.
    /// Throws <see cref="ArgumentNullException"/> for a null argument and <see cref="ArgumentException"/> for an empty path (a manifest that
    /// names a file with no name is malformed and must be refused by the caller before asking).
    /// </summary>
    public static JoinReadinessResult Evaluate(
        IReadOnlyList<string> guestDirtyScenePaths,
        IReadOnlyDictionary<string, string> guestFiles,
        IReadOnlyDictionary<string, string> hostFiles )
    {
        if ( guestDirtyScenePaths == null ) throw new ArgumentNullException( nameof( guestDirtyScenePaths ) );
        if ( guestFiles == null ) throw new ArgumentNullException( nameof( guestFiles ) );
        if ( hostFiles == null ) throw new ArgumentNullException( nameof( hostFiles ) );

        var guest = Index( guestFiles, nameof( guestFiles ) );
        var host = Index( hostFiles, nameof( hostFiles ) );

        var differences = new List<FileDifference>();
        foreach ( var (key, hostEntry) in host )
        {
            if ( !guest.TryGetValue( key, out var guestEntry ) )
            {
                differences.Add( new FileDifference( hostEntry.Path, FileDifferenceKind.Missing, Classify( hostEntry.Path ) ) );
            }
            else if ( !SameContent( guestEntry.Hash, hostEntry.Hash ) )
            {
                // The host's spelling is the one the receiver would fetch, so it is the one reported.
                differences.Add( new FileDifference( hostEntry.Path, FileDifferenceKind.Changed, Classify( hostEntry.Path ) ) );
            }
        }
        foreach ( var (key, guestEntry) in guest )
        {
            if ( !host.ContainsKey( key ) )
            {
                differences.Add( new FileDifference( guestEntry.Path, FileDifferenceKind.Extra, Classify( guestEntry.Path ) ) );
            }
        }
        differences.Sort( ( a, b ) => StringComparer.OrdinalIgnoreCase.Compare( a.Path, b.Path ) );

        var blocking = differences.Where( d => d.Kind != FileDifferenceKind.Extra ).ToList();
        var blockingAssets = blocking.Where( d => d.FileKind == ProjectFileKind.Asset ).ToList();
        var blockingCode = blocking.Where( d => d.FileKind == ProjectFileKind.Code ).ToList();
        var blockingLibraries = blockingCode.Where( d => IsLibraryFile( d.Path ) ).ToList();
        // A library cannot be approved and received like a project file (it has to be installed), so it gets its own reason and is
        // not listed again as code awaiting approval.
        var blockingOtherCode = blockingCode.Where( d => !IsLibraryFile( d.Path ) ).ToList();

        var reasons = new List<string>();
        var seenScenes = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
        foreach ( string scene in guestDirtyScenePaths )
        {
            if ( string.IsNullOrWhiteSpace( scene ) )
            {
                throw new ArgumentException( "A dirty scene path must not be empty.", nameof( guestDirtyScenePaths ) );
            }
            if ( seenScenes.Add( scene ) )
            {
                reasons.Add( $"Scene \"{Display( scene )}\" has unsaved changes. They will be backed up and replaced by the host's scene." );
            }
        }
        if ( blockingLibraries.Count > 0 )
        {
            reasons.Add( LibraryReason );
        }
        if ( blockingAssets.Count > 0 )
        {
            reasons.Add( $"{blockingAssets.Count} project {(blockingAssets.Count == 1 ? "file differs" : "files differ")} from the host's: {NamePaths( blockingAssets )}." );
        }
        if ( blockingOtherCode.Count > 0 )
        {
            reasons.Add( blockingOtherCode.Count == 1
                ? $"1 code file differs and needs your approval before it is received: {NamePaths( blockingOtherCode )}."
                : $"{blockingOtherCode.Count} code files differ and need your approval before they are received: {NamePaths( blockingOtherCode )}." );
        }

        bool needsAssets = blockingAssets.Count > 0;
        bool needsCode = blockingCode.Count > 0;
        return new JoinReadinessResult( differences, reasons, !needsAssets && !needsCode, needsAssets, needsCode );
    }

    private static bool IsLibraryFile( string path )
    {
        return path.Replace( '\\', '/' ).StartsWith( "Libraries/", StringComparison.OrdinalIgnoreCase );
    }

    /// <summary>
    /// One entry per case-insensitive path, with the spelling to report. A hash that is missing proves nothing, so two keys that collapse
    /// to the same Windows file (their real content is unknowable) are stored with no hash and always compare as different.
    /// The smallest spelling is kept so the result does not depend on the dictionary's enumeration order.
    /// </summary>
    private static Dictionary<string, (string Path, string Hash)> Index( IReadOnlyDictionary<string, string> files, string argument )
    {
        var index = new Dictionary<string, (string Path, string Hash)>( StringComparer.OrdinalIgnoreCase );
        foreach ( var pair in files.OrderBy( p => p.Key, StringComparer.Ordinal ) )
        {
            if ( string.IsNullOrWhiteSpace( pair.Key ) )
            {
                throw new ArgumentException( "A manifest entry has no file path.", argument );
            }
            index[pair.Key] = index.TryGetValue( pair.Key, out var first ) ? (first.Path, null) : (pair.Key, pair.Value);
        }
        return index;
    }

    /// <summary>Equal only when both hashes are present and match (hex case ignored). Fail closed: an unknown hash is never "the same".</summary>
    private static bool SameContent( string guestHash, string hostHash )
    {
        return !string.IsNullOrEmpty( guestHash ) && !string.IsNullOrEmpty( hostHash ) && string.Equals( guestHash, hostHash, StringComparison.OrdinalIgnoreCase );
    }

    /// <summary>"a", "a and b", "a, b and c", and past five "a, b, c, d, e and N more".</summary>
    private static string NamePaths( IReadOnlyList<FileDifference> files )
    {
        var shown = files.Take( MaxListedPaths ).Select( f => Display( f.Path ) ).ToList();
        int more = files.Count - shown.Count;
        if ( more > 0 )
        {
            return string.Join( ", ", shown ) + $" and {more} more";
        }
        return shown.Count == 1
            ? shown[0]
            : string.Join( ", ", shown.Take( shown.Count - 1 ) ) + " and " + shown[shown.Count - 1];
    }

    /// <summary>
    /// A path as it may appear in a sentence the guest reads. Paths come from the host, so control characters (line breaks that fake a second
    /// message, NUL) are replaced and a very long name is shortened. The difference list keeps the raw path; the receiver validates it with
    /// <see cref="FilePathRules"/> before touching the disk.
    /// </summary>
    private static string Display( string path )
    {
        var builder = new StringBuilder( Math.Min( path.Length, MaxDisplayedPathLength ) );
        foreach ( char c in path )
        {
            builder.Append( char.IsControl( c ) ? '?' : c );
        }
        if ( builder.Length > MaxDisplayedPathLength )
        {
            builder.Length = MaxDisplayedPathLength - 3;
            builder.Append( "..." );
        }
        return builder.ToString();
    }
}
