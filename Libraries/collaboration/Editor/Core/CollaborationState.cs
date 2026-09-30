#nullable enable

using System;

namespace TeamCreate;

public sealed class CollaborationStateModel
{
    public CollaborationMode Mode { get; private set; } = CollaborationMode.Source;

    public string? RunId { get; private set; }

    public string? ScenePath { get; private set; }

    public RuntimeEnableResult EnableRuntime(string runId, string scenePath)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return RuntimeEnableResult.InvalidRunId;
        }
        if (string.IsNullOrWhiteSpace(scenePath) || !scenePath.StartsWith("scenes/", StringComparison.Ordinal) || scenePath.Contains("..", StringComparison.Ordinal))
        {
            return RuntimeEnableResult.InvalidScenePath;
        }
        if (Mode == CollaborationMode.RuntimeExperimental)
        {
            return (runId == RunId && scenePath == ScenePath) ? RuntimeEnableResult.AlreadyEnabled : RuntimeEnableResult.RunAlreadyActive;
        }
        RunId = runId;
        ScenePath = scenePath;
        Mode = CollaborationMode.RuntimeExperimental;
        return RuntimeEnableResult.Ok;
    }

    public void DisableRuntime()
    {
        Mode = CollaborationMode.Source;
        RunId = null;
        ScenePath = null;
    }
}
