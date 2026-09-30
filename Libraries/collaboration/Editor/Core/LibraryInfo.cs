namespace TeamCreate;

public static class LibraryInfo
{
    public const string LibraryVersion = "1.2.0";

    /// <summary>The ident this workspace's own descriptor (Libraries/collaboration/collaboration.sbproj) carries.</summary>
    public const string LibraryIdent = "editor_collaboration";

    /// <summary>The ident the package was published under on Asset Party. A name is permanent once published, and the Library Manager installs the library into a project under it, so a project that installed the library from Asset Party holds this ident, not <see cref="LibraryIdent"/>.</summary>
    public const string PublishedIdent = "sbox_editor_collab";

    /// <summary>The full ident a new project's settings should name: the project's organization, a dot, then the name the library is installed under.</summary>
    public static string DefaultLibraryIdent(string org)
    {
        return string.IsNullOrWhiteSpace(org) ? null : org.Trim() + "." + PublishedIdent;
    }

    /// <summary>The package name without its organization: "cyn.sbox_editor_collab" gives "sbox_editor_collab". A bare name is returned as it is.</summary>
    public static string IdentPart(string fullIdent)
    {
        if (string.IsNullOrWhiteSpace(fullIdent))
        {
            return null;
        }
        string trimmed = fullIdent.Trim();
        int dot = trimmed.LastIndexOf('.');
        return dot < 0 ? trimmed : trimmed.Substring(dot + 1);
    }

    /// <summary>
    /// Whether an installed library is this one. The same code ships under the ident the project's settings name, under the published Asset Party
    /// ident, and under the workspace descriptor's ident, so all three count; matching only one of them reported an installed library as missing.
    /// </summary>
    public static bool IsThisLibrary(string configuredIdent, string installedIdent)
    {
        string installed = IdentPart(installedIdent);
        if (installed == null)
        {
            return false;
        }
        return string.Equals(installed, IdentPart(configuredIdent), System.StringComparison.Ordinal)
            || string.Equals(installed, PublishedIdent, System.StringComparison.Ordinal)
            || string.Equals(installed, LibraryIdent, System.StringComparison.Ordinal);
    }
}
