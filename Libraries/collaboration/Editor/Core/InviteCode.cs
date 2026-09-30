using System.Collections.Generic;
using System;
using System.Security.Cryptography;
using System.Text;

namespace TeamCreate;

public sealed class InviteData
{
    public int Port;

    public string ProjectId;

    public byte[] Secret;

    public List<byte[]> Endpoints;

    // The relay this session can also be reached through, as a full ws:// or wss:// URL. Null for a
    // direct-only invite, which encodes exactly as it did before relays existed.
    public string Relay;
}

public static class InviteCode
{
    public const string Prefix = "CL1-";

    private const int FormatVersion = 1;

    private const int RelayFormatVersion = 2;

    // A relay is packed as one flag byte (0 = wss, 1 = ws) and the text after the scheme, with the
    // default path left out, so a typical relay costs about twenty characters of the code.
    private const int MaxRelayBytes = 96;

    private const int SecretBytes = 16;

    private const int MaxEndpoints = 4;

    private const int HeaderBytes = 8;

    private const int MinFrameBytes = 25;

    private const int MaxTextChars = 256;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const string KeyInfoPrefix = "TeamCreate/v4|";

    public static string Encode(InviteData data)
    {
        if (data == null)
        {
            throw new ArgumentException("An invite code requires the connection details to encode.", "data");
        }
        return EncodePayload(BuildPayload(data));
    }

    internal static byte[] BuildPayload(InviteData data)
    {
        int port = data.Port;
        if ((port < 1 || port > 65535) ? true : false)
        {
            throw new ArgumentOutOfRangeException("Port", "An invite code requires a port between 1 and 65535.");
        }
        if (!Guid.TryParse(data.ProjectId, out var _))
        {
            throw new ArgumentException("An invite code requires a project id in GUID form, for example 2e9f8f81-8b38-4207-ad3e-4e7c0bea567f.", "ProjectId");
        }
        if (data.Secret == null || data.Secret.Length != 16)
        {
            throw new ArgumentException($"An invite code requires a session secret of exactly {16} bytes.", "Secret");
        }
        List<byte[]> list = data.Endpoints ?? new List<byte[]>();
        if (list.Count > 4)
        {
            throw new ArgumentOutOfRangeException("Endpoints", $"An invite code carries at most {4} endpoints.");
        }
        foreach (byte[] item in list)
        {
            if (item == null || item.Length != 4)
            {
                throw new ArgumentException("Every endpoint of an invite code must be a four-byte IPv4 address.", "Endpoints");
            }
        }
        byte[] relayBytes = PackRelay(data.Relay);
        byte[] array = new byte[8 + list.Count * 4 + 16 + (relayBytes == null ? 0 : 1 + relayBytes.Length)];
        array[0] = (byte)(relayBytes == null ? 1 : 2);
        array[1] = (byte)(data.Port >> 8);
        array[2] = (byte)data.Port;
        Array.Copy(ProjectFingerprint(data.ProjectId), 0, array, 3, 4);
        array[7] = (byte)list.Count;
        int num = 8;
        foreach (byte[] item2 in list)
        {
            Array.Copy(item2, 0, array, num, 4);
            num += 4;
        }
        Array.Copy(data.Secret, 0, array, num, 16);
        if (relayBytes != null)
        {
            array[num + 16] = (byte)relayBytes.Length;
            Array.Copy(relayBytes, 0, array, num + 17, relayBytes.Length);
        }
        return array;
    }

    internal static byte[] PackRelay(string relay)
    {
        if (string.IsNullOrWhiteSpace(relay))
        {
            return null;
        }
        if (!RelayProtocol.TryValidateUrl(relay, out Uri uri, out string error))
        {
            throw new ArgumentException(error, "Relay");
        }
        string text = uri.Authority + (string.Equals(uri.PathAndQuery, RelayProtocol.DefaultPath, StringComparison.Ordinal) ? string.Empty : uri.PathAndQuery);
        if (!string.IsNullOrEmpty(uri.Query))
        {
            throw new ArgumentException("A relay address in an invite code cannot carry a query string.", "Relay");
        }
        byte[] body = Encoding.ASCII.GetBytes(text.ToLowerInvariant());
        if (body.Length + 1 > MaxRelayBytes)
        {
            throw new ArgumentException("The relay address is too long to fit in an invite code.", "Relay");
        }
        byte[] packed = new byte[body.Length + 1];
        packed[0] = (byte)(string.Equals(uri.Scheme, "ws", StringComparison.Ordinal) ? 1 : 0);
        Array.Copy(body, 0, packed, 1, body.Length);
        return packed;
    }

    internal static bool TryUnpackRelay(byte[] packed, out string relay, out string error)
    {
        relay = null;
        if (packed == null || packed.Length < 2 || packed.Length > MaxRelayBytes || packed[0] > 1)
        {
            error = "The invite code carries a relay address this build cannot read.";
            return false;
        }
        foreach (byte b in packed.AsSpan(1))
        {
            if (b < 0x21 || b > 0x7e)
            {
                error = "The invite code carries a relay address with characters this build cannot accept.";
                return false;
            }
        }
        string text = Encoding.ASCII.GetString(packed, 1, packed.Length - 1);
        string url = (packed[0] == 1 ? "ws://" : "wss://") + text;
        if (!text.Contains('/'))
        {
            url += RelayProtocol.DefaultPath;
        }
        if (!RelayProtocol.TryValidateUrl(url, out _, out string reason))
        {
            error = "The invite code's relay address is not usable: " + reason;
            return false;
        }
        relay = url;
        error = null;
        return true;
    }

    internal static string EncodePayload(byte[] payload)
    {
        byte[] array = new byte[payload.Length + 1];
        Array.Copy(payload, 0, array, 0, payload.Length);
        array[payload.Length] = Checksum(payload);
        string text = EncodeBase32(array);
        StringBuilder stringBuilder = new StringBuilder("CL1-");
        for (int i = 0; i < text.Length; i += 4)
        {
            if (i > 0)
            {
                stringBuilder.Append('-');
            }
            stringBuilder.Append(text, i, Math.Min(4, text.Length - i));
        }
        return stringBuilder.ToString();
    }

    public static byte[] ProjectFingerprint(string projectId)
    {
        if (string.IsNullOrEmpty(projectId))
        {
            throw new ArgumentException("A project fingerprint requires a project id.", "projectId");
        }
        byte[] sourceArray = SHA256.HashData(Encoding.ASCII.GetBytes(projectId));
        byte[] array = new byte[4];
        Array.Copy(sourceArray, 0, array, 0, array.Length);
        return array;
    }

    public static byte[] NewSecret()
    {
        return RandomNumberGenerator.GetBytes(16);
    }

    public static byte[] DeriveKey(byte[] secret, string projectId)
    {
        if (secret == null || secret.Length == 0)
        {
            throw new ArgumentException("A session key requires a non-empty invite secret.", "secret");
        }
        if (!Guid.TryParse(projectId, out var _))
        {
            throw new ArgumentException("A session key requires a project id in GUID form, for example 2e9f8f81-8b38-4207-ad3e-4e7c0bea567f.", "projectId");
        }
        return HkdfSha256(secret, null, Encoding.ASCII.GetBytes("TeamCreate/v4|" + projectId), 32);
    }

    public static bool TryDecode(string text, out InviteData data, out string error)
    {
        return TryDecode(text, null, verifyProject: false, out data, out error);
    }

    public static bool TryDecode(string text, string expectedProjectId, out InviteData data, out string error)
    {
        return TryDecode(text, expectedProjectId, verifyProject: true, out data, out error);
    }

    private static bool TryDecode(string text, string expectedProjectId, bool verifyProject, out InviteData data, out string error)
    {
        data = null;
        if (text == null)
        {
            error = "An invite code is required.";
            return false;
        }
        if (text.Length > 256)
        {
            error = "The invite code is longer than any invite code this build can read.";
            return false;
        }
        if (verifyProject && !Guid.TryParse(expectedProjectId, out var _))
        {
            error = "Verifying an invite code requires a project id in GUID form.";
            return false;
        }
        string text2 = Normalize(text);
        if (!text2.StartsWith("CL1", StringComparison.Ordinal))
        {
            error = "An invite code must begin with the prefix CL1-.";
            return false;
        }
        string text3 = text2.Substring(3);
        if (text3.StartsWith("-", StringComparison.Ordinal))
        {
            text3 = text3.Substring(1);
        }
        text3 = text3.Replace("-", string.Empty);
        if (!TryDecodeBase32(text3, out var frame, out error))
        {
            return false;
        }
        if (frame.Length < 25)
        {
            error = "The invite code is too short to carry a port, a project fingerprint, a secret and a checksum.";
            return false;
        }
        if (frame[0] != 1 && frame[0] != 2)
        {
            error = $"The invite code declares format version {frame[0]}, which this build cannot read.";
            return false;
        }
        byte b = frame[7];
        if (b > 4)
        {
            error = $"The invite code declares {b} endpoints; an invite code carries at most {4}.";
            return false;
        }
        int relayField = 0;
        if (frame[0] == 2)
        {
            int relayLengthAt = 8 + b * 4 + 16;
            if (frame.Length <= relayLengthAt)
            {
                error = "The invite code carries fewer bytes than its endpoint count declares; it was truncated or its secret is incomplete.";
                return false;
            }
            relayField = 1 + frame[relayLengthAt];
        }
        int num = 25 + b * 4 + relayField;
        if (frame.Length < num)
        {
            error = "The invite code carries fewer bytes than its endpoint count declares; it was truncated or its secret is incomplete.";
            return false;
        }
        if (frame.Length > num)
        {
            error = "The invite code carries more bytes than its endpoint count declares; it was altered after it was created.";
            return false;
        }
        byte[] array = new byte[num - 1];
        Array.Copy(frame, 0, array, 0, array.Length);
        if (Checksum(array) != frame[num - 1])
        {
            error = "The invite code's checksum does not match its contents; copy the code again from the host.";
            return false;
        }
        int num2 = (frame[1] << 8) | frame[2];
        if ((num2 < 1 || num2 > 65535) ? true : false)
        {
            error = $"The invite code carries port {num2}; a valid port is between 1 and 65535.";
            return false;
        }
        if (verifyProject)
        {
            byte[] array2 = new byte[4];
            Array.Copy(frame, 3, array2, 0, 4);
            if (!((ReadOnlySpan<byte>)array2.AsSpan()).SequenceEqual((ReadOnlySpan<byte>)ProjectFingerprint(expectedProjectId)))
            {
                error = "The invite code was issued for a different project; ask the host for a code from this project.";
                return false;
            }
        }
        byte[] array3 = new byte[16];
        Array.Copy(frame, 8 + b * 4, array3, 0, 16);
        List<byte[]> list = new List<byte[]>(b);
        for (int i = 0; i < b; i++)
        {
            byte[] array4 = new byte[4];
            Array.Copy(frame, 8 + i * 4, array4, 0, 4);
            list.Add(array4);
        }
        string relayUrl = null;
        if (frame[0] == 2)
        {
            int relayStart = 8 + b * 4 + 16 + 1;
            byte[] relayBytes = new byte[relayField - 1];
            Array.Copy(frame, relayStart, relayBytes, 0, relayBytes.Length);
            if (!TryUnpackRelay(relayBytes, out relayUrl, out error))
            {
                return false;
            }
        }
        data = new InviteData
        {
            Port = num2,
            ProjectId = (verifyProject ? expectedProjectId : null),
            Secret = array3,
            Endpoints = list,
            Relay = relayUrl
        };
        error = null;
        return true;
    }

    private static bool TryDecodeBase32(string body, out byte[] frame, out string error)
    {
        frame = null;
        if (body.Length == 0)
        {
            error = "The invite code is too short to carry a port, a project fingerprint, a secret and a checksum.";
            return false;
        }
        byte[] array = new byte[(body.Length * 5 + 7) / 8];
        int num = 0;
        int num2 = 0;
        int num3 = 0;
        foreach (char c in body)
        {
            if (!TryBase32Value(c, out var value))
            {
                error = $"The invite code contains the character '{c}', which is not one of the {"0123456789ABCDEFGHJKMNPQRSTVWXYZ".Length} characters a code may use.";
                return false;
            }
            num = (num << 5) | value;
            num2 += 5;
            while (num2 >= 8)
            {
                num2 -= 8;
                array[num3++] = (byte)(num >> num2);
            }
            num &= (1 << num2) - 1;
        }
        if (num2 > 4)
        {
            error = "The invite code has an unexpected number of characters; it may have been mistyped.";
            return false;
        }
        if (num3 == 0)
        {
            error = "The invite code is too short to carry a port, a project fingerprint, a secret and a checksum.";
            return false;
        }
        frame = new byte[num3];
        Array.Copy(array, frame, num3);
        error = null;
        return true;
    }

    private static bool TryBase32Value(char c, out byte value)
    {
        switch (c)
        {
        case '0':
        case '1':
        case '2':
        case '3':
        case '4':
        case '5':
        case '6':
        case '7':
        case '8':
        case '9':
            value = (byte)(c - 48);
            return true;
        case 'A':
        case 'B':
        case 'C':
        case 'D':
        case 'E':
        case 'F':
        case 'G':
        case 'H':
            value = (byte)(c - 65 + 10);
            return true;
        case 'J':
            value = 18;
            return true;
        case 'K':
            value = 19;
            return true;
        case 'M':
            value = 20;
            return true;
        case 'N':
            value = 21;
            return true;
        case 'P':
            value = 22;
            return true;
        case 'Q':
            value = 23;
            return true;
        case 'R':
            value = 24;
            return true;
        case 'S':
            value = 25;
            return true;
        case 'T':
            value = 26;
            return true;
        case 'V':
            value = 27;
            return true;
        case 'W':
            value = 28;
            return true;
        case 'X':
            value = 29;
            return true;
        case 'Y':
            value = 30;
            return true;
        case 'Z':
            value = 31;
            return true;
        case 'O':
            value = 0;
            return true;
        case 'I':
            value = 1;
            return true;
        case 'L':
            value = 1;
            return true;
        default:
            value = 0;
            return false;
        }
    }

    private static string EncodeBase32(byte[] bytes)
    {
        StringBuilder stringBuilder = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int num = 0;
        int num2 = 0;
        foreach (byte b in bytes)
        {
            num = (num << 8) | b;
            num2 += 8;
            while (num2 >= 5)
            {
                num2 -= 5;
                stringBuilder.Append("0123456789ABCDEFGHJKMNPQRSTVWXYZ"[(num >> num2) & 0x1F]);
            }
            num &= (1 << num2) - 1;
        }
        if (num2 > 0)
        {
            stringBuilder.Append("0123456789ABCDEFGHJKMNPQRSTVWXYZ"[(num << 5 - num2) & 0x1F]);
        }
        return stringBuilder.ToString();
    }

    internal static byte Checksum(byte[] payload)
    {
        byte b = 0;
        foreach (byte b2 in payload)
        {
            b ^= b2;
            for (int j = 0; j < 8; j++)
            {
                b = (((b & 0x80) != 0) ? ((byte)((b << 1) ^ 7)) : ((byte)(b << 1)));
            }
        }
        return b;
    }

    internal static byte[] HkdfSha256(byte[] inputKeyingMaterial, byte[] salt, byte[] info, int length)
    {
        if (inputKeyingMaterial == null || inputKeyingMaterial.Length == 0)
        {
            throw new ArgumentException("HKDF requires non-empty input keying material.", "inputKeyingMaterial");
        }
        if ((length < 1 || length > 8160) ? true : false)
        {
            throw new ArgumentOutOfRangeException("length", "HKDF-SHA256 derives between 1 and 8160 bytes in one invocation.");
        }
        using HMACSHA256 hMACSHA = new HMACSHA256((salt != null && salt.Length > 0) ? salt : new byte[32]);
        byte[] key = hMACSHA.ComputeHash(inputKeyingMaterial);
        byte[] array = new byte[length];
        using HMACSHA256 hMACSHA2 = new HMACSHA256(key);
        byte[] array2 = Array.Empty<byte>();
        int num = 0;
        byte b = 1;
        while (num < length)
        {
            byte[] array3 = new byte[array2.Length + (info?.Length ?? 0) + 1];
            int num2 = 0;
            Array.Copy(array2, 0, array3, 0, array2.Length);
            num2 += array2.Length;
            if (info != null && info.Length > 0)
            {
                Array.Copy(info, 0, array3, num2, info.Length);
                num2 += info.Length;
            }
            array3[num2] = b;
            array2 = hMACSHA2.ComputeHash(array3);
            int num3 = Math.Min(array2.Length, length - num);
            Array.Copy(array2, 0, array, num, num3);
            num += num3;
            b++;
        }
        return array;
    }

    private static string Normalize(string text)
    {
        StringBuilder stringBuilder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                stringBuilder.Append(char.ToUpperInvariant(c));
            }
        }
        return stringBuilder.ToString();
    }
}
