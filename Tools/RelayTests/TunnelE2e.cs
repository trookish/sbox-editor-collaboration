using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using TeamCreate;

namespace TeamCreate.RelayTests;

/// <summary>
/// `tunnel-e2e &lt;cloudflared.exe&gt;`: the one-click hosting path end to end, without the editor. The host runs <see cref="EmbeddedRelay"/> on a loopback
/// port the engine permits, connects its library <see cref="RelayHost"/> over <c>ws://localhost</c>, and opens a real Quick Tunnel with the editor's own
/// arguments. <see cref="HostingPlan"/> decides every step against the wall clock, exactly as CollaborationSession drives it: it waits for the registered
/// edge connection plus 5 s, probes the public <c>/health</c> every 3 s, and publishes only on the relay's own answer. The test then encodes the invite
/// code the session would, decodes it as a friend would, and dials the PUBLIC <c>wss://…trycloudflare.com/relay</c> it carries with a library
/// <see cref="RelayGuest"/>, then exchanges a hello and a 200 KiB packet both ways. It opens a public address for about a minute.
/// </summary>
public static class TunnelE2e
{
    private const string ProjectId = "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f";

    public static async Task<int> Run( string executable )
    {
        var clock = Stopwatch.StartNew();
        double Now() => clock.Elapsed.TotalSeconds;
        void Step( string message ) => Console.WriteLine( $"[{Now():F1}s] {message}" );
        Process tunnel = null;
        EmbeddedRelay relay = null;
        var output = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();
        Task pump = Task.CompletedTask;
        try
        {
            string actual = Convert.ToHexString( SHA256.HashData( File.ReadAllBytes( executable ) ) ).ToLowerInvariant();
            if ( actual != CloudflaredPin.Sha256 )
                throw new Exception( "The executable's SHA-256 does not match the pin: " + actual );
            Step( "Pinned cloudflared re-hashed on disk: " + actual );

            var plan = new HostingPlan();
            if ( plan.Pump( new HostingInputs( Now() ) ).Action != HostingAction.StartRelay )
                throw new Exception( "The plan did not start with the relay." );
            // The editor's port choice: the first loopback port the engine's WebSocket may dial.
            int port = 0;
            foreach ( int candidate in HostingPlan.LocalRelayPorts )
            {
                var attempt = new EmbeddedRelay( candidate );
                try
                {
                    attempt.Start();
                    relay = attempt;
                    port = candidate;
                    break;
                }
                catch ( InvalidOperationException ex )
                {
                    Step( "Port " + candidate + " refused: " + ex.Message );
                    attempt.Dispose();
                }
            }
            if ( relay == null )
                throw new Exception( "No permitted loopback port was free." );
            Step( "EmbeddedRelay listening on loopback port " + port );
            EmbeddedRelay running = relay;
            pump = Task.Run( async () =>
            {
                while ( !stop.IsCancellationRequested )
                {
                    running.Pump();
                    await Task.Delay( 2 );
                }
            } );

            byte[] secret = RandomNumberGenerator.GetBytes( 16 );
            byte[] key = InviteCode.DeriveKey( secret, ProjectId );
            // The address the session's own RelayHost dials, through the default (engine) loopback port policy: anyLoopbackPort stays false.
            using var host = new RelayHost( () => new ClientSocket(), HostingPlan.LocalRelayUrl( port ), secret, key, null );
            host.Start();
            double hostDeadline = Now() + 10;
            while ( host.State != RelayHostState.Ready && Now() < hostDeadline )
                await Task.Delay( 20 );
            if ( host.State != RelayHostState.Ready )
                throw new Exception( "The local RelayHost did not register: " + host.Failure );
            Step( "Library RelayHost registered on " + HostingPlan.LocalRelayUrl( port ) );

            string Invite( string publicUrl ) => InviteCode.Encode( new InviteData
            {
                Port = 27015,
                ProjectId = ProjectId,
                Secret = secret,
                Endpoints = new List<byte[]>(),
                Relay = HostingPlan.InviteRelay( inviteCarriesRelay: publicUrl != null, embeddedRelayInUse: true, publicRelayUrl: publicUrl, effectiveRelayUrl: null, soloHarness: false )
            } );
            InviteCode.TryDecode( Invite( null ), ProjectId, out InviteData before, out _ );
            Step( "Invite before publishing carries relay: " + ( before?.Relay ?? "(none)" ) );

            HostingDecision tunnelStep = plan.Pump( new HostingInputs( Now(), RelayStarted: true, LocalHostReady: true ) );
            if ( tunnelStep.Action != HostingAction.StartTunnel )
                throw new Exception( "The plan did not ask for the tunnel after the relay." );
            var info = new ProcessStartInfo( executable, CloudflaredRules.BuildEmbeddedArguments( port ) )
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            info.Environment.Clear();
            string systemRoot = Environment.GetEnvironmentVariable( "SystemRoot" );
            if ( systemRoot != null )
                info.Environment["SystemRoot"] = systemRoot;
            tunnel = Process.Start( info ) ?? throw new Exception( "cloudflared did not start." );
            tunnel.OutputDataReceived += ( _, e ) => { if ( e.Data != null ) output.Enqueue( e.Data ); };
            tunnel.ErrorDataReceived += ( _, e ) => { if ( e.Data != null ) output.Enqueue( e.Data ); };
            tunnel.BeginOutputReadLine();
            tunnel.BeginErrorReadLine();
            Step( "cloudflared started: " + CloudflaredRules.BuildEmbeddedArguments( port ) );

            bool registered = false;
            string address = null;
            Task<string> probe = null;
            using var http = new HttpClient();
            async Task<string> Health( string healthUrl )
            {
                using var budget = new CancellationTokenSource( TimeSpan.FromSeconds( 5 ) );
                try
                {
                    using HttpResponseMessage response = await http.GetAsync( healthUrl, budget.Token );
                    string body = await response.Content.ReadAsStringAsync( budget.Token );
                    string verdict = HostingPlan.ProbeVerdict( (int)response.StatusCode, body );
                    Step( "Probe " + healthUrl + ": HTTP " + (int)response.StatusCode + ", body " + body + " -> " + verdict );
                    return verdict;
                }
                catch ( Exception ex )
                {
                    Step( "Probe " + healthUrl + ": " + ex.GetType().Name + ": " + ex.Message );
                    return HostingPlan.ProbeVerdict( null, null );
                }
            }
            HostingPhase lastPhase = tunnelStep.Phase;
            double limit = Now() + 130;
            while ( Now() < limit )
            {
                while ( output.TryDequeue( out string line ) )
                {
                    if ( address == null && ( address = CloudflaredRules.ParseTunnelAddress( line ) ) != null )
                        Step( "Public tunnel address printed: " + address );
                    if ( !registered && CloudflaredRules.IsRegisteredLine( line ) )
                    {
                        registered = true;
                        Step( "Edge connection registered" );
                    }
                }
                if ( tunnel.HasExited )
                    throw new Exception( "cloudflared exited with code " + tunnel.ExitCode + "." );
                string result = null;
                if ( probe != null && probe.IsCompleted )
                {
                    result = await probe;
                    probe = null;
                }
                HostingDecision decision = plan.Pump( new HostingInputs( Now(), RelayStarted: true, LocalHostReady: host.State == RelayHostState.Ready, TunnelRunning: address != null,
                    TunnelRelayUrl: CloudflaredRules.ToRelayUrl( address ), TunnelRegistered: registered, ProbeResult: result ) );
                if ( decision.Phase != lastPhase )
                {
                    lastPhase = decision.Phase;
                    Step( "Plan: " + decision.Phase + " - " + decision.Status );
                }
                if ( decision.Action == HostingAction.ProbeReachability )
                    probe = Health( HostingPlan.HealthUrl( decision.PublicUrl ) );
                if ( decision.Phase == HostingPhase.Failed )
                    throw new Exception( plan.Reason );
                if ( decision.Action != HostingAction.PublishRelayInvite )
                {
                    await Task.Delay( 25 );
                    continue;
                }

                // As a friend: decode the code and dial the relay it names, nothing else.
                string code = Invite( decision.PublicUrl );
                if ( !InviteCode.TryDecode( code, ProjectId, out InviteData data, out string decodeError ) )
                    throw new Exception( "The published invite did not decode: " + decodeError );
                Step( "Invite published (" + code.Length + " characters) carrying relay " + data.Relay );
                if ( data.Relay == null || !data.Relay.StartsWith( "wss://", StringComparison.Ordinal ) || !RelayProtocol.IsPublicRelay( data.Relay ) )
                    throw new Exception( "The invite does not carry a public wss relay." );
                double dial = Now();
                using Wire guest = await RelayGuest.ConnectAsync( () => new ClientSocket(), data.Relay, data.Secret, InviteCode.DeriveKey( data.Secret, data.ProjectId ) );
                Step( $"Library RelayGuest connected over the public wss address in {Now() - dial:F2}s" );
                Wire accepted = null;
                double acceptDeadline = Now() + 15;
                while ( accepted == null && Now() < acceptDeadline )
                {
                    host.Accepted.TryDequeue( out accepted );
                    await Task.Delay( 10 );
                }
                if ( accepted == null )
                    throw new Exception( "The host did not accept the guest over the public route." );
                using ( accepted )
                {
                    async Task<Packet> Receive( Wire wire )
                    {
                        Packet packet = null;
                        double until = Now() + 20;
                        while ( packet == null && Now() < until )
                        {
                            wire.Incoming.TryDequeue( out packet );
                            await Task.Delay( 5 );
                        }
                        return packet ?? throw new Exception( "A packet did not arrive within 20 s: " + wire.Failure );
                    }
                    double t = Now();
                    guest.Send( new Packet { Kind = "hello", Peer = "public-guest" } );
                    if ( ( await Receive( accepted ) ).Peer != "public-guest" )
                        throw new Exception( "The guest's hello did not arrive." );
                    Step( $"Guest -> host hello in {Now() - t:F3}s" );
                    t = Now();
                    accepted.Send( new Packet { Kind = "hello", Peer = "local-host" } );
                    if ( ( await Receive( guest ) ).Peer != "local-host" )
                        throw new Exception( "The host's hello did not arrive." );
                    Step( $"Host -> guest hello in {Now() - t:F3}s" );
                    string payload = Convert.ToBase64String( RandomNumberGenerator.GetBytes( 150 * 1024 ) );
                    t = Now();
                    guest.Send( new Packet { Kind = "payload", Error = payload } );
                    if ( ( await Receive( accepted ) ).Error != payload )
                        throw new Exception( "The guest's 200 KiB payload arrived altered." );
                    Step( $"Guest -> host {payload.Length / 1024} KiB packet verified in {Now() - t:F3}s" );
                    t = Now();
                    accepted.Send( new Packet { Kind = "payload", Error = payload } );
                    if ( ( await Receive( guest ) ).Error != payload )
                        throw new Exception( "The host's 200 KiB payload arrived altered." );
                    Step( $"Host -> guest {payload.Length / 1024} KiB packet verified in {Now() - t:F3}s" );
                }
                Step( "RESULT: PASS" );
                return 0;
            }
            throw new Exception( "The plan published no reachable address within 130 s." );
        }
        catch ( Exception ex )
        {
            Step( "RESULT: FAIL " + ex.GetType().Name + ": " + ex );
            return 1;
        }
        finally
        {
            stop.Cancel();
            try
            {
                await pump;
            }
            catch ( Exception ) { }
            if ( tunnel != null )
            {
                if ( !tunnel.HasExited )
                    tunnel.Kill( true );
                tunnel.WaitForExit( 5000 );
                Step( "cloudflared process stopped (exited: " + tunnel.HasExited + ")" );
                tunnel.Dispose();
            }
            relay?.Dispose();
        }
    }
}
