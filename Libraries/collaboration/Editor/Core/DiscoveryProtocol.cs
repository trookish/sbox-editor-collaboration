using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TeamCreate;

public sealed record DiscoveryReply(string ProjectIdHash8, int SessionPort, string HostName, string SecretHex, string Manifest8);

public static class DiscoveryProtocol
{
    public const int Port = 27846;

    public const string QueryPrefix = "TC1?";

    public const string ReplyPrefix = "TC1!";

    public const int MaxMessageBytes = 512;

    public const int MaxHostNameLength = 64;

    public static string ProjectHash8(string projectId)
    {
        if (string.IsNullOrEmpty(projectId))
        {
            throw new ArgumentException("A discovery project hash requires a project id.", "projectId");
        }
        byte[] array = SHA256.HashData(Encoding.ASCII.GetBytes(projectId));
        StringBuilder stringBuilder = new StringBuilder(8);
        for (int i = 0; i < 4; i++)
        {
            stringBuilder.Append(array[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return stringBuilder.ToString();
    }

    public static string BuildQuery(string projectIdHash8)
    {
        if (!IsHex(projectIdHash8, 8))
        {
            throw new ArgumentException("A discovery query requires an 8-character lowercase hexadecimal project id hash.", "projectIdHash8");
        }
        return "TC1?pid=" + projectIdHash8 + "\n";
    }

    /// <summary>
    /// The reply a host sends: project, port, name and revision, without the session secret. A 1.2.0 joiner requires the secret field and so skips this
    /// reply; it still joins with the full invite code, whose own routes do not depend on discovery.
    /// </summary>
    public static string BuildReply(string projectIdHash8, int sessionPort, string hostName, string manifest8)
    {
        Validate(projectIdHash8, sessionPort, manifest8);
        return "TC1!pid=" + projectIdHash8 + ";port=" + sessionPort.ToString(CultureInfo.InvariantCulture) + ";name=" + SanitizeHostName(hostName) + ";rev=" + manifest8 + "\n";
    }

    private static void Validate(string projectIdHash8, int sessionPort, string manifest8)
    {
        if (!IsHex(projectIdHash8, 8))
        {
            throw new ArgumentException("A discovery reply requires an 8-character lowercase hexadecimal project id hash.", "projectIdHash8");
        }
        if (sessionPort < 1 || sessionPort > 65535)
        {
            throw new ArgumentOutOfRangeException("sessionPort", "A discovery reply requires a TCP session port between 1 and 65535.");
        }
        if (!IsHex(manifest8, 8))
        {
            throw new ArgumentException("A discovery reply requires an 8-character lowercase hexadecimal manifest revision.", "manifest8");
        }
    }

    /// <summary>The 1.2.0 reply, which carried the session secret. Kept so the parser's handling of hosts that still send it stays tested; no host sends it now.</summary>
    public static string BuildReply(string projectIdHash8, int sessionPort, string hostName, string secretHex, string manifest8)
    {
        if (!IsHex(projectIdHash8, 8))
        {
            throw new ArgumentException("A discovery reply requires an 8-character lowercase hexadecimal project id hash.", "projectIdHash8");
        }
        if (sessionPort < 1 || sessionPort > 65535)
        {
            throw new ArgumentOutOfRangeException("sessionPort", "A discovery reply requires a TCP session port between 1 and 65535.");
        }
        if (!IsHex(secretHex, 32))
        {
            throw new ArgumentException("A discovery reply requires a 32-character lowercase hexadecimal session secret (16 bytes).", "secretHex");
        }
        if (!IsHex(manifest8, 8))
        {
            throw new ArgumentException("A discovery reply requires an 8-character lowercase hexadecimal manifest revision.", "manifest8");
        }
        return "TC1!pid=" + projectIdHash8 + ";port=" + sessionPort.ToString(CultureInfo.InvariantCulture) + ";name=" + SanitizeHostName(hostName) + ";secret=" + secretHex + ";rev=" + manifest8 + "\n";
    }

    public static bool TryParseQuery(string text, out string projectIdHash8)
    {
        projectIdHash8 = null;
        if (!TryGetPayload(text, "TC1?", out var payload))
        {
            return false;
        }
        if (!payload.StartsWith("pid=", StringComparison.Ordinal))
        {
            return false;
        }
        string text2 = payload.Substring(4);
        if (!IsHex(text2, 8))
        {
            return false;
        }
        projectIdHash8 = text2;
        return true;
    }

    public static bool TryParseReply(string text, out DiscoveryReply reply)
    {
        reply = null;
        if (!TryGetPayload(text, "TC1!", out var payload))
        {
            return false;
        }
        Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] array = payload.Split(';');
        foreach (string text2 in array)
        {
            int num = text2.IndexOf('=');
            if (num <= 0)
            {
                return false;
            }
            string key = text2.Substring(0, num);
            if (!IsKey(key))
            {
                return false;
            }
            if (dictionary.ContainsKey(key))
            {
                return false;
            }
            dictionary[key] = text2.Substring(num + 1);
        }
        if (!dictionary.TryGetValue("pid", out var value) || !IsHex(value, 8))
        {
            return false;
        }
        if (!dictionary.TryGetValue("port", out var value2) || !TryParsePort(value2, out var port))
        {
            return false;
        }
        if (!dictionary.TryGetValue("name", out var value3) || !IsAcceptableHostName(value3))
        {
            return false;
        }
        // Optional since 1.3.0: hosts no longer send it. When a 1.2.0 host does, it must still be well formed.
        if (dictionary.TryGetValue("secret", out var value4) && !IsHex(value4, 32))
        {
            return false;
        }
        if (!dictionary.TryGetValue("rev", out var value5) || !IsHex(value5, 8))
        {
            return false;
        }
        reply = new DiscoveryReply(value, port, value3, value4, value5);
        return true;
    }

    private static bool TryGetPayload(string text, string prefix, out string payload)
    {
        payload = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        if (text.Length > 512)
        {
            return false;
        }
        string text2 = text;
        if (text2.EndsWith("\n", StringComparison.Ordinal))
        {
            text2 = text2.Substring(0, text2.Length - 1);
        }
        if (text2.EndsWith("\r", StringComparison.Ordinal))
        {
            text2 = text2.Substring(0, text2.Length - 1);
        }
        string text3 = text2;
        foreach (char c in text3)
        {
            if (c < ' ' || c > '~')
            {
                return false;
            }
        }
        if (!text2.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        payload = text2.Substring(prefix.Length);
        return true;
    }

    private static bool IsKey(string key)
    {
        int length = key.Length;
        if ((length < 1 || length > 16) ? true : false)
        {
            return false;
        }
        foreach (char c in key)
        {
            if ((c < 'a' || c > 'z') && (c < '0' || c > '9'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsHex(string text, int length)
    {
        if (text == null || text.Length != length)
        {
            return false;
        }
        foreach (char c in text)
        {
            if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TryParsePort(string text, out int port)
    {
        port = 0;
        bool flag = text == null;
        bool flag2 = flag;
        int length;
        if (!flag2)
        {
            length = text.Length;
            bool flag3 = ((length < 1 || length > 5) ? true : false);
            flag2 = flag3;
        }
        if (flag2)
        {
            return false;
        }
        foreach (char c in text)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
            port = port * 10 + (c - 48);
        }
        length = port;
        return length >= 1 && length <= 65535;
    }

    private static bool IsAcceptableHostName(string name)
    {
        return name.Length <= 64 && IsCleanText(name) && !name.Contains(';');
    }

    private static bool IsCleanText(string text)
    {
        foreach (char c in text)
        {
            if (c < ' ' || c > '~')
            {
                return false;
            }
        }
        return true;
    }

    private static string SanitizeHostName(string hostName)
    {
        if (string.IsNullOrEmpty(hostName))
        {
            return string.Empty;
        }
        StringBuilder stringBuilder = new StringBuilder(Math.Min(hostName.Length, 64));
        foreach (char c in hostName)
        {
            if (stringBuilder.Length >= 64)
            {
                break;
            }
            if (c >= ' ' && c <= '~' && c != ';')
            {
                stringBuilder.Append(c);
            }
        }
        return stringBuilder.ToString();
    }
}
