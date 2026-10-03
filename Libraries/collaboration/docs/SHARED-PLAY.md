# Shared play (experimental, off by default)

One person presses Play. Everybody else sees that run in a spectator window and can fly around in it. Nobody else runs the game.

## Switching it on

Tick **Shared play mode (experimental)** on the Relay card. The choice is local to the computer (`.collaboration/shared-play.txt`, ignored by Git and the project manifest; a missing, blank or damaged file means off). It applies to the next session you host or join. The host and each guest must opt in: a host that has it off does not advertise the `shared-play` feature and ignores the messages; a guest that has it off is sent nothing.

## What happens

| Step | Packet | Who sends it |
| --- | --- | --- |
| A member opts in | `play-optin` | the member, on connect |
| Someone presses Play | `play-start { Scene }` | the member; the editor notices its own game session |
| The host accepts or refuses | `play-start { Peer, Name, Scene, RunId }` to everybody opted in, or `play-refused { Notice }` to the requester only | the host |
| The player streams state | `play-frame { RunId, Seq, Play }` | the player; the host forwards to spectators only, never back to the player |
| A spectator is behind or joined late | `play-key { RunId }` | spectator to host; the host asks the player (at most once a second) for a keyframe |
| The run ends | `play-stop { RunId }` | the player; the host also sends it when the player leaves or its connection drops |

Rules the host enforces (`Core/PlayRoster.cs`, `Core/SessionServer.Play.cs`): at most one run at a time, in the order requests reach the host. The second person gets `<name> is already playing. Wait until they stop, or ask them to stop. Your run is local and is not shown to the others.`: their own Play still works, it is just not shared. A frame from anybody but the current player, or for another run, is dropped. A new player after a stop gets a new run id. Reconnecting mid-play is a new run.

While a run is on screen, scene edits are paused for everybody (the existing play pause: edits made meanwhile are held and applied after). A spectator's dock says `Edits are paused while <name> is playing.`

## The mirror: state, not code

A spectator never loads the game's scene and never creates a game component. It receives plain data and builds a separate in-memory scene (`Scene.CreateEditorScene()`, the call the editor's own preview widgets use) from it.

* **Sampler** (`Core/PlaySampler.cs`, 20 Hz). Frames hold `created`, `destroyed`, `moved` (positions and scale to 3 decimals, rotation to 4, changes under 0.001 skipped), `enabled`, `changed` (component properties) and `removed` components. At most 200 moved per frame (the rest round-robin), at most 256 KiB per frame, created and destroyed first, a full keyframe every 5 s and on request.
* **Whitelist** (`Core/SpectateWhitelist.cs`). The only types a spectator may create: `ModelRenderer`, `SkinnedModelRenderer`, `AmbientLight`, `DirectionalLight`, `PointLight`, `SpotLight`, `SkyBox2D`, `MeshComponent`, `Terrain`. Each is justified in the source. `CameraComponent` is never created (the spectator has its own camera). `ParticleEffect` is off: it spawns prefabs from references (with their scripts) and has callbacks. Only listed properties of plain data survive; nested `__type`, `__Prefab`, `$` directives, `OnComponent*` hooks and oversized values are dropped. The player's editor strips before sending (`PlayCapture` does not even serialise a game component) and the receiver refuses again; the mirror sanitises once more when it applies, so a frame built any other way cannot skip the list. Collision on mesh and terrain is forced off.
* **Mirror** (`Core/PlayMirror.cs`). Frames apply only in sequence. A gap, or any reference to an object it never saw, makes the mirror keep what it shows and ask for a keyframe. Its size is bounded.
* **Host relay** (`Core/PlayRelayPolicy.cs`). A spectator whose connection has more than 1 MiB (or 128 messages) queued receives nothing until a keyframe arrives while its queue has drained below 256 KiB (or 32 messages); then deltas resume. It is never sent a frame it cannot keep up with, so the wire's 256-packet cap cannot drop its session.
* **View** (`Editor/SpectatorView.cs`). A floating window with a free-fly camera: right-drag looks, W A S D move, Q and E down and up, Shift faster, Ctrl slower, mouse wheel changes speed (`Core/FreeFlyCamera.cs`: speed limits, pitch clamp ±89°, no NaN). Objects within 100 m of the camera are interpolated 100 ms behind the newest frame with the existing `SnapshotBuffer`; farther ones snap.

## It does not touch networking

Nothing here starts, joins or changes the game's networking, opens a lobby, or spawns a player. The shared play files use the collaboration connection that already exists and open none of their own. `SharedPlayTests.Fence.cs` fails if any file of the library mentions `Networking.`, `NetworkHelper`, `CreateLobby`, `GameNetworkSystem`, `NetworkSpawn`, `Connection.Host` or `LobbyConfig`, or if a shared play file mentions a socket or `Connect(`.

## Bandwidth

Measured over the real serialiser in `SharedPlayTests.Mirror.cs` (a synthetic 300-object scene with one renderer each, 20 Hz, 10 s, keyframes included): see the `PLAY-BANDWIDTH` lines in the CoreTests output, and the report that came with this change for the numbers. The goal was at most 60 KB/s per spectator with 20 objects moving constantly; the test asserts it.

## Known limits

* Nothing in the editor glue (`SharedPlayClient`, `SpectatorView`, `PlayCapture`, the checkbox) has run inside s&box; it compiles against the real engine assemblies. `docs/VALIDATION.md` needs the live runs listed in the report.
* A scene whose meshes live in blob sidecar files is not mirrored for those meshes (the stream carries state, not files).
* Prefab instances: each component serialises itself, so visual components of prefab-spawned objects are streamed, but a prefab's children created by game code appear only as objects with transforms.
* Component data is re-read every fifth sample (and at once for a new object), so a property change reaches spectators within about 250 ms; positions and rotations are read every sample.
* Whether the editor scene ticks a whitelisted component in a way that does something beyond drawing is not confirmed.

## Video fallback (not built, on purpose)

A spectator could instead be sent rendered pictures (`CameraComponent.RenderToBitmap`, `Bitmap.ToJpg`) of the player's view. That cannot give the spectator a camera of their own, which is the point of this feature, and it costs far more bandwidth. If it is ever wanted (for a project whose visuals the mirror cannot reproduce), it slots in as a `play-video` frame kind next to `play-frame`: same host relay, same lag policy (skip to the next keyframe), a view that shows the picture instead of the puppet scene. The packet field and the relay rules are written so that a new kind is an addition, not a change.
