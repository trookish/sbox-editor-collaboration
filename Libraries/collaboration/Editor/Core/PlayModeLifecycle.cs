#nullable enable

using System;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace TeamCreate;

public enum PlayModeLifecycleState
{
    Source,
    RuntimeDisabled,
    RuntimeStarting,
    RuntimeActive,
    RuntimeBlocked
}

public sealed record PlayModeTransitionResult(bool Accepted, string? ActionableError = null, bool ArchiveMarker = false)
{
    public static PlayModeTransitionResult Rejected(string error)
    {
        return new PlayModeTransitionResult(Accepted: false, error);
    }

    public static PlayModeTransitionResult Ok(bool archiveMarker = false)
    {
        return new PlayModeTransitionResult(Accepted: true, null, archiveMarker);
    }
}

public sealed class PlayModeLifecycle
{
    private const int MaxDiagnosticLength = 512;

    private const int MaxErrorLength = 512;

    public PlayModeLifecycleState State { get; private set; } = PlayModeLifecycleState.Source;

    public bool OptIn { get; private set; }

    public string? SourceStateToken { get; private set; }

    public object? SourceSessionToken { get; private set; }

    public string? SourceScenePath { get; private set; }

    public string? PendingScenePath { get; private set; }

    public string? PendingRunId { get; private set; }

    public object? RuntimeSessionToken { get; private set; }

    public object? RuntimeParentSessionToken { get; private set; }

    public string? RuntimeSourceScenePath { get; private set; }

    public string? RunId { get; private set; }

    public string? ArchiveReason { get; private set; }

    public bool HotloadArchiveRecorded { get; private set; }

    public bool HasTerminalTeardownFailure { get; private set; }

    public string? LastDiagnostic { get; private set; }

    public string? LastActionableError { get; private set; }

    public bool IsTransitionInProgress => State == PlayModeLifecycleState.RuntimeStarting;

    public bool RuntimeCollaborationEnabled => State == PlayModeLifecycleState.RuntimeActive;

    public bool RequiresHotloadRecoveryAfterLeave => State == PlayModeLifecycleState.RuntimeBlocked;

    public PlayModeLifecycle(bool optIn, string? sourceStateToken = null)
    {
        OptIn = optIn;
        SourceStateToken = sourceStateToken;
    }

    public bool HasApprovedCurrentRuntime(RuntimeSessionDescriptor? currentRuntime)
    {
        if (!OptIn || State != PlayModeLifecycleState.RuntimeActive)
        {
            return false;
        }
        if (currentRuntime is null || !currentRuntime.IsValid || currentRuntime.IsDestroyed)
        {
            return false;
        }
        if (currentRuntime.Kind != RuntimeSessionKind.Game || currentRuntime.SessionToken == null)
        {
            return false;
        }
        if (currentRuntime.SessionToken != RuntimeSessionToken)
        {
            return false;
        }
        if (currentRuntime.ParentSessionToken != RuntimeParentSessionToken)
        {
            return false;
        }
        if (currentRuntime.ParentSessionToken != SourceSessionToken)
        {
            return false;
        }
        if (currentRuntime.SourceScenePath == null || RuntimeSourceScenePath == null || SourceScenePath == null)
        {
            return false;
        }
        if (!PathsEqual(currentRuntime.SourceScenePath, RuntimeSourceScenePath))
        {
            return false;
        }
        return PathsEqual(currentRuntime.SourceScenePath, SourceScenePath);
    }

    public PlayModeTransitionResult RecordStartIntent(string? scenePath, object? expectedSourceSession = null, string? expectedSourcePath = null, string? pendingRunId = null)
    {
        if (HasTerminalTeardownFailure)
        {
            return PlayModeTransitionResult.Rejected("A connection teardown failed; this editor session remains fail-stop.");
        }
        if (State == PlayModeLifecycleState.RuntimeBlocked)
        {
            return Reject("Runtime lifecycle is blocked. Leave the collaboration session, recover, then start a new session.");
        }
        if (IsTransitionInProgress)
        {
            return Reject("A play-mode lifecycle transition is already in progress.");
        }
        if (State == PlayModeLifecycleState.RuntimeActive)
        {
            return Reject("A runtime collaboration run is already active. Stop it before starting another.");
        }
        if (!RuntimeStateRules.IsSafeScenePath(expectedSourcePath ?? scenePath))
        {
            return Reject("Play mode requires a saved scene under scenes/.");
        }
        if (pendingRunId != null && !RuntimeStateRules.IsValidRunId(pendingRunId))
        {
            return Reject("Runtime run ID is invalid.");
        }
        SourceSessionToken = expectedSourceSession ?? SourceSessionToken;
        SourceScenePath = CanonicalPath(expectedSourcePath ?? scenePath);
        PendingScenePath = SourceScenePath;
        PendingRunId = pendingRunId;
        RuntimeSessionToken = null;
        RuntimeParentSessionToken = null;
        RuntimeSourceScenePath = null;
        RunId = null;
        ArchiveReason = null;
        HotloadArchiveRecorded = false;
        HasTerminalTeardownFailure = false;
        LastActionableError = null;
        State = PlayModeLifecycleState.RuntimeStarting;
        LastDiagnostic = "Runtime lifecycle start intent recorded; waiting for the GameEditorSession.";
        return PlayModeTransitionResult.Ok();
    }

    public PlayModeTransitionResult AttachRuntime(RuntimeSessionDescriptor descriptor, string? runId = null, object? expectedSourceSession = null, string? expectedSourcePath = null)
    {
        if (HasTerminalTeardownFailure)
        {
            return PlayModeTransitionResult.Rejected("A connection teardown failed; this editor session remains fail-stop.");
        }
        if (State == PlayModeLifecycleState.RuntimeBlocked)
        {
            return Reject("Runtime lifecycle is blocked. Leave the collaboration session, recover, then start a new session.");
        }
        if (State != PlayModeLifecycleState.RuntimeStarting)
        {
            return Reject("A scene.startplay intent is required before a runtime session can attach.");
        }
        if (OptIn && string.IsNullOrWhiteSpace(runId))
        {
            return Reject("Runtime collaboration is enabled, but the runtime run ID is missing.");
        }
        if (runId != null && !RuntimeStateRules.IsValidRunId(runId))
        {
            return Reject("Runtime session run ID is invalid.");
        }
        if (PendingRunId != null && !string.Equals(PendingRunId, runId, StringComparison.Ordinal))
        {
            return Reject("Runtime session run ID does not match the pending lifecycle run ID.");
        }
        try
        {
            object? expectedSourceSession2 = expectedSourceSession ?? SourceSessionToken;
            string? expectedSourcePath2 = expectedSourcePath ?? SourceScenePath;
            RuntimeSessionGuard.EnsureRuntimeSession(descriptor, expectedSourceSession2, expectedSourcePath2);
            if (!PathsEqual(descriptor.SourceScenePath, PendingScenePath))
            {
                throw new InvalidOperationException("Runtime session source path does not match the pending play-mode source path.");
            }
        }
        catch (Exception ex)
        {
            return Reject(ex.Message);
        }
        RuntimeSessionToken = descriptor.SessionToken;
        RuntimeParentSessionToken = descriptor.ParentSessionToken;
        RuntimeSourceScenePath = CanonicalPath(descriptor.SourceScenePath);
        PendingScenePath = null;
        PendingRunId = null;
        LastActionableError = null;
        if (!OptIn)
        {
            RunId = null;
            State = PlayModeLifecycleState.RuntimeDisabled;
            LastDiagnostic = "Play mode detected; experimental runtime collaboration remains disabled by user choice.";
            return PlayModeTransitionResult.Ok();
        }
        RunId = runId;
        State = PlayModeLifecycleState.RuntimeActive;
        LastDiagnostic = "Experimental runtime collaboration lifecycle enabled; transport, sampling, and runtime apply remain unsupported.";
        return PlayModeTransitionResult.Ok();
    }

    public PlayModeTransitionResult RecordStop()
    {
        if (HasTerminalTeardownFailure)
        {
            return PlayModeTransitionResult.Rejected("A connection teardown failed; this editor session remains fail-stop.");
        }
        if (State == PlayModeLifecycleState.RuntimeBlocked)
        {
            return Reject("Runtime lifecycle is blocked; stop cannot discard the preserved transition state.");
        }
        if (State == PlayModeLifecycleState.Source)
        {
            LastDiagnostic = "No active runtime collaboration run; source collaboration remains authoritative.";
            return PlayModeTransitionResult.Ok();
        }
        PlayModeLifecycleState state = State;
        RuntimeSessionToken = null;
        RuntimeParentSessionToken = null;
        RuntimeSourceScenePath = null;
        PendingScenePath = null;
        PendingRunId = null;
        RunId = null;
        ArchiveReason = null;
        HotloadArchiveRecorded = false;
        HasTerminalTeardownFailure = false;
        LastActionableError = null;
        State = PlayModeLifecycleState.Source;
        LastDiagnostic = Bounded($"Runtime collaboration stopped from {state}; returned to source collaboration without changing source scene state.", 512);
        return PlayModeTransitionResult.Ok();
    }

    public PlayModeTransitionResult ValidateSessionStart()
    {
        if (HasTerminalTeardownFailure)
        {
            return PlayModeTransitionResult.Rejected("A connection teardown failed; this editor session remains fail-stop.");
        }
        if (State == PlayModeLifecycleState.RuntimeStarting || State == PlayModeLifecycleState.RuntimeBlocked)
        {
            return PlayModeTransitionResult.Rejected("Stop play mode, complete hotload recovery, and Leave the collaboration session before starting a new session");
        }
        return PlayModeTransitionResult.Ok();
    }

    public PlayModeTransitionResult RecoverFromHotload()
    {
        if (HasTerminalTeardownFailure)
        {
            return PlayModeTransitionResult.Rejected("A connection teardown failed; this editor session remains fail-stop.");
        }
        if (State != PlayModeLifecycleState.RuntimeBlocked)
        {
            return PlayModeTransitionResult.Rejected("Hotload recovery reset requires RuntimeBlocked.");
        }
        if (!HotloadArchiveRecorded)
        {
            return Reject("Hotload recovery reset requires a successfully recorded recovery archive. Leave will retry while RuntimeBlocked.");
        }
        string lastDiagnostic = Bounded("Hotload lifecycle reset to Source; transport recovery and Leave must be completed before a new play session. Complete source recovery, sync source in Git, and start a new collaboration session.", 512);
        State = PlayModeLifecycleState.Source;
        SourceSessionToken = null;
        SourceScenePath = null;
        PendingScenePath = null;
        PendingRunId = null;
        RuntimeSessionToken = null;
        RuntimeParentSessionToken = null;
        RuntimeSourceScenePath = null;
        RunId = null;
        ArchiveReason = null;
        HotloadArchiveRecorded = false;
        LastActionableError = null;
        LastDiagnostic = lastDiagnostic;
        return PlayModeTransitionResult.Ok();
    }

    public void RecordHotloadTeardownFailure(string? error)
    {
        HasTerminalTeardownFailure = true;
        LastActionableError = Bounded(string.IsNullOrWhiteSpace(error) ? "Hotload connection teardown failed; runtime remains blocked." : ("Hotload connection teardown failed; runtime remains blocked: " + error), 512);
        LastDiagnostic = LastActionableError;
    }

    public static string FormatHotloadArchiveFailure(Exception? error)
    {
        string? text = error?.Message;
        return Bounded("Runtime lifecycle blocked, but recovery archive failed: " + ((text != null && text.Length > 0) ? text : "unknown archive error"), 512);
    }

    public void RecordHotloadArchiveFailure(Exception? error)
    {
        HotloadArchiveRecorded = false;
        LastActionableError = FormatHotloadArchiveFailure(error);
        LastDiagnostic = LastActionableError;
    }

    public PlayModeTransitionResult RecordHotloadArchive()
    {
        if (HasTerminalTeardownFailure)
        {
            return PlayModeTransitionResult.Rejected("A connection teardown failed; this editor session remains fail-stop.");
        }
        if (State != PlayModeLifecycleState.RuntimeBlocked)
        {
            return Reject("Hotload recovery archive can only be recorded while RuntimeBlocked.");
        }
        HotloadArchiveRecorded = true;
        LastActionableError = null;
        LastDiagnostic = "Runtime lifecycle recovery archive recorded; transport teardown and Leave must complete before recovery reset.";
        return PlayModeTransitionResult.Ok();
    }

    public PlayModeTransitionResult BlockForHotload(string reason)
    {
        if (HasTerminalTeardownFailure)
        {
            return PlayModeTransitionResult.Rejected("A connection teardown failed; this editor session remains fail-stop.");
        }
        if (State == PlayModeLifecycleState.Source && !IsTransitionInProgress)
        {
            return Reject("There is no runtime lifecycle transition to block.");
        }
        State = PlayModeLifecycleState.RuntimeBlocked;
        RuntimeSessionToken = null;
        RuntimeParentSessionToken = null;
        RuntimeSourceScenePath = null;
        PendingScenePath = null;
        PendingRunId = null;
        RunId = null;
        HotloadArchiveRecorded = false;
        ArchiveReason = Bounded(string.IsNullOrWhiteSpace(reason) ? "hotloaded" : reason, 512);
        LastDiagnostic = "Runtime lifecycle blocked and recovery marker requested before collaboration connections are disposed.";
        return PlayModeTransitionResult.Ok(archiveMarker: true);
    }

    public PlayModeTransitionResult SetOptIn(bool value)
    {
        if (IsTransitionInProgress)
        {
            return Reject("Runtime opt-in cannot change while a lifecycle transition is in progress.");
        }
        if (State == PlayModeLifecycleState.RuntimeActive && value != OptIn)
        {
            return Reject("Stop the current runtime run before changing experimental runtime collaboration opt-in.");
        }
        OptIn = value;
        LastActionableError = null;
        LastDiagnostic = (value ? "Experimental runtime collaboration is explicitly enabled for the next play session." : "Experimental runtime collaboration is explicitly disabled; source collaboration remains the default.");
        return PlayModeTransitionResult.Ok();
    }

    private PlayModeTransitionResult Reject(string error)
    {
        LastActionableError = Bounded(error, 512);
        LastDiagnostic = LastActionableError;
        return PlayModeTransitionResult.Rejected(LastActionableError);
    }

    private static string CanonicalPath(string? path)
    {
        return (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
    }

    private static bool PathsEqual(string? left, string? right)
    {
        return left != null && right != null && string.Equals(CanonicalPath(left), CanonicalPath(right), StringComparison.Ordinal);
    }

    private static string Bounded(string? value, int length)
    {
        return (string.IsNullOrEmpty(value) || value.Length <= length) ? (value ?? string.Empty) : value.Substring(0, length);
    }
}

public enum HotloadTeardownRecoveryStatus
{
    NotRequired,
    ArchiveFailed,
    WaitingForTeardown,
    TerminalTeardownFailure,
    Ready
}

public sealed record HotloadTeardownRecoveryRequest(bool LeaveRequested, bool RequiresHotloadRecovery, bool ArchiveSucceeded, bool HotloadArchiveRecorded, bool TeardownPending, string? TeardownFailure, bool TeardownFailed, bool RecoveryDeferred);

public sealed record HotloadTeardownRecoveryDecision(HotloadTeardownRecoveryStatus Status, bool EnableDeferredRecovery, bool AttemptRecovery, string? Error);

public static class HotloadTeardownRecovery
{
    public static HotloadTeardownRecoveryDecision Decide(HotloadTeardownRecoveryRequest request)
    {
        if (request.TeardownFailed || request.TeardownFailure != null)
        {
            return new HotloadTeardownRecoveryDecision(HotloadTeardownRecoveryStatus.TerminalTeardownFailure, EnableDeferredRecovery: false, AttemptRecovery: false, Bounded((request.RequiresHotloadRecovery ? "Hotload connection teardown failed; runtime remains blocked: " : "Connection teardown failed; this editor session remains offline and fail-stop: ") + (string.IsNullOrWhiteSpace(request.TeardownFailure) ? "unknown teardown error" : request.TeardownFailure), 512));
        }
        if (!request.LeaveRequested)
        {
            return new HotloadTeardownRecoveryDecision(HotloadTeardownRecoveryStatus.NotRequired, EnableDeferredRecovery: false, AttemptRecovery: false, null);
        }
        if (!request.RequiresHotloadRecovery)
        {
            return new HotloadTeardownRecoveryDecision(HotloadTeardownRecoveryStatus.NotRequired, EnableDeferredRecovery: false, AttemptRecovery: false, null);
        }
        if (!request.ArchiveSucceeded || !request.HotloadArchiveRecorded)
        {
            return new HotloadTeardownRecoveryDecision(HotloadTeardownRecoveryStatus.ArchiveFailed, EnableDeferredRecovery: false, AttemptRecovery: false, "Hotload recovery archive has not been recorded; runtime remains blocked.");
        }
        if (request.TeardownPending)
        {
            return new HotloadTeardownRecoveryDecision(HotloadTeardownRecoveryStatus.WaitingForTeardown, EnableDeferredRecovery: false, AttemptRecovery: false, "Runtime remains blocked until pending connection teardown completes.");
        }
        return new HotloadTeardownRecoveryDecision(HotloadTeardownRecoveryStatus.Ready, !request.RecoveryDeferred, AttemptRecovery: true, null);
    }

    private static string Bounded(string? value, int length)
    {
        return (string.IsNullOrEmpty(value) || value.Length <= length) ? (value ?? string.Empty) : value.Substring(0, length);
    }
}

public sealed record HotloadCodeChangeRequest(PlayModeLifecycleState LifecycleState, bool LifecycleBlockAccepted, bool ArchiveWritten, bool TransportActive, string? LifecycleRejectionReason = null);

public sealed record HotloadCodeChangeReport(bool ArchiveClaimed, bool RequiresHotloadRecoveryAfterLeave, bool TeardownTransport, bool SurfaceError, string Message, string? LifecycleRejectionReason);

public static class HotloadCodeChangeRules
{
    public const string ArchivedMessage = "Code hotloaded during collaboration. Recovery is archived before connections close; Leave will finish transport teardown before lifecycle recovery.";

    public const string ArchiveFailedMessage = "Code hotload archive failed; runtime remains blocked and Leave will retry before recovery.";

    public const string NoArchiveNeededMessage = "Code hotloaded during collaboration. Connections were closed; no runtime lifecycle recovery archive was required.";

    public static HotloadCodeChangeReport Decide(HotloadCodeChangeRequest request)
    {
        bool flag = request.LifecycleState == PlayModeLifecycleState.RuntimeBlocked;
        bool flag2 = flag && request.ArchiveWritten;
        string value;
        if (flag2)
        {
            value = "Code hotloaded during collaboration. Recovery is archived before connections close; Leave will finish transport teardown before lifecycle recovery.";
        }
        else
        {
            value = (flag ? "Code hotload archive failed; runtime remains blocked and Leave will retry before recovery." : "Code hotloaded during collaboration. Connections were closed; no runtime lifecycle recovery archive was required.");
        }
        return new HotloadCodeChangeReport(flag2, flag2, TeardownTransport: true, request.TransportActive || (flag && !flag2), CollaborationDiagnostics.Bounded(value), request.LifecycleBlockAccepted ? null : CollaborationDiagnostics.Bounded(request.LifecycleRejectionReason));
    }
}

public sealed record PlayModePendingCandidate(object SourceSessionToken, string SourceScenePath, string RuntimeRunId);

public sealed class ConnectionTeardownGate
{
    private readonly object sync = new object();

    private TaskCompletionSource<bool> completion = Completed();

    private string? failure;

    public bool FailStopTeardown { get; private set; }

    public bool HasTerminalFailure
    {
        get
        {
            lock (sync)
            {
                return FailStopTeardown || failure != null;
            }
        }
    }

    public bool IsPending
    {
        get
        {
            lock (sync)
            {
                return !completion.Task.IsCompleted;
            }
        }
    }

    public string? Failure
    {
        get
        {
            lock (sync)
            {
                return failure;
            }
        }
    }

    public void Begin<T>(Task<T> connecting) where T : IDisposable
    {
        if (connecting == null)
        {
            throw new ArgumentNullException("connecting");
        }
        lock (sync)
        {
            EnsureRetryAllowed();
            completion = NewCompletion();
        }
        _ = CompleteAsync(connecting);
    }

    public void ResetForRetry()
    {
        lock (sync)
        {
            EnsureRetryAllowed();
        }
    }

    public void CompleteWithoutConnection()
    {
        lock (sync)
        {
            EnsureRetryAllowed();
            completion = NewCompletion();
            completion.TrySetResult(result: true);
        }
    }

    public bool TryTakeCompletion(out string? teardownFailure)
    {
        lock (sync)
        {
            teardownFailure = null;
            if (!completion.Task.IsCompleted)
            {
                return false;
            }
            teardownFailure = failure;
            FailStopTeardown = failure != null;
            return true;
        }
    }

    private void EnsureRetryAllowed()
    {
        if (!completion.Task.IsCompleted)
        {
            throw new InvalidOperationException("A connection teardown is already pending.");
        }
        if (FailStopTeardown || failure != null)
        {
            throw new InvalidOperationException("A previous connection teardown failed; failure remains terminal for this editor session.");
        }
    }

    private async Task CompleteAsync<T>(Task<T> connecting) where T : IDisposable
    {
        Exception? disposalError = null;
        try
        {
            T produced = await connecting.ConfigureAwait(continueOnCapturedContext: false);
            try
            {
                produced.Dispose();
            }
            catch (Exception ex)
            {
                Exception error = ex;
                disposalError = error;
            }
        }
        catch (Exception ex)
        {
            Exception error2 = ex;
            disposalError = error2;
        }
        lock (sync)
        {
            failure = ((disposalError == null) ? null : Bounded("Connection teardown failed: " + disposalError.Message, 512));
            FailStopTeardown = failure != null;
            completion.TrySetResult(result: true);
        }
    }

    private static TaskCompletionSource<bool> NewCompletion()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource<bool> Completed()
    {
        TaskCompletionSource<bool> taskCompletionSource = NewCompletion();
        taskCompletionSource.TrySetResult(result: true);
        return taskCompletionSource;
    }

    private static string Bounded(string? value, int length)
    {
        return (string.IsNullOrEmpty(value) || value.Length <= length) ? (value ?? string.Empty) : value.Substring(0, length);
    }
}

public sealed class PlayModeLifecycleEventState
{
    private PlayModePendingCandidate? pending;

    public PlayModePendingCandidate? Pending => pending;

    public PlayModeTransitionResult RecoverFromHotload(PlayModeLifecycle lifecycle)
    {
        PlayModeTransitionResult playModeTransitionResult = lifecycle.RecoverFromHotload();
        if (playModeTransitionResult.Accepted)
        {
            pending = null;
        }
        return playModeTransitionResult;
    }

    public PlayModeTransitionResult AttachRuntime(PlayModeLifecycle lifecycle, RuntimeSessionDescriptor descriptor, string runId)
    {
        PlayModePendingCandidate? playModePendingCandidate = pending;
        if (playModePendingCandidate is null)
        {
            return lifecycle.AttachRuntime(descriptor, runId);
        }
        PlayModeTransitionResult playModeTransitionResult = lifecycle.AttachRuntime(descriptor, runId, playModePendingCandidate.SourceSessionToken, playModePendingCandidate.SourceScenePath);
        if (playModeTransitionResult.Accepted)
        {
            pending = null;
        }
        return playModeTransitionResult;
    }

    public PlayModeTransitionResult RecordStop(PlayModeLifecycle lifecycle)
    {
        PlayModeTransitionResult playModeTransitionResult = lifecycle.RecordStop();
        if (playModeTransitionResult.Accepted)
        {
            pending = null;
        }
        return playModeTransitionResult;
    }

    public PlayModeTransitionResult RecordStartIntent(PlayModeLifecycle lifecycle, object sourceSession, string sourceScenePath, string runId)
    {
        PlayModePendingCandidate playModePendingCandidate = new PlayModePendingCandidate(sourceSession, sourceScenePath, runId);
        PlayModeTransitionResult playModeTransitionResult = lifecycle.RecordStartIntent(sourceScenePath, sourceSession, sourceScenePath, runId);
        if (playModeTransitionResult.Accepted)
        {
            pending = playModePendingCandidate;
        }
        return playModeTransitionResult;
    }
}

public sealed record ScenePacketRouteResult(bool Admitted, string? CanonicalSceneKey, string? TargetScenePath, string? Error);

public static class ScenePacketRouting
{
    public static string? CanonicalSceneKey(string? scene)
    {
        return (!string.IsNullOrWhiteSpace(scene) && PlayModeSourceCoexistence.IsUsablePathKey(scene)) ? scene.Trim().Replace('\\', '/').ToLowerInvariant() : null;
    }

    public static bool TryResolveTracked(string? packetScene, IReadOnlyDictionary<string, object> trackedByScene, out string? canonical, out object? target)
    {
        canonical = CanonicalSceneKey(packetScene);
        target = null;
        if (canonical == null)
        {
            return false;
        }
        if (!trackedByScene.TryGetValue(canonical, out target))
        {
            foreach (KeyValuePair<string, object> item in trackedByScene)
            {
                if (CanonicalSceneKey(item.Key) != canonical)
                {
                    continue;
                }
                target = item.Value;
                break;
            }
        }
        if (target == null)
        {
            target = null;
            return false;
        }
        return true;
    }

    public static ScenePacketRouteResult RouteTrackedPacket(string packetKind, string? packetScene, IReadOnlyDictionary<string, object> trackedByScene, Func<object, bool> isPlaying, Func<object, string?> currentTargetPath, PlayModeLifecycle lifecycle, Func<object, RuntimeSessionDescriptor?> currentRuntime)
    {
        if ((!(packetKind == "snapshot") && !(packetKind == "accepted")) || 1 == 0)
        {
            return Reject("Only snapshot and accepted packets use tracked-scene routing.");
        }
        string? text = CanonicalSceneKey(packetScene);
        if (text == null)
        {
            return Reject("A snapshot or accepted packet has a missing or invalid scene key.");
        }
        if (!TryResolveTracked(packetScene, trackedByScene, out string? _, out object? target))
        {
            return Reject("No current tracked target exists for scene " + text + ".");
        }
        if (target == null)
        {
            return Reject("No current tracked target exists for scene " + text + ".");
        }
        string? text2 = currentTargetPath(target);
        if (!PlayModeSourceCoexistence.CanAdmitTrackedScenePacket(packetKind, isPlaying(target), target, text2, lifecycle, currentRuntime(target), text))
        {
            return Reject("The tracked target's current scene path does not admit packet " + text + ".");
        }
        return new ScenePacketRouteResult(Admitted: true, text, text2, null);
    }

    private static ScenePacketRouteResult Reject(string error)
    {
        return new ScenePacketRouteResult(Admitted: false, null, null, CollaborationDiagnostics.Bounded(error));
    }
}

public static class PlayModeSourceCoexistence
{
    public static bool CanProcessSourcePackets(bool isPlaying, object? sourceSession, string? sourceScenePath, PlayModeLifecycle lifecycle, RuntimeSessionDescriptor? currentRuntime = null)
    {
        return CanAdmitTrackedSceneState(isPlaying, sourceSession, sourceScenePath, lifecycle, currentRuntime, sourceScenePath);
    }

    public static bool CanAdmitTrackedScenePacket(string packetKind, bool isPlaying, object? sourceSession, string? sourceScenePath, PlayModeLifecycle lifecycle, RuntimeSessionDescriptor? currentRuntime, string? packetScenePath)
    {
        bool flag = ((packetKind == "snapshot" || packetKind == "accepted") ? true : false);
        return flag && CanAdmitTrackedSceneState(isPlaying, sourceSession, sourceScenePath, lifecycle, currentRuntime, packetScenePath);
    }

    private static bool CanAdmitTrackedSceneState(bool isPlaying, object? sourceSession, string? sourceScenePath, PlayModeLifecycle lifecycle, RuntimeSessionDescriptor? currentRuntime, string? packetScenePath)
    {
        if (lifecycle == null)
        {
            return false;
        }
        PlayModeLifecycleState state = lifecycle.State;
        if ((state == PlayModeLifecycleState.RuntimeStarting || state == PlayModeLifecycleState.RuntimeBlocked) ? true : false)
        {
            return false;
        }
        if (!IsUsablePathKey(sourceScenePath))
        {
            return false;
        }
        if (!IsUsablePathKey(packetScenePath) || !PathsEqual(packetScenePath, sourceScenePath))
        {
            return false;
        }
        if (!isPlaying)
        {
            return true;
        }
        if (sourceSession != null && sourceScenePath != null && CanSampleAuthoringSource(sourceSession, sourceScenePath, lifecycle, currentRuntime))
        {
            return true;
        }
        return false;
    }

    public static bool IsUsablePathKey(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && CanonicalPath(path).Length > 0 && RuntimeStateRules.IsSafeScenePath(CanonicalPath(path));
    }

    public static bool CanSampleAuthoringSource(object sourceSession, string sourceScenePath, PlayModeLifecycle lifecycle, RuntimeSessionDescriptor? currentRuntime = null)
    {
        return lifecycle != null && lifecycle.OptIn && lifecycle.State == PlayModeLifecycleState.RuntimeActive && lifecycle.SourceSessionToken != null && lifecycle.SourceSessionToken == sourceSession && IsUsablePathKey(sourceScenePath) && PathsEqual(sourceScenePath, lifecycle.SourceScenePath) && currentRuntime is not null && lifecycle.HasApprovedCurrentRuntime(currentRuntime);
    }

    private static string CanonicalPath(string path)
    {
        return path.Replace('\\', '/').Trim().ToLowerInvariant();
    }

    private static bool PathsEqual(string? left, string? right)
    {
        return left != null && right != null && IsUsablePathKey(left) && IsUsablePathKey(right) && string.Equals(CanonicalPath(left), CanonicalPath(right), StringComparison.OrdinalIgnoreCase);
    }
}
