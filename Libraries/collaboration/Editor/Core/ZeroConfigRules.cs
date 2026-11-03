using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TeamCreate;

/// <summary>
/// The project id both computers use. A configured id wins, so existing projects keep theirs; otherwise it is derived from the project's
/// own ident (the .sbproj both computers already share), so a project needs no settings file for two people to find each other.
/// </summary>
public static class ProjectIdentity
{
    public static string Resolve(string configuredId, string projectIdent)
    {
        if (!string.IsNullOrWhiteSpace(configuredId))
        {
            return configuredId;
        }
        if (string.IsNullOrWhiteSpace(projectIdent))
        {
            return null;
        }
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("teamcreate-project:" + projectIdent.Trim().ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }
}

public sealed record GitIgnoreResult(bool Changed, string Text, string Error = null);

/// <summary>Keeps the session folder (.collaboration/, which holds the relay key and recovery copies) out of a project's Git history.</summary>
public static class GitIgnoreRules
{
    public const string Rule = ".collaboration/";

    public const string Notice = "Added .collaboration/ to .gitignore.";

    /// <summary>The .gitignore text with the rule added when no equivalent rule is there, in the file's own line endings.</summary>
    public static GitIgnoreResult Apply(string text)
    {
        text ??= string.Empty;
        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            string bare = line.TrimStart('/');
            if (bare.EndsWith("/**", StringComparison.Ordinal))
            {
                bare = bare[..^3];
            }
            if (bare.TrimEnd('/') == ".collaboration")
            {
                return new GitIgnoreResult(false, text);
            }
        }
        string prefix = text.Length == 0 || text.EndsWith('\n') ? text : text + newline;
        return new GitIgnoreResult(true, prefix + Rule + newline);
    }

    /// <summary>Adds the rule to the project's .gitignore when it has one; a project without a .gitignore is left as it is.</summary>
    public static GitIgnoreResult EnsureIgnored(string projectRoot)
    {
        string path = Path.Combine(projectRoot ?? string.Empty, ".gitignore");
        try
        {
            if (!File.Exists(path))
            {
                return new GitIgnoreResult(false, null);
            }
            GitIgnoreResult result = Apply(File.ReadAllText(path));
            if (!result.Changed)
            {
                return result;
            }
            string temp = path + ".tmp";
            File.WriteAllText(temp, result.Text, new UTF8Encoding(false));
            File.Move(temp, path, true);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new GitIgnoreResult(false, null, ".gitignore could not be updated: " + ex.Message);
        }
    }
}

/// <summary>Opens the Collaboration dock the first time the library loads in a project, so a new user does not have to find it.</summary>
public static class FirstRunRules
{
    public static bool ShouldOpenDock(bool alreadyOpened, bool soloCopy)
    {
        return !alreadyOpened && !soloCopy;
    }

    /// <summary>One editor cookie per project folder, independent of slash direction, case and a trailing separator.</summary>
    public static string CookieKey(string projectRoot)
    {
        string normal = (projectRoot ?? string.Empty).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        return "teamcreate.first-run." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normal))).Substring(0, 16).ToLowerInvariant();
    }
}

/// <summary>Puts the host's invite on the clipboard the first time each code appears.</summary>
public static class InviteCopyRules
{
    public const string Toast = "Invite copied. Send it to your collaborators.";

    public static bool ShouldCopy(string code, string lastCopied)
    {
        return !string.IsNullOrWhiteSpace(code) && !string.Equals(code, lastCopied, StringComparison.Ordinal);
    }
}
