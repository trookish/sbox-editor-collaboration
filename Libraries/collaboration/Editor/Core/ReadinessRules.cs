using System;
using System.Collections.Generic;

namespace TeamCreate;

public enum ReadinessLevel
{
    Ok,
    Warning,
    Blocked
}

public sealed record ReadinessItem(string Key, string Title, ReadinessLevel Level, string Detail, string Fix);

public sealed record ReadinessInput(bool SteamSignedIn, string SteamPersona, bool SceneSupported, bool ProjectPublished, bool PackageReachable, bool LibraryInstalled, Version LibraryInstalledVersion, Version LibraryRequiredVersion, bool IsOrgMember, CollaborationConfig Config, string ScenePath = null);

public static class ReadinessRules
{
    public static List<ReadinessItem> Evaluate(ReadinessInput input)
    {
        CollaborationConfig config = input.Config ?? CollaborationConfig.Defaults();
        return new List<ReadinessItem>
        {
            EvaluateSteam(input),
            EvaluateScene(input),
            EvaluatePublished(input, config),
            EvaluateLibrary(input, config),
            EvaluateOrganization(input, config)
        };
    }

    public static bool CanStart(IReadOnlyList<ReadinessItem> items)
    {
        return items != null && items.Count > 0 && (object)FirstBlocked(items) == null;
    }

    public static string BlockReason(IReadOnlyList<ReadinessItem> items)
    {
        ReadinessItem readinessItem = FirstBlocked(items);
        if ((object)readinessItem == null)
        {
            return null;
        }
        string text = readinessItem.Title ?? string.Empty;
        string text2 = ((text.Length == 0) ? text : (char.ToLowerInvariant(text[0]) + text.Substring(1)));
        return "Hosting is unavailable: " + text2 + ".";
    }

    private static ReadinessItem FirstBlocked(IReadOnlyList<ReadinessItem> items)
    {
        if (items == null)
        {
            return null;
        }
        foreach (ReadinessItem item in items)
        {
            if ((object)item != null && item.Level == ReadinessLevel.Blocked)
            {
                return item;
            }
        }
        return null;
    }

    private static ReadinessItem EvaluateSteam(ReadinessInput input)
    {
        return input.SteamSignedIn ? new ReadinessItem("steam", "Steam sign-in", ReadinessLevel.Ok, input.SteamPersona, null) : new ReadinessItem("steam", "Steam sign-in required", ReadinessLevel.Blocked, "Sign in to Steam to identify this editor.", null);
    }

    private static ReadinessItem EvaluateScene(ReadinessInput input)
    {
        return input.SceneSupported ? new ReadinessItem("scene", "Active scene", ReadinessLevel.Ok, input.ScenePath, null) : new ReadinessItem("scene", "Saved scene required", ReadinessLevel.Blocked, "Open a saved scene under scenes/ before starting a session.", null);
    }

    private static ReadinessItem EvaluatePublished(ReadinessInput input, CollaborationConfig config)
    {
        if (!config.RequirePublished)
        {
            return new ReadinessItem("published", "Project publication", ReadinessLevel.Ok, "Publication is not required for this project.", null);
        }
        if (!input.ProjectPublished)
        {
            return new ReadinessItem("published", "Project publication required", ReadinessLevel.Blocked, "Publish this project to s&box (hidden or public) before hosting.", null);
        }
        if (!input.PackageReachable)
        {
            return new ReadinessItem("published", "Project publication", ReadinessLevel.Warning, "The published copy could not be fetched from Asset Party; peers may not be able to install it.", null);
        }
        return new ReadinessItem("published", "Project publication", ReadinessLevel.Ok, "The published copy is available from Asset Party.", null);
    }

    private static ReadinessItem EvaluateLibrary(ReadinessInput input, CollaborationConfig config)
    {
        if (!input.LibraryInstalled)
        {
            return new ReadinessItem("library", "Library installation required", ReadinessLevel.Blocked, "Install the collaboration library from Asset Party.", null);
        }
        Version version = input.LibraryRequiredVersion ?? config.LibraryVersion;
        Version libraryInstalledVersion = input.LibraryInstalledVersion;
        if ((object)version != null && ((object)libraryInstalledVersion == null || VersionRules.IsOlder(libraryInstalledVersion, version)))
        {
            return new ReadinessItem("library", "Library update required", ReadinessLevel.Blocked, $"Installed version {libraryInstalledVersion?.ToString() ?? "unknown"} is older than the required version {version}.", "Update");
        }
        return new ReadinessItem("library", "Collaboration library", ReadinessLevel.Ok, ((object)libraryInstalledVersion == null) ? "The collaboration library is installed." : $"Version {libraryInstalledVersion} installed.", null);
    }

    private static ReadinessItem EvaluateOrganization(ReadinessInput input, CollaborationConfig config)
    {
        if (!config.RequireOrgMember)
        {
            return new ReadinessItem("org", "Organization membership", ReadinessLevel.Ok, "Organization membership is not required for this project.", null);
        }
        return input.IsOrgMember ? new ReadinessItem("org", "Organization membership", ReadinessLevel.Ok, "You are a member of the organization " + config.Org + ".", null) : new ReadinessItem("org", "Organization membership required", ReadinessLevel.Blocked, "You are not a member of the organization " + config.Org + ".", null);
    }
}
