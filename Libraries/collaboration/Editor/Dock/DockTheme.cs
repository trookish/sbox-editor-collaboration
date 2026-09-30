using System;
using Editor;
using Sandbox;

namespace TeamCreate;

public static class DockTheme
{
    public const float Margin = 12f;

    public const float Spacing = 8f;

    public const float RowSpacing = 4f;

    public const float CardPadding = 8f;

    public const int AvatarSize = 32;

    public const float AvatarDotRadius = 4f;

    public const float MemberRowHeight = 40f;

    public const float HeaderHeight = 30f;

    public const int RosterRowCap = 50;

    public const float NameplateNearDistance = 600f;

    public const float NameplateFarDistance = 1500f;

    public const float NameplateAvatarSize = 7f;

    public const float NameplateTextSize = 16f;

    public const float NameplateStatusSize = 12f;

    public const float NameplateAvatarHeight = 20f;

    public const float NameplateNameHeight = 10f;

    public const float NameplateStatusHeight = -14f;

    public const float PeerFrustumNear = 8f;

    public const float PeerFrustumFar = 300f;

    public const float PeerFrustumAspect = 1.7777778f;

    public const float FocusFlashSeconds = 1f;

    public const float FocusNoticeSeconds = 5f;

    public const float SaveNoticeSeconds = 8f;

    public const float UndoNoticeSeconds = 8f;

    public const float ActionNoticeSeconds = 8f;

    public const float FocusButtonWidth = 26f;

    public static float RowHeight => Theme.RowHeight;

    public static Color OkColor => Theme.Green;

    public static Color WarningColor => Theme.Yellow;

    public static Color BlockedColor => Theme.Red;

    public static Color IdleColor => Theme.Text.WithAlpha(0.55f);

    public static Color AccentColor => Theme.Primary;

    public static Color MutedColor => Theme.Text.WithAlpha(0.6f);

    public static Color CardColor => Theme.ControlBackground;

    public static Color StatusColor(ReadinessLevel level)
    {
        if (1 == 0)
        {
        }
        Color result = level switch
        {
            ReadinessLevel.Ok => OkColor, 
            ReadinessLevel.Warning => WarningColor, 
            _ => BlockedColor, 
        };
        if (1 == 0)
        {
        }
        return result;
    }

    public static Color StateColor(SessionState state)
    {
        if (1 == 0)
        {
        }
        Color result = state switch
        {
            SessionState.Ready => OkColor, 
            SessionState.Hosting => AccentColor, 
            SessionState.Joining => AccentColor, 
            SessionState.Connected => OkColor, 
            SessionState.Degraded => BlockedColor, 
            SessionState.Recovering => WarningColor, 
            _ => IdleColor, 
        };
        if (1 == 0)
        {
        }
        return result;
    }

    public static string StateVerb(SessionState state)
    {
        if (1 == 0)
        {
        }
        string result = state switch
        {
            SessionState.Ready => "Ready", 
            SessionState.Hosting => "Hosting", 
            SessionState.Joining => "Joining", 
            SessionState.Connected => "Connected", 
            SessionState.Degraded => "Degraded", 
            SessionState.Recovering => "Recovering", 
            _ => "Offline", 
        };
        if (1 == 0)
        {
        }
        return result;
    }

    public static string StateIcon(SessionState state)
    {
        if (1 == 0)
        {
        }
        string result = state switch
        {
            SessionState.Ready => "check_circle", 
            SessionState.Hosting => "lan", 
            SessionState.Joining => "sync", 
            SessionState.Connected => "wifi", 
            SessionState.Degraded => "warning", 
            SessionState.Recovering => "settings_backup_restore", 
            _ => "power_settings_new", 
        };
        if (1 == 0)
        {
        }
        return result;
    }

    public static string LevelIcon(ReadinessLevel level)
    {
        if (1 == 0)
        {
        }
        string result = level switch
        {
            ReadinessLevel.Ok => "check_circle", 
            ReadinessLevel.Warning => "warning", 
            _ => "block", 
        };
        if (1 == 0)
        {
        }
        return result;
    }

    public static string ReadinessIcon(string key)
    {
        if (1 == 0)
        {
        }
        string result = key switch
        {
            "steam" => "person", 
            "scene" => "movie", 
            "published" => "cloud_done", 
            "library" => "extension", 
            "org" => "groups", 
            _ => "radio_button_unchecked", 
        };
        if (1 == 0)
        {
        }
        return result;
    }

    public static Color PeerColor(string identity)
    {
        var (b, b2, b3) = PeerBytes(identity);
        return new Color(0.3f + (float)(int)b / 365f, 0.3f + (float)(int)b2 / 365f, 0.3f + (float)(int)b3 / 365f);
    }

    private static (byte, byte, byte) PeerBytes(string identity)
    {
        if (Guid.TryParse(identity, out var result))
        {
            byte[] array = result.ToByteArray();
            return (array[0], array[1], array[2]);
        }
        uint num = 2166136261u;
        string text = identity ?? string.Empty;
        foreach (char c in text)
        {
            num ^= c;
            num *= 16777619;
        }
        return ((byte)num, (byte)(num >> 8), (byte)(num >> 16));
    }

    public static float NameplateScale(float distance)
    {
        if (distance <= 600f)
        {
            return 1f;
        }
        if (distance >= 1500f)
        {
            return 0.5f;
        }
        return 1f - 0.5f * (distance - 600f) / 900f;
    }

    public static Color NameplateColor(Color peer, float scale)
    {
        return Color.Lerp(in peer, MutedColor, 1f - scale);
    }

    public static void InvalidateLayout(Widget owner)
    {
        if (owner == null || owner.Layout == null)
        {
            return;
        }
        foreach (Widget child in owner.Children)
        {
            child?.UpdateGeometry();
        }
    }

    public static void SetText(Label label, string text)
    {
        if (label != null)
        {
            string text2 = text ?? string.Empty;
            if (!string.Equals(label.Text, text2, StringComparison.Ordinal))
            {
                label.Text = text2;
                label.UpdateGeometry();
            }
        }
    }

    public static void SetText(Button button, string text)
    {
        if (button != null)
        {
            string text2 = text ?? string.Empty;
            if (!string.Equals(button.Text, text2, StringComparison.Ordinal))
            {
                button.Text = text2;
            }
        }
    }

    public static void OutlineField(LineEdit field)
    {
        if (field != null)
        {
            string value = "1px solid " + Theme.Border.Hex;
            string value2 = $"{Theme.ControlRadius}px";
            field.SetStyles($"LineEdit {{ border: {value}; border-radius: {value2}; }}\nborder: {value}; border-radius: {value2};");
        }
    }

    public static void DrawAvatar(Rect rect, string steamId, string name, bool inSession, bool online, bool notFriend)
    {
        Vector2 position = new Vector2(rect.Left + rect.Width * 0.5f, rect.Top + rect.Height * 0.5f);
        float num = MathF.Min(rect.Width, rect.Height) * 0.5f;
        Pixmap pixmap = AvatarCache.Get(steamId);
        if (pixmap != null)
        {
            Paint.SetBrushAndPen(Theme.ControlBackground, Color.Transparent, 0f);
            Paint.Draw(rect, pixmap, 1f, num);
        }
        else
        {
            Paint.SetBrushAndPen(PeerColor(steamId).WithAlpha(0.55f), Color.Transparent, 0f);
            Paint.DrawCircle(position, num);
            Paint.SetPen(Theme.Text.WithAlpha(0.9f), 0f, PenStyle.Solid);
            Paint.DrawText(in rect, Initials(name));
        }
        Paint.SetBrushAndPen(Color.Transparent, Theme.Border);
        Paint.DrawRect(in rect, num);
        if (inSession)
        {
            Paint.SetPen(PeerColor(steamId), 2f, PenStyle.Solid);
            Paint.DrawCircle(position, num + 1f);
        }
        DrawPresenceDot(rect, steamId, inSession, online, notFriend);
    }

    private static void DrawPresenceDot(Rect rect, string steamId, bool inSession, bool online, bool notFriend)
    {
        Color brushColor;
        if (notFriend)
        {
            brushColor = WarningColor;
        }
        else if (inSession)
        {
            brushColor = PeerColor(steamId);
        }
        else
        {
            brushColor = (online ? OkColor : IdleColor);
        }
        Vector2 position = new Vector2(rect.Left + 2f, rect.Top + rect.Height * 0.75f);
        Paint.SetBrushAndPen(CardColor, Color.Transparent, 0f);
        Paint.DrawCircle(position, 5.5);
        Paint.SetBrushAndPen(brushColor, Color.Transparent, 0f);
        Paint.DrawCircle(position, 4.0);
    }

    private static string Initials(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }
        string text = string.Empty;
        bool flag = true;
        foreach (char c in name)
        {
            if (!char.IsLetter(c))
            {
                flag = true;
            }
            else if (flag)
            {
                flag = false;
                text += char.ToUpperInvariant(c);
                if (text.Length >= 2)
                {
                    break;
                }
            }
        }
        return (text.Length == 0) ? "?" : text;
    }
}
