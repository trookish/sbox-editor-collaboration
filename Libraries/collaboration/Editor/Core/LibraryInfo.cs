namespace TeamCreate;

public static class LibraryInfo
{
    public const string LibraryVersion = "1.2.0";

    public const string LibraryIdent = "editor_collaboration";

    /// <summary>The full ident a new project's settings should name: the project's organization, a dot, then this library's name.</summary>
    public static string DefaultLibraryIdent(string org)
    {
        return string.IsNullOrWhiteSpace(org) ? null : org.Trim() + "." + LibraryIdent;
    }
}
