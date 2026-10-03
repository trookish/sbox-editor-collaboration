using System;
using System.IO;

namespace TeamCreate;

/// <summary>
/// The rules a received file path must pass before anything is written for it. A path comes from another person's machine, so a hostile or
/// buggy host must never be able to name a file outside the project: not by "..", not by a drive letter or a UNC share, not by an NTFS
/// alternate data stream, not by a Windows device name, and not through a symbolic link or junction already sitting inside the project.
/// Only the Assets folder may be written; code, settings and libraries are never received this way.
/// Every refusal is a specific, formal sentence and never repeats the offending path, which may hold control characters or be enormous.
/// </summary>
public static class FilePathRules
{
    /// <summary>The only top-level folder a transferred file may land in. Compared case-sensitively: the sender spells it exactly.</summary>
    public const string AllowedRoot = "Assets";

    private const int MaxPathLength = 240;

    private const int MaxSegmentLength = 100;

    private static readonly string[] ReservedDeviceNames = { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" };

    // Characters Windows refuses in a file name. The colon is refused for the whole path earlier, with its own message.
    private static readonly char[] InvalidNameCharacters = { '<', '>', '"', '|', '?', '*' };

    /// <summary>
    /// True when <paramref name="relativePath"/> is a plain relative path with "/" separators below the Assets folder; it is then returned
    /// unchanged (nothing is rewritten, so what was validated is exactly what is used). Otherwise false, <paramref name="normalized"/> is null
    /// so a caller that ignores the result fails loudly instead of joining an empty string onto a root, and <paramref name="error"/> says why.
    /// </summary>
    public static bool TryNormalize( string relativePath, out string normalized, out string error )
    {
        return TryNormalize( relativePath, false, out normalized, out error );
    }

    /// <summary>The top-level folders a consented code transfer may write into (a script may also sit under Assets). Compared case-sensitively.</summary>
    public static readonly string[] CodeRoots = { AllowedRoot, "Code", "Editor", "ProjectSettings" };

    /// <summary>
    /// The same rules for a path of a consented code transfer: every check is identical, but the first folder may be any of <see cref="CodeRoots"/>.
    /// A root-level file (the project descriptor) is refused: it names the guest's own project and is never overwritten.
    /// </summary>
    public static bool TryNormalizeCode( string relativePath, out string normalized, out string error )
    {
        return TryNormalize( relativePath, true, out normalized, out error );
    }

    private static bool TryNormalize( string relativePath, bool code, out string normalized, out string error )
    {
        string problem = Validate( relativePath, code );
        if ( problem != null )
        {
            normalized = null;
            error = problem;
            return false;
        }
        normalized = relativePath;
        error = null;
        return true;
    }

    /// <summary>
    /// Resolves an already-normalised relative path under <paramref name="rootDirectory"/> to a full path, or refuses. It re-runs
    /// <see cref="TryNormalize"/> so a caller cannot skip it, requires the result to be strictly inside the root, and refuses when any
    /// EXISTING component of the path (including the last) is a symbolic link or junction, because a write through a link inside Assets
    /// would land wherever the link points. Nothing is created. The answer is only as fresh as the moment it is given: a writer must still
    /// open the file without following links, since a link could appear between this check and the write.
    /// </summary>
    public static bool TryResolveUnder( string rootDirectory, string normalizedRelativePath, out string fullPath, out string error )
    {
        return TryResolveUnder( rootDirectory, normalizedRelativePath, false, out fullPath, out error );
    }

    /// <summary>The resolution of <see cref="TryResolveUnder(string, string, out string, out string)"/> for a code path (see <see cref="TryNormalizeCode"/>).</summary>
    public static bool TryResolveUnderCode( string rootDirectory, string normalizedRelativePath, out string fullPath, out string error )
    {
        return TryResolveUnder( rootDirectory, normalizedRelativePath, true, out fullPath, out error );
    }

    private static bool TryResolveUnder( string rootDirectory, string normalizedRelativePath, bool code, out string fullPath, out string error )
    {
        fullPath = null;
        if ( string.IsNullOrWhiteSpace( rootDirectory ) )
        {
            error = "The project root directory is empty.";
            return false;
        }
        if ( !TryNormalize( normalizedRelativePath, code, out string normalized, out error ) )
        {
            return false;
        }

        string root;
        string candidate;
        try
        {
            root = Path.GetFullPath( rootDirectory );
            candidate = Path.GetFullPath( Path.Combine( root, normalized.Replace( '/', Path.DirectorySeparatorChar ) ) );
        }
        catch ( Exception ex ) when ( ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException )
        {
            error = "The project root directory or the file path cannot be resolved to a valid location.";
            return false;
        }
        if ( !IsStrictlyInside( root, candidate ) )
        {
            error = "The file path resolves to a location outside the project root.";
            return false;
        }

        string current = root;
        foreach ( string segment in normalized.Split( '/' ) )
        {
            current = Path.Combine( current, segment );
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes( current );
            }
            catch ( Exception ex ) when ( ex is FileNotFoundException || ex is DirectoryNotFoundException )
            {
                // Nothing exists from here down, so no link can lie deeper on the path.
                break;
            }
            catch ( Exception ex ) when ( ex is IOException || ex is UnauthorizedAccessException )
            {
                error = "A folder on the file path cannot be inspected, so it cannot be shown to stay inside the project.";
                return false;
            }
            if ( ( attributes & FileAttributes.ReparsePoint ) != 0 )
            {
                error = "A folder or file on the path is a symbolic link or junction, so a write could leave the project.";
                return false;
            }
        }

        fullPath = candidate;
        error = null;
        return true;
    }

    /// <summary>
    /// True when <paramref name="candidatePath"/> is inside <paramref name="rootDirectory"/> and not the root itself. The root is compared
    /// WITH a trailing separator so a sibling that merely starts with the root's name (root "proj", candidate "proj-evil/x") is outside.
    /// Case-insensitive where the separator is a backslash (Windows). Internal because a normalised path cannot escape the root, so the
    /// check is a second line of defence that no public input reaches; the tests call it directly.
    /// </summary>
    internal static bool IsStrictlyInside( string rootDirectory, string candidatePath )
    {
        string root = Path.GetFullPath( rootDirectory );
        string candidate = Path.GetFullPath( candidatePath );
        if ( !Path.EndsInDirectorySeparator( root ) )
        {
            root += Path.DirectorySeparatorChar;
        }
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.Length > root.Length && candidate.StartsWith( root, comparison );
    }

    /// <summary>The first rule the path breaks as a sentence, or null when it breaks none.</summary>
    private static string Validate( string path, bool code )
    {
        if ( string.IsNullOrWhiteSpace( path ) )
        {
            return "The file path is empty.";
        }
        if ( path.Length > MaxPathLength )
        {
            return $"The file path is longer than {MaxPathLength} characters.";
        }
        foreach ( char c in path )
        {
            if ( char.IsControl( c ) )
            {
                return "The file path contains a control character.";
            }
        }
        if ( path.Contains( '\\' ) )
        {
            return "The file path contains a backslash; only \"/\" may separate folders.";
        }
        // One rule covers drive letters ("C:/"), drive-relative paths ("C:x") and NTFS alternate data streams ("a.png:stream"):
        // none of them has a legitimate use in a file received into the project.
        if ( path.Contains( ':' ) )
        {
            return "The file path contains a colon, which would name a drive or an alternate data stream.";
        }
        if ( path[0] == '/' )
        {
            return "The file path must be relative; it must not start with \"/\".";
        }
        if ( path[path.Length - 1] == '/' )
        {
            return "The file path must name a file; it must not end with \"/\".";
        }

        string[] segments = path.Split( '/' );
        foreach ( string segment in segments )
        {
            string problem = ValidateSegment( segment );
            if ( problem != null )
            {
                return problem;
            }
        }
        if ( code )
        {
            if ( !Array.Exists( CodeRoots, x => string.Equals( x, segments[0], StringComparison.Ordinal ) ) )
            {
                return "The file path must start with the \"Code\", \"Editor\", \"ProjectSettings\" or \"Assets\" folder; no other folder may be written by a code transfer.";
            }
            return segments.Length < 2 ? "The file path must name a file inside a project folder." : null;
        }
        if ( !string.Equals( segments[0], AllowedRoot, StringComparison.Ordinal ) )
        {
            return $"The file path must start with the \"{AllowedRoot}\" folder; no other folder may be written.";
        }
        if ( segments.Length < 2 )
        {
            return $"The file path must name a file inside \"{AllowedRoot}\".";
        }
        return null;
    }

    private static string ValidateSegment( string segment )
    {
        if ( segment.Length == 0 )
        {
            return "The file path contains an empty segment (\"//\").";
        }
        if ( segment == "." || segment == ".." )
        {
            return "The file path contains a \".\" or \"..\" segment.";
        }
        if ( segment.Length > MaxSegmentLength )
        {
            return $"A segment of the file path is longer than {MaxSegmentLength} characters.";
        }
        // Windows silently drops a trailing space or dot, so "Assets/a." and "Assets/a" would be the same file under two names.
        char last = segment[segment.Length - 1];
        if ( last == ' ' || last == '.' )
        {
            return "A segment of the file path ends with a space or a dot, which Windows silently removes.";
        }
        if ( segment.IndexOfAny( InvalidNameCharacters ) >= 0 )
        {
            return "A segment of the file path contains a character that is not allowed in Windows file names.";
        }
        if ( IsReservedDeviceName( segment ) )
        {
            return "A segment of the file path is a reserved Windows device name.";
        }
        return null;
    }

    /// <summary>
    /// Windows treats CON, PRN, AUX, NUL, COM1-9 and LPT1-9 as devices in any folder and with any extension ("nul.txt", "aux.tar.gz"), so
    /// the part before the FIRST dot decides, ignoring spaces before that dot. The superscript digits (COM¹) are devices too, as are CONIN$ and CONOUT$.
    /// </summary>
    private static bool IsReservedDeviceName( string segment )
    {
        int dot = segment.IndexOf( '.' );
        string stem = ( dot < 0 ? segment : segment.Substring( 0, dot ) ).TrimEnd( ' ' );
        foreach ( string reserved in ReservedDeviceNames )
        {
            if ( string.Equals( stem, reserved, StringComparison.OrdinalIgnoreCase ) )
            {
                return true;
            }
        }
        if ( stem.Length == 4 && ( stem.StartsWith( "COM", StringComparison.OrdinalIgnoreCase ) || stem.StartsWith( "LPT", StringComparison.OrdinalIgnoreCase ) ) )
        {
            char digit = stem[3];
            return ( digit >= '1' && digit <= '9' ) || digit == '\u00b9' || digit == '\u00b2' || digit == '\u00b3';
        }
        return false;
    }
}
