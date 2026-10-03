using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Editor;
using Sandbox;

namespace TeamCreate;

/// <summary>
/// The one place a guest agrees to receive the host's code. It is hidden unless the host has code this project lacks or holds in another version.
/// It lists every file with size and status, the total, and (for a trusted host) the single button "Receive code from &lt;host&gt;" and "Not now".
/// For a host whose identity could not be verified it shows the formal sentence and no button. Nothing is requested before the click.
/// </summary>
public sealed class CodeCard : DockCard
{
    private const int ListedRows = 200;

    private readonly Label headline;

    private readonly Label changed;

    private readonly Label list;

    private readonly Label excess;

    private readonly Label untrusted;

    private readonly Button receive;

    private readonly Button later;

    private string signature = "";

    protected override bool FieldsAssigned => headline != null && list != null && receive != null && later != null && untrusted != null;

    public CodeCard(Widget parent)
        : base(parent, "Code")
    {
        headline = Layout.Add(new Label("", this) { WordWrap = true });
        changed = Layout.Add(new Label("", this) { WordWrap = true, Color = DockTheme.WarningColor });
        list = Layout.Add(new Label("", this) { WordWrap = true, Color = DockTheme.MutedColor });
        excess = Layout.Add(new Label("", this) { WordWrap = true, Color = DockTheme.WarningColor });
        untrusted = Layout.Add(new Label("", this) { WordWrap = true, Color = DockTheme.BlockedColor });
        receive = Layout.Add(new Button("Receive code", this)
        {
            Clicked = () => DockCard.Run(() => CollaborationSession.Current.ReceiveHostCode())
        });
        later = Layout.Add(new Button("Not now", this)
        {
            Clicked = () => DockCard.Run(() => CollaborationSession.Current.DeclineHostCode())
        });
        Visible = false;
    }

    public void Refresh()
    {
        FileSyncClient client = CollaborationSession.Current.ProjectFiles;
        CodeOffer offer = client?.Offer;
        if (client == null || offer == null)
        {
            if (Visible)
            {
                signature = "";
                Visible = false;
                Reflow();
            }
            return;
        }
        string host = client.HostName;
        bool trusted = client.CodeTrusted;
        string next = offer.Fingerprint + "|" + trusted + "|" + host + "|" + client.CodeState;
        if (next == signature && Visible)
        {
            return;
        }
        signature = next;
        SetText(headline, offer.CanOffer ? CodeOfferText.Headline(offer, host) : "The host has code for this project that cannot be received.");
        SetText(changed, offer.ChangeNotice ?? string.Empty);
        changed.Visible = offer.ChangeNotice != null;
        StringBuilder rows = new StringBuilder();
        foreach (CodeOfferFile file in offer.Files.Take(ListedRows))
        {
            rows.AppendLine(CodeOfferText.FileLine(file));
        }
        if (offer.Files.Count > ListedRows)
        {
            rows.AppendLine($"... and {offer.Files.Count - ListedRows} more file(s), all of which are part of this offer.");
        }
        SetText(list, rows.ToString().TrimEnd());
        list.Visible = offer.Files.Count > 0;
        SetText(excess, offer.ExcessNotice ?? string.Empty);
        excess.Visible = offer.ExcessNotice != null;
        SetText(untrusted, CodeTrust.UnverifiedMessage);
        untrusted.Visible = offer.CanOffer && !trusted;
        receive.Text = CodeOfferText.Button(host);
        receive.Visible = offer.CanOffer && trusted;
        later.Visible = offer.CanOffer && trusted;
        Visible = true;
        Reflow();
    }
}
