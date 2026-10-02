using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using TeamCreate;

namespace TeamCreate.RelayTests;

/// <summary>
/// Holds the host-run relay (Libraries/collaboration/Editor/Connect/EmbeddedRelay.cs) to the SAME conformance suite as the Cloudflare Worker and the .NET relay,
/// unchanged: WorkerConformance targets a URL, so this adapter starts an EmbeddedRelay on a free loopback port, runs the suite against it, and pumps the relay
/// from this thread the way the editor frame would, until the suite finishes or a deadline passes.
///
///   dotnet run --project Tools/RelayTests -- embedded
/// </summary>
public static class EmbeddedConformance
{
    public static int Run()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var relay = new EmbeddedRelay(port);
        relay.Start();
        // The embedded relay routes by the room in the address, as the Worker does, so the suite's address-mismatch case applies to it too.
        Environment.SetEnvironmentVariable("ROOM_IN_ADDRESS", "1");
        // The suite is async and awaits on its own; only the relay's pump belongs to this thread (in the editor it is the frame).
        Task<int> suite = Task.Run(() => WorkerConformance.Run("ws://localhost:" + port + "/relay", null));
        var timer = Stopwatch.StartNew();
        while (!suite.IsCompleted && timer.Elapsed < TimeSpan.FromMinutes(3))
        {
            relay.Pump();
            Thread.Sleep(1);
        }
        if (!suite.IsCompleted)
        {
            Console.WriteLine("FAIL: the conformance suite did not finish within three minutes against the embedded relay.");
            return 1;
        }
        RelayStats stats = relay.Stats;
        Console.WriteLine("embedded relay: " + stats.FramesForwarded + " frames forwarded, " + stats.FramesThrottled + " held back by the rate limit, " + stats.FramesRefused + " refused.");
        return suite.Result;
    }
}
