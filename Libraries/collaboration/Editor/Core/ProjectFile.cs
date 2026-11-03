using System;
using System.IO;
using System.Linq;

namespace TeamCreate;

/// <summary>
/// Finds a project's .sbproj file by one rule for every caller: the first in ordinal order in the project folder itself. A folder with two
/// project files is used, not refused, and a folder with none is reported by name rather than failing deep inside a read.
/// </summary>
public static class ProjectFile
{
    /// <summary>The project file of the folder, or null when it has none, does not exist or cannot be listed.</summary>
    public static string Find( string root )
    {
        if ( string.IsNullOrEmpty( root ) ) return null;
        try
        {
            return Directory.EnumerateFiles( root, "*.sbproj", SearchOption.TopDirectoryOnly ).OrderBy( x => x, StringComparer.Ordinal ).FirstOrDefault();
        }
        catch ( Exception ex ) when ( ex is IOException or UnauthorizedAccessException )
        {
            return null;
        }
    }

    /// <summary>The project file of the folder, or an exception that names the folder.</summary>
    public static string Require( string root )
    {
        return Find( root ) ?? throw new InvalidOperationException( "No .sbproj file was found in " + root + "." );
    }
}
