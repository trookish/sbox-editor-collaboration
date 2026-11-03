using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamCreate;

// The relay exists because a home connection usually cannot accept an inbound TCP connection: the
// friend's dial to the host's public address is refused unless the router forwards the port, and UPnP is
// best-effort. Both peers instead dial OUT to a relay over a WebSocket, which works through any NAT.
//
// The relay is deliberately dumb. It never sees the session key: everything a guest and the host say
// to each other is the same AES-256-GCM stream the direct route already uses, carried through the
// relay as opaque bytes. What the relay does see is a room id and two proofs, all derived one-way
// from the invite secret, so it can route and gate access but can neither read nor forge session traffic.

/// <summary>Frame kinds on the host's relay connection. A guest's connection carries raw stream bytes only.</summary>
public enum RelayFrameType : byte
{
    Data = 1,
    Open = 2,
    Close = 3
}

/// <summary>One JSON control message, sent as a WebSocket text message before any stream bytes flow.</summary>
public sealed class RelayControl
{
    [JsonPropertyName( "t" )]
    public string Type { get; set; }

    [JsonPropertyName( "v" )]
    public int Version { get; set; }

    [JsonPropertyName( "room" )]
    public string Room { get; set; }

    [JsonPropertyName( "host" )]
    public string HostProof { get; set; }

    [JsonPropertyName( "guest" )]
    public string GuestProof { get; set; }

    [JsonPropertyName( "proof" )]
    public string Proof { get; set; }

    [JsonPropertyName( "key" )]
    public string AccessKey { get; set; }

    [JsonPropertyName( "code" )]
    public string Code { get; set; }

    [JsonPropertyName( "message" )]
    public string Message { get; set; }
}

public static class RelayProtocol
{
    public const int ProtocolVersion = 1;

    public const int RoomBytes = 16;

    public const int ProofBytes = 32;

    public const int HeaderBytes = 5;

    // A WebSocket message is kept far below the engine's per-message ceiling (4 MiB) and the relay's own
    // receive buffer, so a multi-megabyte scene snapshot travels as many small messages rather than one
    // that either side may refuse.
    public const int MaxChunkBytes = 16 * 1024;

    public const int MaxMessageBytes = MaxChunkBytes + HeaderBytes;

    public const int MaxControlChars = 1024;

    public const int MaxAccessKeyChars = 128;

    public const int MaxUrlChars = 200;

    public const string DefaultPath = "/relay";

    // The label a candidate route carries through the resolver, the failure summary and the dock.
    public const string RouteSource = "relay";

    /// <summary>The code recorded for a relay route that was skipped because it names the host's own computer.</summary>
    public const string LoopbackCode = "loopback";

    public const string HostHello = "host";

    public const string GuestHello = "guest";

    public const string Ok = "ok";

    public const string Error = "error";

    // Loopback is the only target the engine's HTTP allow-list lets a script reach by name and port
    // without an opt-in switch, so a local relay for testing is limited to exactly these ports.
    public static readonly int[] LoopbackPorts = { 80, 443, 8080, 8443 };

    /// <summary>
    /// True when the address can be reached by another machine. A relay on localhost or a loopback address exists only on the computer that names it,
    /// so a friend who is handed such an address dials their OWN computer and finds nothing.
    /// </summary>
    public static bool IsPublicRelay( string url )
    {
        if ( !Uri.TryCreate( url?.Trim(), UriKind.Absolute, out var parsed ) || ( parsed.Scheme != "wss" && parsed.Scheme != "ws" ) )
            return false;
        return !parsed.IsLoopback && !string.Equals( parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase )
            && !parsed.Host.EndsWith( ".localhost", StringComparison.OrdinalIgnoreCase );
    }

    /// <summary>The relay address an invite may carry: a public one, or a local one only when the joiner is known to be on this machine (the solo test).</summary>
    public static string InviteRelayFor( string url, bool allowLoopback )
    {
        if ( string.IsNullOrWhiteSpace( url ) )
            return null;
        return allowLoopback || IsPublicRelay( url ) ? url.Trim() : null;
    }

    /// <summary>
    /// The address a socket dials: the relay's address plus the room it wants. A hosted relay has to choose the right room BEFORE the first message
    /// arrives, so the room id, which is one-way derived from the secret and already known to the relay, rides in the address. The secret never does.
    /// </summary>
    public static string ConnectUrl( string url, byte[] secret )
    {
        var trimmed = url.Trim();
        return trimmed + ( trimmed.Contains( '?' ) ? "&" : "?" ) + "room=" + Hex( DeriveRoom( secret ) );
    }

    public static byte[] DeriveRoom( byte[] secret ) => Derive( secret, "room", RoomBytes );

    public static byte[] DeriveHostProof( byte[] secret ) => Derive( secret, "host-proof", ProofBytes );

    public static byte[] DeriveGuestProof( byte[] secret ) => Derive( secret, "guest-proof", ProofBytes );

    // Three independent labels over one secret. HKDF output under one label reveals nothing about
    // another, so knowing a room id or a proof does not help anyone reach the session key
    // (HKDF over the same secret with the project id as its label, in InviteCode.DeriveKey).
    private static byte[] Derive( byte[] secret, string label, int length )
    {
        if ( secret == null || secret.Length != 16 )
            throw new ArgumentException( "A relay room requires the 16-byte invite secret.", nameof( secret ) );
        return InviteCode.HkdfSha256( secret, null, Encoding.ASCII.GetBytes( "TeamCreate/relay/" + label ), length );
    }

    public static string Hex( byte[] bytes ) => Convert.ToHexString( bytes ).ToLowerInvariant();

    public static bool TryParseHex( string text, int expectedBytes, out byte[] bytes )
    {
        bytes = null;
        if ( text == null || text.Length != expectedBytes * 2 )
            return false;
        foreach ( var c in text )
        {
            if ( !( ( c >= '0' && c <= '9' ) || ( c >= 'a' && c <= 'f' ) ) )
                return false;
        }
        bytes = Convert.FromHexString( text );
        return true;
    }

    public static string BuildHostHello( byte[] secret, string accessKey )
    {
        if ( accessKey != null && accessKey.Length > MaxAccessKeyChars )
            throw new ArgumentException( "The relay access key is longer than a relay accepts.", nameof( accessKey ) );
        return JsonSerializer.Serialize( new RelayControl
        {
            Type = HostHello,
            Version = ProtocolVersion,
            Room = Hex( DeriveRoom( secret ) ),
            HostProof = Hex( DeriveHostProof( secret ) ),
            GuestProof = Hex( DeriveGuestProof( secret ) ),
            AccessKey = string.IsNullOrEmpty( accessKey ) ? null : accessKey
        }, ControlOptions );
    }

    public static string BuildGuestHello( byte[] secret )
    {
        return JsonSerializer.Serialize( new RelayControl
        {
            Type = GuestHello,
            Version = ProtocolVersion,
            Room = Hex( DeriveRoom( secret ) ),
            Proof = Hex( DeriveGuestProof( secret ) )
        }, ControlOptions );
    }

    public static string BuildOk() => JsonSerializer.Serialize( new RelayControl { Type = Ok, Version = ProtocolVersion }, ControlOptions );

    public static string BuildError( string code, string message ) =>
        JsonSerializer.Serialize( new RelayControl { Type = Error, Version = ProtocolVersion, Code = code, Message = message }, ControlOptions );

    private static readonly JsonSerializerOptions ControlOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Parses one control message. Bounded and strict: a hello is the first thing an unauthenticated
    /// stranger can send a relay, so anything oversized or malformed is refused without allocating for it.
    /// </summary>
    public static bool TryParseControl( string text, out RelayControl control, out string error )
    {
        control = null;
        if ( string.IsNullOrEmpty( text ) )
        {
            error = "The control message is empty.";
            return false;
        }
        if ( text.Length > MaxControlChars )
        {
            error = "The control message is longer than a relay accepts.";
            return false;
        }
        try
        {
            control = JsonSerializer.Deserialize<RelayControl>( text );
        }
        catch ( JsonException )
        {
            error = "The control message is not valid JSON.";
            return false;
        }
        if ( control == null || string.IsNullOrEmpty( control.Type ) )
        {
            control = null;
            error = "The control message has no type.";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>Validates a hello for shape only; proof comparison is the relay's job.</summary>
    public static string ValidateHello( RelayControl hello, out byte[] room, out byte[] hostProof, out byte[] guestProof, out byte[] proof )
    {
        room = hostProof = guestProof = proof = null;
        if ( hello.Version != ProtocolVersion )
            return "This relay speaks protocol " + ProtocolVersion + "; update the collaboration library.";
        if ( !TryParseHex( hello.Room, RoomBytes, out room ) )
            return "The room identifier is malformed.";
        if ( hello.Type == HostHello )
        {
            if ( !TryParseHex( hello.HostProof, ProofBytes, out hostProof ) || !TryParseHex( hello.GuestProof, ProofBytes, out guestProof ) )
                return "The host proofs are malformed.";
            if ( hello.AccessKey != null && hello.AccessKey.Length > MaxAccessKeyChars )
                return "The relay access key is too long.";
            return null;
        }
        if ( hello.Type == GuestHello )
        {
            return TryParseHex( hello.Proof, ProofBytes, out proof ) ? null : "The guest proof is malformed.";
        }
        return "The first message must be a host or guest hello.";
    }

    public static byte[] BuildFrame( RelayFrameType type, uint channel, ReadOnlySpan<byte> payload )
    {
        var frame = new byte[HeaderBytes + payload.Length];
        frame[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian( frame.AsSpan( 1 ), channel );
        payload.CopyTo( frame.AsSpan( HeaderBytes ) );
        return frame;
    }

    public static bool TryParseFrame( ReadOnlySpan<byte> message, out RelayFrameType type, out uint channel, out ReadOnlySpan<byte> payload )
    {
        type = 0;
        channel = 0;
        payload = default;
        if ( message.Length < HeaderBytes || message[0] < 1 || message[0] > 3 )
            return false;
        type = (RelayFrameType)message[0];
        channel = BinaryPrimitives.ReadUInt32BigEndian( message.Slice( 1 ) );
        payload = message.Slice( HeaderBytes );
        // Open and Close carry no payload; a frame that does is malformed rather than ignorable.
        return type == RelayFrameType.Data || payload.Length == 0;
    }

    /// <summary>The sentence a person sees for a relay refusal. Every code the relay sends has one.</summary>
    public static string UserMessage( string code, string fallback )
    {
        switch ( code )
        {
            case "no-host":
                return "The host is not connected to the relay. Ask the host to start, or restart, the session and send a new code.";
            case "bad-proof":
                return "The relay refused this invite code. Copy the code again from the host.";
            case "bad-key":
                return "The relay refused this machine's access key. Check .collaboration/relay-access-key.txt.";
            case "room-taken":
                return "Another host is already using this session's relay room.";
            case "full":
                return "The session is full on the relay.";
            case "limit":
                return "The relay is at capacity or is limiting this address. Try again shortly.";
            case "idle":
                return "The shared relay closed this session's room after a long time with no activity. Reconnecting opens it again.";
            case "version":
                return fallback ?? "The relay and this library speak different protocol versions.";
            default:
                return string.IsNullOrWhiteSpace( fallback ) ? "The relay refused the connection." : "The relay refused the connection: " + fallback;
        }
    }

    /// <summary>
    /// True when a code means retrying cannot help, so the host should stop instead of reconnecting forever.
    /// </summary>
    public static bool IsTerminal( string code ) => code == "bad-key" || code == "bad-proof" || code == "room-taken" || code == "version" || code == "bad-hello" || code == LoopbackCode;

    /// <summary>
    /// The engine's HTTP client, which every script WebSocket goes through, refuses IP addresses, private
    /// ranges and any loopback port outside a short list. Checking the same rules here turns a confusing
    /// "not allowed" at connect time into a precise message at configuration time. <c>ws://</c> is loopback-only
    /// because the hello carries the room proofs, and those must not cross the internet unencrypted.
    /// </summary>
    public static bool TryValidateUrl( string url, out Uri uri, out string error, bool anyLoopbackPort = false )
    {
        uri = null;
        if ( string.IsNullOrWhiteSpace( url ) )
        {
            error = "No relay address is configured.";
            return false;
        }
        if ( url.Length > MaxUrlChars )
        {
            error = "The relay address is longer than " + MaxUrlChars + " characters.";
            return false;
        }
        if ( !Uri.TryCreate( url.Trim(), UriKind.Absolute, out var parsed ) || ( parsed.Scheme != "wss" && parsed.Scheme != "ws" ) )
        {
            error = "The relay address must be a ws:// or wss:// URL, for example wss://relay.example.com/relay.";
            return false;
        }
        if ( !string.IsNullOrEmpty( parsed.UserInfo ) || !string.IsNullOrEmpty( parsed.Fragment ) )
        {
            error = "The relay address must not contain credentials or a fragment.";
            return false;
        }
        var loopback = parsed.IsLoopback || string.Equals( parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase );
        if ( loopback )
        {
            // anyLoopbackPort exists for the offline tests, which host a relay on an ephemeral port. The engine
            // still applies its own rule when the editor really connects, so this is never a way around it.
            if ( !anyLoopbackPort && Array.IndexOf( LoopbackPorts, parsed.Port ) < 0 )
            {
                error = "A local relay must listen on port 80, 443, 8080 or 8443; the editor refuses other local ports.";
                return false;
            }
        }
        else
        {
            if ( parsed.Scheme != "wss" )
            {
                error = "A relay on another machine must use wss:// so its room proofs are encrypted in transit.";
                return false;
            }
            if ( IPAddress.TryParse( parsed.Host.Trim( '[', ']' ), out _ ) || Uri.CheckHostName( parsed.Host ) != UriHostNameType.Dns )
            {
                error = "The editor refuses IP addresses; give the relay a DNS name.";
                return false;
            }
        }
        uri = parsed;
        error = null;
        return true;
    }
}
