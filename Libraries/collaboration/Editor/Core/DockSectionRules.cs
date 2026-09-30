using System;
using System.Collections.Generic;

namespace TeamCreate;

public enum DockTab
{
    Session,
    People,
    Setup
}

/// <summary>
/// What the Collaboration dock shows, decided without the engine so it can be tested. The dock has three sections, each its own tab, because a person comes to it
/// for one of three reasons: to run a session, to see who is there, or to get ready. Only what applies right now is shown: an idle dock offers Host and Join and
/// nothing else, and the controls that act on a live session appear when there is one.
/// </summary>
public static class DockSectionRules
{
    public static readonly DockTab[] All = { DockTab.Session, DockTab.People, DockTab.Setup };

    public static string Name(DockTab tab)
    {
        switch (tab)
        {
            case DockTab.People: return "People";
            case DockTab.Setup: return "Setup";
            default: return "Session";
        }
    }

    /// <summary>The Material icon shown beside the tab's name.</summary>
    public static string Icon(DockTab tab)
    {
        switch (tab)
        {
            case DockTab.People: return "groups";
            case DockTab.Setup: return "tune";
            default: return "hub";
        }
    }

    /// <summary>One sentence for the tab's tooltip: what the section is for.</summary>
    public static string Purpose(DockTab tab)
    {
        switch (tab)
        {
            case DockTab.People: return "Who is in this session, and who in your organization could join.";
            case DockTab.Setup: return "What this project needs before a session can start, the relay that connects friends on other networks, and a one-click test you can run alone.";
            default: return "Host a session or join one with a code, then save, undo and leave.";
        }
    }

    /// <summary>The tab to open on when the person has not chosen one: the checklist in a solo test copy, where it is what they came to watch, and Setup when hosting is blocked.</summary>
    public static DockTab Default(bool soloCopy, bool readyToHost)
    {
        if (soloCopy)
        {
            return DockTab.Setup;
        }
        return readyToHost ? DockTab.Session : DockTab.Setup;
    }

    /// <summary>
    /// The tab to open on. A solo test copy always opens on Setup, where the test's checklist is, whatever the person chose last in their real project; otherwise the
    /// tab they last chose, if there is one, else the default.
    /// </summary>
    public static DockTab Initial(bool soloCopy, int savedIndex, bool readyToHost)
    {
        if (soloCopy)
        {
            return DockTab.Setup;
        }
        return FromIndex(savedIndex, Default(false, readyToHost));
    }

    /// <summary>A remembered tab index, or the default when it is missing or out of range (a setting left by an older version must never open a blank dock).</summary>
    public static DockTab FromIndex(int index, DockTab fallback)
    {
        return index >= 0 && index < All.Length ? All[index] : fallback;
    }

    public static int IndexOf(DockTab tab)
    {
        return Array.IndexOf(All, tab);
    }

    /// <summary>Save, undo, recover and the options that go with them: only when there is a session to act on, or something that happened to the last one that needs an answer.</summary>
    public static bool ShowActions(bool inSession, bool hasNotice)
    {
        return inSession || hasNotice;
    }

    public static bool ShowSessionRoster(bool inSession)
    {
        return inSession;
    }

    /// <summary>
    /// A line for the top of the Session tab when hosting is blocked, so the reason is where the person is looking and not only on another tab. Null when nothing
    /// blocks, and null during a session, because by then it is moot.
    /// </summary>
    public static string SetupBanner(IReadOnlyList<ReadinessItem> items, bool inSession)
    {
        if (inSession || items == null || items.Count == 0 || ReadinessRules.CanStart(items))
        {
            return null;
        }
        string reason = ReadinessRules.BlockReason(items);
        return string.IsNullOrWhiteSpace(reason)
            ? "Hosting is not available yet. Open the Setup tab to see what is missing."
            : reason.TrimEnd('.') + ". Open the Setup tab for details.";
    }
}
