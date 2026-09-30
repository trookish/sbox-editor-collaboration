using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeamCreate;

public sealed class CollaborationConfig
{
    public string ProjectId;

    public string Org = "cyn";

    public string LibraryIdent;

    public Version LibraryVersion;

    public bool RequireSteamFriend = true;

    public bool RequireOrgMember = true;

    public bool RequirePublished = true;

    public string[] OrgMemberAllowlist = Array.Empty<string>();

    // Where friends behind home routers meet. Not a secret: it names a server, and the credential that
    // lets a host use it lives in .collaboration/relay-access-key.txt, which Git ignores. Null means the
    // project opted out of the relay and only the direct routes are offered.
    public string RelayUrl;

    public static CollaborationConfig Defaults()
    {
        return new CollaborationConfig();
    }

    public static CollaborationConfig LoadFrom(string path, out string error)
    {
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Defaults();
            }
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) ? true : false)
            {
                error = "Collaboration config " + path + " could not be read: " + ex.Message;
                return Defaults();
            }
            JsonNode jsonNode;
            try
            {
                jsonNode = JsonNode.Parse(json);
            }
            catch (JsonException ex2)
            {
                error = "Collaboration config " + path + " is not valid JSON: " + ex2.Message;
                return Defaults();
            }
            if (!(jsonNode is JsonObject document))
            {
                error = "Collaboration config " + path + " must contain a JSON object.";
                return Defaults();
            }
            return Read(document, out error);
        }
        catch (Exception ex3)
        {
            error = "Collaboration config " + path + " could not be loaded: " + ex3.Message;
            return Defaults();
        }
    }

    private static CollaborationConfig Read(JsonObject document, out string error)
    {
        CollaborationConfig config = Defaults();
        List<string> list = new List<string>();
        if (document.TryGetPropertyValue("ProjectId", out JsonNode jsonNode) && jsonNode != null)
        {
            string text = Text(jsonNode);
            if (text == null || !Guid.TryParse(text, out var result))
            {
                list.Add("ProjectId must be a GUID such as 2e9f8f81-8b38-4207-ad3e-4e7c0bea567f.");
            }
            else
            {
                config.ProjectId = result.ToString("D");
            }
        }
        if (document.TryGetPropertyValue("Org", out JsonNode jsonNode2) && jsonNode2 != null)
        {
            string text2 = Text(jsonNode2);
            if (text2 == null)
            {
                list.Add("Org must be a string.");
            }
            else if (string.IsNullOrWhiteSpace(text2))
            {
                list.Add("Org must be an organization ident such as cyn.");
            }
            else
            {
                config.Org = text2.Trim();
            }
        }
        if (document.TryGetPropertyValue("LibraryIdent", out JsonNode jsonNode3) && jsonNode3 != null)
        {
            string text3 = Text(jsonNode3);
            if (text3 == null)
            {
                list.Add("LibraryIdent must be a string.");
            }
            else
            {
                config.LibraryIdent = text3;
            }
        }
        if (document.TryGetPropertyValue("LibraryVersion", out JsonNode jsonNode4) && jsonNode4 != null)
        {
            string text4 = Text(jsonNode4);
            if (text4 == null || !Version.TryParse(text4, out Version result2))
            {
                list.Add("LibraryVersion must be a system version such as 1.0.0.");
            }
            else
            {
                config.LibraryVersion = result2;
            }
        }
        ReadFlag(document, "RequireSteamFriend", list, (bool value) =>
        {
            config.RequireSteamFriend = value;
        });
        ReadFlag(document, "RequireOrgMember", list, (bool value) =>
        {
            config.RequireOrgMember = value;
        });
        ReadFlag(document, "RequirePublished", list, (bool value) =>
        {
            config.RequirePublished = value;
        });
        if (document.TryGetPropertyValue("OrgMemberAllowlist", out JsonNode jsonNode5) && jsonNode5 != null)
        {
            if (!(jsonNode5 is JsonArray jsonArray))
            {
                list.Add("OrgMemberAllowlist must be an array of decimal account IDs.");
            }
            else
            {
                List<string> list2 = new List<string>();
                List<string> list3 = new List<string>();
                foreach (JsonNode item in jsonArray)
                {
                    string text5 = Text(item);
                    if (text5 != null && ulong.TryParse(text5, NumberStyles.None, CultureInfo.InvariantCulture, out var result3))
                    {
                        list2.Add(result3.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        list3.Add((text5 == null) ? "<non-string>" : text5);
                    }
                }
                config.OrgMemberAllowlist = list2.ToArray();
                if (list3.Count > 0)
                {
                    list.Add("OrgMemberAllowlist entries ignored because they are not decimal account IDs: " + string.Join(", ", list3) + ".");
                }
            }
        }
        if (document.TryGetPropertyValue("RelayUrl", out JsonNode relayNode) && relayNode != null)
        {
            string relayText = Text(relayNode);
            if (relayText == null)
            {
                list.Add("RelayUrl must be a string such as wss://relay.example.com/relay.");
            }
            else if (string.Equals(relayText.Trim(), RelayDefaults.Off, StringComparison.OrdinalIgnoreCase))
            {
                config.RelayUrl = RelayDefaults.Off;
            }
            else if (!string.IsNullOrWhiteSpace(relayText))
            {
                if (RelayProtocol.TryValidateUrl(relayText, out Uri relayUri, out string relayError))
                {
                    config.RelayUrl = relayUri.ToString();
                }
                else
                {
                    list.Add("RelayUrl is not usable: " + relayError);
                }
            }
        }
        error = ((list.Count == 0) ? null : string.Join(" ", list));
        return config;
    }

    private static void ReadFlag(JsonObject document, string name, List<string> problems, Action<bool> assign)
    {
        if (document.TryGetPropertyValue(name, out JsonNode jsonNode) && jsonNode != null)
        {
            if (jsonNode is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var value))
            {
                assign(value);
            }
            else
            {
                problems.Add(name + " must be true or false.");
            }
        }
    }

    private static string Text(JsonNode node)
    {
        string value;
        return (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out value)) ? value : null;
    }
}

public static class VersionRules
{
    public static bool IsOlder(Version version, Version required)
    {
        return Compare(version, required) < 0;
    }

    public static bool IsAtLeast(Version version, Version required)
    {
        return Compare(version, required) >= 0;
    }

    private static int Compare(Version version, Version required)
    {
        if ((object)version == null)
        {
            return ((object)required != null) ? (-1) : 0;
        }
        if ((object)required == null)
        {
            return 1;
        }
        return Normalized(version).CompareTo(Normalized(required));
    }

    private static (int Major, int Minor, int Build, int Revision) Normalized(Version version)
    {
        return (Major: version.Major, Minor: version.Minor, Build: (version.Build >= 0) ? version.Build : 0, Revision: (version.Revision >= 0) ? version.Revision : 0);
    }
}

public enum CollaborationMode
{
    Source,
    RuntimeExperimental
}

public enum RuntimeEnableResult
{
    Ok,
    AlreadyEnabled,
    RunAlreadyActive,
    InvalidRunId,
    InvalidScenePath
}
