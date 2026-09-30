using System.Security.Cryptography;

namespace TeamCreate.Relay;

/// <summary>
/// Every limit the relay enforces, with defaults sized for a handful of friends on one small machine.
/// Each is a real bound on memory or work, because the first thing an unauthenticated stranger can do is
/// open a socket, and nothing about that should cost more than a few kilobytes.
/// </summary>
public sealed class RelayOptions
{
    /// <summary>
    /// Access keys a host must present to create a room. Empty means anyone who can reach the relay may host,
    /// which is fine on a private network and wrong on the internet; the server says so at startup.
    /// Guests never need a key: they prove knowledge of the invite secret instead.
    /// </summary>
    public string[] AccessKeys { get; set; } = Array.Empty<string>();

    public int MaxRooms { get; set; } = 64;

    public int MaxGuestsPerRoom { get; set; } = 16;

    /// <summary>Seconds a new socket has to send its hello before it is closed.</summary>
    public int HelloTimeoutSeconds { get; set; } = 10;

    public int MaxConnectionsPerAddress { get; set; } = 32;

    public int MaxPendingPerAddress { get; set; } = 8;

    /// <summary>Wrong proofs or keys tolerated from one address inside <see cref="FailureWindowSeconds"/>.</summary>
    public int MaxFailuresPerAddress { get; set; } = 10;

    public int FailureWindowSeconds { get; set; } = 60;

    /// <summary>Sustained bytes per second one connection may push through the relay.</summary>
    public int MaxBytesPerSecond { get; set; } = 8 * 1024 * 1024;

    /// <summary>Extra headroom over the sustained rate so one large scene snapshot is not mistaken for abuse.</summary>
    public int BurstBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Seconds a connection may stay silent before the relay drops it. Presence pings arrive ten times a second.</summary>
    public int IdleTimeoutSeconds { get; set; } = 45;

    /// <summary>Frames queued for one slow receiver before it is disconnected instead of buffered forever.</summary>
    public int MaxQueuedMessages { get; set; } = 512;

    /// <summary>
    /// Read the client address from the first X-Forwarded-For hop. Turn it on only when a reverse proxy you
    /// control terminates TLS in front of the relay; otherwise any client could spoof its own address.
    /// </summary>
    public bool TrustForwardedFor { get; set; }

    public string Path { get; set; } = RelayProtocol.DefaultPath;

    public static RelayOptions FromEnvironment(Func<string, string> get)
    {
        RelayOptions options = new RelayOptions();
        string keys = get("RELAY_ACCESS_KEYS");
        if (!string.IsNullOrWhiteSpace(keys))
        {
            options.AccessKeys = keys.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        options.MaxRooms = Int(get, "RELAY_MAX_ROOMS", options.MaxRooms);
        options.MaxGuestsPerRoom = Int(get, "RELAY_MAX_GUESTS_PER_ROOM", options.MaxGuestsPerRoom);
        options.MaxConnectionsPerAddress = Int(get, "RELAY_MAX_CONNECTIONS_PER_IP", options.MaxConnectionsPerAddress);
        options.MaxBytesPerSecond = Int(get, "RELAY_MAX_BYTES_PER_SECOND", options.MaxBytesPerSecond);
        options.TrustForwardedFor = string.Equals(get("RELAY_TRUST_FORWARDED_FOR"), "true", StringComparison.OrdinalIgnoreCase);
        return options;
    }

    private static int Int(Func<string, string> get, string name, int fallback)
    {
        string text = get(name);
        return int.TryParse(text, out int value) && value > 0 ? value : fallback;
    }

    public bool AccessKeyAccepted(string presented)
    {
        if (AccessKeys.Length == 0)
        {
            return true;
        }
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }
        // Hash both sides to a fixed length so the comparison neither leaks the key's length nor its prefix.
        byte[] candidate = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(presented));
        bool accepted = false;
        foreach (string key in AccessKeys)
        {
            accepted |= CryptographicOperations.FixedTimeEquals(candidate, SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        }
        return accepted;
    }
}
