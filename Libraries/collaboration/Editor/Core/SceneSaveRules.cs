using System;

namespace TeamCreate;

public enum SceneSaveOutcome
{
    Ignore,
    Absorb,
    Warn
}

public static class SceneSaveRules
{
    public static readonly TimeSpan ExternalChangeGrace = TimeSpan.FromMilliseconds(250L);

    public static SceneSaveOutcome Decide(bool ownSave, bool sessionLive, bool sceneTracked, bool saveGuardHolds)
    {
        if (ownSave || !sessionLive || !sceneTracked)
        {
            return SceneSaveOutcome.Ignore;
        }
        return saveGuardHolds ? SceneSaveOutcome.Absorb : SceneSaveOutcome.Warn;
    }

    /// <summary>
    /// Whether a save is the one this session made: one made while it was saving, or a file with the time it last wrote and the same
    /// content. Another program writing within the same second leaves the time alone but not the content. Without a digest on either
    /// side the time decides.
    /// </summary>
    public static bool IsOwnSave(bool saving, DateTime knownStamp, DateTime diskStamp, string knownSha256, string diskSha256)
    {
        if (saving)
        {
            return true;
        }
        if (knownStamp != diskStamp)
        {
            return false;
        }
        return knownSha256 == null || diskSha256 == null || string.Equals(knownSha256, diskSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A save that may differ from the session is copied to the recovery folder before the session takes its time as its own.</summary>
    public static bool KeepsDiskCopy(SceneSaveOutcome outcome)
    {
        return outcome == SceneSaveOutcome.Warn;
    }

    public static bool ShouldFailOnDiskStamp(bool stampMatches, TimeSpan mismatchFor)
    {
        return !stampMatches && mismatchFor >= ExternalChangeGrace;
    }
}
