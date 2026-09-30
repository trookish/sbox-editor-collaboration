using System.Collections.Generic;
using System.Text;

namespace TeamCreate;

/// <summary>
/// The words under the Host and Join buttons. One sentence says what to do next; further lines appear only when something needs the person's attention (a relay that is
/// missing, a router that did not answer, a scene that is playing). The host's public address is deliberately not shown: the invite code already carries it, and it
/// is the one detail a person pastes into a chat or a screenshot without meaning to.
/// </summary>
public static class ConnectCopy
{
    public const string Idle = "Host a session, or paste a friend's code to join.";

    public const string ShareCode = "Share this code with your friend. They paste it under Join.";

    public const string ReadyToJoin = "Ready to join with this code. The local network is tried first, then the relay in the code, then the host's address.";

    public static string Build(bool connected, bool isHost, bool hosting, string route, string typedCode, string relayLine, string portMappingFailure, string playNotice, string soloNotice, string discoveryFailure)
    {
        var lines = new List<string>();
        if (connected && !isHost && !string.IsNullOrWhiteSpace(route))
        {
            lines.Add(route);
        }
        else if (!connected && !string.IsNullOrWhiteSpace(typedCode))
        {
            lines.Add(ReadyToJoin);
        }
        else if (isHost && hosting)
        {
            lines.Add(ShareCode);
            Add(lines, relayLine);
            Add(lines, portMappingFailure);
        }
        else
        {
            lines.Add(Idle);
        }
        Add(lines, playNotice);
        Add(lines, soloNotice);
        Add(lines, discoveryFailure);
        return string.Join("\n", lines);
    }

    private static void Add(List<string> lines, string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            lines.Add(line.Trim());
        }
    }
}
