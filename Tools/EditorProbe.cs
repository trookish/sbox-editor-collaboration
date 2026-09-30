// TEST ONLY. Copied into Editor/ in disposable test copies; never shipped in the library.
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using TeamCreate;
using static Sandbox.Internal.GlobalToolsNamespace;

public static class CollaborationProbe
{
    static IDisposable continuous;
    static DateTime scheduledTime;
    static string scheduledTarget, scheduledName;

    // TEST-ONLY identity probe state. The raw auth token is deliberately NEVER
    // placed in the reply file or logged: only whether one was obtained, and its
    // length, so the probe cannot leak a credential to disk.
    //
    // THREADING: s&box runs async on the MAIN thread by default and its hotloader
    // suspends managed threads (docs: Code > Hotload > Concurrency). A Task.Run or
    // a raw CancellationTokenSource would be a thread the hotloader cannot track,
    // so the token task is stored and POLLED from the existing [EditorEvent.Frame]
    // tick instead — the pattern s&box documents for async called from sync code.
    static Task<string> tokenTask;
    static string tokenState = "not-started", tokenError = null;
    static int tokenLength = 0;
    static string identitySteamId, identityPersona, identityReadError;

    // WAVE B STATE. The shortcut probe reports which handler ran and whether anything
    // escaped it; the toast probe reports whether the widget could be constructed at all.
    // Both keep their own error string so a handler that refused is distinguishable from
    // one that never ran.
    static string shortcutState, shortcutError;
    static string toastState, toastError;

    // DEFERRED START (harness parity with the dock). host/join/wrong-key must hand Start a
    // Steam identity token or the fail-closed handshake refuses the hello, so the command
    // only REQUESTS the token; the frame tick below consumes it once it has completed and
    // then starts the session. Nothing ever waits on the task, and no thread is created —
    // s&box's hotloader suspends managed threads, so a task that is polled from the frame
    // is the only sanctioned shape. The token VALUE never reaches the reply file.
    static Task<string> startTokenTask;
    static string pendingStart, pendingStartRequestId;
    static string identityState = "not-started", identityStartError;
    [EditorEvent.Frame]
    public static void Tick()
    {
        if ( Project.Current is null ) return;
        // Poll the pending identity-token task on the MAIN thread. This is the
        // s&box-sanctioned way to consume async from sync code: no extra thread is
        // created, so the hotloader can never be surprised by an untracked one.
        if ( tokenTask is not null && tokenTask.IsCompleted )
        {
            try
            {
                var t = tokenTask.GetAwaiter().GetResult();
                tokenState = t is null ? "null-return" : "obtained";
                tokenLength = t?.Length ?? 0;
            }
            catch ( Exception e ) { tokenState = "failed"; tokenError = e.GetType().Name + ": " + e.Message; }
            tokenTask = null;
        }
        // Consume a queued Start once its identity token has arrived. The command that
        // queued it has already replied with IdentityState = pending; this is where that
        // state becomes ready or failed, still on the frame thread and still without
        // blocking on anything.
        if ( pendingStart is not null && startTokenTask is not null && startTokenTask.IsCompleted )
        {
            var queuedCommand = pendingStart;
            var queuedRequestId = pendingStartRequestId;
            pendingStart = null; pendingStartRequestId = null;
            string token = null;
            string failure = null;
            try { token = startTokenTask.GetAwaiter().GetResult(); }
            catch ( Exception e ) { failure = e.GetType().Name + ": " + e.Message; }
            finally { startTokenTask = null; }
            if ( failure is null && string.IsNullOrWhiteSpace( token ) ) failure = "the auth service returned no identity token";
            if ( failure is null )
            {
                if ( LocalSteamIdentity.TryGet( out var localIdentity, out _, out var identityFailure ) )
                {
                    try
                    {
                        var steamId = SteamIdentityRules.Canonical( localIdentity );
                        switch ( queuedCommand )
                        {
                            case "host": CollaborationSession.Current.Start( true, "127.0.0.1", 27845, "Developer A", "test-session-key-2026", steamId, token ); break;
                            case "join": CollaborationSession.Current.Start( false, "127.0.0.1", 27845, "Developer B", "test-session-key-2026", steamId, token ); break;
                            case "wrong-key": CollaborationSession.Current.Start( false, "127.0.0.1", 27845, "Developer B", "incorrect-test-key", steamId, token ); break;
                            default: failure = "Unknown queued start command"; break;
                        }
                    }
                    catch ( Exception e ) { failure = "Start refused: " + e.Message; }
                }
                else failure = identityFailure;
            }

            if ( failure is null ) identityState = "ready";
            else { identityState = "failed"; identityStartError = failure; }
            UpgradeStartReply( queuedRequestId );
        }
        if ( scheduledName is not null && DateTime.UtcNow >= scheduledTime )
        {
            var go = SceneAdapter.Walk( SceneEditorSession.Active.Scene ).Single( x => x.Name == scheduledTarget || x.Id.ToString() == scheduledTarget );
            go.Name = scheduledName; scheduledName = null;
        }
        var root = Project.Current.RootDirectory.FullName;
        var request = Path.Combine( root, ".collaboration", "request.json" );
        if ( !File.Exists( request ) ) return;
        string id = "unknown";
        try
        {
            var p = JsonNode.Parse( File.ReadAllText( request ) )!;
            File.Delete( request );
            id = p["id"]!.GetValue<string>();
            var collab = CollaborationSession.Current;
            var session = SceneEditorSession.Active;
            var target = p["target"]?.GetValue<string>();
            GameObject Find() => SceneAdapter.Walk( session.Scene ).Single( x => x.Id.ToString() == target || x.Name == target );
            switch ( p["command"]!.GetValue<string>() )
            {
                case "dock":
                    var dock = EditorWindow.DockManager.OpenDock( "Collaboration", DockArea.Right );
                    if ( dock is null ) throw new InvalidOperationException( "Collaboration dock is not registered" );
                    break;
                case "reset":
                    continuous?.Dispose(); continuous = null;
                    collab.Leave();
                    foreach ( var s in SceneEditorSession.All.Where( x => x is not GameEditorSession ).ToArray() ) { s.Reload(); s.HasUnsavedChanges = false; }
                    break;
                case "play": EditorScene.Play( session ); break;
                case "play-default": EditorScene.Play(); break;
                case "stop": EditorScene.Stop(); break;
                case "runtime-opt-in": collab.ExperimentalRuntimeOptIn = p["value"]?.Deserialize<bool>() ?? true; break;
                case "host":
                case "join":
                case "wrong-key":
                    // Start needs a Steam identity token, and the handshake refuses a hello
                    // without one, so the session is queued here and started from the frame
                    // tick once the token arrives. The reply reports IdentityState = pending.
                    QueueStart( p["command"]!.GetValue<string>(), id );
                    break;
                case "leave": collab.Leave(); break;
                case "begin": continuous = collab.BeginContinuousEdit(); break;
                case "end": continuous?.Dispose(); continuous = null; break;
                case "create-model":
                    using ( session.Scene.Push() )
                    {
                        var modelObject = session.Scene.CreateObject(); modelObject.Name = p["value"]!.GetValue<string>();
                        modelObject.Components.Create<ModelRenderer>().Model = Model.Load( "models/dev/box.vmdl" );
                        modelObject.Components.Create<BoxCollider>();
                    }
                    break;
                case "unsupported": Find().NetworkMode = NetworkMode.Never; break;
                case "identity":
                    // Reports whether the PUBLIC identity APIs return a real account in
                    // the EDITOR (not only in play mode). This is the live gate for the
                    // Steam-identity design: if SteamId is 0 here, the design must change.
                    identitySteamId = null; identityPersona = null; identityReadError = null;
                    // NOTE: fully qualified as Sandbox.Utility / Sandbox.Services. The probe
                    // has `using static Sandbox.Internal.GlobalToolsNamespace`, which brings
                    // an unrelated `Utility` (and no `Services`) into scope and makes the bare
                    // names fail to resolve. Verified public by reflection over Sandbox.Engine.
                    try { identitySteamId = Sandbox.Utility.Steam.SteamId.ValueUnsigned.ToString(); identityPersona = Sandbox.Utility.Steam.PersonaName; }
                    catch ( Exception e ) { identityReadError = e.GetType().Name + ": " + e.Message; }
                    break;
                case "identity-token":
                    // Requests an s&box auth token for the signed-in account. The VALUE is
                    // never written anywhere; only success/failure and the length are.
                    //
                    // Verified by reflection over Sandbox.Engine: the signature really is
                    // Task<string> GetToken(String, CancellationToken). The task is only
                    // STORED here; the frame tick below polls it, so no thread is created.
                    tokenTask = null; tokenState = "pending"; tokenError = null; tokenLength = 0;
                    try { tokenTask = Sandbox.Services.Auth.GetToken( "sbox-editor-collaboration" ); }
                    catch ( Exception e ) { tokenState = "failed"; tokenError = e.GetType().Name + ": " + e.Message; }
                    break;
                case "schedule-rename":
                    var parts = p["value"]!.GetValue<string>().Split( '|' );
                    scheduledTime = DateTime.Parse( parts[0] ).ToUniversalTime(); scheduledTarget = target; scheduledName = parts[1];
                    break;
                case "reconnect": collab.Reconnect( "127.0.0.1", 27845 ); break;
                case "open": SceneEditorSession.CreateFromPath( p["value"]!.GetValue<string>() )?.MakeActive(); break;
                case "close": SceneEditorSession.All.Single( x => SceneAdapter.PathOf( x ) == p["value"]!.GetValue<string>() ).Destroy(); break;
                case "create":
                    using ( session.Scene.Push() )
                    using ( session.UndoScope( "Probe create" ).WithGameObjectCreations().Push() )
                    { var go = session.Scene.CreateObject(); go.Name = p["value"]!.GetValue<string>(); }
                    break;
                case "rename": Find().Name = p["value"]!.GetValue<string>(); break;
                case "position": Find().LocalPosition = p["value"].Deserialize<Vector3>(); break;
                case "rotation": Find().LocalRotation = p["value"].Deserialize<Rotation>(); break;
                case "scale": Find().LocalScale = p["value"].Deserialize<Vector3>(); break;
                case "tint": Find().Components.Get<ModelRenderer>().Tint = p["value"].Deserialize<Color>(); break;
                case "box-scale": Find().Components.Get<BoxCollider>().Scale = p["value"].Deserialize<Vector3>(); break;
                case "parent": Find().SetParent( SceneAdapter.Walk( session.Scene ).Single( x => x.Name == p["value"]!.GetValue<string>() ), true ); break;
                case "delete": Find().Destroy(); break;
                case "select": session.Selection.Set( Find() ); break;
                case "camera": SceneViewWidget.Current.LastSelectedViewportWidget.State.CameraPosition = p["value"].Deserialize<Vector3>(); break;
                case "light": Find().Components.Get<DirectionalLight>().LightColor = p["value"].Deserialize<Color>(); break;
                case "undo": collab.Undo(); break;
                case "save": collab.Save(); break;
                // NATIVE SAVE. The scene dock's Save button and Ctrl+S both reach the engine
                // through SceneEditorSession.Save(false) — no collaboration bookkeeping at all
                // — which is what this calls, so the report it raises is the editor's own and
                // not this session's. The reply's SaveNotice shows what the session made of it.
                case "native-save": SceneEditorSession.Active.Save( false ); break;
                // EXTERNAL CHANGE. Moves the scene file's stamp with no save behind it, which
                // is the one change the frame scan must still fail on. It is destructive to the
                // session on purpose: it is the counter-example to native-save.
                case "touch-scene":
                    {
                        var touched = SceneAdapter.PathOf( SceneEditorSession.Active );
                        if ( touched is null ) throw new InvalidOperationException( "No saved scene to touch" );
                        File.SetLastWriteTimeUtc( Path.Combine( SceneAdapter.Root, "Assets", touched ), DateTime.UtcNow );
                        break;
                    }
                // The driver sends -Value as a JSON STRING, so the text is read first; the
                // typed parse is kept for a caller that sends a real JSON boolean.
                case "auto-save":
                    collab.AutoSaveIdle = p["value"] is JsonValue autoSaveValue &&
                        ( autoSaveValue.TryGetValue<string>( out var autoSaveText )
                            ? string.Equals( autoSaveText, "true", StringComparison.OrdinalIgnoreCase )
                            : autoSaveValue.Deserialize<bool>() );
                    break;
                // WAVE B — AUTOMATIC RECOVERY TOGGLE. Same shape as auto-save: the write goes
                // to the session's own property and the reply reports that property back, so a
                // write that did not land is visible instead of assumed.
                case "auto-recover":
                    collab.AutoRecover = p["value"] is JsonValue autoRecoverValue &&
                        ( autoRecoverValue.TryGetValue<string>( out var autoRecoverText )
                            ? string.Equals( autoRecoverText, "true", StringComparison.OrdinalIgnoreCase )
                            : autoRecoverValue.Deserialize<bool>() );
                    break;
                // WAVE B — THE SHORTCUT HANDLERS. A chord needs a key press this probe cannot
                // send, so this invokes the same public handlers the editor dispatches to,
                // through the same dock instance — the whole of what a chord reaches once it
                // is registered. A refusal the session raises must surface as a recorded
                // failure and never as an exception escaping into the key dispatcher, which
                // is exactly what the guarded state below distinguishes.
                case "shortcuts":
                    {
                        shortcutState = "never-run"; shortcutError = null;
                        try
                        {
                            var shortcutDock =
                                ( EditorWindow.DockManager.OpenDock( "Collaboration", DockArea.Right )?.Widget
                                    ?? EditorWindow.DockManager.FindDockWidget( "Collaboration" )?.Widget ) as TeamCreate.CollaborationDock;
                            if ( shortcutDock is null ) throw new InvalidOperationException( "Collaboration dock is not registered" );
                            switch ( p["value"]?.GetValue<string>() ?? "all" )
                            {
                                case "save": shortcutDock.SaveShortcut(); shortcutState = "save"; break;
                                case "undo": shortcutDock.UndoShortcut(); shortcutState = "undo"; break;
                                case "recover": shortcutDock.RecoverShortcut(); shortcutState = "recover"; break;
                                default:
                                    shortcutDock.SaveShortcut();
                                    shortcutDock.UndoShortcut();
                                    shortcutDock.RecoverShortcut();
                                    shortcutState = "all";
                                    break;
                            }
                        }
                        catch ( Exception e ) { shortcutState = "failed"; shortcutError = e.GetType().Name + ": " + e.Message; }
                    }
                    break;
                // WAVE B — TOAST CONSTRUCTION. Builds the same widget the recovery notices
                // build and hands it to the same manager: either this throws, or the next
                // frame's read of ToastManager finds it. The strings are the ones the session
                // actually raises, so the reply doubles as their transcript.
                case "toast":
                    {
                        toastState = "never-run"; toastError = null;
                        try
                        {
                            foreach ( var pair in new[]
                            {
                                new[] { "Connection lost", "Select Recover & reconnect, or press CTRL+ALT+R, to restore this session." },
                                new[] { "Connection restored", "Automatic recovery reconnected this session." },
                                new[] { "Automatic recovery stopped", "Three automatic attempts did not reconnect this session." }
                            } )
                            {
                                ToastManager.Remove( new ToastWidget
                                {
                                    Title = pair[0],
                                    Subtitle = pair[1],
                                    BorderColor = Color.White,
                                    DrawTimer = false,
                                    IsRunning = false,
                                    FixedWidth = 360
                                }, 2f );
                            }
                            toastState = "constructed";
                        }
                        catch ( Exception e ) { toastState = "failed"; toastError = e.GetType().Name + ": " + e.Message; }
                    }
                    break;
                case "status": break;
                default: throw new InvalidOperationException( "Unknown probe command" );
            }
            // CanSave hashes the source tree, so it is answered once, here, and guarded: a
            // reply must never be lost because one file could not be read.
            bool canSaveAnswer; string canSaveError;
            try { canSaveAnswer = collab.CanSave(); canSaveError = null; }
            catch ( Exception e ) { canSaveAnswer = false; canSaveError = e.GetType().Name + ": " + e.Message; }
            // WAVE B — what the native Undo dock would show for the active scene. Both stacks
            // are public, so this is a read of the real state rather than an inference from
            // this session's own bookkeeping: Back is the stack Ctrl+Z walks, Forward is what
            // a redo would return to, and Top is the name of the entry it acts on next.
            int undoBack = -1, undoForward = -1; string undoTop = null, undoError = null;
            try
            {
                var nativeUndo = SceneEditorSession.Active?.UndoSystem;
                if ( nativeUndo is null ) undoError = "no active scene";
                else
                {
                    undoBack = nativeUndo.Back.Count;
                    undoForward = nativeUndo.Forward.Count;
                    undoTop = nativeUndo.Back.TryPeek( out var nativeEntry ) ? nativeEntry.Name : null;
                }
            }
            catch ( Exception e ) { undoError = e.GetType().Name + ": " + e.Message; }
            // WAVE B — the toast manager's own list, read a frame after construction, so a
            // notice that was raised is one this reply can see rather than one the caller
            // has to trust.
            int toastCount = -1; string[] toastTitles = null; string toastListError = null;
            try
            {
                var liveToasts = ToastManager.All.OfType<ToastWidget>().ToArray();
                toastCount = liveToasts.Length;
                toastTitles = liveToasts.Select( x => x?.Title ).ToArray();
            }
            catch ( Exception e ) { toastListError = e.GetType().Name + ": " + e.Message; }
            var data = new { id, success = true, process = Environment.ProcessId, collab.Status, collab.Error, collab.Connected, collab.AcceptedOperations,
                collab.ExperimentalRuntimeOptIn, collab.PlayModeStatus, RuntimeLifecycleState = collab.RuntimeLifecycle.State.ToString(), collab.RuntimeLifecycle.RunId,
                collab.RuntimeLifecycle.SourceScenePath, collab.RuntimeLifecycleDiagnostic, collab.RuntimeLifecycleError, collab.HasPendingLifecycleCandidate,
                collab.DrawnMarkers, collab.DrawnSelections, Roster = collab.Roster.Values.ToArray(),
                // AUTOMATION STATE. SaveNotice is what the last save — native, accepted or
                // automatic — told the user; CanSave is Save's own precondition answered
                // without throwing; AutoSaveIdle is the idle toggle's armed state.
                SaveNotice = collab.SaveNotice, CanSave = canSaveAnswer, CanSaveError = canSaveError,
                AutoSaveIdle = collab.AutoSaveIdle,
                // WAVE B. AutoRecover is the silent-retry toggle — off by default, so a reply
                // that never wrote it must read false — and UndoNotice is what the last refused
                // native undo or redo told the user.
                AutoRecover = collab.AutoRecover, UndoNotice = collab.UndoNotice,
                NativeUndoBack = undoBack, NativeUndoForward = undoForward,
                NativeUndoTop = undoTop, NativeUndoError = undoError,
                ShortcutState = shortcutState, ShortcutError = shortcutError,
                ToastState = toastState, ToastError = toastError,
                ToastCount = toastCount, ToastTitles = toastTitles, ToastListError = toastListError,
                // TEST-ONLY identity fields. SteamId and PersonaName are the account the
                // editor is signed into; TokenState/TokenLength deliberately expose NO
                // token value, so a credential can never be written to this file.
                IdentitySteamId = identitySteamId, IdentityPersona = identityPersona, IdentityReadError = identityReadError,
                TokenState = tokenState, TokenLength = tokenLength, TokenError = tokenError,
                // DEFERRED START STATE. pending: the token is still being requested, ready:
                // Start has been called with the canonical SteamId and token, failed: it was
                // not. The token value itself is never part of the reply.
                IdentityState = identityState, IdentityStartError = identityStartError,
                Scenes = SceneEditorSession.All.Where( x => x is not GameEditorSession ).Select( x => new { Path = SceneAdapter.PathOf( x ), x.HasUnsavedChanges, Objects = SceneAdapter.Capture( x.Scene ) } ).ToArray(),
                RuntimeSessions = SceneEditorSession.All.OfType<GameEditorSession>().Select( x => new { ParentPath = SceneAdapter.PathOf( x.Parent ), ParentPlaying = x.Parent?.IsPlaying ?? false, SceneValid = x.Scene is not null && x.Scene.IsValid() } ).ToArray() };
            File.WriteAllText( Path.Combine( root, ".collaboration", "reply.json" ), JsonSerializer.Serialize( data ) );
        }
        catch ( Exception e ) { File.WriteAllText( Path.Combine( root, ".collaboration", "reply.json" ), JsonSerializer.Serialize( new { id, success = false, error = e.ToString() } ) ); }
    }

    /// <summary>
    /// Queues a Start for the frame tick and requests the Steam identity token it needs.
    /// The task is only stored here — never awaited, never given a thread — because s&box
    /// suspends managed threads on hotload and the frame tick is the sanctioned place to
    /// consume async work.
    /// </summary>
    static void QueueStart( string command, string requestId )
    {
        pendingStart = command;
        pendingStartRequestId = requestId;
        identityState = "pending";
        identityStartError = null;
        try { startTokenTask = Sandbox.Services.Auth.GetToken( LocalSteamIdentity.ServiceName ); }
        catch ( Exception e )
        {
            // A token that could never be requested fails the queued start outright rather
            // than leaving it pending forever with no task to complete.
            startTokenTask = null;
            pendingStart = null;
            pendingStartRequestId = null;
            identityState = "failed";
            identityStartError = e.GetType().Name + ": " + e.Message;
        }
    }

    /// <summary>
    /// Best-effort update of the reply that queued the start, so a harness still reading it
    /// sees the final IdentityState instead of the pending one. The reply is only rewritten
    /// while it still carries this request's id: a reply a newer command has already
    /// produced belongs to that command and is left strictly alone. A reply already read and
    /// discarded is skipped just as strictly — later commands report the state themselves.
    /// </summary>
    static void UpgradeStartReply( string requestId )
    {
        if ( string.IsNullOrWhiteSpace( requestId ) ) return;
        try
        {
            var root = Project.Current.RootDirectory.FullName;
            var path = Path.Combine( root, ".collaboration", "reply.json" );
            if ( !File.Exists( path ) ) return;
            if ( JsonNode.Parse( File.ReadAllText( path ) ) is not JsonObject reply ) return;
            if ( reply["id"]?.GetValue<string>() != requestId ) return;
            reply["IdentityState"] = identityState;
            reply["IdentityStartError"] = identityStartError;
            reply["Status"] = CollaborationSession.Current.Status;
            reply["Error"] = CollaborationSession.Current.Error;
            File.WriteAllText( path, reply.ToJsonString() );
        }
        catch ( Exception ) { /* a reply that cannot be refreshed is still the reply we wrote */ }
    }
}
