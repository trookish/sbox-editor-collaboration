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

public sealed partial class CollaborationSession : EditorEvent.ISceneView, EditorEvent.IEventListener
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

        // The scene's own settings as the host last confirmed them (SettingsMerge). Null when the host holds none for the scene.
        public JsonObject Settings;

        // The one settings change awaiting the host's answer, like Pending for edits.
        public Packet SettingsPending;

        // What this editor last wrote into the scene's settings and what the engine reported right after: the difference is what the engine
        // could not take, which SettingsMerge.LocalChange must never mistake for the user's change. Null until the first write.
        public JsonObject SettingsApplied;

        public JsonObject SettingsEcho;

        public DateTime DiskStamp;

        public DateTime StampMismatchSince;
        public ApplyRecovery Recovery = new ApplyRecovery();
        // Objects this editor could not show or capture: their host JSON stays in Known and is never sent back (ApplyRecovery).
        public HashSet<string> SkippedObjects = new HashSet<string>(StringComparer.Ordinal);
        // The person's state to replay after a resync, when the live scene could not be put back after a failed apply.
        public Dictionary<string, ObjectState> HeldLocal;
        // The resync replaces the local state instead of replaying it (the host refused it; it is in the recovery copy).
        public bool DiscardLocalOnResync;
        public bool DiskWarningShown;
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

    // Remote receipts arrive at drag rates, and every one used to reset the whole native undo history. The scene is still applied per receipt;
    // only the history reset (Initialize + MirrorNativeUndo) is coalesced by UndoResetScheduler, and the scenes awaiting it are kept here.
    private readonly UndoResetScheduler undoReset = new UndoResetScheduler();

    private readonly HashSet<Tracked> undoResetScenes = new HashSet<Tracked>();

    private static readonly System.Diagnostics.Stopwatch undoResetClock = System.Diagnostics.Stopwatch.StartNew();

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

    // The one-click internet relay (Core/HostingPlan.cs), used only when no relay is named anywhere: the loopback relay this host runs, the Quick Tunnel
    // that exposes it, and the reachability probe in flight. All of it is polled from PollConnectivity and released with the rest of the connectivity.
    private HostingPlan hostingPlan;

    private EmbeddedRelay embeddedRelay;

    private string embeddedRelayError;

    private CloudflaredTool quickTunnel;

    private Task<string> reachabilityTask;

    private CancellationTokenSource reachabilityCancel;

    // The public address the invite carries while the plan is Ready, and null otherwise; the invite never falls back to the loopback address.
    private string publicRelayUrl;

    private static readonly TimeSpan ReachabilityProbeTimeout = TimeSpan.FromSeconds(5L);

    private static double HostingSeconds => Environment.TickCount64 / 1000.0;

    private bool soloHarness;

    private readonly HeldPacketBuffer heldPackets = new HeldPacketBuffer();

    // Shared play (Core/PlayRoster.cs and friends): null unless this computer opted in and the host advertised it.
    private SharedPlayClient play;
    // Scenes whose host updates were discarded (play-mode buffer overflow) or could not be routed (the scene is playing): each is
    // resynchronized from the host once it can take a snapshot again.
    private readonly HashSet<string> resyncWhenRoutable = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> closedPacketNotices = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> onceNotices = new HashSet<string>(StringComparer.Ordinal);

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

    private bool autoRecover = AutoRecoveryRules.EnabledByDefault;

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

    // Ids of every settings change this editor sent: a reject for one of them is the host refusing that change, never a reason to fail.
    private readonly HashSet<string> sentSettings = new HashSet<string>();

    private const float PromptToastSeconds = 15f;

    private const float OutcomeToastSeconds = 6f;

    public const string NativeUndoEntryName = "Collaboration Undo";

    private const int ToolTextLimit = 40;

    private const int StateTextLimit = 32;

    private const int SceneTextLimit = 96;

    private const int ObjectNameTextLimit = 64;

    public static CollaborationSession Current { get; } = new CollaborationSession();

    public string Status { get; private set; } = "Offline";

    // Project file synchronization (FileSyncClient): created at welcome when the host advertises it, polled from Tick, fed from Receive.
    // Its progress is shown through Status (the dock's session line) and its notices through SetActionNotice and the log, the two ways
    // this session already reports to the person.
    private FileSyncClient projectFiles;

    /// <summary>The project files changed in the current session (the dock's "Files changed in this session"); replaced when a session starts.</summary>
    public SessionChangeLog Changes { get; private set; } = new SessionChangeLog();

    /// <summary>How many files the previous session changed, read once when this session started; null when there is no usable record.</summary>
    public int? PreviousSessionChanges { get; private set; }

    // Never merges the earlier file: it is read only for its count, and the new log marks itself for writing so the file stops describing the old session.
    private void BeginChangeLog()
    {
        PreviousSessionChanges = SessionChangeLog.ReadPreviousCount(SceneAdapter.Root);
        Changes = new SessionChangeLog();
        Changes.RequestWrite();
    }

    // The person's "Allow peers whose Steam identity could not be verified" choice for the session being joined; decides whether code from an unverified host may be received.
    private bool guestAllowUnverified;

    /// <summary>The guest's project file client, or null when not joined to a host that synchronizes files. The Code card reads its offer.</summary>
    public FileSyncClient ProjectFiles => projectFiles;

    /// <summary>The person agreed on the Code card: asks the host for exactly the offered files. False (nothing sent) when no offer is open or the host is not trusted.</summary>
    public bool ReceiveHostCode()
    {
        return projectFiles != null && wire != null && projectFiles.ConsentToCode( packet => wire.Send( packet ) );
    }

    /// <summary>The person chose "Not now" on the Code card.</summary>
    public void DeclineHostCode()
    {
        projectFiles?.DeclineCode();
    }

    private string lastHostFileNotice;

    // The geometry channel of this editor's connection (null while the host does not advertise it). It serves what the host asks for, holds
    // scene packets whose geometry has not arrived and releases them in order (docs/BLOB-SYNC.md).
    private BlobExchange blobs;

    private string lastHostBlobNotice;

    // The next packet to handle: one released by the exchange first, then the wire. Geometry packets are consumed here.
    private bool NextIncoming(out Packet packet)
    {
        while (true)
        {
            if (blobs != null && blobs.TryTakeReady(out packet))
            {
                return true;
            }
            if (!wire.Incoming.TryDequeue(out packet))
            {
                return false;
            }
            if (blobs == null)
            {
                return true;
            }
            if (BlobExchange.IsBlobPacket(packet.Kind))
            {
                lastReceive = DateTime.UtcNow;
                blobs.Handle(packet);
                continue;
            }
            if (blobs.Offer(packet) == BlobOffer.Process)
            {
                return true;
            }
            lastReceive = DateTime.UtcNow;
        }
    }

    // Once per frame: asks for missing geometry, serves requests, and for every scene update that had to be discarded (a bound was reached,
    // the geometry did not arrive or did not verify) asks the host for the scene again, with the reason shown to the person.
    private void PumpBlobs()
    {
        if (blobs == null)
        {
            return;
        }
        blobs.Pump();
        foreach ((Packet Packet, string Reason) item in blobs.TakeDropped())
        {
            Tracked affected = item.Packet.Scene == null ? null : TrackedFor(item.Packet.Scene);
            if (affected != null && affected.Session.Scene.IsValid() && !affected.Recovery.Paused)
            {
                affected.Recovery.Requested();
                RequestSceneResync(item.Packet.Scene, affected);
            }
        }
        foreach (string notice in blobs.TakeNotices())
        {
            GlobalGameNamespace.Log.Info("Collaboration: " + notice);
            SetActionNotice(notice);
        }
        string hostNotice = server?.BlobNotice;
        if (hostNotice != null && !string.Equals(hostNotice, lastHostBlobNotice, StringComparison.Ordinal))
        {
            lastHostBlobNotice = hostNotice;
            GlobalGameNamespace.Log.Info("Collaboration: " + hostNotice);
        }
    }

    // Scene files open in this editor are owned by the live scene sync, so file sync neither fetches nor sends them. PathOf is lowercase
    // and relative to Assets; the comparison in FileSyncClient is case-insensitive.
    private static string[] OpenFileScenes() => SceneEditorSession.All.Where( x => !(x is GameEditorSession) && !x.IsPrefabSession ).Select( x => SceneAdapter.PathOf( x ) ).Where( x => x != null ).Select( x => "Assets/" + x ).ToArray();

    // Makes the editor notice a file synchronization wrote or replaced: AssetSystem.FindByPath / RegisterFile ("If you just created an asset,
    // you probably want to immediately register it", Sandbox.Tools.xml) and Asset.Compile(false), the call the editor's own MCP
    // AssetSystem.cs makes after writing a source file. A deleted file is left to the engine's own file watching.
    private static void RefreshAsset( string relativePath )
    {
        try
        {
            string absolute = Path.Combine( Project.Current.RootDirectory.FullName, relativePath.Replace( '/', Path.DirectorySeparatorChar ) );
            if ( !File.Exists( absolute ) ) return;
            Asset asset = AssetSystem.FindByPath( absolute ) ?? AssetSystem.RegisterFile( absolute );
            if ( asset != null && asset.CanRecompile ) asset.Compile( false );
        }
        catch ( Exception ex )
        {
            GlobalGameNamespace.Log.Warning( "Collaboration: the asset \"" + relativePath + "\" was received but the editor could not refresh it: " + ex.Message );
        }
    }

    private void ShowFileNotices()
    {
        for ( string notice = projectFiles?.TakeNotice(); notice != null; notice = projectFiles.TakeNotice() )
        {
            GlobalGameNamespace.Log.Info( "Collaboration: " + notice );
            SetActionNotice( notice );
        }
        string hostNotice = server?.FileNotice;
        if ( hostNotice != null && !string.Equals( hostNotice, lastHostFileNotice, StringComparison.Ordinal ) )
        {
            lastHostFileNotice = hostNotice;
            GlobalGameNamespace.Log.Info( "Collaboration: " + hostNotice );
        }
    }

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

    /// <summary>The host's relay registration, worded for the dock: the internet relay's progress when the host runs its own, else the configured relay's. Null when there is none.</summary>
    public string RelayStatus
    {
        get
        {
            if (hostingPlan != null)
            {
                return hostingPlan.Status;
            }
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

    /// <summary>True while the internet relay has failed or is restarting, so the dock can colour its sentence as a problem.</summary>
    public bool InternetRelayTrouble => hostingPlan != null && (hostingPlan.Phase == HostingPhase.Failed || hostingPlan.Phase == HostingPhase.Degraded);

    /// <summary>Set while hosting with no relay a friend elsewhere could reach, so the dock can say so instead of leaving a silent gap. Null otherwise.</summary>
    public string NoRelayNotice => IsHost && hostingPlan == null && (relayHost == null || relayHost.State == RelayHostState.Stopped)
        ? HostingPlan.NoRelayNotice(effectiveRelay: EffectiveRelayUrl(), configuredRelay: sessionConfig?.RelayUrl, internetRelayEnabled: RelaySetup.InternetRelayEnabled)
        : null;

    /// <summary>What source synchronization is doing this frame; the dock reads it to explain a pause.</summary>
    public PlaySpectateDecision PlayDecision { get; private set; }

    public bool PausedForPlay => Connected && PlayDecision == PlaySpectateDecision.PausedForPlay;

    public int HeldSceneChanges => heldPackets.Count;

    /// <summary>The sentence shown while the local editor is playing and scene edits are held back. Null otherwise.</summary>
    public string PlayNotice
    {
        get
        {
            if (PausedForPlay && play?.EditsPausedNotice != null)
            {
                return play.EditsPausedNotice;
            }
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

    /// <summary>
    /// A formal sentence about somebody else's edit meeting this editor's own (the host combined them, a clash was settled in favour of
    /// one side, or an unsent change could not be kept). Null when there is nothing to say; cleared by <see cref="Frame"/> about eight
    /// seconds after <see cref="MergeNoticeAt"/>. The Controls card shows it.
    /// </summary>
    public string MergeNotice { get; private set; }

    /// <summary>When <see cref="MergeNotice"/> was last set (UTC); meaningful only while the notice is non-null.</summary>
    public DateTime MergeNoticeAt { get; private set; }

    private DateTime mergeNoticeExpires;

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
        if (text != null && Connected) Changes.Record("Assets/" + text, SessionChangeKind.Changed, "you");
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

    private void SetMergeNotice(string text)
    {
        // `text` is already joined, cleaned and bounded by ChangeRebase.JoinNotices; a repeated sentence only extends the display time.
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        MergeNotice = text;
        MergeNoticeAt = DateTime.UtcNow;
        mergeNoticeExpires = MergeNoticeAt.AddSeconds(8.0);
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
        guestAllowUnverified = allowUnverifiedPeers;
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
        BeginChangeLog();
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
        ClearDrag();
        seen.Clear();
        sentSettings.Clear();
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
        MergeNotice = null;
        mergeNoticeExpires = default;
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
                result.FileProjectRoot = Project.Current.RootDirectory.FullName;
                BlobHub.Store.AttachDisk(SceneAdapter.Root, BlobLimits.DiskCacheBytes);
                result.BlobStore = BlobHub.Store;
                result.SharedPlayEnabled = SharedPlayPreference.Enabled;
                result.HostEngineBaseline = SceneAdapter.EngineBaseline();
                result.FileChanged = RefreshAsset;
                result.Changes = Changes;
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
        // No relay named by the project or this computer, and nobody opted out: the host runs the relay itself and exposes it through an account-free
        // Quick Tunnel. Its own RelayHost dials the loopback relay now; the invite gains the public address only when the plan has seen it answer.
        if (HostingPlan.Applies(effectiveRelay: relayUrl, configuredRelay: sessionConfig?.RelayUrl, internetRelayEnabled: RelaySetup.InternetRelayEnabled))
        {
            hostingPlan = new HostingPlan();
            HostingDecision first = hostingPlan.Pump(HostingInputsNow(probeResult: null));
            relayUrl = first.Action == HostingAction.StartRelay ? StartEmbeddedRelay() : null;
        }
        if (string.IsNullOrWhiteSpace(relayUrl) || plan.Secret == null || plan.SessionKey == null)
        {
            return;
        }
        // The embedded relay is this host's own and takes no access key; the room's proofs, derived from the invite secret, keep strangers out of it.
        string accessKey = embeddedRelay != null ? null : ReadRelayAccessKey();
        try
        {
            relayHost = new RelayHost(() => new SandboxRelaySocket(), relayUrl, plan.Secret, plan.SessionKey, accessKey);
            if (server != null)
            {
                server.Relay = relayHost;
            }
            relayHost.Start();
            // A configured relay goes into the invite at once, as it always has; the embedded one only when the plan publishes its public address.
            inviteCarriesRelay = embeddedRelay == null;
            inviteCodeValue = BuildInviteCode(port);
        }
        catch (Exception error)
        {
            relayHost?.Dispose();
            relayHost = null;
            inviteCarriesRelay = false;
            if (embeddedRelay != null)
            {
                embeddedRelayError = "The host could not connect to its own local relay.";
            }
            GlobalGameNamespace.Log.Error("Collaboration relay: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error));
        }
    }

    // Takes the first loopback port the engine lets the host's own WebSocket dial (HostingPlan.LocalRelayPorts). A free port picked by the system would
    // be refused by the engine's ws:// policy, so the choice is limited to those four. Returns the local relay address, or null with embeddedRelayError
    // set for the plan to report on its next pump.
    private string StartEmbeddedRelay()
    {
        foreach (int candidate in HostingPlan.LocalRelayPorts)
        {
            EmbeddedRelay relay = new EmbeddedRelay(candidate);
            try
            {
                relay.Start();
                embeddedRelay = relay;
                return HostingPlan.LocalRelayUrl(candidate);
            }
            catch (InvalidOperationException)
            {
                relay.Dispose();
            }
        }
        embeddedRelayError = "The local relay could not listen on any of the ports the editor permits (" + string.Join(", ", HostingPlan.LocalRelayPorts) + "); close the program that uses them and host again.";
        return null;
    }

    private HostingInputs HostingInputsNow(string probeResult)
    {
        CloudflaredTool tunnel = quickTunnel;
        string relayError = embeddedRelayError;
        if (relayError == null && embeddedRelay != null && !embeddedRelay.IsListening)
        {
            relayError = "The local relay stopped listening.";
        }
        if (relayError == null && embeddedRelay != null && relayHost?.State == RelayHostState.Failed)
        {
            relayError = "The host's connection to its own local relay failed: " + relayHost.Failure;
        }
        bool running = tunnel?.State == CloudflaredTool.Phase.Running;
        return new HostingInputs(
            Now: HostingSeconds,
            RelayStarted: embeddedRelay?.IsListening == true,
            RelayError: relayError,
            LocalHostReady: relayHost?.State == RelayHostState.Ready,
            TunnelRunning: running,
            TunnelRelayUrl: running ? tunnel.RelayUrl : null,
            TunnelRegistered: tunnel?.RegisteredConnectionSeen == true,
            TunnelError: tunnel?.State == CloudflaredTool.Phase.Failed ? (tunnel.Error ?? "The tunnel stopped.") : null,
            TunnelProgress: tunnel?.State == CloudflaredTool.Phase.Downloading ? tunnel.Status : null,
            ProbeResult: probeResult);
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
            Relay = HostingPlan.InviteRelay(inviteCarriesRelay: inviteCarriesRelay, embeddedRelayInUse: embeddedRelay != null, publicRelayUrl: publicRelayUrl, effectiveRelayUrl: EffectiveRelayUrl(), soloHarness: soloHarness)
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
        guestAllowUnverified = allowUnverifiedPeers;
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
        guestAllowUnverified = allowUnverifiedPeers;
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
        PollInternetHosting();
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

    // One step of the internet relay per editor frame. Everything slow (the download, cloudflared's start, the HTTP probe) is a task or a process that is
    // polled here and never waited on; the relay itself is pumped first so a frame's traffic is forwarded before anything else is decided.
    private void PollInternetHosting()
    {
        if (hostingPlan == null)
        {
            return;
        }
        embeddedRelay?.Pump();
        if (hostingPlan.Phase == HostingPhase.Failed)
        {
            return;
        }
        quickTunnel?.Pump();
        string probe = null;
        if (reachabilityTask != null && reachabilityTask.IsCompleted)
        {
            // Completed, so reading it cannot block. CheckReachabilityAsync turns every failure into a sentence; a faulted task is not expected.
            probe = reachabilityTask.IsCompletedSuccessfully ? reachabilityTask.GetAwaiter().GetResult() : HostingPlan.ProbeVerdict(null, null);
            reachabilityTask = null;
            reachabilityCancel?.Dispose();
            reachabilityCancel = null;
        }
        HostingDecision decision = hostingPlan.Pump(HostingInputsNow(probe));
        // The plan has now seen the failed tool. Releasing it kills cloudflared's process tree, so a restart begins from a clean one.
        if (quickTunnel?.State == CloudflaredTool.Phase.Failed)
        {
            quickTunnel.Dispose();
            quickTunnel = null;
        }
        switch (decision.Action)
        {
            case HostingAction.StartTunnel:
                quickTunnel?.Dispose();
                quickTunnel = null;
                if (embeddedRelay != null)
                {
                    quickTunnel = new CloudflaredTool();
                    quickTunnel.Begin(embeddedRelay.Port);
                }
                break;
            case HostingAction.ProbeReachability:
            {
                string health = HostingPlan.HealthUrl(decision.PublicUrl);
                reachabilityCancel = new CancellationTokenSource(ReachabilityProbeTimeout);
                reachabilityTask = health != null ? CheckReachabilityAsync(health, reachabilityCancel.Token) : Task.FromResult(HostingPlan.ProbeVerdict(null, null));
                break;
            }
            case HostingAction.PublishRelayInvite:
                publicRelayUrl = decision.PublicUrl;
                inviteCarriesRelay = true;
                inviteCodeValue = BuildInviteCode(hostPort);
                break;
            case HostingAction.WithdrawRelayInvite:
                WithdrawPublicRelay();
                break;
        }
        // Whatever the action, an invite names the public relay only while the plan is Ready.
        if (hostingPlan.Phase != HostingPhase.Ready && publicRelayUrl != null)
        {
            WithdrawPublicRelay();
        }
        if (hostingPlan.Phase == HostingPhase.Failed)
        {
            // Final for this session: nothing is left to probe or restart. The loopback relay stays until the session ends, as the host's RelayHost uses it.
            reachabilityCancel?.Cancel();
            reachabilityCancel?.Dispose();
            reachabilityCancel = null;
            reachabilityTask = null;
            quickTunnel?.Dispose();
            quickTunnel = null;
        }
    }

    private void WithdrawPublicRelay()
    {
        publicRelayUrl = null;
        inviteCarriesRelay = false;
        if (inviteCodeValue != null)
        {
            inviteCodeValue = BuildInviteCode(hostPort);
        }
    }

    // Through the engine's HTTP client, the only one an editor script may use, as SandboxCloudflareTransport does. Every outcome becomes the plan's
    // verdict sentence; the time budget is the token's (ReachabilityProbeTimeout).
    private static async Task<string> CheckReachabilityAsync(string healthUrl, CancellationToken token)
    {
        try
        {
            using var response = await Http.RequestAsync(healthUrl, "GET", null, null, token);
            string body = await response.Content.ReadAsStringAsync(token);
            return HostingPlan.ProbeVerdict((int)response.StatusCode, body);
        }
        catch (OperationCanceledException)
        {
            return "The public relay address did not answer within " + ReachabilityProbeTimeout.TotalSeconds + " seconds.";
        }
        catch (Exception)
        {
            return HostingPlan.ProbeVerdict(null, null);
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
        // The internet relay closes with the session that opened it: the probe is cancelled, cloudflared's process tree is killed (it also dies on its
        // own at hotload and editor exit, see CloudflaredTool), and the loopback relay stops listening.
        reachabilityCancel?.Cancel();
        reachabilityCancel?.Dispose();
        reachabilityCancel = null;
        reachabilityTask = null;
        quickTunnel?.Dispose();
        quickTunnel = null;
        embeddedRelay?.Dispose();
        embeddedRelay = null;
        embeddedRelayError = null;
        hostingPlan = null;
        publicRelayUrl = null;
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
                value.Settings = SceneSettingsAdapter.Capture(value.Session.Scene);
                value.SettingsApplied = null;
                value.SettingsEcho = null;
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
        SessionLeaveOutcome sessionLeaveOutcome = SessionLeaveRules.Run(new SessionLeaveRequest(lifecycle, RuntimeLifecycle.IsTransitionInProgress, () => !archiveFailed && scenes.Values.Any((Tracked x) => x.Pending != null || x.SettingsPending != null || (x.Ready && x.Session.Scene.IsValid() && StateRules.Diff(x.Known, SceneAdapter.Capture(x.Session.Scene)).Count > 0)), (string reason) =>
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
        undoReset.Flush();
        undoResetScenes.Clear();
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
        ClearDrag();
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
        if (CollaborationFailureRules.ShouldArchive(everConnected, scenes.Values.Any((Tracked x) => x.Pending != null || x.SettingsPending != null)))
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
        if (error == null)
        {
            return;
        }
        // A failure of one user action (save, undo, redo, play toggle, a dock button) is reported and the session continues; only the integrity
        // failures docs/FAILURE-MODES.md keeps fail-closed still end it (ContentFailureRules.ClassifyActionFailure).
        if (ContentFailureRules.ClassifyActionFailure(error) == ActionFailureAction.Fail)
        {
            Fail(error.Message);
            return;
        }
        if (!ActionRefusalRules.IsRefusal(error))
        {
            GlobalGameNamespace.Log.Warning("Collaboration action: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error));
        }
        SetActionNotice(ContentFailureRules.ActionFailureNotice(error));
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
        Changes.Flush(SceneAdapter.Root, true);
        wire = null;
        server = null;
        projectFiles?.Dispose();
        projectFiles = null;
        blobs?.Release();
        blobs = null;
        BlobHub.Enabled = false;
        play?.Dispose();
        play = null;
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
            SettingsPending = x.Value.SettingsPending,
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
        if (Project.Current != null) Changes.Flush(SceneAdapter.Root, false);
        DrawRemoteDrags();
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
        if (mergeNoticeExpires != default(DateTime) && DateTime.UtcNow >= mergeNoticeExpires)
        {
            MergeNotice = null;
            mergeNoticeExpires = default;
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
            RunUndoResetIfDue();
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
                // Playing, or having no scene open, only pauses source synchronization (PlaySpectateRules): the connection completes,
                // presence flows and scene packets are held until play stops. Only a runtime a hotload interrupted still refuses.
                PlaySpectateDecision promoteDecision = PlaySpectateRules.Decide(IsLocalPlaying(sceneEditorSession), RuntimeLifecycle.State, flag, PlayModeSourceCoexistence.IsUsablePathKey(sourceScenePath), play?.SpectatingRun ?? false);
                if (!ContentFailureRules.MayPromoteConnection(promoteDecision, lifecycle.CanPromoteConnection))
                {
                    BeginConnectionTeardown(recoverWhenComplete: false);
                    if (promoteDecision == PlaySpectateDecision.FailStop)
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
                    ProjectManifest = projectManifestValue,
                    EngineBaseline = SceneAdapter.EngineBaseline()
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
            PlaySpectateDecision decision = PlaySpectateRules.Decide(IsLocalPlaying(sceneEditorSession2), RuntimeLifecycle.State, sourceAdmissible, PlayModeSourceCoexistence.IsUsablePathKey(sourceScenePath2), play?.SpectatingRun ?? false);
            if (decision != PlayDecision && decision == PlaySpectateDecision.Normal && !heldPackets.IsEmpty)
            {
                Status = "Connected • applying changes made while you were playing";
            }
            PlayDecision = decision;
            if (decision == PlaySpectateDecision.Normal && resyncWhenRoutable.Count > 0)
            {
                // Updates for these scenes were discarded while they could not take them: ask the host for each again (uncounted).
                foreach (string scene in resyncWhenRoutable.ToArray())
                {
                    Tracked affected = TrackedFor(scene);
                    if (affected != null && affected.Session.IsPlaying)
                    {
                        continue;
                    }
                    resyncWhenRoutable.Remove(scene);
                    if (affected != null && affected.Session.Scene.IsValid() && !affected.Recovery.Paused)
                    {
                        affected.Recovery.Requested();
                        RequestSceneResync(scene, affected);
                    }
                }
            }
            if (decision == PlaySpectateDecision.FailStop)
            {
                throw new InvalidOperationException("Stop play mode or complete the approved runtime lifecycle before applying source packets");
            }
            Packet result;
            PumpBlobs();
            if (decision == PlaySpectateDecision.Normal)
            {
                // Changes held while playing come first so they land in the order the host accepted them.
                while (heldPackets.TryTake(out result))
                {
                    ProcessIncoming(result);
                }
            }
            while (NextIncoming(out result))
            {
                if (decision != PlaySpectateDecision.Normal && HeldPacketBuffer.IsSceneChange(result.Kind))
                {
                    if (result.Scene != null && resyncWhenRoutable.Contains(result.Scene))
                    {
                        // That scene is resynchronized when play ends; the snapshot will contain this update.
                    }
                    else if (!heldPackets.TryHold(result))
                    {
                        // Applying some of the held updates and not others would leave scenes in a state nobody had: all are discarded
                        // and every affected scene is synchronized from the host when play ends (ContentFailureRules.DrainOverflow).
                        resyncWhenRoutable.UnionWith(ContentFailureRules.DrainOverflow(heldPackets, result));
                        SetActionNotice("More scene updates arrived while you were playing than this editor holds. The affected scenes will be synchronized from the host when play ends.");
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
            play?.Tick();
            if (DateTime.UtcNow >= nextPresence)
            {
                // Polled every SendCadence.PresenceMinimum (33 ms), but only SENT when something changed or a refresh is due (SendCadence): the
                // fast floor exists so remote markers can be drawn from interpolated samples, and an idle session still sends almost nothing.
                nextPresence = DateTime.UtcNow.AddMilliseconds(SendCadence.PresenceMinimum.TotalMilliseconds);
                SendPresence(force: false);
                TickDragSend();
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
            if (projectFiles != null)
            {
                projectFiles.Poll( p => wire.Send( p ), OpenFileScenes() );
                ShowFileNotices();
                if (!projectFiles.JoinReady)
                {
                    // Scenes are subscribed only once the files they reference have arrived (the scan below subscribes them).
                    Status = projectFiles.Progress;
                    return;
                }
                if (Status.StartsWith( "Receiving project files:", StringComparison.Ordinal )) Status = "Connected • live";
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
                    // Another source is playing: do not sample runtime mutations as authoring edits.
                    continue;
                }
                if (text == null || !text.StartsWith("scenes/"))
                {
                    continue;
                }
                if (!scenes.TryGetValue(text, out var value) || value.Session != sceneEditorSession3)
                {
                    if (value?.Pending != null)
                    {
                        // Keep the durable outbox until the host answers; replacing the tracked scene here would orphan it.
                        NoticeOnce("reopened-pending:" + text, "\"" + text + "\" is synchronized again once the host answers the edit you sent before it was closed. Your edit remains in the recovery outbox.");
                        continue;
                    }
                    if (sceneEditorSession3.HasUnsavedChanges)
                    {
                        try
                        {
                            WriteSceneBackup(text, new Tracked { Session = sceneEditorSession3 });
                            SetActionNotice("Unsent changes to the reopened scene were backed up before host synchronization.");
                        }
                        catch (Exception error)
                        {
                            NoticeOnce("reopened-backup:" + text, "\"" + text + "\" is not synchronized because a recovery copy of its unsaved changes could not be written (" +
                                ApplyRecovery.ShortReason(error.Message) + "). Save the scene, then reopen it.");
                            continue;
                        }
                    }
                    value = new Tracked
                    {
                        Session = sceneEditorSession3,
                        Known = SceneAdapter.Capture(sceneEditorSession3.Scene),
                        Settings = SceneSettingsAdapter.Capture(sceneEditorSession3.Scene),
                        DiskStamp = SceneAdapter.DiskStamp(text)
                    };
                    scenes[text] = value;
                    // The settings seed the host's copy when this editor is the scene's first subscriber; otherwise the snapshot replaces them.
                    wire.Send(new Packet
                    {
                        Kind = "subscribe",
                        Scene = text,
                        Objects = value.Known.Values.ToList(),
                        Settings = value.Settings
                    });
                }
                else
                {
                    if (!value.Ready || value.Pending != null || value.Recovery.Resyncing || value.Recovery.Paused)
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
                    bool stampFlagged = SceneSaveRules.ShouldFailOnDiskStamp(flag2, mismatchFor);
                    string diskBackup = null;
                    string diskError = null;
                    if (stampFlagged)
                    {
                        // The file on disk changed outside a collaboration save. The open, live scene is what everyone shares; the disk
                        // version is copied to the recovery folder so nothing written there is lost.
                        try
                        {
                            diskBackup = CopyDiskVersion(text);
                        }
                        catch (Exception error)
                        {
                            diskError = error.Message;
                        }
                    }
                    DiskStampAction stampAction = ContentFailureRules.OnDiskStampMismatch(stampFlagged, diskBackup != null);
                    if (stampAction == DiskStampAction.KeepLiveScene)
                    {
                        value.DiskStamp = dateTime;
                        value.StampMismatchSince = default;
                        if (!value.DiskWarningShown)
                        {
                            value.DiskWarningShown = true;
                            SetActionNotice("The file of \"" + text + "\" was changed outside collaboration. The open scene stays as it is for everyone; the file's version was copied to " + diskBackup + ".");
                        }
                    }
                    else if (stampAction == DiskStampAction.PauseScene)
                    {
                        PauseScene(text, value, "Synchronization of \"" + text + "\" is paused because its file was changed outside collaboration and a copy of that file could not be written (" +
                            ApplyRecovery.ShortReason(diskError) + "). Your changes to this scene are kept locally. Reopen the scene to try again.");
                        continue;
                    }
                    // Scene settings are host-authoritative state like objects (SettingsMerge): a local change is sent whole, one packet per scene
                    // at a time, and the host merges it per top-level key. LocalChange compares float-tolerantly and ignores what the engine
                    // itself refused to take, so neither noise nor residue is ever sent.
                    // Reading the scene is the engine's work: if it throws for the scene as a whole (an object that throws alone is kept
                    // by CaptureTolerant), only this scene stops synchronizing.
                    JsonObject settingsChange;
                    Dictionary<string, ObjectState> dictionary;
                    try
                    {
                        settingsChange = value.SettingsPending == null ? SettingsMerge.LocalChange(value.Settings, value.SettingsApplied, value.SettingsEcho, SceneSettingsAdapter.Capture(sceneEditorSession3.Scene)) : null;
                        dictionary = CaptureTolerant(text, value);
                    }
                    catch (Exception error)
                    {
                        PauseScene(text, value, "Synchronization of \"" + text + "\" is paused because this editor could not read the scene (" + ApplyRecovery.ShortReason(error.Message) +
                            "). Your changes to this scene are kept locally. Reopen the scene to try again.");
                        continue;
                    }
                    MaskTransientEcho(text, value, dictionary);
                    if (settingsChange != null)
                    {
                        SendSettings(text, value, settingsChange);
                    }
                    List<string> scanNotices = new List<string> { SceneAdapter.TakeNotices() };
                    // An object that is still in the scene but no longer saved with it (for example marked Not Saved) is absent from
                    // the capture, so the diff below deletes it from the shared scene; the person is told once.
                    foreach (string item in value.Known.Keys.Except(dictionary.Keys))
                    {
                        GameObject gameObject = sceneEditorSession3.Scene.Directory.FindByGuid(Guid.Parse(item));
                        if (gameObject != null && gameObject.IsValid && !gameObject.IsDestroyed)
                        {
                            scanNotices.Add(StateRules.NoLongerSavedNotice(StateRules.NameOf(value.Known[item], item)));
                        }
                    }
                    // Sibling order is part of each object's state (SiblingOrder.Key), so a reorder is an ordinary change below.
                    List<Change> list = StateRules.Diff(value.Known, dictionary);
                    NoteLocalDrag(text, list);
                    if (list.Count > 0)
                    {
                        lastLocalEdit = DateTime.UtcNow;
                    }
                    // A change that breaks a structural limit is held back instead of ending the session: the rest of the batch is
                    // sent, the held object stays as the session last confirmed it, and the reason is shown once.
                    List<HeldChange> held = new List<HeldChange>();
                    List<Change> sendable = StateRules.SendableChanges(value.Known, list, held);
                    NoteHeldChanges(text, held, scanNotices);
                    CountUnknownReservedKeys(sendable);
                    SetMergeNotice(ChangeRebase.JoinNotices(scanNotices.ToArray()));
                    bool flag3 = continuousEdits == 0 && !Editor.Application.MouseButtons.HasFlag(MouseButtons.Left);
                    if (sendable.Count == 0 && (!flag3 || value.Group.Count == 0))
                    {
                        continue;
                    }
                    SendEdit(text, value, sendable, flag3);
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
                Tracked routed = TrackedFor(result.Scene);
                string canonicalKey = ScenePacketRouting.CanonicalSceneKey(result.Scene);
                bool targetOpen = routed != null && routed.Session.Scene.IsValid() && SceneEditorSession.All.Contains(routed.Session) &&
                    ScenePacketRouting.CanonicalSceneKey(SceneAdapter.PathOf(routed.Session)) == canonicalKey;
                switch (ContentFailureRules.ClassifyRouteFailure(canonicalKey != null, routed != null, targetOpen))
                {
                    case RouteFailureAction.IgnoreClosed:
                        NoticeClosedPacket(result.Scene);
                        return;
                    case RouteFailureAction.ResyncWhenRoutable:
                        // The scene is open but cannot take host state now (it is playing). The update is not lost: the scene is
                        // synchronized from the host once it can.
                        resyncWhenRoutable.Add(result.Scene);
                        return;
                    default:
                        throw new InvalidOperationException((scenePacketRouteResult.Error ?? "A scene update named no valid scene.") +
                            " The host is not speaking this protocol. Leave the session and reconnect to the correct host.");
                }
            }
        }
        lastReceive = DateTime.UtcNow;
        // Recorded here and not in Receive so that every packet that carries a revision is counted, whichever branch handles it.
        if (result.Kind == "snapshot")
        {
            lastRevision = result.Revision;
        }
        else if ((result.Kind == "accepted" || result.Kind == SettingsMerge.PacketKind) && result.Revision > lastRevision)
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

    // The reason last shown for each held object, per scene, so a change held on every scan is announced once and again only
    // when its reason changes. An object that is sent again is forgotten.
    private readonly Dictionary<string, Dictionary<string, string>> heldReasons = new Dictionary<string, Dictionary<string, string>>();

    private void NoteHeldChanges(string scene, List<HeldChange> held, List<string> notices)
    {
        if (!heldReasons.TryGetValue(scene, out Dictionary<string, string> shown))
        {
            shown = new Dictionary<string, string>();
            heldReasons[scene] = shown;
        }
        foreach (string id in shown.Keys.Except(held.Select((HeldChange x) => x.Change.Id)).ToList())
        {
            shown.Remove(id);
        }
        foreach (HeldChange change in held)
        {
            if (!shown.TryGetValue(change.Change.Id, out string reason) || reason != change.Reason)
            {
                shown[change.Change.Id] = change.Reason;
                notices.Add(change.Reason);
                GlobalGameNamespace.Log.Warning("Collaboration: " + change.Reason);
            }
        }
    }

    // Reserved (__) property names outside StateRules.KnownReservedKeys are allowed, because a newer engine may write new ones;
    // each distinct name is counted and logged once so it can be added to the known set.
    private readonly HashSet<string> unknownReservedKeys = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>How many distinct reserved (<c>__</c>) property names outside the known set this editor has sent.</summary>
    public int UnknownReservedKeyCount => unknownReservedKeys.Count;

    private void CountUnknownReservedKeys(List<Change> changes)
    {
        foreach (Change change in changes)
        {
            if ((object)change.After == null)
            {
                continue;
            }
            foreach (string key in StateRules.FindUnknownReservedKeys(change.After.Data))
            {
                if (unknownReservedKeys.Add(key))
                {
                    GlobalGameNamespace.Log.Info("Collaboration: the scene uses the reserved property name \"" + key + "\", which is not in the known set; it is synchronised as is.");
                }
            }
        }
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

    // Sends the scene's settings as they are now, with the confirmed settings they were changed from. A change above the size bound is not
    // sent at all: the host would refuse it, and the refusal is the same either way.
    private void SendSettings(string scene, Tracked tracked, JsonObject after)
    {
        string sizeError = SettingsMerge.SizeError(after);
        if (sizeError != null)
        {
            RefuseSettings(tracked, sizeError);
            return;
        }
        tracked.SettingsPending = new Packet
        {
            Kind = SettingsMerge.PacketKind,
            Scene = scene,
            Peer = Peer,
            SettingsBefore = SettingsMerge.Clone(tracked.Settings),
            Settings = after
        };
        sentSettings.Add(tracked.SettingsPending.Id);
        wire.Send(tracked.SettingsPending);
        Status = "Connected • scene settings awaiting acknowledgement";
    }

    // A refused settings change is not retried: the scene takes back the host's settings, which this editor already holds as the confirmed
    // settings (the same settings the next snapshot would carry), and the user is told why. Without the revert the scan would find the
    // same change again and resend it every 50 ms.
    private void RefuseSettings(Tracked tracked, string reason)
    {
        tracked.SettingsPending = null;
        SetActionNotice(reason);
        ShowToast("Scene settings change refused", reason, Theme.Primary, 15f);
        if (tracked.Settings != null)
        {
            WriteSettings(tracked, tracked.Settings);
        }
    }

    private bool HasUnsentSettings(Tracked tracked)
    {
        return tracked.Session.Scene.IsValid() && SettingsMerge.LocalChange(tracked.Settings, tracked.SettingsApplied, tracked.SettingsEcho, SceneSettingsAdapter.Capture(tracked.Session.Scene)) != null;
    }

    // Writes settings into the live scene when they differ from what it has, and records what was written and what the engine then reported.
    private void WriteSettings(Tracked tracked, JsonObject settings)
    {
        if (!tracked.Session.Scene.IsValid() || !SceneEditorSession.All.Contains(tracked.Session))
        {
            return;
        }
        if (!StateRules.ValuesEqual(SceneSettingsAdapter.Capture(tracked.Session.Scene), settings))
        {
            List<string> skipped = SceneSettingsAdapter.Apply(tracked.Session.Scene, settings);
            if (skipped.Count > 0)
            {
                SetActionNotice("Some scene settings could not be applied in this editor and were left unchanged: " + string.Join("; ", skipped));
            }
        }
        tracked.SettingsApplied = SettingsMerge.Clone(settings);
        tracked.SettingsEcho = SceneSettingsAdapter.Capture(tracked.Session.Scene);
    }

    // A settings broadcast from the host: the merged settings become the confirmed settings. A change of this editor's that the host has not
    // seen yet (made after the last send, or not sent yet) is kept on top of them, key by key, exactly as an unsent object edit is kept on
    // top of a remote change, and the next scan sends it.
    private void ReceiveSettings(Tracked tracked, Packet p)
    {
        if (!seen.Add(p.Peer + ":" + p.Id))
        {
            // A receipt repeated after a resend was applied the first time; applying it again would undo whatever was accepted since.
            if (tracked.SettingsPending?.Id == p.Id)
            {
                tracked.SettingsPending = null;
            }
            return;
        }
        if (p.Settings == null)
        {
            throw new InvalidOperationException("The host sent a scene settings change without settings.");
        }
        bool own = p.Peer == Peer;
        if (own && tracked.SettingsPending?.Id == p.Id)
        {
            tracked.SettingsPending = null;
        }
        List<string> notices = new List<string> { p.Notice };
        JsonObject target = p.Settings;
        if (tracked.Session.Scene.IsValid() && SceneEditorSession.All.Contains(tracked.Session))
        {
            JsonObject local = SettingsMerge.LocalChange(tracked.Settings, tracked.SettingsApplied, tracked.SettingsEcho, SceneSettingsAdapter.Capture(tracked.Session.Scene));
            if (local != null && tracked.Settings != null && SettingsMerge.TryMerge(p.Settings, tracked.Settings, local, out JsonObject kept, out List<string> clashed))
            {
                target = kept;
                if (!own)
                {
                    notices.Add(SettingsMerge.LocalKeptNotice(clashed));
                }
            }
        }
        tracked.Settings = SettingsMerge.Clone(p.Settings);
        WriteSettings(tracked, target);
        SetMergeNotice(ChangeRebase.JoinNotices(notices.ToArray()));
        Status = "Connected • revision " + p.Revision;
    }

    private Tracked TrackedFor(string scene)
    {
        Dictionary<string, object> trackedByScene = scenes.ToDictionary((KeyValuePair<string, Tracked> pair) => pair.Key, (KeyValuePair<string, Tracked> pair) => (object)pair.Value);
        return (scene != null && ScenePacketRouting.TryResolveTracked(scene, trackedByScene, out string _, out object target)) ? target as Tracked : null;
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
            if (!tracked.Ready || tracked.Pending != null || tracked.SettingsPending != null || IsLocalPlaying(active))
            {
                return;
            }
            // The host's digest covers its settings for the scene too (SessionServer), so this one covers the confirmed settings.
            string digest = SceneDigest.Compute(tracked.Known, tracked.Settings);
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

    // ---- Content failures heal one scene (ApplyRecovery, ContentFailureRules, docs/FAILURE-MODES.md) ----------------------------------------------------

    // Logged once per scene: a packet for a scene this editor does not have open (closed meanwhile). Reopening it subscribes again.
    private void NoticeClosedPacket(string scene)
    {
        if (closedPacketNotices.Add(scene ?? ""))
        {
            GlobalGameNamespace.Log.Info("Collaboration: an update for \"" + scene + "\", which is not open in this editor, was ignored. Reopening the scene synchronizes it again.");
        }
    }

    // A notice that would otherwise be set on every scan (50 ms) is shown once per key.
    private void NoticeOnce(string key, string notice)
    {
        if (onceNotices.Add(key))
        {
            SetActionNotice(notice);
        }
    }

    // Copies a scene's file on disk to the recovery folder under a name never used before and returns the copy's path. Nothing to
    // copy (the file was removed) counts as preserved.
    private string CopyDiskVersion(string scene)
    {
        string disk = Path.Combine(SceneAdapter.Root, "Assets", scene);
        if (!File.Exists(disk))
        {
            return "(no file on disk)";
        }
        string folder = Path.Combine(SceneAdapter.Root, ".collaboration", "recovery");
        Directory.CreateDirectory(folder);
        string name = RecoveryBackup.BuildFileName(scene, DateTime.UtcNow);
        string stem = name.EndsWith(".scene", StringComparison.Ordinal) ? name.Substring(0, name.Length - ".scene".Length) : name;
        string path = Path.Combine(folder, stem + "-disk.scene");
        for (int counter = 2; File.Exists(path) && counter < 100; counter++)
        {
            path = Path.Combine(folder, stem + "-disk-" + counter + ".scene");
        }
        File.Copy(disk, path, overwrite: false);
        return path;
    }

    // Stops synchronizing one scene; the session, presence and every other scene continue. Reopening the scene creates a new tracked
    // scene and so clears the pause.
    private void PauseScene(string scene, Tracked tracked, string notice)
    {
        tracked.Recovery.Pause();
        tracked.Ready = false;
        tracked.HeldLocal = null;
        SetActionNotice(notice);
        ShowToast("Scene synchronization paused", notice, Theme.Primary, 15f);
    }

    private static string BackupFailedPauseNotice(string scene, string reason)
    {
        return "Synchronization of \"" + scene + "\" is paused because a recovery copy of the scene could not be written (" + ApplyRecovery.ShortReason(reason) +
            "). Your changes to this scene are kept locally. Free disk space or check folder permissions, then reopen the scene to try again.";
    }

    // Asks the host for the scene again: the same subscribe a reconnect sends. The answer is an ordinary snapshot.
    private void RequestSceneResync(string scene, Tracked tracked)
    {
        if (tracked.Recovery.Paused || !tracked.Session.Scene.IsValid())
        {
            return;
        }
        tracked.Ready = false;
        wire.Send(new Packet
        {
            Kind = "subscribe",
            Scene = scene,
            Objects = tracked.Known.Values.ToList(),
            Settings = tracked.Settings
        });
    }

    // The live scene as a scan sees it: an object whose capture throws keeps its confirmed state, and so does every object this editor
    // could not show. Neither is ever sent as this person's change or deletion.
    private Dictionary<string, ObjectState> CaptureTolerant(string scene, Tracked tracked)
    {
        HashSet<string> failures = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, ObjectState> live = SceneAdapter.Capture(tracked.Session.Scene, tracked.Known, failures, limitGeometryRate: true);
        foreach (string id in failures)
        {
            if (tracked.SkippedObjects.Add(id))
            {
                string name = tracked.Known.TryGetValue(id, out ObjectState known) ? StateRules.NameOf(known, id) : id;
                SetActionNotice("\"" + name + "\" in \"" + scene + "\" could not be read by this editor. Its last synchronized state is kept for everyone and it is not sent as a change.");
            }
        }
        return ApplyRecovery.PreserveSkipped(tracked.Known, live, tracked.SkippedObjects);
    }

    // The live scene as the editor shows it, for building an apply target: a skipped object keeps the version shown here, so applying
    // the target never touches it again. (An object whose capture throws is listed with its confirmed state.)
    private Dictionary<string, ObjectState> CaptureShown(string scene, Tracked tracked)
    {
        return SceneAdapter.Capture(tracked.Session.Scene, tracked.Known, new HashSet<string>(StringComparer.Ordinal));
    }

    private static Func<Dictionary<string, ObjectState>, Exception> TryApply(Tracked tracked)
    {
        return candidate =>
        {
            try
            {
                SceneAdapter.Apply(tracked.Session, candidate);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        };
    }

    // Applying a host packet threw in this editor. The decision is ApplyRecovery's: resync (the live scene is put back where it was when
    // that is known, backed up, and the host is asked for the scene again), isolate (returned to the caller, which owns the snapshot), or
    // pause. Never Fail: the cause is this editor's engine, not the session.
    private ApplyRecoveryDecision RecoverScene(string scene, Tracked tracked, Exception error, Dictionary<string, ObjectState> before, bool snapshot)
    {
        ApplyRecoveryDecision decision = tracked.Recovery.Failed(DateTime.UtcNow, snapshot);
        GlobalGameNamespace.Log.Warning("Collaboration: applying a host update to \"" + scene + "\" failed (" + decision + "): " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic("", error));
        if (decision == ApplyRecoveryDecision.SkipObjects)
        {
            return decision;
        }
        if (before != null)
        {
            // A half-applied packet must not be read back as this person's edits. If the scene cannot be put back, the state from before
            // the packet is what gets replayed after the snapshot.
            if (TryApply(tracked)(before) != null)
            {
                tracked.HeldLocal ??= before;
            }
        }
        string backup;
        try
        {
            backup = WriteSceneBackup(scene, tracked);
        }
        catch (Exception backupError)
        {
            PauseScene(scene, tracked, BackupFailedPauseNotice(scene, backupError.Message));
            return ApplyRecoveryDecision.Pause;
        }
        if (decision == ApplyRecoveryDecision.Pause)
        {
            PauseScene(scene, tracked, ApplyRecovery.PausedNotice(scene));
            return decision;
        }
        RequestSceneResync(scene, tracked);
        SetActionNotice("An update to \"" + scene + "\" could not be applied in your editor (" + ApplyRecovery.ShortReason(error?.Message) + "). A copy of your scene was saved to " +
            backup + ". The scene is being synchronized again; your unsent changes are kept and sent afterwards.");
        return decision;
    }

    // Applies a snapshot. Returns false when the scene did not take it (resync requested, paused); the caller then leaves Known as it is.
    // While the scene resyncs, the edits held since the failure are replayed on top of the fresh state (ApplyRecovery.ReplayHeld) and the next
    // scan sends them as ordinary edits.
    private bool ApplyHostScene(string scene, Tracked tracked, Dictionary<string, ObjectState> target)
    {
        if (tracked.Recovery.Paused)
        {
            return false;
        }
        // A malformed host graph is not an engine limitation of this editor: it stays a protocol failure.
        StateRules.ValidateGraph(target);
        bool resyncing = tracked.Recovery.Resyncing;
        // live: what the editor shows (the starting point of isolation and of a put-back); held: the person's edits as a scan reads them.
        Dictionary<string, ObjectState> live = CaptureShown(scene, tracked);
        Dictionary<string, ObjectState> heldBase = new Dictionary<string, ObjectState>(tracked.Known);
        Dictionary<string, ObjectState> held = tracked.HeldLocal ?? CaptureTolerant(scene, tracked);
        Dictionary<string, ObjectState> shown = target;
        try
        {
            SceneAdapter.Apply(tracked.Session, target);
            tracked.SkippedObjects.Clear();
        }
        catch (Exception error)
        {
            if (RecoverScene(scene, tracked, error, live, snapshot: true) != ApplyRecoveryDecision.SkipObjects)
            {
                return false;
            }
            // The fresh snapshot fails as well: show what this editor can, leave out what it cannot. The host's JSON of every skipped object
            // becomes Known with the rest, so nothing is lost and nothing is sent back.
            Dictionary<string, string> reasons = new Dictionary<string, string>(StringComparer.Ordinal);
            shown = ApplyRecovery.IsolateObjects(target, live, TryApply(tracked), reasons);
            Exception final = TryApply(tracked)(shown);
            if (final != null)
            {
                PauseScene(scene, tracked, ApplyRecovery.PausedNotice(scene));
                return false;
            }
            List<string> skippedNotices = new List<string>();
            foreach (KeyValuePair<string, string> skipped in reasons)
            {
                ObjectState state = target.GetValueOrDefault(skipped.Key) ?? live.GetValueOrDefault(skipped.Key);
                if (!tracked.SkippedObjects.Contains(skipped.Key))
                {
                    skippedNotices.Add(ApplyRecovery.SkippedNotice(StateRules.NameOf(state, skipped.Key), skipped.Value));
                }
            }
            tracked.SkippedObjects = new HashSet<string>(reasons.Keys, StringComparer.Ordinal);
            string joined = ChangeRebase.JoinNotices(skippedNotices.ToArray());
            if (joined != null)
            {
                SetActionNotice(joined);
                ShowToast("Scene objects not shown", joined, Theme.Primary, 15f);
            }
        }
        if (resyncing && !tracked.DiscardLocalOnResync)
        {
            List<string> notices = new List<string>();
            Dictionary<string, ObjectState> replayed = ApplyRecovery.ReplayHeld(heldBase, held, target, shown, tracked.SkippedObjects, notices);
            if (StateRules.Diff(shown, replayed).Count > 0)
            {
                Exception replayError = TryApply(tracked)(replayed);
                if (replayError != null)
                {
                    // The edits cannot be shown on top of the host's scene in this editor. They are in the recovery copy written before
                    // the snapshot; the scene follows the host.
                    TryApply(tracked)(shown);
                    notices.Add("Your unsent changes to \"" + scene + "\" could not be reapplied on top of the host's scene (" + ApplyRecovery.ShortReason(replayError.Message) +
                        "). They are kept in the recovery copy.");
                }
            }
            SetMergeNotice(ChangeRebase.JoinNotices(notices.ToArray()));
        }
        tracked.HeldLocal = null;
        tracked.DiscardLocalOnResync = false;
        return true;
    }

    private void Receive(Packet p)
    {
        if (projectFiles != null && projectFiles.Handle( p, packet => wire.Send( packet ), OpenFileScenes() ))
        {
            return;
        }
        if (play != null && play.Handle(p))
        {
            return;
        }
        if (p.Kind == "welcome")
        {
            if (restoring && hostSession != null && hostSession != p.Session)
            {
                Fail("Host session ended or restarted. Preserve recovery data, sync saved scenes and start a new session.");
                return;
            }
            hostSession = p.Session;
            hostTakesTransients = p.Features != null && p.Features.Contains(TransientLimits.Feature);
            ClearDrag();
            projectFiles?.Dispose();
            projectFiles = null;
            // Geometry is replaced by placeholders only for a host that advertises the channel that carries it; an older host would take the
            // placeholder for the mesh.
            blobs?.Release();
            blobs = null;
            BlobHub.Enabled = false;
            if (p.Features != null && p.Features.Contains( BlobLimits.Feature ))
            {
                BlobHub.Store.AttachDisk( SceneAdapter.Root, BlobLimits.DiskCacheBytes );
                blobs = new BlobExchange( BlobHub.Store, packet => wire.Send( packet ), () => DateTime.UtcNow );
                BlobHub.Enabled = true;
            }
            // Only a host that advertises file synchronization is asked for its index; an older host gets the session as before.
            if (p.Features != null && p.Features.Contains( FileSyncLimits.Feature ))
            {
                // The welcome carries no verdict on the host's identity, so a guest cannot verify it: code from the host is received only when the person
                // ticked "Allow peers whose Steam identity could not be verified" (fail closed). HostIdentityVerified stays false until a verdict exists.
                projectFiles = new FileSyncClient( Project.Current.RootDirectory.FullName )
                {
                    FileChanged = RefreshAsset,
                    Changes = Changes,
                    HostHasCode = p.Features.Contains( CodeTransferLimits.Feature ),
                    HostIdentityVerified = false,
                    AllowUnverifiedHost = guestAllowUnverified
                };
                projectFiles.Begin( packet => wire.Send( packet ) );
            }
            // Shared play is opt-in on both sides: this computer chose it, and the host says it relays it. Otherwise nothing of it exists here.
            play?.Dispose();
            play = SharedPlayPreference.Enabled && p.Features != null && p.Features.Contains(PlayRelayPolicy.Feature)
                ? new SharedPlayClient(packet => wire.Send(packet), Peer, SetActionNotice)
                : null;
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
                    if (scene.Value.SettingsPending != null)
                    {
                        // The same id: a host that already applied it answers with the receipt it gave the first time.
                        wire.Send(scene.Value.SettingsPending);
                    }
                    if (scene.Value.Session.Scene.IsValid())
                    {
                        wire.Send(new Packet
                        {
                            Kind = "subscribe",
                            Scene = scene.Key,
                            Objects = scene.Value.Known.Values.ToList(),
                            Settings = scene.Value.Settings
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
            if (p.Kind == "reject" && p.Id != null && sentSettings.Contains(p.Id))
            {
                // The host refused a settings change (for instance above the size bound). That is the host deciding, not a broken session.
                Tracked refusedScene = TrackedFor(p.Scene);
                if (refusedScene?.SettingsPending != null && refusedScene.SettingsPending.Id == p.Id)
                {
                    RefuseSettings(refusedScene, "The host refused your change to the scene settings: " + text);
                }
                return;
            }
            if (p.Kind == "reject" && p.Scene == null)
            {
                Fail(text, AutoRecoveryRules.ClassifyRefusal(p.Error));
                return;
            }
            if (restoring && p.Kind == "reject")
            {
                RecoveryNotice = CollaborationDiagnostics.Bounded("Rejected offline edit retained in " + RecoveryPath + ": " + text);
            }
            else if (p.Kind == "fatal")
            {
                Fail(text, AutoRecoveryRules.ClassifyRefusal(p.Error));
            }
            if (p.Kind == "reject")
            {
                Tracked tracked = TrackedFor(p.Scene);
                if (tracked == null)
                {
                    NoticeClosedPacket(p.Scene);
                    return;
                }
                if (tracked.Pending?.Id != p.Id)
                {
                    return;
                }
                // The host refused this edit. That is the host deciding about content, not a broken session: the scene is copied to the
                // recovery folder, the edit is dropped from the outbox, and the scene is synchronized from the host, replacing the refused
                // state (re-sending it would only be refused again).
                ForgetPending(tracked, p.Id);
                if (!tracked.Session.Scene.IsValid() || tracked.Recovery.Paused)
                {
                    return;
                }
                string refusedBackup;
                try
                {
                    refusedBackup = WriteSceneBackup(p.Scene, tracked);
                }
                catch (Exception error)
                {
                    PauseScene(p.Scene, tracked, BackupFailedPauseNotice(p.Scene, error.Message));
                    return;
                }
                tracked.Recovery.Requested();
                tracked.DiscardLocalOnResync = true;
                RequestSceneResync(p.Scene, tracked);
                string refused = "The host refused your change to \"" + p.Scene + "\" (" + text + "). The scene is synchronized from the host again; your version was saved to " + refusedBackup + ".";
                SetActionNotice(refused);
                ShowToast("Change refused by the host", refused, Theme.Primary, 15f);
            }
        }
        else if (p.Kind == "presence")
        {
            if (p.Peer != Peer)
            {
                Roster[p.Peer] = p;
                FeedPeerSmoothing(p);
            }
        }
        else if (p.Kind == TransientLimits.Kind)
        {
            ReceiveTransient(p);
        }
        else if (p.Kind == "left")
        {
            Roster.Remove(p.Peer);
            peerSmoothing.Remove(p.Peer);
        }
        else
        {
            if (p.Kind == "pong")
            {
                return;
            }
            if (p.Scene == null)
            {
                throw new InvalidOperationException("The host sent a scene update without a scene. The host is not speaking this protocol. Leave the session and reconnect to the correct host.");
            }
            if (!ScenePacketRouting.TryResolveTracked(p.Scene, ((IEnumerable<KeyValuePair<string, Tracked>>)scenes).ToDictionary((Func<KeyValuePair<string, Tracked>, string>)((KeyValuePair<string, Tracked> pair) => pair.Key), (Func<KeyValuePair<string, Tracked>, object>)((KeyValuePair<string, Tracked> pair) => pair.Value)), out canonical, out object target2) || !(target2 is Tracked tracked2))
            {
                NoticeClosedPacket(p.Scene);
                return;
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
                    tracked2.Settings = SettingsMerge.Clone(p.Settings);
                    return;
                }
                if (tracked2.Recovery.Paused)
                {
                    // A paused scene follows the host on paper only: nothing is applied or sent until it is reopened.
                    tracked2.Known = dictionary;
                    tracked2.Settings = SettingsMerge.Clone(p.Settings);
                    return;
                }
                string restoredNotice = null;
                if (!restoring)
                {
                    if (!tracked2.Recovery.Resyncing && tracked2.Ready && (tracked2.Pending != null || tracked2.SettingsPending != null))
                    {
                        // A resync that arrives while an edit awaits the host's answer would replace the scene under that edit, and the answer
                        // would then be applied to a scene it was not made against. Nothing is lost by waiting: the digest is not sent while
                        // an edit is pending, and the next ping after the answer finds the drift again if it is still there.
                        return;
                    }
                    // Unsent scene settings count as much as unsent objects: the copy is Scene.Serialize, which holds the scene's Properties
                    // (its settings) next to its GameObjects. Settings that cannot even be read are treated as unsent.
                    bool unsentSettings;
                    try
                    {
                        unsentSettings = tracked2.Settings != null && HasUnsentSettings(tracked2);
                    }
                    catch (Exception)
                    {
                        unsentSettings = true;
                    }
                    if (ApplyRecovery.NeedsBackup(StateRules.Diff(tracked2.Known, CaptureTolerant(p.Scene, tracked2)).Count > 0, unsentSettings))
                    {
                        // Unsent edits are about to be replaced by the host's scene: they are written to a file first, and if that fails
                        // the host's scene is not applied, because unsaved work is never replaced without a copy. Only this scene pauses.
                        try
                        {
                            restoredNotice = RecoveryBackup.BuildNotice(p.Scene, WriteSceneBackup(p.Scene, tracked2));
                        }
                        catch (Exception error)
                        {
                            PauseScene(p.Scene, tracked2, BackupFailedPauseNotice(p.Scene, error.Message));
                            return;
                        }
                    }
                }
                if (!ApplyHostScene(p.Scene, tracked2, dictionary)) return;
                tracked2.Known = dictionary;
                if (tracked2.Recovery.Resyncing && tracked2.Pending != null)
                {
                    ForgetPending(tracked2, tracked2.Pending.Id);
                    tracked2.Group.Clear();
                }
                if (tracked2.Recovery.Resyncing) tracked2.SettingsPending = null;
                tracked2.Recovery.Succeeded();
                // The host's settings replace this editor's, as its objects do. A host holding none (its first subscriber predates settings
                // sync) leaves the scene's settings alone, and with nothing confirmed no local change is ever sent for the scene.
                tracked2.Settings = SettingsMerge.Clone(p.Settings);
                tracked2.SettingsApplied = null;
                tracked2.SettingsEcho = null;
                if (p.Settings != null)
                {
                    try
                    {
                        WriteSettings(tracked2, p.Settings);
                    }
                    catch (Exception error)
                    {
                        // The objects are in; the settings are not. The next snapshot writes them again.
                        tracked2.Recovery.Succeeded();
                        RecoverScene(p.Scene, tracked2, error, null, snapshot: false);
                        return;
                    }
                }
                tracked2.Ready = true;
                tracked2.Session.UndoSystem.Initialize();
                tracked2.Group.Clear();
                tracked2.Undo.Clear();
                MirrorNativeUndo(tracked2);
                if (scenes.Values.All((Tracked x) => x.Ready || x.Recovery.Paused || !x.Session.Scene.IsValid()))
                {
                    restoring = false;
                }
                if (restoredNotice != null)
                {
                    SetActionNotice(restoredNotice);
                    ShowToast("Scene restored from the host", restoredNotice, Theme.Primary, 15f);
                }
            }
            else if (p.Kind == SettingsMerge.PacketKind)
            {
                if (p.Settings == null)
                {
                    // Protocol integrity: a settings packet always carries the merged settings.
                    throw new InvalidOperationException("The host sent a scene settings change without settings. The host is not speaking this protocol. Leave the session and reconnect to the correct host.");
                }
                if (!tracked2.Recovery.AppliesIncoming)
                {
                    // Resyncing: the snapshot carries these settings. Paused: they are recorded, not applied.
                    seen.Add(p.Peer + ":" + p.Id);
                    if (tracked2.Recovery.Paused)
                    {
                        tracked2.Settings = SettingsMerge.Clone(p.Settings);
                    }
                    if (tracked2.SettingsPending?.Id == p.Id)
                    {
                        tracked2.SettingsPending = null;
                    }
                    return;
                }
                try
                {
                    ReceiveSettings(tracked2, p);
                }
                catch (Exception error)
                {
                    RecoverScene(p.Scene, tracked2, error, null, snapshot: false);
                }
            }
            else
            {
                if (p.Kind != "accepted")
                {
                    return;
                }
                if (tracked2.Recovery.Resyncing)
                {
                    // The snapshot requested for this scene contains this change; the outbox is settled when it arrives.
                    return;
                }
                if (tracked2.Recovery.Paused)
                {
                    // Recorded, not applied: the scene follows the host on paper until it is reopened.
                    if (seen.Add(p.Peer + ":" + p.Id))
                    {
                        ApplyRecovery.Record(tracked2.Known, p.Changes);
                    }
                    ForgetPending(tracked2, p.Id);
                    return;
                }
                if (!seen.Add(p.Peer + ":" + p.Id))
                {
                    ForgetPending(tracked2, p.Id);
                    return;
                }
                AcceptedOperations++;
                // Sentences for the user gathered while this packet is processed: the host's own notice first, then ours.
                List<string> rebaseNotices = new List<string> { p.Notice };
                if (p.Peer == Peer)
                {
                    // What this editor sent for the packet being answered, when it is still the pending one. A receipt that outlived it
                    // (an outbox replay after a reconnect) has no sent edit to compare with.
                    Packet sentPacket = (tracked2.Pending != null && tracked2.Pending.Id == p.Id) ? tracked2.Pending : null;
                    bool liveScene = tracked2.Session.Scene.IsValid() && SceneEditorSession.All.Contains(tracked2.Session);
                    Dictionary<string, ObjectState> rebased = null;
                    bool rebaseChangesScene = false;
                    foreach (Change change in p.Changes)
                    {
                        if (liveScene)
                        {
                            ObjectState sentAfter = sentPacket?.Changes?.FirstOrDefault((Change x) => x.Id == change.Id)?.After;
                            if (ChangeRebase.NeedsOwnReceiptRebase(p.Merged, sentPacket != null, sentAfter, change.After))
                            {
                                // The host kept something other than what was sent. Take its result into the live scene before Known
                                // moves on, otherwise the next scan sees scene != Known and sends the old local state back over it.
                                rebased ??= SceneAdapter.Capture(tracked2.Session.Scene);
                                ObjectState local = rebased.GetValueOrDefault(change.Id);
                                RebaseResult result = ChangeRebase.RebaseOntoOwnReceipt(change.Id, sentAfter, local, change.After);
                                rebaseNotices.Add(result.Notice);
                                if (!StateRules.Equal(result.NewLocal, local))
                                {
                                    rebaseChangesScene = true;
                                }
                                if ((object)result.NewLocal == null)
                                {
                                    rebased.Remove(change.Id);
                                }
                                else
                                {
                                    rebased[change.Id] = result.NewLocal;
                                }
                            }
                        }
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
                    if (rebaseChangesScene)
                    {
                        Exception applyError = TryApply(tracked2)(rebased);
                        if (applyError != null)
                        {
                            // Known now holds what the host kept; rebased is the person's scene on top of it, which is what is replayed.
                            ForgetPending(tracked2, p.Id);
                            tracked2.HeldLocal = rebased;
                            RecoverScene(p.Scene, tracked2, applyError, null, snapshot: false);
                            return;
                        }
                        tracked2.Session.UndoSystem.Initialize();
                        MirrorNativeUndo(tracked2);
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
                    Dictionary<string, ObjectState> dictionary2 = (flag ? CaptureShown(p.Scene, tracked2) : new Dictionary<string, ObjectState>(tracked2.Known));
                    NoteAcceptedForDrag(p, tracked2, dictionary2);
                    // What the editor showed and what was confirmed before this change, to put both back if the engine refuses it.
                    Dictionary<string, ObjectState> shownBefore = new Dictionary<string, ObjectState>(dictionary2);
                    Dictionary<string, ObjectState> knownPrior = new Dictionary<string, ObjectState>(StringComparer.Ordinal);
                    foreach (Change change2 in p.Changes)
                    {
                        if (!knownPrior.ContainsKey(change2.Id))
                        {
                            knownPrior[change2.Id] = tracked2.Known.GetValueOrDefault(change2.Id);
                        }
                        if ((object)change2.After != null && tracked2.SkippedObjects.Contains(change2.Id))
                        {
                            // An object this editor cannot show stays as it is shown here; the host's version is recorded, never applied
                            // (applying it is what failed) and never sent back.
                            tracked2.Known[change2.Id] = change2.After;
                            continue;
                        }
                        // The user may hold an edit the host has not seen (mid-drag, or not yet sent). It is kept on top of the host's
                        // change instead of failing the session; ChangeRebase never throws on divergence because the host is the authority.
                        // A skipped object has no edit of this person's: its local side is what the host last confirmed.
                        ObjectState localSide = tracked2.SkippedObjects.Contains(change2.Id) ? tracked2.Known.GetValueOrDefault(change2.Id) : dictionary2.GetValueOrDefault(change2.Id);
                        RebaseResult rebase = ChangeRebase.RebaseOntoRemoteChange(change2.Id, tracked2.Known.GetValueOrDefault(change2.Id), localSide, change2.Before, change2.After);
                        rebaseNotices.Add(rebase.Notice);
                        if ((object)rebase.NewLocal == null)
                        {
                            dictionary2.Remove(change2.Id);
                        }
                        else
                        {
                            dictionary2[change2.Id] = rebase.NewLocal;
                        }
                        if ((object)change2.After == null)
                        {
                            tracked2.Known.Remove(change2.Id);
                        }
                        else
                        {
                            tracked2.Known[change2.Id] = change2.After;
                        }
                    }
                    if (flag)
                    {
                        Exception applyError = TryApply(tracked2)(dictionary2);
                        if (applyError != null)
                        {
                            // Undo the bookkeeping too: the scene shows what it showed before, Known is what it was, and the snapshot
                            // brings this change in. The person's unsent edits are then exactly Known against the scene, and are replayed.
                            tracked2.Known = ApplyRecovery.WithPrior(tracked2.Known, knownPrior);
                            RecoverScene(p.Scene, tracked2, applyError, shownBefore, snapshot: false);
                            return;
                        }
                        foreach (Change applied in p.Changes)
                        {
                            if ((object)applied.After == null)
                            {
                                tracked2.SkippedObjects.Remove(applied.Id);
                            }
                        }
                        // The scene above is already applied. Only the history reset is coalesced (UndoResetScheduler): a drag of another
                        // person would otherwise reset the whole native undo history on every packet.
                        RequestUndoReset(tracked2);
                    }
                }
                SetMergeNotice(ChangeRebase.JoinNotices(rebaseNotices.ToArray()));
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
        if (value.SettingsPending != null || HasUnsentSettings(value))
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
        if (value.SettingsPending != null)
        {
            return "a change to the scene settings is still waiting for the host to acknowledge it";
        }
        if (HasUnsentSettings(value))
        {
            return "a change to the scene settings has not been sent to the host yet";
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
                bool isHost = IsHost;
                bool flag4 = !isHost && HasUnsavedTrackedChanges();
                bool flag5 = reconnectQueued || reconnectTokenTask != null || connecting != null;
                if (isHost)
                {
                    DeclineAutoRecover("A hosted session is never recovered automatically.");
                }
                else
                {
                    if (flag4 && !recoverPromptShown)
                    {
                        // Recovery reloads scenes from the host, so it waits for unsaved work to be saved. Say so once, or the wait looks like nothing.
                        recoverPromptShown = true;
                        ShowToast("Connection lost", "Save your scenes to reconnect automatically, or select Recover & reconnect to archive the unsaved changes and reconnect now.", Theme.Primary, 15f);
                    }
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
                        if (recoverAttempts < int.MaxValue)
                        {
                            recoverAttempts++;
                        }
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
        // A coalesced reset still pending would leave a stale snapshot under this undo; run it first.
        FlushUndoReset();
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

    // The coalesced half of "apply, capture, Initialize, MirrorNativeUndo". Runs the first request of a burst at once and holds the rest;
    // Frame() releases what is held, and Undo/TryRedo flush it before they act (see UndoResetScheduler for the bounded stale window).
    private void RequestUndoReset(Tracked tracked)
    {
        undoResetScenes.Add(tracked);
        undoReset.Request(undoResetClock.Elapsed.TotalMilliseconds);
        RunUndoResetIfDue();
    }

    private void RunUndoResetIfDue()
    {
        if (undoReset.Pending && undoReset.ShouldRunNow(undoResetClock.Elapsed.TotalMilliseconds))
        {
            RunPendingUndoResets();
        }
    }

    private void FlushUndoReset()
    {
        if (undoReset.Flush())
        {
            RunPendingUndoResets();
        }
    }

    private void RunPendingUndoResets()
    {
        Tracked[] pendingScenes = undoResetScenes.ToArray();
        undoResetScenes.Clear();
        foreach (Tracked tracked in pendingScenes)
        {
            try
            {
                // A scene that was closed or replaced since the request has nothing to reset.
                if (tracked.Session != null && tracked.Session.Scene.IsValid() && scenes.ContainsValue(tracked))
                {
                    tracked.Session.UndoSystem.Initialize();
                    MirrorNativeUndo(tracked);
                }
            }
            catch (Exception error)
            {
                GlobalGameNamespace.Log.Error("Collaboration undo reset: " + CollaborationTransportDiagnostics.BoundedExceptionDiagnostic(string.Empty, error));
            }
        }
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
            FlushUndoReset();
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

    // Presence smoothing: one PeerSmoothing per remote peer, fed from every received presence and read by DrawGizmos. presenceClock is the
    // monotonic clock this session stamps its own presence with (SentMs) and also reads as "local now" for arrival times; the two uses never
    // meet, because a receiver only compares one sender's stamps with its own arrival times.
    private readonly SessionClock presenceClock = new SessionClock();

    private readonly Dictionary<string, PeerSmoothing> peerSmoothing = new Dictionary<string, PeerSmoothing>();

    /// <summary>
    /// Feeds a received presence to its peer's smoother. A presence without a usable SentMs (a peer that predates the field) or without a camera
    /// is not fed, and that peer keeps being drawn from its raw packets.
    /// </summary>
    private void FeedPeerSmoothing(Packet p)
    {
        PrunePeerSmoothing();
        if (!PeerSmoothing.CanSmooth(p.SentMs) || !TryReadPresenceCamera(p.Presence, out var position, out var rotation, out var _))
        {
            return;
        }
        if (!peerSmoothing.TryGetValue(p.Peer, out var smoothing))
        {
            smoothing = new PeerSmoothing();
            peerSmoothing[p.Peer] = smoothing;
        }
        smoothing.OnPresence(p.SentMs, presenceClock.NowMs, PresenceMarker.PoseOf(position, rotation));
    }

    /// <summary>Forgets smoothers of peers that are no longer in the roster (the roster is cleared on leave and reconnect).</summary>
    private void PrunePeerSmoothing()
    {
        if (peerSmoothing.Count <= Roster.Count)
        {
            return;
        }
        foreach (string gone in peerSmoothing.Keys.Where((string key) => !Roster.ContainsKey(key)).ToList())
        {
            peerSmoothing.Remove(gone);
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
            Presence = jsonObject,
            SentMs = presenceClock.NowMs
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
                    // The smoothed pose replaces the raw packet once that peer's smoother is ready; until then (and for a peer that sends no SentMs)
                    // the raw value above is drawn, so a marker may pop in once at warm-up but never stutters.
                    if (peerSmoothing.TryGetValue(item.Key, out var smoothing))
                    {
                        PresenceMarker.ApplySmoothing(smoothing, presenceClock.NowMs, ref position, ref rotation);
                    }
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
