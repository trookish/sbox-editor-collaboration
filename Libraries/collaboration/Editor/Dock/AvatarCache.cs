using System;
using System.Collections.Generic;
using Editor;
using Sandbox;
using Sandbox.Internal;

namespace TeamCreate;

public static class AvatarCache
{
    private sealed class Entry
    {
        public Texture Texture;

        public Pixmap Pixmap;

        public bool Failed;

        public int Conversions;

        public long RequestedAt;
    }

    private const int TextureSize = 64;

    private const int MaxWaitFrames = 600;

    private static readonly int[] ConvertAt = new int[3] { 3, 75, 225 };

    private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

    private static readonly List<string> pending = new List<string>();

    private static long frame;

    private static int cursor;

    private static readonly TimeSpan BudgetWindow = TimeSpan.FromMilliseconds(4L);

    private static DateTime lastSpend = DateTime.MinValue;

    public static Pixmap Get(string steamId)
    {
        Entry entry = EntryFor(steamId);
        return (entry == null || entry.Failed) ? null : entry.Pixmap;
    }

    /// <summary>Set only by a solo test copy: the reserved test identity shows this real account's picture. Null everywhere else.</summary>
    public static string SyntheticAvatarAlias { get; set; }

    public static Texture GetTexture(string steamId)
    {
        Entry entry = EntryFor(steamId);
        return (entry == null || entry.Failed) ? null : entry.Texture;
    }

    private static Entry EntryFor(string steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId))
        {
            return null;
        }
        string text = steamId.Trim();
        if (SyntheticAvatarAlias != null && TestHarnessRules.IsSyntheticIdentity(text))
        {
            text = SyntheticAvatarAlias;
        }
        if (!entries.TryGetValue(text, out var value))
        {
            value = new Entry
            {
                RequestedAt = frame
            };
            entries[text] = value;
            pending.Add(text);
        }
        return value;
    }

    public static void Poll()
    {
        DateTime utcNow = DateTime.UtcNow;
        if (utcNow - lastSpend < BudgetWindow)
        {
            return;
        }
        lastSpend = utcNow;
        frame++;
        int num = 2;
        while (num > 0 && pending.Count > 0)
        {
            if (cursor >= pending.Count)
            {
                cursor = 0;
            }
            num--;
            if (Advance(pending[cursor]))
            {
                pending.RemoveAt(cursor);
            }
            else
            {
                cursor++;
            }
        }
    }

    public static void Clear()
    {
        entries.Clear();
        pending.Clear();
        cursor = 0;
    }

    private static bool Advance(string key)
    {
        if (!entries.TryGetValue(key, out var value))
        {
            return true;
        }
        if (value.Failed)
        {
            return true;
        }
        long num = frame - value.RequestedAt;
        try
        {
            if (value.Texture == null)
            {
                if (!long.TryParse(key, out var result) || result <= 0)
                {
                    return Fail(value);
                }
                value.Texture = Texture.LoadAvatar(key, 64);
            }
            if (value.Conversions >= ConvertAt.Length)
            {
                return true;
            }
            if (num < ConvertAt[value.Conversions])
            {
                return false;
            }
            if (!value.Texture.IsLoaded)
            {
                return num > 600 && Fail(value, null, log: true);
            }
            Pixmap pixmap = Pixmap.FromTexture(value.Texture);
            if (pixmap == null || pixmap.Width <= 0 || pixmap.Height <= 0)
            {
                return num > 600 && Fail(value, null, log: true);
            }
            value.Pixmap = pixmap;
            value.Conversions++;
            return value.Conversions >= ConvertAt.Length;
        }
        catch (Exception ex)
        {
            return Fail(value, CollaborationDiagnostics.Bounded(ex.Message), log: true);
        }
    }

    private static bool Fail(Entry entry, string reason = null, bool log = false)
    {
        entry.Failed = true;
        entry.Pixmap = null;
        entry.Texture = null;
        if (log)
        {
            GlobalGameNamespace.Log.Error("Collaboration avatars: " + CollaborationDiagnostics.Bounded(reason ?? "The avatar texture could not be loaded."));
        }
        return true;
    }
}
