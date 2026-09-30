using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Editor;
using Sandbox;
using Sandbox.Services;

namespace TeamCreate;

/// <summary>
/// The one-click solo test. It runs only inside a disposable copy of the project (see <see cref="SoloCopy"/>), where one person with one Steam account can
/// host a session and have a scripted collaborator join it over the real transport. The steps exercise the real host session and this editor's real scene:
/// the host admits the collaborator through its normal checks, the collaborator's edits arrive in this editor, this editor's edits reach the collaborator,
/// per-user undo, refused stale edits, play pause, saving, and the relay when one is configured.
///
/// Everything runs from the frame tick. Nothing here starts a thread or a timer, because hotloaded editor code cannot own one; each step is a begin action and a
/// poll that is called once per frame until it settles or times out.
/// </summary>
public static class SoloTestRunner
{
    private enum Phase
    {
        Idle,
        Steps,
        Demo,
        Stopping,
        Done
    }

    private sealed class Step
    {
        public SoloStep View;

        public double TimeoutSeconds = 20;

        public bool Critical;

        public Action Begin;

        /// <summary>Runs once when the step settles, whatever the outcome, so a step that timed out cannot leave a collaborator holding the test identity.</summary>
        public Action Cleanup;

        /// <summary>Returns the step's state: Running keeps waiting, anything else settles it.</summary>
        public Func<(SoloStepState State, string Detail)> Poll;
    }

    public const string ReportFile = "solo-report.json";

    private const double OrbitRadius = 230;

    private static readonly (double X, double Y, double Z) OrbitCenter = (0, 0, 90);

    private static readonly List<Step> steps = new List<Step>();

    private static readonly List<SoloStep> views = new List<SoloStep>();

    private static Phase phase = Phase.Idle;

    private static int index;

    private static DateTime stepStarted;

    private static DateTime startedAt;

    private static DateTime nextPresence;

    private static DateTime nextDemoEdit;

    private static DateTime processStarted = DateTime.UtcNow;

    private static bool autorunHandled;

    private static string runToken;

    private static string scenePath;

    private static string headline = "Not run yet.";

    private static string handshakeProject;

    private static InviteData invite;

    private static byte[] sessionKey;

    private static SoloBot bot;

    private static SoloBot relayBot;

    private static SoloBot demoBot;

    private static readonly List<SoloBot> impostors = new List<SoloBot>();

    private static Task<string> tokenTask;

    private static bool hostStarted;

    private static ObjectState cube;

    private static ObjectState stopObject;

    private static DateTime stopBegan;

    private static bool stopWasDemo;

    private static string stopState;

    private static string stopHeadline;

    private static ObjectState demoCube;

    private static double demoAngle;

    public static IReadOnlyList<SoloStep> Steps => views;

    public static string Headline => headline;

    public static bool Running => phase == Phase.Steps;

    public static bool Watching => phase == Phase.Demo;

    public static bool Finished => phase == Phase.Done;

    public static bool Active => phase != Phase.Idle && phase != Phase.Done;

    public static string Root
    {
        get
        {
            try
            {
                return SceneAdapter.Root;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>True only inside a folder the launcher made (right path and marker file); the same test the session applies.</summary>
    public static bool IsSoloCopy => TestHarnessRules.IsDisposableCopy(Root, SoloCopy.HasMarker(Root));

    private static CollaborationSession Collab => CollaborationSession.Current;

    [EditorEvent.Frame]
    public static void Tick()
    {
        try
        {
            if (Project.Current == null)
            {
                return;
            }
            if (IsSoloCopy)
            {
                SoloTestLauncher.Beat(Root);
            }
            if (!autorunHandled && (DateTime.UtcNow - processStarted).TotalSeconds > 10)
            {
                autorunHandled = true;
                if (IsSoloCopy && SoloCopy.WantsAutorun(Root))
                {
                    SoloCopy.ConsumeAutorun(Root);
                    Start();
                }
            }
            if (IsSoloCopy)
            {
                PollCommand();
            }
            if (phase == Phase.Idle || phase == Phase.Done)
            {
                return;
            }
            bot?.Pump();
            relayBot?.Pump();
            demoBot?.Pump();
            foreach (SoloBot impostor in impostors)
            {
                impostor.Pump();
            }
            DriveOrbit();
            if (phase == Phase.Steps)
            {
                RunCurrentStep();
            }
            else if (phase == Phase.Demo)
            {
                RunDemo();
            }
            else if (phase == Phase.Stopping)
            {
                PollStopping();
            }
        }
        catch (Exception ex)
        {
            Abort("The solo test stopped on an unexpected error: " + ex.Message);
        }
    }

    public const string CommandFile = "solo-command.txt";

    private static DateTime nextCommandCheck;

    /// <summary>
    /// Lets Tools/Run-SoloTest.ps1 -Stop end the test from outside the window. A one-word file, read and deleted here; it is honoured only inside a
    /// disposable copy, which is the only place this code runs at all.
    /// </summary>
    private static void PollCommand()
    {
        if (DateTime.UtcNow < nextCommandCheck)
        {
            return;
        }
        nextCommandCheck = DateTime.UtcNow.AddSeconds(1);
        try
        {
            string path = Path.Combine(Root, ".collaboration", CommandFile);
            if (!File.Exists(path))
            {
                return;
            }
            string word = File.ReadAllText(path).Trim().ToLowerInvariant();
            File.Delete(path);
            if (word == "stop")
            {
                Stop();
            }
            else if (word == "start")
            {
                Start();
            }
        }
        catch (Exception)
        {
        }
    }

    public static void Start()
    {
        if (!IsSoloCopy)
        {
            throw new InvalidOperationException("The solo test only runs inside a disposable solo test copy. Use Run solo test in the Collaboration dock to make one.");
        }
        if (Active)
        {
            return;
        }
        CleanupParticipants();
        OpenDock();
        BuildSteps();
        index = 0;
        startedAt = DateTime.UtcNow;
        phase = Phase.Steps;
        BeginStep();
        headline = "Running…";
        WriteReport("running");
    }

    /// <summary>
    /// Ends the test in two phases. First the collaborators go and the test's object is removed; the session stays up until the host has been told, because
    /// leaving a session checkpoints the scene and would save the object into the file. Then the session ends. Safe to call at any time.
    /// </summary>
    public static void Stop()
    {
        if (phase == Phase.Idle || phase == Phase.Done || phase == Phase.Stopping)
        {
            return;
        }
        stopWasDemo = phase == Phase.Demo;
        stopObject = demoCube ?? cube;
        try
        {
            RemoveTestObject(stopObject);
        }
        catch (Exception)
        {
        }
        CleanupParticipants();
        stopBegan = DateTime.UtcNow;
        phase = Phase.Stopping;
    }

    private static void PollStopping()
    {
        bool present = false;
        try
        {
            present = stopObject != null && Find(stopObject) != null;
        }
        catch (Exception)
        {
        }
        bool pending = false;
        try
        {
            pending = Collab.Connected && !Collab.CanSave();
        }
        catch (Exception)
        {
            pending = false;
        }
        if (SoloScript.StopDecision(present, pending, Collab.Connected, (DateTime.UtcNow - stopBegan).TotalSeconds) == SoloStopDecision.Proceed)
        {
            FinishStop();
        }
    }

    private static void FinishStop()
    {
        try
        {
            Collab.Leave();
        }
        catch (Exception)
        {
        }
        CollaborationSession.SoloGuestVoucher = null;
        foreach (Step step in steps)
        {
            if (step.View.State == SoloStepState.Running || step.View.State == SoloStepState.Waiting)
            {
                step.View.State = SoloStepState.Skipped;
                step.View.Detail = "Stopped before this ran.";
            }
        }
        phase = Phase.Done;
        headline = stopHeadline ?? SoloReport.Summary(views, true) + (stopWasDemo ? " Session ended." : string.Empty);
        WriteReport(stopState ?? (stopWasDemo ? "stopped" : "aborted"));
        stopState = null;
        stopHeadline = null;
    }

    private static void Abort(string message)
    {
        if (index < steps.Count && steps[index].View.State == SoloStepState.Running)
        {
            steps[index].View.State = SoloStepState.Failed;
            steps[index].View.Detail = message;
        }
        stopState = "aborted";
        stopHeadline = message;
        Stop();
    }

    private static void CleanupParticipants()
    {
        bot?.Leave();
        relayBot?.Leave();
        demoBot?.Leave();
        foreach (SoloBot impostor in impostors)
        {
            impostor.Dispose();
        }
        impostors.Clear();
        bot = null;
        relayBot = null;
        demoBot = null;
    }

    // ---- stepping ------------------------------------------------------------------------------------------------------

    private static void RunCurrentStep()
    {
        if (index >= steps.Count)
        {
            FinishChecks();
            return;
        }
        Step step = steps[index];
        (SoloStepState state, string detail) = step.Poll();
        if (state == SoloStepState.Running)
        {
            if ((DateTime.UtcNow - stepStarted).TotalSeconds > step.TimeoutSeconds)
            {
                step.View.State = SoloStepState.Failed;
                step.View.Detail = "Timed out after " + step.TimeoutSeconds.ToString("0", CultureInfo.InvariantCulture) + " seconds. " + (string.IsNullOrEmpty(step.View.Detail) ? string.Empty : step.View.Detail);
                Settled(step);
            }
            else if (!string.IsNullOrEmpty(detail))
            {
                step.View.Detail = detail;
            }
            return;
        }
        step.View.State = state;
        step.View.Detail = detail;
        Settled(step);
    }

    private static void Settled(Step step)
    {
        try
        {
            step.Cleanup?.Invoke();
        }
        catch (Exception)
        {
        }
        if (step.View.State == SoloStepState.Failed && step.Critical)
        {
            for (int i = index + 1; i < steps.Count; i++)
            {
                steps[i].View.State = SoloStepState.Skipped;
                steps[i].View.Detail = "Not run because \"" + step.View.Title + "\" failed.";
            }
            index = steps.Count;
            FinishChecks();
            return;
        }
        index++;
        if (index < steps.Count)
        {
            BeginStep();
        }
        WriteReport("running");
    }

    private static void BeginStep()
    {
        Step step = steps[index];
        if (step.View.Id == "demo" && !SoloReport.DemoAllowed(views))
        {
            step.View.State = SoloStepState.Skipped;
            step.View.Detail = "Not started because a check failed.";
            index++;
            if (index < steps.Count)
            {
                BeginStep();
            }
            return;
        }
        step.View.State = SoloStepState.Running;
        step.View.Detail = null;
        stepStarted = DateTime.UtcNow;
        try
        {
            step.Begin?.Invoke();
        }
        catch (Exception ex)
        {
            step.View.State = SoloStepState.Failed;
            step.View.Detail = ex.Message;
            Settled(step);
        }
    }

    private static void FinishChecks()
    {
        bool ok = SoloReport.AllPassed(views.Where((SoloStep s) => s.State != SoloStepState.Skipped).ToList()) && !views.Any((SoloStep s) => s.State == SoloStepState.Failed);
        headline = SoloReport.Summary(views, true);
        if (!ok)
        {
            stopState = "checks-failed";
            stopHeadline = headline;
            phase = Phase.Steps;
            Stop();
            return;
        }
        phase = Phase.Demo;
        headline += " The collaborator is now orbiting your scene. Press Stop to end.";
        WriteReport("checks-passed");
    }

    private static void RemoveTestObject(ObjectState state)
    {
        if (state == null)
        {
            return;
        }
        GameObject go = Find(state);
        if (go != null && go.IsValid())
        {
            go.Destroy();
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private static SceneEditorSession SceneSession()
    {
        return SceneEditorSession.All.FirstOrDefault((SceneEditorSession x) => !(x is GameEditorSession) && SceneAdapter.PathOf(x) == scenePath);
    }

    private static GameObject Find(ObjectState state)
    {
        SceneEditorSession session = SceneSession();
        if (session == null || state == null)
        {
            return null;
        }
        return SceneAdapter.Walk(session.Scene).FirstOrDefault((GameObject x) => x.IsValid() && string.Equals(x.Id.ToString(), state.Id, StringComparison.Ordinal));
    }

    private static Vector3 ParseVector(string text)
    {
        float[] n = text.Split(',').Select((string x) => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return new Vector3(n[0], n[1], n[2]);
    }

    private static bool Near(Vector3 a, Vector3 b)
    {
        return (a - b).Length < 0.5f;
    }

    private static string PositionOf(SoloBot who)
    {
        return who != null && cube != null && who.Known(scenePath).TryGetValue(cube.Id, out ObjectState s) ? s.Data["Position"]?.GetValue<string>() : null;
    }

    private static SoloHandshake Handshake(string steamId, string name, string token)
    {
        (string ProjectId, string Baseline, string Manifest, string OrgIdent, string LibraryVersion) facts = Collab.HandshakeFacts;
        return new SoloHandshake
        {
            Project = facts.ProjectId,
            Baseline = facts.Baseline,
            Manifest = facts.Manifest,
            Org = facts.OrgIdent,
            LibraryVersion = facts.LibraryVersion,
            SteamId = steamId,
            SteamToken = token,
            Name = name,
            SessionToken = Convert.ToHexString(sessionKey)
        };
    }

    private static SoloBot NewDirectBot(string steamId, string name, string token)
    {
        SoloBot made = new SoloBot(Handshake(steamId, name, token));
        int port = invite.Port;
        byte[] key = sessionKey;
        made.Connect(() => Wire.Connect("127.0.0.1", port, key, true));
        return made;
    }

    private static SoloBot NewRelayBot()
    {
        SoloBot made = new SoloBot(Handshake(TestHarnessRules.SoloGuestSteamId, TestHarnessRules.SoloGuestName, runToken));
        string url = invite.Relay;
        byte[] secret = invite.Secret;
        byte[] key = sessionKey;
        made.Connect(() => RelayGuest.ConnectAsync(() => new SandboxRelaySocket(), url, secret, key));
        return made;
    }

    private static (double X, double Y, double Z) OrbitPoint(double seconds)
    {
        return SoloScript.Orbit(OrbitCenter, OrbitRadius, seconds * 0.55, 28);
    }

    /// <summary>The collaborator's marker circles the scene and looks at its middle, so the sphere is easy to find and to watch.</summary>
    private static void DriveOrbit()
    {
        SoloBot who = demoBot ?? bot ?? relayBot;
        if (who == null || !who.IsLive || scenePath == null || DateTime.UtcNow < nextPresence)
        {
            return;
        }
        nextPresence = DateTime.UtcNow.AddMilliseconds(100);
        double t = (DateTime.UtcNow - startedAt).TotalSeconds;
        (double X, double Y, double Z) p = OrbitPoint(t);
        who.SendPresence(scenePath, SoloScript.Presence(p, SoloScript.LookAt(p, OrbitCenter), phase == Phase.Demo ? "editing" : "editing"));
    }

    private static void RunDemo()
    {
        if (demoBot == null || !demoBot.IsLive || demoCube == null || DateTime.UtcNow < nextDemoEdit)
        {
            return;
        }
        nextDemoEdit = DateTime.UtcNow.AddMilliseconds(400);
        demoAngle += 0.16;
        (double X, double Y, double Z) c = SoloScript.Orbit((OrbitCenter.X, OrbitCenter.Y, 45), 90, demoAngle, 14);
        ObjectState next = SoloScript.WithTint(SoloScript.WithPosition(demoCube, SoloScript.SnapVec(c.X, c.Y, c.Z)), SoloScript.TintAt(demoAngle / 6.0));
        if (demoBot.PendingCount == 0)
        {
            demoBot.SendEdit(scenePath, new[] { SoloScript.Update(demoCube, next) });
            demoCube = next;
        }
    }

    /// <summary>
    /// The checklist lives in the Collaboration dock, and a fresh copy starts with the default layout, where that dock is closed. A test that
    /// starts by itself and shows its progress somewhere nobody is looking would appear to do nothing.
    /// </summary>
    private static void OpenDock()
    {
        try
        {
            Sandbox.Internal.GlobalToolsNamespace.EditorWindow.DockManager.OpenDock("Collaboration", DockArea.Right);
        }
        catch (Exception)
        {
        }
    }

    private static void PlaceCamera()
    {
        try
        {
            SceneViewportWidget widget = SceneViewWidget.Current?.LastSelectedViewportWidget;
            if (widget == null)
            {
                return;
            }
            Vector3 target = new Vector3((float)OrbitCenter.X, (float)OrbitCenter.Y, (float)OrbitCenter.Z - 20f);
            Vector3 from = new Vector3(-520f, -520f, 300f);
            widget.State.CameraPosition = from;
            widget.State.CameraRotation = Rotation.LookAt((target - from).Normal);
        }
        catch (Exception)
        {
        }
    }

    // ---- the script ----------------------------------------------------------------------------------------------------

    private static void Add(string id, string title, double timeout, bool critical, Action begin, Func<(SoloStepState, string)> poll, Action cleanup = null)
    {
        SoloStep view = new SoloStep { Id = id, Title = title };
        views.Add(view);
        steps.Add(new Step { View = view, TimeoutSeconds = timeout, Critical = critical, Begin = begin, Poll = poll, Cleanup = cleanup });
    }

    private static (SoloStepState, string) Wait(string detail = null)
    {
        return (SoloStepState.Running, detail);
    }

    private static (SoloStepState, string) Pass(string detail)
    {
        return (SoloStepState.Passed, detail);
    }

    private static (SoloStepState, string) Fail(string detail)
    {
        return (SoloStepState.Failed, detail);
    }

    private static void BuildSteps()
    {
        steps.Clear();
        views.Clear();
        cube = null;
        demoCube = null;
        demoAngle = 0;
        hostStarted = false;
        tokenTask = null;
        scenePath = null;
        invite = null;

        Add("scene", "A saved scene is open and fully loaded", 60, true, () =>
        {
            foreach (SceneEditorSession open in SceneEditorSession.All.Where((SceneEditorSession x) => !(x is GameEditorSession)).ToArray())
            {
                if (open.HasUnsavedChanges)
                {
                    // A disposable copy: unsaved edits here are leftovers, and a session cannot start over them.
                    open.Reload();
                    open.HasUnsavedChanges = false;
                }
            }
            string active = SceneAdapter.PathOf(SceneEditorSession.Active);
            if (active == null || !active.StartsWith("scenes/", StringComparison.Ordinal))
            {
                string folder = Path.Combine(SceneAdapter.Root, "Assets", "scenes");
                string first = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.scene").OrderBy((string x) => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault() : null;
                if (first == null)
                {
                    throw new InvalidOperationException("The copy has no scene under Assets/scenes. Save one in the project first.");
                }
                SceneEditorSession.CreateFromPath("scenes/" + Path.GetFileName(first))?.MakeActive();
            }
        }, () =>
        {
            string active = SceneAdapter.PathOf(SceneEditorSession.Active);
            if (active == null || !active.StartsWith("scenes/", StringComparison.Ordinal) || SceneEditorSession.Active is GameEditorSession)
            {
                return Wait("Opening a scene…");
            }
            scenePath = active;
            // The editor may still be restoring the scene when this runs. Starting then makes the host publish a partial scene, and the save step
            // would write it back over the copy's file. Wait until the editor holds at least as many top-level objects as the file does.
            int? expected = SoloScript.TopLevelObjectCount(Path.Combine(Root, "Assets", active.Replace('/', Path.DirectorySeparatorChar)));
            SceneEditorSession session = SceneSession();
            int loaded = session?.Scene != null && session.Scene.IsValid() ? session.Scene.Children.Count((GameObject x) => x.IsValid()) : 0;
            SoloSceneLoad state = SoloScript.SceneLoadState(expected, loaded);
            string detail = SoloScript.SceneLoadDetail(active, expected, loaded);
            if (state == SoloSceneLoad.Loading)
            {
                return Wait(detail);
            }
            PlaceCamera();
            return Pass(detail);
        });

        Add("host", "Host session starts", 150, true, () =>
        {
            runToken = TestHarnessRules.NewRunToken();
            string token = runToken;
            CollaborationSession.SoloGuestVoucher = (Packet hello) => TestHarnessRules.MayVouchFor(hello.SteamId, hello.SteamToken, token);
            if (Collab.Busy)
            {
                Collab.Leave();
            }
            tokenTask = Auth.GetToken(LocalSteamIdentity.ServiceName);
        }, () =>
        {
            if (!hostStarted)
            {
                // The readiness checks answer asynchronously after the editor loads; hosting before they have is refused for a reason that is gone a
                // moment later. Wait for them, and only call a row blocked once the grace period has passed.
                SoloHostReadiness readiness = SoloScript.HostReadiness(Collab.Readiness, (DateTime.UtcNow - stepStarted).TotalSeconds);
                if (readiness == SoloHostReadiness.Waiting)
                {
                    return Wait("Waiting for the readiness checks (Steam, publication, organization)…");
                }
                if (readiness == SoloHostReadiness.Blocked)
                {
                    return Fail(ReadinessRules.BlockReason(Collab.Readiness) ?? "The readiness checklist never completed. Open the Collaboration dock and check the Readiness card.");
                }
                if (!tokenTask.IsCompleted)
                {
                    return Wait("Waiting for a fresh Steam identity token…");
                }
                if (tokenTask.IsFaulted || tokenTask.IsCanceled || string.IsNullOrWhiteSpace(tokenTask.Result))
                {
                    return Fail("A Steam identity token could not be obtained. Sign in to Steam and try again.");
                }
                if (!LocalSteamIdentity.TryGet(out SteamIdentity identity, out string persona, out string failure))
                {
                    return Fail(failure ?? "No signed-in Steam account is available.");
                }
                Collab.StartHosted(27845, persona, SteamIdentityRules.Canonical(identity), tokenTask.Result, allowUnverifiedPeers: false);
                hostStarted = true;
                return Wait("Starting the host…");
            }
            if (Collab.Error != null)
            {
                return Fail(Collab.Error);
            }
            if (!Collab.SoloHarness)
            {
                return Fail("The test identity is not active in this folder, so a collaborator could not be admitted.");
            }
            if (Collab.Connected && Collab.IsHost && Collab.InviteCode != null)
            {
                return Pass("Hosting with the signed-in Steam account.");
            }
            return Wait(Collab.Status);
        });

        Add("live", "The host's scene is live", 25, true, null, () =>
        {
            if (Collab.Error != null)
            {
                return Fail(Collab.Error);
            }
            return Collab.ReadyScenes.Contains(scenePath) ? Pass(scenePath + " is synchronized.") : Wait("Waiting for the host to publish the scene…");
        });

        Add("relay", "Relay is registered", 20, false, null, () =>
        {
            string status = Collab.RelayStatus;
            if (status == null)
            {
                return (SoloStepState.Skipped, "No relay is configured for this project (RelayUrl), so only direct routes are tested.");
            }
            if (Collab.RelayFailed)
            {
                return Fail(status);
            }
            return status.StartsWith("Relay connected", StringComparison.Ordinal) ? Pass("The relay accepted this session's room.") : Wait(status);
        });

        Add("join", "A collaborator joins and passes the host's checks", 30, true, () =>
        {
            (string ProjectId, string Baseline, string Manifest, string OrgIdent, string LibraryVersion) facts = Collab.HandshakeFacts;
            handshakeProject = facts.ProjectId;
            if (!InviteCode.TryDecode(Collab.InviteCode, facts.ProjectId, out invite, out string error))
            {
                throw new InvalidOperationException("The host's own invite code did not decode: " + error);
            }
            sessionKey = InviteCode.DeriveKey(invite.Secret, facts.ProjectId);
            bot = NewDirectBot(TestHarnessRules.SoloGuestSteamId, TestHarnessRules.SoloGuestName, runToken);
        }, () =>
        {
            if (bot.State == SoloBotState.Refused || bot.State == SoloBotState.Failed)
            {
                return Fail("The host refused the collaborator: " + bot.Refusal);
            }
            if (bot.IsLive)
            {
                bot.Subscribe(scenePath);
                return Pass("Admitted as \"" + TestHarnessRules.SoloGuestName + "\" after the host's normal project, baseline, library and manifest checks.");
            }
            return Wait("Connecting…");
        });

        Add("marker", "Its floating marker appears in your scene view", 20, false, null, () =>
        {
            bool inRoster = Collab.Roster.Values.Any((Packet x) => x.SteamId == TestHarnessRules.SoloGuestSteamId && x.Presence != null);
            if (!inRoster)
            {
                return Wait("Waiting for the collaborator's presence…");
            }
            return Collab.DrawnMarkers > 0 ? Pass("The blue sphere is drawn in the scene view, orbiting the middle of the scene.") : Wait("The collaborator is in the roster; waiting for the scene view to draw its marker. Open a scene view if none is visible.");
        });

        Add("create", "Its new object appears in your scene", 20, true, () =>
        {
            cube = SoloScript.NewCube(Guid.NewGuid(), Guid.NewGuid(), SoloScript.SnapVec(OrbitCenter.X, OrbitCenter.Y, 45), SoloScript.Color(1.0, 0.34375, 0.25));
            bot.SendEdit(scenePath, new[] { SoloScript.Create(cube) });
        }, () => Find(cube) != null ? Pass("\"" + SoloScript.ObjectName + "\" appeared in this editor.") : Wait());

        Vector3 movedTo = new Vector3(70, -50, 80);
        Add("move", "Its move appears in your scene as it happens", 20, true, () =>
        {
            ObjectState next = SoloScript.WithPosition(cube, SoloScript.SnapVec(movedTo.x, movedTo.y, movedTo.z));
            bot.SendEdit(scenePath, new[] { SoloScript.Update(cube, next) });
            cube = next;
        }, () =>
        {
            GameObject go = Find(cube);
            return go != null && Near(go.WorldPosition, movedTo) ? Pass("Position changed to " + go.WorldPosition + ".") : Wait();
        });

        string tintText = SoloScript.TintAt(0.62);
        Add("property", "Its property change appears in your scene", 20, true, () =>
        {
            ObjectState next = SoloScript.WithTint(cube, tintText);
            bot.SendEdit(scenePath, new[] { SoloScript.Update(cube, next) });
            cube = next;
        }, () =>
        {
            GameObject go = Find(cube);
            ModelRenderer renderer = go?.Components.Get<ModelRenderer>();
            if (renderer == null)
            {
                return Wait();
            }
            float[] want = tintText.Split(',').Select((string x) => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            Color have = renderer.Tint;
            return Math.Abs(have.r - want[0]) < 0.02f && Math.Abs(have.g - want[1]) < 0.02f && Math.Abs(have.b - want[2]) < 0.02f ? Pass("The renderer tint changed.") : Wait();
        });

        Vector3 hostMoved = new Vector3(-90, 60, 100);
        Add("reverse", "Your move reaches the collaborator as it happens", 20, true, () =>
        {
            GameObject go = Find(cube);
            if (go == null)
            {
                throw new InvalidOperationException("The test object is not in this scene.");
            }
            go.LocalPosition = hostMoved;
        }, () =>
        {
            string seen = PositionOf(bot);
            return seen != null && Near(ParseVector(seen), hostMoved) ? Pass("The collaborator saw the position change to " + seen + ".") : Wait();
        });

        bool undone = false;
        Add("undo", "Undo reverses only your own move", 25, false, () => undone = false, () =>
        {
            if (!undone)
            {
                try
                {
                    Collab.Undo();
                    undone = true;
                }
                catch (InvalidOperationException)
                {
                    return Wait("Waiting for your move to be acknowledged…");
                }
            }
            string seen = PositionOf(bot);
            bool botsTintKept = bot.Known(scenePath).TryGetValue(cube.Id, out ObjectState s) && (s.Data["Components"] as JsonArray)?[0]?["Tint"]?.GetValue<string>() == tintText;
            return seen != null && Near(ParseVector(seen), movedTo) && botsTintKept ? Pass("Back to " + seen + "; the collaborator's own change to the object was not undone.") : Wait();
        });

        int rejectsBefore = 0;
        Add("conflict", "A stale edit is refused, not silently applied", 20, false, () =>
        {
            rejectsBefore = bot.Rejects.Count;
            // Built on a position the object left long ago, so it is out of date by construction.
            ObjectState old = SoloScript.WithPosition(cube, "1,1,1");
            bot.SendEdit(scenePath, new[] { SoloScript.Update(old, SoloScript.WithPosition(old, "5,5,5")) });
        }, () =>
        {
            if (bot.Rejects.Count <= rejectsBefore)
            {
                return Wait();
            }
            GameObject go = Find(cube);
            return go != null && Near(go.WorldPosition, movedTo) ? Pass("The host answered: " + Truncate(bot.Rejects.Last().Error, 110)) : Fail("The refused edit still changed the scene.");
        });

        Vector3 whilePlaying = new Vector3(10, 20, 110);
        int playPhase = 0;
        Add("play", "Playing pauses scene edits; changes wait until you stop", 60, false, () =>
        {
            playPhase = 0;
            EditorScene.Play();
        }, () =>
        {
            if (Collab.Error != null)
            {
                return Fail(Collab.Error);
            }
            if (playPhase == 0)
            {
                if (Collab.PlayDecision != PlaySpectateDecision.PausedForPlay)
                {
                    return Wait("Starting the game…");
                }
                ObjectState next = SoloScript.WithPosition(cube, SoloScript.SnapVec(whilePlaying.x, whilePlaying.y, whilePlaying.z));
                bot.SendEdit(scenePath, new[] { SoloScript.Update(cube, next) });
                cube = next;
                playPhase = 1;
                return Wait("Playing; the collaborator changes the object…");
            }
            if (playPhase == 1)
            {
                GameObject go = Find(cube);
                if (Collab.HeldSceneChanges < 1)
                {
                    return Wait("Waiting for the collaborator's change to reach this editor…");
                }
                if (go != null && Near(go.WorldPosition, whilePlaying))
                {
                    return Fail("The change was applied while playing instead of being held.");
                }
                EditorScene.Stop();
                playPhase = 2;
                return Wait("Held " + Collab.HeldSceneChanges + " change(s); stopping the game…");
            }
            GameObject after = Find(cube);
            return Collab.PlayDecision == PlaySpectateDecision.Normal && after != null && Near(after.WorldPosition, whilePlaying) ? Pass("The change made while you were playing applied when you stopped.") : Wait("Applying held changes…");
        });

        Add("delete", "Its delete appears in your scene", 20, false, () =>
        {
            bot.SendEdit(scenePath, new[] { SoloScript.Delete(cube) });
        }, () => Find(cube) == null ? Pass("The object is gone from this editor.") : Wait());

        DateTime saveAt = default;
        Add("save", "Saving works while a collaborator is connected", 30, false, () => saveAt = default, () =>
        {
            if (Collab.Error != null)
            {
                return Fail(Collab.Error);
            }
            if (saveAt == default)
            {
                if (!Collab.CanSave())
                {
                    return Wait("Waiting to save: " + (Collab.WhyCannotSave() ?? "a check is still settling") + ".");
                }
                Collab.Save();
                saveAt = DateTime.UtcNow;
                return Wait("Saving…");
            }
            if ((DateTime.UtcNow - saveAt).TotalSeconds < 1.5)
            {
                return Wait("Saving…");
            }
            return Collab.Error == null && bot.IsLive ? Pass("Saved the synchronized scene; the collaborator stayed connected.") : Fail(Collab.Error ?? "The collaborator was dropped by the save.");
        });

        Add("leave", "The collaborator leaves cleanly", 15, false, () => bot.Leave(), () =>
        {
            bool gone = !Collab.Roster.Values.Any((Packet x) => x.SteamId == TestHarnessRules.SoloGuestSteamId);
            if (gone)
            {
                bot = null;
            }
            return gone ? Pass("Its row and marker were removed at once.") : Wait();
        });

        int relayPhase = 0;
        Add("via-relay", "A collaborator joins and edits through the relay", 45, false, () =>
        {
            relayPhase = 0;
            if (Collab.RelayStatus == null || Collab.RelayFailed || string.IsNullOrEmpty(invite?.Relay))
            {
                return;
            }
            relayBot = NewRelayBot();
        }, () =>
        {
            if (relayBot == null)
            {
                return (SoloStepState.Skipped, "No working relay for this session, so this route was not tested.");
            }
            if (relayBot.State == SoloBotState.Refused || relayBot.State == SoloBotState.Failed)
            {
                return Fail("The relay route failed: " + relayBot.Refusal);
            }
            if (relayPhase == 0)
            {
                if (!relayBot.IsLive)
                {
                    return Wait("Dialling the relay…");
                }
                relayBot.Subscribe(scenePath);
                relayPhase = 1;
                return Wait("Admitted through the relay; subscribing…");
            }
            if (relayPhase == 1)
            {
                if (!relayBot.HasScene(scenePath))
                {
                    return Wait();
                }
                cube = SoloScript.NewCube(Guid.NewGuid(), Guid.NewGuid(), SoloScript.SnapVec(OrbitCenter.X, OrbitCenter.Y, 45), SoloScript.Color(0.3125, 0.59375, 1.0));
                relayBot.SendEdit(scenePath, new[] { SoloScript.Create(cube) });
                relayPhase = 2;
                return Wait("Sending an edit through the relay…");
            }
            if (relayPhase == 2)
            {
                if (Find(cube) == null)
                {
                    return Wait();
                }
                relayBot.SendEdit(scenePath, new[] { SoloScript.Delete(cube) });
                relayPhase = 3;
                return Wait("The object arrived; removing it…");
            }
            if (Find(cube) != null)
            {
                return Wait();
            }
            relayBot.Leave();
            relayBot = null;
            return Pass("Joined, subscribed, created and deleted an object, all through the relay.");
        }, () =>
        {
            relayBot?.Leave();
            relayBot = null;
        });

        int closedPhase = 0;
        Add("closed", "The test identity opens nothing else", 30, false, () => closedPhase = 0, () =>
        {
            if (closedPhase == 0)
            {
                // The earlier steps' collaborators say goodbye and the host relays it; the reserved account must be gone from the roster before this check
                // starts, or "already connected" would be the refusal and would prove nothing about the token.
                if (Collab.Roster.Values.Any((Packet x) => x.SteamId == TestHarnessRules.SoloGuestSteamId))
                {
                    return Wait("Waiting for the earlier collaborator to finish leaving…");
                }
                impostors.Add(NewDirectBot(TestHarnessRules.SoloGuestSteamId, "Wrong token", TestHarnessRules.NewRunToken()));
                impostors.Add(NewDirectBot("76561198000000002", "Other account", runToken));
                closedPhase = 1;
                return Wait("Trying a wrong token and a different account…");
            }
            if (impostors.Any((SoloBot x) => x.IsLive))
            {
                return Fail("A collaborator without the test token was admitted.");
            }
            if (impostors.Any((SoloBot x) => x.State == SoloBotState.Connecting || x.State == SoloBotState.AwaitingWelcome))
            {
                return Wait();
            }
            // Each must be refused BECAUSE its identity token was rejected. A refusal for any other reason (for example "already connected") would
            // pass a weaker check while proving nothing about the token.
            SoloBot wrong = impostors.FirstOrDefault();
            foreach (SoloBot x in impostors)
            {
                if (x.State != SoloBotState.Refused || x.Refusal == null || x.Refusal.IndexOf("rejected", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return Fail("Refused for the wrong reason, or not refused: " + x.State + " " + Truncate(x.Refusal, 100));
                }
            }
            return Pass("A wrong token and a different account were both refused as: " + Truncate(wrong?.Refusal, 90));
        }, () =>
        {
            foreach (SoloBot x in impostors)
            {
                x.Dispose();
            }
            impostors.Clear();
        });

        Add("demo", "Live demo: the collaborator moves an object in your scene", 45, false, () =>
        {
            // Pressing Play and Stop earlier resets the scene view, so frame the orbit again for the part meant to be watched.
            PlaceCamera();
            demoBot = NewDirectBot(TestHarnessRules.SoloGuestSteamId, TestHarnessRules.SoloGuestName, runToken);
        }, () =>
        {
            if (demoBot.State == SoloBotState.Refused || demoBot.State == SoloBotState.Failed)
            {
                return Fail("The demo collaborator could not join: " + demoBot.Refusal);
            }
            if (!demoBot.IsLive)
            {
                return Wait("Connecting…");
            }
            if (!demoBot.HasScene(scenePath))
            {
                demoBot.Subscribe(scenePath);
                return Wait("Subscribing…");
            }
            if (demoCube == null)
            {
                demoCube = SoloScript.NewCube(Guid.NewGuid(), Guid.NewGuid(), SoloScript.SnapVec(OrbitCenter.X, OrbitCenter.Y, 45), SoloScript.Color(1, 0.40625, 0.21875));
                cube = demoCube;
                demoBot.SendEdit(scenePath, new[] { SoloScript.Create(demoCube) });
                return Wait();
            }
            return Find(demoCube) != null ? Pass("Running. Watch the blue sphere and the cube; press Stop to end.") : Wait();
        });
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
    }

    // ---- report --------------------------------------------------------------------------------------------------------

    private static void WriteReport(string state)
    {
        try
        {
            string path = Path.Combine(Root, ".collaboration", ReportFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string json = JsonSerializer.Serialize(new
            {
                state,
                summary = SoloReport.Summary(views, state != "running"),
                headline,
                updatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                steps = views.Select((SoloStep s) => new { id = s.Id, title = s.Title, state = s.State.ToString(), detail = s.Detail }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>The last written report, for the dock's "last run" line. Null when there is none.</summary>
    public static string LastRunSummary(string root)
    {
        try
        {
            string path = Path.Combine(root, ".collaboration", ReportFile);
            if (!File.Exists(path))
            {
                return null;
            }
            JsonNode node = JsonNode.Parse(File.ReadAllText(path));
            return node?["summary"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
