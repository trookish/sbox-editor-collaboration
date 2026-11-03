#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace TeamCreate;

public static class CollaborationDiagnostics
{
    public const int MaxDiagnosticLength = 512;

    public static string Bounded(string? value)
    {
        return Bounded(value, 512);
    }

    public static string Bounded(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        if (maxLength < 0)
        {
            maxLength = 0;
        }
        return (value.Length <= maxLength) ? value : value.Substring(0, maxLength);
    }

    public static string DescribeExternalFailure(string context, Exception? error)
    {
        string? text = error?.Message;
        return Bounded(context + ((text != null && text.Length > 0) ? text : "unknown error"));
    }
}

public sealed record CollaborationFailureOutcome(string? Error, string? Status);

public static class CollaborationFailureRules
{
    /// <summary>
    /// Whether a failure should write a recovery archive. The archive exists to keep local work that a
    /// reconnect might discard; a join that never got a welcome never touched a scene, so there is nothing
    /// to keep, and an archive per refused attempt only buries the ones that matter.
    /// </summary>
    public static bool ShouldArchive(bool everConnected, bool hasPendingEdits)
    {
        return everConnected || hasPendingEdits;
    }

    // A join that never reached the host has nothing that "stopped": no scene was ever synchronized.
    // Saying so would send a person looking for lost work that does not exist.
    public static CollaborationFailureOutcome Compose(string? existingError, string error, string? recoveryWriteFailure, bool everConnected = true)
    {
        if (!string.IsNullOrEmpty(existingError))
        {
            return new CollaborationFailureOutcome(existingError, null);
        }
        string text = CollaborationDiagnostics.Bounded(error);
        if (!string.IsNullOrEmpty(recoveryWriteFailure))
        {
            text = CollaborationDiagnostics.Bounded(text + " Recovery write also failed: " + recoveryWriteFailure);
        }
        return new CollaborationFailureOutcome(text, CollaborationDiagnostics.Bounded((everConnected ? "Synchronization stopped: " : "Could not connect: ") + text));
    }
}

public sealed record ResourceDisposalResult(bool Succeeded, string? Failure);

public static class ResourceDisposalRules
{
    public static ResourceDisposalResult DisposePair(IDisposable? first, string firstName, IDisposable? second, string secondName)
    {
        List<string> list = new List<string>();
        DisposeOne(first, firstName, list);
        DisposeOne(second, secondName, list);
        if (list.Count == 0)
        {
            return new ResourceDisposalResult(Succeeded: true, null);
        }
        return new ResourceDisposalResult(Succeeded: false, CollaborationDiagnostics.Bounded(string.Join(" | ", list)));
    }

    private static void DisposeOne(IDisposable? resource, string what, ICollection<string> failures)
    {
        if (resource == null)
        {
            return;
        }
        try
        {
            resource.Dispose();
        }
        catch (Exception error)
        {
            failures.Add(CollaborationDiagnostics.Bounded(CollaborationDiagnostics.DescribeExternalFailure(what + " could not be disposed: ", error), 256));
        }
    }
}

public sealed record SessionStartGuardRequest(PlayModeTransitionResult LifecycleValidation, bool ConnectionBusy, bool ConnectionTeardownPending, bool ConnectionTeardownFailed, string? TerminalTeardownDiagnostic);

public sealed record SessionStartCommitRequest(PlayModeTransitionResult Validation, Func<bool> Prepare);

public enum CollaborationTransportState
{
    None,
    Connecting,
    Live,
    Failed,
    Disconnected
}

public static class CollaborationTransportStateRules
{
    public static CollaborationTransportState Detect(bool attached, bool live, bool failed, bool connecting)
    {
        if (connecting)
        {
            return CollaborationTransportState.Connecting;
        }
        if (failed)
        {
            return CollaborationTransportState.Failed;
        }
        if (!attached)
        {
            return CollaborationTransportState.None;
        }
        return live ? CollaborationTransportState.Live : CollaborationTransportState.Disconnected;
    }
}

public sealed record SessionReconnectGuardRequest(PlayModeLifecycle Lifecycle, bool SessionEstablished, bool ConnectionAttemptPending, bool ConnectionBusy, bool ConnectionTeardownPending = false, bool ConnectionTeardownFailed = false, string? TerminalTeardownDiagnostic = null, bool ConnectionLive = false, CollaborationTransportState? TransportState = null, bool SourceBaselineMatches = true, bool SessionLeft = false, bool HostedSession = false);

public enum SessionFailureClass
{
    Terminal,
    Transient
}

public sealed record AutoRecoveryInput(bool AutoRecoverEnabled, bool ErrorRecorded, SessionFailureClass FailureClass, bool TerminalTeardown, bool HostedSession, bool UnsavedChanges, bool ReconnectAccepted, bool AttemptInFlight, int AttemptsUsed, DateTime Now, DateTime NextAttemptAt);

public static class AutoRecoveryRules
{
    /// <summary>A guest's dropped session reconnects by itself unless the person turned that off.</summary>
    public const bool EnabledByDefault = true;

    // Quick at first, for a blip in the network, then settling at every thirty seconds, for a host that is restarting its editor.
    private static readonly int[] BackoffSeconds = { 1, 2, 4, 8, 15, 30 };

    /// <summary>
    /// Whether retrying can help. A dropped connection, a timeout and a relay that is momentarily without the host, full or limiting can
    /// all pass; a key that does not match (which is also what a tampered frame looks like), a peer that does not speak the protocol and
    /// a relay refusal of the code, the key or the version cannot, and are never retried.
    /// </summary>
    public static SessionFailureClass Classify(Exception error)
    {
        if (error == null)
        {
            return SessionFailureClass.Terminal;
        }
        if (error is AggregateException ex)
        {
            Exception? innerException = ex.InnerException;
            return (innerException != null) ? Classify(innerException) : SessionFailureClass.Terminal;
        }
        if (error is RelayRefusedException refused)
        {
            return RelayProtocol.IsTerminal(refused.Code) ? SessionFailureClass.Terminal : SessionFailureClass.Transient;
        }
        if (error is IOException && (error.Message.Contains(Wire.DecryptionFailure, StringComparison.Ordinal) || error.Message.Contains(Wire.PreambleFailure, StringComparison.Ordinal)))
        {
            return SessionFailureClass.Terminal;
        }
        return (error is IOException || error is SocketException || error is TimeoutException) ? SessionFailureClass.Transient : SessionFailureClass.Terminal;
    }

    /// <summary>
    /// Whether a refusal the host sent can pass by itself: the host still holding this account's previous connection (it times out) or
    /// being busy verifying others. Every other refusal is the host deciding, and is not retried.
    /// </summary>
    public static SessionFailureClass ClassifyRefusal(string? reason)
    {
        if (reason == null)
        {
            return SessionFailureClass.Terminal;
        }
        return (reason.Contains(SteamIdentityAdmission.DuplicateAccountError, StringComparison.Ordinal) || reason.Contains(SessionServer.VerificationBusyError, StringComparison.Ordinal))
            ? SessionFailureClass.Transient
            : SessionFailureClass.Terminal;
    }

    /// <summary>How long to wait after the given number of attempts. There is no budget: the attempts go on while the editor is open.</summary>
    public static TimeSpan Backoff(int attemptsUsed)
    {
        int index = Math.Clamp(attemptsUsed, 0, BackoffSeconds.Length - 1);
        return TimeSpan.FromSeconds(BackoffSeconds[index]);
    }

    public static bool Eligible(AutoRecoveryInput input)
    {
        if (!input.AutoRecoverEnabled)
        {
            return false;
        }
        if (!input.ErrorRecorded)
        {
            return false;
        }
        if (input.FailureClass != SessionFailureClass.Transient)
        {
            return false;
        }
        if (input.TerminalTeardown)
        {
            return false;
        }
        if (input.HostedSession)
        {
            return false;
        }
        if (input.UnsavedChanges)
        {
            return false;
        }
        if (!input.ReconnectAccepted)
        {
            return false;
        }
        if (input.AttemptInFlight)
        {
            return false;
        }
        if (input.Now < input.NextAttemptAt)
        {
            return false;
        }
        return true;
    }

    public static bool PromptOffered(bool autoRecoverEnabled, bool errorRecorded, SessionFailureClass failureClass, bool terminalTeardown, bool reconnectAccepted, bool promptShown)
    {
        return (((!autoRecoverEnabled & errorRecorded) && failureClass == SessionFailureClass.Transient && !terminalTeardown) & reconnectAccepted) && !promptShown;
    }
}

public sealed record SessionReconnectCommitRequest(PlayModeTransitionResult Validation, Func<bool> Prepare);

public enum ReconnectCommitStage
{
    RejectedBeforePrepare,
    FailedDuringPrepare,
    Committed
}

public sealed record SessionReconnectCommitOutcome(PlayModeTransitionResult Result, ReconnectCommitStage Stage, bool PreviousResourcesDisposed)
{
    public bool Accepted => Result.Accepted;

    public string? ActionableError => Result.ActionableError;
}

public static class CollaborationSessionResourceRules
{
    public static bool IsBusy(bool wireAttached, bool connecting, bool hostServer, bool failStop)
    {
        return wireAttached | connecting | hostServer | failStop;
    }
}

public sealed record RecoveryPathOutcome(string? Published, bool Succeeded);

public static class RecoveryPathRules
{
    public static string CreatePath(string folder, string stamp)
    {
        return CollaborationDiagnostics.Bounded(Path.Combine(folder, stamp + ".json"));
    }

    public static RecoveryPathOutcome PublishAfterWrite(string path, Action write)
    {
        try
        {
            write();
            return new RecoveryPathOutcome(path, Succeeded: true);
        }
        catch (Exception)
        {
            return new RecoveryPathOutcome(null, Succeeded: false);
        }
    }
}

public sealed record ConnectionPromotionRequest(bool SourcePacketsAdmissible, bool TeardownBusy, bool ConnectCompleted);

public static class ConnectionPromotionRules
{
    public static bool MayPromote(ConnectionPromotionRequest request)
    {
        return request.ConnectCompleted && !request.TeardownBusy && request.SourcePacketsAdmissible;
    }

    public static bool ShouldPromote(bool sourcePacketsAdmissible, bool canPromoteConnection)
    {
        return MayPromote(new ConnectionPromotionRequest(sourcePacketsAdmissible, !canPromoteConnection, ConnectCompleted: true));
    }
}

public sealed record SessionLeaveGuardRequest(bool LifecycleTransitionInProgress);

public sealed record SessionLeaveRequest(ConnectionTeardownCoordinator Coordinator, bool LifecycleTransitionInProgress, Func<bool> HasUnacknowledgedWork, Func<string, bool> Preserve, Action Announce, Func<bool> DisposeResources);

public sealed record SessionLeaveOutcome(PlayModeTransitionResult Guard, bool PreserveAttempted, bool PreserveFailed, bool ResourcesDisposed, bool DisposalFailed, string? Failure)
{
    public bool Accepted => Guard.Accepted;

    public string? ActionableError => Guard.ActionableError;
}

public static class SessionLeaveRules
{
    public const string PreserveFailedDiagnostic = "Left with unacknowledged edits, but the local recovery archive could not be written.";

    public static SessionLeaveOutcome Run(SessionLeaveRequest request)
    {
        if (request is null)
        {
            throw new ArgumentNullException("request");
        }
        PlayModeTransitionResult playModeTransitionResult = request.Coordinator.TryBeginLeave(new SessionLeaveGuardRequest(request.LifecycleTransitionInProgress));
        if (!playModeTransitionResult.Accepted)
        {
            return new SessionLeaveOutcome(playModeTransitionResult, PreserveAttempted: false, PreserveFailed: false, ResourcesDisposed: false, DisposalFailed: false, null);
        }
        bool preserveAttempted = false;
        string? text = null;
        try
        {
            if (request.HasUnacknowledgedWork())
            {
                preserveAttempted = true;
                request.Preserve("Left with unacknowledged edits");
            }
        }
        catch (Exception error)
        {
            preserveAttempted = true;
            text = CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("Left with unacknowledged edits, but the local recovery archive could not be written. ", error);
        }
        try
        {
            request.Announce();
        }
        catch
        {
        }
        bool flag = false;
        bool flag2 = false;
        try
        {
            flag = request.DisposeResources();
        }
        catch (Exception error2)
        {
            flag = true;
            flag2 = true;
            if (text == null)
            {
                text = CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("Connection teardown failed while leaving: ", error2);
            }
        }
        return new SessionLeaveOutcome(playModeTransitionResult, preserveAttempted, text != null, ResourcesDisposed: true, flag2 || !flag, CollaborationDiagnostics.Bounded(text));
    }
}

public enum ConnectionTeardownOutcome
{
    None,
    Detached,
    ReusedPending,
    CompletedWithoutConnection,
    TerminalFailure
}

public sealed record ConnectionTeardownResult(ConnectionTeardownOutcome Outcome, string? Failure);

public sealed record TeardownCompletionObservation(bool Observed, bool TeardownFailed, string? TeardownFailure, bool AttemptRecovery, bool EnableDeferredRecovery, HotloadTeardownRecoveryStatus Status, string? Error);

public sealed class ConnectionTeardownCoordinator
{
    private readonly ConnectionTeardownGate gate = new ConnectionTeardownGate();

    public bool LeaveRequested { get; private set; }

    public bool DeferredHotloadRecovery { get; private set; }

    public bool TeardownCompletionPending { get; private set; }

    public bool TeardownCompleted { get; private set; }

    public string? TeardownFailure { get; private set; }

    public bool HotloadArchiveRecorded { get; private set; }

    public string? TerminalDiagnostic { get; private set; }

    public bool IsPending => gate.IsPending;

    public bool HasTerminalFailure => gate.HasTerminalFailure || TerminalDiagnostic != null;

    public bool IsFailStop => IsPending || HasTerminalFailure;

    public bool CanPromoteConnection => !IsFailStop && !TeardownCompletionPending;

    private static TeardownCompletionObservation NotObserved => new TeardownCompletionObservation(Observed: false, TeardownFailed: false, null, AttemptRecovery: false, EnableDeferredRecovery: false, HotloadTeardownRecoveryStatus.NotRequired, null);

    public void DeferHotloadRecovery()
    {
        DeferredHotloadRecovery = true;
    }

    public void ClearDeferredHotloadRecovery()
    {
        DeferredHotloadRecovery = false;
    }

    public PlayModeTransitionResult ValidateStart(SessionStartGuardRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.TerminalTeardownDiagnostic))
        {
            return Reject(request.TerminalTeardownDiagnostic);
        }
        if (request.ConnectionTeardownPending)
        {
            return Reject("A connection teardown is still pending. Leave the collaboration session before starting a new one.");
        }
        if (TeardownCompletionPending)
        {
            return Reject("A completed connection teardown is waiting to be observed; Start cannot clear it.");
        }
        if (request.ConnectionTeardownFailed)
        {
            return Reject("A previous connection teardown failed. This editor session remains fail-stop; restart the editor to recover.");
        }
        if (request.ConnectionBusy)
        {
            return Reject("Leave the current connection first");
        }
        if (!request.LifecycleValidation.Accepted)
        {
            return Reject(request.LifecycleValidation.ActionableError);
        }
        return PlayModeTransitionResult.Ok();
    }

    public PlayModeTransitionResult CommitStart()
    {
        if (TeardownCompletionPending)
        {
            return Reject("A completed connection teardown is waiting to be observed; Start cannot clear it.");
        }
        return CommitStartCore();
    }

    private PlayModeTransitionResult CommitStartCore()
    {
        LeaveRequested = false;
        DeferredHotloadRecovery = false;
        TeardownCompletionPending = false;
        TeardownCompleted = false;
        TeardownFailure = null;
        return PlayModeTransitionResult.Ok();
    }

    public PlayModeTransitionResult BeginStart(SessionStartCommitRequest request)
    {
        if (request is null || !request.Validation.Accepted)
        {
            return request?.Validation ?? Reject("Start validation did not complete.");
        }
        try
        {
            if (!request.Prepare())
            {
                return Reject("Start preparation failed; no collaboration bookkeeping was committed.");
            }
        }
        catch (Exception error)
        {
            return Reject(CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("Start preparation failed: ", error));
        }
        return CommitStart();
    }

    public PlayModeTransitionResult TryBeginStart(SessionStartGuardRequest request)
    {
        PlayModeTransitionResult playModeTransitionResult = ValidateStart(request);
        if (!playModeTransitionResult.Accepted)
        {
            return playModeTransitionResult;
        }
        return CommitStart();
    }

    public PlayModeTransitionResult ValidateReconnect(SessionReconnectGuardRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.TerminalTeardownDiagnostic))
        {
            return Reject(request.TerminalTeardownDiagnostic);
        }
        if (request.ConnectionTeardownPending)
        {
            return Reject("A connection teardown is still pending. Wait for it to complete or Leave before reconnecting.");
        }
        if (TeardownCompletionPending)
        {
            return Reject("A completed connection teardown is waiting to be observed; Reconnect cannot clear it.");
        }
        if (request.ConnectionTeardownFailed)
        {
            return Reject("A previous connection teardown failed. This editor session remains fail-stop; restart the editor to recover.");
        }
        if (request.Lifecycle == null)
        {
            return Reject("Runtime collaboration lifecycle is missing; recovery is not safe.");
        }
        PlayModeLifecycleState state = request.Lifecycle.State;
        if ((state == PlayModeLifecycleState.RuntimeStarting || state == PlayModeLifecycleState.RuntimeBlocked) ? true : false)
        {
            return Reject("Stop play mode and complete hotload recovery with Leave before reconnecting.");
        }
        if (request.Lifecycle.HasTerminalTeardownFailure)
        {
            return Reject("A connection teardown failed; this editor session remains fail-stop.");
        }
        if (request.SessionLeft)
        {
            return Reject("No session to recover — start or join one.");
        }
        if (request.HostedSession)
        {
            return Reject("A hosted session cannot be recovered in place; leave and host the session again.");
        }
        if (!request.SessionEstablished)
        {
            return Reject("Join a session first");
        }
        if (!request.SourceBaselineMatches)
        {
            return Reject("Source, assets or project settings changed. Sync in Git and start a new session.");
        }
        if (request.ConnectionAttemptPending)
        {
            return Reject("A connection attempt is already pending");
        }
        if (request.ConnectionLive)
        {
            return Reject("Already connected");
        }
        if (!request.TransportState.HasValue)
        {
            return Reject(request.ConnectionBusy ? "Reconnect requires an explicitly failed or disconnected transport." : "Reconnect requires a transport state.");
        }
        CollaborationTransportState? transportState = request.TransportState;
        bool flag;
        if (transportState.HasValue)
        {
            CollaborationTransportState valueOrDefault = transportState.GetValueOrDefault();
            if ((uint)(valueOrDefault - 3) <= 1u)
            {
                flag = true;
                goto IL_01fc;
            }
        }
        flag = false;
        goto IL_01fc;
        IL_01fc:
        if (!flag)
        {
            return Reject("Reconnect requires an explicitly failed or disconnected transport.");
        }
        return PlayModeTransitionResult.Ok();
    }

    public SessionReconnectCommitOutcome BeginReconnect(SessionReconnectCommitRequest request)
    {
        if (request is null || !request.Validation.Accepted)
        {
            PlayModeTransitionResult result = request?.Validation ?? Reject("Reconnect validation did not complete.");
            return new SessionReconnectCommitOutcome(result, ReconnectCommitStage.RejectedBeforePrepare, PreviousResourcesDisposed: false);
        }
        try
        {
            if (!request.Prepare())
            {
                return new SessionReconnectCommitOutcome(Reject("Reconnect preparation reported failure; the previous connection was already closed."), ReconnectCommitStage.FailedDuringPrepare, PreviousResourcesDisposed: true);
            }
        }
        catch (Exception error)
        {
            return new SessionReconnectCommitOutcome(Reject(CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("Reconnect preparation failed after the previous connection was closed: ", error)), ReconnectCommitStage.FailedDuringPrepare, PreviousResourcesDisposed: true);
        }
        return new SessionReconnectCommitOutcome(CommitReconnect(), ReconnectCommitStage.Committed, PreviousResourcesDisposed: true);
    }

    public PlayModeTransitionResult CommitReconnect()
    {
        if (TeardownCompletionPending)
        {
            return Reject("A completed connection teardown is waiting to be observed; Reconnect cannot clear it.");
        }
        return CommitReconnectCore();
    }

    private PlayModeTransitionResult CommitReconnectCore()
    {
        LeaveRequested = false;
        DeferredHotloadRecovery = false;
        return PlayModeTransitionResult.Ok();
    }

    public PlayModeTransitionResult TryBeginReconnect(SessionReconnectGuardRequest request)
    {
        PlayModeTransitionResult playModeTransitionResult = ValidateReconnect(request);
        if (!playModeTransitionResult.Accepted)
        {
            return playModeTransitionResult;
        }
        return CommitReconnect();
    }

    public PlayModeTransitionResult ValidateLeave(SessionLeaveGuardRequest request)
    {
        if (request.LifecycleTransitionInProgress)
        {
            return Reject("Stop the in-progress play-mode lifecycle before leaving; no source or runtime state was discarded");
        }
        return PlayModeTransitionResult.Ok();
    }

    public PlayModeTransitionResult TryBeginLeave(SessionLeaveGuardRequest request)
    {
        if (request.LifecycleTransitionInProgress)
        {
            return Reject("Stop the in-progress play-mode lifecycle before leaving; no source or runtime state was discarded");
        }
        LeaveRequested = true;
        return PlayModeTransitionResult.Ok();
    }

    public PlayModeTransitionResult ApplyArchiveMarker(PlayModeTransitionResult marker)
    {
        if (marker is null || !marker.Accepted)
        {
            HotloadArchiveRecorded = false;
            return marker ?? PlayModeTransitionResult.Rejected("Hotload recovery archive was not recorded.");
        }
        HotloadArchiveRecorded = true;
        return marker;
    }

    public ConnectionTeardownResult BeginDetachedTeardown<T>(Task<T>? pendingConnect, bool recoverWhenComplete) where T : IDisposable
    {
        if (gate.HasTerminalFailure || TerminalDiagnostic != null)
        {
            RecordTerminalTeardownFailure(gate.Failure ?? TerminalDiagnostic);
            return new ConnectionTeardownResult(ConnectionTeardownOutcome.TerminalFailure, TerminalDiagnostic);
        }
        if (gate.IsPending)
        {
            if (recoverWhenComplete)
            {
                DeferredHotloadRecovery = true;
            }
            return new ConnectionTeardownResult(ConnectionTeardownOutcome.ReusedPending, null);
        }
        if (TeardownCompletionPending)
        {
            if (recoverWhenComplete)
            {
                DeferredHotloadRecovery = true;
            }
            return new ConnectionTeardownResult(ConnectionTeardownOutcome.ReusedPending, null);
        }
        if (recoverWhenComplete)
        {
            DeferredHotloadRecovery = true;
        }
        if (pendingConnect != null)
        {
            TeardownCompletionPending = true;
            gate.Begin(pendingConnect);
            return new ConnectionTeardownResult(ConnectionTeardownOutcome.Detached, null);
        }
        gate.CompleteWithoutConnection();
        TeardownCompletionPending = true;
        return new ConnectionTeardownResult(ConnectionTeardownOutcome.CompletedWithoutConnection, null);
    }

    public void RecordTerminalTeardownFailure(string? failure)
    {
        string text = (string.IsNullOrWhiteSpace(failure) ? "unknown teardown error" : failure);
        if (!text.StartsWith("Connection teardown failed;", StringComparison.Ordinal))
        {
            TerminalDiagnostic = CollaborationDiagnostics.Bounded("Connection teardown failed; this editor session remains offline and fail-stop: " + text);
        }
    }

    public TeardownCompletionObservation ObserveTeardownCompletion(bool requiresHotloadRecovery, bool connectionPending)
    {
        if (gate.IsPending)
        {
            return NotObserved;
        }
        if (TeardownCompletionPending)
        {
            if (!gate.TryTakeCompletion(out string? teardownFailure))
            {
                return NotObserved;
            }
            TeardownCompletionPending = false;
            TeardownCompleted = true;
            TeardownFailure = teardownFailure;
        }
        else if (!TeardownCompleted && TerminalDiagnostic == null)
        {
            return NotObserved;
        }
        if (TeardownFailure != null)
        {
            RecordTerminalTeardownFailure(TeardownFailure);
        }
        string? text = TeardownFailure ?? TerminalDiagnostic;
        bool teardownFailed = text != null;
        HotloadTeardownRecoveryDecision hotloadTeardownRecoveryDecision = HotloadTeardownRecovery.Decide(new HotloadTeardownRecoveryRequest(LeaveRequested, requiresHotloadRecovery, HotloadArchiveRecorded, HotloadArchiveRecorded, TeardownPending: false, text, teardownFailed, DeferredHotloadRecovery));
        if (hotloadTeardownRecoveryDecision.EnableDeferredRecovery)
        {
            DeferredHotloadRecovery = true;
        }
        bool flag = connectionPending && hotloadTeardownRecoveryDecision.AttemptRecovery;
        return new TeardownCompletionObservation(Observed: true, teardownFailed, text, hotloadTeardownRecoveryDecision.AttemptRecovery && !flag, hotloadTeardownRecoveryDecision.EnableDeferredRecovery, flag ? HotloadTeardownRecoveryStatus.WaitingForTeardown : hotloadTeardownRecoveryDecision.Status, hotloadTeardownRecoveryDecision.Error);
    }

    private static PlayModeTransitionResult Reject(string? error)
    {
        return PlayModeTransitionResult.Rejected(CollaborationDiagnostics.Bounded(error));
    }
}
