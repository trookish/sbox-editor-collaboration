using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeamCreate;

/// <summary>
/// Which relay a project uses when the person set nothing up. The library carries one address of its own so that hosting works the first time, with
/// no server to run and no setting to edit; a project can name a different relay in ProjectSettings/collaboration.json, or turn relaying off.
/// </summary>
public static class RelayDefaults
{
    /// <summary>
    /// The relay every project uses unless it says otherwise. Null until the library's own relay has been deployed; Tools/RelayWorker/set-default-relay.js
    /// writes the address here after `wrangler deploy`.
    /// </summary>
    public static readonly string DefaultUrl = null;

    /// <summary>The value of RelayUrl that opts a project out of relaying altogether.</summary>
    public const string Off = "off";

    /// <summary>What to tell a host whose session has no relay a friend on another network could use. Null when nothing needs saying.</summary>
    public static string NoRelayNotice(string effectiveRelay, string configured)
    {
        if (!string.IsNullOrWhiteSpace(effectiveRelay))
        {
            return RelayProtocol.IsPublicRelay(effectiveRelay)
                ? null
                : "The relay is on this computer, so only this computer can use it. A friend on another network cannot join through it; use a public relay address.";
        }
        if (string.Equals(configured?.Trim(), Off, StringComparison.OrdinalIgnoreCase))
        {
            return "Relaying is turned off for this project (RelayUrl is \"off\"), so friends can join only from your network or through a port you forward.";
        }
        return "No relay is set up, so friends can join only from the same network or through a port you forward. To let friends anywhere join, open the Setup tab and press Set up relay.";
    }

    /// <summary>The relay to use: the project's own if it names one, nothing if it says "off", otherwise the built-in one.</summary>
    public static string Resolve(string configured, string builtIn)
    {
        if (string.Equals(configured?.Trim(), Off, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.Trim();
        }
        return string.IsNullOrWhiteSpace(builtIn) ? null : builtIn.Trim();
    }
}

public sealed record SetupResult(bool Created, string Error);

/// <summary>
/// Makes the shared settings file on first use, so a new project needs no hand-written JSON. It only ever adds what is missing: an existing file keeps
/// every choice in it, including a relaxed admission gate, and a file it cannot read is reported and left exactly as it was.
/// </summary>
public static class CollaborationSetup
{
    public static SetupResult EnsureConfig(string path, string org, string libraryIdent, string libraryVersion)
    {
        try
        {
            JsonObject document;
            if (File.Exists(path))
            {
                JsonNode parsed;
                try
                {
                    parsed = JsonNode.Parse(File.ReadAllText(path));
                }
                catch (JsonException ex)
                {
                    return new SetupResult(false, path + " is not valid JSON (" + ex.Message + "). It was left as it is; fix it or delete it.");
                }
                if (!(parsed is JsonObject existing))
                {
                    return new SetupResult(false, path + " must contain a JSON object. It was left as it is.");
                }
                document = existing;
                if (document.TryGetPropertyValue("ProjectId", out JsonNode idNode) && idNode != null)
                {
                    string text = (idNode is JsonValue value && value.TryGetValue<string>(out string s)) ? s : null;
                    return text != null && Guid.TryParse(text, out _)
                        ? new SetupResult(false, null)
                        : new SetupResult(false, "ProjectId in " + path + " is not a GUID. It was left as it is; fix it or delete the file.");
                }
                document["ProjectId"] = Guid.NewGuid().ToString("D");
            }
            else
            {
                document = new JsonObject { ["ProjectId"] = Guid.NewGuid().ToString("D") };
                if (!string.IsNullOrWhiteSpace(org))
                {
                    document["Org"] = org.Trim();
                }
                if (!string.IsNullOrWhiteSpace(libraryIdent))
                {
                    document["LibraryIdent"] = libraryIdent.Trim();
                }
                if (!string.IsNullOrWhiteSpace(libraryVersion))
                {
                    document["LibraryVersion"] = libraryVersion.Trim();
                }
                // Every admission gate starts ON. A person can relax one by editing the file; nothing here ever does.
                document["RequireSteamFriend"] = true;
                document["RequireOrgMember"] = true;
                document["RequirePublished"] = true;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temp = path + ".tmp";
            File.WriteAllText(temp, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, true);
            return new SetupResult(true, null);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return new SetupResult(false, path + " could not be written: " + ex.Message);
        }
    }
}
