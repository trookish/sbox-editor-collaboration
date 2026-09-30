# Publishing Editor Collaboration

The checklist for releasing the library so that other people can install it. Every step states why it matters; run them in order.

## Before you publish

### 1. Deploy the shared relay

The library connects two editors over the internet through a relay. The address every installation uses by default is compiled into `Libraries/collaboration/Editor/Core/RelayDefaults.cs` (`DefaultUrl`), and it is **empty until you deploy**, which means a fresh install can only host sessions on one network.

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
2. Open **Library Manager** (View menu) and select the `editor_collaboration` library.
3. Choose **Publish Project** (right-click or the publish action in the library's detail view). The package is uploaded under the organization `cyn` with the ident `editor_collaboration` - idents are lowercase and dotted, and the name is permanent once published.
4. Finish the package page on **sbox.game**: title, description, tags - and the **thumbnail**. Use the repository icon, `Libraries/collaboration/docs/icon.png` (square PNG, 1254x1254): it is the same artwork shown at the top of this repository's README.
5. Set the package's publish state to **Released** and save. Until then the package exists but is not visible to anyone outside your organization.

Packages stay private to their organization unless explicitly released, so a first publish is not public until step 5.

## After publishing

Users install the library the standard way: in their project's **Library Manager**, add `cyn.editor_collaboration`. The project settings file that the library creates on first use names the ident and the version:

```json
{
  "LibraryIdent": "cyn.editor_collaboration",
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
