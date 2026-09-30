using System.Globalization;

namespace TeamCreate;

public readonly record struct SteamIdentity(ulong Value)
{
    public bool IsValid => Value != 0;

    public const int MaxLength = 20;

    public override string ToString()
    {
        return Value.ToString(CultureInfo.InvariantCulture);
    }
}

public static class SteamIdentityRules
{
    public const int MaxLength = 20;

    public static bool IsValid(ulong value)
    {
        return value != 0;
    }

    public static bool TryParse(string text, out SteamIdentity identity)
    {
        identity = default;
        if (string.IsNullOrEmpty(text) || text.Length > 20)
        {
            return false;
        }
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
        {
            return false;
        }
        if (!IsValid(result))
        {
            return false;
        }
        identity = new SteamIdentity(result);
        return true;
    }

    public static string Canonical(SteamIdentity identity)
    {
        return identity.ToString();
    }

    public static string DisplayLabel(SteamIdentity identity)
    {
        return identity.Value.ToString("X16", CultureInfo.InvariantCulture);
    }
}
