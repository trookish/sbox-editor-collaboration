using TeamCreate;

namespace TeamCreate.RelayTests;

/// <summary>
/// Runs the library's REAL relay client (RelayHost, RelayGuest, Wire, the AES-GCM framing) against any relay reachable at a URL, so the Cloudflare Worker
/// can be held to exactly the behaviour the .NET relay is held to. Nothing here knows which implementation it is talking to.
///
///   dotnet run --project Tools/RelayTests -- worker ws://localhost:8787/relay [accessKey]
///
/// With an access key, the relay under test must have been started with that key, and the refusal cases for a missing or wrong key run too.
/// </summary>
public static class WorkerConformance
{
    private static int passed;

    private static void Assert(bool value, string name)
    {
        if (!value)
        {
            throw new Exception("FAIL: " + name);
        }
        Console.WriteLine("PASS: " + name);
        passed++;
    }

    private static async Task<bool> WaitFor(Func<bool> condition, double seconds = 10)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(15);
        }
        return condition();
    }

    private static async Task<Packet> Next(Wire wire, double seconds = 10)
    {
        Packet result = null;
        await WaitFor(() => wire.Incoming.TryDequeue(out result), seconds);
        return result;
    }

    private static async Task<Wire> Accepted(RelayHost host, double seconds = 10)
    {
        Wire result = null;
        await WaitFor(() => host.Accepted.TryDequeue(out result), seconds);
        return result;
    }

    private static byte[] Secret(byte seed)
    {
        // Unique per run: rooms persist on a live relay, so a fixed secret could collide with a previous run's room.
        byte[] bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        bytes[0] = seed;
        return bytes;
    }

    private static async Task<(RelayControl Reply, bool Closed)> Raw(string url, byte[] secret, Func<ClientSocket, Task> act, double seconds = 5)
    {
        using ClientSocket socket = new ClientSocket();
        RelayControl reply = null;
        bool closed = false;
        socket.TextReceived += text =>
        {
            if (RelayProtocol.TryParseControl(text, out RelayControl c, out _))
            {
                reply ??= c;
            }
        };
        socket.Closed += (_, _) => closed = true;
        await socket.ConnectAsync(RelayProtocol.ConnectUrl(url, secret), CancellationToken.None);
        await act(socket);
        await WaitFor(() => reply != null, seconds);
        await WaitFor(() => closed, 3);
        return (reply, closed);
    }

    public static async Task<int> Run(string url, string accessKey)
    {
        string projectId = "2e9f8f81-8b38-4207-ad3e-4e7c0bea567f";
        try
        {
            // ---- host, guest, encryption, order, volume, chunking, isolation ----------------------------------------------------------
            byte[] secret = Secret(1);
            byte[] key = InviteCode.DeriveKey(secret, projectId);
            List<byte[]> hostSeen = new List<byte[]>();
            List<byte[]> guestSeen = new List<byte[]>();
            List<ClientSocket> hostSockets = new List<ClientSocket>();
            using RelayHost host = new RelayHost(() =>
            {
                ClientSocket s = new ClientSocket { OutboundObserver = b => { lock (hostSeen) hostSeen.Add(b); } };
                lock (hostSockets) hostSockets.Add(s);
                return s;
            }, url, secret, key, accessKey, new[] { TimeSpan.FromMilliseconds(80) }, anyLoopbackPort: true);
            host.Start();
            Assert(await WaitFor(() => host.State == RelayHostState.Ready), "a host registers its room and becomes ready (state " + host.State + ", " + host.Failure + ")");

            using Wire guest = await RelayGuest.ConnectAsync(() => new ClientSocket { OutboundObserver = b => { lock (guestSeen) guestSeen.Add(b); } }, url, secret, key, anyLoopbackPort: true);
            Wire accepted = await Accepted(host);
            Assert(accepted != null, "the relay announces the guest to the host");

            const string marker = "PLAINTEXT-MARKER-7C41D2";
            guest.Send(new Packet { Kind = "hello", Peer = "guest-1", Name = marker });
            Packet atHost = await Next(accepted);
            Assert(atHost != null && atHost.Kind == "hello" && atHost.Name == marker, "a packet from the guest arrives at the host intact");
            accepted.Send(new Packet { Kind = "welcome", Session = "s-1" });
            Packet atGuest = await Next(guest);
            Assert(atGuest != null && atGuest.Kind == "welcome" && atGuest.Session == "s-1", "a packet from the host arrives at the guest intact");

            byte[] needle = System.Text.Encoding.UTF8.GetBytes(marker);
            bool Contains(List<byte[]> seen)
            {
                lock (seen)
                {
                    return seen.Any((byte[] b) => b.AsSpan().IndexOf(needle) >= 0);
                }
            }
            Assert(!Contains(guestSeen) && !Contains(hostSeen), "no byte handed to the relay contains the plaintext, so the relay cannot read the session");

            for (int i = 0; i < 200; i++)
            {
                guest.Send(new Packet { Kind = "presence", Id = "p" + i });
            }
            bool inOrder = true;
            for (int i = 0; i < 200; i++)
            {
                Packet next = await Next(accepted);
                if (next == null || next.Id != "p" + i)
                {
                    inOrder = false;
                    break;
                }
            }
            Assert(inOrder, "two hundred packets sent back to back arrive in order");

            string big = new string('q', 3 * 1024 * 1024);
            accepted.Send(new Packet { Kind = "snapshot", Error = big });
            Packet bigAtGuest = await Next(guest, 30);
            Assert(bigAtGuest?.Error != null && bigAtGuest.Error.Length == big.Length, "a 3 MiB packet host to guest crosses the relay in chunks and arrives whole");
            guest.Send(new Packet { Kind = "edit", Error = big });
            Packet bigAtHost = await Next(accepted, 30);
            Assert(bigAtHost?.Error != null && bigAtHost.Error.Length == big.Length, "a 3 MiB packet guest to host arrives whole too");

            using Wire guest2 = await RelayGuest.ConnectAsync(() => new ClientSocket(), url, secret, key, anyLoopbackPort: true);
            Wire accepted2 = await Accepted(host);
            guest2.Send(new Packet { Kind = "hello", Peer = "guest-2" });
            Packet at2 = await Next(accepted2);
            Assert(accepted2 != null && at2?.Peer == "guest-2" && accepted.Incoming.IsEmpty, "a second guest gets its own channel and never reaches the first guest's wire");

            guest2.Dispose();
            Assert(await WaitFor(() => accepted2.Failure != null), "a guest that leaves surfaces to the host as a closed wire");
            guest.Send(new Packet { Kind = "presence", Id = "still-here" });
            Packet stillHere = await Next(accepted);
            Assert(stillHere?.Id == "still-here", "the first guest is unaffected by the second one leaving");

            // ---- the host's relay socket drops and comes back ---------------------------------------------------------------------------
            ClientSocket first;
            lock (hostSockets)
            {
                first = hostSockets[0];
            }
            first.Dispose();
            Assert(await WaitFor(() => guest.Failure != null), "when the host's connection drops, its guests see a closed connection instead of hanging");
            Assert(await WaitFor(() => host.State == RelayHostState.Ready && hostSockets.Count >= 2), "the host re-registers its room on its own after the drop");
            using Wire again = await RelayGuest.ConnectAsync(() => new ClientSocket(), url, secret, key, anyLoopbackPort: true);
            Wire acceptedAgain = await Accepted(host);
            again.Send(new Packet { Kind = "hello", Peer = "returned" });
            Assert((await Next(acceptedAgain))?.Peer == "returned", "a guest can join again through the re-registered room");

            // ---- refusals ------------------------------------------------------------------------------------------------------------------
            byte[] lonely = Secret(2);
            RelayRefusedException noHost = null;
            try
            {
                using Wire w = await RelayGuest.ConnectAsync(() => new ClientSocket(), url, lonely, InviteCode.DeriveKey(lonely, projectId), anyLoopbackPort: true);
            }
            catch (RelayRefusedException ex)
            {
                noHost = ex;
            }
            Assert(noHost?.Code == "no-host", "a guest arriving before its host is told the host is not connected");

            // The RIGHT room (derived from the real secret) with a proof derived from a DIFFERENT secret.
            byte[] stranger = Secret(9);
            string wrongProofHello = System.Text.Json.JsonSerializer.Serialize(new RelayControl
            {
                Type = RelayProtocol.GuestHello,
                Version = RelayProtocol.ProtocolVersion,
                Room = RelayProtocol.Hex(RelayProtocol.DeriveRoom(secret)),
                Proof = RelayProtocol.Hex(RelayProtocol.DeriveGuestProof(stranger))
            });
            (RelayControl wrongProof, bool wrongClosed) = await Raw(url, secret, s2 => s2.SendTextAsync(wrongProofHello, CancellationToken.None));
            Assert(wrongProof?.Code == "bad-proof" && wrongClosed, "a guest that names the right room with the wrong proof is refused and disconnected");

            // A DIFFERENT host (proofs from another secret) naming the live room.
            byte[] impostor = Secret(3);
            string impostorHello = System.Text.Json.JsonSerializer.Serialize(new RelayControl
            {
                Type = RelayProtocol.HostHello,
                Version = RelayProtocol.ProtocolVersion,
                Room = RelayProtocol.Hex(RelayProtocol.DeriveRoom(secret)),
                HostProof = RelayProtocol.Hex(RelayProtocol.DeriveHostProof(impostor)),
                GuestProof = RelayProtocol.Hex(RelayProtocol.DeriveGuestProof(impostor)),
                AccessKey = string.IsNullOrEmpty(accessKey) ? null : accessKey
            });
            (RelayControl taken, _) = await Raw(url, secret, s2 => s2.SendTextAsync(impostorHello, CancellationToken.None));
            Assert(taken?.Code == "room-taken", "a different host cannot take over a live room");

            (RelayControl junk, _) = await Raw(url, Secret(4), s2 => s2.SendTextAsync("this is not json", CancellationToken.None));
            Assert(junk?.Code == "bad-hello", "a first message that is not JSON is refused");
            (RelayControl binaryFirst, _) = await Raw(url, Secret(5), s2 => s2.SendBinaryAsync(new byte[] { 1, 2, 3 }, CancellationToken.None));
            Assert(binaryFirst?.Code == "bad-hello", "a first message that is binary is refused");
            byte[] versioned = Secret(6);
            string versionedHello = System.Text.Json.JsonSerializer.Serialize(new RelayControl { Type = RelayProtocol.GuestHello, Version = 99, Room = RelayProtocol.Hex(RelayProtocol.DeriveRoom(versioned)), Proof = RelayProtocol.Hex(RelayProtocol.DeriveGuestProof(versioned)) });
            (RelayControl badVersion, _) = await Raw(url, versioned, s2 => s2.SendTextAsync(versionedHello, CancellationToken.None));
            Assert(badVersion?.Code == "version", "a hello for another protocol version is refused with the version code");
            // Only a relay that routes by the address (the Worker) can hold this rule; the .NET relay reads the room from the hello alone, so it is
            // opt-in with ROOM_IN_ADDRESS=1 rather than a difference in behaviour that would make the two relays look non-conforming.
            if (Environment.GetEnvironmentVariable("ROOM_IN_ADDRESS") == "1")
            {
                byte[] mismatch = Secret(7);
                (RelayControl wrongRoom, _) = await Raw(url, mismatch, s2 => s2.SendTextAsync(RelayProtocol.BuildGuestHello(Secret(8)), CancellationToken.None));
                Assert(wrongRoom?.Code == "bad-hello", "a hello whose room differs from the address it was sent to is refused");
            }
            (RelayControl oversized, _) = await Raw(url, Secret(10), s2 => s2.SendTextAsync(new string('a', 9000), CancellationToken.None));
            // "limit" (the .NET relay, at its receive buffer) and "bad-hello" (the Worker, at its control-message cap) are both refusals; what matters is that it is
            // refused and the socket closed, not which of the two words a particular relay uses.
            Assert(oversized != null && oversized.Type == RelayProtocol.Error && (oversized.Code == "limit" || oversized.Code == "bad-hello"), "an oversized first message is refused");

            // ---- access keys -----------------------------------------------------------------------------------------------------------------
            if (!string.IsNullOrEmpty(accessKey))
            {
                byte[] keyed = Secret(11);
                using RelayHost keyless = new RelayHost(() => new ClientSocket(), url, keyed, InviteCode.DeriveKey(keyed, projectId), null, new[] { TimeSpan.FromMilliseconds(80) }, anyLoopbackPort: true);
                keyless.Start();
                Assert(await WaitFor(() => keyless.State == RelayHostState.Failed) && keyless.Failure.Contains("access key"), "a host without the access key is refused and stops instead of retrying forever");
                using RelayHost wrong = new RelayHost(() => new ClientSocket(), url, keyed, InviteCode.DeriveKey(keyed, projectId), "not-the-key", new[] { TimeSpan.FromMilliseconds(80) }, anyLoopbackPort: true);
                wrong.Start();
                Assert(await WaitFor(() => wrong.State == RelayHostState.Failed), "a host with the wrong access key is refused");
            }

            // ---- host leaves ------------------------------------------------------------------------------------------------------------------
            byte[] leaveSecret = Secret(13);
            RelayHost leaver = new RelayHost(() => new ClientSocket(), url, leaveSecret, InviteCode.DeriveKey(leaveSecret, projectId), accessKey, new[] { TimeSpan.FromMilliseconds(80) }, anyLoopbackPort: true);
            leaver.Start();
            await WaitFor(() => leaver.State == RelayHostState.Ready);
            using Wire leaveGuest = await RelayGuest.ConnectAsync(() => new ClientSocket(), url, leaveSecret, InviteCode.DeriveKey(leaveSecret, projectId), anyLoopbackPort: true);
            leaver.Dispose();
            Assert(await WaitFor(() => leaveGuest.Failure != null), "when the host ends its session, its guests are told at once");
            RelayRefusedException afterLeave = null;
            try
            {
                using Wire w = await RelayGuest.ConnectAsync(() => new ClientSocket(), url, leaveSecret, InviteCode.DeriveKey(leaveSecret, projectId), anyLoopbackPort: true);
            }
            catch (RelayRefusedException ex)
            {
                afterLeave = ex;
            }
            Assert(afterLeave?.Code == "no-host", "after the host leaves, the room is gone and a later joiner is told so");

            Console.WriteLine(passed + " relay conformance checks passed against " + url);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine(passed + " checks passed before the failure");
            return 1;
        }
    }
}
