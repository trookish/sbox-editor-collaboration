using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Editor;
using Sandbox;
using Sandbox.Internal;
using Sandbox.UI;

namespace TeamCreate;

[Dock("Editor", "Collaboration", "groups", DockArea.Bottom)]
public sealed class CollaborationDock : Widget
{
    private sealed class ContentCanvas : Widget
    {
        public ContentCanvas(Widget parent)
            : base(parent)
        {
        }

        public Vector2 WantedSize()
        {
            return SizeHint();
        }

        public float MinimumNeeded()
        {
            return MinimumSizeHint().y;
        }

        public void Reflow()
        {
            DockTheme.InvalidateLayout(this);
            UpdateGeometry();
        }

        protected override void OnResize()
        {
            base.OnResize();
            DockTheme.InvalidateLayout(this);
        }
    }

    private sealed class HeaderBar : Widget
    {
        public HeaderBar(Widget parent)
            : base(parent)
        {
            FixedHeight = 30f;
            OnPaintOverride = PaintHeader;
        }

        private bool PaintHeader()
        {
            CollaborationSession current = CollaborationSession.Current;
            SessionState state = current.State;
            Color color = DockTheme.StateColor(state);
            string text = DockTheme.StateVerb(state);
            Rect localRect = LocalRect;
            float width = 30f + Paint.MeasureText(text).x + 14f;
            Rect rect = new Rect(localRect.Left, localRect.Top + 3f, in width, localRect.Height - 6f);
            Paint.SetBrushAndPen(color.WithAlpha(0.16f), color.WithAlpha(0.55f));
            Paint.DrawRect(in rect, rect.Height * 0.5f);
            Paint.SetBrush(in color);
            Paint.ClearPen();
            Paint.DrawCircle(new Vector2(rect.Left + 12f, rect.Top + rect.Height * 0.5f), 4.0);
            Paint.SetPen(in Theme.Text, 0f, PenStyle.Solid);
            Paint.DrawText(new Rect(rect.Left + 22f, rect.Top, width - 26f, rect.Height), text, TextFlag.LeftCenter | TextFlag.SingleLine);
            string text2 = current.OrganizationDirectory?.Org ?? current.Config?.Org;
            string text3 = "org " + (string.IsNullOrWhiteSpace(text2) ? "unknown" : text2);
            string text4 = "library " + LibraryInfo.LibraryVersion;
            float width2 = Paint.MeasureText(text4).x + 16f;
            float width3 = Paint.MeasureText(text3).x + 16f;
            Rect rect2 = new Rect(localRect.Right - width2, rect.Top, in width2, rect.Height);
            Rect rect3 = new Rect(rect2.Left - 6f - width3, rect.Top, in width3, rect.Height);
            Paint.SetBrushAndPen(Theme.ControlBackground, Theme.Text.WithAlpha(0.12f));
            Paint.DrawRect(in rect3, 4f);
            Paint.DrawRect(in rect2, 4f);
            Paint.SetPen(Theme.Text.WithAlpha(0.75f), 0f, PenStyle.Solid);
            Paint.DrawText(in rect3, text3, TextFlag.Center | TextFlag.SingleLine);
            Paint.DrawText(in rect2, text4, TextFlag.Center | TextFlag.SingleLine);
            return false;
        }
    }

    public const string FooterUndoLine = "While connected, use collaboration Undo. Each commit clears the native undo history and replaces it with one Collaboration Undo entry, so the Undo dock and Ctrl+Z act on your most recent collaboration operation only. Save only when synchronized. Identity is the signed-in Steam account.";

    public const string FooterTransportLine = "Invite-code and discovery sessions are encrypted; Advanced sessions opened with a typed key are not.";

    public const string RuntimeParagraph = "Runtime collaboration is experimental and opt-in only. It currently coordinates lifecycle state only; runtime transport, sampling, undo, apply, runtime hotload data flow, and synchronization are unsupported. Hotload lifecycle blocking, recovery archival, and transport disposal are supported. Source collaboration remains the default.";

    public const string LimitsParagraph = "There is no public matchmaking and no NAT hole punching: friends on different networks need the project's relay, or a forwarded port on the host. While you play, scene edits are paused and a collaborator cannot watch the running game. Full-scene scanning, bounded 8 MiB packets, queues of 256 packets and a limit of four participants (the host and three guests) suit small experimental sessions. Large scenes, high latency, large teams and long-running memory/journal growth have not been qualified. Native undo history carries only the most recent collaboration operation, and redo restores that same operation — there is no longer undo or redo history behind it. See the library README, Explicit limits, for the full list.";

    private Editor.Label statusDetail;

    private Editor.Label setupBanner;

    private Editor.Label undoHint;

    private ReadinessCard readiness;

    private ConnectCard connect;

    private RelayCard relay;

    private ChangesCard changes;

    private OrgRosterCard orgRoster;

    private SessionRosterCard sessionRoster;

    private ControlsCard controls;

    private SoloTestCard soloTest;

    private Editor.Button aboutToggle;

    private Widget aboutBody;

    private SegmentedControl tabs;

    private readonly Dictionary<DockTab, ScrollArea> scrollers = new Dictionary<DockTab, ScrollArea>();

    private readonly Dictionary<DockTab, ContentCanvas> pages = new Dictionary<DockTab, ContentCanvas>();

    private DockTab selectedTab = DockTab.Session;

    private DockTab? appliedTab;

    private bool tabChosenByPerson;

    private DateTime nextBuildAttempt;

    private Widget lastParent;

    private Vector2 lastSize;

    private Vector2 lastPosition;

    private bool reflowing;

    private int layoutReflowCount;

    private const string TabCookie = "collaboration.dock.tab";

    private ScrollArea scroller => scrollers.TryGetValue(selectedTab, out ScrollArea value) ? value : null;

    private ContentCanvas content => pages.TryGetValue(selectedTab, out ContentCanvas value) ? value : null;

    private bool Built => statusDetail != null && setupBanner != null && undoHint != null && readiness != null && connect != null && relay != null && changes != null && orgRoster != null && sessionRoster != null && controls != null && soloTest != null
        && aboutToggle != null && aboutBody != null && tabs != null && scrollers.Count == DockSectionRules.All.Length && pages.Count == DockSectionRules.All.Length
        && DockSectionRules.All.All(tab => scrollers[tab] != null && scrollers[tab].Canvas == pages[tab])
        && readiness.Complete && connect.Complete && relay.Complete && changes.Complete && orgRoster.Complete && sessionRoster.Complete && controls.Complete && soloTest.Complete;

    public CollaborationDock(Widget parent)
        : base(parent)
    {
        Layout = Layout.Column();
        Layout.Margin = 12f;
        Layout.Spacing = 8f;
        Build();
    }

    private void Build()
    {
        Layout.Clear(deleteWidgets: true);
        scrollers.Clear();
        pages.Clear();
        Layout.Add(new HeaderBar(this));
        statusDetail = Layout.Add(new Editor.Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });

        // The native segmented control, the same one the engine's own pages use for Global / Current Scene: themed, with icons, and it collapses to icons when narrow.
        tabs = Layout.Add(new SegmentedControl(this));
        foreach (DockTab tab in DockSectionRules.All)
        {
            tabs.AddOption(DockSectionRules.Name(tab), DockSectionRules.Icon(tab));
        }
        tabs.ToolTip = "Session: host or join, save, undo. People: who is here. Setup: readiness, relay and the solo test.";

        // One scroll area per section, so each keeps its own scroll position and nothing the person is not looking at is laid out.
        foreach (DockTab tab in DockSectionRules.All)
        {
            var area = Layout.Add(new ScrollArea(this), 1);
            area.VerticalSizeMode = SizeMode.Default;
            area.VerticalScrollbarMode = ScrollbarMode.Auto;
            area.HorizontalScrollbarMode = ScrollbarMode.Off;
            var page = new ContentCanvas(area);
            area.Canvas = page;
            page.SetSizeMode(SizeMode.Default, SizeMode.CanGrow);
            page.Layout = Layout.Column();
            page.Layout.Spacing = 8f;
            page.Layout.SizeConstraint = SizeConstraint.SetMinimumSize;
            page.ContentMargins = new Margin(0f, 0f, 10f, 0f);
            area.Visible = false;
            scrollers[tab] = area;
            pages[tab] = page;
        }

        // ---- Session: run a session ----
        Layout session = pages[DockTab.Session].Layout;
        setupBanner = session.Add(new Editor.Label("", pages[DockTab.Session])
        {
            WordWrap = true,
            Color = DockTheme.WarningColor
        });
        setupBanner.Visible = false;
        connect = session.Add(new ConnectCard(pages[DockTab.Session]));
        controls = session.Add(new ControlsCard(pages[DockTab.Session], connect));
        changes = session.Add(new ChangesCard(pages[DockTab.Session]));
        undoHint = session.Add(new Editor.Label("While connected, use collaboration Undo: each commit clears the native undo history and leaves one Collaboration Undo entry, so Ctrl+Z acts on your most recent collaboration operation only.", pages[DockTab.Session])
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        session.AddStretchCell(1);

        // ---- People: who is here ----
        Layout people = pages[DockTab.People].Layout;
        sessionRoster = people.Add(new SessionRosterCard(pages[DockTab.People]));
        orgRoster = people.Add(new OrgRosterCard(pages[DockTab.People]));
        people.AddStretchCell(1);

        // ---- Setup: get ready ----
        Layout setup = pages[DockTab.Setup].Layout;
        bool soloCopy = SoloTestRunner.IsSoloCopy;
        if (soloCopy)
        {
            soloTest = setup.Add(new SoloTestCard(pages[DockTab.Setup]));
        }
        readiness = setup.Add(new ReadinessCard(pages[DockTab.Setup]));
        relay = setup.Add(new RelayCard(pages[DockTab.Setup]));
        if (!soloCopy)
        {
            soloTest = setup.Add(new SoloTestCard(pages[DockTab.Setup]));
        }
        aboutToggle = setup.Add(new Editor.Button("▸ About & limits", pages[DockTab.Setup])
        {
            ToolTip = "What this library does, and what it deliberately does not do."
        });
        aboutToggle.Clicked = ToggleAbout;
        aboutBody = setup.Add(new Widget(pages[DockTab.Setup]));
        aboutBody.Layout = Layout.Column();
        aboutBody.Layout.Spacing = 8f;
        aboutBody.Layout.Add(new Editor.Label("Runtime collaboration is experimental and opt-in only. It currently coordinates lifecycle state only; runtime transport, sampling, undo, apply, runtime hotload data flow, and synchronization are unsupported. Hotload lifecycle blocking, recovery archival, and transport disposal are supported. Source collaboration remains the default.", pages[DockTab.Setup])
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        aboutBody.Layout.Add(new Editor.Label(LimitsParagraph, pages[DockTab.Setup])
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        aboutBody.Layout.Add(new Editor.Label(FooterTransportLine, pages[DockTab.Setup])
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        aboutBody.Visible = false;
        setup.AddStretchCell(1);

        // Which section to open on: the last one the person chose, except that a solo test copy always opens on Setup, where its checklist is.
        int saved = Sandbox.Internal.GlobalToolsNamespace.EditorCookie.Get(TabCookie, -1);
        selectedTab = DockSectionRules.Initial(soloCopy, saved, CollaborationSession.Current.Readiness == null || ReadinessRules.CanStart(CollaborationSession.Current.Readiness));
        appliedTab = null;
        tabChosenByPerson = false;
        tabs.SelectedIndex = DockSectionRules.IndexOf(selectedTab);
        tabs.OnSelectedChanged = name => ChooseTab(name);
        ApplyTab();
        Reflow();
    }

    private void ChooseTab(string name)
    {
        foreach (DockTab tab in DockSectionRules.All)
        {
            if (string.Equals(DockSectionRules.Name(tab), name, StringComparison.Ordinal))
            {
                selectedTab = tab;
                tabChosenByPerson = true;
                if (!SoloTestRunner.IsSoloCopy)
                {
                    Sandbox.Internal.GlobalToolsNamespace.EditorCookie.Set(TabCookie, DockSectionRules.IndexOf(tab));
                }
                ApplyTab();
                return;
            }
        }
    }

    /// <summary>Shows the current section's scroll area and hides the others. Only the visible one is laid out.</summary>
    private void ApplyTab()
    {
        if (appliedTab == selectedTab)
        {
            return;
        }
        appliedTab = selectedTab;
        foreach (DockTab tab in DockSectionRules.All)
        {
            if (scrollers.TryGetValue(tab, out ScrollArea area) && area != null)
            {
                area.Visible = tab == selectedTab;
            }
        }
        int index = DockSectionRules.IndexOf(selectedTab);
        if (tabs != null && tabs.SelectedIndex != index)
        {
            tabs.SelectedIndex = index;
        }
        Reflow();
    }

    private void ToggleAbout()
    {
        aboutBody.Visible = !aboutBody.Visible;
        DockTheme.SetText(aboutToggle, aboutBody.Visible ? "▾ About & limits" : "▸ About & limits");
        content?.Reflow();
    }

    [Shortcut("collab.save", "CTRL+ALT+S", ShortcutType.Window)]
    public void SaveShortcut()
    {
        if (Built)
        {
            RunShortcut(() =>
            {
                CollaborationSession.Current.Save();
            });
        }
    }

    [Shortcut("collab.undo", "CTRL+ALT+Z", ShortcutType.Window)]
    public void UndoShortcut()
    {
        if (Built)
        {
            RunShortcut(() =>
            {
                CollaborationSession.Current.Undo();
            });
        }
    }

    [Shortcut("collab.recover", "CTRL+ALT+R", ShortcutType.Window)]
    public void RecoverShortcut()
    {
        if (Built)
        {
            RunShortcut(() =>
            {
                CollaborationSession.Current.Reconnect(connect.Address, connect.PortNumber);
            });
        }
    }

    private static void RunShortcut(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            CollaborationSession.Current.ReportAction(error);
        }
    }

    [EditorEvent.Frame]
    public void Refresh()
    {
        if (!Built)
        {
            if (DateTime.UtcNow < nextBuildAttempt)
            {
                return;
            }
            try
            {
                Build();
            }
            catch (Exception ex)
            {
                GlobalGameNamespace.Log.Error("Collaboration dock: " + CollaborationDiagnostics.Bounded(ex.Message));
            }
            if (!Built)
            {
                nextBuildAttempt = DateTime.UtcNow.AddSeconds(5.0);
                return;
            }
        }
        Vector2 size = Size;
        Vector2 position = Position;
        Widget parent = Parent;
        if (size != lastSize || position != lastPosition || parent != lastParent)
        {
            lastSize = size;
            lastPosition = position;
            lastParent = parent;
            Reflow();
        }
        CollaborationSession current = CollaborationSession.Current;
        AvatarCache.Poll();
        DockTheme.SetText(statusDetail, current.Status);
        readiness.Refresh();
        connect.Refresh();
        relay.Refresh();
        changes.Refresh();
        orgRoster.Refresh();
        sessionRoster.Refresh();
        controls.Refresh();
        soloTest?.Refresh();
        RefreshSections(current);
    }

    private bool? lastActionsShown;

    private bool? lastRosterShown;

    private string lastBanner;

    /// <summary>
    /// Decides, from the session's state, what each section shows. Everything is compared before it is written, so a frame in which nothing changed touches nothing.
    /// </summary>
    private void RefreshSections(CollaborationSession session)
    {
        bool inSession = session.State != SessionState.Offline && session.State != SessionState.Ready;

        // Save, Undo, Recover and their options: only when there is a session to act on, or something just happened that needs reading.
        bool actions = DockSectionRules.ShowActions(inSession, controls.HasMessage);
        if (lastActionsShown != actions)
        {
            lastActionsShown = actions;
            controls.Visible = actions;
            undoHint.Visible = actions;
            Reflow();
        }
        bool roster = DockSectionRules.ShowSessionRoster(inSession);
        if (lastRosterShown != roster)
        {
            lastRosterShown = roster;
            sessionRoster.Visible = roster;
            Reflow();
        }

        // Why hosting is blocked, at the top of the tab where the person would press Host.
        string banner = DockSectionRules.SetupBanner(session.Readiness, inSession);
        if (!string.Equals(banner, lastBanner, StringComparison.Ordinal))
        {
            lastBanner = banner;
            DockTheme.SetText(setupBanner, banner ?? string.Empty);
            setupBanner.Visible = banner != null;
            Reflow();
        }

        // The project folder is not always known on the first frame, so a solo test copy is recognised here as well as when the dock is built: it opens on Setup, where its checklist is.
        if (!tabChosenByPerson && SoloTestRunner.IsSoloCopy && selectedTab != DockTab.Setup)
        {
            selectedTab = DockTab.Setup;
            ApplyTab();
        }

        // A person who has not chosen a tab is taken where the action is: to Setup if hosting is blocked, to Session once it is possible. Their own choice is never overridden.
        if (!tabChosenByPerson && !SoloTestRunner.IsSoloCopy && Sandbox.Internal.GlobalToolsNamespace.EditorCookie.Get(TabCookie, -1) < 0)
        {
            DockTab wanted = DockSectionRules.Default(false, session.Readiness == null || session.Readiness.Count == 0 || ReadinessRules.CanStart(session.Readiness));
            if (wanted != selectedTab)
            {
                selectedTab = wanted;
                ApplyTab();
            }
        }
    }

    private static bool IsTooShort(Widget widget)
    {
        if (widget == null)
        {
            return false;
        }
        if (ContentBottom(widget) > widget.Height + 0.5f)
        {
            return true;
        }
        if (widget is DockCard dockCard)
        {
            return dockCard.MinimumNeeded() > widget.Height + 0.5f;
        }
        if (widget is ContentCanvas contentCanvas)
        {
            return contentCanvas.MinimumNeeded() > widget.Height + 0.5f;
        }
        return false;
    }

    private void Reflow()
    {
        if (reflowing)
        {
            return;
        }
        reflowing = true;
        try
        {
            layoutReflowCount++;
            DockTheme.InvalidateLayout(this);
            foreach (var item2 in VisibleCards())
            {
                DockCard item = item2.Card;
                DockTheme.InvalidateLayout(item);
            }
            content?.Reflow();
        }
        finally
        {
            reflowing = false;
        }
    }

    protected override void OnResize()
    {
        base.OnResize();
        Reflow();
    }

    protected override void OnMoved()
    {
        base.OnMoved();
        Reflow();
    }

    public string LayoutDiagnostic()
    {
        List<string> list = new List<string>();
        int num = 0;
        if (content == null || scroller == null)
        {
            return "state=faulty; reason=not-built; reflows=" + layoutReflowCount;
        }
        if (IsTooShort(content))
        {
            list.Add("column");
        }
        num += CountOverlaps(content);
        foreach (var (item, dockCard) in VisibleCards())
        {
            if (dockCard != null)
            {
                if (IsTooShort(dockCard))
                {
                    list.Add(item);
                }
                num += CountOverlaps(dockCard);
            }
        }
        num += CountOverlaps(this);
        Vector2 vector = content.WantedSize();
        StringBuilder stringBuilder = new StringBuilder(384);
        AppendCardDetail(stringBuilder, "column", vector.y, content.Height, content.Width, ContentBottom(content), content.MinimumNeeded());
        foreach (var (text, dockCard2) in VisibleCards())
        {
            if (dockCard2 != null)
            {
                bool flag = list.Contains(text);
                AppendCardDetail(stringBuilder, text, dockCard2.WantedSize().y, dockCard2.Height, dockCard2.Width, ContentBottom(dockCard2), dockCard2.MinimumNeeded());
                if (flag)
                {
                    AppendChildDetail(stringBuilder, dockCard2);
                }
            }
        }
        StringBuilder stringBuilder2 = new StringBuilder(512);
        stringBuilder2.Append((list.Count == 0 && num == 0) ? "state=ok" : "state=faulty");
        stringBuilder2.Append("; tab=").Append(DockSectionRules.Name(selectedTab));
        stringBuilder2.Append("; reflows=").Append(layoutReflowCount);
        stringBuilder2.Append("; dock=").Append(Size.x.ToString("0")).Append('x')
            .Append(Size.y.ToString("0"));
        stringBuilder2.Append("; viewport=").Append(scroller.Width.ToString("0")).Append('x')
            .Append(scroller.Height.ToString("0"));
        stringBuilder2.Append("; column=").Append(content.Height.ToString("0"));
        stringBuilder2.Append("; columnWanted=").Append(vector.y.ToString("0"));
        stringBuilder2.Append("; scroll=").Append((scroller.Canvas == content) ? "on" : "off");
        stringBuilder2.Append("; compressed=").Append((list.Count == 0) ? "none" : string.Join(",", list));
        stringBuilder2.Append("; overlaps=").Append(num);
        stringBuilder2.Append("; detail=").Append(stringBuilder);
        return stringBuilder2.ToString();
    }

    private static void AppendCardDetail(StringBuilder builder, string name, float want, float height, float width, float fit, float? minimum = null)
    {
        if (builder.Length > 0)
        {
            builder.Append('|');
        }
        builder.Append(name).Append(":want=").Append(want.ToString("0"))
            .Append(",h=")
            .Append(height.ToString("0"))
            .Append(",w=")
            .Append(width.ToString("0"))
            .Append(",fit=")
            .Append(fit.ToString("0"));
        if (minimum.HasValue)
        {
            builder.Append(",min=").Append(minimum.Value.ToString("0"));
        }
    }

    private static void AppendChildDetail(StringBuilder builder, Widget card)
    {
        builder.Append(",kids=");
        bool flag = true;
        foreach (Widget child in card.Children)
        {
            if (child != null && child.Visible)
            {
                if (!flag)
                {
                    builder.Append(';');
                }
                flag = false;
                builder.Append(child.GetType().Name).Append(':').Append(child.Position.y.ToString("0"))
                    .Append('/')
                    .Append(child.Height.ToString("0"));
            }
        }
    }

    private static float ContentBottom(Widget parent)
    {
        float num = 0f;
        foreach (Widget child in parent.Children)
        {
            if (child != null && child.Visible)
            {
                float num2 = child.Position.y + child.Height;
                if (num2 > num)
                {
                    num = num2;
                }
            }
        }
        return num;
    }

    /// <summary>Every card with the section it lives in. A hidden card has no size, so the layout check and the reflow use only the ones on the visible section.</summary>
    private IEnumerable<(string Name, DockCard Card)> Cards()
    {
        yield return (Name: "connect", Card: connect);
        yield return (Name: "controls", Card: controls);
        yield return (Name: "changes", Card: changes);
        yield return (Name: "sessionRoster", Card: sessionRoster);
        yield return (Name: "orgRoster", Card: orgRoster);
        yield return (Name: "readiness", Card: readiness);
        yield return (Name: "relay", Card: relay);
        yield return (Name: "soloTest", Card: soloTest);
    }

    private static DockTab SectionOf(string card)
    {
        switch (card)
        {
            case "connect":
            case "controls":
            case "changes":
                return DockTab.Session;
            case "sessionRoster":
            case "orgRoster":
                return DockTab.People;
            default:
                return DockTab.Setup;
        }
    }

    private IEnumerable<(string Name, DockCard Card)> VisibleCards()
    {
        foreach (var item in Cards())
        {
            if (item.Card != null && item.Card.Visible && SectionOf(item.Name) == selectedTab)
            {
                yield return item;
            }
        }
    }

    private static int CountOverlaps(Widget parent)
    {
        List<Widget> list = new List<Widget>();
        foreach (Widget child in parent.Children)
        {
            if (child != null && child.Visible)
            {
                list.Add(child);
            }
        }
        if (list.Count < 2)
        {
            return 0;
        }
        list.Sort((Widget a, Widget b) => a.Position.y.CompareTo(b.Position.y));
        int num = 0;
        for (int num2 = 1; num2 < list.Count; num2++)
        {
            Widget widget = list[num2 - 1];
            Widget widget2 = list[num2];
            if (!(MathF.Abs(widget.Position.x - widget2.Position.x) > 0.5f) && widget.Position.y + widget.Height > widget2.Position.y + 0.5f)
            {
                num++;
            }
        }
        return num;
    }
}
