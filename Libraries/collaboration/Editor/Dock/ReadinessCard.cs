using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Editor;
using Sandbox;

namespace TeamCreate;

public sealed class ReadinessCard : DockCard
{
    private sealed class ItemBody : Widget
    {
        private readonly ReadinessItem item;

        public ItemBody(Widget parent, ReadinessItem item)
            : base(parent)
        {
            this.item = item;
            FixedHeight = DockTheme.RowHeight;
            ToolTip = item.Detail ?? string.Empty;
            OnPaintOverride = PaintItem;
        }

        private bool PaintItem()
        {
            Rect localRect = LocalRect;
            Paint.SetPen(DockTheme.StatusColor(item.Level), 0f, PenStyle.Solid);
            Paint.DrawIcon(new Rect(localRect.Left, localRect.Top, 16f, localRect.Height), DockTheme.LevelIcon(item.Level), 14f, TextFlag.LeftCenter);
            Paint.SetPen(Theme.Text.WithAlpha(0.55f), 0f, PenStyle.Solid);
            Paint.DrawIcon(new Rect(localRect.Left + 18f, localRect.Top, 14f, localRect.Height), DockTheme.ReadinessIcon(item.Key), 12f, TextFlag.LeftCenter);
            string text = item.Title ?? string.Empty;
            float width = ((text.Length == 0) ? 0f : (Paint.MeasureText(text).x + 8f));
            float x = localRect.Left + 36f;
            Paint.SetPen(in Theme.Text, 0f, PenStyle.Solid);
            if (width > 0f)
            {
                Paint.DrawText(new Rect(in x, localRect.Top, in width, localRect.Height), text, TextFlag.LeftCenter | TextFlag.SingleLine);
            }
            string text2 = item.Detail ?? string.Empty;
            float x2 = x + width;
            float width2 = localRect.Right - x2 - 4f;
            if (text2.Length > 0 && width2 > 8f)
            {
                string elidedText = Paint.GetElidedText(text2, width2);
                Paint.SetPen(Theme.Text.WithAlpha(0.6f), 0f, PenStyle.Solid);
                Paint.DrawText(new Rect(in x2, localRect.Top, in width2, localRect.Height), elidedText, TextFlag.LeftCenter | TextFlag.SingleLine);
            }
            return false;
        }
    }

    private readonly Widget rows;

    private readonly Label notice;

    private IReadOnlyList<ReadinessItem> lastList;

    private string signature;

    protected override bool FieldsAssigned => rows != null && notice != null;

    public ReadinessCard(Widget parent)
        : base(parent, "Readiness")
    {
        rows = Layout.Add(new Widget(this));
        rows.Layout = Layout.Column();
        rows.Layout.Spacing = 4f;
        notice = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.WarningColor
        });
        notice.Visible = false;
        Refresh();
    }

    public void Refresh()
    {
        IReadOnlyList<ReadinessItem> readiness = CollaborationSession.Current.Readiness;
        if (readiness != lastList)
        {
            lastList = readiness;
            string a = Signature(readiness);
            if (!string.Equals(a, signature, StringComparison.Ordinal))
            {
                signature = a;
                Rebuild(readiness);
            }
        }
    }

    private static string Signature(IReadOnlyList<ReadinessItem> list)
    {
        if (list == null || list.Count == 0)
        {
            return string.Empty;
        }
        StringBuilder stringBuilder = new StringBuilder(list.Count * 64);
        foreach (ReadinessItem item in list)
        {
            if ((object)item != null)
            {
                stringBuilder.Append(item.Key).Append('|').Append(item.Level)
                    .Append('|')
                    .Append(item.Title)
                    .Append('|')
                    .Append(item.Detail)
                    .Append('|')
                    .Append(item.Fix)
                    .Append('\n');
            }
        }
        return stringBuilder.ToString();
    }

    private void Rebuild(IReadOnlyList<ReadinessItem> list)
    {
        rows.Layout.Clear(deleteWidgets: true);
        if (list == null || list.Count == 0)
        {
            rows.Layout.Add(new Label("The readiness checklist has not been evaluated yet.", this)
            {
                WordWrap = true,
                Color = DockTheme.MutedColor
            });
            return;
        }
        foreach (ReadinessItem item in list)
        {
            if ((object)item != null)
            {
                Widget widget = rows.Layout.Add(new Widget(rows));
                widget.Layout = Layout.Row();
                widget.Layout.Spacing = 4f;
                widget.Layout.Add(new ItemBody(widget, item), 1);
                Widget widget2 = FixButton(widget, item);
                if (widget2 != null)
                {
                    widget.Layout.Add(widget2);
                }
            }
        }
    }

    private Widget FixButton(Widget parent, ReadinessItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Fix))
        {
            return null;
        }
        if (string.Equals(item.Fix, "Update", StringComparison.Ordinal))
        {
            return new Button("Update", parent)
            {
                ToolTip = "Install the current copy of the collaboration library from Asset Party.",
                Clicked = () =>
                {
                    _ = UpdateLibrary();
                }
            };
        }
        return new Button(item.Fix, parent)
        {
            Enabled = false,
            ToolTip = "This build has no action for this item."
        };
    }

    private async Task UpdateLibrary()
    {
        try
        {
            CollaborationConfig config = CollaborationSession.Current.Config;
            string ident = FullIdent(config);
            if (ident == null)
            {
                SetNotice("The collaboration library ident is not configured in ProjectSettings/collaboration.json.");
                return;
            }
            SetNotice("Requesting the current copy of " + ident + " from Asset Party…");
            Package package = await Package.FetchAsync(ident, partial: false);
            if (package?.Revision == null)
            {
                SetNotice("The package " + ident + " could not be fetched from Asset Party.");
                return;
            }
            await LibrarySystem.Install(ident, package.Revision.VersionId);
            SetNotice("The collaboration library is being installed. The checklist reports the installed version when it finishes.");
        }
        catch (Exception ex)
        {
            Exception e = ex;
            SetNotice(CollaborationDiagnostics.Bounded(e.Message));
        }
    }

    private static string FullIdent(CollaborationConfig config)
    {
        string text = config?.LibraryIdent;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        if (text.Contains('.'))
        {
            return text;
        }
        string org = config.Org;
        return string.IsNullOrWhiteSpace(org) ? text : (org + "." + text);
    }

    private void SetNotice(string text)
    {
        notice.Text = text ?? string.Empty;
        notice.Visible = notice.Text.Length > 0;
    }
}
