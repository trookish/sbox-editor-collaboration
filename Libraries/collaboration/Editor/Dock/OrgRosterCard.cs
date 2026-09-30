using System;
using System.Collections.Generic;
using Editor;
using Sandbox;
using Sandbox.UI;

namespace TeamCreate;

public sealed class OrgRosterCard : DockCard
{
    private sealed class MemberRow : Widget
    {
        private readonly OrgRosterCard owner;

        private readonly OrgMemberInfo member;

        private readonly bool isSelf;

        private string lastStatus;

        public MemberRow(OrgRosterCard owner, Widget parent, OrgMemberInfo member, bool isSelf)
            : base(parent)
        {
            this.owner = owner;
            this.member = member;
            this.isSelf = isSelf;
            FixedHeight = 40f;
            ToolTip = ToolTipFor(StatusOf(owner.IsInSession(member.SteamId)));
            lastStatus = StatusOf(owner.IsInSession(member.SteamId));
            OnPaintOverride = PaintRow;
            MouseClick = OpenProfile;
        }

        public void SyncStatus(OrgRosterCard owner)
        {
            string text = StatusOf(owner.IsInSession(member.SteamId));
            if (!string.Equals(text, lastStatus, StringComparison.Ordinal))
            {
                lastStatus = text;
                ToolTip = ToolTipFor(text);
            }
        }

        private string StatusOf(bool inSession)
        {
            string result;
            if (inSession)
            {
                result = "In this session";
            }
            else
            {
                result = (member.Online ? "Steam online" : "Offline");
            }
            return result;
        }

        private string ToolTipFor(string status)
        {
            string text = (string.IsNullOrWhiteSpace(member.Name) ? "(unknown name)" : member.Name);
            string text2 = text + "\nSteamId: " + member.SteamId + "\n" + status;
            if (!isSelf && !member.IsFriend)
            {
                text2 += "\nNot a Steam friend — they cannot join this session.";
            }
            return text2 + "\nClick to open the Steam profile.";
        }

        private void OpenProfile()
        {
            if (ulong.TryParse(member.SteamId, out var result))
            {
                new Friend(result).OpenInOverlay();
            }
        }

        private bool PaintRow()
        {
            Rect rect = LocalRect;
            if (Paint.HasMouseOver)
            {
                Paint.SetBrush(Theme.Text.WithAlpha(0.05f));
                Paint.DrawRect(in rect, 4f);
            }
            bool flag = owner.IsInSession(member.SteamId);
            Rect rect2 = new Rect(rect.Left + 4f, rect.Top + (rect.Height - 32f) * 0.5f, 32f, 32f);
            DockTheme.DrawAvatar(rect2, member.SteamId, member.Name, flag, member.Online, !isSelf && !member.IsFriend);
            string text = (string.IsNullOrWhiteSpace(member.Name) ? "(unknown name)" : member.Name);
            if (isSelf)
            {
                text += " (you)";
            }
            string text2 = StatusOf(flag);
            float num = Paint.MeasureText(text2).x + 14f;
            float x = rect.Left + 32f + 14f;
            float width = MathF.Max(0f, rect.Right - x - num - 14f);
            Paint.SetPen(in Theme.Text, 0f, PenStyle.Solid);
            Paint.DrawText(new Rect(in x, rect.Top, in width, rect.Height), Paint.GetElidedText(text, width), TextFlag.LeftCenter | TextFlag.SingleLine);
            if (!isSelf && !member.IsFriend)
            {
                Paint.SetBrush(DockTheme.WarningColor);
                Paint.ClearPen();
                Paint.DrawCircle(new Vector2(rect.Right - num - 4f, rect.Top + rect.Height * 0.5f), 3.0);
            }
            Paint.SetPen(flag ? DockTheme.PeerColor(member.SteamId) : (member.Online ? DockTheme.OkColor : DockTheme.IdleColor), 0f, PenStyle.Solid);
            Paint.DrawText(new Rect(rect.Right - num, rect.Top, num - 6f, rect.Height), text2, TextFlag.RightCenter | TextFlag.SingleLine);
            return false;
        }
    }

    private static readonly TimeSpan RebuildInterval = TimeSpan.FromSeconds(30L);

    private readonly ScrollArea scroll;

    private readonly Editor.Label empty;

    private readonly List<MemberRow> memberRows = new List<MemberRow>();

    private HashSet<string> inSession = new HashSet<string>(StringComparer.Ordinal);

    private string selfId;

    private int lastRosterCount = -1;

    private int lastRevision = -1;

    private DateTime nextRebuild;

    private bool? hadDirectory;

    protected override bool FieldsAssigned => scroll != null && empty != null && memberRows != null;

    public OrgRosterCard(Widget parent)
        : base(parent, "Organization")
    {
        empty = Layout.Add(new Editor.Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        empty.Visible = false;
        scroll = Layout.Add(new ScrollArea(this), 1);
        scroll.Canvas = new Widget();
        scroll.Canvas.Layout = Layout.Column();
        scroll.Canvas.ContentMargins = new Margin(0f, 0f, 8f, 0f);
        scroll.Canvas.VerticalSizeMode = SizeMode.Flexible;
        scroll.Canvas.HorizontalSizeMode = SizeMode.Flexible;
        scroll.HorizontalScrollbarMode = ScrollbarMode.Off;
        scroll.Visible = false;
    }

    public void Refresh()
    {
        CollaborationSession current = CollaborationSession.Current;
        if (selfId == null && LocalSteamIdentity.TryGet(out var identity, out var _, out var _))
        {
            selfId = SteamIdentityRules.Canonical(identity);
        }
        if (current.Roster.Count != lastRosterCount)
        {
            RebuildInSession(current);
        }
        OrgDirectory organizationDirectory = current.OrganizationDirectory;
        bool flag = organizationDirectory != null;
        bool flag2 = hadDirectory != flag;
        bool flag3 = flag && (organizationDirectory.Revision != lastRevision || DateTime.UtcNow >= nextRebuild);
        if (flag2 | flag3)
        {
            Rebuild(organizationDirectory);
        }
        foreach (MemberRow memberRow in memberRows)
        {
            memberRow.SyncStatus(this);
        }
    }

    public bool IsInSession(string steamId)
    {
        if (string.IsNullOrEmpty(steamId))
        {
            return false;
        }
        if (string.Equals(steamId, selfId, StringComparison.Ordinal))
        {
            return true;
        }
        return inSession.Contains(steamId);
    }

    private void RebuildInSession(CollaborationSession session)
    {
        lastRosterCount = session.Roster.Count;
        inSession.Clear();
        foreach (Packet value in session.Roster.Values)
        {
            if (!string.IsNullOrWhiteSpace(value.SteamId))
            {
                inSession.Add(value.SteamId);
            }
        }
    }

    private void Rebuild(OrgDirectory directory)
    {
        lastRevision = directory?.Revision ?? (-1);
        hadDirectory = directory != null;
        nextRebuild = DateTime.UtcNow + RebuildInterval;
        memberRows.Clear();
        scroll.Canvas.Layout.Clear(deleteWidgets: true);
        empty.Visible = false;
        scroll.Visible = false;
        if (directory == null)
        {
            DockCard.SetText(Title, "Organization");
            ShowEmpty("No organization members found. The organization directory is not available yet.");
            return;
        }
        List<OrgMemberInfo> list = new List<OrgMemberInfo>(directory.Members);
        DockCard.SetText(Title, "Organization · " + directory.Org + " (" + list.Count + ")");
        if (list.Count == 0)
        {
            ShowEmpty((directory.Revision == 0) ? "No organization members found. Waiting for the organization service to answer." : ("No organization members found. The organization service returned no members for " + directory.Org + "."));
            return;
        }
        list.Sort(CompareMembers);
        int num = Math.Min(list.Count, 50);
        for (int i = 0; i < num; i++)
        {
            OrgMemberInfo orgMemberInfo = list[i];
            MemberRow item = scroll.Canvas.Layout.Add(new MemberRow(this, scroll.Canvas, orgMemberInfo, IsSelf(orgMemberInfo.SteamId)));
            memberRows.Add(item);
        }
        if (list.Count > num)
        {
            scroll.Canvas.Layout.Add(new Editor.Label("+" + (list.Count - num) + " more", scroll.Canvas)
            {
                Color = DockTheme.MutedColor
            });
        }
        scroll.Canvas.Layout.AddStretchCell();
        scroll.Visible = true;
        scroll.MaximumHeight = MathF.Min((float)num * 40f + 4f, 420f);
    }

    private void ShowEmpty(string reason)
    {
        empty.Text = reason;
        empty.Visible = true;
    }

    private bool IsSelf(string steamId)
    {
        return !string.IsNullOrEmpty(steamId) && string.Equals(steamId, selfId, StringComparison.Ordinal);
    }

    private int CompareMembers(OrgMemberInfo a, OrgMemberInfo b)
    {
        bool flag = IsSelf(a.SteamId);
        bool flag2 = IsSelf(b.SteamId);
        if (flag != flag2)
        {
            return (!flag) ? 1 : (-1);
        }
        if (a.Online != b.Online)
        {
            return (!a.Online) ? 1 : (-1);
        }
        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }
}
