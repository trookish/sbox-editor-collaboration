using System;
using System.Collections.Generic;
using System.Text.Json;

namespace TeamCreate;

public enum HostingPhase
{
    /// <summary>Not started, or not used because the project or this computer names its own relay or turned the internet relay off.</summary>
    Off,
    StartingRelay,
    StartingTunnel,
    WaitingForTunnelConnection,
    CheckingReachability,
    Ready,
    /// <summary>The tunnel died after it was ready; the relay is out of the invite and a restart is scheduled.</summary>
    Degraded,
    /// <summary>Final for this session. LAN, direct and UPnP routes are unaffected.</summary>
    Failed
}

public enum HostingAction { None, StartRelay, StartTunnel, ProbeReachability, PublishRelayInvite, WithdrawRelayInvite }

/// <summary>
/// What the editor glue observed this frame. <see cref="TunnelError"/> is set only while a failed tunnel tool is still held (the glue disposes it after the
/// pump that reported it); <see cref="ProbeResult"/> is set only on the pump after a probe finished: <see cref="HostingPlan.ProbeOk"/> or a formal reason.
/// </summary>
public sealed record HostingInputs(
    double Now,
    bool RelayStarted = false,
    string RelayError = null,
    bool LocalHostReady = false,
    bool TunnelRunning = false,
    string TunnelRelayUrl = null,
    bool TunnelRegistered = false,
    string TunnelError = null,
    string TunnelProgress = null,
    string ProbeResult = null );

/// <summary>The plan's answer to one pump: what to do now, and the sentence the dock shows. <see cref="PublicUrl"/> is the address to probe or publish.</summary>
public sealed record HostingDecision( HostingPhase Phase, HostingAction Action, string Status, string Reason, string PublicUrl );

/// <summary>
/// One-click hosting without an account: the host runs <see cref="EmbeddedRelay"/> on loopback, a Cloudflare Quick Tunnel exposes it, and the invite code
/// gains the public <c>wss://&lt;name&gt;.trycloudflare.com/relay</c> address only once that address has been shown to answer. This class decides; the
/// editor glue (CollaborationSession) acts and reports back every frame, so every timing rule here runs under a test clock.
///
/// Why each rule exists (measured in Tools/TunnelSmoke): cloudflared prints its address before the name resolves, and trycloudflare.com tells resolvers
/// to cache "no such name" for 60 s. A friend handed the address in that first second can be locked out by their own resolver for a minute, and so can
/// the host's own check. So nothing is asked until cloudflared reports a registered edge connection and 5 s more have passed; then the public
/// <c>/health</c> is probed every 3 s, for up to 150 s (well beyond the negative cache), and only a real relay answer publishes the address.
///
/// After it was ready, a dead tunnel is withdrawn from the invite in the same pump (a code naming it would send friends nowhere) and restarted after
/// 5, 15 and 45 s; each restart gets a NEW address, which is probed from scratch and republished. A failure before the first Ready is final instead:
/// what fails there (the download, the port, the edge) is not fixed by retrying seconds later, and the host is told to host again.
/// </summary>
public sealed class HostingPlan
{
    public const string ProbeOk = "ok";

    public const double RegistrationSettleSeconds = 5;

    public const double ProbeIntervalSeconds = 3;

    /// <summary>
    /// How long, after the first probe, the public address may stay unreachable before the plan gives up. The first probe's negative DNS answer is
    /// cached for 60 s (trycloudflare.com), and a real run on the development machine needed 62 s of probing with the earlier 75 s cap; 150 s
    /// leaves the same margin again for a slower resolver without hiding a tunnel that really is dead for much longer.
    /// </summary>
    public const double ReachabilityWindowSeconds = 150;

    /// <summary>cloudflared registers within a second or two of printing its address; an address with no edge connection after this is a failed start.</summary>
    public const double RegistrationTimeoutSeconds = 30;

    public const int MaxRestarts = 3;

    private static readonly double[] RestartBackoff = { 5, 15, 45 };

    // A health answer is a few dozen bytes; anything long is some other server (an HTML error page), and is refused without being parsed.
    private const int MaxHealthChars = 1024;

    /// <summary>
    /// Where the embedded relay may listen. The host's own RelayHost reaches it through the engine's WebSocket, which allows plain <c>ws://</c> to loopback
    /// only on these ports (<see cref="RelayProtocol.LoopbackPorts"/>), so an arbitrary free port would be refused by the engine. 8443 comes first because
    /// the repository's own test relay and many local tools use 8080; 80 and 443 last because system web servers hold them.
    /// </summary>
    public static IReadOnlyList<int> LocalRelayPorts { get; } = new[] { 8443, 8080, 443, 80 };

    private const string LanStill = "Friends on your network can still join with the invite code.";

    private const string LanAlready = "Friends on your network can already join.";

    private double addressAt = -1, registeredAt = -1, firstProbeAt, nextProbeAt, restartAt;

    private bool probing, reachable, everReady;

    private string address, progress;

    public HostingPhase Phase { get; private set; } = HostingPhase.Off;

    /// <summary>Why the plan failed or degraded, as a formal sentence; null while all is well.</summary>
    public string Reason { get; private set; }

    /// <summary>Restarts begun so far (0 until the tunnel first dies after being ready).</summary>
    public int Restarts { get; private set; }

    public string Status => Describe();

    /// <summary>
    /// True only when hosting would otherwise have no relay a friend elsewhere could use, and nobody chose that: the project names no relay and does not
    /// turn relaying off, this computer set none up, and this computer has not switched the internet relay off. Every other case keeps its old path.
    /// </summary>
    public static bool Applies( string effectiveRelay, string configuredRelay, bool internetRelayEnabled )
    {
        if ( !internetRelayEnabled || !string.IsNullOrWhiteSpace( effectiveRelay ) )
            return false;
        return !string.Equals( configuredRelay?.Trim(), RelayDefaults.Off, StringComparison.OrdinalIgnoreCase );
    }

    /// <summary>The address the host's own RelayHost dials. It names loopback, so <see cref="RelayProtocol.InviteRelayFor"/> refuses to put it in an invite.</summary>
    public static string LocalRelayUrl( int port ) => "ws://localhost:" + port + RelayProtocol.DefaultPath;

    /// <summary>The <c>https://&lt;host&gt;/health</c> behind a public <c>wss://</c> relay address, or null for anything else (a loopback address is never probed).</summary>
    public static string HealthUrl( string publicRelayUrl )
    {
        if ( !IsPublishable( publicRelayUrl ) || !Uri.TryCreate( publicRelayUrl.Trim(), UriKind.Absolute, out Uri uri ) )
            return null;
        return "https://" + uri.Authority + "/health";
    }

    /// <summary>
    /// <see cref="ProbeOk"/> when a probe got the relay's own health answer (HTTP 200, the JSON <c>{"status":"ok","service":"collab-relay","protocol":N}</c>
    /// of this protocol version, as both the Worker and <see cref="EmbeddedRelay"/> send it); otherwise the formal reason the address is not usable yet.
    /// A 200 from anything else (a parked page, a captive portal) does not count.
    /// </summary>
    public static string ProbeVerdict( int? status, string body )
    {
        if ( status == null )
            return "The public relay address could not be reached.";
        if ( status != 200 )
            return "The public relay address answered HTTP " + status + ".";
        if ( body == null || body.Length > MaxHealthChars )
            return "The public relay address did not answer as a collaboration relay.";
        try
        {
            using JsonDocument document = JsonDocument.Parse( body );
            JsonElement rootElement = document.RootElement;
            bool healthy = rootElement.ValueKind == JsonValueKind.Object
                && rootElement.TryGetProperty( "status", out JsonElement s ) && s.ValueKind == JsonValueKind.String && s.GetString() == "ok"
                && rootElement.TryGetProperty( "service", out JsonElement v ) && v.ValueKind == JsonValueKind.String && v.GetString() == "collab-relay"
                && rootElement.TryGetProperty( "protocol", out JsonElement p ) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32( out int protocol )
                && protocol == RelayProtocol.ProtocolVersion;
            return healthy ? ProbeOk : "The public relay address did not answer as a collaboration relay.";
        }
        catch ( JsonException )
        {
            return "The public relay address did not answer as a collaboration relay.";
        }
    }

    /// <summary>
    /// The relay address an invite code carries, or null. With the embedded relay in use it is the published public address and nothing else, never
    /// the loopback address the host itself dials, not even for the solo harness (whose guest reads the code on this machine but must still prove the
    /// public route). Otherwise it is the configured relay exactly as before, loopback allowed only for the solo harness.
    /// </summary>
    public static string InviteRelay( bool inviteCarriesRelay, bool embeddedRelayInUse, string publicRelayUrl, string effectiveRelayUrl, bool soloHarness )
    {
        if ( !inviteCarriesRelay )
            return null;
        return embeddedRelayInUse
            ? ( IsPublishable( publicRelayUrl ) ? publicRelayUrl.Trim() : null )
            : RelayProtocol.InviteRelayFor( effectiveRelayUrl, allowLoopback: soloHarness );
    }

    /// <summary>
    /// What the dock says when hosting has no running plan to report. With the internet relay on and nothing else naming a relay, hosting always has a
    /// plan, so the answer is that plan's own status (<paramref name="planStatus"/>, failure sentence included), or a "preparing" sentence in the
    /// moment before the plan exists: the older sentence that sends the person to a Cloudflare login is never shown on this path. The person's own
    /// opt-out is said plainly; every other case is <see cref="RelayDefaults.NoRelayNotice"/>.
    /// </summary>
    public static string NoRelayNotice( string effectiveRelay, string configuredRelay, bool internetRelayEnabled, string planStatus = null )
    {
        if ( Applies( effectiveRelay, configuredRelay, internetRelayEnabled ) )
            return string.IsNullOrWhiteSpace( planStatus ) ? "Preparing the internet relay. " + LanAlready : planStatus;
        bool projectOff = string.Equals( configuredRelay?.Trim(), RelayDefaults.Off, StringComparison.OrdinalIgnoreCase );
        if ( !internetRelayEnabled && string.IsNullOrWhiteSpace( effectiveRelay ) && !projectOff )
            return "The internet relay is turned off on this computer. " + LanStill + " Turn it on in the Relay card and host again to let friends anywhere join.";
        return RelayDefaults.NoRelayNotice( effectiveRelay, configuredRelay );
    }

    public HostingDecision Pump( HostingInputs input )
    {
        HostingAction action = HostingAction.None;
        double now = input.Now;
        progress = input.TunnelProgress;
        switch ( Phase )
        {
            case HostingPhase.Off:
                Phase = HostingPhase.StartingRelay;
                action = HostingAction.StartRelay;
                break;
            case HostingPhase.StartingRelay:
                if ( input.RelayError != null )
                    Fail( input.RelayError );
                else if ( input.RelayStarted )
                    action = BeginAttempt();
                break;
            case HostingPhase.StartingTunnel:
            case HostingPhase.WaitingForTunnelConnection:
            case HostingPhase.CheckingReachability:
                action = PumpAttempt( input, now );
                break;
            case HostingPhase.Ready:
                if ( input.RelayError != null )
                {
                    action = HostingAction.WithdrawRelayInvite;
                    Fail( input.RelayError );
                }
                else if ( input.TunnelError != null || !input.TunnelRunning )
                {
                    action = HostingAction.WithdrawRelayInvite;
                    LoseTunnel( now, input.TunnelError ?? "The tunnel stopped." );
                }
                break;
            case HostingPhase.Degraded:
                if ( input.RelayError != null )
                    Fail( input.RelayError );
                else if ( now >= restartAt )
                    action = BeginAttempt();
                break;
        }
        return new HostingDecision( Phase, action, Describe(), Reason, address );
    }

    private HostingAction BeginAttempt()
    {
        Phase = HostingPhase.StartingTunnel;
        address = null;
        addressAt = registeredAt = -1;
        probing = reachable = false;
        return HostingAction.StartTunnel;
    }

    private HostingAction PumpAttempt( HostingInputs input, double now )
    {
        if ( input.RelayError != null )
        {
            Fail( input.RelayError );
            return HostingAction.None;
        }
        if ( input.TunnelError != null )
        {
            LoseTunnel( now, input.TunnelError );
            return HostingAction.None;
        }
        if ( !input.TunnelRunning || string.IsNullOrEmpty( input.TunnelRelayUrl ) )
            return HostingAction.None;
        if ( address == null )
        {
            // Checked before anything is probed: whatever the tunnel reported, an address a friend cannot reach is never asked about or published.
            if ( !IsPublishable( input.TunnelRelayUrl ) )
            {
                Fail( "The tunnel reported an address that is not a public relay address." );
                return HostingAction.None;
            }
            address = input.TunnelRelayUrl.Trim();
            addressAt = now;
        }
        if ( registeredAt < 0 )
        {
            Phase = HostingPhase.WaitingForTunnelConnection;
            if ( !input.TunnelRegistered )
            {
                if ( now - addressAt >= RegistrationTimeoutSeconds )
                    LoseTunnel( now, "The tunnel did not register a connection with Cloudflare within " + RegistrationTimeoutSeconds + " seconds." );
                return HostingAction.None;
            }
            registeredAt = now;
            firstProbeAt = nextProbeAt = now + RegistrationSettleSeconds;
        }
        Phase = HostingPhase.CheckingReachability;
        if ( probing && input.ProbeResult != null )
        {
            probing = false;
            if ( input.ProbeResult == ProbeOk )
                reachable = true;
            else
            {
                Reason = input.ProbeResult;
                nextProbeAt = now + ProbeIntervalSeconds;
            }
        }
        if ( reachable && input.LocalHostReady )
        {
            Phase = HostingPhase.Ready;
            everReady = true;
            Reason = null;
            return HostingAction.PublishRelayInvite;
        }
        if ( now >= firstProbeAt + ReachabilityWindowSeconds )
        {
            LoseTunnel( now, "The public relay address did not become reachable within " + ReachabilityWindowSeconds + " seconds."
                + ( Reason != null ? " The last check said: " + Reason : string.Empty ) );
            return HostingAction.None;
        }
        if ( !probing && !reachable && now >= nextProbeAt )
        {
            probing = true;
            return HostingAction.ProbeReachability;
        }
        return HostingAction.None;
    }

    // The tunnel is gone or will not come up. Before the first Ready that ends the plan; after it, a restart is scheduled until the budget is spent.
    private void LoseTunnel( double now, string reason )
    {
        Reason = reason;
        if ( !everReady )
        {
            Fail( reason );
            return;
        }
        if ( Restarts >= MaxRestarts )
        {
            Fail( reason );
            return;
        }
        restartAt = now + RestartBackoff[Restarts];
        Restarts++;
        Phase = HostingPhase.Degraded;
        address = null;
        probing = reachable = false;
    }

    private void Fail( string reason )
    {
        Reason = reason;
        Phase = HostingPhase.Failed;
        probing = false;
    }

    private static bool IsPublishable( string url ) =>
        url != null && url.TrimStart().StartsWith( "wss://", StringComparison.OrdinalIgnoreCase ) && RelayProtocol.IsPublicRelay( url );

    private string Describe()
    {
        switch ( Phase )
        {
            case HostingPhase.Off:
                return "The internet relay is not in use.";
            case HostingPhase.Ready:
                return Restarts == 0
                    ? "Internet relay ready. The invite code now works over the internet."
                    : "Internet relay ready again at a new address. Copy the invite code again; the earlier code no longer reaches the internet relay.";
            case HostingPhase.Failed:
                return ( everReady
                    ? "The internet relay stopped and could not be restarted. "
                    : "The internet relay could not be started. " ) + ( Reason != null ? Reason + " " : string.Empty ) + LanStill;
        }
        if ( Restarts > 0 )
            return "The internet relay stopped. Restarting it (attempt " + Restarts + " of " + MaxRestarts + "). Friends on your network can still join.";
        string lead = "Preparing the internet relay. ";
        if ( Phase == HostingPhase.StartingTunnel && !string.IsNullOrWhiteSpace( progress ) )
            lead += progress.Trim() + " ";
        else if ( Phase == HostingPhase.CheckingReachability )
            lead = "Preparing the internet relay: confirming that its public address is reachable. ";
        return lead + "Friends on your network can already join.";
    }
}
