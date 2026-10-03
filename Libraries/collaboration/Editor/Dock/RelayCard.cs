using System;
using Editor;
using Sandbox;

namespace TeamCreate;

/// <summary>
/// The Relay card. By default nothing has to be set up: when this computer hosts and no relay is named, the session opens the internet relay on its own
/// (an account-free Cloudflare Quick Tunnel to a relay the host runs, Core/HostingPlan.cs) and this card shows its progress. The checkbox turns that off
/// for people who do not want a public address (LAN only). Deploying a personal relay to one's own Cloudflare account is kept, but only behind
/// "Advanced: use your own relay", because it needs a Cloudflare login and the default path must not ask anyone to sign in anywhere.
/// </summary>
public sealed class RelayCard : DockCard
{
    private const string AutomaticText = "The internet relay starts automatically when you host, so friends anywhere can join with the invite code. No account is required.";

    private const string TurnedOffText = "The internet relay is turned off on this computer. Friends on your network can still join with the invite code.";

    private readonly Checkbox enabled;

    private readonly Checkbox sharedPlay;

    private readonly Label notice;

    private readonly Button advanced;

    private readonly Button open;

    private readonly Button setup;

    private readonly Button cancel;

    private bool advancedShown;

    private string lastText;

    private bool lastProblem;

    protected override bool FieldsAssigned => enabled != null && sharedPlay != null && notice != null && advanced != null && open != null && setup != null && cancel != null;

    public RelayCard(Widget parent)
        : base(parent, "Relay")
    {
        enabled = Layout.Add(new Checkbox("Use the internet relay when hosting", this));
        enabled.Value = RelaySetup.InternetRelayEnabled;
        enabled.ToolTip = "On by default. When on, hosting opens a public relay address so friends on other networks can join with the invite code. Turn it off to host for your network only. A change takes effect the next time you host.";
        enabled.Toggled = () => DockCard.Run(() => RelaySetup.InternetRelayEnabled = enabled.Value);
        sharedPlay = Layout.Add(new Checkbox("Shared play mode (experimental)", this));
        sharedPlay.Value = SharedPlayPreference.Enabled;
        sharedPlay.ToolTip = "Off by default. When on, one person's Play is shown to the others as a spectator window they can fly around in. Spectators see a copy of the scene drawn from positions and visual components only; no game code runs on their computer, and the game's own networking is not touched. It applies to the next session you host or join, and the host and the others must each turn it on.";
        sharedPlay.Toggled = () => DockCard.Run(() => SharedPlayPreference.Enabled = sharedPlay.Value);
        notice = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        advanced = Layout.Add(new Button("Advanced: use your own relay", this));
        advanced.ToolTip = "Puts a relay on your own Cloudflare account instead of the automatic internet relay. This requires a Cloudflare account; the automatic relay does not.";
        advanced.Clicked = () =>
        {
            advancedShown = !advancedShown;
            Refresh();
        };
        open = Layout.Add(new Button("1. Open Cloudflare to create a token", this));
        open.ToolTip = "Opens Cloudflare in your browser with the one permission the relay needs already chosen (edit Workers scripts, read the account). Sign in or create a free account, click Continue, then Create Token, and copy the token it shows.";
        open.Clicked = () => DockCard.Run(RelaySetup.OpenTokenPage);
        setup = Layout.Add(new Button.Primary("2. Set up relay from the clipboard", this));
        setup.ToolTip = "Reads the token you copied, puts the relay on your own free Cloudflare account, and remembers its address on this computer. The token is used once and is not saved; it is removed from the clipboard afterwards.";
        setup.Clicked = () => DockCard.Run(RelaySetup.Start);
        cancel = Layout.Add(new Button("Cancel", this));
        cancel.ToolTip = "Stops the setup. Nothing is changed on this computer.";
        cancel.Clicked = () => DockCard.Run(RelaySetup.Cancel);
        advanced.Visible = false;
        open.Visible = false;
        setup.Visible = false;
        cancel.Visible = false;
    }

    public void Refresh()
    {
        CollaborationSession current = CollaborationSession.Current;
        if (enabled.Value != RelaySetup.InternetRelayEnabled)
        {
            enabled.Value = RelaySetup.InternetRelayEnabled;
        }
        if (sharedPlay.Value != SharedPlayPreference.Enabled)
        {
            sharedPlay.Value = SharedPlayPreference.Enabled;
        }
        RelaySetupView view = RelaySetup.Describe(current.ConfiguredRelayUrl);
        // Missing is the default: no relay of one's own. Its own sentence describes the Cloudflare login steps, so the card says what actually happens
        // instead, and those steps appear only once the person opens the advanced option. Every other mode names a relay (or a setup the person
        // started from the advanced option) and is shown as before.
        bool defaultPath = view.Mode == RelaySetupMode.Missing;
        string text;
        bool problem;
        if (current.IsHost && current.RelayStatus != null)
        {
            text = current.RelayStatus;
            problem = current.RelayFailed || current.InternetRelayTrouble;
        }
        else if (defaultPath)
        {
            text = enabled.Value ? AutomaticText : TurnedOffText;
            problem = false;
        }
        else
        {
            text = view.Text;
            problem = view.IsProblem;
            // A relay chosen after a session started is used by the next session, because the relay connection is made when the session begins.
            if (current.IsHost && (view.Mode is RelaySetupMode.Done or RelaySetupMode.Own))
            {
                text += " It is used the next time you host; end this session and host again to use it now.";
            }
        }
        if (advancedShown && defaultPath)
        {
            // With the internet relay off, the card's own sentence already says so; only the steps are added.
            text += " " + (enabled.Value ? view.Text : RelaySetupRules.ClipboardHint);
        }
        // The login steps show only after the person asked for them, or while a setup they started is running or has failed.
        bool expanded = defaultPath ? advancedShown : view.Mode is RelaySetupMode.Working or RelaySetupMode.Failed;
        bool showOpen = expanded && view.ShowOpen;
        bool showSetup = expanded && view.ShowSetup;
        bool showCancel = view.ShowCancel;
        if (lastText != text || lastProblem != problem || advanced.Visible != defaultPath || open.Visible != showOpen || setup.Visible != showSetup || cancel.Visible != showCancel)
        {
            lastText = text;
            lastProblem = problem;
            notice.Color = problem ? DockTheme.BlockedColor : DockTheme.MutedColor;
            DockCard.SetText(notice, text);
            advanced.Visible = defaultPath;
            open.Visible = showOpen;
            setup.Visible = showSetup;
            cancel.Visible = showCancel;
            AdjustSize();
            Reflow();
        }
    }
}
