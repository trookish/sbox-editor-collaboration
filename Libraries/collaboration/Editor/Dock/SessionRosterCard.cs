using System;
using System.Collections.Generic;
using Editor;
using Sandbox;

namespace TeamCreate;

public sealed class SessionRosterCard : DockCard
{
    private sealed class PeerRow : Widget
    {
        private readonly Packet peer;

        private readonly string key;

        private readonly Button focus;

        public PeerRow(Widget parent, string key, Packet peer, string role)
            : base(parent)
        {
            this.peer = peer;
            this.key = key;
            FixedHeight = 40f;
            Layout = Layout.Row();
            Layout.Spacing = 4f;
            Layout.Add(new PeerBody(this, peer, role), 1);
            focus = Layout.Add(new Button(this)
            {
                Icon = "center_focus_strong"
            });
            focus.FixedWidth = 26f;
            focus.Clicked = () =>
            {
                DockCard.Run(() =>
                {
                    CollaborationSession.Current.FocusCamera(key);
                });
            };
        }

        public void SyncFocus(string localScene)
        {
            bool flag = !string.IsNullOrEmpty(localScene) && string.Equals(peer.Scene, localScene, StringComparison.Ordinal);
            string text;
            if (string.IsNullOrWhiteSpace(peer.Scene))
            {
                text = "no saved scene";
            }
            else
            {
                text = ((peer.Scene.Length <= 96) ? peer.Scene : peer.Scene.Substring(0, 96));
            }
            string text2 = (flag ? "Move my camera to theirs." : ("Move my camera to theirs — unavailable: that collaborator is in " + text + ". Open the scene first."));
            if (focus.Enabled != flag || !string.Equals(focus.ToolTip, text2, StringComparison.Ordinal))
            {
                focus.Enabled = flag;
                focus.ToolTip = text2;
            }
        }
    }

    private sealed class PeerBody : Widget
    {
        private readonly Packet peer;

        private readonly string role;

        public PeerBody(Widget parent, Packet peer, string role)
            : base(parent)
        {
            this.peer = peer;
            this.role = role;
            ToolTip = BuildToolTip();
            OnPaintOverride = PaintRow;
        }

        private string BuildToolTip()
        {
            string text = (string.IsNullOrWhiteSpace(peer.Name) ? "(unknown name)" : peer.Name);
            string text2 = text + "\nSteamId: " + (string.IsNullOrWhiteSpace(peer.SteamId) ? "not reported" : peer.SteamId);
            text2 = text2 + "\nRole: " + ((role == "—") ? "not reported by this build" : role);
            return text2 + "\nActive scene: " + (peer.Scene ?? "No saved scene");
        }

        private bool PaintRow()
        {
            Rect rect = LocalRect;
            if (Paint.HasMouseOver)
            {
                Paint.SetBrush(Theme.Text.WithAlpha(0.05f));
                Paint.DrawRect(in rect, 4f);
            }
            Rect rect2 = new Rect(rect.Left + 4f, rect.Top + (rect.Height - 32f) * 0.5f, 32f, 32f);
            DockTheme.DrawAvatar(rect2, peer.SteamId, peer.Name, inSession: true, online: true, notFriend: false);
            float width = ((role == "—") ? 18f : (Paint.MeasureText(role).x + 14f));
            float x = rect.Left + 32f + 14f;
            float width2 = MathF.Max(0f, rect.Right - x - width - 16f);
            float height = (rect.Height - 6f) * 0.5f;
            string text = (string.IsNullOrWhiteSpace(peer.Name) ? "(unknown name)" : peer.Name);
            Paint.SetPen(in Theme.Text, 0f, PenStyle.Solid);
            Paint.DrawText(new Rect(in x, rect.Top + 3f, in width2, in height), Paint.GetElidedText(text, width2), TextFlag.LeftCenter | TextFlag.SingleLine);
            string text2 = peer.Scene ?? "No saved scene";
            Paint.SetPen(Theme.Text.WithAlpha(0.6f), 0f, PenStyle.Solid);
            Paint.DrawText(new Rect(in x, rect.Top + 3f + height, in width2, in height), Paint.GetElidedText(text2, width2), TextFlag.LeftCenter | TextFlag.SingleLine);
            Rect rect3 = new Rect(rect.Right - width - 6f, rect.Top + (rect.Height - height - 4f) * 0.5f, in width, height + 4f);
            Paint.SetBrushAndPen(Theme.Text.WithAlpha(0.1f), Color.Transparent, 0f);
            Paint.DrawRect(in rect3, 4f);
            Paint.SetPen((role == "—") ? DockTheme.MutedColor : Theme.Text.WithAlpha(0.85f), 0f, PenStyle.Solid);
            Paint.DrawText(in rect3, role, TextFlag.Center | TextFlag.SingleLine);
            return false;
        }
    }

    private const string FocusIcon = "center_focus_strong";

    private const int ScenePathLimit = 96;

    private readonly Widget rows;

    private readonly Label notice;

    private readonly List<PeerRow> peerRows = new List<PeerRow>();

    private int lastFingerprint = int.MinValue;

    protected override bool FieldsAssigned => rows != null && notice != null && peerRows != null;

    public SessionRosterCard(Widget parent)
        : base(parent, "This session")
    {
        notice = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.WarningColor
        });
        notice.Visible = false;
        rows = Layout.Add(new Widget(this));
        rows.Layout = Layout.Column();
        rows.Layout.Spacing = 4f;
        Refresh();
    }

    public void Refresh()
    {
        CollaborationSession current = CollaborationSession.Current;
        int num = MembershipFingerprint(current);
        if (num != lastFingerprint)
        {
            Rebuild(current);
        }
        string text = current.FocusNotice ?? string.Empty;
        DockCard.SetText(notice, text);
        bool flag = text.Length > 0;
        if (notice.Visible != flag)
        {
            notice.Visible = flag;
        }
        if (peerRows.Count == 0)
        {
            return;
        }
        string localScene = SceneAdapter.PathOf(SceneEditorSession.Active);
        foreach (PeerRow peerRow in peerRows)
        {
            peerRow.SyncFocus(localScene);
        }
    }

    private static int MembershipFingerprint(CollaborationSession session)
    {
        int num = session.Roster.Count;
        foreach (KeyValuePair<string, Packet> item in session.Roster)
        {
            num = num * 31 + item.Key.GetHashCode();
        }
        return num;
    }

    private void Rebuild(CollaborationSession session)
    {
        lastFingerprint = MembershipFingerprint(session);
        peerRows.Clear();
        rows.Layout.Clear(deleteWidgets: true);
        DockCard.SetText(Title, "This session (" + (session.Roster.Count + 1) + ")");
        if (session.Roster.Count == 0)
        {
            rows.Layout.Add(new Label("No one else is in this session yet.", this)
            {
                Color = DockTheme.MutedColor
            });
            return;
        }
        foreach (KeyValuePair<string, Packet> item in session.Roster)
        {
            PeerRow peerRow = new PeerRow(rows, item.Key, item.Value, RoleOf(session, item.Value));
            peerRows.Add(peerRow);
            rows.Layout.Add(peerRow);
        }
    }

    private static string RoleOf(CollaborationSession session, Packet peer)
    {
        if (session.IsHost)
        {
            return "Guest";
        }
        return (session.Roster.Count == 1) ? "Host" : "—";
    }
}
