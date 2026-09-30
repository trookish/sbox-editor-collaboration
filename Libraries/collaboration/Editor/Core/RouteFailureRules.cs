using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TeamCreate;

/// <summary>One route a join attempt tried, and how it ended.</summary>
public sealed record RouteAttempt(string Source, string Target, string Reason, string RelayCode = null);

public static class RouteFailureRules
{
    public const int MaxLength = 480;

    /// <summary>
    /// Turns "every route failed" into something a person can act on. The raw socket message ("the target
    /// machine actively refused it") names a symptom on one address; what a friend needs is which routes were
    /// tried and what to ask the host to change, and that differs by which routes the code actually offered.
    /// </summary>
    public static string Summarize(IReadOnlyList<RouteAttempt> attempts)
    {
        if (attempts == null || attempts.Count == 0)
        {
            return "This invite code offers no route to the host. Ask the host for a new code.";
        }
        bool relayTried = attempts.Any(a => a.Source == RelayProtocol.RouteSource);
        RouteAttempt relayAttempt = attempts.FirstOrDefault(a => a.Source == RelayProtocol.RouteSource);
        bool directTried = attempts.Any(a => a.Source == "code" || a.Source == "advanced");
        StringBuilder text = new StringBuilder("Could not reach the host. ");
        bool loopback = relayAttempt != null && relayAttempt.RelayCode == RelayProtocol.LoopbackCode;
        if (relayAttempt != null)
        {
            text.Append(loopback
                ? "This code points to a relay on the host's own computer, which only works for the host. "
                : relayAttempt.RelayCode == "no-host"
                    ? "The relay is reachable but the host is not connected to it. "
                    : "The relay route failed (" + Trim(relayAttempt.Reason) + "). ");
        }
        RouteAttempt direct = attempts.FirstOrDefault(a => a.Source == "code");
        if (direct != null)
        {
            text.Append("The host's public address " + direct.Target + " did not accept a connection (" + Trim(direct.Reason) + "). ");
        }
        if (!relayTried && directTried)
        {
            text.Append("This code has no relay route, so it depends on the host's router forwarding the session port. ");
        }
        if (loopback)
        {
            text.Append("Ask the host to start a new session and send a new code.");
        }
        else if (relayAttempt != null && relayAttempt.RelayCode == "no-host")
        {
            text.Append("Ask the host to start, or restart, the session and send a new code.");
        }
        else if (!relayTried)
        {
            text.Append("Ask the host to configure a relay (RelayUrl in ProjectSettings/collaboration.json), to forward the port, or join from the same network.");
        }
        else
        {
            text.Append("Try again in a moment, or ask the host to confirm the relay is running.");
        }
        return CollaborationDiagnostics.Bounded(text.ToString().Trim(), MaxLength);
    }

    private static string Trim(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "no reason given";
        }
        string one = reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length <= 90 ? one.TrimEnd('.') : one.Substring(0, 90).TrimEnd('.') + "…";
    }
}

/// <summary>
/// The relay access key is a credential a host presents to create a room. It lives in an untracked local
/// file rather than the shared project settings, so committing the project never publishes it.
/// </summary>
public static class RelayAccessKeyFile
{
    public const string FileName = "relay-access-key.txt";

    /// <summary>Reads the first non-blank, non-comment line. Returns null when there is none, and an error when it is unusable.</summary>
    public static string Parse(string text, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }
            if (line.Length > RelayProtocol.MaxAccessKeyChars)
            {
                error = "The relay access key is longer than " + RelayProtocol.MaxAccessKeyChars + " characters.";
                return null;
            }
            foreach (char c in line)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    error = "The relay access key must be a single word with no spaces or control characters.";
                    return null;
                }
            }
            return line;
        }
        return null;
    }
}
