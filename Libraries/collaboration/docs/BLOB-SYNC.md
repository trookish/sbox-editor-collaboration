# Mapping-tool geometry (blob) synchronization

Status: evidence first. Every claim carries a label: **Read** (seen in decompiled engine source, installed files or this repository, with the location), **Inferred** (follows from Read facts, not observed running) or **Unknown** (not established; the live procedure at the end settles it).

Sources: `Sandbox.Engine.dll` (the build installed under `steamapps/common/sbox/bin/managed`) decompiled with `ilspycmd`; `addons/tools` decompiled and source; installed sample scenes under `download/assets/scenes` and `templates`.

## 1. How the engine represents blobs

| # | Fact | Label | Where |
|---|------|-------|-------|
| 1 | `BlobDataSerializer` is an **internal** static class. Its nested `BlobContext` is declared `public` but is only reachable through the internal type; `Capture()` and `Load*()` are public members of that internal class. Everything that moves bytes in or out of a context (`RegisterBlob`, `ReadBlob`, `Detach`, `PackBlobs`, `LoadFrom( JsonNode )`, `BlobContext.SaveTo( JsonNode )`, `Suppress`, `IsActive`) is `internal`. | Read | `Sandbox/BlobDataSerializer.cs:18,48,115,187,241,396`; `Properties/AssemblyInfo.cs:16-36` lists the only assemblies that may see internals (`Sandbox.Tools`, `Sandbox.Menu`, ...); a library's editor assembly is not among them. |
| 2 | A `BlobData` property is written by `BinaryConvert`: it asks the active context for a GUID and writes `{ "$blob": "<guid>" }` (plus `$refs` for `IBlobReferences`). With **no** active context `RegisterBlob` returns `Guid.Empty`. Reading `{ "$blob": ... }` asks the active context; with no context, or a GUID the context lacks, it returns `null`. | Read | `Sandbox/BinaryConvert.cs:19-72`, `BlobDataSerializer.cs:187-197,241-278` |
| 3 | **`PolygonMesh` (the type of `MeshComponent.Mesh`) does not follow rule 2.** Its own `JsonWrite` writes `{ "Data": ..., "Materials": [...] }` and chooses the form of `Data` by `BlobDataSerializer.IsActive`: with a context, the `$blob` reference; **without one, the whole mesh as an inline base64 string** (`WriteBase64StringValue( meshBlob.ToBytes() )`). Its `JsonRead` accepts both: a JSON string is decoded as base64 directly, an object goes through the blob converter. | Read | `Sandbox/PolygonMesh.cs:9845-9872` (write), `:9706-9770` (read, `case "Data"`) |
| 4 | `GameObject.Serialize()` and `Component.Serialize()` open **no** blob context. The contexts the engine opens are in scene save (`Scene.ToSceneFile`), `GameResource` save, network snapshot/spawn messages, undo/prefab paths, and the editor's copy-component (`tools-src Sandbox/SceneExtensions.cs`). Each is a `using` scope. | Read | grep of `BlobDataSerializer` over the decompiled engine: `Scene.cs:1630`, `GameResource.cs:342`, `NetworkObject.cs`, `SceneNetworkSystem.cs`, `SnapshotCapture.cs`, `SceneExtensions.cs:62` |
| 5 | Consequence for this library: `SceneAdapter.Capture` calls `gameObject.Serialize()` outside any context, so a mesh object's state already holds the **whole mesh inline** (`Mesh.Data` is a base64 string), not a `$blob` reference. The premise in `ENGINE-SPIKES.md` S4 ("the sync sends only the reference") was an inference from saved scenes; for the live capture it is wrong. The same fact turns the problem around: the geometry is already in the state, and what fails is size (see 2). | Inferred from rows 3 and 4 (no engine code opens a context around a bare `Serialize()`); the live check is step 2 of the procedure | |
| 6 | Where saved bytes go: `Scene.ToSceneFile` runs `Serialize()` for every root object inside one `BlobContext`, then `SceneFile.BinaryData = blobContext.ToByteArray()`. The `.scene` JSON therefore holds `$blob` references and **no `__blobs` section**. Top-level keys of an installed scene: `__guid, GameObjects, SceneProperties, ResourceVersion, Title, Description, __references, __version`. The bytes are the **`<scene>_d` sidecar file** (`BlobContext.SaveTo( string )` writes `filePath + "_d"`; installed samples: `templates/**/Assets/scenes/*.scene_d`), and in a compiled `.scene_c` the `DBLOB` block. | Read | `Scene.cs:1618-1637`, `BlobDataSerializer.cs:131-148,466-501`, `SceneFile.cs:128-131,158-180`; probe of `pregamelobby.bff7fe0ef510ba14.scene` keys; `.scene_d` files in `templates` |
| 7 | Blob container format (public knowledge now, but there is no public API to use it): `int version(=1)`, `int count`, `count` x `{ Guid, int blobVersion, long offset, int length }`, then the payloads. A `MeshBlob` payload is `int version`, then named sections (`topology`, positions, ...). Deriving the GUID: xxHash3-128 of the payload, marked version 8. | Read | `BlobDataSerializer.cs:293-326,230-238`; `PolygonMesh.cs:1221-1470` |
| 8 | `PolygonMesh.JsonWrite` calls `mesh.CleanupUnusedMaterials()` (it mutates the mesh) before writing. A scan therefore may touch the mesh it reads. It is idempotent. | Read | `PolygonMesh.cs:9852` |

## 2. What actually goes wrong today (and how big meshes are)

| # | Fact | Label |
|---|------|-------|
| 1 | The state of a mesh object contains `Mesh.Data` as a base64 string. `StateRules` refuses a text value over 256 KiB (`MaxStringBytes`) and an object over 1 MiB (`MaxObjectBytes`) and holds that change with a notice. | Read (`Core/Protocol.cs:295,309,524,574`) |
| 2 | Sizes in the installed sample `level2_blockout` (a mapping-heavy level, 335 mesh blobs): median 12.5 KB, p90 63.9 KB, p99 261 KB, largest 576 KB; **total 9.2 MB raw, 12.3 MB as base64**. Six blobs exceed the 256 KiB string limit once base64-encoded. `pregamelobby` (11 blobs): median 1.2 KB, largest 51 KB, total 134 KB. | Read (probe of the `DBLOB` block of the compiled scenes) |
| 3 | The whole scene goes to a joiner in **one** `snapshot` packet, and one packet may not exceed `Wire.MaxBytes` = 8 MiB (`Wire.Send` throws `Connection queue or message limit exceeded`). So a map like `level2_blockout` (12.3 MB of base64) cannot be snapshotted at all, whatever the per-object limits are. | Read (`Core/Protocol.cs:806-813`, `Core/Transport.cs:151-156`) |
| 4 | Every accepted change carries `Before` **and** `After`, so inline geometry costs about 2x its size per edit, before fan-out. | Read (`ENGINE-SPIKES.md` wire-cost paragraph, `Core/Protocol.cs` `Change`) |

So the inline form is correct but cannot scale: large meshes are refused, and a realistic map overflows the snapshot frame. Geometry has to leave the object state and travel on a bounded, chunked side channel, which is what this feature builds.

## 3. Which components hold blob data

| Holder | Form | Label |
|--------|------|-------|
| `Sandbox.MeshComponent` (`Mesh`: `PolygonMesh`) | inline base64 (live capture); `$blob` in saved scenes. 10 of 11 `$blob` references in `pregamelobby` belong to `MeshComponent` | Read |
| `ClutterStorage : BlobData, IBlobReferences` (`Sandbox.Clutter`) | `{ "$blob": "000..." }` without a context: **not recoverable by any public route**; installed scenes contain no clutter component outside the one non-mesh reference | Read (`ClutterGridSystem.cs:23`); not carried by this feature |
| `TerrainStorage` (`TerrainMapBlob`) | inside the **`.terrain` asset**, not in the scene object. The `Terrain` component stores `Storage` as an asset path; the asset is a `GameResource` saved with its own `.terrain_d` sidecar | Read (`TerrainStorage.cs:106,370`, sample `Sandbox.Terrain.Storage: "terrain/middle.terrain"`) |
| NavMesh | its own file next to the scene (`navmesh/`), loaded by the scene, not by objects | Read (directory listing) |

## 4. Public route: possible or not

* Capturing or restoring through a `BlobContext`: **not possible from a library** (row 1.1). No reflection into internals is used or proposed.
* Not needed for meshes: row 1.3 gives a public route. `Serialize()` outside a context writes the whole mesh inline, and `Deserialize` of that JSON restores it. The engine's own copy-component stores blobs in `__blobdata` using internals; this library does not.
* Other `BlobData` holders (clutter) have no public route. They are neither read nor written by this feature; see the limits below.

## 5. Design (what is built)

Geometry stays in the object state, but **by hash**:

* Capture finds every text value of at least 4 KiB that is canonical base64 (decode, re-encode, equal) and replaces it with the placeholder `teamcreate-blob:sha256:<64 hex>`, storing the decoded bytes in a content-addressed `BlobStore`. The placeholder is a plain JSON string, so the object's diff, merge and rebase need no change: a changed mesh is a changed string, hence a changed object. A canonical base64 string can never equal a placeholder (`:` is not in the base64 alphabet).
* Before the engine sees a state, the placeholders are replaced by the exact original strings (`Internalize`). A missing blob means the object is skipped with a notice, never applied half-way.
* Spec deviation: the original brief proposed keeping the engine's `$blob` GUID plus a sidecar key `__collabBlobs`. There is no `$blob` GUID in the live JSON (row 1.3), so the placeholder takes that role directly and no sidecar key exists; the hashes a packet needs are found by scanning its states.
* Transport: additive packet kinds `blob-need { Hashes }`, `blob-data { Sha256, Offset, Total, Data }` and `blob-ack { Sha256, Offset }`. Chunks are `FileSyncLimits.ChunkSize`, validated by the same rule as file chunks, windowed by the same `FileChunkWindow`. A receiver holds a scene packet whose blobs are missing (and every scene packet behind it, so order is kept), asks the sender with `blob-need`, and processes the packet when the last blob verifies. The host does the same for edits and subscribes from a guest, so what it rebroadcasts is always resolvable.
* Bounds: a blob over 32 MiB is refused with a notice naming the object; at most 64 MiB of incomplete blobs and 32 held packets per endpoint; a held packet that waits 60 s is dropped with a notice and the scene is resynchronized. A blob whose bytes do not hash to the requested value is discarded.
* Host cache: `.collaboration/blobs/<sha256>`, written atomically, pruned to 256 MiB by age. Clients keep blobs in memory (256 MiB, least recently used out).
* Rate limit: at most one new blob version per object per 500 ms (the latest wins) so dragging a vertex does not flood. Between versions the object keeps its last confirmed state.
* No chunk-level delta: the `MeshBlob` payload is a versioned, section-based binary format, but its sections shift on every topology change and no public API reads it. A one-vertex edit sends the whole new blob once per receiver (plus one upload). With the 12.5 KB median that is about 17 KB of JSON per receiver; for the largest sample mesh (576 KB) about 770 KB. The edit packet itself is a few hundred bytes (it was 2x the geometry before).

## 6. Terrain

The terrain *shape* is in the `.terrain` asset (and its `.terrain_d` sidecar), not in the scene object. Both are ordinary files under `Assets`, covered by `ProjectManifest` (`_d` is not excluded) and therefore by file synchronization (later write wins). Whether sculpting writes the asset only on save: **Inferred** yes (the brush tool edits `TerrainStorage` in memory, `Asset` save writes the resource); **Unknown** until observed. The library does not sync terrain live. When a `Terrain` component is present in a collaborated scene it shows once: `Terrain changes reach other editors when the terrain asset is saved.`

## 7. Limits stated plainly

* `ClutterStorage` and any other `BlobData` the engine writes as `{ "$blob": ... }` outside a context are not recoverable; they are left exactly as the engine gives them (the placeholder logic never touches them).
* Meshes up to 32 MiB (decoded) are synchronized. The sample's largest is 576 KB.
* The geometry travels after the edit that references it, so on the receiving editor a large mesh appears when its bytes have arrived, not at the same instant as a transform change.
* Saved scene files carry `$blob` references and a `.scene_d` sidecar written by each editor's own save; the library never reads or writes those.

## 8. Live procedure (what must be tested in the two editors)

1. Host and one guest join the same scene (`Collaboration` dock). Both show `Connected`.
2. On the host, with the **mapping tool** (the built-in polygon mesh tool, not Hammer), draw a polygon mesh. Expected: it appears on the guest within about a second; no notice appears on either side.
3. On the host, move one vertex. Expected: the guest's mesh follows, and the guest's inspector shows the same vertex count. Check the `.collaboration` log/dock for any `Mesh geometry` or `blob` notice: there should be none.
4. On the guest, move a different vertex. Expected: the host's mesh follows.
5. Drag a vertex for about 5 seconds on one side. Expected: the other side updates a few times per second, never freezes, and ends on the final shape.
6. Both sides move a vertex of the **same** mesh within one second. Expected: both end on the same shape, and the person whose edit lost sees `Your unsent change to "<name>" replaced another collaborator's edit to: MeshComponent.Mesh geometry.` (or `Mesh geometry`).
7. A third editor joins late. Expected: it receives the mesh with the snapshot (the geometry may arrive a moment later).
8. Open a Terrain component scene. Expected: the one-time notice from section 6.
9. Save the scene on both. Expected: no error; the `.scene` and `.scene_d` files are identical in content on both machines (compare hashes).
10. Unknown to settle with step 2-3: confirm in the dock or log that no `larger than ... KiB collaboration limit` notice appears for a mesh. If the first mesh edit is held with a notice naming `Mesh.Data` as a `$blob` GUID instead, the engine does open a context around `Serialize()` in this build; report it, the premise of section 1.5 is then wrong and the library falls back as in section 9.

## 9. Fallback (not taken, kept for the record)

Had the evidence shown that `Serialize()` outside a context yields only `{ "$blob": "<guid>" }` and no public route restores it, the fallback would be: detect those objects, keep them out of the edit stream, show once per object `The geometry of "<name>" is edited with the mapping tool and is shared when the scene is saved.`, and rely on file sync of the saved scene and its `_d` sidecar, which an editor that has the scene open would not reload. The evidence in section 1 shows the fallback is not needed for meshes.
