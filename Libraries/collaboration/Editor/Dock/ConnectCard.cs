using System;
using System.Threading.Tasks;
using Editor;
using Sandbox.Services;

namespace TeamCreate;

public sealed class ConnectCard : DockCard
{
    private const string HealthyPlayModeStatus = "Source collaboration";

    private const string PreparingIdentityText = "Preparing identity… waiting for a fresh Steam identity token; the session starts as soon as it arrives.";

    private readonly Button host;

    private readonly Button leave;

    private readonly LineEdit hostCode;

    private readonly Button copy;

    private readonly LineEdit invite;

    private readonly Label notice;

    private readonly Label tokenNotice;

    private readonly Label runtime;

    private readonly Widget advanced;

    private readonly Button advancedToggle;

    private readonly LineEdit address;

    private readonly LineEdit port;

    private readonly LineEdit key;

    private readonly Checkbox allowUnverified;

    private readonly Checkbox experimentalRuntime;

    private readonly Label playMode;

    private readonly Label lifecycle;

    private bool? lastHostShown;

    private bool? lastCodeShown;

    private bool? lastTokenShown;

    private bool? lastTokenBlocked;

    private bool? lastLifecycleShown;

    private bool? lastRuntimeShown;

    private Task<string> tokenTask;

    private string pendingToken;

    private string tokenProblem;

    private Action pendingStart;

    private Action lastStart;

    private bool retryArmed;

    private bool retrySpent;

    private static string PublishedCode => CollaborationSession.Current?.InviteCode;

    public string Address => address.Text;

    public int PortNumber => ParsePort(port.Text);

    protected override bool FieldsAssigned => host != null && leave != null && hostCode != null && copy != null && invite != null && notice != null && tokenNotice != null && runtime != null && advanced != null && advancedToggle != null && address != null && port != null && key != null && allowUnverified != null && experimentalRuntime != null && playMode != null && lifecycle != null;

    public ConnectCard(Widget parent)
        : base(parent, "Connect")
    {
        host = Layout.Add(new Button.Primary("Host session"));
        host.Clicked = () =>
        {
            DockCard.Run(Host);
        };
        leave = Layout.Add(new Button.Danger("Leave"));
        leave.Clicked = () =>
        {
            DockCard.Run(LeaveSession);
        };
        Widget widget = Layout.Add(new Widget(this));
        widget.Layout = Layout.Row();
        widget.Layout.Spacing = 4f;
        hostCode = widget.Layout.Add(new LineEdit("", this)
        {
            ReadOnly = true,
            ToolTip = "Give this code to the people you want in the session."
        }, 1);
        DockTheme.OutlineField(hostCode);
        copy = widget.Layout.Add(new Button("Copy", this)
        {
            Clicked = () =>
            {
                EditorUtility.Clipboard.Copy(hostCode.Text);
            }
        });
        notice = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.MutedColor
        });
        Widget widget2 = Layout.Add(new Widget(this));
        widget2.Layout = Layout.Row();
        widget2.Layout.Spacing = 4f;
        invite = widget2.Layout.Add(new LineEdit("", this)
        {
            PlaceholderText = "Invite code from the host"
        }, 1);
        DockTheme.OutlineField(invite);
        widget2.Layout.Add(new Button("Join", this)
        {
            Clicked = () =>
            {
                DockCard.Run(JoinSession);
            }
        });
        tokenNotice = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.BlockedColor
        });
        tokenNotice.Visible = false;
        runtime = Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.WarningColor
        });
        runtime.Visible = false;
        advancedToggle = Layout.Add(new Button("▸ Advanced", this)
        {
            ToolTip = "Direct-connection details: address, port and session key, plus the admission options. A normal join needs only the invite code."
        });
        advancedToggle.Clicked = ToggleAdvanced;
        advanced = Layout.Add(new Widget(this));
        advanced.Layout = Layout.Column();
        advanced.Layout.Spacing = 4f;
        advanced.Visible = false;
        address = AdvancedField("Host address (used only when the code has no relay or the relay fails)", "127.0.0.1");
        port = AdvancedField("Port", "27845");
        key = AdvancedField("Shared session key (12+ characters)", "");
        allowUnverified = advanced.Layout.Add(new Checkbox("Allow peers whose Steam identity could not be verified", this)
        {
            Value = false,
            ToolTip = "When off (the default), a peer is admitted only if s&box's auth service confirms its Steam account. When on, a peer is also admitted if the service is simply unreachable — its SteamID is then self-asserted. This does not weaken the transport: encryption follows how the session is joined (invite-code and discovery sessions are encrypted, Advanced sessions opened with a typed key are not), and this setting changes neither."
        });
        experimentalRuntime = advanced.Layout.Add(new Checkbox("Enable experimental runtime collaboration", this)
        {
            Value = false,
            ToolTip = "Explicit opt-in for the next play session. Runtime transport, sampling, undo, apply, runtime hotload data flow, and synchronization are not implemented. Hotload lifecycle blocking, recovery archival, and transport disposal are supported."
        });
        experimentalRuntime.Toggled = () =>
        {
            DockCard.Run(() =>
            {
                CollaborationSession.Current.ExperimentalRuntimeOptIn = experimentalRuntime.Value;
            });
        };
        playMode = advanced.Layout.Add(new Label("", this)
        {
            Color = DockTheme.MutedColor
        });
        lifecycle = advanced.Layout.Add(new Label("", this)
        {
            WordWrap = true,
            Color = DockTheme.WarningColor
        });
    }

    private LineEdit AdvancedField(string title, string value)
    {
        advanced.Layout.Add(new Label(title, this)
        {
            Color = DockTheme.MutedColor
        });
        LineEdit lineEdit = advanced.Layout.Add(new LineEdit(value, this));
        DockTheme.OutlineField(lineEdit);
        return lineEdit;
    }

    private void ToggleAdvanced()
    {
        advanced.Visible = !advanced.Visible;
        DockTheme.SetText(advancedToggle, advanced.Visible ? "▾ Advanced" : "▸ Advanced");
        AdjustSize();
        Reflow();
    }

    private void Host()
    {
        RequestStart(() =>
        {
            CollaborationSession.Current.StartHosted(ParsePort(port.Text), IdentityName(), IdentitySteamId(), TakeToken(), allowUnverified.Value);
        });
    }

    private void JoinSession()
    {
        string code = invite.Text;
        if (code != null && code.Length > 0)
        {
            RequestStart(() =>
            {
                CollaborationSession.Current.StartFromInvite(code, IdentityName(), IdentitySteamId(), TakeToken(), allowUnverified.Value, address.Text, ParsePort(port.Text));
            });
        }
        else
        {
            RequestStart(() =>
            {
                CollaborationSession.Current.Start(host: false, address.Text, ParsePort(port.Text), IdentityName(), key.Text, IdentitySteamId(), TakeToken(), allowUnverified.Value);
            });
        }
    }

    private static int ParsePort(string text)
    {
        if (!int.TryParse(text, out var result) || result < 1 || result > 65535)
        {
            throw new InvalidOperationException("The port must be a number between 1 and 65535.");
        }
        return result;
    }

    private static string BuildNotice(CollaborationSession session, string typedCode)
    {
        string discoveryStatus = session.DiscoveryStatus;
        string text = ((discoveryStatus != null && discoveryStatus.Length > 0) ? ("\n" + discoveryStatus) : string.Empty);
        // While playing, the one thing a person needs to know is why nothing they change appears for others.
        string playNotice = session.PlayNotice;
        string play = (playNotice != null) ? ("\n" + playNotice) : string.Empty;
        string solo = (session.SoloHarnessNotice != null) ? ("\n" + session.SoloHarnessNotice) : string.Empty;
        play += solo;
        if (session.Connected && !session.IsHost && session.RouteDescription != null)
        {
            return session.RouteDescription + play + text;
        }
        if (typedCode != null && typedCode.Length > 0 && !session.Connected)
        {
            return "Joining with this code: the local network is tried first, then the relay named in the code, then the host's address, then Advanced." + text;
        }
        if (session.IsHost && session.InviteCode != null)
        {
            string publicEndpointStatus = session.PublicEndpointStatus;
            string relayStatus = session.RelayStatus ?? session.NoRelayNotice;
            string relayLine = (relayStatus != null) ? ("\n" + relayStatus) : string.Empty;
            string text2 = (session.PortMappingFailed ? ("\n" + session.PortMappingStatus) : string.Empty);
            string head = (publicEndpointStatus == null) ? "Give the code above to everyone you want in this session." : (publicEndpointStatus + "\nGive the code above to everyone you want in this session.");
            return head + relayLine + text2 + play + text;
        }
        return "Enter an invite code to join, or set the host address under Advanced." + text;
    }

    public void Refresh()
    {
        CollaborationSession current = CollaborationSession.Current;
        SessionState state = current.State;
        bool flag = (uint)state <= 1u;
        bool flag2 = flag;
        if (lastHostShown != flag2)
        {
            lastHostShown = flag2;
            host.Visible = flag2;
            leave.Visible = !flag2;
        }
        if (flag2)
        {
            current.StartDiscovery();
        }
        string publishedCode = PublishedCode;
        bool flag3 = current.IsHost && publishedCode != null;
        if (lastCodeShown != flag3)
        {
            lastCodeShown = flag3;
            hostCode.Visible = flag3;
            copy.Visible = flag3;
        }
        if (flag3 && !string.Equals(hostCode.Text, publishedCode, StringComparison.Ordinal))
        {
            hostCode.Text = publishedCode;
        }
        DockCard.SetText(notice, BuildNotice(current, invite.Text));
        if (current.Error != null)
        {
            if (!retryArmed && !retrySpent && lastStart != null && !current.ReconnectRecovery && SteamIdentityAdmission.IsTokenRejection(current.Error))
            {
                ArmTokenRetry(current);
            }
            else if (!retryArmed && pendingStart != null)
            {
                pendingStart = null;
            }
        }
        PollToken();
        ContinuePendingStart();
        bool flag4 = tokenProblem != null;
        string text = tokenProblem ?? ((pendingStart != null) ? "Preparing identity… waiting for a fresh Steam identity token; the session starts as soon as it arrives." : string.Empty);
        DockCard.SetText(tokenNotice, text);
        if (lastTokenBlocked != flag4)
        {
            lastTokenBlocked = flag4;
            tokenNotice.Color = (flag4 ? DockTheme.BlockedColor : DockTheme.MutedColor);
        }
        bool flag5 = text.Length > 0;
        if (lastTokenShown != flag5)
        {
            lastTokenShown = flag5;
            tokenNotice.Visible = flag5;
        }
        if (experimentalRuntime.Value != current.ExperimentalRuntimeOptIn)
        {
            experimentalRuntime.Value = current.ExperimentalRuntimeOptIn;
        }
        string playModeStatus = current.PlayModeStatus;
        bool flag6 = !string.Equals(playModeStatus, "Source collaboration", StringComparison.Ordinal);
        if (flag6)
        {
            string runtimeLifecycleDiagnostic = current.RuntimeLifecycleDiagnostic;
            DockCard.SetText(runtime, (string.IsNullOrWhiteSpace(runtimeLifecycleDiagnostic) || string.Equals(runtimeLifecycleDiagnostic, playModeStatus, StringComparison.Ordinal)) ? playModeStatus : (playModeStatus + "\n" + runtimeLifecycleDiagnostic));
        }
        if (lastRuntimeShown != flag6)
        {
            lastRuntimeShown = flag6;
            runtime.Visible = flag6;
        }
        DockCard.SetText(playMode, playModeStatus);
        DockCard.SetText(lifecycle, current.RuntimeLifecycleDiagnostic ?? string.Empty);
        bool flag7 = lifecycle.Text.Length > 0;
        if (lastLifecycleShown != flag7)
        {
            lastLifecycleShown = flag7;
            lifecycle.Visible = flag7;
        }
    }

    private void BeginTokenRequest()
    {
        if (tokenTask != null || pendingToken != null)
        {
            return;
        }
        try
        {
            tokenTask = Auth.GetToken("sbox-editor-collaboration");
        }
        catch (Exception)
        {
            tokenProblem = "A Steam identity token could not be requested.";
        }
    }

    private void PollToken()
    {
        if (tokenTask == null || !tokenTask.IsCompleted)
        {
            return;
        }
        string value = null;
        try
        {
            value = tokenTask.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            tokenProblem = "A Steam identity token could not be obtained.";
        }
        finally
        {
            tokenTask = null;
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            pendingToken = null;
            if (tokenProblem == null)
            {
                tokenProblem = "A Steam identity token could not be obtained.";
            }
        }
        else
        {
            pendingToken = value;
        }
        if (pendingToken != null)
        {
            ContinuePendingStart();
        }
        else if (pendingStart != null)
        {
            CancelStarts();
        }
    }

    private string TakeToken()
    {
        string result = pendingToken;
        pendingToken = null;
        return result;
    }

    private void RequestStart(Action start)
    {
        lastStart = start;
        pendingStart = start;
        retryArmed = false;
        retrySpent = false;
        tokenProblem = null;
        ContinuePendingStart();
    }

    public void CancelStarts()
    {
        pendingStart = null;
        lastStart = null;
        retryArmed = false;
        retrySpent = false;
        pendingToken = null;
    }

    private void LeaveSession()
    {
        CancelStarts();
        CollaborationSession.Current.Leave();
    }

    private void ContinuePendingStart()
    {
        if (pendingStart == null || tokenTask != null)
        {
            return;
        }
        if (pendingToken == null)
        {
            BeginTokenRequest();
            if (tokenProblem != null)
            {
                CancelStarts();
            }
            return;
        }
        if (retryArmed)
        {
            CollaborationSession current = CollaborationSession.Current;
            if (current.State == SessionState.Degraded)
            {
                CancelStarts();
                return;
            }
            if (current.Busy)
            {
                return;
            }
        }
        Action action = pendingStart;
        pendingStart = null;
        retryArmed = false;
        DockCard.Run(action);
    }

    private void ArmTokenRetry(CollaborationSession session)
    {
        Action action = lastStart;
        retrySpent = true;
        try
        {
            session.Leave();
        }
        catch (Exception)
        {
            pendingStart = null;
            lastStart = null;
            return;
        }
        retryArmed = true;
        pendingStart = action;
        pendingToken = null;
        tokenProblem = null;
        session.ClearRecordedFailure();
        BeginTokenRequest();
    }

    private static string IdentityName()
    {
        if (LocalSteamIdentity.TryGet(out var _, out var personaName, out var _) && !string.IsNullOrWhiteSpace(personaName))
        {
            return personaName;
        }
        return null;
    }

    private static string IdentitySteamId()
    {
        SteamIdentity identity;
        string personaName;
        string failure;
        return LocalSteamIdentity.TryGet(out identity, out personaName, out failure) ? SteamIdentityRules.Canonical(identity) : null;
    }
}
