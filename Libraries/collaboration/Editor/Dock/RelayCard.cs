using System;
using Editor;
using Sandbox;

namespace TeamCreate;

/// <summary>
/// The Relay card. It says which relay sessions will use, and when there is none it offers the two steps that set one up from inside the editor: open the
/// Cloudflare page that creates the token, then press Set up relay to deploy it. Nothing needs to be installed and the editor does not need to be closed.
/// </summary>
public sealed class RelayCard : DockCard
{
    private readonly Label notice;

    private readonly Label hint;

    private readonly Button open;

    private readonly Button setup;

    private readonly Button cancel;

    private RelaySetupMode? lastMode;

    private string lastText;

    private bool lastProblem;

    protected override bool FieldsAssigned => notice != null && hint != null && open != null && setup != null && cancel != null;

    public RelayCard(Widget parent)
        : base(parent, "Relay")
    {
        notice = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        hint = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        open = Layout.Add(new Button("1. Open Cloudflare to create a token", this));
        open.ToolTip = "Opens Cloudflare in your browser with the one permission the relay needs already chosen (edit Workers scripts, read the account). Sign in or create a free account, click Continue, then Create Token, and copy the token it shows.";
        open.Clicked = () => DockCard.Run(RelaySetup.OpenTokenPage);
        setup = Layout.Add(new Button.Primary("2. Set up relay from the clipboard", this));
        setup.ToolTip = "Reads the token you copied, puts the relay on your own free Cloudflare account, and remembers its address on this computer. The token is used once and is not saved; it is removed from the clipboard afterwards.";
        setup.Clicked = () => DockCard.Run(RelaySetup.Start);
        cancel = Layout.Add(new Button("Cancel", this));
        cancel.ToolTip = "Stops the setup. Nothing is changed on this computer.";
        cancel.Clicked = () => DockCard.Run(RelaySetup.Cancel);
        open.Visible = false;
        setup.Visible = false;
        cancel.Visible = false;
        hint.Visible = false;
    }

    public void Refresh()
    {
        CollaborationSession current = CollaborationSession.Current;
        RelaySetupView view = RelaySetup.Describe(current.ConfiguredRelayUrl);
        string text = view.Text;
        // A relay chosen after a session started is used by the next session, because the relay connection is made when the session begins.
        if (current.IsHost && current.RelayStatus == null && (view.Mode is RelaySetupMode.Done or RelaySetupMode.Own))
        {
            text += " It is used the next time you host; end this session and host again to use it now.";
        }
        if (lastMode != view.Mode || lastText != text || lastProblem != view.IsProblem)
        {
            lastMode = view.Mode;
            lastText = text;
            lastProblem = view.IsProblem;
            notice.Color = view.IsProblem ? DockTheme.BlockedColor : DockTheme.MutedColor;
            DockCard.SetText(notice, text);
            open.Visible = view.ShowOpen;
            setup.Visible = view.ShowSetup;
            cancel.Visible = view.ShowCancel;
            AdjustSize();
            Reflow();
        }
    }
}
