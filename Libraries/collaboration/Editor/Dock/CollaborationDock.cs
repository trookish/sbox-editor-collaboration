using System;
using System.Collections.Generic;
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

    public const string LimitsParagraph = "There is no public matchmaking and no NAT hole punching: friends on different networks need the project's relay, or a forwarded port on the host. While you play, scene edits are paused and a collaborator cannot watch the running game. Full-scene scanning, bounded 8 MiB packets, queues of 256 packets and a host limit of 16 connections suit small experimental sessions. Large scenes, high latency, large teams and long-running memory/journal growth have not been qualified. Native undo history carries only the most recent collaboration operation, and redo restores that same operation — there is no longer undo or redo history behind it. See the library README, Explicit limits, for the full list.";

    private Editor.Label statusDetail;

    private ReadinessCard readiness;

    private ConnectCard connect;

    private RelayCard relay;

    private OrgRosterCard orgRoster;

    private SessionRosterCard sessionRoster;

    private ControlsCard controls;

    private SoloTestCard soloTest;

    private Editor.Button aboutToggle;

    private Widget aboutBody;

    private ScrollArea scroller;

    private ContentCanvas content;

    private DateTime nextBuildAttempt;

    private Widget lastParent;

    private Vector2 lastSize;

    private Vector2 lastPosition;

    private bool reflowing;

    private int layoutReflowCount;

    private bool Built => statusDetail != null && readiness != null && connect != null && relay != null && orgRoster != null && sessionRoster != null && controls != null && soloTest != null && aboutToggle != null && aboutBody != null && scroller != null && content != null && scroller.Canvas == content && readiness.Complete && connect.Complete && relay.Complete && orgRoster.Complete && sessionRoster.Complete && controls.Complete && soloTest.Complete;

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
        scroller = null;
        content = null;
        Layout.Add(new HeaderBar(this));
        scroller = Layout.Add(new ScrollArea(this), 1);
        scroller.VerticalSizeMode = SizeMode.Default;
        scroller.VerticalScrollbarMode = ScrollbarMode.Auto;
        scroller.HorizontalScrollbarMode = ScrollbarMode.Off;
        content = new ContentCanvas(scroller);
        scroller.Canvas = content;
        content.SetSizeMode(SizeMode.Default, SizeMode.CanGrow);
        content.Layout = Layout.Column();
        content.Layout.Spacing = 8f;
        content.Layout.SizeConstraint = SizeConstraint.SetMinimumSize;
        content.ContentMargins = new Margin(0f, 0f, 10f, 0f);
        Layout layout = content.Layout;
        statusDetail = layout.Add(new Editor.Label("", content)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        // In a solo test copy the checklist is what the person came to watch, so it goes first; in a real project the card sits below the controls.
        bool soloCopy = SoloTestRunner.IsSoloCopy;
        if (soloCopy)
        {
            soloTest = layout.Add(new SoloTestCard(content));
        }
        readiness = layout.Add(new ReadinessCard(content));
        connect = layout.Add(new ConnectCard(content));
        relay = layout.Add(new RelayCard(content));
        orgRoster = layout.Add(new OrgRosterCard(content));
        sessionRoster = layout.Add(new SessionRosterCard(content));
        controls = layout.Add(new ControlsCard(content, connect));
        if (!soloCopy)
        {
            soloTest = layout.Add(new SoloTestCard(content));
        }
        layout.Add(new Editor.Label("While connected, use collaboration Undo. Each commit clears the native undo history and replaces it with one Collaboration Undo entry, so the Undo dock and Ctrl+Z act on your most recent collaboration operation only. Save only when synchronized. Identity is the signed-in Steam account.", content)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        layout.Add(new Editor.Label("Invite-code and discovery sessions are encrypted; Advanced sessions opened with a typed key are not.", content)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        aboutToggle = layout.Add(new Editor.Button("▸ About & limits", content)
        {
            ToolTip = "What this library does, and what it deliberately does not do."
        });
        aboutToggle.Clicked = ToggleAbout;
        aboutBody = layout.Add(new Widget(content));
        aboutBody.Layout = Layout.Column();
        aboutBody.Layout.Spacing = 8f;
        aboutBody.Layout.Add(new Editor.Label("Runtime collaboration is experimental and opt-in only. It currently coordinates lifecycle state only; runtime transport, sampling, undo, apply, runtime hotload data flow, and synchronization are unsupported. Hotload lifecycle blocking, recovery archival, and transport disposal are supported. Source collaboration remains the default.", content)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        aboutBody.Layout.Add(new Editor.Label(LimitsParagraph, content)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        aboutBody.Visible = false;
        layout.AddStretchCell();
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
        orgRoster.Refresh();
        sessionRoster.Refresh();
        controls.Refresh();
        soloTest?.Refresh();
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
            foreach (var item2 in Cards())
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
        foreach (var (item, dockCard) in Cards())
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
        foreach (var (text, dockCard2) in Cards())
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

    private IEnumerable<(string Name, DockCard Card)> Cards()
    {
        yield return (Name: "readiness", Card: readiness);
        yield return (Name: "connect", Card: connect);
        yield return (Name: "relay", Card: relay);
        yield return (Name: "orgRoster", Card: orgRoster);
        yield return (Name: "sessionRoster", Card: sessionRoster);
        yield return (Name: "controls", Card: controls);
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
