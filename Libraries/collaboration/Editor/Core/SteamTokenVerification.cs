using System;

namespace TeamCreate;

public enum SteamTokenVerdict
{
    Verified,
    TokenMissing,
    TokenMalformed,
    SteamIdMismatch,
    ServiceUnavailable,
    ServiceRejected
}

public static class SteamTokenRules
{
    public const int MaxTokenLength = 4096;

    public static bool IsPlausibleToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096)
        {
            return false;
        }
        foreach (char c in token)
        {
            if (c < '!' || c > '~')
            {
                return false;
            }
        }
        return true;
    }

    public static SteamTokenVerdict Decide(long claimedSteamId, string token, int? httpStatus, long? verifiedSteamId, string serviceStatus)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return SteamTokenVerdict.TokenMissing;
        }
        if (!IsPlausibleToken(token))
        {
            return SteamTokenVerdict.TokenMalformed;
        }
        if (!httpStatus.HasValue)
        {
            return SteamTokenVerdict.ServiceUnavailable;
        }
        if (httpStatus != 200)
        {
            return SteamTokenVerdict.ServiceRejected;
        }
        if (!string.Equals(serviceStatus, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return SteamTokenVerdict.ServiceRejected;
        }
        if (!verifiedSteamId.HasValue || verifiedSteamId.Value != claimedSteamId)
        {
            return SteamTokenVerdict.SteamIdMismatch;
        }
        if (claimedSteamId <= 0)
        {
            return SteamTokenVerdict.SteamIdMismatch;
        }
        return SteamTokenVerdict.Verified;
    }
}
