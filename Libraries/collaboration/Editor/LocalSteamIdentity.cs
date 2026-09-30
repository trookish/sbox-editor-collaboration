using System;
using Sandbox;
using Sandbox.Utility;

namespace TeamCreate;

public static class LocalSteamIdentity
{
    public const string ServiceName = "sbox-editor-collaboration";

    public static bool TryGet(out SteamIdentity identity, out string personaName, out string failure)
    {
        identity = default;
        personaName = null;
        try
        {
            SteamId steamId = Steam.SteamId;
            if (!SteamIdentityRules.IsValid(steamId.ValueUnsigned))
            {
                failure = "No Steam account is signed in. Sign in to Steam in the s&box editor before collaborating.";
                return false;
            }
            identity = new SteamIdentity(steamId.ValueUnsigned);
            personaName = Steam.PersonaName;
            failure = null;
            return true;
        }
        catch (Exception error)
        {
            identity = default;
            personaName = null;
            failure = "The signed-in Steam account could not be read: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("", error);
            return false;
        }
    }
}
