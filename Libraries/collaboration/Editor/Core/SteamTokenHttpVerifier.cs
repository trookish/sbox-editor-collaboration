using System.Threading;
using System.Threading.Tasks;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamCreate;

public readonly record struct SteamTokenHttpResponse(int? Status, string Body);

public interface ISteamTokenTransport
{
    Task<SteamTokenHttpResponse> PostAsync(string url, string jsonBody, CancellationToken cancellationToken);
}

public static class SteamTokenEndpoint
{
    private sealed record SteamTokenRequest([property: JsonPropertyName("steamid")] long SteamId, [property: JsonPropertyName("token")] string Token);

    private sealed record SteamTokenResponse
    {
        [JsonPropertyName("SteamId")]
        public long? SteamId { get; set; }

        [JsonPropertyName("Status")]
        public string Status { get; set; }
    }

    public const string VerifyUrl = "https://services.facepunch.com/sbox/auth/token";

    public const string RequestSteamIdField = "steamid";

    public const string RequestTokenField = "token";

    public const string ResponseSteamIdField = "SteamId";

    public const string ResponseStatusField = "Status";

    public const int MaxResponseBytes = 65536;

    private static readonly JsonSerializerOptions ResponseOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    public static string BuildRequestBody(long claimedSteamId, string token)
    {
        return JsonSerializer.Serialize(new SteamTokenRequest(claimedSteamId, token));
    }

    public static long? ReadVerifiedSteamId(string body)
    {
        if (!TryRead(body, out var parsed))
        {
            return null;
        }
        return parsed.SteamId;
    }

    public static string ReadStatus(string body)
    {
        if (!TryRead(body, out var parsed))
        {
            return null;
        }
        return parsed.Status;
    }

    private static bool TryRead(string body, out SteamTokenResponse parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(body) || body.Length > 65536)
        {
            return false;
        }
        try
        {
            parsed = JsonSerializer.Deserialize<SteamTokenResponse>(body, ResponseOptions);
            return (object)parsed != null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}

public sealed class SteamTokenHttpVerifier
{
    private readonly ISteamTokenTransport transport;

    public SteamTokenHttpVerifier(ISteamTokenTransport transport)
    {
        this.transport = transport ?? throw new ArgumentNullException("transport");
    }

    public async Task<SteamTokenVerdict> VerifyAsync(Packet hello, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hello?.SteamToken))
        {
            return SteamTokenVerdict.TokenMissing;
        }
        if (!SteamTokenRules.IsPlausibleToken(hello.SteamToken))
        {
            return SteamTokenVerdict.TokenMalformed;
        }
        if (!SteamIdentityRules.TryParse(hello.SteamId, out var identity))
        {
            return SteamTokenVerdict.SteamIdMismatch;
        }
        if (identity.Value == 0)
        {
            return SteamTokenVerdict.SteamIdMismatch;
        }
        if (identity.Value > long.MaxValue)
        {
            return SteamTokenVerdict.SteamIdMismatch;
        }
        SteamTokenHttpResponse response;
        try
        {
            response = await transport.PostAsync("https://services.facepunch.com/sbox/auth/token", SteamTokenEndpoint.BuildRequestBody((long)identity.Value, hello.SteamToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return SteamTokenVerdict.ServiceUnavailable;
        }
        catch (Exception)
        {
            return SteamTokenVerdict.ServiceUnavailable;
        }
        if (!response.Status.HasValue)
        {
            return SteamTokenVerdict.ServiceUnavailable;
        }
        long? verified = null;
        string status = null;
        if (response.Status.Value == 200)
        {
            verified = SteamTokenEndpoint.ReadVerifiedSteamId(response.Body);
            status = SteamTokenEndpoint.ReadStatus(response.Body);
        }
        return SteamTokenRules.Decide((long)identity.Value, hello.SteamToken, response.Status, verified, status);
    }
}
