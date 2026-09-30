using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Sandbox.Services;
using Sandbox.Services.Players;

namespace TeamCreate;

public sealed record OrgMemberInfo(string SteamId, string Name, bool Online, bool IsFriend);

public sealed class OrgDirectory
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60L);

    private readonly CollaborationConfig config;

    private readonly HashSet<string> members = new HashSet<string>(StringComparer.Ordinal);

    private Task<Organization> pending;

    private DateTime expiresAt;

    public string Org => config.Org;

    public IReadOnlyList<OrgMemberInfo> Members { get; private set; } = Array.Empty<OrgMemberInfo>();

    public int Revision { get; private set; }

    public OrgDirectory(CollaborationConfig config)
    {
        this.config = config ?? CollaborationConfig.Defaults();
    }

    public void Poll()
    {
        if (pending != null)
        {
            if (pending.IsCompleted)
            {
                try
                {
                    Apply(pending.GetAwaiter().GetResult());
                }
                catch (Exception)
                {
                    Apply(null);
                }
                pending = null;
            }
        }
        else if (!(DateTime.UtcNow < expiresAt))
        {
            expiresAt = DateTime.UtcNow + CacheDuration;
            try
            {
                pending = Organization.Get(config.Org);
            }
            catch (Exception)
            {
                Apply(null);
            }
        }
    }

    public bool Contains(string steamId)
    {
        if (!SteamIdentityRules.TryParse(steamId, out var identity))
        {
            return false;
        }
        string text = SteamIdentityRules.Canonical(identity);
        if (members.Count > 0)
        {
            return members.Contains(text);
        }
        string[] array = config.OrgMemberAllowlist ?? Array.Empty<string>();
        foreach (string a in array)
        {
            if (string.Equals(a, text, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private void Apply(Organization organization)
    {
        members.Clear();
        List<OrgMemberInfo> list = new List<OrgMemberInfo>();
        Profile[] array = organization?.Members;
        if (array != null)
        {
            Profile[] array2 = array;
            foreach (Profile profile in array2)
            {
                if (SteamIdentityRules.TryParse(profile.Id.ValueUnsigned.ToString(CultureInfo.InvariantCulture), out var identity))
                {
                    string text = SteamIdentityRules.Canonical(identity);
                    members.Add(text);
                    list.Add(new OrgMemberInfo(text, profile.Name, profile.Online, profile.IsFriend));
                }
            }
        }
        Members = list;
        Revision++;
    }
}
