# Publishing Editor Collaboration

The checklist for releasing the library so that other people can install it. Every step states why it matters; run them in order.

## Before you publish

### 1. Deploy the shared relay

The library connects two editors over the internet through a relay. The address every installation uses by default is compiled into `Libraries/collaboration/Editor/Core/RelayDefaults.cs` (`DefaultUrl`), and it is **empty until a relay is deployed**, which means a fresh install can only host sessions on one network.

Either deploy from the editor or from a terminal; both put the same Worker on the same free plan.

**From the editor (no tools needed).** Open the Collaboration dock in a project and use the **Relay** card: *1. Open Cloudflare to create a token*, copy the token, then *2. Set up relay from the clipboard*. When the card says *Relay ready*, read the address from `.collaboration/relay-url.txt` in that project and set `RelayDefaults.DefaultUrl` to it in `Libraries/collaboration/Editor/Core/RelayDefaults.cs`. Delete the token afterwards at dash.cloudflare.com/profile/api-tokens.

**From a terminal.**

```bash
cd Tools/RelayWorker
npm install
npm run deploy
```

The command signs in to Cloudflare (a free account is enough), deploys the Worker, checks that its health endpoint answers, and writes the assigned address into `RelayDefaults.cs`. Commit that file with your release. Until this has been done, the dock tells the host plainly: *"No relay is set up, so friends can join only from the same network..."*.

Full details, limits and cost notes: `Tools/RelayWorker/README.md`.

### 2. Run the gates

```bash
dotnet run --project Tools/CoreTests/CoreTests.csproj
dotnet build Tools/Compile/Compile.csproj -v q --nologo
dotnet run --project Tools/RelayTests/RelayTests.csproj
```

All three must pass: the unit tests, the editor compile gate (0 warnings, 0 errors) and the relay protocol tests. A release with a red gate is a broken installation for every user.

### 3. Bump the version

The library version is stated in three places that must agree - the test suite fails if they drift:

- `Libraries/collaboration/VERSION`
- `Libraries/collaboration/.version`
- `LibraryInfo.LibraryVersion` in `Libraries/collaboration/Editor/Core/LibraryInfo.cs`

A consuming project also pins the version in its `ProjectSettings/collaboration.json` (`LibraryVersion`); the same test checks that pin wherever a project is present.

### 4. Confirm the test project state

`ProjectSettings/collaboration.json` in this repository's development project should name the version you are releasing. The library itself does not depend on that file, but keeping it current keeps the readiness checks honest while you test.

## Publish the package

1. Open the project that contains the library in the s&box editor.
2. Open **Library Manager** (View menu) and select the library named **s&box Editor Collaboration**. Its descriptor (`Libraries/collaboration/collaboration.sbproj`) must say `"Org": "cyn"` and `"Ident": "sbox_editor_collab"`, the same as the package page; if they differ, Publish targets a package that does not exist and fails (see *If Publish fails* below).
3. Choose **Publish Project** (right-click or the publish action in the library's detail view). The package is uploaded under the organization `cyn`. The package page on Asset Party carries the ident `sbox_editor_collab`, so a project that installs it holds `Libraries/cyn.sbox_editor_collab` - idents are lowercase and dotted, and the name is permanent once published. The readiness check recognises that name, the workspace descriptor's `editor_collaboration`, and whatever `LibraryIdent` the project's settings file names.
4. Finish the package page on **sbox.game**: title, description, tags - and the **thumbnail**. Use the repository icon, `Libraries/collaboration/docs/icon.png` (square PNG, 1254x1254): it is the same artwork shown at the top of this repository's README.
5. Set the package's publish state to **Released** and save. Until then the package exists but is not visible to anyone outside your organization.

Packages stay private to their organization unless explicitly released, so a first publish is not public until step 5.

### If Publish fails

- **The Library Manager lists the library as "Editor Collaboration", version 0, with an empty description, and its log says `Failed to fetch package info for cyn.<ident>: package not found`.** The descriptor's `Ident` does not match the package on sbox.game. The wizard looks the package up by `Org` + `Ident`, finds nothing, and cannot publish as an update. Set `Ident` to the ident in the package page's address (`sbox.game/cyn/sbox_editor_collab` gives `sbox_editor_collab`), reopen the editor, and publish again.
- **"You must specify an organisation to publish under."** `Org` is `local`. Set it to the organization that owns the package.
- **"This package is a different type to the one you're trying to upload."** The package on the site is a different type from `"Type": "library"`. Create the package as a Library, or correct the descriptor.
- **"This package already exists and is currently marked as archived."** Un-archive it on sbox.game first.

## After publishing

Users install the library the standard way: in their project's **Library Manager**, add `cyn.sbox_editor_collab`. The project settings file that the library creates on first use names the ident and the version:

```json
{
  "LibraryIdent": "cyn.sbox_editor_collab",
  "LibraryVersion": "1.2.0"
}
```

Both people in a session must run the same library version and identical project files; the handshake refuses a mismatch with a message naming the difference rather than failing later.

## Repository release

Tag the repository so a published version can always be traced to its source:

```bash
git tag v1.2.0
git push origin v1.2.0
```

Use the same number as the version files.
