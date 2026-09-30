using TeamCreate;

namespace TeamCreate.RelayTests;

/// <summary>
/// Counts the WebSocket messages one two-person session pushes into a relay, using the library's real SessionServer, RelayHost, RelayGuest and Wire.
/// The Cloudflare free plan bills incoming WebSocket messages at 20 to 1 against 100,000 requests a day, so this number decides how much of that
/// allowance a session uses. Presence and heartbeat are driven at the editor's own cadence (every 100 ms), for real elapsed time.
///
///   dotnet run --project Tools/RelayTests -- measure ws://localhost:8787/relay [seconds]
/// </summary>
public static class MeasureUsage
{
    private static Packet Hello(string steamId, string name, string project, string token)
    {
        return new Packet
        {
            Kind = "hello",
            Peer = Guid.NewGuid().ToString(),
            ProtocolVersion = Protocol.CurrentVersion,
            Mode = CollaborationMode.Source,
            Name = name,
            Token = token,
            Project = project,
            Baseline = "base-1",
            SteamId = steamId,
            SteamToken = "a-realistic-session-token",
            OrgIdent = "cyn",
            LibraryVersion = LibraryInfo.LibraryVersion,
            ProjectManifest = "a1b2c3d4"
        };
    }

    public static async Task<int> Run(string url, int seconds)
    {
        string projectId = "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f";
        byte[] secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        byte[] key = InviteCode.DeriveKey(secret, projectId);
        string token = Convert.ToHexString(key);
        string journal = Path.Combine(Path.GetTempPath(), "teamcreate-measure-" + Guid.NewGuid().ToString("N") + ".jsonl");
        int port;
        using (System.Net.Sockets.TcpListener probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        int hostToRelay = 0;
        int guestToRelay = 0;
        using SessionServer server = new SessionServer("127.0.0.1", port, projectId, "base-1", token, journal, null, null, key);
        server.AllowUnverifiedPeers = true;
        server.hostManifest = "a1b2c3d4";
        using RelayHost relayHost = new RelayHost(() => new ClientSocket { OutboundObserver = _ => Interlocked.Increment(ref hostToRelay) }, url, secret, key, null, new[] { TimeSpan.FromMilliseconds(80) }, anyLoopbackPort: true);
        server.Relay = relayHost;
        relayHost.Start();
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (relayHost.State != RelayHostState.Ready && DateTime.UtcNow < deadline)
        {
            server.Pump();
            await Task.Delay(20);
        }
        if (relayHost.State != RelayHostState.Ready)
        {
            Console.WriteLine("FAIL: the host could not register with the relay: " + relayHost.State + " " + relayHost.Failure);
            return 1;
        }

        // The host's own editor is a member of its session over loopback TCP, exactly as in a real session.
        using Wire local = await Wire.Connect("127.0.0.1", port, key, true);
        local.Send(Hello("76561198000000001", "Host", projectId, token));
        using Wire guest = await RelayGuest.ConnectAsync(() => new ClientSocket { OutboundObserver = _ => Interlocked.Increment(ref guestToRelay) }, url, secret, key, anyLoopbackPort: true);
        guest.Send(Hello("76561198000000002", "Guest", projectId, token));
        deadline = DateTime.UtcNow.AddSeconds(15);
        bool localWelcomed = false;
        bool guestWelcomed = false;
        while (!(localWelcomed && guestWelcomed) && DateTime.UtcNow < deadline)
        {
            server.Pump();
            while (local.Incoming.TryDequeue(out Packet a))
            {
                localWelcomed |= a.Kind == "welcome";
            }
            while (guest.Incoming.TryDequeue(out Packet b))
            {
                guestWelcomed |= b.Kind == "welcome";
            }
            await Task.Delay(10);
        }
        if (!(localWelcomed && guestWelcomed))
        {
            Console.WriteLine("FAIL: both members were not admitted (host member " + localWelcomed + ", guest " + guestWelcomed + ")");
            return 1;
        }

        // Drain, then start counting from zero so the hello and welcome are not part of the steady state.
        await Task.Delay(300);
        Interlocked.Exchange(ref hostToRelay, 0);
        Interlocked.Exchange(ref guestToRelay, 0);
        // Each member is polled every 100 ms, exactly as CollaborationSession.Frame does, and asks SendCadence (the code the editor really calls) whether to
        // send. `Legacy` reproduces the old behaviour (send every poll) so the two can be compared in the same run.
        DateTime end = DateTime.UtcNow.AddSeconds(seconds);
        DateTime nextPoll = DateTime.UtcNow;
        DateTime[] lastPresence = { DateTime.MinValue, DateTime.MinValue };
        DateTime[] lastPing = { DateTime.MinValue, DateTime.MinValue };
        string[] lastBody = { null, null };
        long tick = 0;
        while (DateTime.UtcNow < end)
        {
            DateTime now = DateTime.UtcNow;
            server.Pump();
            while (local.Incoming.TryDequeue(out _)) { }
            while (guest.Incoming.TryDequeue(out _)) { }
            if (now >= nextPoll)
            {
                nextPoll = nextPoll.AddMilliseconds(100);
                tick++;
                Wire[] wires = { local, guest };
                for (int who = 0; who < 2; who++)
                {
                    // "Moving": the camera is somewhere new on every poll. "Idle": it never changes.
                    string body = Moving ? "camera-" + tick + "-" + who : "camera-still-" + who;
                    bool changed = body != lastBody[who];
                    bool sendPresence = Legacy || SendCadence.ShouldSendPresence(changed, now - lastPresence[who], isEditing: false);
                    if (sendPresence)
                    {
                        lastBody[who] = body;
                        lastPresence[who] = now;
                        wires[who].Send(new Packet { Kind = "presence", Peer = who == 0 ? "host" : "guest", Presence = new System.Text.Json.Nodes.JsonObject { ["state"] = "idle", ["camera"] = body } });
                    }
                    if (Legacy || SendCadence.ShouldSendPing(now - lastPing[who]))
                    {
                        lastPing[who] = now;
                        wires[who].Send(new Packet { Kind = "ping" });
                    }
                }
            }
            await Task.Delay(5);
        }
        await Task.Delay(500);
        double messages = hostToRelay + guestToRelay;
        double perSecond = messages / seconds;
        double requestsPerHour = perSecond * 3600.0 / 20.0;
        Console.WriteLine("measured over " + seconds + " s, " + (Legacy ? "OLD behaviour (send every poll)" : "NEW behaviour (SendCadence)") + ", " + (Moving ? "both cameras moving" : "idle session") + ":");
        Console.WriteLine("  messages into the relay: guest " + guestToRelay + ", host " + hostToRelay + ", total " + (int)messages + "  (" + perSecond.ToString("0.0") + " per second)");
        Console.WriteLine("  billed requests per hour of session at 20:1: " + requestsPerHour.ToString("0"));
        Console.WriteLine("  hours of session per day inside the free 100,000 requests: " + (100000.0 / requestsPerHour).ToString("0.0"));
        return 0;
    }

    public static bool Legacy;

    public static bool Moving;
}
