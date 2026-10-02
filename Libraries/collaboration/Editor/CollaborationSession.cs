using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using Sandbox.Helpers;
using Sandbox.Internal;
using Sandbox.Services;
using Sandbox.Utility;

namespace TeamCreate;

public enum SessionState
{
    Offline,
    Ready,
    Hosting,
    Joining,
    Connected,
    Degraded,
    Recovering
}

public sealed class CollaborationSession : EditorEvent.ISceneView, EditorEvent.IEventListener
{
    private sealed class Tracked
    {
        public SceneEditorSession Session;

        public Dictionary<string, ObjectState> Known;

        public Packet Pending;

        public bool Ready;

        public Dictionary<string, Change> Group = new Dictionary<string, Change>();

        public Stack<List<Change>> Undo = new Stack<List<Change>>();

        public bool UndoPending;

        public JsonObject Settings;

        public Dictionary<string, ObjectState> LastOrder;

        public DateTime DiskStamp;

        public DateTime StampMismatchSince;
    }

    private sealed class StartPlan
    {
        public bool Host;

        public string Address;

        public int Port;

        public string Name;

        public string Token;

        public string SteamId;

        public string SteamToken;

        public bool AllowUnverifiedPeers;

        public bool ZeroConfig;

        public byte[] SessionKey;

        public byte[] Secret;

        public List<EndpointResolver.EndpointCandidate> Candidates;
    }

    private enum HotloadArchiveOutcome
    {
        NotApplicable,
        Archived,
        Failed
    }

    private sealed class EditScope(CollaborationSession owner) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                owner.continuousEdits--;
            }
        }
    }

    private readonly Dictionary<string, Tracked> scenes = new Dictionary<string, Tracked>();

    private Wire wire;

    private SessionServer server;

    private Task<Wire> connecting;

    private byte[] activeSessionKey;

    private string activeAddress;

    private int activePort;

    private Task<string> reconnectTokenTask;

    private bool reconnectQueued;

    private string reconnectAddress;

    private int reconnectPort;

    private bool reconnectRecovery;

    private bool reconnectRetrySpent;

    private bool endedByLeave;

    private LanDiscovery discovery;

    private StunClient stun;

    private RelayHost relayHost;

    private bool soloHarness;

    private readonly HeldPacketBuffer heldPackets = new HeldPacketBuffer();

    private bool inviteCarriesRelay;

    /// <summary>The relay this session uses: the project's own if it names one, none if it says "off", otherwise the one built into the library.</summary>
    private string EffectiveRelayUrl()
    {
        return RelayDefaults.Resolve(sessionConfig?.RelayUrl, RelaySetupRules.Fallback(RelaySetup.LocalRelay, RelayDefaults.DefaultUrl));
    }

    /// <summary>What the project's own settings say about the relay (an address, "off", or nothing), for the Relay card.</summary>
    public string ConfiguredRelayUrl => sessionConfig?.RelayUrl;

    private byte[] activeSecret;

    private List<EndpointResolver.EndpointCandidate> activeCandidates;

    private UpnpPortMapper upnp;

    private string inviteCodeValue;

    private string projectIdValue;

    private bool projectIdResolved;

    private byte[] hostSecret;

    private int hostPort;

    private bool stunEndpointFolded;

    private CancellationTokenSource hostVerificationLifetime = new CancellationTokenSource();

    private readonly ConnectionTeardownCoordinator lifecycle = new ConnectionTeardownCoordinator();

    private string baseline;

    private string project;

    private string userName;

    private string sessionToken;

    private string sourceBaseline;

    private string hostSession;

    private string steamIdValue;

    private string steamTokenValue;

    private DateTime nextScan;

    private DateTime nextPresence;

    private DateTime lastPresenceSent;

    private DateTime lastPingSent;

    // The newest host revision this editor has seen, from every accepted and snapshot packet. A ping reports it next to the scene digest so
    // the host compares digests only when this editor is up to date (see DriftDetector).
    private long lastRevision;

    private string driftNoticeShown;

    private string lastPresenceFingerprint;

    private DateTime lastReceive;

    private CollaborationConfig sessionConfig;

    private OrgDirectory orgDirectory;

    private string orgIdentValue;

    private string libraryVersionValue;

    private string projectManifestValue;

    private Task<Package> packageFetch;

    private bool? packageReachable;

    private DateTime nextReadiness;

    private DateTime nextPackageFetch;

    private int readinessOrgRevision = -1;

    private const int PackageRefreshSeconds = 30;

    private const int AutoSaveCheckSeconds = 1;

    private bool restoring;

    private int continuousEdits;

    private bool saving;

    private string focusedPeer;

    private DateTime focusedUntil;

    private DateTime focusNoticeExpires;

    private DateTime saveNoticeExpires;

    private DateTime lastAutoSave;

    private DateTime lastLocalEdit;

    private DateTime nextAutoSaveCheck;

    private bool autoRecover;

    private DateTime undoNoticeExpires;

    private DateTime actionNoticeExpires;

    private SessionFailureClass errorClass = SessionFailureClass.Terminal;

    private int recoverAttempts;

    private DateTime recoverNextAttempt;

    private bool recoverPromptShown;

    private bool recoverArmed;

    private bool recoverDeclined;

    private bool recoverArming;

    private string startAddress;

    private int startPort;

    private readonly HashSet<string> seen = new HashSet<string>();

    private const float PromptToastSeconds = 15f;

    private const float OutcomeToastSeconds = 6f;

    public const string NativeUndoEntryName = "Collaboration Undo";

    private const int ToolTextLimit = 40;

    private const int StateTextLimit = 32;

    private const int SceneTextLimit = 96;

    private const int ObjectNameTextLimit = 64;

    public static CollaborationSession Current { get; } = new CollaborationSession();

    public string Status { get; private set; } = "Offline";

    public string Error { get; private set; }

    public bool Connected { get; private set; }

    public SessionState State
    {
        get
        {
            string terminalTeardownError = TerminalTeardownError;
            if (Connected)
            {
                if (Error != null || terminalTeardownError != null)
                {
                    return SessionState.Degraded;
                }
                return IsHost ? SessionState.Hosting : SessionState.Connected;
            }
            if (terminalTeardownError != null)
            {
                return SessionState.Degraded;
            }
            if (Status.StartsWith("Reconnecting", StringComparison.Ordinal))
            {
                return SessionState.Recovering;
            }
            if (Busy)
            {
                return IsHost ? SessionState.Hosting : SessionState.Joining;
            }
            return ReadinessRules.CanStart(Readiness) ? SessionState.Ready : SessionState.Offline;
        }
    }

    public bool IsHost => server != null;

    public CollaborationConfig Config => sessionConfig;

    public OrgDirectory OrganizationDirectory => orgDirectory;

    public string InviteCode => inviteCodeValue;

    public IReadOnlyList<DiscoveredHost> DiscoveredHosts => discovery?.Hosts;

    public string PublicEndpointStatus { get; private set; }

    /// <summary>How this editor is connected: the route that actually carried the join, for the dock to show.</summary>
    public string RouteDescription { get; private set; }

    /// <summary>
    /// TEST-ONLY SEAM. Nothing in this library sets it; only the workspace's editor probe does. When set, and ONLY inside a
    /// disposable test copy (a folder under <c>.collaboration/test-copies/</c> that carries the marker file the solo-test
    /// launcher writes), the host lets it vouch for a guest hello instead of asking Steam's service, and treats that guest as
    /// a friend and organization member. It exists so one person with one Steam account can run a full two-editor session.
    /// In any other folder it is ignored, so a real project cannot be weakened by it.
    /// </summary>
    public static Func<Packet, bool> SoloGuestVoucher { get; set; }

    /// <summary>
    /// The facts a guest must present in its hello, read back so an out-of-process test collaborator can present the same ones.
    /// They are not secrets: the project id, baseline hash and manifest hash travel in every hello, and the organization and library
    /// version are printed in the dock.
    /// </summary>
    public (string ProjectId, string Baseline, string Manifest, string OrgIdent, string LibraryVersion) HandshakeFacts => (project, baseline, projectManifestValue, orgIdentValue, libraryVersionValue);

    /// <summary>Scene paths this session has subscribed and received a snapshot for. Empty until the host's scene is live.</summary>
    public IReadOnlyList<string> ReadyScenes => scenes.Where((KeyValuePair<string, Tracked> x) => x.Value.Ready).Select((KeyValuePair<string, Tracked> x) => x.Key).ToList();

    /// <summary>True in a disposable test copy with the voucher installed. Shown to the user, so it is never a silent state.</summary>
    public bool SoloHarness => soloHarness;

    /// <summary>The visible banner for a session running with the test seam active. Null otherwise.</summary>
    public string SoloHarnessNotice => soloHarness ? "Solo test copy: one reserved test identity is accepted here in place of a second Steam account. This never applies to a real project." : null;

    private static bool ComputeSoloHarness()
    {
        if (SoloGuestVoucher == null)
        {
            return false;
        }
        string root = SceneAdapter.Root;
        bool marker = false;
        try
        {
            marker = !string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, ".collaboration", TestHarnessRules.MarkerFile));
        }
        catch (Exception)
        {
            marker = false;
        }
        return TestHarnessRules.IsDisposableCopy(root, marker);
    }

    private bool SoloMayVouch(Packet hello)
    {
        try
        {
            return soloHarness && hello != null && SoloGuestVoucher != null && SoloGuestVoucher(hello);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The host's relay registration, worded for the dock. Null when no relay is configured.</summary>
    public string RelayStatus
    {
        get
        {
            RelayHost host = relayHost;
            if (host == null || host.State == RelayHostState.Stopped || host.State == RelayHostState.Idle)
            {
                return null;
            }
            switch (host.State)
            {
                case RelayHostState.Ready:
                    return "Relay connected — friends can join from any network.";
                case RelayHostState.Failed:
                    return "Relay unavailable — " + host.Failure;
                default:
                    return host.Status ?? "Connecting to the relay…";
            }
        }
    }

    public bool RelayFailed => relayHost?.State == RelayHostState.Failed;

    /// <summary>Set while hosting with no relay a friend elsewhere could reach, so the dock can say so instead of leaving a silent gap. Null otherwise.</summary>
    public string NoRelayNotice => IsHost && (relayHost == null || relayHost.State == RelayHostState.Stopped) ? RelayDefaults.NoRelayNotice(EffectiveRelayUrl(), sessionConfig?.RelayUrl) : null;

    /// <summary>What source synchronization is doing this frame; the dock reads it to explain a pause.</summary>
    public PlaySpectateDecision PlayDecision { get; private set; }

    public bool PausedForPlay => Connected && PlayDecision == PlaySpectateDecision.PausedForPlay;

    public int HeldSceneChanges => heldPackets.Count;

    /// <summary>The sentence shown while the local editor is playing and scene edits are held back. Null otherwise.</summary>
    public string PlayNotice
    {
        get
        {
            if (!PausedForPlay)
            {
                return null;
            }
            int held = heldPackets.Count;
            return held == 0
                ? "Playing: scene edits are paused. Your collaborators can still see you; changes resume when you stop."
                : "Playing: scene edits are paused. " + held + " change" + (held == 1 ? "" : "s") + " from collaborators will apply when you stop.";
        }
    }

    public string PortMappingStatus => upnp?.Status;

    public bool PortMappingFailed
    {
        get
        {
            UpnpPortMapper upnpPortMapper = upnp;
            return upnpPortMapper != null && upnpPortMapper.Current == UpnpPortMapper.State.Failed;
        }
    }

    public string DiscoveryStatus => discovery?.Failure;

    public bool Busy => CollaborationSessionResourceRules.IsBusy(wire != null, connecting != null, server != null, lifecycle.IsFailStop);

    public PlayModeLifecycle RuntimeLifecycle { get; } = new PlayModeLifecycle(optIn: false, "source-known-state");

    public PlayModeLifecycleEventState RuntimeLifecycleEvents { get; } = new PlayModeLifecycleEventState();

    public bool ExperimentalRuntimeOptIn
    {
        get
        {
            return RuntimeLifecycle.OptIn;
        }
        set
        {
            PlayModeTransitionResult playModeTransitionResult = RuntimeLifecycle.SetOptIn(value);
            if (!playModeTransitionResult.Accepted)
            {
                SetLifecycleError(playModeTransitionResult.ActionableError);
            }
        }
    }

    public string PlayModeStatus
    {
        get
        {
            PlayModeLifecycle runtimeLifecycle = RuntimeLifecycle;
            if (1 == 0)
            {
            }
            if (runtimeLifecycle == null)
            {
                goto IL_0059;
            }
            switch (runtimeLifecycle.State)
            {
            case PlayModeLifecycleState.Source:
                break;
            case PlayModeLifecycleState.RuntimeDisabled:
                goto IL_0039;
            case PlayModeLifecycleState.RuntimeStarting:
                goto IL_0041;
            case PlayModeLifecycleState.RuntimeActive:
                goto IL_0049;
            case PlayModeLifecycleState.RuntimeBlocked:
                goto IL_0051;
            default:
                goto IL_0059;
            }
            string result = "Source collaboration";
            goto IL_0061;
            IL_0051:
            result = "Runtime collaboration blocked • recovery required";
            goto IL_0061;
            IL_0059:
            result = "Source collaboration";
            goto IL_0061;
            IL_0061:
            if (1 == 0)
            {
            }
            return result;
            IL_0039:
            result = "Source collaboration • runtime disabled";
            goto IL_0061;
            IL_0041:
            result = "Source collaboration • starting runtime";
            goto IL_0061;
            IL_0049:
            result = "Experimental runtime collaboration enabled • transport and apply unsupported";
            goto IL_0061;
        }
    }

    public string RuntimeLifecycleDiagnostic => RuntimeLifecycle.LastDiagnostic;

    public string RuntimeLifecycleError => RuntimeLifecycle.LastActionableError;

    public bool HasPendingLifecycleCandidate => (object)RuntimeLifecycleEvents.Pending != null;

    public Dictionary<string, Packet> Roster { get; } = new Dictionary<string, Packet>();

    public string Peer { get; } = Guid.NewGuid().ToString();

    public long AcceptedOperations { get; private set; }

    public int DrawnMarkers { get; private set; }

    public int DrawnSelections { get; private set; }

    public string FocusNotice { get; private set; }

    public string RecoveryPath { get; private set; }

    public IReadOnlyList<ReadinessItem> Readiness { get; private set; } = Array.Empty<ReadinessItem>();

    public string ActiveSceneSupport
    {
        get
        {
            SceneEditorSession supportedActiveScene = SupportedActiveScene;
            return (supportedActiveScene != null) ? ("Active scene: " + SceneAdapter.PathOf(supportedActiveScene)) : "This view is outside the supported edit set (use a saved scene under Assets/scenes).";
        }
    }

    private static SceneEditorSession SupportedActiveScene
    {
        get
        {
            SceneEditorSession active = SceneEditorSession.Active;
            object result;
            if (active != null && !active.IsPrefabSession && !active.IsPlaying)
            {
                string text = SceneAdapter.PathOf(active);
                if (text != null && text.StartsWith("scenes/"))
                {
                    result = active;
                    goto IL_0036;
                }
            }
            result = null;
            goto IL_0036;
            IL_0036:
            return (SceneEditorSession)result;
        }
    }

    public string RecoveryNotice { get; private set; }

    public string SaveNotice { get; private set; }

    public bool AutoSaveIdle { get; set; }

    public bool AutoRecover
    {
        get
        {
            return autoRecover;
        }
        set
        {
            if (autoRecover != value)
            {
                autoRecover = value;
                if (value)
                {
                    recoverAttempts = 0;
                    recoverNextAttempt = default;
                    recoverDeclined = false;
                }
            }
        }
    }

    public string UndoNotice { get; private set; }

    public string ActionNotice { get; private set; }

    /// <summary>
    /// On the host: a formal sentence when a guest's editor keeps drifting from the host's scene although it was resynchronised repeatedly,
    /// otherwise null. Always null on a guest.
    /// </summary>
    public string DriftNotice => server?.DriftNotice;

    public string TerminalTeardownError => lifecycle.TerminalDiagnostic;

    public bool ReconnectRecovery => reconnectRecovery;

    private CollaborationSession()
    {
        EditorEvent.Register(this);
    }

    private void SetLifecycleError(string error)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            RecoveryNotice = BoundedDiagnostic(error);
            GlobalGameNamespace.Log.Error("Collaboration lifecycle: " + RecoveryNotice);
        }
    }

    private void ApplyLifecycleResult(PlayModeTransitionResult result)
    {
        if (!result.Accepted)
        {
            SetLifecycleError(result.ActionableError);
        }
    }

    [Event("scene.startplay")]
    public static void OnStartPlay()
    {
        CollaborationSession current = Current;
        current.Checkpoint();
        SceneEditorSession active = SceneEditorSession.Active;
        SceneEditorSession sceneEditorSession = ((active != null && !active.IsPrefabSession) ? active : null);
        string text = ((sceneEditorSession == null) ? null : SceneAdapter.PathOf(sceneEditorSession));
        if (sceneEditorSession == null || !RuntimeStateRules.IsSafeScenePath(text))
        {
            current.SetLifecycleError("Play mode could not identify a saved source scene; source collaboration is unchanged.");
        }
        else
        {
            current.ApplyLifecycleResult(current.RuntimeLifecycleEvents.RecordStartIntent(current.RuntimeLifecycle, sceneEditorSession, text, Guid.NewGuid().ToString("N")));
        }
    }

    [Event("scene.play")]
    public static void OnPlay()
    {
        CollaborationSession current = Current;
        current.Checkpoint();
        GameEditorSession[] array = SceneEditorSession.All.OfType<GameEditorSession>().ToArray();
        if (array.Length != 1)
        {
            current.SetLifecycleError((array.Length == 0) ? "Play-mode lifecycle could not find a GameEditorSession in SceneEditorSession.All; source collaboration is unchanged." : "Multiple GameEditorSession objects are active; runtime collaboration was not enabled. Stop play mode and retry.");
            return;
        }
        GameEditorSession gameEditorSession = array[0];
        SceneEditorSession parent = gameEditorSession.Parent;
        string a = SceneAdapter.PathOf(parent) ?? parent?.Scene?.Source?.ResourcePath;
        PlayModePendingCandidate pending = current.RuntimeLifecycleEvents.Pending;
        if ((object)pending == null || parent != pending.SourceSessionToken || !string.Equals(a, pending.SourceScenePath, StringComparison.OrdinalIgnoreCase))
        {
            current.SetLifecycleError("Runtime GameEditorSession parent/path did not match the source captured by scene.startplay; runtime collaboration was not enabled.");
            return;
        }
        PlayModeTransitionResult result = current.RuntimeLifecycleEvents.AttachRuntime(current.RuntimeLifecycle, RuntimeSceneAdapter.Describe(gameEditorSession), pending.RuntimeRunId);
        current.ApplyLifecycleResult(result);
    }

    [Event("scene.stop")]
    public static void OnStop()
    {
        CollaborationSession current = Current;
        current.ApplyLifecycleResult(current.RuntimeLifecycleEvents.RecordStop(current.RuntimeLifecycle));
    }

    [Event("scene.saved")]
    public static void OnSceneSaved(Scene scene)
    {
        Current.ReconcileSavedScene(scene);
    }

    private void ReconcileSavedScene(Scene scene)
    {
        SceneEditorSession sceneEditorSession = SceneEditorSession.All.FirstOrDefault((SceneEditorSession x) => x.Scene == scene) ?? SceneEditorSession.Active;
        string text = SceneAdapter.PathOf(sceneEditorSession);
        Tracked tracked = null;
        DateTime dateTime = default;
        if (text != null && scenes.TryGetValue(text, out var value) && value.Session == sceneEditorSession)
        {
            tracked = value;
            dateTime = SceneAdapter.DiskStamp(text);
        }
        bool flag = Connected && Error == null;
        switch (SceneSaveRules.Decide(saving || (tracked != null && tracked.DiskStamp == dateTime), flag, tracked != null, flag && tracked != null && CanSave()))
        {
        case SceneSaveOutcome.Absorb:
            tracked.DiskStamp = dateTime;
            server?.AllowSavedBaseline(SceneAdapter.Baseline());
            SetSaveNotice("The scene was saved outside the session; the session accepted the save.");
            break;
        case SceneSaveOutcome.Warn:
            tracked.DiskStamp = dateTime;
            SetSaveNotice("The scene was saved outside the session while edits were in flight; disk and session state may differ.");
            break;
        }
    }

    private void SetSaveNotice(string text)
    {
        string text2 = BoundedDiagnostic(text);
        if (!string.Equals(SaveNotice, text2, StringComparison.Ordinal) || !(DateTime.UtcNow < saveNoticeExpires))
        {
            SaveNotice = text2;
            saveNoticeExpires = DateTime.UtcNow.AddSeconds(8.0);
        }
    }

    [EditorEvent.Hotload]
    public static void CodeChanged()
    {
        CollaborationSession current = Current;
        PlayModeTransitionResult playModeTransitionResult = current.RuntimeLifecycle.BlockForHotload("hotloaded");
        current.ApplyLifecycleResult(playModeTransitionResult);
        HotloadArchiveOutcome hotloadArchiveOutcome = current.ArchiveHotloadLifecycle(current.RuntimeLifecycle.ArchiveReason ?? "hotloaded");
        bool busy = current.Busy;
        HotloadCodeChangeReport hotloadCodeChangeReport = HotloadCodeChangeRules.Decide(new HotloadCodeChangeRequest(current.RuntimeLifecycle.State, playModeTransitionResult.Accepted, hotloadArchiveOutcome == HotloadArchiveOutcome.Archived, busy, playModeTransitionResult.Accepted ? null : playModeTransitionResult.ActionableError));
        if (hotloadCodeChangeReport.SurfaceError)
        {
            current.Fail(hotloadCodeChangeReport.Message);
        }
        if (!hotloadCodeChangeReport.ArchiveClaimed)
        {
            current.RecoveryNotice = CollaborationDiagnostics.Bounded(hotloadCodeChangeReport.LifecycleRejectionReason ?? current.RuntimeLifecycle.LastDiagnostic);
        }
        if (hotloadCodeChangeReport.TeardownTransport)
        {
            current.DisposeActiveResources();
        }
        current.BeginConnectionTeardown(hotloadCodeChangeReport.RequiresHotloadRecoveryAfterLeave);
    }

    private static string BoundedDiagnostic(string value)
    {
        return CollaborationDiagnostics.Bounded(value);
    }

    public void Start(bool host, string address, int port, string name, string token, string steamId = null, string steamToken = null, bool allowUnverifiedPeers = false)
    {
        StartCore(new StartPlan
        {
            Host = host,
            Address = address,
            Port = port,
            Name = name,
            Token = token,
            SteamId = steamId,
            SteamToken = steamToken,
            AllowUnverifiedPeers = allowUnverifiedPeers
        });
    }

    private void StartCore(StartPlan plan)
    {
        activeAddress = null;
        activePort = 0;
        soloHarness = ComputeSoloHarness();
        if (soloHarness && LocalSteamIdentity.TryGet(out var soloLocal, out var _, out var _))
        {
            // The reserved guest borrows the signed-in account's picture so its marker is not an empty ring.
            AvatarCache.SyntheticAvatarAlias = SteamIdentityRules.Canonical(soloLocal);
        }
        else
        {
            AvatarCache.SyntheticAvatarAlias = null;
        }
        PlayModeTransitionResult playModeTransitionResult = lifecycle.ValidateStart(new SessionStartGuardRequest(RuntimeLifecycle.ValidateSessionStart(), Busy, lifecycle.IsPending, lifecycle.HasTerminalFailure, lifecycle.TerminalDiagnostic));
        if (!playModeTransitionResult.Accepted)
        {
            throw new InvalidOperationException(playModeTransitionResult.ActionableError);
        }
        if (!plan.ZeroConfig && (string.IsNullOrWhiteSpace(plan.Token) || plan.Token.Length < 12))
        {
            throw new InvalidOperationException("Use a session key of at least 12 characters");
        }
        if (plan.ZeroConfig && !plan.Host && plan.SessionKey == null)
        {
            throw new InvalidOperationException("A zero-config join requires the session key derived from the invite code.");
        }
        if (string.IsNullOrWhiteSpace(plan.Name) || plan.Name.Length > 64)
        {
            throw new InvalidOperationException("No display name is available for the signed-in Steam account");
        }
        foreach (SceneEditorSession item in SceneEditorSession.All.Where((SceneEditorSession x) => !(x is GameEditorSession)))
        {
            if (item.HasUnsavedChanges)
            {
                throw new InvalidOperationException("Save or revert local scenes before starting a new collaboration session");
            }
        }
        string path = Path.Combine(SceneAdapter.Root, "ProjectSettings", "collaboration.json");
        if (plan.Host)
        {
            // The first Host click on a new project writes the shared settings itself: a fresh project id and the library's own gates, all on.
            // Only the host does this; a friend joining a project that has no settings is told to get the host's copy, since a project id they
            // invented would never match.
            SetupResult setup = CollaborationSetup.EnsureConfig(path, Project.Current?.Config?.Org, LibraryInfo.DefaultLibraryIdent(Project.Current?.Config?.Org), LibraryInfo.LibraryVersion);
            if (setup.Error != null)
            {
                throw new InvalidOperationException(setup.Error);
            }
            if (setup.Created)
            {
                ActionNotice = "Created ProjectSettings/collaboration.json for this project. Your friend needs the same file: send them the project as it is now.";
                actionNoticeExpires = DateTime.UtcNow.AddSeconds(30.0);
            }
        }
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("This project has no ProjectSettings/collaboration.json. Ask the host for a copy of the project, so both of you have the same file.");
        }
        JsonNode jsonNode = JsonNode.Parse(File.ReadAllText(path))?["ProjectId"];
        if (jsonNode == null || !Guid.TryParse(jsonNode.GetValue<string>(), out var _))
        {
            throw new InvalidOperationException("ProjectId must be a GUID");
        }
        string preparedProject = jsonNode.GetValue<string>();
        string preparedBaseline = SceneAdapter.Baseline();
        string text = SceneAdapter.Baseline(includeScenes: false);
        CollaborationConfig collaborationConfig = CollaborationConfig.LoadFrom(path, out var _);
        UseConfig(collaborationConfig);
        if (plan.ZeroConfig && plan.Host)
        {
            PrepareHostSecret(plan, collaborationConfig);
        }
        string org = collaborationConfig.Org;
        string preparedLibraryVersion = LibraryInfo.LibraryVersion;
        string preparedManifest = ProjectManifest.Compute(Project.Current.RootDirectory.FullName);
        try
        {
            RefreshReadiness();
        }
        catch (Exception error2)
        {
            Fail("Readiness could not be evaluated: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error2));
            return;
        }
        string text2 = (ReadinessRules.CanStart(Readiness) ? null : (ReadinessRules.BlockReason(Readiness) ?? "Hosting is unavailable: the readiness checklist could not be evaluated."));
        if (text2 != null)
        {
            Fail(text2);
            return;
        }
        SessionServer preparedServer = null;
        Task<Wire> preparedConnect = null;
        int boundPort = plan.Port;
        string dialAddress = ((plan.Host && plan.Address == "0.0.0.0") ? "127.0.0.1" : plan.Address);
        PlayModeTransitionResult playModeTransitionResult2 = lifecycle.BeginStart(new SessionStartCommitRequest(playModeTransitionResult, () =>
        {
            if (plan.Host)
            {
                preparedServer = OpenHostServer(plan, preparedProject, preparedBaseline, out boundPort);
                preparedServer.AllowUnverifiedPeers = plan.AllowUnverifiedPeers;
                preparedServer.peerFacts = BuildPeerFacts;
                preparedServer.config = sessionConfig;
                preparedServer.hostLibraryVersion = preparedLibraryVersion;
                preparedServer.hostManifest = preparedManifest;
            }
            List<EndpointResolver.EndpointCandidate> candidates2 = plan.Candidates;
            preparedConnect = ((candidates2 != null && candidates2.Count > 0) ? ConnectCandidates(plan.Candidates, plan.SessionKey, plan.Secret) : Wire.Connect(dialAddress, boundPort, plan.SessionKey, plan.SessionKey != null));
            return true;
        }));
        if (!playModeTransitionResult2.Accepted)
        {
            if (preparedServer != null)
            {
                ResourceDisposalResult resourceDisposalResult = ResourceDisposalRules.DisposePair(null, "collaboration transport", preparedServer, "collaboration host server");
                if (!resourceDisposalResult.Succeeded)
                {
                    RecordTerminalTeardownFailure(resourceDisposalResult.Failure);
                }
            }
            connecting = preparedConnect;
            BeginConnectionTeardown(recoverWhenComplete: false);
            throw new InvalidOperationException(playModeTransitionResult2.ActionableError);
        }
        project = preparedProject;
        baseline = preparedBaseline;
        userName = plan.Name;
        sessionToken = plan.Token;
        activeSessionKey = plan.SessionKey;
        activeSecret = plan.Secret;
        activeCandidates = plan.Candidates;
        RouteDescription = null;
        List<EndpointResolver.EndpointCandidate> candidates = plan.Candidates;
        if (candidates == null || candidates.Count <= 0)
        {
            activeAddress = dialAddress;
            activePort = boundPort;
        }
        startAddress = dialAddress;
        startPort = boundPort;
        steamIdValue = plan.SteamId;
        steamTokenValue = plan.SteamToken;
        orgIdentValue = org;
        libraryVersionValue = preparedLibraryVersion;
        projectManifestValue = preparedManifest;
        sourceBaseline = text;
        hostSession = null;
        reconnectQueued = false;
        reconnectTokenTask = null;
        reconnectRecovery = false;
        reconnectRetrySpent = false;
        scenes.Clear();
        Roster.Clear();
        seen.Clear();
        lastRevision = 0;
        driftNoticeShown = null;
        Error = null;
        RecoveryNotice = null;
        // A path from an earlier session would otherwise sit under a new failure and point at an archive that has
        // nothing to do with it.
        RecoveryPath = null;
        restoring = false;
        errorClass = SessionFailureClass.Terminal;
        recoverAttempts = 0;
        recoverNextAttempt = default;
        recoverPromptShown = false;
        recoverArmed = false;
        recoverDeclined = false;
        SaveNotice = null;
        saveNoticeExpires = default;
        UndoNotice = null;
        undoNoticeExpires = default;
        ActionNotice = null;
        actionNoticeExpires = default;
        lastAutoSave = default;
        lastLocalEdit = default;
        nextAutoSaveCheck = default;
        endedByLeave = false;
        server = preparedServer;
        connecting = preparedConnect;
        if (plan.ZeroConfig && plan.Host)
        {
            BeginHostConnectivity(plan, boundPort);
        }
        Status = "Connecting…";
    }

    private void PrepareHostSecret(StartPlan plan, CollaborationConfig config)
    {
        if (string.IsNullOrEmpty(config.ProjectId))
        {
            throw new InvalidOperationException("ProjectSettings/collaboration.json must carry a shared ProjectId before a session can be hosted.");
        }
        plan.Secret = TeamCreate.InviteCode.NewSecret();
        plan.SessionKey = TeamCreate.InviteCode.DeriveKey(plan.Secret, config.ProjectId);
        plan.Token = Convert.ToHexString(plan.SessionKey);
    }

    private SessionServer OpenHostServer(StartPlan plan, string project, string baseline, out int boundPort)
    {
        int num = ((!plan.ZeroConfig) ? 1 : 5);
        SocketException error = null;
        for (int i = 0; i < num; i++)
        {
            int num2 = plan.Port + i;
            if (num2 > 65535)
            {
                break;
            }
            string text = Path.Combine(SceneAdapter.Root, ".collaboration", "journal-" + Guid.NewGuid().ToString() + ".jsonl");
            try
            {
                SessionServer result = new SessionServer(plan.Address, num2, project, baseline, plan.Token, text, null, (Packet hello) => SoloMayVouch(hello) ? Task.FromResult(SteamTokenVerdict.Verified) : new SteamTokenHttpVerifier(SandboxHttpTokenTransport.Default).VerifyAsync(hello, hostVerificationLifetime.Token), plan.SessionKey);
                boundPort = num2;
                return result;
            }
            catch (SocketException ex)
            {
                error = ex;
                try
                {
                    File.Delete(text);
                }
                catch
                {
                }
            }
        }
        boundPort = plan.Port;
        throw new IOException(CollaborationDiagnostics.DescribeExternalFailure((num == 1) ? $"Port {plan.Port} could not be opened: " : $"None of the {num} ports from {plan.Port} could be opened: ", error));
    }

    private async Task<Wire> ConnectCandidates(IReadOnlyList<EndpointResolver.EndpointCandidate> candidates, byte[] sessionKey, byte[] secret)
    {
        List<RouteAttempt> attempts = new List<RouteAttempt>();
        foreach (EndpointResolver.EndpointCandidate candidate in candidates)
        {
            try
            {
                Wire reached;
                if (candidate.Source == RelayProtocol.RouteSource)
                {
                    if (secret == null || sessionKey == null)
                    {
                        throw new InvalidOperationException("The relay route needs the invite code's secret.");
                    }
                    if (!soloHarness && !RelayProtocol.IsPublicRelay(candidate.Address) && !IsHost)
                    {
                        // A code naming localhost was made on the host's own computer. Dialling it here reaches THIS computer, so say so instead of
                        // reporting a raw socket error that sends the person looking for a network problem that does not exist.
                        throw new RelayRefusedException(RelayProtocol.LoopbackCode, "the code points to a relay on the host's own computer");
                    }
                    reached = await RelayGuest.ConnectAsync(() => new SandboxRelaySocket(), candidate.Address, secret, sessionKey);
                    RouteDescription = "Connected through the relay (" + RelayHostName(candidate.Address) + ")";
                }
                else
                {
                    reached = await Wire.Connect(candidate.Address, candidate.Port, sessionKey, sessionKey != null);
                    RouteDescription = candidate.Source == "lan" ? "Connected on the local network" : "Connected directly to " + candidate.Address + ":" + candidate.Port;
                }
                activeAddress = candidate.Address;
                activePort = candidate.Port;
                return reached;
            }
            catch (Exception ex)
            {
                string target = candidate.Source == RelayProtocol.RouteSource ? candidate.Address : candidate.Address + ":" + candidate.Port;
                attempts.Add(new RouteAttempt(candidate.Source, target, ex.Message, (ex as RelayRefusedException)?.Code));
            }
        }
        string summary = RouteFailureRules.Summarize(attempts);
        // A wrong invite secret is refused by the relay and cannot succeed on a second try; every other
        // route failure is a network fault that recovery may legitimately retry.
        bool terminal = attempts.Any((RouteAttempt a) => a.RelayCode != null && RelayProtocol.IsTerminal(a.RelayCode));
        if (terminal)
        {
            throw new InvalidOperationException(summary);
        }
        throw new IOException(summary);
    }

    private static string RelayHostName(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ? uri.Host : "relay";
    }

    private void BeginHostConnectivity(StartPlan plan, int port)
    {
        hostSecret = plan.Secret;
        hostPort = port;
        PublicEndpointStatus = "Public endpoint pending…";
        stunEndpointFolded = false;
        stun = new StunClient();
        inviteCodeValue = BuildInviteCode(port);
        StartDiscovery();
        if (discovery != null)
        {
            string text = projectManifestValue;
            if (text != null && text.Length == 8)
            {
                try
                {
                    discovery.StartHost(port, Convert.ToHexString(hostSecret).ToLowerInvariant(), projectManifestValue);
                }
                catch (Exception error)
                {
                    GlobalGameNamespace.Log.Error("Collaboration discovery: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error));
                }
            }
        }
        upnp = new UpnpPortMapper();
        try
        {
            upnp.Map(port);
        }
        catch (Exception error2)
        {
            GlobalGameNamespace.Log.Error("Collaboration port mapping: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error2));
        }
        StartRelayHost(plan, port);
    }

    private void StartRelayHost(StartPlan plan, int port)
    {
        string relayUrl = EffectiveRelayUrl();
        if (string.IsNullOrWhiteSpace(relayUrl) || plan.Secret == null || plan.SessionKey == null)
        {
            return;
        }
        string accessKey = ReadRelayAccessKey();
        try
        {
            relayHost = new RelayHost(() => new SandboxRelaySocket(), relayUrl, plan.Secret, plan.SessionKey, accessKey);
            if (server != null)
            {
                server.Relay = relayHost;
            }
            relayHost.Start();
            inviteCarriesRelay = true;
            inviteCodeValue = BuildInviteCode(port);
        }
        catch (Exception error)
        {
            relayHost?.Dispose();
            relayHost = null;
            inviteCarriesRelay = false;
            GlobalGameNamespace.Log.Error("Collaboration relay: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error));
        }
    }

    // The access key is a credential, so it comes from an untracked local file and never from the shared,
    // committed project settings. A missing file is normal (a relay that needs no key); an unusable one is logged
    // by name and treated as absent, which the relay then answers with its own refusal.
    private static string ReadRelayAccessKey()
    {
        try
        {
            string path = Path.Combine(SceneAdapter.Root, ".collaboration", RelayAccessKeyFile.FileName);
            if (!File.Exists(path))
            {
                return null;
            }
            string key = RelayAccessKeyFile.Parse(File.ReadAllText(path), out string problem);
            if (problem != null)
            {
                GlobalGameNamespace.Log.Error("Collaboration relay: " + problem);
            }
            return key;
        }
        catch (Exception error)
        {
            GlobalGameNamespace.Log.Error("Collaboration relay: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("The access key file could not be read: ", error));
            return null;
        }
    }

    private string BuildInviteCode(int port)
    {
        return TeamCreate.InviteCode.Encode(new InviteData
        {
            Port = port,
            ProjectId = sessionConfig.ProjectId,
            Secret = hostSecret,
            Endpoints = EndpointsFor(stun?.PublicEndPoint),
            Relay = inviteCarriesRelay ? RelayProtocol.InviteRelayFor(EffectiveRelayUrl(), allowLoopback: soloHarness) : null
        });
    }

    private static List<byte[]> EndpointsFor(string address)
    {
        if (!IPAddress.TryParse(address, out IPAddress address2) || address2.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }
        return new List<byte[]> { address2.GetAddressBytes() };
    }

    public void StartFromInvite(string code, string name, string steamId = null, string steamToken = null, bool allowUnverifiedPeers = false, string advancedAddress = null, int advancedPort = 0)
    {
        string text = CurrentProjectId();
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("Create ProjectSettings/collaboration.json with a shared ProjectId, then commit it to Git");
        }
        if (!TeamCreate.InviteCode.TryDecode(code, text, out var data, out var error))
        {
            throw new InvalidOperationException(error);
        }
        StartPlan startPlan = JoinPlan(data, text, name, steamId, steamToken, allowUnverifiedPeers, advancedAddress, advancedPort);
        if (startPlan.Candidates.Count == 0)
        {
            throw new InvalidOperationException("No address to try: this code carries no public address and discovery has not found that host on your network. Type the host's address under Advanced.");
        }
        StartCore(startPlan);
    }

    public void StartFromDiscovery(DiscoveredHost host, string name, string steamId = null, string steamToken = null, bool allowUnverifiedPeers = false)
    {
        if ((object)host == null)
        {
            throw new InvalidOperationException("Select a discovered host to join.");
        }
        string text = CurrentProjectId();
        if (string.IsNullOrEmpty(text))
        {
            throw new InvalidOperationException("Create ProjectSettings/collaboration.json with a shared ProjectId, then commit it to Git");
        }
        byte[] array;
        try
        {
            array = Convert.FromHexString(host.SecretHex ?? string.Empty);
        }
        catch (FormatException)
        {
            array = null;
        }
        if (array == null || array.Length != 16)
        {
            throw new InvalidOperationException("That host did not report a usable session secret.");
        }
        byte[] array2 = TeamCreate.InviteCode.DeriveKey(array, text);
        StartCore(new StartPlan
        {
            Host = false,
            Address = host.EndPoint,
            Port = host.Port,
            Name = name,
            Token = Convert.ToHexString(array2),
            SteamId = steamId,
            SteamToken = steamToken,
            AllowUnverifiedPeers = allowUnverifiedPeers,
            ZeroConfig = true,
            SessionKey = array2,
            Secret = array,
            Candidates = new List<EndpointResolver.EndpointCandidate>
            {
                new EndpointResolver.EndpointCandidate(host.EndPoint, host.Port, "lan")
            }
        });
    }

    public void StartHosted(int port, string name, string steamId = null, string steamToken = null, bool allowUnverifiedPeers = false)
    {
        StartCore(new StartPlan
        {
            Host = true,
            Address = "0.0.0.0",
            Port = port,
            Name = name,
            SteamId = steamId,
            SteamToken = steamToken,
            AllowUnverifiedPeers = allowUnverifiedPeers,
            ZeroConfig = true
        });
    }

    private StartPlan JoinPlan(InviteData invite, string projectId, string name, string steamId, string steamToken, bool allowUnverifiedPeers, string advancedAddress, int advancedPort)
    {
        byte[] array = TeamCreate.InviteCode.DeriveKey(invite.Secret, projectId);
        return new StartPlan
        {
            Host = false,
            Address = advancedAddress,
            Port = invite.Port,
            Name = name,
            Token = Convert.ToHexString(array),
            SteamId = steamId,
            SteamToken = steamToken,
            AllowUnverifiedPeers = allowUnverifiedPeers,
            ZeroConfig = true,
            SessionKey = array,
            Secret = invite.Secret,
            Candidates = EndpointResolver.Order(invite, DiscoveredHosts, advancedAddress, advancedPort).ToList()
        };
    }

    private string CurrentProjectId()
    {
        if (!string.IsNullOrEmpty(sessionConfig?.ProjectId))
        {
            return sessionConfig.ProjectId;
        }
        if (projectIdResolved)
        {
            return projectIdValue;
        }
        projectIdResolved = true;
        return projectIdValue = LoadConfig().ProjectId;
    }

    public void StartDiscovery()
    {
        if (discovery != null)
        {
            return;
        }
        try
        {
            string text = CurrentProjectId();
            if (!string.IsNullOrEmpty(text))
            {
                discovery = new LanDiscovery(DiscoveryProtocol.ProjectHash8(text));
            }
        }
        catch (Exception error)
        {
            GlobalGameNamespace.Log.Error("Collaboration discovery: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error));
        }
    }

    public void StopDiscovery()
    {
        discovery?.Dispose();
        discovery = null;
    }

    private void PollConnectivity()
    {
        discovery?.Poll();
        if (discovery != null && server == null && wire == null && connecting == null)
        {
            discovery.SendQuery();
        }
        stun?.Poll();
        FoldPublicEndpoint();
        if (relayHost != null && inviteCarriesRelay && relayHost.State == RelayHostState.Failed && inviteCodeValue != null)
        {
            // A code that names a relay this host has given up on would send friends to a room that will
            // never exist. Re-issue it without the relay so the direct routes are all it promises.
            inviteCarriesRelay = false;
            inviteCodeValue = BuildInviteCode(hostPort);
        }
        upnp?.Poll();
        if (upnp != null && upnp.Finished)
        {
            upnp.Dispose();
            upnp = null;
        }
    }

    private void FoldPublicEndpoint()
    {
        if (stun != null && inviteCodeValue != null && !stunEndpointFolded && (stun.PublicEndPoint != null || stun.Failure != null))
        {
            stunEndpointFolded = true;
            if (stun.PublicEndPoint != null)
            {
                PublicEndpointStatus = "Public endpoint: " + stun.PublicEndPoint;
                inviteCodeValue = BuildInviteCode(hostPort);
            }
            else
            {
                PublicEndpointStatus = CollaborationDiagnostics.Bounded("No public address found; invitees on this network join by code or LAN discovery, and a remote invitee needs the host's address under Advanced.");
            }
        }
    }

    private void TeardownConnectivity()
    {
        StopAdvertising();
        upnp?.Dispose();
        upnp = null;
        stun?.Dispose();
        stun = null;
        relayHost?.Dispose();
        relayHost = null;
        inviteCarriesRelay = false;
        inviteCodeValue = null;
        hostSecret = null;
        hostPort = 0;
        PublicEndpointStatus = null;
        stunEndpointFolded = false;
        StopDiscovery();
    }

    private void StopAdvertising()
    {
        discovery?.StopHost();
        upnp?.Unmap();
        // A failed or ended session must not keep taking guests through the relay either.
        relayHost?.Dispose();
    }

    private PlayModeTransitionResult ReconnectGuard()
    {
        CollaborationTransportState value = CollaborationTransportStateRules.Detect(wire != null, Connected && Error == null, wire?.StoredFailure != null || Error != null, connecting != null);
        return lifecycle.ValidateReconnect(new SessionReconnectGuardRequest(RuntimeLifecycle, baseline != null, connecting != null, wire != null, lifecycle.IsPending, lifecycle.HasTerminalFailure, lifecycle.TerminalDiagnostic, Connected && Error == null, value, sourceBaseline == SceneAdapter.Baseline(includeScenes: false), endedByLeave, IsHost));
    }

    public void Reconnect(string address, int port)
    {
        if (!recoverArming)
        {
            recoverArmed = false;
        }
        if (!reconnectQueued && reconnectTokenTask == null)
        {
            PlayModeTransitionResult playModeTransitionResult = ReconnectGuard();
            if (!playModeTransitionResult.Accepted)
            {
                throw new ActionRefusal(playModeTransitionResult.ActionableError ?? "Recovery is not possible in the session's current state.");
            }
            reconnectRecovery = true;
            reconnectRetrySpent = false;
            reconnectAddress = activeAddress ?? address;
            reconnectPort = ((activePort != 0) ? activePort : port);
            reconnectQueued = true;
            Status = "Reconnecting; preparing identity…";
            RequestReconnectToken();
        }
    }

    private void RequestReconnectToken()
    {
        if (reconnectTokenTask != null)
        {
            return;
        }
        try
        {
            reconnectTokenTask = Auth.GetToken("sbox-editor-collaboration");
        }
        catch (Exception)
        {
            AbandonReconnect("A Steam identity token could not be requested.");
        }
    }

    private void PollReconnect()
    {
        if (reconnectTokenTask != null)
        {
            if (!reconnectTokenTask.IsCompleted)
            {
                return;
            }
            string text = null;
            try
            {
                text = reconnectTokenTask.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }
            finally
            {
                reconnectTokenTask = null;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                AbandonReconnect("A Steam identity token could not be obtained.");
            }
            else if (reconnectQueued)
            {
                RunReconnect(text);
            }
        }
        else if (reconnectQueued)
        {
            AbandonReconnect("A Steam identity token could not be obtained.");
        }
        else if (reconnectRecovery && !reconnectRetrySpent && Error != null && SteamIdentityAdmission.IsTokenRejection(Error))
        {
            reconnectRetrySpent = true;
            ClearRecordedFailure();
            reconnectQueued = true;
            Status = "Reconnecting; preparing identity…";
            RequestReconnectToken();
        }
    }

    private void AbandonReconnect(string reason)
    {
        reconnectQueued = false;
        reconnectTokenTask = null;
        reconnectRecovery = false;
        ClearRecordedFailure();
        Fail(reason);
    }

    private void RunReconnect(string freshToken)
    {
        reconnectQueued = false;
        PlayModeTransitionResult playModeTransitionResult = ReconnectGuard();
        if (!playModeTransitionResult.Accepted)
        {
            AbandonReconnect(playModeTransitionResult.ActionableError ?? "Reconnect is not possible in the session's current state.");
            return;
        }
        SessionReconnectCommitOutcome sessionReconnectCommitOutcome = lifecycle.BeginReconnect(new SessionReconnectCommitRequest(playModeTransitionResult, () =>
        {
            Preserve("Reconnect: local state before authoritative recovery");
            return DisposeActiveResources();
        }));
        if (!sessionReconnectCommitOutcome.Accepted)
        {
            AbandonReconnect((sessionReconnectCommitOutcome.Stage == ReconnectCommitStage.FailedDuringPrepare) ? (sessionReconnectCommitOutcome.ActionableError ?? "Reconnect preparation failed after the previous connection was closed.") : (sessionReconnectCommitOutcome.ActionableError ?? "Reconnect was refused before any connection was closed."));
            return;
        }
        Error = null;
        Connected = false;
        restoring = true;
        foreach (Tracked value in scenes.Values)
        {
            value.Ready = false;
            if (value.Session.Scene.IsValid())
            {
                value.Session.Reload();
                value.Settings = value.Session.Scene.SerializeProperties();
                value.DiskStamp = SceneAdapter.DiskStamp(SceneAdapter.PathOf(value.Session));
            }
        }
        steamTokenValue = freshToken;
        connecting = (activeCandidates != null && activeCandidates.Count > 0)
            ? ConnectCandidates(activeCandidates, activeSessionKey, activeSecret)
            : Wire.Connect(reconnectAddress, reconnectPort, activeSessionKey, activeSessionKey != null);
        Status = "Reconnecting; recovering authoritative state…";
    }

    public void Leave()
    {
        PlayModeTransitionResult playModeTransitionResult = lifecycle.ValidateLeave(new SessionLeaveGuardRequest(RuntimeLifecycle.IsTransitionInProgress));
        if (!playModeTransitionResult.Accepted)
        {
            throw new InvalidOperationException(playModeTransitionResult.ActionableError);
        }
        SceneEditorSession active = SceneEditorSession.Active;
        if (active == null || !active.IsPlaying)
        {
            Checkpoint();
        }
        bool archiveFailed = RuntimeLifecycle.State == PlayModeLifecycleState.RuntimeBlocked && ArchiveHotloadLifecycle(RuntimeLifecycle.ArchiveReason ?? "hotloaded") == HotloadArchiveOutcome.Failed;
        SessionLeaveOutcome sessionLeaveOutcome = SessionLeaveRules.Run(new SessionLeaveRequest(lifecycle, RuntimeLifecycle.IsTransitionInProgress, () => !archiveFailed && scenes.Values.Any((Tracked x) => x.Pending != null || (x.Ready && x.Session.Scene.IsValid() && StateRules.Diff(x.Known, SceneAdapter.Capture(x.Session.Scene)).Count > 0)), (string reason) =>
        {
            Preserve(reason);
            return true;
        }, () =>
        {
            wire?.Send(new Packet
            {
                Kind = "leave"
            });
        }, DisposeActiveResources));
        if (!sessionLeaveOutcome.Accepted)
        {
            throw new InvalidOperationException(sessionLeaveOutcome.ActionableError);
        }
        reconnectQueued = false;
        reconnectTokenTask = null;
        reconnectRecovery = false;
        reconnectRetrySpent = false;
        heldPackets.Clear();
        PlayDecision = PlaySpectateDecision.Normal;
        endedByLeave = true;
        ClearRecordedFailure();
        ActionNotice = null;
        actionNoticeExpires = default;
        ClearNativeUndoMirrors();
        recoverArmed = false;
        recoverAttempts = 0;
        recoverNextAttempt = default;
        recoverPromptShown = false;
        recoverDeclined = false;
        bool disposalFailed = sessionLeaveOutcome.DisposalFailed;
        if (sessionLeaveOutcome.PreserveFailed && !disposalFailed)
        {
            RecoveryNotice = sessionLeaveOutcome.Failure;
            GlobalGameNamespace.Log.Error("Collaboration lifecycle: " + RecoveryNotice);
        }
        Connected = false;
        Roster.Clear();
        foreach (Tracked value in scenes.Values)
        {
            value.Ready = false;
        }
        bool requiresHotloadRecoveryAfterLeave = RuntimeLifecycle.RequiresHotloadRecoveryAfterLeave;
        if (connecting != null)
        {
            BeginConnectionTeardown(requiresHotloadRecoveryAfterLeave);
        }
        if (disposalFailed)
        {
            Status = "Offline • connection teardown failed; session remains fail-stop";
            RecordTerminalTeardownFailure(lifecycle.TerminalDiagnostic);
            return;
        }
        if (archiveFailed)
        {
            Status = "Offline • hotload recovery failed";
            RecoveryNotice = BoundedDiagnostic(RuntimeLifecycle.LastDiagnostic);
            GlobalGameNamespace.Log.Error("Collaboration lifecycle: " + RecoveryNotice);
            return;
        }
        if (lifecycle.IsPending)
        {
            Status = (requiresHotloadRecoveryAfterLeave ? "Offline • waiting for connection teardown before hotload recovery" : "Offline • waiting for pending connection teardown");
            return;
        }
        if (lifecycle.HasTerminalFailure)
        {
            Status = "Offline • connection teardown failed; runtime remains blocked";
            return;
        }
        Status = "Offline";
        string error = null;
        if (requiresHotloadRecoveryAfterLeave)
        {
            CompleteHotloadRecovery(out error);
        }
        if (error != null)
        {
            SetLifecycleError(error);
        }
    }

    public void Fail(string error, SessionFailureClass classification = SessionFailureClass.Terminal)
    {
        if (Error != null)
        {
            return;
        }
        string recoveryWriteFailure = null;
        bool everConnected = hostSession != null;
        if (CollaborationFailureRules.ShouldArchive(everConnected, scenes.Values.Any((Tracked x) => x.Pending != null)))
        {
            try
            {
                Preserve(error);
            }
            catch (Exception error2)
            {
                recoveryWriteFailure = CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("", error2);
            }
        }
        CollaborationFailureOutcome collaborationFailureOutcome = CollaborationFailureRules.Compose(Error, error, recoveryWriteFailure, everConnected);
        Error = collaborationFailureOutcome.Error;
        Status = collaborationFailureOutcome.Status;
        errorClass = classification;
        GlobalGameNamespace.Log.Error("Collaboration: " + Error);
        try
        {
            StopAdvertising();
        }
        catch (Exception error3)
        {
            GlobalGameNamespace.Log.Error("Collaboration connectivity: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error3));
        }
    }

    public void ReportAction(Exception error)
    {
        if (error != null)
        {
            if (!ActionRefusalRules.IsRefusal(error))
            {
                Fail(error.Message);
            }
            else
            {
                SetActionNotice(error.Message);
            }
        }
    }

    public void ClearRecordedFailure()
    {
        if (Error != null && lifecycle.TerminalDiagnostic == null)
        {
            Error = null;
            errorClass = SessionFailureClass.Terminal;
        }
    }

    private HotloadArchiveOutcome ArchiveHotloadLifecycle(string reason)
    {
        if (RuntimeLifecycle.State != PlayModeLifecycleState.RuntimeBlocked)
        {
            return HotloadArchiveOutcome.NotApplicable;
        }
        try
        {
            Preserve("Runtime lifecycle hotload marker: " + reason);
            PlayModeTransitionResult playModeTransitionResult = lifecycle.ApplyArchiveMarker(RuntimeLifecycle.RecordHotloadArchive());
            if (!playModeTransitionResult.Accepted)
            {
                RecoveryNotice = BoundedDiagnostic(RuntimeLifecycle.LastActionableError);
                GlobalGameNamespace.Log.Error("Collaboration lifecycle: " + RecoveryNotice);
                return HotloadArchiveOutcome.Failed;
            }
            RecoveryNotice = BoundedDiagnostic("Runtime lifecycle blocked; recovery archived before connections were disposed. Leave, sync source in Git, and start a new session.");
            return HotloadArchiveOutcome.Archived;
        }
        catch (Exception error)
        {
            RuntimeLifecycle.RecordHotloadArchiveFailure(error);
            lifecycle.ApplyArchiveMarker(PlayModeTransitionResult.Rejected("archive failed"));
            RecoveryNotice = BoundedDiagnostic(RuntimeLifecycle.LastDiagnostic);
            GlobalGameNamespace.Log.Error("Collaboration lifecycle: " + RecoveryNotice);
            return HotloadArchiveOutcome.Failed;
        }
    }

    private bool DisposeActiveResources()
    {
        CancellationTokenSource cancellationTokenSource = hostVerificationLifetime;
        hostVerificationLifetime = new CancellationTokenSource();
        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        try
        {
            cancellationTokenSource.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        ResourceDisposalResult resourceDisposalResult = ResourceDisposalRules.DisposePair(wire, "collaboration transport", server, "collaboration host server");
        wire = null;
        server = null;
        Connected = false;
        try
        {
            TeardownConnectivity();
        }
        catch (Exception error)
        {
            GlobalGameNamespace.Log.Error("Collaboration connectivity: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error));
        }
        if (resourceDisposalResult.Succeeded)
        {
            return true;
        }
        lifecycle.RecordTerminalTeardownFailure(resourceDisposalResult.Failure);
        return false;
    }

    private void BeginConnectionTeardown(bool recoverWhenComplete)
    {
        Task<Wire> pendingConnect = connecting;
        connecting = null;
        ConnectionTeardownResult connectionTeardownResult = lifecycle.BeginDetachedTeardown(pendingConnect, recoverWhenComplete);
        if (connectionTeardownResult.Outcome == ConnectionTeardownOutcome.TerminalFailure)
        {
            RecordTerminalTeardownFailure(connectionTeardownResult.Failure);
        }
    }

    private void RecordTerminalTeardownFailure(string failure)
    {
        bool requiresHotloadRecoveryAfterLeave = RuntimeLifecycle.RequiresHotloadRecoveryAfterLeave;
        bool flag = !string.IsNullOrWhiteSpace(failure) && failure.StartsWith("Connection teardown failed;", StringComparison.Ordinal);
        string text = (string.IsNullOrWhiteSpace(failure) ? "unknown teardown error" : BoundedDiagnostic(failure));
        if (requiresHotloadRecoveryAfterLeave && !flag)
        {
            RuntimeLifecycle.RecordHotloadTeardownFailure(text);
        }
        string text2 = (RecoveryNotice = BoundedDiagnostic(requiresHotloadRecoveryAfterLeave ? (RuntimeLifecycle.LastActionableError ?? text) : (flag ? text : ("Connection teardown failed; this editor session remains offline and fail-stop: " + text))));
        if (requiresHotloadRecoveryAfterLeave)
        {
            SetLifecycleError(text2);
        }
        else
        {
            GlobalGameNamespace.Log.Error("Collaboration lifecycle: " + text2);
        }
    }

    private void CompleteHotloadRecovery(out string error)
    {
        error = null;
        if (!RuntimeLifecycle.RequiresHotloadRecoveryAfterLeave || !lifecycle.LeaveRequested)
        {
            return;
        }
        TeardownCompletionObservation teardownCompletionObservation = lifecycle.ObserveTeardownCompletion(requiresHotloadRecovery: true, connecting != null);
        if (!teardownCompletionObservation.Observed)
        {
            return;
        }
        if (teardownCompletionObservation.TeardownFailed)
        {
            RecordTerminalTeardownFailure(teardownCompletionObservation.TeardownFailure);
            return;
        }
        if (!teardownCompletionObservation.AttemptRecovery)
        {
            if (teardownCompletionObservation.Status == HotloadTeardownRecoveryStatus.ArchiveFailed && teardownCompletionObservation.Error != null)
            {
                RecoveryNotice = BoundedDiagnostic(teardownCompletionObservation.Error);
                GlobalGameNamespace.Log.Error("Collaboration lifecycle: " + RecoveryNotice);
            }
            return;
        }
        if (!RuntimeLifecycle.HotloadArchiveRecorded && RuntimeLifecycle.State == PlayModeLifecycleState.RuntimeBlocked && ArchiveHotloadLifecycle(RuntimeLifecycle.ArchiveReason ?? "hotloaded") == HotloadArchiveOutcome.Failed)
        {
            error = RecoveryNotice;
            return;
        }
        PlayModeTransitionResult playModeTransitionResult = RuntimeLifecycleEvents.RecoverFromHotload(RuntimeLifecycle);
        if (playModeTransitionResult.Accepted)
        {
            lifecycle.ClearDeferredHotloadRecovery();
            RecoveryNotice = "Hotload recovery reset completed after transport teardown and Leave. Sync source in Git, then start a new collaboration session.";
        }
        else
        {
            error = playModeTransitionResult.ActionableError;
        }
    }

    private void Preserve(string reason)
    {
        string text = Path.Combine(SceneAdapter.Root, ".collaboration", "recovery");
        Directory.CreateDirectory(text);
        string path = RecoveryPathRules.CreatePath(text, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
        var captures = scenes.Where((KeyValuePair<string, Tracked> x) => x.Value.Session.Scene.IsValid()).ToDictionary((KeyValuePair<string, Tracked> x) => x.Key, (KeyValuePair<string, Tracked> x) => new
        {
            Live = x.Value.Session.Scene.Serialize(),
            Pending = x.Value.Pending,
            Known = x.Value.Known
        });
        Packet[] pending = (from x in scenes.Values
            where x.Pending != null
            select x.Pending).ToArray();
        RecoveryPathOutcome recoveryPathOutcome = RecoveryPathRules.PublishAfterWrite(path, () =>
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { reason, captures, pending }));
        });
        if (!recoveryPathOutcome.Succeeded)
        {
            throw new IOException("Recovery archive could not be written to " + path);
        }
        RecoveryPath = recoveryPathOutcome.Published;
    }

    [EditorEvent.Frame]
    public static void PumpCurrent()
    {
        Current.Frame();
    }

    public void Frame()
    {
        if (focusNoticeExpires != default(DateTime) && DateTime.UtcNow >= focusNoticeExpires)
        {
            FocusNotice = null;
            focusNoticeExpires = default;
        }
        if (saveNoticeExpires != default(DateTime) && DateTime.UtcNow >= saveNoticeExpires)
        {
            SaveNotice = null;
            saveNoticeExpires = default;
        }
        if (undoNoticeExpires != default(DateTime) && DateTime.UtcNow >= undoNoticeExpires)
        {
            UndoNotice = null;
            undoNoticeExpires = default;
        }
        if (actionNoticeExpires != default(DateTime) && DateTime.UtcNow >= actionNoticeExpires)
        {
            ActionNotice = null;
            actionNoticeExpires = default;
        }
        try
        {
            try
            {
                PollReconnect();
            }
            catch (Exception error)
            {
                AbandonReconnect(CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("Reconnect could not continue: ", error));
            }
            server?.Pump();
            try
            {
                orgDirectory?.Poll();
                PollPackageFetch();
                if (DateTime.UtcNow >= nextReadiness || (orgDirectory?.Revision ?? (-1)) != readinessOrgRevision)
                {
                    RefreshReadiness();
                }
            }
            catch (Exception error2)
            {
                GlobalGameNamespace.Log.Error("Collaboration readiness: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error2));
            }
            try
            {
                PollConnectivity();
            }
            catch (Exception error3)
            {
                GlobalGameNamespace.Log.Error("Collaboration connectivity: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error3));
            }
            try
            {
                TickIdleAutoSave();
            }
            catch (Exception error4)
            {
                GlobalGameNamespace.Log.Error("Collaboration auto-save: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error4));
            }
            try
            {
                TickAutoRecover();
            }
            catch (Exception error5)
            {
                GlobalGameNamespace.Log.Error("Collaboration auto-recover: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error5));
            }
            TeardownCompletionObservation teardownCompletionObservation = lifecycle.ObserveTeardownCompletion(RuntimeLifecycle.RequiresHotloadRecoveryAfterLeave, connecting != null);
            if (teardownCompletionObservation.Observed)
            {
                if (teardownCompletionObservation.TeardownFailed)
                {
                    RecordTerminalTeardownFailure(teardownCompletionObservation.TeardownFailure);
                }
                else if (teardownCompletionObservation.AttemptRecovery)
                {
                    CompleteHotloadRecovery(out var error6);
                    if (error6 != null)
                    {
                        SetLifecycleError(error6);
                    }
                }
                else if (teardownCompletionObservation.Error != null && RuntimeLifecycle.RequiresHotloadRecoveryAfterLeave)
                {
                    RecoveryNotice = BoundedDiagnostic(teardownCompletionObservation.Error);
                    GlobalGameNamespace.Log.Error("Collaboration lifecycle: " + RecoveryNotice);
                }
            }
            Task<Wire> task = connecting;
            if (task != null && task.IsCompleted)
            {
                SceneEditorSession sceneEditorSession = AuthoringSource();
                string sourceScenePath = SceneAdapter.PathOf(sceneEditorSession);
                bool flag = PlayModeSourceCoexistence.CanProcessSourcePackets(sceneEditorSession?.IsPlaying ?? false, sceneEditorSession, sourceScenePath, RuntimeLifecycle, CurrentRuntimeFor(sceneEditorSession));
                if (!ConnectionPromotionRules.ShouldPromote(flag, lifecycle.CanPromoteConnection))
                {
                    BeginConnectionTeardown(recoverWhenComplete: false);
                    if (!flag)
                    {
                        throw new InvalidOperationException("Stop play mode or complete the approved runtime lifecycle before promoting a source connection");
                    }
                    return;
                }
                Task<Wire> task2 = connecting;
                connecting = null;
                wire = task2.GetAwaiter().GetResult();
                wire.Send(new Packet
                {
                    Kind = "hello",
                    ProtocolVersion = 4,
                    Mode = CollaborationMode.Source,
                    Peer = Peer,
                    Project = project,
                    Baseline = baseline,
                    Name = userName,
                    Token = sessionToken,
                    SteamId = steamIdValue,
                    SteamToken = steamTokenValue,
                    OrgIdent = orgIdentValue,
                    LibraryVersion = libraryVersionValue,
                    ProjectManifest = projectManifestValue
                });
                lastReceive = DateTime.UtcNow;
            }
            if (wire == null)
            {
                return;
            }
            if (wire.StoredFailure != null)
            {
                Connected = false;
                throw new IOException("Disconnected: " + wire.StoredFailure);
            }
            SceneEditorSession sceneEditorSession2 = AuthoringSource();
            string sourceScenePath2 = SceneAdapter.PathOf(sceneEditorSession2);
            bool sourceAdmissible = PlayModeSourceCoexistence.CanProcessSourcePackets(sceneEditorSession2?.IsPlaying ?? false, sceneEditorSession2, sourceScenePath2, RuntimeLifecycle, CurrentRuntimeFor(sceneEditorSession2));
            PlaySpectateDecision decision = PlaySpectateRules.Decide(IsLocalPlaying(sceneEditorSession2), RuntimeLifecycle.State, sourceAdmissible, PlayModeSourceCoexistence.IsUsablePathKey(sourceScenePath2));
            if (decision != PlayDecision && decision == PlaySpectateDecision.Normal && !heldPackets.IsEmpty)
            {
                Status = "Connected • applying changes made while you were playing";
            }
            PlayDecision = decision;
            if (decision == PlaySpectateDecision.FailStop)
            {
                throw new InvalidOperationException("Stop play mode or complete the approved runtime lifecycle before applying source packets");
            }
            Packet result;
            if (decision == PlaySpectateDecision.Normal)
            {
                // Changes held while playing come first so they land in the order the host accepted them.
                while (heldPackets.TryTake(out result))
                {
                    ProcessIncoming(result);
                }
            }
            while (wire.Incoming.TryDequeue(out result))
            {
                if (decision != PlaySpectateDecision.Normal && HeldPacketBuffer.IsSceneChange(result.Kind))
                {
                    if (!heldPackets.TryHold(result))
                    {
                        throw new InvalidOperationException("Too many scene changes arrived while you were playing. Stop play mode, then use Recover & reconnect to catch up.");
                    }
                    lastReceive = DateTime.UtcNow;
                    continue;
                }
                ProcessIncoming(result);
            }
            if (DateTime.UtcNow - lastReceive > TimeSpan.FromSeconds(15L))
            {
                Connected = false;
                throw new IOException("Host heartbeat timed out");
            }
            if (!Connected)
            {
                return;
            }
            if (DateTime.UtcNow >= nextPresence)
            {
                // Polled every 100 ms as before, but only SENT when something changed or a refresh is due (SendCadence). Every packet an editor
                // sends is one the relay receives and a shared free relay is billed per message; an idle session used to send fifty a second.
                nextPresence = DateTime.UtcNow.AddMilliseconds(SendCadence.PresenceMinimum.TotalMilliseconds);
                SendPresence(force: false);
                if (SendCadence.ShouldSendPing(DateTime.UtcNow - lastPingSent))
                {
                    lastPingSent = DateTime.UtcNow;
                    Packet ping = new Packet
                    {
                        Kind = "ping"
                    };
                    AttachSceneDigest(ping);
                    wire.Send(ping);
                    ShowDriftNoticeOnce();
                }
            }
            if (Error != null || DateTime.UtcNow < nextScan)
            {
                return;
            }
            if (decision != PlaySpectateDecision.Normal)
            {
                // Playing runs a copy of the scene. Sampling it would report the game's own state changes as edits,
                // so nothing is sampled until play stops; presence and the heartbeat above keep flowing.
                return;
            }
            nextScan = DateTime.UtcNow.AddMilliseconds(50.0);
            SceneEditorSession[] array = SceneEditorSession.All.Where((SceneEditorSession x) => !(x is GameEditorSession) && !x.IsPrefabSession).ToArray();
            foreach (SceneEditorSession sceneEditorSession3 in array)
            {
                string text = SceneAdapter.PathOf(sceneEditorSession3);
                if (sceneEditorSession3.IsPlaying && !PlayModeSourceCoexistence.CanSampleAuthoringSource(sceneEditorSession3, text, RuntimeLifecycle, CurrentRuntimeFor(sceneEditorSession3)))
                {
                    throw new InvalidOperationException("Stop play mode before editing collaboratively");
                }
                if (text == null || !text.StartsWith("scenes/"))
                {
                    continue;
                }
                if (!scenes.TryGetValue(text, out var value) || value.Session != sceneEditorSession3)
                {
                    if (value?.Pending != null)
                    {
                        throw new InvalidOperationException("Scene closed with an edit awaiting acknowledgement");
                    }
                    if (sceneEditorSession3.HasUnsavedChanges)
                    {
                        throw new InvalidOperationException("Newly opened scene has local unsaved edits; recover before subscribing");
                    }
                    value = new Tracked
                    {
                        Session = sceneEditorSession3,
                        Known = SceneAdapter.Capture(sceneEditorSession3.Scene),
                        Settings = sceneEditorSession3.Scene.SerializeProperties(),
                        DiskStamp = SceneAdapter.DiskStamp(text)
                    };
                    scenes[text] = value;
                    value.LastOrder = value.Known;
                    wire.Send(new Packet
                    {
                        Kind = "subscribe",
                        Scene = text,
                        Objects = value.Known.Values.ToList()
                    });
                }
                else
                {
                    if (!value.Ready || value.Pending != null)
                    {
                        continue;
                    }
                    DateTime dateTime = SceneAdapter.DiskStamp(text);
                    bool flag2 = dateTime == value.DiskStamp;
                    TimeSpan mismatchFor;
                    if (flag2)
                    {
                        mismatchFor = TimeSpan.Zero;
                        value.StampMismatchSince = default;
                    }
                    else if (value.StampMismatchSince == default(DateTime))
                    {
                        value.StampMismatchSince = DateTime.UtcNow;
                        mismatchFor = TimeSpan.Zero;
                    }
                    else
                    {
                        mismatchFor = DateTime.UtcNow - value.StampMismatchSince;
                    }
                    if (SceneSaveRules.ShouldFailOnDiskStamp(flag2, mismatchFor))
                    {
                        value.StampMismatchSince = default;
                        throw new InvalidOperationException("Scene file changed outside Collaboration Save. Local state retained; leave and reconcile the file in Git.");
                    }
                    if (!JsonNode.DeepEquals(value.Settings, sceneEditorSession3.Scene.SerializeProperties()))
                    {
                        throw new InvalidOperationException("Scene settings/metadata/navmesh changes are outside the supported edit set. Local changes retained.");
                    }
                    Dictionary<string, ObjectState> dictionary = SceneAdapter.Capture(sceneEditorSession3.Scene);
                    foreach (string item in value.Known.Keys.Except(dictionary.Keys))
                    {
                        GameObject gameObject = sceneEditorSession3.Scene.Directory.FindByGuid(Guid.Parse(item));
                        if (gameObject != null && gameObject.IsValid && !gameObject.IsDestroyed)
                        {
                            throw new InvalidOperationException("Object became non-serializable instead of being deleted; edit is outside the supported set");
                        }
                    }
                    SceneAdapter.ValidateSiblingOrder(value.LastOrder, dictionary);
                    value.LastOrder = dictionary;
                    List<Change> list = StateRules.Diff(value.Known, dictionary);
                    if (list.Count > 0)
                    {
                        lastLocalEdit = DateTime.UtcNow;
                    }
                    bool flag3 = continuousEdits == 0 && !Editor.Application.MouseButtons.HasFlag(MouseButtons.Left);
                    if (list.Count == 0 && (!flag3 || value.Group.Count == 0))
                    {
                        continue;
                    }
                    foreach (Change item2 in list)
                    {
                        StateRules.ValidateChange(item2);
                    }
                    SendEdit(text, value, list, flag3);
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex.Message, AutoRecoveryRules.Classify(ex));
        }
    }

    private void ProcessIncoming(Packet result)
    {
        string kind = result.Kind;
        if (HeldPacketBuffer.IsSceneChange(kind))
        {
            Dictionary<string, object> trackedByScene = ((IEnumerable<KeyValuePair<string, Tracked>>)scenes).ToDictionary((Func<KeyValuePair<string, Tracked>, string>)((KeyValuePair<string, Tracked> pair) => pair.Key), (Func<KeyValuePair<string, Tracked>, object>)((KeyValuePair<string, Tracked> pair) => pair.Value));
            ScenePacketRouteResult scenePacketRouteResult = ScenePacketRouting.RouteTrackedPacket(result.Kind, result.Scene, trackedByScene, (object target) => ((Tracked)target).Session.IsPlaying, (object target) => SceneAdapter.PathOf(((Tracked)target).Session), RuntimeLifecycle, (object target) => CurrentRuntimeFor(((Tracked)target).Session));
            if (!scenePacketRouteResult.Admitted)
            {
                throw new InvalidOperationException(scenePacketRouteResult.Error ?? "Scene packet was rejected by routing admission.");
            }
        }
        lastReceive = DateTime.UtcNow;
        // Recorded here and not in Receive so that every packet that carries a revision is counted, whichever branch handles it.
        if (result.Kind == "snapshot")
        {
            lastRevision = result.Revision;
        }
        else if (result.Kind == "accepted" && result.Revision > lastRevision)
        {
            lastRevision = result.Revision;
        }
        Receive(result);
    }

    // Play can be running in the editor's own game session or as a flag on the source scene session; either means
    // the scene in front of the user is a copy being played, not the one being authored.
    private static bool IsLocalPlaying(SceneEditorSession source)
    {
        return (source?.IsPlaying ?? false) || SceneEditorSession.All.Any((SceneEditorSession x) => x is GameEditorSession);
    }

    private static CollaborationConfig LoadConfig()
    {
        string error;
        return CollaborationConfig.LoadFrom(Path.Combine(SceneAdapter.Root, "ProjectSettings", "collaboration.json"), out error);
    }

    private void UseConfig(CollaborationConfig config)
    {
        sessionConfig = config;
        projectIdResolved = false;
        if (orgDirectory == null || !string.Equals(orgDirectory.Org, config.Org, StringComparison.OrdinalIgnoreCase))
        {
            orgDirectory = new OrgDirectory(config);
        }
    }

    private void RefreshReadiness()
    {
        nextReadiness = DateTime.UtcNow.AddSeconds(1.0);
        CollaborationConfig collaborationConfig = LoadConfig();
        UseConfig(collaborationConfig);
        readinessOrgRevision = orgDirectory.Revision;
        orgDirectory.Poll();
        PollPackageFetch();
        bool flag = LocalSteamIdentity.TryGet(out var identity, out var personaName, out var _);
        string text = SceneAdapter.PathOf(SupportedActiveScene);
        (bool, Version) tuple = InstalledLibrary(collaborationConfig);
        Readiness = ReadinessRules.Evaluate(new ReadinessInput(flag, flag ? personaName : null, text != null, Project.Current?.IsPublished ?? false, packageReachable == true, tuple.Item1, tuple.Item2, collaborationConfig.LibraryVersion, flag && orgDirectory.Contains(SteamIdentityRules.Canonical(identity)), collaborationConfig, text));
    }

    private static (bool Installed, Version Version) InstalledLibrary(CollaborationConfig config)
    {
        foreach (LibraryProject item in LibrarySystem.All)
        {
            if (!LibraryInfo.IsThisLibrary(config?.LibraryIdent, item.Project?.Config?.Ident))
            {
                continue;
            }
            // Asset Party stamps its own build number into the installed copy's .version file (1.0.<build number>), so item.Version is a build id and can
            // never be compared with the release version the handshake and the settings file use. The code running here is the installed copy.
            Version.TryParse(LibraryInfo.LibraryVersion, out Version result);
            return (Installed: true, Version: result);
        }
        return (Installed: false, Version: null);
    }

    private void PollPackageFetch()
    {
        if (packageFetch != null)
        {
            if (packageFetch.IsCompleted)
            {
                try
                {
                    packageReachable = packageFetch.GetAwaiter().GetResult() != null;
                }
                catch (Exception)
                {
                    packageReachable = false;
                }
                packageFetch = null;
                nextPackageFetch = DateTime.UtcNow.AddSeconds(30.0);
            }
            return;
        }
        bool? flag = packageReachable;
        if (flag.HasValue && DateTime.UtcNow < nextPackageFetch)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(Project.Current?.Config?.FullIdent))
        {
            packageReachable = false;
            return;
        }
        try
        {
            packageFetch = Package.FetchAsync(Project.Current.Config.FullIdent, partial: false);
        }
        catch (Exception)
        {
            packageReachable = false;
            nextPackageFetch = DateTime.UtcNow.AddSeconds(30.0);
        }
    }

    private PeerFacts BuildPeerFacts(Packet hello)
    {
        string steamId = (SteamIdentityRules.TryParse(hello.SteamId, out var identity) ? SteamIdentityRules.Canonical(identity) : hello.SteamId);
        bool isFriend = false;
        if (SteamIdentityRules.TryParse(hello.SteamId, out var identity2))
        {
            try
            {
                isFriend = Steam.IsFriend(identity2.Value);
            }
            catch (Exception)
            {
                isFriend = false;
            }
        }
        bool isHostSelf = SteamIdentityRules.TryParse(hello.SteamId, out var identity3) && LocalSteamIdentity.TryGet(out var identity4, out var _, out var _) && string.Equals(SteamIdentityRules.Canonical(identity3), SteamIdentityRules.Canonical(identity4), StringComparison.Ordinal);
        bool isOrgMember = orgDirectory?.Contains(steamId) ?? false;
        if (SoloMayVouch(hello))
        {
            // The reserved test identity is neither a Steam friend nor a member of the organization; in a
            // disposable copy the host treats it as both so the remaining gates (project, baseline, library
            // version, manifest, duplicate account) still run for real.
            isFriend = true;
            isOrgMember = true;
        }
        return new PeerFacts(steamId, IdentityVerified: false, isFriend, isOrgMember, hello.LibraryVersion, hello.ProjectManifest, hello.OrgIdent, isHostSelf);
    }

    private static SceneEditorSession AuthoringSource()
    {
        SceneEditorSession active = SceneEditorSession.Active;
        if (1 == 0)
        {
        }
        SceneEditorSession result;
        if (active is GameEditorSession { Parent: var parent })
        {
            result = ((parent != null && !parent.IsPrefabSession) ? parent : null);
        }
        else
        {
            result = ((active == null || active.IsPrefabSession) ? null : active);
        }
        if (1 == 0)
        {
        }
        return result;
    }

    private RuntimeSessionDescriptor CurrentRuntimeFor(SceneEditorSession source)
    {
        if (source == null || !source.IsPlaying)
        {
            return null;
        }
        GameEditorSession[] array = SceneEditorSession.All.OfType<GameEditorSession>().ToArray();
        if (array.Length != 1)
        {
            return null;
        }
        GameEditorSession gameEditorSession = array[0];
        if (gameEditorSession.Parent != source)
        {
            return null;
        }
        return RuntimeSceneAdapter.Describe(gameEditorSession);
    }

    private void SendEdit(string scene, Tracked tracked, List<Change> changes, bool final)
    {
        tracked.Pending = new Packet
        {
            Kind = "edit",
            Scene = scene,
            Peer = Peer,
            Changes = changes,
            Final = final
        };
        StorePending(tracked.Pending);
        wire.Send(tracked.Pending);
        Status = "Connected • edit awaiting acknowledgement";
    }

    private string PendingPath(string id)
    {
        return Path.Combine(SceneAdapter.Root, ".collaboration", "outbox", Peer + "-" + id + ".json");
    }

    private void StorePending(Packet packet)
    {
        string text = PendingPath(packet.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(text));
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { project, baseline, packet }));
        using (FileStream fileStream = new FileStream(text + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fileStream.Write(bytes);
            fileStream.Flush(flushToDisk: true);
        }
        File.Move(text + ".tmp", text, overwrite: true);
    }

    private void ForgetPending(Tracked tracked, string id)
    {
        if (!(tracked.Pending?.Id != id))
        {
            File.Delete(PendingPath(id));
            tracked.Pending = null;
        }
    }

    // Adds the scene digest to a ping for the active scene, when there is something trustworthy to report. The digest is of Known, the state the
    // host last confirmed, and not of the live scene: a drag that has not been sent yet is an edit in progress, never drift. Nothing is reported
    // while an edit awaits acknowledgement, while the scene is still joining, or during play, when the scene on screen is a copy.
    private void AttachSceneDigest(Packet ping)
    {
        try
        {
            SceneEditorSession active = SceneEditorSession.Active;
            string path = SceneAdapter.PathOf(active);
            if (path == null || !scenes.TryGetValue(path, out var tracked) || tracked.Session != active)
            {
                return;
            }
            if (!tracked.Ready || tracked.Pending != null || IsLocalPlaying(active))
            {
                return;
            }
            string digest = SceneDigest.Compute(tracked.Known);
            ping.Digest = digest;
            ping.DigestRevision = lastRevision;
            ping.DigestScene = path;
        }
        catch (Exception)
        {
            // The digest is a courtesy on top of the heartbeat. A scene it cannot fingerprint just goes unreported; the ping itself must still go out.
            ping.Digest = null;
            ping.DigestRevision = null;
            ping.DigestScene = null;
        }
    }

    // Tells the host's user once when a guest keeps drifting despite resyncs; nothing is shown again until the notice changes or clears.
    private void ShowDriftNoticeOnce()
    {
        string notice = server?.DriftNotice;
        if (notice == null)
        {
            driftNoticeShown = null;
            return;
        }
        if (!string.Equals(notice, driftNoticeShown, StringComparison.Ordinal))
        {
            driftNoticeShown = notice;
            SetActionNotice(notice);
            ShowToast("A collaborator's scene keeps drifting", notice, Theme.Red, 15f);
        }
    }

    // Writes the live text of a scene to .collaboration/recovery before the host's snapshot replaces it, and returns the file's path. The folder,
    // the engine's own scene serialisation and the write wrapper are the ones Preserve uses; the name is RecoveryBackup's. A copy is never
    // overwritten (two snapshots in one second get a counter), and a failed write throws the specific message so that the caller applies nothing.
    private string WriteSceneBackup(string scene, Tracked tracked)
    {
        string path = null;
        try
        {
            string text = tracked.Session.Scene.Serialize().ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            });
            string folder = Path.Combine(SceneAdapter.Root, ".collaboration", "recovery");
            Directory.CreateDirectory(folder);
            string name = RecoveryBackup.BuildFileName(scene, DateTime.UtcNow);
            path = Path.Combine(folder, name);
            for (int counter = 2; File.Exists(path) && counter < 100; counter++)
            {
                path = Path.Combine(folder, name.Substring(0, name.Length - ".scene".Length) + "-" + counter + ".scene");
            }
            string target = path;
            RecoveryPathOutcome outcome = RecoveryPathRules.PublishAfterWrite(target, () =>
            {
                using FileStream stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(Encoding.UTF8.GetBytes(text));
                stream.Flush(flushToDisk: true);
            });
            if (outcome.Succeeded)
            {
                return outcome.Published;
            }
        }
        catch (Exception)
        {
        }
        throw new InvalidOperationException(RecoveryBackup.BackupFailedMessage);
    }

    private void Receive(Packet p)
    {
        if (p.Kind == "welcome")
        {
            if (restoring && hostSession != null && hostSession != p.Session)
            {
                Fail("Host session ended or restarted. Preserve recovery data, sync saved scenes and start a new session.");
                return;
            }
            hostSession = p.Session;
            Connected = true;
            Status = "Connected • live";
            reconnectRecovery = false;
            if (!restoring)
            {
                return;
            }
            {
                foreach (KeyValuePair<string, Tracked> scene in scenes)
                {
                    if (scene.Value.Pending != null)
                    {
                        wire.Send(scene.Value.Pending);
                    }
                    if (scene.Value.Session.Scene.IsValid())
                    {
                        wire.Send(new Packet
                        {
                            Kind = "subscribe",
                            Scene = scene.Key,
                            Objects = scene.Value.Known.Values.ToList()
                        });
                    }
                }
                return;
            }
        }
        string canonical;
        if (p.Kind == "fatal" || p.Kind == "reject")
        {
            string text = CollaborationTransportDiagnostics.BoundedStoredDiagnostic(p.Error);
            if (p.Kind == "reject" && p.Scene == null)
            {
                Fail(text);
                return;
            }
            if (restoring && p.Kind == "reject")
            {
                RecoveryNotice = CollaborationDiagnostics.Bounded("Rejected offline edit retained in " + RecoveryPath + ": " + text);
            }
            else
            {
                Fail(text);
            }
            if (p.Kind == "reject")
            {
                if (!ScenePacketRouting.TryResolveTracked(p.Scene, ((IEnumerable<KeyValuePair<string, Tracked>>)scenes).ToDictionary((Func<KeyValuePair<string, Tracked>, string>)((KeyValuePair<string, Tracked> pair) => pair.Key), (Func<KeyValuePair<string, Tracked>, object>)((KeyValuePair<string, Tracked> pair) => pair.Value)), out canonical, out object target) || !(target is Tracked tracked))
                {
                    throw new InvalidOperationException("Rejected packet has no current tracked scene target.");
                }
                ForgetPending(tracked, p.Id);
            }
        }
        else if (p.Kind == "presence")
        {
            if (p.Peer != Peer)
            {
                Roster[p.Peer] = p;
            }
        }
        else if (p.Kind == "left")
        {
            Roster.Remove(p.Peer);
        }
        else
        {
            if (p.Kind == "pong")
            {
                return;
            }
            if (p.Scene == null)
            {
                throw new InvalidOperationException("Scene packet has no current tracked target.");
            }
            if (!ScenePacketRouting.TryResolveTracked(p.Scene, ((IEnumerable<KeyValuePair<string, Tracked>>)scenes).ToDictionary((Func<KeyValuePair<string, Tracked>, string>)((KeyValuePair<string, Tracked> pair) => pair.Key), (Func<KeyValuePair<string, Tracked>, object>)((KeyValuePair<string, Tracked> pair) => pair.Value)), out canonical, out object target2) || !(target2 is Tracked tracked2))
            {
                throw new InvalidOperationException("Scene packet has no current tracked target.");
            }
            if (Error != null)
            {
                return;
            }
            if (p.Kind == "snapshot")
            {
                Dictionary<string, ObjectState> dictionary = StateRules.Map(p.Objects);
                if (!tracked2.Session.Scene.IsValid())
                {
                    tracked2.Known = dictionary;
                    return;
                }
                string restoredNotice = null;
                if (!restoring)
                {
                    if (tracked2.Ready && tracked2.Pending != null)
                    {
                        // A resync that arrives while an edit awaits the host's answer would replace the scene under that edit, and the answer
                        // would then be applied to a scene it was not made against. Nothing is lost by waiting: the digest is not sent while
                        // an edit is pending, and the next ping after the answer finds the drift again if it is still there.
                        return;
                    }
                    if (StateRules.Diff(tracked2.Known, SceneAdapter.Capture(tracked2.Session.Scene)).Count > 0)
                    {
                        // Unsent edits are about to be replaced by the host's scene: they are written to a file first, and if that fails
                        // the host's scene is not applied, because unsaved work is never replaced without a copy.
                        restoredNotice = RecoveryBackup.BuildNotice(p.Scene, WriteSceneBackup(p.Scene, tracked2));
                    }
                }
                SceneAdapter.Apply(tracked2.Session, dictionary);
                tracked2.Known = dictionary;
                tracked2.Ready = true;
                tracked2.LastOrder = SceneAdapter.Capture(tracked2.Session.Scene);
                tracked2.Session.UndoSystem.Initialize();
                tracked2.Group.Clear();
                tracked2.Undo.Clear();
                MirrorNativeUndo(tracked2);
                if (scenes.Values.All((Tracked x) => x.Ready || !x.Session.Scene.IsValid()))
                {
                    restoring = false;
                }
                if (restoredNotice != null)
                {
                    SetActionNotice(restoredNotice);
                    ShowToast("Scene restored from the host", restoredNotice, Theme.Primary, 15f);
                }
            }
            else
            {
                if (p.Kind != "accepted")
                {
                    return;
                }
                if (!seen.Add(p.Peer + ":" + p.Id))
                {
                    ForgetPending(tracked2, p.Id);
                    return;
                }
                AcceptedOperations++;
                if (p.Peer == Peer)
                {
                    foreach (Change change in p.Changes)
                    {
                        if ((object)change.After == null)
                        {
                            tracked2.Known.Remove(change.Id);
                        }
                        else
                        {
                            tracked2.Known[change.Id] = change.After;
                        }
                        ObjectState before = (tracked2.Group.TryGetValue(change.Id, out var value) ? value.Before : change.Before);
                        tracked2.Group[change.Id] = new Change(change.Id, before, change.After);
                    }
                    ForgetPending(tracked2, p.Id);
                    if (p.Final)
                    {
                        if (!tracked2.UndoPending)
                        {
                            tracked2.Undo.Push(tracked2.Group.Values.ToList());
                        }
                        tracked2.Group.Clear();
                        tracked2.UndoPending = false;
                        if (tracked2.Session.Scene.IsValid())
                        {
                            tracked2.Session.UndoSystem.Initialize();
                        }
                        MirrorNativeUndo(tracked2);
                    }
                }
                else
                {
                    bool flag = tracked2.Session.Scene.IsValid() && SceneEditorSession.All.Contains(tracked2.Session);
                    Dictionary<string, ObjectState> dictionary2 = (flag ? SceneAdapter.Capture(tracked2.Session.Scene) : new Dictionary<string, ObjectState>(tracked2.Known));
                    foreach (Change change2 in p.Changes)
                    {
                        if (!StateRules.Equal(dictionary2.GetValueOrDefault(change2.Id), tracked2.Known.GetValueOrDefault(change2.Id)))
                        {
                            throw new InvalidOperationException("Concurrent edit on " + change2.Id + ". Your local version was preserved. Use Recover & reconnect to accept the host state.");
                        }
                        if ((object)change2.After == null)
                        {
                            dictionary2.Remove(change2.Id);
                            tracked2.Known.Remove(change2.Id);
                        }
                        else
                        {
                            dictionary2[change2.Id] = change2.After;
                            tracked2.Known[change2.Id] = change2.After;
                        }
                    }
                    if (flag)
                    {
                        SceneAdapter.Apply(tracked2.Session, dictionary2);
                        tracked2.LastOrder = SceneAdapter.Capture(tracked2.Session.Scene);
                        tracked2.Session.UndoSystem.Initialize();
                        MirrorNativeUndo(tracked2);
                    }
                }
                Status = "Connected • revision " + p.Revision;
            }
        }
    }

    public IDisposable BeginContinuousEdit()
    {
        if (!Connected || Error != null)
        {
            throw new InvalidOperationException("Connect before starting a continuous edit");
        }
        continuousEdits++;
        return new EditScope(this);
    }

    public bool CanSave()
    {
        if (sourceBaseline != SceneAdapter.Baseline(includeScenes: false))
        {
            return false;
        }
        if (!Connected || Error != null)
        {
            return false;
        }
        string text = SceneAdapter.PathOf(SceneEditorSession.Active);
        if (text == null || !scenes.TryGetValue(text, out var value))
        {
            return false;
        }
        if (!value.Ready || value.Pending != null || value.Group.Count > 0)
        {
            return false;
        }
        if (!JsonNode.DeepEquals(value.Settings, value.Session.Scene.SerializeProperties()))
        {
            return false;
        }
        return StateRules.Diff(value.Known, SceneAdapter.Capture(value.Session.Scene)).Count == 0;
    }

    /// <summary>Why a save is not possible right now, in words, or null when it is possible. Mirrors <see cref="CanSave"/> condition for condition.</summary>
    public string WhyCannotSave()
    {
        if (sourceBaseline != SceneAdapter.Baseline(includeScenes: false))
        {
            return "the project's source or assets changed since the session started";
        }
        if (!Connected || Error != null)
        {
            return "the session is not connected";
        }
        string text = SceneAdapter.PathOf(SceneEditorSession.Active);
        if (text == null || !scenes.TryGetValue(text, out var value))
        {
            return "the active scene is not part of the session (active: " + (text ?? "none") + ")";
        }
        if (!value.Ready)
        {
            return "the host has not finished sending the scene";
        }
        if (value.Pending != null)
        {
            return "an edit is still waiting for the host to acknowledge it";
        }
        if (value.Group.Count > 0)
        {
            return "a multi-part edit is still being grouped";
        }
        if (!JsonNode.DeepEquals(value.Settings, value.Session.Scene.SerializeProperties()))
        {
            return "the scene's own settings differ from what the host has";
        }
        List<Change> difference = StateRules.Diff(value.Known, SceneAdapter.Capture(value.Session.Scene));
        if (difference.Count > 0)
        {
            Change first = difference[0];
            string name = (first.After ?? first.Before)?.Data?["Name"]?.GetValue<string>() ?? first.Id;
            return difference.Count + " object" + (difference.Count == 1 ? "" : "s") + " differ from the host's copy (first: " + name + ")";
        }
        return null;
    }

    private string SaveRefusal()
    {
        if (sourceBaseline != SceneAdapter.Baseline(includeScenes: false))
        {
            return "Source/assets changed during collaboration; end the session and sync in Git before saving a checkpoint";
        }
        return "Wait for all edits to be acknowledged before saving";
    }

    public void Save()
    {
        if (!CanSave())
        {
            throw new InvalidOperationException(SaveRefusal());
        }
        string text = SceneAdapter.PathOf(SceneEditorSession.Active);
        if (text == null || !scenes.TryGetValue(text, out var value))
        {
            throw new InvalidOperationException(SaveRefusal());
        }
        saving = true;
        try
        {
            SendPresence();
            value.Session.Save(saveAs: false);
            value.DiskStamp = SceneAdapter.DiskStamp(text);
            server?.AllowSavedBaseline(SceneAdapter.Baseline());
        }
        finally
        {
            saving = false;
        }
    }

    private void Checkpoint()
    {
        if (AutoSaveRules.CheckpointEligible(SceneEditorSession.Active?.HasUnsavedChanges ?? false, CanSave))
        {
            RunAutoSave();
        }
    }

    private void RunAutoSave()
    {
        lastAutoSave = DateTime.UtcNow;
        try
        {
            Save();
            SetSaveNotice("Auto-saved the synchronized scene at " + DateTime.Now.ToString("HH:mm") + ".");
        }
        catch (Exception ex)
        {
            GlobalGameNamespace.Log.Error("Collaboration auto-save: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, ex));
            SetSaveNotice("The automatic save did not run: " + ex.Message);
        }
    }

    private void TickIdleAutoSave()
    {
        if (!AutoSaveIdle)
        {
            return;
        }
        DateTime utcNow = DateTime.UtcNow;
        if (!(utcNow < nextAutoSaveCheck))
        {
            nextAutoSaveCheck = utcNow.AddSeconds(1.0);
            if (AutoSaveRules.Eligible(new IdleSaveInput(Enabled: true, SceneEditorSession.Active?.HasUnsavedChanges ?? false, Roster.Values.Any((Packet x) => string.Equals(PresenceText(x.Presence, "state", 32), "editing", StringComparison.Ordinal)), SceneEditorSession.All.Any((SceneEditorSession x) => x.IsPlaying), lastLocalEdit, lastAutoSave, utcNow, CanSave)))
            {
                RunAutoSave();
            }
        }
    }

    private void TickAutoRecover()
    {
        if (Connected)
        {
            bool flag = recoverArmed;
            recoverArmed = false;
            recoverDeclined = false;
            recoverAttempts = 0;
            recoverNextAttempt = default;
            recoverPromptShown = false;
            if (flag)
            {
                ShowToast("Connection restored", "Automatic recovery reconnected this session.", Theme.Green, 6f);
            }
        }
        else
        {
            if (Error == null)
            {
                return;
            }
            bool flag2 = TerminalTeardownError != null || RuntimeLifecycle.HasTerminalTeardownFailure;
            if ((errorClass != SessionFailureClass.Transient) | flag2)
            {
                DeclineAutoRecover("This editor session remains fail-stop; automatic recovery cannot continue.");
            }
            else if (!AutoRecover)
            {
                if (!recoverPromptShown)
                {
                    bool flag3 = AutoRecoveryRules.PromptOffered(AutoRecover, Error != null, errorClass, flag2, RecoveryOffered(), recoverPromptShown);
                    recoverPromptShown = true;
                    if (flag3)
                    {
                        ShowToast("Connection lost", "Select Recover & reconnect, or press CTRL+ALT+R, to restore this session.", Theme.Primary, 15f);
                    }
                }
            }
            else
            {
                if (recoverDeclined || (recoverNextAttempt != default(DateTime) && DateTime.UtcNow < recoverNextAttempt))
                {
                    return;
                }
                if (recoverAttempts >= 3)
                {
                    DeclineAutoRecover("Three automatic attempts did not reconnect this session.");
                    return;
                }
                bool isHost = IsHost;
                bool flag4 = !isHost && HasUnsavedTrackedChanges();
                bool flag5 = reconnectQueued || reconnectTokenTask != null || connecting != null;
                if (isHost)
                {
                    DeclineAutoRecover("A hosted session is never recovered automatically.");
                }
                else
                {
                    if (flag4 | flag5)
                    {
                        return;
                    }
                    string text = activeAddress ?? startAddress;
                    int num = ((activePort != 0) ? activePort : startPort);
                    if (string.IsNullOrWhiteSpace(text) || num <= 0)
                    {
                        DeclineAutoRecover("Automatic recovery has no endpoint to redial.");
                        return;
                    }
                    bool reconnectAccepted = RecoveryOffered();
                    if (AutoRecoveryRules.Eligible(new AutoRecoveryInput(AutoRecover, Error != null, errorClass, flag2, isHost, flag4, reconnectAccepted, flag5, recoverAttempts, DateTime.UtcNow, recoverNextAttempt)))
                    {
                        recoverAttempts++;
                        recoverArmed = true;
                        recoverNextAttempt = DateTime.UtcNow.Add(AutoRecoveryRules.Backoff(recoverAttempts - 1));
                        try
                        {
                            recoverArming = true;
                            Reconnect(text, num);
                            return;
                        }
                        catch (Exception ex)
                        {
                            recoverArmed = false;
                            recoverDeclined = true;
                            ShowToast("Automatic recovery stopped", ex.Message, Theme.Red, 6f);
                            GlobalGameNamespace.Log.Error("Collaboration auto-recover: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, ex));
                            return;
                        }
                        finally
                        {
                            recoverArming = false;
                        }
                    }
                    DeclineAutoRecover("Automatic recovery is not possible in this session's current state.");
                }
            }
        }
    }

    private bool HasUnsavedTrackedChanges()
    {
        foreach (Tracked value in scenes.Values)
        {
            SceneEditorSession sceneEditorSession = value?.Session;
            if (sceneEditorSession != null && sceneEditorSession.HasUnsavedChanges)
            {
                return true;
            }
        }
        return false;
    }

    private void DeclineAutoRecover(string reason)
    {
        if (!recoverDeclined || recoverArmed)
        {
            bool flag = recoverArmed;
            recoverArmed = false;
            recoverDeclined = true;
            if (flag)
            {
                ShowToast("Automatic recovery stopped", reason, Theme.Red, 6f);
            }
        }
    }

    private bool RecoveryOffered()
    {
        if (Error == null)
        {
            return false;
        }
        try
        {
            return ReconnectGuard().Accepted;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void ShowToast(string title, string subtitle, Color border, float seconds)
    {
        try
        {
            ToastWidget widget = new ToastWidget
            {
                Title = title,
                Subtitle = subtitle,
                BorderColor = border,
                DrawTimer = false,
                IsRunning = false,
                FixedWidth = 360f
            };
            ToastManager.Remove(widget, seconds);
        }
        catch (Exception error)
        {
            GlobalGameNamespace.Log.Error("Collaboration notice: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error));
        }
    }

    public void Undo()
    {
        string text = SceneAdapter.PathOf(SceneEditorSession.Active);
        if (!Connected || Error != null || text == null || !scenes.TryGetValue(text, out var value) || !value.Ready || value.Pending != null || value.Undo.Count == 0)
        {
            throw new InvalidOperationException("No acknowledged local operation available to undo");
        }
        List<Change> list = (from c in value.Undo.Peek()
            select new Change(c.Id, c.After, c.Before)).ToList();
        Dictionary<string, ObjectState> dictionary = SceneAdapter.Capture(value.Session.Scene);
        foreach (Change item in list)
        {
            if (!StateRules.Equal(dictionary.GetValueOrDefault(item.Id), item.Before))
            {
                throw new InvalidOperationException("Undo refused: object changed since your edit");
            }
            if ((object)item.After == null)
            {
                dictionary.Remove(item.Id);
            }
            else
            {
                dictionary[item.Id] = item.After;
            }
        }
        SceneAdapter.Apply(value.Session, dictionary);
        value.Undo.Pop();
        value.UndoPending = true;
        SendEdit(text, value, list, final: true);
    }

    private void MirrorNativeUndo(Tracked tracked)
    {
        try
        {
            if (tracked == null || tracked.Session == null || tracked.Undo.Count == 0)
            {
                return;
            }
            UndoSystem undoSystem = tracked.Session.UndoSystem;
            if (undoSystem == null)
            {
                return;
            }
            List<Change> changes = tracked.Undo.Peek();
            if (changes != null && changes.Count != 0)
            {
                undoSystem.Insert("Collaboration Undo", () =>
                {
                    TryUndo();
                }, () =>
                {
                    TryRedo(tracked, changes);
                });
            }
        }
        catch (Exception error)
        {
            GlobalGameNamespace.Log.Error("Collaboration undo mirror: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error));
        }
    }

    private void ClearNativeUndoMirrors()
    {
        try
        {
            UndoSystem undoSystem = SceneEditorSession.Active?.UndoSystem;
            if (IsNativeUndoMirror(undoSystem))
            {
                undoSystem.Initialize();
            }
            foreach (Tracked value in scenes.Values)
            {
                UndoSystem undoSystem2 = value?.Session?.UndoSystem;
                if (IsNativeUndoMirror(undoSystem2))
                {
                    undoSystem2.Initialize();
                }
            }
        }
        catch (Exception error)
        {
            GlobalGameNamespace.Log.Error("Collaboration undo mirror: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error));
        }
    }

    private static bool IsNativeUndoMirror(UndoSystem undoSystem)
    {
        if (undoSystem == null)
        {
            return false;
        }
        UndoSystem.Entry result;
        return undoSystem.Back.TryPeek(out result) && string.Equals(result?.Name, "Collaboration Undo", StringComparison.Ordinal);
    }

    private bool TryUndo()
    {
        try
        {
            Undo();
            UndoNotice = null;
            undoNoticeExpires = default;
            return true;
        }
        catch (Exception ex)
        {
            SetUndoNotice(ex.Message);
            return false;
        }
    }

    private bool TryRedo(Tracked tracked, List<Change> changes)
    {
        try
        {
            if (changes == null || changes.Count == 0 || !Connected || Error != null || tracked == null || tracked.Session == null || !tracked.Session.Scene.IsValid())
            {
                SetUndoNotice("Redo is not available for this collaboration operation.");
                return false;
            }
            string text = SceneAdapter.PathOf(tracked.Session);
            if (text == null || !scenes.TryGetValue(text, out var value) || value != tracked || !value.Ready || value.Pending != null || value.UndoPending)
            {
                SetUndoNotice("Redo is not available for this collaboration operation.");
                return false;
            }
            Dictionary<string, ObjectState> dictionary = SceneAdapter.Capture(value.Session.Scene);
            foreach (Change change in changes)
            {
                if (StateRules.Equal(dictionary.GetValueOrDefault(change.Id), change.Before))
                {
                    continue;
                }
                if (StateRules.Equal(dictionary.GetValueOrDefault(change.Id), change.After))
                {
                    SetUndoNotice("Nothing to redo: this collaboration operation is still applied.");
                    return false;
                }
                SetUndoNotice("Redo refused: the scene changed since your edit.");
                return false;
            }
            foreach (Change change2 in changes)
            {
                if ((object)change2.After == null)
                {
                    dictionary.Remove(change2.Id);
                }
                else
                {
                    dictionary[change2.Id] = change2.After;
                }
            }
            SceneAdapter.Apply(value.Session, dictionary);
            value.Undo.Push(changes);
            value.UndoPending = true;
            SendEdit(text, value, changes, final: true);
            UndoNotice = null;
            undoNoticeExpires = default;
            return true;
        }
        catch (Exception ex)
        {
            SetUndoNotice(ex.Message);
            return false;
        }
    }

    private void SetUndoNotice(string text)
    {
        string text2 = BoundedDiagnostic(text);
        if (!string.Equals(UndoNotice, text2, StringComparison.Ordinal) || !(DateTime.UtcNow < undoNoticeExpires))
        {
            UndoNotice = text2;
            undoNoticeExpires = DateTime.UtcNow.AddSeconds(8.0);
        }
    }

    private void SetActionNotice(string text)
    {
        string text2 = BoundedDiagnostic(text);
        if (!string.Equals(ActionNotice, text2, StringComparison.Ordinal) || !(DateTime.UtcNow < actionNoticeExpires))
        {
            ActionNotice = text2;
            actionNoticeExpires = DateTime.UtcNow.AddSeconds(8.0);
        }
    }

    private void SendPresence(bool force = true)
    {
        if (wire == null)
        {
            return;
        }
        SceneEditorSession active = SceneEditorSession.Active;
        SceneViewportWidget sceneViewportWidget = SceneViewWidget.Current?.LastSelectedViewportWidget;
        JsonObject jsonObject = new JsonObject();
        SceneEditorSession source = AuthoringSource();
        GameEditorSession game = SceneEditorSession.All.OfType<GameEditorSession>().FirstOrDefault();
        bool playing = IsLocalPlaying(source);
        CameraComponent gameCamera = null;
        try
        {
            gameCamera = game?.Scene?.Camera;
        }
        catch (Exception)
        {
            gameCamera = null;
        }
        if (playing && gameCamera != null && gameCamera.IsValid())
        {
            // While playing, the collaborator's marker follows the game camera, so someone still in the editor
            // sees where the player is and which way they look. The scene view is not what they are looking at.
            jsonObject["position"] = JsonSerializer.SerializeToNode(gameCamera.WorldPosition);
            jsonObject["rotation"] = JsonSerializer.SerializeToNode(gameCamera.WorldRotation);
            float gameFov = gameCamera.FieldOfView;
            if (gameFov > 0f && gameFov < 180f)
            {
                jsonObject["fov"] = gameFov;
            }
        }
        else if (!playing && sceneViewportWidget != null && SceneViewWidget.Current.Session == active)
        {
            jsonObject["position"] = JsonSerializer.SerializeToNode(sceneViewportWidget.State.CameraPosition);
            jsonObject["rotation"] = JsonSerializer.SerializeToNode(sceneViewportWidget.State.CameraRotation);
            float? num = sceneViewportWidget.Renderer?.Camera?.FieldOfView;
            if (num.HasValue)
            {
                float valueOrDefault = num.GetValueOrDefault();
                if (valueOrDefault > 0f && valueOrDefault < 180f)
                {
                    jsonObject["fov"] = num.Value;
                }
            }
        }
        GameObject[] array = (playing ? null : active?.Selection.OfType<GameObject>().ToArray()) ?? Array.Empty<GameObject>();
        jsonObject["selection"] = JsonSerializer.SerializeToNode(array.Select((GameObject x) => x.Id.ToString()).ToArray());
        // The left mouse button is held down for gameplay too, so it only means "editing" outside play.
        bool flag = !playing && (continuousEdits > 0 || Editor.Application.MouseButtons.HasFlag(MouseButtons.Left));
        JsonObject jsonObject2 = jsonObject;
        string text = PlaySpectateRules.PresenceState(saving, playing, flag);
        jsonObject2["state"] = text;
        if (flag && array.Length != 0)
        {
            jsonObject["editing"] = array[0].Id.ToString();
        }
        string text2 = BoundPresenceText(EditorToolManager.CurrentModeName, 40);
        if (text2 != null)
        {
            jsonObject["tool"] = text2;
        }
        string presenceScene = SceneAdapter.PathOf(playing && source != null ? source : active);
        // What "changed" means: the same presence body and scene as the last one sent. The state word and selection are part of the body, so a
        // selection or a status change is sent at once; only an identical packet is held back.
        string fingerprint = presenceScene + "|" + jsonObject.ToJsonString();
        TimeSpan sinceLast = DateTime.UtcNow - lastPresenceSent;
        bool changed = !string.Equals(fingerprint, lastPresenceFingerprint, StringComparison.Ordinal);
        if (!force && !SendCadence.ShouldSendPresence(changed, sinceLast, text == "editing"))
        {
            return;
        }
        lastPresenceFingerprint = fingerprint;
        lastPresenceSent = DateTime.UtcNow;
        wire.Send(new Packet
        {
            Kind = "presence",
            Scene = presenceScene,
            Peer = Peer,
            Presence = jsonObject
        });
    }

    private static string BoundPresenceText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string text = value.Trim();
        return (text.Length <= maxLength) ? text : text.Substring(0, maxLength);
    }

    private static string PresenceText(JsonObject presence, string key, int maxLength)
    {
        try
        {
            string value;
            return (presence?[key] is JsonValue jsonValue && jsonValue.TryGetValue<string>(out value)) ? BoundPresenceText(value, maxLength) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string[] ReadSelection(JsonObject presence)
    {
        try
        {
            return presence?["selection"]?.Deserialize<string[]>() ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static bool TryReadPresenceCamera(JsonObject presence, out Vector3 position, out Rotation rotation, out float? fieldOfView)
    {
        position = default;
        rotation = default;
        fieldOfView = null;
        if (presence?["position"] == null || presence["rotation"] == null)
        {
            return false;
        }
        try
        {
            position = presence["position"].Deserialize<Vector3>();
            rotation = presence["rotation"].Deserialize<Rotation>();
            if (presence["fov"] is JsonValue jsonValue && jsonValue.TryGetValue<float>(out var value) && value > 0f && value < 180f)
            {
                fieldOfView = value;
            }
        }
        catch (Exception)
        {
            return false;
        }
        return true;
    }

    private static Frustum PeerFrustum(Vector3 position, Rotation rotation, float fieldOfView)
    {
        float num = MathF.Tan(fieldOfView * 0.5f * ((float)Math.PI / 180f)) * 8f;
        float num2 = num * 1.7777778f;
        Vector3 vector = position + rotation.Forward * 8f;
        Vector3 vector2 = rotation.Up * num;
        Vector3 vector3 = rotation.Right * num2;
        return Frustum.FromCorners(new Ray(position, (vector + vector2 - vector3 - position).Normal), new Ray(position, (vector + vector2 + vector3 - position).Normal), new Ray(position, (vector - vector2 + vector3 - position).Normal), new Ray(position, (vector - vector2 - vector3 - position).Normal), 8f, 300f);
    }

    private static string StatusLineFor(Packet peer, string localScene, string edited, GameObject[] objects)
    {
        if (!string.Equals(peer.Scene, localScene, StringComparison.Ordinal))
        {
            return "scene: " + (BoundPresenceText(peer.Scene, 96) ?? "no saved scene");
        }
        string text = PresenceText(peer.Presence, "state", 32) ?? "idle";
        if (string.Equals(text, "playing", StringComparison.Ordinal))
        {
            return PlaySpectateRules.StatusText(text);
        }
        if (!string.Equals(text, "editing", StringComparison.Ordinal) || edited == null || objects == null)
        {
            return text;
        }
        GameObject gameObject = Array.Find(objects, (GameObject x) => string.Equals(x.Id.ToString(), edited, StringComparison.Ordinal));
        string text2 = ((gameObject == null) ? null : BoundPresenceText(gameObject.Name, 64));
        return (text2 == null) ? text : (text2 + " · " + text);
    }

    public void DrawGizmos(Scene scene)
    {
        DrawnMarkers = 0;
        DrawnSelections = 0;
        string text = scene.Source?.ResourcePath?.ToLowerInvariant();
        if (!Connected || text == null)
        {
            return;
        }
        AvatarCache.Poll();
        DateTime utcNow = DateTime.UtcNow;
        Vector3 vector = Gizmo.Camera?.Position ?? Vector3.Zero;
        GameObject[] array = null;
        foreach (KeyValuePair<string, Packet> item in Roster)
        {
            Packet value = item.Value;
            if (value.Presence == null)
            {
                continue;
            }
            bool flag = string.Equals(value.Scene, text, StringComparison.Ordinal);
            Color color = DockTheme.PeerColor(value.Peer);
            string text2 = BoundPresenceText(value.Name, 64) ?? "(unknown name)";
            string[] array2 = ReadSelection(value.Presence);
            string text3 = PresenceText(value.Presence, "editing", 64);
            if ((array2.Length != 0 || (flag && text3 != null)) && array == null)
            {
                array = SceneAdapter.Walk(scene).ToArray();
            }
            using (Gizmo.Scope("collaboration-" + value.Peer, Transform.Zero))
            {
                bool flag2 = (utcNow < focusedUntil && string.Equals(focusedPeer, item.Key, StringComparison.Ordinal)) || Gizmo.IsHovered;
                if (TryReadPresenceCamera(value.Presence, out var position, out var rotation, out var fieldOfView))
                {
                    float length = (position - vector).Length;
                    bool flag3 = length <= PresenceStyle.FarDistance;
                    string presenceState = PresenceText(value.Presence, "state", 32) ?? "idle";
                    PresenceMarker.Draw(new PresenceMarker.Subject
                    {
                        Position = position,
                        ViewRotation = rotation,
                        FieldOfView = fieldOfView,
                        Name = text2,
                        State = presenceState,
                        StatusLine = flag3 ? StatusLineFor(value, text, text3, array) : null,
                        Avatar = AvatarCache.GetTexture(value.SteamId),
                        SameScene = flag,
                        Emphasized = flag2,
                        ShowFrustum = fieldOfView.HasValue & flag,
                        Frustum = (fieldOfView.HasValue & flag) ? PeerFrustum(position, rotation, fieldOfView.Value) : null
                    });
                    DrawnMarkers++;
                }
                if (array2.Length == 0 || array == null)
                {
                    continue;
                }
                GameObject[] array3 = array;
                foreach (GameObject gameObject in array3)
                {
                    if (array2.Contains(gameObject.Id.ToString()))
                    {
                        Gizmo.Draw.Color = color;
                        Gizmo.Draw.LineThickness = (flag2 ? 2f : 1f);
                        Gizmo.Draw.LineBBox(gameObject.GetBounds());
                        Gizmo.Draw.Text(BoundPresenceText(gameObject.Name, 64) ?? "(unnamed)", new Transform(gameObject.WorldPosition + Vector3.Up * 16f), "Inter");
                        Texture texture2 = AvatarCache.GetTexture(value.SteamId);
                        if (texture2 != null)
                        {
                            Gizmo.Draw.Color = Color.White;
                            Gizmo.Draw.Sprite(gameObject.WorldPosition + Vector3.Up * 24f, 3.0, texture2, worldspace: true);
                        }
                        DrawnSelections++;
                    }
                }
                Gizmo.Draw.LineThickness = 1f;
            }
        }
    }

    public void FocusCamera(string peer)
    {
        FocusNotice = null;
        focusNoticeExpires = default;
        if (string.IsNullOrWhiteSpace(peer) || !Roster.TryGetValue(peer, out var value))
        {
            RefuseFocus("That collaborator is no longer connected.");
            return;
        }
        if (!string.Equals(value.Scene, SceneAdapter.PathOf(SceneEditorSession.Active), StringComparison.Ordinal))
        {
            RefuseFocus("That collaborator is in " + (BoundPresenceText(value.Scene, 96) ?? "no saved scene") + ". Open the scene first.");
            return;
        }
        SceneViewportWidget sceneViewportWidget = SceneViewWidget.Current?.LastSelectedViewportWidget;
        if (sceneViewportWidget?.State == null)
        {
            RefuseFocus("No scene view is open to move.");
            return;
        }
        if (value.Presence?["position"] == null || value.Presence["rotation"] == null)
        {
            RefuseFocus("That collaborator has not reported a camera position yet.");
            return;
        }
        Vector3 cameraPosition;
        Rotation cameraRotation;
        try
        {
            cameraPosition = value.Presence["position"].Deserialize<Vector3>();
            cameraRotation = value.Presence["rotation"].Deserialize<Rotation>();
        }
        catch (Exception)
        {
            RefuseFocus("That collaborator reported a camera position this build cannot read.");
            return;
        }
        sceneViewportWidget.State.CameraPosition = cameraPosition;
        sceneViewportWidget.State.CameraRotation = cameraRotation;
        focusedPeer = peer;
        focusedUntil = DateTime.UtcNow.AddSeconds(1.0);
    }

    private void RefuseFocus(string reason)
    {
        FocusNotice = BoundedDiagnostic(reason);
        focusNoticeExpires = DateTime.UtcNow.AddSeconds(5.0);
    }
}
