using System;

namespace TeamCreate;

public sealed record IdleSaveInput(bool Enabled, bool HasUnsavedChanges, bool PeerEditing, bool InPlayMode, DateTime LastLocalEdit, DateTime LastAutoSave, DateTime Now, Func<bool> CanSave);

public static class AutoSaveRules
{
    public const int MinIntervalSeconds = 30;

    public const int IdleSeconds = 30;

    public static bool CheckpointEligible(bool hasUnsavedChanges, Func<bool> canSave)
    {
        if (!hasUnsavedChanges)
        {
            return false;
        }
        return canSave?.Invoke() ?? false;
    }

    public static bool Eligible(IdleSaveInput input)
    {
        if (!input.Enabled)
        {
            return false;
        }
        if (input.PeerEditing || input.InPlayMode)
        {
            return false;
        }
        if (input.LastLocalEdit == default(DateTime))
        {
            return false;
        }
        if (input.Now - input.LastLocalEdit < TimeSpan.FromSeconds(30L))
        {
            return false;
        }
        if (input.LastAutoSave != default(DateTime) && input.Now - input.LastAutoSave < TimeSpan.FromSeconds(30L))
        {
            return false;
        }
        return CheckpointEligible(input.HasUnsavedChanges, input.CanSave);
    }
}
