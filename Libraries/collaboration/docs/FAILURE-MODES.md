# Failure modes in the source session

This is the audit of every place where the source session (`Editor/CollaborationSession.cs`) can end itself, and of the engine
exceptions from `SceneAdapter.cs` and `SceneSettingsAdapter.cs` that reach those places. Line numbers refer to
`CollaborationSession.cs` as it was before this hardening (the base of branch `w/harden`); "Fail" means `CollaborationSession.Fail`,
which ends the session for this editor.

The rule applied: a failure that says the session itself can no longer be trusted (authentication, protocol, identity, a malformed host
graph, the outbox or journal) stays fail-closed, with a formal message that says what to do. A failure caused by scene content or by
this editor's engine heals the one scene concerned, with one formal notice, and the session, presence and every other scene continue.

## Fail call sites (10) and the throws that reach them

| Site (line) | Trigger | Class | Behaviour now |
| --- | --- | --- | --- |
| `Fail` 794 | Code hotload while an approved runtime is running | Integrity / lifecycle | Unchanged: fail-closed, recovery data archived. |
| `Fail` 909, 915 | Start readiness cannot be evaluated or is refused | Admission | Unchanged: the start is refused with the readiness reason. |
| `Fail` 1527 (`AbandonReconnect`) | Automatic reconnect gave up (identity, token or teardown) | Transport / identity | Unchanged. |
| `Fail` 1707 (`ReportAction`) | A user action (save, undo, redo, play toggle, a dock button) threw | External action | `ContentFailureRules.ClassifyActionFailure`: an ordinary failure (I/O, null reference, an invalid operation from `SceneAdapter`, an `ActionRefusal`) shows `The action could not be completed: <short reason>. The session continues.` and the session goes on. The integrity failures stay fail-closed: `CryptographicException`, `AuthenticationException`, `RelayRefusedException`, out of memory, also when wrapped in an aggregate or another exception. |
| `Fail` 2226 (frame catch) | Everything thrown inside `Frame()` and `Receive` lands here | Depends on the throw | Each throw below is now classified at its source; only the integrity rows still reach this catch. |
| `Fail` 2712 | `welcome` from a host whose session id changed during a reconnect | Identity | Unchanged: fail-closed; start a new session. |
| `Fail` 2765 | `reject` with no scene (the host refused the session) | Admission | Unchanged. |
| `Fail` 2774 | `fatal`; and, before this change, every scene-level `reject` | Admission (fatal) / content (scene reject) | `fatal` unchanged. A scene-level `reject` of this editor's pending edit no longer ends the session: the scene is copied to the recovery folder, the edit leaves the outbox and the scene is resynchronized from the host, replacing the refused state (re-sending it would only be refused again). |

| Throw (line) | Trigger | Class | Behaviour now |
| --- | --- | --- | --- |
| 2005 | A connection completes while the game is playing or no scene is open | Content / lifecycle | The connection completes (`ContentFailureRules.MayPromoteConnection`, reusing `PlaySpectateRules`); scene packets are held until play stops. Only a runtime a hotload interrupted (`FailStop`) or a teardown in progress still refuses. |
| 2037 | The wire reports a stored failure (decryption, authentication, disconnect) | Security / transport | Unchanged: fail-closed (auto-recovery classification applies). |
| 2050 | `PlaySpectateDecision.FailStop`: runtime blocked by a hotload | Integrity / lifecycle | Unchanged. |
| 2067 | More than 4096 scene packets arrive while playing | Content | All held packets are discarded (`ContentFailureRules.DrainOverflow`) and every affected scene is resynchronized from the host when play ends, with one notice. Later packets for those scenes are dropped too: the snapshot contains them. |
| 2077 | Host heartbeat timed out | Transport | Unchanged. |
| 2118 | The scan meets a scene that is playing without approved source sampling | Content / lifecycle | That scene is skipped while it plays; presence and the heartbeat continue. |
| 2128 | A scene was closed and reopened while its edit awaits the host | Content | The outbox is kept and the scene waits for the answer; the notice is shown once. |
| 2132 | A newly opened scene has unsaved edits | Content | The scene is copied to the recovery folder, then subscribed. If the copy fails, the scene is not synchronized (notice once). |
| 2177 | The scene file changed on disk outside a collaboration save | Content | The disk version is copied to the recovery folder and the open, live scene stays authoritative (`DiskStampAction.KeepLiveScene`). If that copy fails, only this scene pauses. |
| 2239 | Routing refused a scene packet | Protocol if the scene key is malformed; content otherwise | `ContentFailureRules.ClassifyRouteFailure`: a malformed or missing key stays fail-closed; a scene that is not open here is ignored and logged once; an open scene that cannot take host state now (playing) is resynchronized once it can. |
| 2559 | A settings packet without settings | Protocol integrity | Unchanged: fail-closed, now checked before any apply. |
| 2703 | The recovery copy of a scene cannot be written | Data integrity of local work | The scene that needed the copy pauses; nothing is replaced without a copy. The session continues. |
| 2780, 2810 | A `reject` or scene packet for a scene this editor no longer tracks | Content | Ignored and logged once. Reopening the scene subscribes again. |
| 2806 | A scene packet with no scene at all | Protocol integrity | Unchanged: fail-closed. |
| 2835–2851 (snapshot) | `SceneAdapter.Apply` (or its internal capture) throws while applying a snapshot | Content / engine | See "Self-healing apply". `StateRules.ValidateGraph` on the host's graph runs first and stays fail-closed. |
| 2894–2986 (accepted) | `SceneAdapter.Apply` throws while applying another person's change, or this editor's rebased receipt | Content / engine | See "Self-healing apply". For another person's change, Known and the live scene are put back to their state before the packet. |
| 2559–2582 (settings) | `SceneSettingsAdapter.Apply` or the settings capture throws | Content / engine | The scene is resynchronized like an object apply failure. |
| scan capture (2140–2190) | `SceneAdapter.Capture` or the settings capture throws for the scene as a whole | Content / engine | Only that scene pauses. |
| `SceneAdapter.Capture` | One root object's `Serialize` throws | Content / engine | That object and its descendants keep their last confirmed state, are never sent (in particular never as a deletion), and one notice names it. |
| `SceneAdapter.Apply` 269 | The engine did not keep an object's GUID | Content / engine | An apply failure like any other: resync, then isolation. |
| `SceneSettingsAdapter.Apply` | One setting fails | Content / engine | Already per key (skipped keys are reported); unchanged. |

## Self-healing apply (`Core/ApplyRecovery.cs`)

When applying a host `snapshot`, `accepted` or `settings` packet throws in this editor:

1. **Resync.** The live scene is put back to what it showed before the packet (when that is known), copied to the recovery folder, marked
   resyncing, and the host is asked for the scene again with the same `subscribe` a reconnect sends. While resyncing, the scan sends
   nothing for the scene and accepted/settings packets for it are not applied (the snapshot contains them); the person's edits stay in
   the live scene.
2. **Replay.** When the fresh snapshot is applied, the edits held since the failure are merged onto it with the existing rebase
   (`ChangeRebase.RebaseOntoHostChange`, through `ApplyRecovery.ReplayHeld`) and the next scan sends them as ordinary edits. An edit to an
   object the host deleted meanwhile is dropped with the rebase's notice. A refused edit (`reject`) is not replayed.
3. **Isolate.** If the fresh snapshot also throws, it is applied in parts (`ApplyRecovery.IsolateObjects`, bisecting so one bad object
   costs a logarithmic number of applies). An object that still fails is left out, with its descendants; a local version the editor
   already had stays as it was. Each one gets the notice `"<name>" could not be shown in your editor (<short engine reason>). It is kept
   unchanged for everyone else.` Its host JSON is in Known, and every scan replaces whatever the live scene holds for it with that JSON
   (`ApplyRecovery.PreserveSkipped`), so it is never sent back, least of all as a deletion. Later host changes to it are recorded, not applied.
4. **Pause.** At most three resyncs per scene in any 60 seconds; the fourth failure pauses the scene with the notice `Synchronization of
   "<scene>" is paused because it repeatedly failed to apply. Your changes to this scene are kept locally. Reopen the scene to try again.`
   A paused scene sends nothing and applies nothing; what the host accepts is still recorded in Known. Reopening the scene creates a new
   tracked scene and so clears the pause.

## Scene settings and the recovery copy

`Scene.Serialize()` (decompiled `Sandbox.Scene`, `Scene.LoadSave.cs`) writes `{"Type":"Scene","Properties":SerializeProperties(),
"GameObjects":[...]}`: the scene's settings are already in the recovery copy `WriteSceneBackup` writes. The only missing piece was the
decision: a drift resync replaced unsent settings without a copy when no object was unsent. The snapshot path now writes the copy when
either is unsent (`ApplyRecovery.NeedsBackup`), and settings that cannot be read count as unsent.

## Not verified, and known gaps

- None of the editor glue in `CollaborationSession.cs` has run inside s&box; only the pure rules (`ApplyRecovery`, `ContentFailureRules`)
  are tested, and the glue compiles.
- `Scene.Serialize()` serialises every root object; an object whose `Serialize` throws therefore also makes the recovery copy fail, and
  the scene pauses instead of resyncing.
- After a failed apply of this editor's own rebased receipt, edits made while the scene resyncs are kept in the recovery copy written
  before the snapshot, not replayed.
- `ReportAction` no longer ends the session for an ordinary exception from a user action. The classification is by exception type only; an integrity failure that surfaces as a plain `InvalidOperationException` or `IOException` from a user action would be shown as a notice, not end the session. The glue itself has not run in s&box.
- The host side is unchanged: a client that resyncs sends the same `subscribe` it sends after a reconnect.
