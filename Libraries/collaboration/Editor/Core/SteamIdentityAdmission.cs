using System;
using System.Collections.Generic;
using System.Linq;

namespace TeamCreate;

public static class SteamIdentityAdmission
{
    public const string TokenRejectedMessage = "This peer's Steam identity token was rejected.";

    /// <summary>The refusal of an account the host still holds a connection for. It passes once that connection times out.</summary>
    public const string DuplicateAccountError = "That Steam account is already connected to this session.";

    public static string GetDuplicateError(string joiningSteamId, IEnumerable<string> existingSteamIds)
    {
        if (!SteamIdentityRules.TryParse(joiningSteamId, out var identity))
        {
            return "Invalid Steam account ID.";
        }
        foreach (string item in existingSteamIds ?? Enumerable.Empty<string>())
        {
            if (!SteamIdentityRules.TryParse(item, out var identity2) || !string.Equals(SteamIdentityRules.Canonical(identity2), SteamIdentityRules.Canonical(identity), StringComparison.Ordinal))
            {
                continue;
            }
            return DuplicateAccountError;
        }
        return null;
    }

    public static bool IsTokenRejection(string message)
    {
        return string.Equals(message, "This peer's Steam identity token was rejected.", StringComparison.Ordinal);
    }

    public static string GetVerificationError(SteamTokenVerdict verdict, bool allowUnverified)
    {
        if (1 == 0)
        {
        }
        string result = verdict switch
        {
            SteamTokenVerdict.Verified => null, 
            SteamTokenVerdict.ServiceUnavailable => (!allowUnverified) ? "The Steam identity service could not be reached, so this peer could not be verified. Enable \"Allow unverified peers\" to admit them anyway." : null, 
            SteamTokenVerdict.TokenMissing => "This peer did not provide a Steam identity token.", 
            SteamTokenVerdict.TokenMalformed => "This peer provided a malformed Steam identity token.", 
            SteamTokenVerdict.SteamIdMismatch => "This peer's Steam identity could not be confirmed.", 
            SteamTokenVerdict.ServiceRejected => "This peer's Steam identity token was rejected.", 
            _ => "This peer's Steam identity could not be verified.", 
        };
        if (1 == 0)
        {
        }
        return result;
    }
}
