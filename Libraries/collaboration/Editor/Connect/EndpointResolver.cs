using System;
using System.Collections.Generic;

namespace TeamCreate;

public static class EndpointResolver
{
    public sealed record EndpointCandidate(string Address, int Port, string Source);

    public const int MaxCandidates = 8;

    public const string RelaySource = RelayProtocol.RouteSource;

    public static IEnumerable<EndpointCandidate> Order(InviteData invite, IReadOnlyList<DiscoveredHost> discovered, string advancedAddress, int advancedPort)
    {
        List<EndpointCandidate> ordered = new List<EndpointCandidate>(MaxCandidates);
        // The relay works from behind any NAT, so it is the route most likely to succeed when the others fail. It is held a place in the list from
        // the start: without that, a busy network with MaxCandidates LAN answers would fill the list before the relay was ever reached.
        bool relayPending = invite != null && !string.IsNullOrWhiteSpace(invite.Relay);
        if (discovered != null)
        {
            foreach (DiscoveredHost item in discovered)
            {
                if ((object)item != null)
                {
                    Add(item.EndPoint, item.Port, "lan");
                }
            }
        }
        // The relay comes after the LAN answers and before the code's direct addresses. A direct dial to a host's public address only works when its
        // router forwards the port, and when it does not the dial is refused or times out; trying the relay first costs a working direct route only
        // the few milliseconds the relay takes to answer.
        if (relayPending)
        {
            AddRelay(invite.Relay);
        }
        if (invite != null)
        {
            foreach (byte[] item2 in invite.Endpoints ?? new List<byte[]>())
            {
                Add(FormatIpv4(item2), invite.Port, "code");
            }
        }
        Add(advancedAddress, advancedPort, "advanced");
        return ordered;
        void AddRelay(string url)
        {
            relayPending = false;
            if (ordered.Count < MaxCandidates)
            {
                ordered.Add(new EndpointCandidate(url.Trim(), 0, RelaySource));
            }
        }
        void Add(string address, int port, string source)
        {
            int room = relayPending ? MaxCandidates - 1 : MaxCandidates;
            if (ordered.Count < room && !string.IsNullOrWhiteSpace(address) && port >= 1 && port <= 65535)
            {
                string text = address.Trim();
                foreach (EndpointCandidate item3 in ordered)
                {
                    if (string.Equals(item3.Address, text, StringComparison.Ordinal) && item3.Port == port)
                    {
                        return;
                    }
                }
                ordered.Add(new EndpointCandidate(text, port, source));
            }
        }
    }

    internal static string FormatIpv4(byte[] address)
    {
        if (address == null || address.Length != 4)
        {
            return null;
        }
        return address[0] + "." + address[1] + "." + address[2] + "." + address[3];
    }
}
