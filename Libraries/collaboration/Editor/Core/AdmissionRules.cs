using System;
using System.Collections.Generic;
using System.Security.Cryptography;
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
        // File differences are reconciled after admission: assets are synchronized and code differences are reported, never received.
        // The manifest therefore stops being a gate; the engine build still is (BaselineRefusal).
        return null;
    }

    public const string BaselineRefusalMessage = "Project/scene baseline differs (engine, source, assets or metadata). Sync copies in Git before joining.";

    public const string EngineRefusalMessage = "This peer runs a different s&box engine build from the host's. Update s&box to the same version on both computers and reconnect.";

    public const string EngineUnknownRefusalMessage = "This peer's project differs from the host's and its collaboration library cannot synchronize project files. Update the library and reconnect.";

    /// <summary>
    /// The baseline gate of the hello. When the peer's files match the host's (or the host has no manifest to compare), the full baseline
    /// must be one the host accepts, exactly as before file synchronization. When the files differ, the full baseline necessarily differs
    /// too (it hashes every file), so only the engine fingerprint is compared: the files are reconciled after admission, the engine build
    /// cannot be. A peer that sends no engine fingerprint predates file synchronization and is refused, because nothing would reconcile its
    /// files; a host that has no fingerprint of its own falls back to the full baseline. Null when the peer passes.
    /// </summary>
    public static string BaselineRefusal(string peerManifest, string hostManifest, string peerBaseline, ICollection<string> compatibleBaselines, string peerEngine, string hostEngine)
    {
        bool sameFiles = string.IsNullOrWhiteSpace(hostManifest) || string.Equals(peerManifest, hostManifest, StringComparison.Ordinal);
        if (sameFiles || string.IsNullOrWhiteSpace(hostEngine))
        {
            return (compatibleBaselines != null && peerBaseline != null && compatibleBaselines.Contains(peerBaseline)) ? null : BaselineRefusalMessage;
        }
        if (string.IsNullOrWhiteSpace(peerEngine))
        {
            return EngineUnknownRefusalMessage;
        }
        return string.Equals(peerEngine, hostEngine, StringComparison.Ordinal) ? null : EngineRefusalMessage;
    }

    /// <summary>
    /// A fingerprint of the engine build from the module version ids of the engine assemblies (the same ids SceneAdapter.Baseline starts
    /// with), in the order given. Lowercase SHA-256 hex.
    /// </summary>
    public static string EngineFingerprint(IEnumerable<string> moduleVersionIds)
    {
        string text = "teamcreate-engine-1\n" + string.Join("\n", moduleVersionIds ?? Array.Empty<string>());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
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
