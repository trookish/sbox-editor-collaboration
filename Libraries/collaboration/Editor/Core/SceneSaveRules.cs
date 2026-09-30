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

    public static bool ShouldFailOnDiskStamp(bool stampMatches, TimeSpan mismatchFor)
    {
        return !stampMatches && mismatchFor >= ExternalChangeGrace;
    }
}
