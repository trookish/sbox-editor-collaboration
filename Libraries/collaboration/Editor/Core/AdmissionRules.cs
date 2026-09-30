using System;
using System.Text;

namespace TeamCreate;

public sealed record PeerFacts(string SteamId, bool IdentityVerified, bool IsFriend, bool IsOrgMember, string PeerLibraryVersion, string PeerProjectManifest, string PeerOrgIdent, bool IsHostSelf = false);

public static class AdmissionRules
{
    public const string VerifiedGate = "identity";

    public const string FriendGate = "friend";

    public const string OrgGate = "org";

    public const string LibraryGate = "library";

    public const string ManifestGate = "manifest";

    private const int MaxPeerTextLength = 64;

    public static string Evaluate(PeerFacts facts, CollaborationConfig config, string hostLibraryVersion, string hostManifest, out string failedGate, string peerName = null)
    {
        failedGate = null;
        string text = SafePeerText(peerName, 64);
        string text2 = ((text.Length == 0) ? string.Empty : (text + " "));
        if ((object)facts == null || !facts.IdentityVerified)
        {
            failedGate = "identity";
            return text2 + "could not be verified by s&box's auth service.";
        }
        if (config != null && config.RequireSteamFriend && !facts.IsFriend && !facts.IsHostSelf)
        {
            failedGate = "friend";
            return text2 + "is not on your Steam friends list. Add them on Steam and reconnect.";
        }
        if (config != null && config.RequireOrgMember && (!facts.IsOrgMember || !string.Equals(facts.PeerOrgIdent, config.Org, StringComparison.OrdinalIgnoreCase)))
        {
            failedGate = "org";
            return text2 + "is not a member of the organization " + config.Org + ".";
        }
        Version version = config?.LibraryVersion ?? ParseVersion(hostLibraryVersion);
        if ((object)version != null)
        {
            string text3 = (string.IsNullOrWhiteSpace(facts.PeerLibraryVersion) ? null : facts.PeerLibraryVersion);
            if (text3 == null || !Version.TryParse(text3, out Version result) || !VersionRules.IsAtLeast(result, version))
            {
                string text4 = SafePeerText(text3, 64);
                failedGate = "library";
                return text2 + $"runs collaboration library {((text4.Length == 0) ? "an unknown version" : text4)}; this session requires {version}. Update the library and reconnect.";
            }
        }
        if (!string.IsNullOrWhiteSpace(hostManifest) && !string.Equals(facts.PeerProjectManifest, hostManifest, StringComparison.Ordinal))
        {
            string text5 = SafePeerText(facts.PeerProjectManifest, 64);
            string value = ((text5.Length == 0) ? "unknown" : text5);
            failedGate = "manifest";
            return text2 + $"has a different copy of this project ({value} vs {hostManifest}). Sync the project (Git and Asset Party) before joining.";
        }
        return null;
    }

    private static Version ParseVersion(string value)
    {
        Version result;
        return (!string.IsNullOrWhiteSpace(value) && Version.TryParse(value, out result)) ? result : null;
    }

    private static string SafePeerText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        string text = CollaborationDiagnostics.Bounded(value, maxLength);
        StringBuilder stringBuilder = new StringBuilder(text.Length);
        string text2 = text;
        foreach (char c in text2)
        {
            if (!char.IsControl(c))
            {
                stringBuilder.Append(c);
            }
        }
        return stringBuilder.ToString();
    }
}
