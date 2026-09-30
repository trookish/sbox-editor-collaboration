# Validation record

## Scope warning: what has and has not been proven

The relay, presence and play-pause work of 2026-09-29 is described in its own dated section near the end of this file, with an explicit list of what was not run. The Steam-identity work added in Task 7 is verified **only** by compilation and by engine-independent unit tests. **A live Host/Join handshake has never been run.** Reading the signed-in Steam account and obtaining an identity token have since been observed in an open editor — see the dated section at the end of this file — while the handshake itself, the verification call and every multi-peer behaviour have not. Every editor-run result in the middle of this file is **historical evidence gathered before this work** and does not cover Steam identity. See [API-AUDIT.md → Known gaps](API-AUDIT.md#known-gaps).

## Result

Verified in this documentation pass (2026-09-25), all on cold `--no-incremental` builds:

- **Build:** `dotnet build Tools/Compile/Compile.csproj --no-restore --no-incremental` — **Build succeeded, 0 Warning(s), 0 Error(s)**, using the installed .NET 10 / s&box assemblies and the installed Base Editor Library project.
- **Collaboration editor build:** `dotnet build Libraries/collaboration/Editor/editor_collaboration.editor.csproj --no-restore --no-incremental` — **Build succeeded, 0 Warning(s), 0 Error(s)**.
- **Engine-independent checks:** `dotnet run --project Tools/CoreTests/CoreTests.csproj --no-restore` — **377 tests passed**, console tail `377 tests passed`. This **replaces the earlier 351 figure**; the suite has grown since that number was recorded. All builds were run with `--no-incremental`, because incremental builds silently skip unchanged files and can hide compiler warnings.
- The 377 checks are pure and engine-independent. They cover the handshake validator, `SteamIdentityRules`, `SteamTokenRules.Decide`, `SteamIdentityAdmission`, and the runtime/authority state machines. **They do not run an editor, read a Steam account, or open a socket to a real peer with a real identity.**

Editor-run results below are **historical and predate the Steam-identity work**. They are retained as evidence of what the earlier milestones demonstrated, not as evidence that identity works:

- **Open-editor checks (2026-09-25, single open editor):** `Tools/Drive-OpenEditor.ps1` — **13 passed** against the already-running `sbox-dev.exe` (PID 3648) in the user's own project. Per-check results: `TestResults/open-editor-probe.json`. **These 13 checks cover the play-mode lifecycle and a real editor scene/transform edit. They do not cover Steam identity, host/join, the roster, or the handshake** — none of the 13 check names concerns identity. No live editor has hosted or joined a collaboration session.
- **Two-editor checks (pre-Task 6, not rerun):** `Tools/Run-EditorTests.ps1` — **46 passed**, using **two separate `sbox-dev.exe` processes** and independent project directories under `.collaboration/test-copies/A` and `B`. The test editors were PIDs **27952** and **25008**. Per-check timestamps and results are retained in [editor-test-results.json](editor-test-results.json); the machine-local original is `TestResults/editor-tests.json`.
  - **This suite is now stale against the code.** It predates the fail-closed Steam handshake, and the probe it drives calls `Start` without the now-required identity values, so its host/join checks would be refused at the handshake. It must be re-baselined before it is cited as current evidence.
- **Editor probe compile gate:** copying `Tools/EditorProbe.cs` to `Editor/EditorProbe.cs` then building succeeded with 0 warnings and 0 errors. The copy is **not** in place at the time of this record. `EditorProbe.cs` is test-only and must be removed before shipping the library.

The editor probe receives local test commands and executes public editor APIs on editor frames. The Collaboration dock is opened through the real `EditorWindow.DockManager`. Scene capture, GUID checks, rendering callbacks and remote application execute inside the engine — but only in the historical runs above.

## Milestone coverage

The milestones below were demonstrated by the **historical** two-editor run. Read them as pre-Task-6 evidence.

| Milestone | Observed checks |
| --- | --- |
| 1 — Library and dock | Local library loaded by two editors; registered Collaboration dock opened in both processes. |
| 2 — Connection | Host/join, both rosters, explicit wrong-key rejection, explicit incompatible-project rejection with sync instructions. |
| 3 — Presence | Remote camera position arrives without saving; camera and selection gizmo draw callbacks execute; other-scene peers remain in roster with markers filtered; opening their scene restores markers. |
| 4 — Operations | Unsaved object creation preserves GUID; rotation/scale/position converge; eight intermediate transform values arrive; one continuous-edit undo reverses the complete gesture; model/collider object creation preserves component GUIDs; Tint, collider Scale and light LightColor synchronize; parent/rename/delete and collaboration undo converge. |
| 4 — Lifecycle | Late join from unchanged disk receives unsaved host state; disconnect removes presence; host edits during disconnect; reconnect has no duplicate IDs; closed scenes tolerate reconnect and catch up when reopened; new join accepts a host-saved checkpoint. |
| 4 — Failures/save | Simultaneous same-object changes yield an explicit conflict; recovery converges both editors; unsupported NetworkMode change halts synchronization; recovery restores host state; saved GameObject data agrees on both copies and contains no presence-marker objects. |

None of these checks exercised a Steam identity token, a verification endpoint, or the canonical duplicate-account rule. The continuous-edit test uses the library's public `BeginContinuousEdit()` scope and real successive editor transform changes. Production built-in drag grouping reads `Editor.Application.MouseButtons`. **A physical/native gizmo mouse drag was not automated or manually visually accepted.** This distinction matters: transport streaming and final grouping were tested, but it is not evidence of every native tool's interaction behavior.

## Protocol checks

The standalone executable verifies first-accepted-wins conflicts, idempotent replay, independent-object merging, atomic multi-object rejection, creation, cycles, orphan prevention, subtree deletion, authoritative late-join snapshots, unsupported property rejection, seed and operation persistence failures, and actual framed TCP message/acknowledgement round trips. It additionally covers the Steam handshake contract: a hello with a missing, malformed, or zero SteamID is refused; a hello with a missing or malformed token is refused; a mismatched, malformed, missing, or rejected token verdict is always refused; the signed-out sentinel `0` never verifies; canonical duplicate detection rejects a second slot for one account. These are **unit-level** checks against the pure validators — they use synthetic values such as `"76561198000000001"` and `"test-steam-token"`, not a real account and not a real endpoint.

## Bounds of the evidence

- **In the runs recorded above, the transport was plain TCP and unencrypted.** Those runs predate the AES-256-GCM framing, so nothing in them is evidence of confidentiality, integrity, or replay protection on the wire. The current library does encrypt; what it still does not provide is stated in [API-AUDIT.md → Known gaps](API-AUDIT.md#known-gaps).
- **A peer's Steam identity was not proven in the runs recorded here.** The verification endpoint was not wired at the time, so a host reported `ServiceUnavailable` and the SteamID on the wire was self-asserted. The endpoint is wired now but still has never been observed executing — see [API-AUDIT.md → Known gaps](API-AUDIT.md#known-gaps); no test in this file changes that.
- **The live Steam-identity path had not run when these runs were recorded.** An open editor has since read the signed-in account and completed an identity-token fetch, but no handshake has been observed completing. The passing checks quoted in this section (377 at the time) are engine-independent and do not cover it; the current count is in the dated section at the end.
- This was a same-machine loopback test with separate local project copies, not a WAN/high-latency, large-team or large-scene qualification.
- Marker drawing was exercised in the engine and counted by the rendering callback. There was no screenshot-based visual QA of marker placement, label readability or overlapping selections.
- The property allowlist is in `Core/Protocol.cs`. Direct runtime inspector-property coverage here is ModelRenderer.Tint, BoxCollider.Scale and DirectionalLight.LightColor. The other listed scalar properties follow the implemented allowlisted path and current source APIs, but were not individually exercised through their native inspector controls in this run.
- Copy/paste/duplicate UI gestures, prefab/terrain/mesh workflows, editor-time custom component side effects, host process crash recovery, automatic outbox replay after application restart, asset transfers and native undo/redo are not claimed as validated.
- Hotload/source changes, native saves, schema/serialization drift and non-serializable objects fail conservatively. A final regression caught that `Destroy()` is deferred while `IsValid` can still be true; the detection now also checks the public `IsDestroyed` property. The complete 46-check suite passed after that correction.

## Reproduce

Use the commands in the [library README](../README.md). Close every s&box editor first: the current build redirects new launches while an editor is already open. `Prepare-EditorTests.ps1` now detects this and exits with the blocking PID/command instead of timing out. It starts the test editors sequentially because simultaneous startup previously collided on s&box's shared `config/addons.json`, and copies the same source asset metadata to both fixtures. The runner saves modified fixture scenes near its end; run preparation again with all editors closed to restore fresh copies before another complete run.

Keep `EditorProbe.cs` out of production libraries/projects. Its file-command interface exists only to automate these local disposable test fixtures.

## 2026-09-28 — Phase 6 closing pass (task T7)

Everything below was executed on 2026-09-28 against this workspace and the user's already-open editor (`sbox-dev.exe`, process **13496**). The editor was never restarted or closed; every editor command went through `Tools/Invoke-OpenProbe.ps1`.

### Gates run for this pass

- **Engine-independent unit tests:** `dotnet run --project Tools/CoreTests/CoreTests.csproj` — **`624 tests passed`**, exit code 0. This replaces the 377 figure recorded on 2026-09-25 and the 405 baseline; the suite has grown with the handshake, encryption, discovery, admission, config, manifest, readiness and lifecycle work. The tests remain pure: no editor, no socket to a real peer with a real identity, no live service.
- **Editor compile check:** `dotnet build Tools/Compile/Compile.csproj -v q --nologo` — **`Build succeeded.` / `0 Warning(s)` / `0 Error(s)`**, exit code 0, 7.19 s. This compiles the whole library, including `Editor/**`, against the installed s&box assemblies.
- **Shared config load:** `ProjectSettings/collaboration.json` was read with `CollaborationConfig.LoadFrom` from a throwaway console project written **outside** this workspace (no file added to the repository). The result: `error=<null>`, `ProjectId=2e9f8f81-8b38-4207-ad3e-4e7c0bea567f`, `Org=cyn`, `LibraryIdent=cyn.editor_collaboration`, `LibraryVersion=1.1.1`, `RequireSteamFriend=True`, `RequireOrgMember=True`, `RequirePublished=True`, `OrgMemberAllowlist.Length=0`.
- **`LibraryIdent` format, observed and corrected.** The field was first written with a slash joining the org to the ident instead of a dot — a form `IdentPart` cannot split, so it never names an installed package. With that value the open editor's readiness checklist reported the library row blocked, and a `Start` in that window was refused with `Hosting is unavailable: library installation required.` The cause is `IdentPart` (`Editor/CollaborationSession.cs`, `static string IdentPart`), which splits a full ident on the **last `.`** and returns the whole string when there is none — so a slash-separated value can never equal the ident part of the installed library (`editor_collaboration`) and `InstalledLibrary` reports "not installed". The shipped value is the dot form `cyn.editor_collaboration`, which is also how every other package full ident is written in this project (`cyn.collab_library_testing`). **No `Start` was run afterwards to confirm the row turned green** — starting a session is out of scope for this pass and would interfere with the editor work running alongside it; the config-load result above and the single code path are what this claim rests on.
- **Stale-claim greps** — the three patterns are named here only in paraphrase so this record does not register as a hit against itself: the pre-v4 handshake name → **0 hits** anywhere in the repository; a package-descriptor `Org` value of `local` → **0 hits**; the advanced field label `Host address (host binds…)` used as an instruction → **1 hit**, `Libraries/collaboration/Editor/Dock/ConnectCard.cs:141`, which is the *Advanced* fallback field label in library code (out of scope for this pass) and appears in no instruction any more.

### Live probe checks (open editor, process 13496)

| Command | Outcome |
| --- | --- |
| `status` | `success:true`. `Status=Offline`, `Error=null`, `Connected=false`, `Roster=[]`, `SourceScenePath=scenes/minimal.scene`, `PlayModeStatus=Source collaboration`. Identity state in the same reply: `TokenState=obtained`, `TokenLength=36`, `TokenError=null`, `StartState="started with the signed-in account 76561198964875726; identity token presented (36 characters)"`. |
| `probe-org` (`cyn`) → `probe-org-state` | `success:true`, then `OrgState=done`, `OrgError=null`, `OrgFound=true`, `OrgIdent=cyn`, `OrgTitle=Daloo Works`, `OrgMemberCount=1`. |
| `probe-steam` (`76561198964875726`) | `success:true`, `SteamIdError=null`, `SteamIsFriend=false`, `SteamIsFriendError=null`, `SteamIsOnline=true`, `SteamIsOnlineError=null`; `FriendName=live spontaneously`, `FriendIsOnline=true`, `FriendIsFriend=false`, `FriendIsAway=true`, `FriendIsBusy=false`, `FriendIsPlayingThisGame=true`, `FriendError=null`. Time-varying presence values are a point-in-time reading. |
| `probe-avatar` (`76561198964875726`) → `probe-avatar-state` | `success:true`, `AvatarState=returned`, `AvatarError=null`, `AvatarIsValid=true`, `AvatarIsLoaded=true`, `AvatarWidth=64`, `AvatarHeight=64`; after the follow-up poll `AvatarPixmapWidth=64`, `AvatarPixmapHeight=64`, `AvatarPixmapError=null`. |
| `probe-library` | `success:true`, `Libraries=[{FullIdent: "local.editor_collaboration", Version: "1.1.1"}]`, `LibraryError=null`, `LibrarySystemAllCount=1`, `IsPublished=true`, `ProjectOrg=cyn`, `ProjectIdent=collab_library_testing`, `ProjectFullIdent=cyn.collab_library_testing`, `LibraryProjectError=null`. The full ident is still `local.…` because the open editor loaded the library before `collaboration.sbproj` was changed to `Org: cyn`; a reload (which this pass may not perform) is needed before the descriptor change is visible here. |
| `probe-package` | Started with `success:true`, `PackageState=pending`; on the next poll `PackageState=done`, `PackageError=null`, `PackageFullIdent=cyn.collab_library_testing`, `PackageFound=true`, `PackagePublic=false`, `PackageOrgIdent=cyn`, `PackageUpdated=2026-09-26T20:25:56.8+00:00`, `PackageTypeName=game`. |
| `probe-paint` | `success:true`, `PaintOverrideExists=true`, `PaintOverrideFieldType=System.Func\`1[[System.Boolean, …]]`, `PaintConstructResult="constructed; override invoked and returned false"`, `PaintError=null`. The throwaway widget is constructed parentless and destroyed; the user's dock is untouched. |
| `dock` | `success:true`, process 13496, no error. The registered Collaboration dock opened through the real `EditorWindow.DockManager`. |

**Later readings during the final gate run.** The probe driver is shared with other work running against the same open editor, so `Status`/`Error` changed between readings. In order: (a) after the config correction, one reading still carried the sticky `Hosting is unavailable: library installation required.` recorded by a `Start` attempted while `LibraryIdent` still held the slash form — the editor's own log timestamps it `2026/09/28 15:25:17`; (b) a later reading carried `No connection could be made because the target machine actively refused it.` — a different, subsequently written failure (logged at `15:31:37` and `15:31:48`), so the library-installation refusal was no longer the recorded one; (c) the last successful `status` and `dock` readings, both `success:true` from process 13496, reported `Status=Offline`, `Error=null`, `Connected=false`. Whether the readiness library row itself turned green was **not** confirmed by a `Start` of this pass: starting a session is out of scope here and would have collided with the editor work running alongside it. `Fail` is one-shot (`if ( Error is not null ) return;`) and the start/reconnect commits clear `Error`, so that field reflects the most recent attempt, not a live evaluation of the corrected config.

**The open editor exited during the final gate run.** Two further probe attempts (`status` twice, 25 s and 75 s budgets) timed out with *Open editor did not respond*; `tasklist` then showed **no `sbox` process at all**. The engine log ends with a clean shutdown — `15:36:09 [engine/ToolFramework2] Shutting down Qt windows`, `Source2Shutdown`, `ShutdownSource2Logging` — with no exception or stack trace before it, so the editor was closed normally rather than crashed. **No command from this task closes or restarts the editor**, and it was not restarted here. The gate results above (`status` and `dock` both `success:true`) are the last successful readings before that shutdown. The same log also corroborates the one failed build seen during this pass: `15:23:07 Compile of 'local.editor_collaboration.editor' Failed: … 'CollaborationSession' does not contain a definition for 'ReconnectRecovery'`, which is an in-flight edit in `Libraries/collaboration/Editor/**` belonging to other work, resolved by the next build.

### What was NOT run for this pass

- **The two-editor harness** (`Tools/Prepare-EditorTests.ps1` + `Tools/Run-EditorTests.ps1`) — not re-run since protocol v4, and not runnable while any s&box editor is open. It also needs two Steam accounts, or `RequireSteamFriend`/`RequireOrgMember` temporarily set to `false`, because both fixture editors sign into one account.
- **A completed host/join handshake observed end to end.** The probe state carries a `Start` that returned without throwing, but every reading taken in this pass is `Status=Offline`, `Connected=false`, `Roster=[]`, `Error=null`; nothing here observed a session.
- **The real HTTP token-verification path** against the live identity service — no request and no verdict were recorded.
- **A real-router UPnP port mapping.**
- **Two-machine LAN discovery (UDP 27846), and a two-machine STUN/UPnP remote-join path.** Every connection exercised anywhere in this project is loopback on one machine.
- **Cross-account friend admission** — a friend gate evaluated for a second, distinct Steam account. The organization has one member and it is the signed-in account.
- **Multi-peer scene-view visuals with a second editor** (nameplates, camera frustums, selection and focus indicators).
- **Dock interactive clicks.** Buttons, checkboxes and collapsible sections were never exercised; only probe commands were driven.

### Configuration and packaging changes recorded by this pass

- `Libraries/collaboration/collaboration.sbproj`: `Org` changed from `local` to `cyn`; `Ident` stays `editor_collaboration`, `Title` stays `Editor Collaboration`.
- `Libraries/collaboration/.version` created with `1.1.1`, byte-identical to the existing `VERSION`. The file name was checked against the installed editor: `addons/tools/Code` contains no `ReadVersionFromFile`, but the installed `Sandbox.Tools.dll` carries both the identifier `ReadVersionFromFile` and the literal `/.version`, next to `"Failed to install library, version wasn't found"`.
- `ProjectSettings/collaboration.json` extended to the full schema (`Org`, `LibraryIdent`, `LibraryVersion`, the three `Require*` switches, `OrgMemberAllowlist`), load-verified as above.
- `Tools/Prepare-EditorTests.ps1`: the two references to the non-existent `testing.sbproj` now name `Collab_Library_Testing.sbproj`. The `Assets/scenes/minimal.scene` → `secondary.scene` copy was left alone — that source file exists.

**Publication of the library package to Asset Party was not attempted.** No publishing tool was found in this workspace or in the editor's own command surface, so publishing is a manual maintainer action; until it happens, `LibraryIdent` (`cyn.editor_collaboration`) names a package that does not exist on Asset Party yet, and the readiness checklist's library row matches on the ident part only, so a local copy still counts as installed.

## 2026-09-29 — Wave B pass (mirrored native undo, shortcuts, toasts, automatic recovery)

Everything below was executed on 2026-09-29 against this workspace and the user's already-open editor (`sbox-dev.exe`, process **23460**). The editor was never restarted or closed by this work; it hot-loaded the library source on its own after each edit, and every editor command went through `Tools/Invoke-OpenProbe.ps1`.

### Gates run for this pass

- **Engine-independent unit tests:** `dotnet run --project Tools/CoreTests/CoreTests.csproj` — **`681 tests passed`**, exit code 0. The Wave B start baseline was 652, so this pass added 29 tests. They cover the recovery classification (`SessionFailureClass`, `AutoRecoveryRules.Classify`), the attempt/backoff eligibility (default off, terminal never, unsaved blocks, host never, at most three attempts), the prompt that never offers an action the reconnect guard would refuse, and the backoff sequence.
- **Editor compile check:** `dotnet build Tools/Compile/Compile.csproj -v q --nologo` — **`0 Warning(s)` / `0 Error(s)`**, exit code 0. Re-run after the final source edit (a dock-layout diagnosis change in `DockTheme.cs`/`CollaborationDock.cs`) and still 0/0 with 681 tests.

### Live evidence (open editor, process 23460)

| Command | Outcome |
| --- | --- |
| `status` | `success:true`. Wave B fields read back: `AutoRecover=false`, `RecoveryOffered=null`, `UndoNotice=null`, `SaveNotice=null`, `ToastCount`, `ToastState="constructed"`, `NativeUndoBack`, `NativeUndoForward`, `NativeUndoTop`, `NativeUndoError`, `ShortcutState`, `ShortcutError`. |
| `host` | The first call returned `success:false` carrying `System.InvalidOperationException: identity-token-pending: the Steam identity token is not available yet; poll status until TokenState is "obtained", then retry`, raised at `EditorProbe.StartWithIdentity` (`Editor/EditorProbe.cs:993`). The session had nevertheless started: the next `status` read `Status="Connected \x07 live"`, `Connected=true`, `Error=null`. Reported here because it is what the command actually returned, not because it is the outcome that was wanted. |
| `create-model` | `success:true`; three seconds later `Status="Connected \x07 revision 1"`. |
| `status` (after the commit) | **`NativeUndoBack=1`, `NativeUndoForward=0`, `NativeUndoTop="Collaboration Undo"`, `NativeUndoError=null`.** This is the proof for Item 2: the native `UndoSystem.Back` stack holds exactly one entry, named by us, after an edit was acknowledged. The `Insert` itself is engine-side and cannot be unit-tested, so this reading is the evidence. |
| `shortcuts` | `success:true`, `ShortcutState="all"`, `ShortcutError=null`. All three handler bodies (`collab.save`, `collab.undo`, `collab.recover`) ran and returned without an exception reaching the dispatcher. Invoking `collab.recover` on a hosted session produced the guard refusal `A hosted session cannot be recovered in place; leave and host the session again.` as the recorded `Error` with `Connected` still `true` — the refusal is surfaced rather than thrown, which is the behaviour the guarded button already had. |
| `join` to a dead port (prompt mode, `AutoRecover=false`) | `Error="No connection could be made because the target machine actively refused it."` (a `SocketException`, classified transient) and **exactly one toast: `ToastCount=1`, `ToastTitles=["Connection lost"]`**. A second `status` 12 s later still read `ToastCount=1` — nothing was re-offered — and no reconnect attempt was made while the toggle was off. |
| `auto-recover false` then `auto-recover true` (same outage) | Silent retry armed. Following samples alternated `Status` between `Reconnecting; preparing identity…` and `Reconnecting; recovering authoritative state.`, then the refusal returned. Exactly **one** `ToastTitles=["Automatic recovery stopped"]` appeared, first observed in the sample at t+17.5 s after arming; the 27 s of samples after it contain no further `Reconnecting` line. Attempt one ran immediately after arming (t+0.4 s), then the one-second and four-second gaps, then the ten-second gap before the budget is declared spent — 0.4 s + 15 s matches the observed t+17.5 s stop. `AutoRecover` was set back to `false` afterwards. |
| `toast` | `ToastState="constructed"`, `ToastError=null`, and `ToastCount`/`ToastTitles` read back the live manager list. The final reading was `ToastCount=0`, `ToastState="constructed"` — no toast was left on screen. |
| `leave` | `success:true`; the following `status` read `Status="Offline"`, `Error=null`, `Connected=false`, `AutoRecover=false`, notices `null`. |
| `dock` | `success:true`, `error=null`. |
| `dock-layout` | `state=faulty` at that moment — root-caused as a false positive in the diagnostic and fixed in the follow-up pass (see the resolution note below the sequence). |

**Exact strings introduced by this pass.** Shortcut chords `CTRL+ALT+S` (`collab.save`, save the synchronized scene), `CTRL+ALT+Z` (`collab.undo`, undo my last collaboration operation), `CTRL+ALT+R` (`collab.recover`, recover the connection). Native undo entry name `Collaboration Undo`. Toast titles `Connection lost` (prompt, 15 s) and `Automatic recovery stopped` / the recovery outcome toasts (outcome duration). Controls-card notices are bounded by `UndoNoticeSeconds = 8f` and `SaveNoticeSeconds = 8f`.

### The dock layout diagnostic did not end green

`-Command dock-layout` at the end of the pass returned:

`state=faulty; reflows=194; dock=509x970; viewport=485x908; column=908; columnWanted=808; scroll=on; compressed=connect; overlaps=0`

- At the start of this pass the same build reported `state=ok; … compressed=none; dock=278x970`.
- The diagnostic means only that `ConnectCard`'s own `WantedSize()` exceeds the height the column last gave it. `overlaps=0` and the column as a whole fits (`columnWanted=808` inside `column=908`); no two cards overlap.
- The reading changed when the dock was widened from 278 px to 509 px — the probe's `dock` and `shortcuts` commands call `DockManager.OpenDock` — and after a hot-load. It was not present before either.
- Tried, in this order, without clearing it: closing and reopening the dock, `redock` to left/bottom/right, opening and closing the Performance dock in the same area, and `force-rebuild` (a fresh `Build()`).
- Two invalidations were added and hot-loaded while diagnosing: `DockTheme.SetText(Label, …)` now calls `UpdateGeometry()` when the text really changes (a label that gains a wanted line previously never re-laid its column), and `CollaborationDock.Reflow()` now invalidates each card as well as the dock. Both are defensible in isolation and both stay behind the existing comparison-first/`reflowing` guards, but **neither cleared the reading, and a known-good `state=ok` was not reproduced afterwards**, so neither can be credited with a fix.
- The compressed list named `connect` throughout and `controls` only after the last hot-load; `controls` is the card that gained the Auto-recover row in this pass, so its appearance in the list cannot be ruled out as related.

**Resolution (follow-up pass, 2026-09-29).** The reading was a **false positive in the diagnostic**, not a layout fault. `DockCard.WantedSize()` is `Widget.SizeHint()`, which for a wrapped label is frozen at the width the text was last written at and does not follow the panel when it is dragged wider, while the heights are re-laid-out width-aware — measured: the Connect card read `want=152` at both 509 px and 700 px while its rows re-laid correctly at both widths, and at the original 278 px the frozen hint happened to equal reality, which is why the reading looked green there and flipped on the first width change. The diagnostic now judges `MinimumNeeded()` (`MinimumSizeHint().y`, a width-independent floor) against the card height plus a content-overflow test (`ContentBottom > Height`), with `WantedSize()` kept for context only; an overflow-only rule was rejected because a force-squeezed Connect card (60 px) reported itself comfortably fitting while its children rendered at 5 px. After the fix `dock-layout` returned `state=ok; compressed=none; overlaps=0` at 278, 400, 509 and 700 px and after a fresh `Build()`, and the forced-squeeze check still returns `state=faulty; compressed=connect` (`min=138 > h=60`) — real compression is still caught. Gates at that pass: 693 tests, build 0 warnings / 0 errors. The two invalidations added during this pass (`DockTheme.SetText` → `UpdateGeometry()`, `CollaborationDock.Reflow` → per-card invalidation) remain, correct on their own terms.

### What was NOT run or proven for this pass

- **Real key presses.** The chords `CTRL+ALT+S`/`CTRL+ALT+Z`/`CTRL+ALT+R` dispatching through `EditorMainWindow`'s `[Shortcut]` registry was never exercised — no key events can be sent to the editor. Only the handler bodies were invoked.
- **`CTRL+Z` acting on the mirrored entry.** The entry's presence in `Back` was observed; neither the Undo dock's Undo button nor the native chord was pressed, and no undo was watched actually run.
- **Whether a toast is painted.** The probe reads `ToastManager` state (count, titles), not pixels. A toast appearing on screen, its position and its styling were not observed.
- **A real mid-session link loss.** The transient failure used was a refused TCP connection on a dead port. Heartbeat timeout, a stored write failure and an IO failure were not reproduced live — they are covered by unit tests of the classification only.
- **Real two-editor recovery.** Every connection here is loopback on one machine; no second editor exists to lose.
- **The two-editor harness, UPnP/STUN/LAN discovery, cross-account admission** — still not run, unchanged from the 2026-09-28 record above.
- **The redo side of the mirrored entry being watched run.** Redo is built from the forward change set captured when the entry was seeded — `TryRedo( tracked, changes )` applies the captured `After` states after checking every object is still in its `Before` state — and its refusal notices are verified in code, not live. No redo was invoked through the native Undo dock or `CTRL+SHIFT+Z`, so that path was not observed.

### Log evidence at the end of this pass

Source: `C:/Program Files (x86)/Steam/steamapps/common/sbox/logs/sbox-dev.log`.

- Lines containing `Exception` **and** naming `TeamCreate`: **0**.
- Occurrences of `Painter not active`: **0**.
- `Exception` anywhere in the log: 73, none naming `TeamCreate` — the same count as before this pass's final hot-load, so no new engine exception was introduced.
- Three `Error |` lines naming `TeamCreate`, all timestamped `2026/09/29 02:12:38`–`02:12:52`: `Cannot convert type 'Editor.DockWidget' to 'TeamCreate.CollaborationDock' via a reference conversion…` — the probe's earlier dock lookup, raised by an unconditional cast. The lookup was corrected to `OpenDock(…)?.Widget ?? FindDockWidget(…)?.Widget`; no line of that kind appears after 02:12.
- 38 `[hotload/GameMenu] Collection keys (TeamCreate.CollaborationSession) have started colliding during hotload` warnings, the first at `00:51:19`. They span every hot-load of this work rather than the last edit, are warnings rather than exceptions, and were left alone.



---

## 2026-09-29 — Appendix: unit-test gate counts recovered from the transcripts

> **Reconstructed appendix.** This list was **not** recovered from the deleted file. The pre-deletion
> `VALIDATION.md` recorded only the per-pass figures (the 377/405 baseline, then 624, 652, 681 and 693
>); the intermediate counts below exist only in the delegation transcripts and are reproduced here with
> their source. Every row is a strict `N tests passed` match from the engine-independent unit-test gate
> (`dotnet run --project Tools/CoreTests/CoreTests.csproj`); nothing in this table has been re-run since
> the deletion.

| Tests | Date recorded | Work the count belongs to | Transcript source |
| --- | --- | --- | --- |
| 405 | 2026-09-28 | baseline before the Phase-1 core-logic work | `cache/delegation/live/deleg_1a1fb6d0/task-*.log` |
| 454 | 2026-09-28 | Phase-1 core-logic TDD additions | `cache/delegation/live/deleg_1a1fb6d0/task-*.log` |
| 472 | 2026-09-28 | Phase-1 fix pass (core logic items 1-14) re-verification | `cache/delegation/live/deleg_6a274b8e/task-*.log` |
| 482 | 2026-09-28 | T2 handshake-v4 gates, review verification | `cache/delegation/live/deleg_239d72cc/task-*.log` |
| 507 | 2026-09-28 | T2 handshake-v4 gates complete (build 0/0 alongside) | `cache/delegation/live/deleg_239d72cc/task-*.log` |
| 534 | 2026-09-28 | baseline before the T4a transport-defect fixes | `cache/delegation/live/deleg_3a72fef8/task-*.log` |
| 555 | 2026-09-28 | T4a transport fixes (baseline 534 + 21 new), build 0/0 | `cache/delegation/live/deleg_3a72fef8/task-*.log` |
| 600 | 2026-09-28 | T4b transport/dock defect fixes | `cache/delegation/live/deleg_429eb8cd/task-*.log` |
| 606 | 2026-09-28 | T4b re-review fixes (project-scoped invite codes) | `cache/delegation/live/deleg_429eb8cd/task-*.log` |
| 624 | 2026-09-28 | reconnect identity-token work, Phase-6 baseline | `cache/delegation/live/deleg_af0d5abe/task-*.log` |
| 628 | 2026-09-28 | final cleanup pass (baseline 624 + 4 new) | `cache/delegation/live/deleg_1538ee35/task-*.log` |
| 652 | 2026-09-29 | Wave B: mirrored Collaboration Undo, shortcuts, toasts, auto-recover | `cache/delegation/live/deleg_55c9db34/task-*.log` |
| 681 | 2026-09-29 | Wave B gate run (build 0/0 alongside) | `cache/delegation/live/deleg_55c9db34/task-*.log` |
| 693 | 2026-09-29 | Wave B residual defects: dock-layout, Recover refusals, notice order | `cache/delegation/live/deleg_d225c7d9/task-*.log` |

Counts are reported exactly as the transcripts printed them. No count has been promoted to a
running total, and no gate listed in this table has been executed as part of this reconstruction.

## 2026-09-29 — Relay, presence and play pause

Scope: what this pass executed, what it did not, and what a reader must not infer. Everything below was run on one Windows machine with one Steam account (`live spontaneously`), against a disposable copy under `.collaboration/test-copies/A`; the user's own editor was closed for the runs.

### Gates (cold builds, last run after the final edit)

| Gate | Command | Result |
| --- | --- | --- |
| Core tests | `dotnet run --project Tools/CoreTests/CoreTests.csproj` | **805 passed**, exit 0 |
| Editor build | `dotnet build Tools/Compile/Compile.csproj --no-incremental` | **0 Warning(s), 0 Error(s)** |
| Relay integration | `dotnet run --project Tools/RelayTests/RelayTests.csproj` | **44 passed**, exit 0 (real relay, real `Wire`/`RelayHost`/`RelayGuest`, loopback) |
| Relay executable | `node Tools/Relay/smoke.js` against `dotnet run` of `Tools/Relay` (access key set) | 11 checks passed, including a wrong key, a wrong proof, garbage hello and a plain-HTTP request |
| Relay publish | `dotnet publish Tools/Relay -c Release` | succeeded |

Negative controls (the change reverted, the test must fail): the strict receive sequence (a replayed frame is refused) and the version-literal guard (a restored stale `library 1.1.1` in the dock header fails the suite, naming `CollaborationDock.cs`). Most of the other new checks were **not** individually reverted; they are behaviour pins, not proven guards.

### Executed in a live editor (copy A, hotloaded)

- **`Sandbox.WebSocket` through `SandboxRelaySocket` against a running relay:** the real `RelayHost` and `RelayGuest` ran inside the editor. A hello passed guest to host, a reply passed back, and a 300 KiB packet (many WebSocket messages) crossed intact: `atHost=loopback-guest; atGuest=welcome; bigLength=307200; hostState=Ready`.
- **Host with a relay configured (`ws://localhost:8080/relay`):** status `Connected • live`, `RelayStatus` *"Relay connected — friends can join from any network."*, the relay reported one room, and the invite decoded to name the relay plus one direct endpoint. **Leave** emptied the relay's room.
- **A guest outside the engine joining that live host through the relay:** the relay dial and the AEAD stream worked and the host answered through it with its own baseline refusal (*"Project/scene baseline differs…"*). That proves the host accepts a relayed connection and applies its own gates; it is **not** a completed join, because the driver is not a real project copy.
- **Presence marker:** drawn in the scene view for a synthetic collaborator (`DrawnMarkers = 1`), inspected in screenshots at several sizes. Two defects were found and fixed this way (marker too small; a backing disc tinted the picture blue). The final screenshot shows a circular full-colour picture inside a blue ring, a lit blue sphere with a halo and glint, a name and a status line.
- **Play pause:** `play-default` gave `PlayDecision = PausedForPlay`, the dock's *"Playing: scene edits are paused…"* line, the connection still `Connected • live` and the relay room still present; `stop` returned `Normal` with the notice cleared.
- **Dock:** the Connect card showed the relay line, the play notice and the version *library 1.2.0* (a stale `1.1.1` literal in the header was found and removed).
- One host start failed once with *"The Steam identity service could not be reached…"*; the service answered directly a moment later and the next start connected. The refusal wording is accurate; the outage was transient.

### Not executed — do not infer these from the above

- **No second Steam account, no second computer, no home NAT, no TLS relay.** The cross-network claim rests on the design and on the loopback and in-process evidence; it has not been demonstrated between two houses.
- **A real guest editor has never joined through the relay.** The dock's guest-side route line (*"Connected through the relay…"*), the multi-route failure summary, and the reconnect through the route list are covered by unit tests only.
- **The marker was drawn from a fabricated roster entry**, not from a peer's real presence packets, and the marker following a **game camera** while playing has not been seen.
- **Held scene changes during play** are unit-tested (ordering, capacity) but no edit from a second peer was held and then applied in an editor.
- **The relay-failure fallback** (the invite re-issued without a dead relay) is coded and not observed.
- **The dock's buttons were not clicked** in this pass either.
- The relay server ran only locally over `ws://`; nothing was tested through a reverse proxy, at load, or for long durations.

## 2026-09-29 — Solo test (one-click, one account)

Scope: what was executed for the solo test, and what a reader must not infer. One Windows machine, one Steam account (`live spontaneously`), the user's own editor closed for every run.

### Gates (last run after the final edit)

| Gate | Result |
| --- | --- |
| `dotnet run --project Tools/CoreTests` | **909 passed**, exit 0 |
| `dotnet build Tools/Compile --no-incremental` | **0 Warning(s), 0 Error(s)** |
| `dotnet run --project Tools/RelayTests` | **44 passed**, exit 0 |

Negative controls (change reverted, the test must fail; run and observed): the float32 round-trip guard (reverting the cube's scale to `0.6` fails *"a new cube's position, scale and rotation survive a 32-bit round trip"*), the directory-held-open replacement (failed with the live message before the fix), and the empty-leftover-folder reclaim (failed before the fix). The other new checks are behaviour pins that were not individually reverted.

### Executed live (a disposable copy, launched with `Tools/Run-SoloTest.ps1 -Relay ws://localhost:8080/relay -Wait`, several runs)

- **19 of 19 checks passed** in the final runs, with the relay registered and a relay collaborator joining, editing and leaving. The host's own journal was read afterwards and holds the collaborator's create, update and delete operations.
- **Stop from outside** (`-Stop`) ended the session in about one second and the copy's scene file was back to its original size (12,641 bytes) with no test object in it. The real project's scene was unchanged in every run.
- **The button in the real project's editor**: the *Try it alone* card renders below Controls with its intro text, *Run solo test*, the last-run line and *Open the last report*, and shows no checklist there. **The button was not clicked**: a click was prepared and not sent because the machine had 0.8 GB of memory available with the real editor open. The memory guard added afterwards was exercised through the script (it refused at 0.9 GB with the intended message) and unit tests, not through the button.
- The **menu item** (`Collaboration → Run solo test`) compiles and appeared in the menu bar; it was not clicked.

### Defects the live runs found, now fixed

Seven cascading failures from 32-bit float text (a scale of `0.6` came back as `0.600000024` and the host editor "corrected" it, making every later edit stale); a copy that could not be replaced while a tool held its folder as a working directory, leaving a folder the next run refused as "not a copy"; the test running against a scene that had not finished loading and passing on an empty one; the test's own cube being saved into the copy's scene file by the checkpoint that `Leave` performs; a check that passed for the wrong reason ("already connected" instead of "token rejected"); and the checklist living in a dock that a fresh copy has closed.

### Not executed, and not to be inferred

- The collaborator is a **script**. It shows protocol-level correctness between this editor and a conforming peer, not that a second person's editor draws the same thing.
- The host treats the reserved test identity as a friend and organization member **inside the copy**, so the real friend and organization admission gates were **not** exercised by this test.
- No second Steam account, second computer, TLS relay, router or home network was involved.
- The one-click button and menu item were not clicked; the launcher script was used for every live run, and it shares the copy code with them.
- It copies the whole project each run and was only run on this one (74 files, a 12 KB scene). Projects over 2 GB are refused.

## 2026-09-30 — Built-in relay (Cloudflare Worker), first-run setup, and the friend's failed join

Scope: one Windows machine, one Steam account, no Cloudflare account. **The Worker has not been deployed and no session has crossed the real internet.** Everything below ran locally.

### What the screenshot showed, and why

A friend's join failed with *"The relay route failed (Could not reach the relay: Unable to connect to the remote server). The host's public address 203.0.113.42:27845 did not accept a connection."* The invite code named a relay, and the address it named was `ws://localhost:8080/relay`: the host's own test relay. On the friend's computer `localhost` is the friend's computer, so the dial found nothing, and the direct route failed as before (nothing forwards the port). Two defects, both fixed: an invite could carry a localhost relay to another machine, and the failure text blamed the network instead of the code. An invite now leaves a localhost relay out unless the joiner is the solo test on the same machine, and a code that still carries one says *"This code points to a relay on the host's own computer… Ask the host to start a new session and send a new code."*

### Gates (last run after the final edit)

| Gate | Result |
| --- | --- |
| `dotnet run --project Tools/CoreTests` | **974 passed**, exit 0 |
| `dotnet build Tools/Compile --no-incremental` | **0 Warning(s), 0 Error(s)** |
| `dotnet run --project Tools/RelayTests` (the .NET relay's own suite) | **44 passed**, exit 0, re-run after the address began carrying `?room=` |
| `dotnet run --project Tools/RelayTests -- worker ws://localhost:8080/relay <key>` (same client code against the .NET relay) | **25 passed** |
| The same against the Cloudflare Worker on the real `workerd` runtime, locally (`ROOM_IN_ADDRESS=1`) | **26 passed**; 24 without that opt-in |
| `npm test` in `Tools/RelayWorker` (plain Node WebSocket clients) | 18 checks passed |
| `wrangler deploy --dry-run` | bundle builds, 12.4 KiB |

Negative control run and observed: reverting the manifest's use of `MetaFileRules` fails *"two copies of a project that differ only in the compile-dirty flag have the same manifest"*. The other new checks are behaviour pins that were not individually reverted.

### Executed live (a disposable solo copy, the Worker on the local runtime as the relay)

**19 of 19 solo-test checks passed** with the editor hosting through the Worker: the relay registered the room, a collaborator joined through it, edited and left, and the copy's scene was byte-identical to the original after Stop. This is the first run in which the relay in use was the Worker rather than the .NET relay.

### Defects found by running it, now fixed

- **Message volume.** A two-person session sent the relay 50 messages a second (presence and a heartbeat every 100 ms even when nothing changed): about 9,000 billed requests an hour, eleven session-hours of a free day for everyone combined. Presence is now sent when it changes and refreshed every two seconds, the heartbeat every two. Idle drops to 2.5 a second; two people both moving cameras is 18 a second.
- **A silent socket is not closed for about twenty seconds** on the Worker (the refusal arrives at ten, the close about ten seconds after, at the runtime's next idle point). I tried four alternatives; none changed it. It costs nothing (a hibernating socket is free and counts toward no limit) and is documented rather than hidden.
- **Save refused after a collaborator's first edit** with *"Source/assets changed during collaboration"*. The engine writes `sceneCompileDirty` into the scene's `.meta` the moment the scene is edited, and both the join baseline and the save guard hashed raw bytes. That flag is now ignored (`MetaFileRules`), every other field still counts. This also removes a way two machines could disagree about having the same project.
- **The solo test raced the readiness checks** and failed with *"project publication required"* while the same card turned green a minute later. It now waits for them.
- **A conformance check of mine was wrong** three times (a random secret used where a fixed one was needed, a `ws://` URL passed to `fetch`, a raw request needed for an Upgrade header). In each case the reference relay failed the same check, which is how it was recognised as a test bug and not a relay bug.

### Not executed, and not to be inferred

- **The Worker has never run on Cloudflare's network.** No account was used. Its behaviour there (cold starts, the free-tier request and duration limits, real hibernation eviction, real TLS) is unobserved. The message-cost figures come from a local run.
- **`npm run deploy` has not been run.** Its parsing of Wrangler's output was checked against sample text, not a real deploy. Until it is run, `RelayDefaults.DefaultUrl` is empty and the dock says *"No relay is set up…"*.
- **No friend has joined a session from another country.** The library cannot yet claim that Saudi Arabia to Germany works; the design and every local check say it should.
- **First-run creation of `ProjectSettings/collaboration.json`** is unit-tested (creation, an existing file left alone, a broken file reported, a relaxed gate preserved) but was not exercised by clicking Host in an editor with no settings file.

---

## 2026-09-29 — Reconstruction note

The `Libraries/collaboration/` folder, and with it this document, was accidentally deleted on 2026-09-29. The text above was reconstructed afterwards from the evidence that survived the deletion: the task specifications under `.hermes/tasks/` and the overhaul plan under `.hermes/plans/`; the full-file reads of this document kept in the session record, replayed against the exact unified diffs produced by every later patch to it; and the `file_size` and `total_lines` those reads reported, used as checkpoints.

The reconstruction was byte-checked against the recorded size of this file at 2026-09-29 05:33 (168 lines, 30994 bytes) before the two final patches of that pass were replayed. Nothing above has been re-run since the deletion: every entry reports work performed before 2026-09-29, no entry that was not run has been promoted to a passing result, and no continuity between the deleted file and this reconstruction is claimed beyond that byte-level check.
