using System;

namespace TeamCreate;

/// <summary>
/// The rules that let one person test a two-editor session alone, and the fences around them.
///
/// A real join needs two different Steam accounts: the host refuses a second connection from the same account,
/// and it asks Steam's auth service to confirm every guest. With one account, the only way to exercise a full
/// join is to let the host accept ONE reserved, obviously synthetic identity for a guest editor. That is a
/// deliberate hole in admission, so it is confined three ways: it exists only inside a disposable copy of the
/// project (a folder under <c>.collaboration/test-copies/</c> that carries a marker file the test launcher writes),
/// it can only be installed by local editor code, and it accepts exactly the reserved identity below and nothing
/// else; every other guest still goes through the real verification.
/// </summary>
public static class TestHarnessRules
{
    /// <summary>The folder that holds disposable copies, relative to the real project's <c>.collaboration</c> folder.</summary>
    public const string CopiesSegment = "/.collaboration/test-copies/";

    /// <summary>Written by the test launcher into each copy. A folder that merely has a matching name is not enough.</summary>
    public const string MarkerFile = "disposable-test-copy.txt";

    /// <summary>
    /// A well-formed Steam account number at the very top of the individual-account range, chosen so it cannot be
    /// mistaken for a person. It is the only identity the test verifier will vouch for.
    /// </summary>
    public const string SoloGuestSteamId = "76561202255233023";

    public const string SoloGuestName = "Solo Test Guest";

    /// <summary>
    /// A fresh secret for one solo-test run: 32 random bytes as text, printable and long enough to pass the handshake's shape check.
    /// It lives in memory only. The host vouches for the reserved identity only when it presents THIS value, so a remote peer that merely
    /// claims the reserved account number, even one that has the invite, is not admitted.
    /// </summary>
    public static string NewRunToken()
    {
        return "solo-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>Constant-time comparison; false when either side is empty.</summary>
    public static bool TokenMatches(string presented, string expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
        {
            return false;
        }
        byte[] a = System.Text.Encoding.UTF8.GetBytes(presented);
        byte[] b = System.Text.Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>True only for a path that is a folder inside <c>.collaboration/test-copies/</c>.</summary>
    public static bool IsDisposableCopyPath(string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return false;
        }
        string path = projectRoot.Replace('\\', '/');
        if (path.Contains("/../", StringComparison.Ordinal) || path.EndsWith("/..", StringComparison.Ordinal))
        {
            return false;
        }
        int at = path.IndexOf(CopiesSegment, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return false;
        }
        // Something must follow the segment: the copy's own folder name. The folder that merely holds the copies
        // is not itself a copy.
        string rest = path.Substring(at + CopiesSegment.Length).Trim('/');
        return rest.Length > 0;
    }

    /// <summary>The path check plus the marker file, which is what the session insists on before it honours the test verifier.</summary>
    public static bool IsDisposableCopy(string projectRoot, bool markerFilePresent)
    {
        return markerFilePresent && IsDisposableCopyPath(projectRoot);
    }

    /// <summary>The one identity the test verifier may accept. Compared as text, exactly, after trimming.</summary>
    public static bool IsSyntheticIdentity(string steamId)
    {
        return string.Equals(steamId?.Trim(), SoloGuestSteamId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a hello may be answered by the test verifier instead of Steam's service: the reserved identity AND this run's token.
    /// Anything else, including the reserved identity with any other token and the host's own real account, is left to real verification.
    /// </summary>
    public static bool MayVouchFor(string steamId, string presentedToken, string runToken)
    {
        return IsSyntheticIdentity(steamId) && TokenMatches(presentedToken, runToken);
    }
}
