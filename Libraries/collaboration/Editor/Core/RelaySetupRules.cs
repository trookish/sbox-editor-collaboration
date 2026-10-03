using System;

namespace TeamCreate;

/// <summary>
/// The relay this computer set up for itself (the dock's Set up relay button). It is per machine and lives in <c>.collaboration/relay-url.txt</c>, which Git
/// ignores and the project manifest skips, because the shared <c>ProjectSettings/collaboration.json</c> is hashed into the manifest: writing the address there
/// would make the host's project differ from the friend's. The friend needs no copy of it; the invite code carries the relay's address.
/// </summary>
public static class LocalRelayFile
{
    public const string FileName = "relay-url.txt";

    /// <summary>The address on the first line that is not blank and not a comment, or null when there is none or it is unusable.</summary>
    public static string Parse(string text, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            if (!RelayProtocol.TryValidateUrl(line, out _, out string reason))
            {
                error = "The relay address in " + FileName + " is not usable: " + reason;
                return null;
            }
            return line;
        }
        return null;
    }

    public static string Format(string relayUrl)
    {
        return "# The relay this computer set up for itself. Delete this file to go back to the library's default relay.\n" + relayUrl.Trim() + "\n";
    }
}

public enum RelaySetupMode
{
    /// <summary>The library's own relay is in use.</summary>
    Shared,

    /// <summary>The relay this computer set up is in use.</summary>
    Own,

    /// <summary>The project's settings name the relay.</summary>
    Project,

    /// <summary>The project's settings turn relaying off.</summary>
    Off,

    /// <summary>No relay at all: the case the setup button exists for.</summary>
    Missing,

    /// <summary>A relay that only this computer can reach.</summary>
    LocalOnly,

    /// <summary>A setup is running.</summary>
    Working,

    /// <summary>A setup just finished and its relay is in use.</summary>
    Done,

    /// <summary>The last setup failed and no relay is in use.</summary>
    Failed
}

public sealed record RelaySetupInput(string ProjectRelay, string LocalRelay, string BuiltInRelay, bool Working, string Progress, DeployResult LastResult, bool InternetRelayEnabled = true);

/// <summary>What the dock's relay card shows: one sentence, which buttons make sense, and whether the sentence reports a problem.</summary>
public sealed record RelaySetupView(RelaySetupMode Mode, string Text, bool ShowOpen, bool ShowSetup, bool ShowCancel, bool IsProblem);

public static class RelaySetupRules
{
    public const string ClipboardHint = "Step 1 opens Cloudflare, where you create a token in one click. Step 2 reads it from the clipboard and sets the relay up.";

    public static RelaySetupView Describe(RelaySetupInput input)
    {
        if (input.Working)
        {
            return new RelaySetupView(RelaySetupMode.Working, string.IsNullOrWhiteSpace(input.Progress) ? "Setting up the relay…" : input.Progress, false, false, true, false);
        }

        string trimmedProject = input.ProjectRelay?.Trim();
        if (string.Equals(trimmedProject, RelayDefaults.Off, StringComparison.OrdinalIgnoreCase))
        {
            return new RelaySetupView(RelaySetupMode.Off, "Relaying is turned off in this project's settings (RelayUrl is \"off\"), so friends can join only from your network or through a forwarded port.", false, false, false, false);
        }

        bool projectNamesRelay = !string.IsNullOrWhiteSpace(trimmedProject);
        string effective = RelayDefaults.Resolve(input.ProjectRelay, Fallback(input.LocalRelay, input.BuiltInRelay));
        bool ownIsEffective = !projectNamesRelay && !string.IsNullOrWhiteSpace(input.LocalRelay);

        if (effective != null && !RelayProtocol.IsPublicRelay(effective))
        {
            string where = projectNamesRelay ? "in this project's settings" : "set up on this computer";
            return new RelaySetupView(RelaySetupMode.LocalOnly, "The relay " + where + " is on this computer, so only this computer can use it and a friend on another network cannot join. Use a public relay address" + (projectNamesRelay ? " in ProjectSettings/collaboration.json." : "."), false, !projectNamesRelay, false, true);
        }

        if (effective != null)
        {
            string host = HostOf(effective);
            if (input.LastResult != null && input.LastResult.Ok && string.Equals(input.LastResult.RelayUrl, effective, StringComparison.Ordinal) && ownIsEffective)
            {
                return new RelaySetupView(RelaySetupMode.Done, "Relay ready: " + host + ". Every session you host now uses it, and friends on any network can join.", false, false, false, false);
            }
            if (projectNamesRelay)
            {
                return new RelaySetupView(RelaySetupMode.Project, "Relay: " + host + " (named in this project's settings). Friends on any network can join.", false, false, false, false);
            }
            if (ownIsEffective)
            {
                return new RelaySetupView(RelaySetupMode.Own, "Relay: " + host + " (set up on this computer). Friends on any network can join.", false, false, false, false);
            }
            return new RelaySetupView(RelaySetupMode.Shared, "Relay: " + host + " (the library's shared relay). Friends on any network can join.", false, false, false, false);
        }

        if (input.LastResult != null && !input.LastResult.Ok && !input.LastResult.Cancelled && !string.IsNullOrWhiteSpace(input.LastResult.Error))
        {
            return new RelaySetupView(RelaySetupMode.Failed, input.LastResult.Error, true, true, false, true);
        }
        // Reached only when the person opened the advanced option (the card shows its own automatic-relay sentence on the default path). The
        // internet relay is on by default, so a relay of one's own is an alternative to it and not a missing piece.
        string lead;
        if (input.LastResult != null && input.LastResult.Cancelled)
        {
            lead = input.InternetRelayEnabled ? "Setup was cancelled. " : "The internet relay is turned off on this computer. Setup was cancelled. ";
        }
        else
        {
            lead = input.InternetRelayEnabled
                ? "A relay of your own on Cloudflare is optional, because the internet relay starts automatically when you host. "
                : "The internet relay is turned off on this computer. No relay of your own is set up either. ";
        }
        return new RelaySetupView(RelaySetupMode.Missing, lead + ClipboardHint, true, true, false, false);
    }

    /// <summary>The relay used when the project names none: the one this computer set up, else the library's own. The session and the card both ask here, so they cannot disagree.</summary>
    public static string Fallback(string localRelay, string builtInRelay)
    {
        return !string.IsNullOrWhiteSpace(localRelay) ? localRelay.Trim() : (string.IsNullOrWhiteSpace(builtInRelay) ? null : builtInRelay.Trim());
    }

    public static string HostOf(string relayUrl)
    {
        return Uri.TryCreate(relayUrl?.Trim(), UriKind.Absolute, out Uri uri) ? uri.Host : relayUrl;
    }
}
