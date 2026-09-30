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
        List<EndpointCandidate> ordered = new List<EndpointCandidate>(8);
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
        // The relay comes before the code's direct addresses. A direct dial to a host's public address only works
        // when its router forwards the port, and when it does not the dial is refused or times out; the relay works
        // from behind any NAT, so trying it second costs a working direct route nothing but a few milliseconds.
        if (invite != null && !string.IsNullOrWhiteSpace(invite.Relay))
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
            if (ordered.Count < 8)
            {
                ordered.Add(new EndpointCandidate(url.Trim(), 0, RelaySource));
            }
        }
        void Add(string address, int port, string source)
        {
            if (ordered.Count < 8 && !string.IsNullOrWhiteSpace(address) && ((port >= 1 && port <= 65535) || 1 == 0))
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
